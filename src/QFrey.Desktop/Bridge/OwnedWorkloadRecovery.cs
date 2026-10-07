using QFrey.Core.Contracts;
using System.IO;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private async Task LoadOwnedWorkloadRecoveryAsync(CancellationToken token)
    {
        if (session?.IsValidated != true || target is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        pendingOwnedWorkloadId = Guid.Empty; // Fail closed until the complete bounded journal scan finishes.
        try
        {
            var records = await workloadStore.ReadAllAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
            var identity = CurrentOwnedTarget(target);
            var verified = new List<OwnedWorkloadJournal>();
            foreach (var journal in records.Where(item => item.Target.Endpoint == identity.Endpoint))
            {
                if (journal.Target != identity) { pendingOwnedWorkloadId = journal.Id; return; }
                var result = await workloadCoordinator.ReconcileAsync(session, journal.Id, deadline.Token).ConfigureAwait(false);
                if (result.Status == OwnedWorkloadReconciliationStatus.IdentityVerified) verified.Add(journal);
                else if (result.ReasonCode != "WORKLOAD_NOT_FOUND") { pendingOwnedWorkloadId = journal.Id; return; }
            }
            if (verified.Count > 1) { pendingOwnedWorkloadId = verified[0].Id; return; }
            if (verified.Count == 1)
            {
                var journal = verified[0];
                var prior = experiment?.Workload;
                // Recovery cannot replace the workload of an already captured measurement.
                if (currentCycle?.BaselineContext is null || prior?.Reference.Id == journal.Id)
                    experiment = (experiment ?? new(null, draft, null, null, null, review?.Snapshot, [])) with
                    { Workload = new(new(journal.Id, WorkloadKind.Owned, [journal.Hash]), journal.Name,
                        journal.TotalBytesDecimal, journal.ServerSavePath, journal.CatalogueId, true, []) };
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
}
