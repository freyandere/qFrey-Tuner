using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Workloads;

public enum OwnedWorkloadPrepareStatus { IdentityVerified, AcceptedUnverified, RecoveryRequired, CancelledBeforeAdd }
public sealed record OwnedWorkloadPrepareResult(OwnedWorkloadPrepareStatus Status, Guid JournalId,
    OwnedWorkloadJournal Journal, string? ReasonCode);
public enum OwnedWorkloadReconciliationStatus { IdentityVerified, NotObserved, RecoveryRequired }
public sealed record OwnedWorkloadReconciliation(OwnedWorkloadReconciliationStatus Status, Guid JournalId,
    OwnedWorkloadJournal? Journal, string? ReasonCode);
public enum OwnedWorkloadActionStatus { AcceptedUnverified, Denied, RecoveryRequired, AcceptedVerified }
public sealed record OwnedWorkloadActionResult(OwnedWorkloadActionStatus Status, Guid JournalId, string? ReasonCode);

/// <summary>Coordinates consent, durable identity, one add request, and read-only recovery.</summary>
public sealed class OwnedWorkloadCoordinator
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(45);
    private readonly OwnedWorkloadStore store;
    private readonly Func<string> markerFactory;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public OwnedWorkloadCoordinator(OwnedWorkloadStore store) : this(store, () => "qfrey-test-" + Guid.NewGuid().ToString("N")) { }

    internal OwnedWorkloadCoordinator(OwnedWorkloadStore store, Func<string> markerFactory)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.markerFactory = markerFactory ?? throw new ArgumentNullException(nameof(markerFactory));
    }

    public async Task<OwnedWorkloadPrepareResult> PrepareAsync(QbittorrentSession session, string catalogueId,
        string serverSavePath, string consentIdentity, ReadOnlyMemory<byte> torrentBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!operationGate.Wait(0)) throw new QbittorrentException(ErrorCodes.OperationConflict);
        try
        {
            using var deadline = CreateDeadline(token);
            var operationToken = deadline.Token;
            operationToken.ThrowIfCancellationRequested();
            if (!session.IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
            WorkloadCatalogueEntry entry;
            TorrentMetadataInfo metadata;
            try
            {
                entry = WorkloadCatalogue.Get(catalogueId);
                if (!string.Equals(consentIdentity, WorkloadCatalogue.ConsentIdentity(entry), StringComparison.Ordinal))
                    throw new QbittorrentException(ErrorCodes.InvalidCommand);
                metadata = TorrentMetadata.Parse(torrentBytes.Span, entry.FileName);
                entry = WorkloadCatalogue.ValidateMetadata(entry.Id, metadata);
            }
            catch (QbittorrentException) { throw; }
            catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException)
            { throw new QbittorrentException(ErrorCodes.MetadataInvalid); }

            var inventory = await session.ReadRecoveryOwnedWorkloadInventoryAsync(operationToken).ConfigureAwait(false);
            var category = markerFactory();
            var tag = markerFactory();
            var decision = OwnedWorkloadJournal.CreateNewAddIntent(
                new(inventory.Target, entry.Id, metadata, category, tag, serverSavePath), inventory);
            if (!decision.Allowed) throw new QbittorrentException(ErrorCodes.OperationConflict);
            var journal = decision.Journal!;
            await store.SaveAsync(journal, operationToken).ConfigureAwait(false);

            if (operationToken.IsCancellationRequested)
                return new(OwnedWorkloadPrepareStatus.CancelledBeforeAdd, journal.Id, journal, ErrorCodes.Cancelled);
            try
            {
                await session.AddOwnedWorkloadAsync(journal, torrentBytes, operationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException)
            {
                return new(OwnedWorkloadPrepareStatus.RecoveryRequired, journal.Id, journal,
                    error is QbittorrentException qBError ? qBError.Code : ErrorCodes.Cancelled);
            }

            try
            {
                var observed = await session.ReadRecoveryOwnedWorkloadInventoryAsync(operationToken).ConfigureAwait(false);
                if (journal.Authorize(OwnedWorkloadAction.Stop, observed.Target, observed).Allowed)
                {
                    if (await ReadStoppedStateAsync(session, journal, operationToken).ConfigureAwait(false))
                        return new(OwnedWorkloadPrepareStatus.IdentityVerified, journal.Id, journal, null);
                    return new(OwnedWorkloadPrepareStatus.RecoveryRequired, journal.Id, journal, "WORKLOAD_STOPPED_STATE_NOT_OBSERVED");
                }
                return new(OwnedWorkloadPrepareStatus.AcceptedUnverified, journal.Id, journal, "WORKLOAD_IDENTITY_NOT_OBSERVED");
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException)
            {
                return new(OwnedWorkloadPrepareStatus.RecoveryRequired, journal.Id, journal,
                    error is QbittorrentException qBError ? qBError.Code : ErrorCodes.Cancelled);
            }
        }
        finally { operationGate.Release(); }
    }

    public Task<OwnedWorkloadReconciliation> ReconcileAsync(QbittorrentSession session, Guid trustedJournalId,
        CancellationToken token) => VerifyAsync(session, trustedJournalId, false, token);

    /// <summary>Fresh read-only ownership and active-state check. Cancellation never stops or deletes the torrent.</summary>
    public Task<OwnedWorkloadReconciliation> VerifyForMeasurementAsync(QbittorrentSession session, Guid trustedJournalId,
        CancellationToken token) => VerifyAsync(session, trustedJournalId, true, token);

    private async Task<OwnedWorkloadReconciliation> VerifyAsync(QbittorrentSession session, Guid trustedJournalId,
        bool requireActive, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (trustedJournalId == Guid.Empty) throw new ArgumentException("Journal id is required.", nameof(trustedJournalId));
        if (!operationGate.Wait(0)) throw new QbittorrentException(ErrorCodes.OperationConflict);
        try
        {
            using var deadline = CreateDeadline(token);
            var operationToken = deadline.Token;
            var journal = await store.ReadAsync(trustedJournalId, operationToken).ConfigureAwait(false);
            if (journal is null) return new(OwnedWorkloadReconciliationStatus.NotObserved, trustedJournalId, null, "WORKLOAD_JOURNAL_NOT_FOUND");
            try
            {
                var inventory = await session.ReadRecoveryOwnedWorkloadInventoryAsync(operationToken).ConfigureAwait(false);
                var identity = journal.Authorize(OwnedWorkloadAction.Stop, inventory.Target, inventory);
                if (identity.Allowed && !await ReadStateAsync(session, journal, requireActive, operationToken).ConfigureAwait(false))
                    return new(OwnedWorkloadReconciliationStatus.RecoveryRequired, trustedJournalId, journal,
                        requireActive ? "WORKLOAD_ACTIVE_STATE_NOT_OBSERVED" : "WORKLOAD_STOPPED_STATE_NOT_OBSERVED");
                return identity.Allowed
                    ? new(OwnedWorkloadReconciliationStatus.IdentityVerified, trustedJournalId, journal, null)
                    : new(OwnedWorkloadReconciliationStatus.NotObserved, trustedJournalId, journal, identity.ReasonCode);
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException)
            {
                return new(OwnedWorkloadReconciliationStatus.RecoveryRequired, trustedJournalId, journal,
                    error is QbittorrentException qBError ? qBError.Code : ErrorCodes.Cancelled);
            }
        }
        finally { operationGate.Release(); }
    }

    public async Task<OwnedWorkloadActionResult> ChangeAsync(QbittorrentSession session, Guid trustedJournalId,
        OwnedWorkloadAction action, bool deleteFiles, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (trustedJournalId == Guid.Empty) throw new ArgumentException("Journal id is required.", nameof(trustedJournalId));
        if (!operationGate.Wait(0)) throw new QbittorrentException(ErrorCodes.OperationConflict);
        try
        {
            using var deadline = CreateDeadline(token);
            var operationToken = deadline.Token;
            var journal = await store.ReadAsync(trustedJournalId, operationToken).ConfigureAwait(false);
            if (journal is null) return new(OwnedWorkloadActionStatus.Denied, trustedJournalId, "WORKLOAD_JOURNAL_NOT_FOUND");
            if (!Enum.IsDefined(action) || deleteFiles)
                return new(OwnedWorkloadActionStatus.Denied, trustedJournalId, ErrorCodes.InvalidCommand);
            if (action == OwnedWorkloadAction.Delete)
                return new(OwnedWorkloadActionStatus.Denied, trustedJournalId, "WORKLOAD_PATH_UNVERIFIED");
            try
            {
                // The write is attempted once. A lost response is reconciled only through reads.
                try
                {
                    await session.ChangeOwnedWorkloadAsync(journal, action, deleteFiles, null, operationToken).ConfigureAwait(false);
                }
                catch (QbittorrentException error) when (error.Code is ErrorCodes.ApiUnavailable or ErrorCodes.ApiTimeout) { }
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var inventory = await session.ReadRecoveryOwnedWorkloadInventoryAsync(operationToken).ConfigureAwait(false);
                    if (!journal.Authorize(action, inventory.Target, inventory).Allowed)
                        return new(OwnedWorkloadActionStatus.RecoveryRequired, trustedJournalId, "WORKLOAD_IDENTITY_NOT_OBSERVED");
                    if (await ReadStateAsync(session, journal, action == OwnedWorkloadAction.Start, operationToken).ConfigureAwait(false))
                        return new(OwnedWorkloadActionStatus.AcceptedVerified, trustedJournalId, null);
                    if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(250), operationToken).ConfigureAwait(false);
                }
                return new(OwnedWorkloadActionStatus.RecoveryRequired, trustedJournalId,
                    action == OwnedWorkloadAction.Start ? "WORKLOAD_ACTIVE_STATE_NOT_OBSERVED" : "WORKLOAD_STOPPED_STATE_NOT_OBSERVED");
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException)
            {
                return new(OwnedWorkloadActionStatus.RecoveryRequired, trustedJournalId,
                    error is QbittorrentException qBError ? qBError.Code : ErrorCodes.Cancelled);
            }
        }
        finally { operationGate.Release(); }
    }

    private static Task<bool> ReadStoppedStateAsync(QbittorrentSession session, OwnedWorkloadJournal journal,
        CancellationToken token) => ReadStateAsync(session, journal, false, token);

    private static async Task<bool> ReadStateAsync(QbittorrentSession session, OwnedWorkloadJournal journal,
        bool requireActive, CancellationToken token)
    {
        var telemetry = await session.ReadRecoveryOwnedWorkloadMetricsAsync([journal.Hash], token).ConfigureAwait(false);
        if (telemetry.Context.SelectedTorrents is not { Count: 1 } selected
            || !string.Equals(selected[0].Hash, journal.Hash, StringComparison.OrdinalIgnoreCase)
            || selected[0].StateReasonCode is not null) return false;
        if (requireActive)
            return selected[0].State is "downloading" or "uploading" or "forcedDL" or "forcedUP" or "stalledDL" or "stalledUP";
        return journal.Target.QbittorrentVersion.StartsWith("v4.", StringComparison.OrdinalIgnoreCase)
            ? selected[0].State is "pausedDL" or "pausedUP"
            : selected[0].State is "stoppedDL" or "stoppedUP";
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(OperationTimeout);
        return deadline;
    }
}
