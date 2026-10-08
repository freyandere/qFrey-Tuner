using System.IO;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private sealed record RestoreConsent(string Token, string SelectionToken, Guid SessionId, long Revision,
        Guid SourceCycleId, DateTimeOffset ExpiresUtc);

    private readonly LegacySourceArchive legacySourceArchive = new(Path.Combine(dataRoot, "legacy-sources"));
    private readonly Dictionary<string, RestoreConsent> legacyRestoreConsents = new(StringComparer.Ordinal);
    private LegacyRestoreCoordinator? legacyRestoreCoordinator;
    private Guid legacyRestoreSessionId;
    private LegacyCycleImport? legacyRestoreBackup;
    private RestorePreview? restorePreview;

    private async Task<string> RequestLegacyRestoreConfirmationAsync(CommandEnvelope command,
        RequestConfirmationPayload payload, string canonical, CancellationToken token)
    {
        RequireMutationTarget(command);
        if (activeOperation is not null || string.IsNullOrWhiteSpace(payload.SelectionToken))
            throw new QbittorrentException(ErrorCodes.OperationConflict);
        EnsureLegacyRestoreAvailable();

        var pickedSelection = payload.SelectionToken;
        LegacyCycleImport backup;
        RestoreReview reviewResult;
        if (restorePreview is not null && legacyRestoreBackup is not null
            && restorePreview.SelectionToken == pickedSelection
            && restorePreview.SourceCycleId == legacyRestoreBackup.CycleId)
        {
            backup = legacyRestoreBackup;
            // Re-review replaces any older bridge/Core consent for this selected source.
            RevokeLegacyRestoreConfirmations();
            reviewResult = await LegacyRestoreCoordinatorForSession().ReviewAsync(backup, token).ConfigureAwait(false);
        }
        else
        {
            RevokeLegacyRestoreConfirmations();
            var bytes = await nativeSelections.ReadLegacyAsync(pickedSelection, token).ConfigureAwait(false);
            var sourceId = Guid.NewGuid();
            backup = LegacyCycleImporter.Import(bytes, sourceId);
            // Bind legacy target identity to the same canonical URI used by the live session.
            backup = backup with { Target = backup.Target with { Endpoint = new Uri(backup.Target.Endpoint, UriKind.Absolute).AbsoluteUri } };
            await legacySourceArchive.SaveAsync(sourceId, bytes, token).ConfigureAwait(false);
            reviewResult = await LegacyRestoreCoordinatorForSession().ReviewAsync(backup, token).ConfigureAwait(false);
            legacyRestoreBackup = backup;
        }
        legacyRestoreBackup = backup;
        restorePreview = new(pickedSelection, "legacy-backup.json", backup.CycleId, reviewResult);

        // The Core coordinator issues the only write authority. Its consent binds exact source and live fingerprint.
        if (reviewResult.CanRestore)
        {
            var consent = await LegacyRestoreCoordinatorForSession()
                .RequestConfirmationAsync(backup, reviewResult.Fingerprint, token).ConfigureAwait(false);
            PruneLegacyRestoreConsents();
            if (legacyRestoreConsents.Count >= 32) throw new QbittorrentException(ErrorCodes.OperationConflict);
            var bridgeConsent = new RestoreConsent(consent.Token, pickedSelection, target!.SessionId, revision,
                consent.SourceCycleId, consent.ExpiresUtc);
            legacyRestoreConsents.Add(consent.Token, bridgeConsent);
            confirmations = [.. confirmations.Where(item => item.ActionId != "RestoreLegacyBackup"),
                new ConfirmationSummary(consent.Token, target.SessionId, revision, consent.ExpiresUtc,
                    "RestoreLegacyBackup", new LocalizedMessage("confirmations.restoreLegacy",
                        new Dictionary<string, MessageParameter> { ["endpoint"] = new TextParameter(target.Endpoint) }))];
        }
        else
        {
            RevokeLegacyRestoreConfirmations();
            restorePreview = new(pickedSelection, "legacy-backup.json", backup.CycleId, reviewResult);
            legacyRestoreBackup = backup;
        }

        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private async Task<string> StartLegacyRestoreAsync(CommandEnvelope command, RestoreBackupPayload payload,
        string canonical, CancellationToken token)
    {
        RequireMutationTarget(command);
        EnsureLegacyRestoreAvailable();
        PruneLegacyRestoreConsents();
        if (activeOperation is not null || restorePreview is null || legacyRestoreBackup is null
            || restorePreview.SelectionToken != payload.SelectionToken
            || !legacyRestoreConsents.Remove(payload.ConfirmationToken, out var consent)
            || consent.SelectionToken != payload.SelectionToken || consent.SessionId != target!.SessionId
            || consent.Revision != revision || consent.SourceCycleId != restorePreview.SourceCycleId
            || consent.ExpiresUtc <= timeProvider.GetUtcNow())
            throw new QbittorrentException(ErrorCodes.ConfirmationExpired);

        var restoreCoordinator = LegacyRestoreCoordinatorForSession();
        var operationId = Guid.NewGuid();
        var operationSessionId = target.SessionId;
        var operationSession = session!;
        var cancellation = new CancellationTokenSource();
        confirmations = [.. confirmations.Where(item => item.Token != payload.ConfirmationToken)];
        activeOperation = new(operationId, OperationKind.Restore, "starting", null, true);
        revision++;
        measurementCancellation = cancellation;
        measurementTask = Task.Run(() => RunLegacyRestoreAsync(restoreCoordinator, payload.ConfirmationToken,
            operationId, operationSessionId, operationSession, cancellation));

        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private async Task RunLegacyRestoreAsync(LegacyRestoreCoordinator coordinator, string confirmationToken,
        Guid operationId, Guid sessionId, QbittorrentSession operationSession, CancellationTokenSource cancellation)
    {
        string? failure = null;
        var verified = false;
        try
        {
            await PauseDashboardAsync(cancellation.Token).ConfigureAwait(false);
            var journal = await coordinator.RestoreAsync(confirmationToken, cancellation.Token).ConfigureAwait(false);
            verified = journal.Status == "verified";
            if (!verified) failure = ErrorCodes.RecoveryRequired;
        }
        catch (OperationCanceledException) { failure = "MEASUREMENT_CANCELLED"; }
        catch (QbittorrentException error) { failure = error.Code; }
        catch (Exception) { failure = ErrorCodes.PersistenceFailed; }

        var hasUnresolvedRestore = true;
        try
        {
            hasUnresolvedRestore = await LoadLegacyRestoreRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
            if (hasUnresolvedRestore) failure = ErrorCodes.RecoveryRequired;
        }
        catch (Exception) { failure = ErrorCodes.RecoveryRequired; }

        string? message = null;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
            {
                recoveryBlocked |= hasUnresolvedRestore;
                if (verified && !hasUnresolvedRestore && failure is null)
                {
                    // A restore invalidates active-plan authority; its history remains untouched.
                    currentCycle = null;
                    experiment = null;
                    if (review is not null) review = new PlanReview(sessionId);
                    mutation?.Dispose(); mutation = null; mutationReview = null;
                    confirmations = [];
                    availableActions = [new("restore.completed", false, null, "restore.completed")];
                }
                else
                {
                    availableActions = failure is null ? [] : [new("operation.error", false, failure, ErrorMessageKey(failure))];
                }
                RevokeLegacyRestoreConfirmations();
                activeOperation = null;
                measurementCancellation = null;
                revision++;
                message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId, NextEventSequence(), revision, Snapshot()), Protocol.Json);
                if (!disposed && !shuttingDown && ReferenceEquals(session, operationSession) && collectorCancellation is null)
                    StartDashboardCollector(operationSession, sessionId);
            }
        }
        finally { gate.Release(); cancellation.Dispose(); }
        if (message is not null) { try { await PublishSnapshotAsync(message).ConfigureAwait(false); } catch (Exception) { } }
    }

    private LegacyRestoreCoordinator LegacyRestoreCoordinatorForSession()
    {
        if (session?.IsValidated != true || target is null) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        if (legacyRestoreCoordinator is not null && legacyRestoreSessionId == target.SessionId) return legacyRestoreCoordinator;
        legacyRestoreCoordinator?.Dispose();
        legacyRestoreSessionId = target.SessionId;
        legacyRestoreCoordinator = new LegacyRestoreCoordinator(session, target.SessionId,
            Path.Combine(dataRoot, "legacy-restore-journals"), timeProvider, MarkCommitStartingAsync);
        return legacyRestoreCoordinator;
    }

    private void EnsureLegacyRestoreAvailable()
    {
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified
            || currentCycle is { ApplyStatus: ApplyStatus.Verified, OperationStage: not "completed" })
            throw new QbittorrentException(ErrorCodes.RecoveryRequired);
    }

    private void PruneLegacyRestoreConsents()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var token in legacyRestoreConsents.Where(item => item.Value.ExpiresUtc <= now)
                     .Select(item => item.Key).ToArray()) legacyRestoreConsents.Remove(token);
    }

    private void RevokeLegacyRestoreConfirmations()
    {
        legacyRestoreConsents.Clear();
        confirmations = [.. confirmations.Where(item => item.ActionId != "RestoreLegacyBackup")];
        legacyRestoreCoordinator?.Dispose();
        legacyRestoreCoordinator = null;
        legacyRestoreSessionId = Guid.Empty;
        legacyRestoreBackup = null;
        restorePreview = null;
    }

    // Reconcile through API reads only; uncertain journals for this target block writes.
    internal async Task<bool> LoadLegacyRestoreRecoveryAsync(CancellationToken token)
    {
        var root = Path.Combine(dataRoot, "legacy-restore-journals");
        if (!Directory.Exists(root)) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var files = Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).Take(1001).ToArray();
            if (files.Length > 1000) return true;
            foreach (var path in files)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(stem, "N", out var expectedId)) return true;
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > AtomicJsonStore.MaxFileBytes) return true;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var bytes = new MemoryStream((int)info.Length);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > AtomicJsonStore.MaxFileBytes) return true;
                    await bytes.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                }
                using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                Protocol.RejectDuplicateProperties(document.RootElement);
                var journal = document.RootElement.Deserialize<LegacyRestoreJournal>(Protocol.Json);
                if (journal is null || journal.SchemaVersion != 1 || journal.RestoreId != expectedId
                    || journal.SourceCycleId == Guid.Empty || journal.Target is null
                    || !ValidJournalMap(journal.Original) || !ValidJournalMap(journal.BeforeRestore)
                    || !ValidJournalMap(journal.ObservedReadback)
                    || journal.Original.Count == 0 || !SameKeys(journal.Original, journal.BeforeRestore)
                    || !ValidJournalTarget(journal.Target) || journal.UpdatedUtc < journal.CreatedUtc) return true;
                if (journal.Status is not ("pending" or "unverified" or "verified" or "notApplied" or "cancelledBeforeWrite")) return true;
                if (target is null || session?.IsValidated != true) return true;
                if (journal.Target.Endpoint != target.Endpoint) continue;
                if (journal.Target.QbittorrentVersion != target.QbittorrentVersion
                    || journal.Target.ApiVersion != target.ApiVersion
                    || journal.Target.LibtorrentVersion != target.LibtorrentVersion) return true;
                if (journal.Status is "pending" or "unverified")
                    journal = await LegacyRestoreCoordinatorForSession().ReconcileAsync(expectedId, deadline.Token).ConfigureAwait(false);
                if (journal.Status is "pending" or "unverified") return true;
                if (journal.Status == "verified" && !Matches(journal.ObservedReadback, journal.Original)) return true;
                if (journal.Status == "notApplied" && !Matches(journal.ObservedReadback, journal.BeforeRestore)) return true;
                if (journal.Status == "cancelledBeforeWrite" && journal.ObservedReadback.Count != 0) return true;
            }
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return true; }
    }

    private static bool ValidJournalMap(IReadOnlyDictionary<string, PreferenceValue>? values) => values is not null
        && values.Count <= 64 && values.All(item => CycleStore.IsSafePreferenceKey(item.Key)
            && CycleStore.IsSafePreferenceValue(item.Key, item.Value));

    private static bool SameKeys(IReadOnlyDictionary<string, PreferenceValue> left,
        IReadOnlyDictionary<string, PreferenceValue> right) => left.Count == right.Count
        && left.Keys.All(right.ContainsKey);

    private static bool Matches(IReadOnlyDictionary<string, PreferenceValue> actual,
        IReadOnlyDictionary<string, PreferenceValue> expected) => actual.Count == expected.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool ValidJournalTarget(TargetIdentity identity)
    {
        if (!Uri.TryCreate(identity.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.AbsoluteUri != identity.Endpoint
            || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
        try { Compatibility.Validate(identity.QbittorrentVersion, identity.ApiVersion, identity.LibtorrentVersion); return true; }
        catch (QbittorrentException) { return false; }
    }
}
