using QFrey.Core.Contracts;
using System.IO;
using System.Text.Json;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private WorkloadSummary[] ownedWorkloadCandidates = [];

    private static WorkloadSummary OwnedSummary(OwnedWorkloadJournal journal) => new(
        new(journal.Id, WorkloadKind.Owned, [journal.Hash]), journal.Name, journal.TotalBytesDecimal,
        journal.ServerSavePath, journal.CatalogueId, true, []);

    private async Task<OwnedWorkloadReconciliation> ReconcileOwnedStateAsync(Guid id, CancellationToken token)
    {
        var result = await workloadCoordinator.ReconcileAsync(session!, id, token).ConfigureAwait(false);
        if (result.ReasonCode == "WORKLOAD_STOPPED_STATE_NOT_OBSERVED")
            result = await workloadCoordinator.VerifyForMeasurementAsync(session!, id, token).ConfigureAwait(false);
        return result;
    }

    private async Task LoadOwnedWorkloadRecoveryAsync(CancellationToken token)
    {
        ownedWorkloadCandidates = [];
        if (session?.IsValidated != true || target is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        pendingOwnedWorkloadId = Guid.Empty; // Fail closed until the complete bounded journal scan finishes.
        try
        {
            var records = await workloadStore.ReadAllAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
            var identity = CurrentOwnedTarget(target);
            var verified = new List<WorkloadSummary>();
            foreach (var journal in records.Where(item => item.Target.Endpoint == identity.Endpoint))
            {
                if (journal.Target != identity) { pendingOwnedWorkloadId = journal.Id; return; }
                var result = await ReconcileOwnedStateAsync(journal.Id, deadline.Token).ConfigureAwait(false);
                if (result.Status == OwnedWorkloadReconciliationStatus.IdentityVerified && result.Journal is not null)
                    verified.Add(OwnedSummary(result.Journal));
                else if (result.ReasonCode != "WORKLOAD_NOT_FOUND") { pendingOwnedWorkloadId = journal.Id; return; }
            }
            if (verified.Count > 1) { ownedWorkloadCandidates = verified.ToArray(); return; }
            if (verified.Count == 1)
            {
                var workload = verified[0];
                var prior = experiment?.Workload;
                // Recovery cannot replace the workload of an already captured measurement.
                if (currentCycle?.BaselineContext is null || prior?.Reference.Id == workload.Reference.Id)
                    experiment = (experiment ?? new(null, draft, null, null, null, review?.Snapshot, [])) with
                    { Workload = workload };
            }
            pendingOwnedWorkloadId = null;
        }
        catch (Exception error) when (error is QbittorrentException or OperationCanceledException or IOException or UnauthorizedAccessException)
        { availableActions = [new("operation.error", false, ErrorCodes.RecoveryRequired, "errors.recoveryRequired")]; }
        finally
        {
            if (pendingOwnedWorkloadId is not null)
                availableActions = [new("operation.error", false, ErrorCodes.RecoveryRequired, "errors.recoveryRequired")];
        }
    }

    private async Task<string> SelectOwnedWorkloadAsync(CommandEnvelope command, SelectOwnedWorkloadPayload payload,
        string canonical, CancellationToken token)
    {
        if (!TryRequireOwnedTarget(command, out var currentSession, out var targetError)) return targetError!;
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (recoveryBlocked || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        var candidate = ownedWorkloadCandidates.SingleOrDefault(item => item.Reference.Id == payload.WorkloadId);
        if (candidate is null || pendingOwnedWorkloadId != Guid.Empty)
            return Failure(command.RequestId, ErrorCodes.WorkloadNotOwned, "errors.workloadNotOwned");
        if (currentCycle?.BaselineContext is not null || experiment?.Baseline is not null)
            return Failure(command.RequestId, ErrorCodes.MeasurementInvalid, "errors.measurementInvalid");
        await currentSession!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (CurrentOwnedTarget(currentSession) != CurrentOwnedTarget(target!))
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        var result = await ReconcileOwnedStateAsync(payload.WorkloadId, token).ConfigureAwait(false);
        if (result.Status != OwnedWorkloadReconciliationStatus.IdentityVerified || result.Journal is null
            || result.Journal.Target != CurrentOwnedTarget(target!) || !MatchesOwnedSummary(candidate, result.Journal))
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        experiment = (experiment ?? new(null, draft, null, null, null, review?.Snapshot, [])) with
        { Workload = OwnedSummary(result.Journal) };
        pendingOwnedWorkloadId = null;
        ownedWorkloadCandidates = [];
        ownedWorkloadConfirmations.Clear();
        RevokeConfirmations();
        availableActions = [];
        revision++;
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private static bool MatchesOwnedSummary(WorkloadSummary summary, OwnedWorkloadJournal journal) =>
        summary.OwnershipVerified && summary.Reference.Kind == WorkloadKind.Owned
        && summary.Reference.Id == journal.Id && summary.Reference.Hashes is { Length: 1 }
        && string.Equals(summary.Reference.Hashes[0], journal.Hash, StringComparison.OrdinalIgnoreCase)
        && summary.Name == journal.Name && summary.TotalBytesDecimal == journal.TotalBytesDecimal
        && summary.ServerSavePath == journal.ServerSavePath && summary.CatalogueId == journal.CatalogueId;

    private async Task<WorkloadSummary> ResolveOwnedMeasurementAsync(WorkloadReference reference, CancellationToken token)
    {
        var selected = experiment?.Workload ?? currentCycle?.Experiment.Workload;
        if (session?.IsValidated != true || target is null || reference.Kind != WorkloadKind.Owned
            || reference.Id == Guid.Empty || reference.Hashes is not { Length: 1 }
            || selected is null || selected.Reference.Id != reference.Id
            || !selected.Reference.Hashes.SequenceEqual(reference.Hashes, StringComparer.OrdinalIgnoreCase))
            throw new QbittorrentException(ErrorCodes.WorkloadNotOwned);
        var result = await workloadCoordinator.VerifyForMeasurementAsync(session, reference.Id, token).ConfigureAwait(false);
        if (result.Status != OwnedWorkloadReconciliationStatus.IdentityVerified || result.Journal is null
            || result.Journal.Target != CurrentOwnedTarget(target) || !MatchesOwnedSummary(selected, result.Journal))
            throw new QbittorrentException(ErrorCodes.WorkloadNotOwned);
        return OwnedSummary(result.Journal);
    }
}