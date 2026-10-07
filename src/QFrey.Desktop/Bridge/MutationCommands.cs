using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private MutationCoordinator? mutation;
    private PlanReview? mutationReview;
    private ConfirmationSummary[] confirmations = [];
    private bool recoveryBlocked;

    private async Task LoadRecoveryAsync(CancellationToken token)
    {
        recoveryBlocked = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        string? cursor = null;
        var unresolved = new List<CycleRecord>();
        // ponytail: bounded history scan; add an index when history exceeds 10,000 cycles.
        for (var page = 0; page < 100; page++)
        {
            var history = await cycles.ListAsync(cursor, 100, deadline.Token).ConfigureAwait(false);
            foreach (var entry in history.Items.Where(item => item.Endpoint == target!.Endpoint
                && item.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified))
            {
                var record = await cycles.ReadAsync(entry.CycleId, cancellationToken: deadline.Token).ConfigureAwait(false);
                if (record is not null && record.OperationStage != "completed") unresolved.Add(record);
            }
            cursor = history.NextCursor;
            if (cursor is null) break;
        }
        if (cursor is not null) throw new QbittorrentException(ErrorCodes.RecoveryRequired);
        if (unresolved.Count == 0) { recoveryBlocked = false; return; }
        var chosen = unresolved.OrderByDescending(item => item.UpdatedUtc).First();
        currentCycle = chosen;
        experiment = chosen.Experiment with { Plan = chosen.Plan with { TargetSessionId = target!.SessionId, Approved = false } };
        availableActions = [new("operation.error", false, ErrorCodes.RecoveryRequired, "errors.recoveryRequired")];
        var versions = session!.Versions;
        if (chosen.Target.QbittorrentVersion != versions.Qbittorrent || chosen.Target.ApiVersion != versions.WebApi
            || chosen.Target.LibtorrentVersion != versions.Libtorrent) return;
        currentCycle = await Coordinator().ReconcileAsync(chosen.CycleId, deadline.Token).ConfigureAwait(false);
        experiment = currentCycle.Experiment with { Plan = currentCycle.Plan with { TargetSessionId = target!.SessionId, Approved = false } };
        if (currentCycle.ApplyStatus == ApplyStatus.Reverted && unresolved.Count == 1)
        { recoveryBlocked = false; availableActions = []; }
    }

    private AvailableAction[] MutationActions()
    {
        var plan = experiment?.Plan;
        var idle = activeOperation is null && !shuttingDown && session?.IsValidated == true
            && target is not null && plan?.TargetSessionId == target.SessionId;
        var applicable = idle && !recoveryBlocked && pendingOwnedWorkloadId is null && plan is { Applicable: true, PreviewOnly: false };
        var status = currentCycle?.ApplyStatus;
        return [.. availableActions,
            new("AcceptPlan", applicable && plan is { Approved: false } && status is null or ApplyStatus.NotApplied, null, "mutation.approve"),
            new("ApplyPlan", applicable && plan is { Approved: true } && status == ApplyStatus.NotApplied
                && currentCycle?.BaselineContext is not null && experiment?.Baseline?.Status == MeasurementStatus.Valid, null, "mutation.apply"),
            new("Rollback", idle && status is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified, null, "mutation.rollback"),
            new("KeepChanges", idle && status == ApplyStatus.Verified
                && currentCycle?.OperationStage is "appliedVerified" or "afterReady", null, "mutation.keep")];
    }

    private async Task<string> KeepMutationAsync(CommandEnvelope command, KeepChangesPayload payload,
        string canonical, CancellationToken token)
    {
        RequireMutationTarget(command);
        if (activeOperation is not null) throw new QbittorrentException(ErrorCodes.OperationConflict);
        if (currentCycle?.CycleId != payload.CycleId) throw new QbittorrentException(ErrorCodes.PlanStale);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        currentCycle = await Coordinator().KeepChangesAsync(payload.CycleId, deadline.Token).ConfigureAwait(false);
        experiment = currentCycle.Experiment;
        if (recoveryBlocked) await LoadRecoveryAsync(deadline.Token).ConfigureAwait(false);
        confirmations = []; availableActions = []; revision++;
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private MutationCoordinator Coordinator()
    {
        if (session?.IsValidated != true || target is null || review is null)
            throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        if (mutation is not null && ReferenceEquals(mutationReview, review)) return mutation;
        mutation?.Dispose();
        var ownedSession = session;
        mutationReview = review;
        mutation = new(ownedSession, target.SessionId, review, cycles,
            (cycle, token) => ExperimentCommands.ValidateBaselineAsync(cycle, ownedSession, token),
            PauseDashboardAsync, () => Task.CompletedTask, timeProvider, MarkCommitStartingAsync);
        return mutation;
    }

    private void RevokeConfirmations()
    {
        RevokeLegacyRestoreConfirmations();
        confirmations = [];
        ownedWorkloadConfirmations.Clear();
        mutation?.Dispose(); mutation = null; mutationReview = null;
    }

    private async Task PauseDashboardAsync(CancellationToken token)
    {
        collectorCancellation?.Cancel();
        await collectorTask.WaitAsync(token).ConfigureAwait(false);
        collectorCancellation?.Dispose(); collectorCancellation = null;
        collectorTask = Task.CompletedTask;
    }

    private async Task MarkCommitStartingAsync()
    {
        string? message = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (activeOperation is { } operation)
                activeOperation = operation with { Stage = "committing", Cancellable = false };
            if (currentCycle is { } cycle)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                currentCycle = await cycles.ReadAsync(cycle.CycleId, cancellationToken: deadline.Token).ConfigureAwait(false) ?? cycle;
                experiment = currentCycle.Experiment;
            }
            if (activeOperation is { } current && target is { } currentTarget)
                message = JsonSerializer.Serialize(new SnapshotEvent(currentTarget.SessionId, current.Id,
                    NextEventSequence(), revision, Snapshot()), Protocol.Json);
        }
        finally { gate.Release(); }
        if (message is not null) await PublishSnapshotAsync(message).ConfigureAwait(false);
    }

    private async Task<string> RequestMutationConfirmationAsync(CommandEnvelope command, RequestConfirmationPayload payload,
        string canonical, CancellationToken token)
    {
        RequireMutationTarget(command);
        if (activeOperation is not null) throw new QbittorrentException(ErrorCodes.OperationConflict);
        var cycleId = payload.CycleId ?? currentCycle?.CycleId ?? throw new QbittorrentException(ErrorCodes.BackupInvalid);
        var cycle = await cycles.ReadAsync(cycleId, cancellationToken: token).ConfigureAwait(false)
            ?? throw new QbittorrentException(ErrorCodes.BackupInvalid);
        if (payload.ActionId is not ("ApplyPlan" or "Rollback")) throw new QbittorrentException(ErrorCodes.InvalidCommand);
        if (payload.PlanId is Guid planId && planId != cycle.Plan.Id
            || payload.ActionId == "ApplyPlan" && currentCycle?.CycleId != cycleId) throw new QbittorrentException(ErrorCodes.PlanStale);
        var confirmation = await Coordinator().RequestConfirmationAsync(cycleId, cycle.Plan.Id,
            cycle.Plan.Revision, payload.ActionId, token).ConfigureAwait(false);
        confirmations = [.. confirmations.Where(item => item.ExpiresUtc > timeProvider.GetUtcNow()), confirmation];
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private void RequireMutationTarget(CommandEnvelope command)
    {
        if (target is null || session?.IsValidated != true || command.TargetSessionId != target.SessionId)
            throw new QbittorrentException(ErrorCodes.SessionStale);
        if (command.ExpectedRevision != revision) throw new QbittorrentException(ErrorCodes.PlanStale);
    }

    private async Task<string> StartMutationAsync(CommandEnvelope command, object payload, string canonical, CancellationToken token)
    {
        RequireMutationTarget(command);
        if (activeOperation is not null) throw new QbittorrentException(ErrorCodes.OperationConflict);
        var action = command.Command;
        if (action == "ApplyPlan" && (recoveryBlocked || pendingOwnedWorkloadId is not null)) throw new QbittorrentException(ErrorCodes.RecoveryRequired);
        var cycleId = payload is RollbackPayload rollback ? rollback.CycleId
            : currentCycle?.CycleId ?? throw new QbittorrentException(ErrorCodes.BaselineRequired);
        var cycle = await cycles.ReadAsync(cycleId, cancellationToken: token).ConfigureAwait(false)
            ?? throw new QbittorrentException(ErrorCodes.BackupInvalid);
        var planRevision = payload is ApplyPlanPayload apply ? apply.ExpectedRevision : ((RollbackPayload)payload).ExpectedRevision;
        var consent = payload is ApplyPlanPayload applyConsent ? applyConsent.ConfirmationToken : ((RollbackPayload)payload).ConfirmationToken;
        if (planRevision != cycle.Plan.Revision || payload is ApplyPlanPayload applyPlan && applyPlan.PlanId != cycle.Plan.Id)
            throw new QbittorrentException(ErrorCodes.PlanStale);
        if (!confirmations.Any(item => item.Token == consent && item.ActionId == action && item.TargetSessionId == target!.SessionId
            && item.ExpiresUtc > timeProvider.GetUtcNow())) throw new QbittorrentException(ErrorCodes.ConfirmationExpired);
        var coordinator = Coordinator();
        var operationId = Guid.NewGuid();
        var operationSession = session!;
        var sessionId = target!.SessionId;
        var cancellation = new CancellationTokenSource();
        confirmations = [];
        currentCycle = cycle; experiment = cycle.Experiment;
        activeOperation = new(operationId, action == "ApplyPlan" ? OperationKind.Apply : OperationKind.Rollback, "starting", null, true);
        measurementCancellation = cancellation;
        revision++;
        measurementTask = Task.Run(() => RunMutationAsync(coordinator, action, cycle, consent, operationId, sessionId, operationSession, cancellation));
        var reply = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true, revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private async Task RunMutationAsync(MutationCoordinator coordinator, string action, CycleRecord cycle, string consent,
        Guid operationId, Guid sessionId, QbittorrentSession operationSession, CancellationTokenSource cancellation)
    {
        CycleRecord? result = null;
        string? failure = null;
        try
        {
            result = action == "ApplyPlan"
                ? await coordinator.ApplyAsync(cycle.CycleId, cycle.Plan.Id, cycle.Plan.Revision, consent, cancellation.Token).ConfigureAwait(false)
                : await coordinator.RollbackAsync(cycle.CycleId, cycle.Plan.Id, cycle.Plan.Revision, consent, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { failure = "MEASUREMENT_CANCELLED"; }
        catch (QbittorrentException error) { failure = error.Code; }
        catch (Exception) { failure = ErrorCodes.PersistenceFailed; }
        finally
        {
            if (result is null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    result = await cycles.ReadAsync(cycle.CycleId, cancellationToken: deadline.Token).ConfigureAwait(false);
                }
                catch (Exception) { /* Keep the last trusted in-memory state; the durable journal is never deleted. */ }
            }
            string? message = null;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
                {
                    if (result is not null) { currentCycle = result; experiment = result.Experiment; }
                    if (recoveryBlocked && result?.ApplyStatus == ApplyStatus.Reverted)
                    {
                        try { await LoadRecoveryAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception) { failure = ErrorCodes.RecoveryRequired; }
                    }
                    availableActions = failure is null ? [] : [new("operation.error", false, failure, ErrorMessageKey(failure))];
                    activeOperation = null; measurementCancellation = null; revision++;
                    message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId, NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    if (!disposed && !shuttingDown && ReferenceEquals(session, operationSession) && collectorCancellation is null)
                        StartDashboardCollector(operationSession, sessionId);
                }
            }
            finally { gate.Release(); cancellation.Dispose(); }
            if (message is not null) { try { await PublishSnapshotAsync(message).ConfigureAwait(false); } catch (Exception) { } }
        }
    }
}
