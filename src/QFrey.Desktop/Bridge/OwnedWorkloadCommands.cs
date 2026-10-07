using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private sealed record OwnedWorkloadConfirmationToken(string Token, string ActionId, Guid SessionId,
        long Revision, DateTimeOffset ExpiresUtc, TargetIdentity Target, string? CatalogueId,
        string? ConsentIdentity, string? ServerSavePath, Guid? WorkloadId);

    private readonly OwnedWorkloadStore workloadStore = new(Path.Combine(dataRoot, "workloads"));
    private OwnedWorkloadCoordinator? ownedWorkloadCoordinator;
    private readonly List<OwnedWorkloadConfirmationToken> ownedWorkloadConfirmations = [];
    private Guid? pendingOwnedWorkloadId;

    // Tests inject mock bytes here; the default downloader only runs after explicit confirmation and Start.
    internal Func<WorkloadCatalogueEntry, CancellationToken, Task<byte[]>> WorkloadMetadataFetch { get; set; } = FetchWorkloadMetadataAsync;

    private OwnedWorkloadCoordinator workloadCoordinator => ownedWorkloadCoordinator ??= new(workloadStore);

    private static async Task<byte[]> FetchWorkloadMetadataAsync(WorkloadCatalogueEntry entry, CancellationToken token)
    {
        using var downloader = MetadataDownloader.CreateDefault();
        return await downloader.DownloadAsync(entry, token).ConfigureAwait(false);
    }

    private async Task<string> RequestOwnedWorkloadConfirmationAsync(CommandEnvelope command,
        RequestConfirmationPayload payload, string canonical, CancellationToken token)
    {
        if (!TryRequireOwnedTarget(command, out var currentSession, out var targetError)) return targetError!;
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        if (payload.ActionId is not ("PrepareWorkload" or "StopOwnedWorkload" or "DeleteOwnedWorkload"))
            return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand");
        if (payload.ActionId != "PrepareWorkload" && (payload.PlanId is not null || payload.CycleId is not null))
            return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand");
        if (payload.ActionId == "DeleteOwnedWorkload")
            return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");

        await currentSession!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        var identity = CurrentOwnedTarget(currentSession);
        if (identity != CurrentOwnedTarget(target!))
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");

        string? catalogueId = null, consentIdentity = null, serverSavePath = null;
        Guid? workloadId = null;
        string messageKey;
        Dictionary<string, MessageParameter> parameters;
        if (payload.ActionId == "PrepareWorkload")
        {
            if (currentCycle?.Experiment.Baseline is not null || experiment?.Baseline is not null)
                return Failure(command.RequestId, ErrorCodes.MeasurementInvalid, "errors.measurementInvalid");
            if (payload.CatalogueId is null || payload.ServerSavePath is null || payload.PlanId is not null
                || payload.CycleId is not null || payload.WorkloadId is not null || !ValidOwnedServerPath(payload.ServerSavePath))
                return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand");
            WorkloadCatalogueEntry entry;
            try { entry = WorkloadCatalogue.Get(payload.CatalogueId); }
            catch (ArgumentException) { return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand"); }
            catalogueId = entry.Id;
            consentIdentity = WorkloadCatalogue.ConsentIdentity(entry);
            serverSavePath = payload.ServerSavePath;
            messageKey = "confirmation.workload.prepare";
            parameters = new(StringComparer.Ordinal)
            {
                ["name"] = new TextParameter(entry.FileName),
                ["sizeBytes"] = new NumberParameter(entry.SizeBytes),
                ["serverSavePath"] = new TextParameter(serverSavePath),
                ["endpoint"] = new TextParameter(identity.Endpoint)
            };
        }
        else
        {
            if (payload.CatalogueId is not null || payload.ServerSavePath is not null || payload.WorkloadId is null)
                return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand");
            var workload = experiment?.Workload ?? currentCycle?.Experiment.Workload;
            if (workload is not { OwnershipVerified: true, Reference.Kind: WorkloadKind.Owned }
                || workload.Reference.Id != payload.WorkloadId || workload.Reference.Hashes is not { Length: 1 })
                return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");
            var journal = await workloadStore.ReadAsync(payload.WorkloadId.Value, token).ConfigureAwait(false);
            if (journal is null || journal.Target != identity || journal.CatalogueId != workload.CatalogueId
                || !workload.Reference.Hashes.Contains(journal.Hash, StringComparer.OrdinalIgnoreCase)
                || journal.Name != workload.Name || journal.TotalBytesDecimal != workload.TotalBytesDecimal
                || journal.ServerSavePath != workload.ServerSavePath)
                return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");
            workloadId = journal.Id;
            catalogueId = journal.CatalogueId;
            consentIdentity = journal.CatalogueConsentIdentity;
            serverSavePath = journal.ServerSavePath;
            messageKey = "confirmation.workload.stop";
            parameters = new(StringComparer.Ordinal)
            {
                ["name"] = new TextParameter(journal.Name),
                ["sizeBytes"] = new NumberParameter(double.Parse(journal.TotalBytesDecimal, System.Globalization.CultureInfo.InvariantCulture)),
                ["serverSavePath"] = new TextParameter(journal.ServerSavePath),
                ["endpoint"] = new TextParameter(identity.Endpoint)
            };
        }

        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(2);
        var actionToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var bound = new OwnedWorkloadConfirmationToken(actionToken, payload.ActionId, target!.SessionId,
            revision, expires, identity, catalogueId, consentIdentity, serverSavePath, workloadId);
        ownedWorkloadConfirmations.RemoveAll(item => item.ExpiresUtc <= now);
        if (ownedWorkloadConfirmations.Count == 32) ownedWorkloadConfirmations.RemoveAt(0);
        ownedWorkloadConfirmations.Add(bound);
        var summary = new ConfirmationSummary(actionToken, target.SessionId, revision, expires, payload.ActionId,
            new LocalizedMessage(messageKey, parameters));
        confirmations = [.. confirmations.Where(item => item.ExpiresUtc > now).TakeLast(31), summary];
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private async Task<string> StartOwnedPrepareAsync(CommandEnvelope command, PrepareWorkloadPayload payload,
        string canonical, CancellationToken token)
    {
        if (!TryRequireOwnedTarget(command, out var operationSession, out var targetError)) return targetError!;
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        await operationSession!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (CurrentOwnedTarget(operationSession) != CurrentOwnedTarget(target!))
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        WorkloadCatalogueEntry entry;
        try { entry = WorkloadCatalogue.Get(payload.CatalogueId); }
        catch (ArgumentException) { return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand"); }
        var consent = WorkloadCatalogue.ConsentIdentity(entry);
        var bound = ConsumeOwnedWorkloadConfirmation(payload.ApprovalToken, "PrepareWorkload", command,
            entry.Id, consent, payload.ServerSavePath, null);
        if (bound is null) return Failure(command.RequestId, ErrorCodes.ConfirmationExpired, "errors.confirmationExpired");

        RevokeConfirmations();
        ownedWorkloadConfirmations.Clear();
        var operationId = Guid.NewGuid();
        var sessionId = target!.SessionId;
        var cancellation = new CancellationTokenSource();
        activeOperation = new(operationId, OperationKind.PrepareWorkload, "metadataDownload", null, true);
        measurementCancellation = cancellation;
        revision++;
        measurementTask = Task.Run(() => RunOwnedPrepareAsync(operationId, sessionId, operationSession,
            entry, payload.ServerSavePath, consent, cancellation));
        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private Task<string> StartOwnedWorkloadActionAsync(CommandEnvelope command, StopWorkloadPayload payload,
        string canonical, CancellationToken token) => StartOwnedWorkloadActionAsync(command, "StopOwnedWorkload", payload.WorkloadId,
            payload.ConfirmationToken, false, canonical, token);

    private Task<string> StartOwnedWorkloadActionAsync(CommandEnvelope command, DeleteWorkloadPayload payload,
        string canonical, CancellationToken token) => StartOwnedWorkloadActionAsync(command, "DeleteOwnedWorkload", payload.WorkloadId,
            payload.ConfirmationToken, payload.DeleteFiles, canonical, token);

    private async Task<string> StartOwnedWorkloadActionAsync(CommandEnvelope command, string action, Guid workloadId,
        string confirmationToken, bool deleteFiles, string canonical, CancellationToken token)
    {
        if (!TryRequireOwnedTarget(command, out var operationSession, out var targetError)) return targetError!;
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        if (action == "DeleteOwnedWorkload" || deleteFiles)
            return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");
        await operationSession!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (CurrentOwnedTarget(operationSession) != CurrentOwnedTarget(target!))
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        var journal = await workloadStore.ReadAsync(workloadId, token).ConfigureAwait(false);
        if (journal is null || journal.Target != CurrentOwnedTarget(operationSession!))
            return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");
        var bound = ConsumeOwnedWorkloadConfirmation(confirmationToken, action, command, journal.CatalogueId,
            journal.CatalogueConsentIdentity, journal.ServerSavePath, workloadId);
        if (bound is null) return Failure(command.RequestId, ErrorCodes.ConfirmationExpired, "errors.confirmationExpired");
        var cancellation = new CancellationTokenSource();
        var operationId = Guid.NewGuid();
        var sessionId = target!.SessionId;
        activeOperation = new(operationId, OperationKind.StopWorkload, "ownershipCheck", null, true);
        measurementCancellation = cancellation;
        RevokeConfirmations();
        ownedWorkloadConfirmations.Clear();
        revision++;
        measurementTask = Task.Run(() => RunOwnedWorkloadActionAsync(operationId, sessionId, session!,
            workloadId, cancellation));
        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private OwnedWorkloadConfirmationToken? ConsumeOwnedWorkloadConfirmation(string suppliedToken, string action,
        CommandEnvelope command, string? catalogueId, string? consentIdentity, string? serverSavePath, Guid? workloadId)
    {
        var now = timeProvider.GetUtcNow();
        ownedWorkloadConfirmations.RemoveAll(item => item.ExpiresUtc <= now);
        var index = ownedWorkloadConfirmations.FindIndex(item => string.Equals(item.Token, suppliedToken, StringComparison.Ordinal));
        if (index < 0) return null;
        var confirmation = ownedWorkloadConfirmations[index];
        ownedWorkloadConfirmations.RemoveAt(index);
        return confirmation.ActionId == action && confirmation.SessionId == target?.SessionId
            && confirmation.Revision == revision && command.ExpectedRevision == revision
            && confirmation.ExpiresUtc > now && confirmation.Target == CurrentOwnedTarget(target!)
            && confirmation.CatalogueId == catalogueId && confirmation.ConsentIdentity == consentIdentity
            && confirmation.ServerSavePath == serverSavePath && confirmation.WorkloadId == workloadId
                ? confirmation : null;
    }

    private bool TryRequireOwnedTarget(CommandEnvelope command, out QbittorrentSession? currentSession,
        out string? failure)
    {
        currentSession = session;
        if (target is null || currentSession?.IsValidated != true || command.TargetSessionId != target.SessionId)
        { failure = Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale"); return false; }
        if (command.ExpectedRevision != revision)
        { failure = Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale"); return false; }
        failure = null;
        return true;
    }

    private static TargetIdentity CurrentOwnedTarget(QbittorrentSession ownedSession) => new(
        ownedSession.Endpoint.AbsoluteUri, ownedSession.Versions.Qbittorrent, ownedSession.Versions.WebApi,
        ownedSession.Versions.Libtorrent);

    private static TargetIdentity CurrentOwnedTarget(TargetSummary ownedTarget) => new(ownedTarget.Endpoint,
        ownedTarget.QbittorrentVersion, ownedTarget.ApiVersion, ownedTarget.LibtorrentVersion);

    private static bool ValidOwnedServerPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return false;
        var driveRooted = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\';
        var posixRooted = value[0] == '/';
        var uncRooted = value.StartsWith("\\\\", StringComparison.Ordinal) && value.Length > 2;
        return (driveRooted || posixRooted || uncRooted)
            && !value.Split(['/', '\\'], StringSplitOptions.None).Any(component => component is "." or "..");
    }

    private async Task RunOwnedPrepareAsync(Guid operationId, Guid sessionId, QbittorrentSession operationSession,
        WorkloadCatalogueEntry entry, string savePath, string consentIdentity, CancellationTokenSource cancellation)
    {
        OwnedWorkloadPrepareResult? result = null;
        var failureCode = "WORKLOAD_PREPARATION_FAILED";
        try
        {
            await PauseDashboardAsync(cancellation.Token).ConfigureAwait(false);
            var metadata = await WorkloadMetadataFetch(entry, cancellation.Token).ConfigureAwait(false);
            result = await workloadCoordinator.PrepareAsync(operationSession, entry.Id, savePath,
                consentIdentity, metadata, cancellation.Token).ConfigureAwait(false);
            failureCode = result.ReasonCode ?? ErrorCodes.UnexpectedFailure;
        }
        catch (MetadataDownloadException error) { failureCode = error.Code; }
        catch (OperationCanceledException) { failureCode = "MEASUREMENT_CANCELLED"; }
        catch (QbittorrentException error) { failureCode = error.Code; }
        catch (Exception) { failureCode = ErrorCodes.UnexpectedFailure; }
        finally
        {
            string? message = null;
            await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
                    {
                        if (result is { Status: OwnedWorkloadPrepareStatus.IdentityVerified })
                        {
                            var journal = result.Journal;
                            var workload = new WorkloadSummary(new(journal.Id, WorkloadKind.Owned, [journal.Hash]),
                                journal.Name, journal.TotalBytesDecimal, journal.ServerSavePath, journal.CatalogueId,
                                true, []);
                            experiment = (experiment ?? new ExperimentSummary(currentCycle?.CycleId, draft, null,
                                currentCycle?.Experiment.Baseline, currentCycle?.Experiment.After, review?.Snapshot,
                                currentCycle?.Experiment.Results ?? [])) with { Workload = workload };
                            pendingOwnedWorkloadId = null;
                            availableActions = [];
                        }
                        else
                        {
                            if (result?.Status is OwnedWorkloadPrepareStatus.AcceptedUnverified or OwnedWorkloadPrepareStatus.RecoveryRequired)
                                pendingOwnedWorkloadId = result.JournalId;
                            var code = result?.ReasonCode ?? (result?.Status == OwnedWorkloadPrepareStatus.CancelledBeforeAdd
                                ? "MEASUREMENT_CANCELLED" : failureCode);
                            var messageKey = result?.Status switch
                            {
                                OwnedWorkloadPrepareStatus.CancelledBeforeAdd => "errors.operationCancelled",
                                OwnedWorkloadPrepareStatus.AcceptedUnverified or OwnedWorkloadPrepareStatus.RecoveryRequired => "errors.recoveryRequired",
                                _ => "errors.workloadPreparationFailed"
                            };
                            availableActions = [new("operation.error", false, code, messageKey)];
                        }
                        activeOperation = null;
                        revision++;
                        message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                            NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    }
                    if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
                }
                finally { gate.Release(); }
                if (message is not null) { try { await PublishSnapshotAsync(message).ConfigureAwait(false); } catch (Exception) { } }
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!disposed && !shuttingDown && target?.SessionId == sessionId
                        && ReferenceEquals(session, operationSession) && collectorCancellation is null)
                        StartDashboardCollector(operationSession, sessionId);
                }
                finally { gate.Release(); }
            }
            finally { commandAdmission.Release(); cancellation.Dispose(); }
        }
    }

    private async Task RunOwnedWorkloadActionAsync(Guid operationId, Guid sessionId, QbittorrentSession operationSession,
        Guid workloadId, CancellationTokenSource cancellation)
    {
        OwnedWorkloadActionResult? result = null;
        var failureCode = ErrorCodes.UnexpectedFailure;
        try
        {
            await PauseDashboardAsync(cancellation.Token).ConfigureAwait(false);
            result = await workloadCoordinator.ChangeAsync(operationSession, workloadId,
                OwnedWorkloadAction.Stop, false, cancellation.Token).ConfigureAwait(false);
        }
        catch (QbittorrentException error) { failureCode = error.Code; }
        catch (OperationCanceledException) { failureCode = "MEASUREMENT_CANCELLED"; }
        catch (Exception) { failureCode = ErrorCodes.UnexpectedFailure; }
        finally
        {
            string? message = null;
            await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
                    {
                        if (result?.Status is OwnedWorkloadActionStatus.RecoveryRequired or OwnedWorkloadActionStatus.AcceptedUnverified)
                        {
                            pendingOwnedWorkloadId = workloadId;
                            availableActions = [new("operation.error", false,
                                result.ReasonCode ?? "WORKLOAD_ACTION_UNVERIFIED", "errors.recoveryRequired")];
                        }
                        else availableActions = [new("operation.error", false,
                            result?.ReasonCode ?? failureCode, result is null ? "errors.workloadPreparationFailed" : "errors.workloadNotOwned")];
                        activeOperation = null;
                        revision++;
                        message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                            NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    }
                    if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
                }
                finally { gate.Release(); }
                if (message is not null) { try { await PublishSnapshotAsync(message).ConfigureAwait(false); } catch (Exception) { } }
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!disposed && !shuttingDown && target?.SessionId == sessionId
                        && ReferenceEquals(session, operationSession) && collectorCancellation is null)
                        StartDashboardCollector(operationSession, sessionId);
                }
                finally { gate.Release(); }
            }
            finally { commandAdmission.Release(); cancellation.Dispose(); }
        }
    }
}
