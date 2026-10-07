using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

// One coordinator per validated target. No UI-supplied plan or backup grants write authority.
public sealed class MutationCoordinator : IDisposable
{
    private readonly QbittorrentSession session;
    private readonly Guid sessionId;
    private readonly PlanReview review;
    private readonly CycleStore store;
    private readonly OperationGate gate = new();
    private readonly Func<CycleRecord, CancellationToken, Task> validateRun;
    private readonly Func<CancellationToken, Task> pauseCollector;
    private readonly Func<Task> resumeCollector;
    private readonly TimeProvider clock;
    private readonly Func<Task>? commitStarting;
    private readonly Dictionary<string, Consent> consents = new(StringComparer.Ordinal);
    private sealed record Consent(Guid CycleId, Guid PlanId, long Revision, string Action, DateTimeOffset Expires,
        string RecordFingerprint);

    public MutationCoordinator(QbittorrentSession session, Guid sessionId, PlanReview review, CycleStore store,
        Func<CycleRecord, CancellationToken, Task> validateRun, Func<CancellationToken, Task> pauseCollector,
        Func<Task> resumeCollector, TimeProvider? clock = null, Func<Task>? commitStarting = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session required.", nameof(sessionId));
        this.session = session; this.sessionId = sessionId; this.review = review; this.store = store;
        this.validateRun = validateRun ?? throw new ArgumentNullException(nameof(validateRun));
        this.pauseCollector = pauseCollector ?? throw new ArgumentNullException(nameof(pauseCollector));
        this.resumeCollector = resumeCollector ?? throw new ArgumentNullException(nameof(resumeCollector));
        this.clock = clock ?? TimeProvider.System;
        this.commitStarting = commitStarting;
    }

    public Task<ConfirmationSummary> RequestConfirmationAsync(Guid cycleId, Guid planId, long revision,
        string action, CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        if (action is not ("ApplyPlan" or "Rollback")) throw Failure(ErrorCodes.InvalidCommand);
        var cycle = await LoadAsync(cycleId, cancellation).ConfigureAwait(false);
        ValidateSubject(cycle, planId, revision, action);
        if (action == "ApplyPlan") RequireCurrentPlan(cycle, planId, revision);
        var now = clock.GetUtcNow();
        foreach (var key in consents.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) consents.Remove(key);
        if (consents.Count >= 32) throw Failure(ErrorCodes.OperationConflict);
        var consentToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var expires = now.AddMinutes(2);
        consents.Add(consentToken, new(cycleId, planId, revision, action, expires, Fingerprint(cycle)));
        return new ConfirmationSummary(consentToken, sessionId, revision, expires, action,
            new LocalizedMessage(action == "ApplyPlan" ? "confirmations.apply" : "confirmations.rollback",
                new Dictionary<string, MessageParameter> { ["endpoint"] = new TextParameter(session.Endpoint.AbsoluteUri),
                    ["count"] = new NumberParameter(cycle.Plan.Proposed.Count) }));
    }, token);

    public Task<CycleRecord> ApplyAsync(Guid cycleId, Guid planId, long revision, string confirmation,
        CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        var cycle = await LoadAsync(cycleId, cancellation).ConfigureAwait(false);
        ValidateSubject(cycle, planId, revision, "ApplyPlan");
        Consume(cycle, planId, revision, "ApplyPlan", confirmation);
        var plan = RequireCurrentPlan(cycle, planId, revision);
        var baseline = cycle.Experiment.Baseline;
        if (cycle.BaselineContext is null || cycle.BaselineContext.PreferencesFingerprint != plan.BaselineFingerprint
            || baseline is null || baseline.Kind != MeasurementKind.Baseline || baseline.AnalysisVersion != MeasurementAnalysis.Version
            || MeasurementAnalysis.Analyze(baseline.Id, baseline.Kind, baseline.StartedUtc, baseline.Scope, cycle.BaselineSamples).Status != MeasurementStatus.Valid)
            throw Failure(ErrorCodes.BaselineRequired);
        await validateRun(cycle, cancellation).ConfigureAwait(false);
        await pauseCollector(cancellation).ConfigureAwait(false);
        try
        {
            var current = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
            if (PlanBuilder.FingerprintPreferences(current) != plan.BaselineFingerprint) throw Failure(ErrorCodes.PreferenceDrift);
            cancellation.ThrowIfCancellationRequested();
            var beforeBackup = cycle;
            cycle = cycle with { Original = plan.Original, IntendedApplied = plan.Proposed,
                ObservedReadback = new Dictionary<string, PreferenceValue>(), ApplyStatus = ApplyStatus.Pending,
                OperationStage = "applying", UpdatedUtc = clock.GetUtcNow() };
            await store.SaveAsync(cycle, cancellation).ConfigureAwait(false); // Durable before the first possible POST.
            if (commitStarting is not null) await commitStarting().ConfigureAwait(false);
            await CancelBeforeCommitAsync(cycle, beforeBackup, cancellation).ConfigureAwait(false);
            return await CommitAsync(cycle, cycle.IntendedApplied, cycle.Original, false, plan.BaselineFingerprint).ConfigureAwait(false);
        }
        finally { await resumeCollector().ConfigureAwait(false); }
    }, token);

    public Task<CycleRecord> RollbackAsync(Guid cycleId, Guid planId, long revision, string confirmation,
        CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        var cycle = await LoadAsync(cycleId, cancellation).ConfigureAwait(false);
        ValidateSubject(cycle, planId, revision, "Rollback");
        Consume(cycle, planId, revision, "Rollback", confirmation);
        await pauseCollector(cancellation).ConfigureAwait(false);
        try
        {
            await session.RevalidateVersionsAsync(cancellation).ConfigureAwait(false);
            var current = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
            var expected = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
            var conflict = false;
            foreach (var (key, original) in cycle.Original)
            {
                if (!current.TryGetValue(key, out var actual) || actual != original
                    && (!cycle.IntendedApplied.TryGetValue(key, out var intended) || actual != intended))
                {
                    conflict = true;
                    continue;
                }
                expected.Add(key, actual);
            }
            if (conflict)
            {
                await SaveRecoveryAsync(cycle, current).ConfigureAwait(false);
                throw Failure(ErrorCodes.RollbackConflict);
            }
            cancellation.ThrowIfCancellationRequested();
            if (Matches(current, cycle.Original))
            {
                var reverted = cycle with { ApplyStatus = ApplyStatus.Reverted, OperationStage = "rolledBack",
                    ObservedReadback = Select(current, cycle.Original), UpdatedUtc = clock.GetUtcNow(),
                    Experiment = Historical(cycle.Experiment) };
                await store.SaveAsync(reverted, cancellation).ConfigureAwait(false);
                return reverted;
            }
            var pending = cycle with { ApplyStatus = ApplyStatus.Pending, OperationStage = "applying", UpdatedUtc = clock.GetUtcNow() };
            await store.SaveAsync(pending, cancellation).ConfigureAwait(false);
            if (commitStarting is not null) await commitStarting().ConfigureAwait(false);
            await CancelBeforeCommitAsync(pending, cycle, cancellation).ConfigureAwait(false);
            return await CommitAsync(pending, cycle.Original, expected, true).ConfigureAwait(false);
        }
        finally { await resumeCollector().ConfigureAwait(false); }
    }, token);

    // Reconnect recovery is read-only: an uncertain POST is never automatically replayed.
    public Task<CycleRecord> ReconcileAsync(Guid cycleId, CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        var cycle = await LoadAsync(cycleId, cancellation).ConfigureAwait(false);
        if (cycle.Original.Count == 0 || cycle.ApplyStatus == ApplyStatus.NotApplied) throw Failure(ErrorCodes.BackupInvalid);
        await session.RevalidateVersionsAsync(cancellation).ConfigureAwait(false);
        var actual = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
        var reverted = Matches(actual, cycle.Original);
        var applied = Matches(actual, cycle.IntendedApplied);
        var reconciled = cycle with { ApplyStatus = reverted ? ApplyStatus.Reverted : applied ? ApplyStatus.Verified : ApplyStatus.Unverified,
            OperationStage = reverted ? "rolledBack" : applied ? "appliedVerified" : "recoveryRequired",
            Experiment = reverted ? Historical(cycle.Experiment) : cycle.Experiment,
            ObservedReadback = Select(actual, cycle.Original), UpdatedUtc = clock.GetUtcNow() };
        await store.SaveAsync(reconciled, cancellation).ConfigureAwait(false);
        return reconciled;
    }, token);

    public Task<CycleRecord> KeepChangesAsync(Guid cycleId, CancellationToken token = default) => gate.RunAsync(async cancellation =>
    {
        var cycle = await LoadAsync(cycleId, cancellation).ConfigureAwait(false);
        if (cycle.ApplyStatus != ApplyStatus.Verified || cycle.OperationStage is not ("appliedVerified" or "afterReady"))
            throw Failure(ErrorCodes.ApplyUnverified);
        await session.RevalidateVersionsAsync(cancellation).ConfigureAwait(false);
        var actual = await session.ReadPreferencesAsync(cancellation).ConfigureAwait(false);
        if (!Matches(actual, cycle.IntendedApplied)) throw Failure(ErrorCodes.PreferenceDrift);
        var kept = cycle with { OperationStage = "completed", UpdatedUtc = clock.GetUtcNow(), ObservedReadback = Select(actual, cycle.Original) };
        await store.SaveAsync(kept, cancellation).ConfigureAwait(false);
        return kept;
    }, token);

    private async Task<CycleRecord> CommitAsync(CycleRecord pending, IReadOnlyDictionary<string, PreferenceValue> intended,
        IReadOnlyDictionary<string, PreferenceValue> expected, bool rollback, string? expectedFingerprint = null)
    {
        // User cancellation is deferred while reconciling the single, non-retryable POST.
        using var critical = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        IReadOnlyDictionary<string, PreferenceValue>? lastObserved = null;
        var interrupted = false;
        try
        {
            await session.WritePreferencesAsync(intended, expected, critical.Token, expectedFingerprint).ConfigureAwait(false);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var actual = await session.ReadPreferencesAsync(critical.Token).ConfigureAwait(false);
                lastObserved = actual;
                if (Matches(actual, intended))
                {
                    var verified = Resolved(pending, actual, rollback);
                    await store.SaveAsync(verified, critical.Token).ConfigureAwait(false);
                    return verified; // Never report success before verified journal persistence.
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), critical.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is QbittorrentException or OperationCanceledException or ObjectDisposedException)
        { interrupted = true; }

        // A failed/ambiguous POST is never retried. Read once under a fresh short deadline to
        // discover whether the server committed it; preserve the last known values on uncertainty.
        if (interrupted)
        {
            // The 45-second POST/reconciliation budget may itself have expired. The
            // single recovery read is deliberately granted its own bounded deadline.
            using var readbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                lastObserved = await session.ReadRecoveryPreferencesAsync(readbackDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is QbittorrentException or OperationCanceledException or ObjectDisposedException)
            { }
        }

        if (lastObserved is not null && (Matches(lastObserved, intended) || Matches(lastObserved, pending.Original)))
        {
            var resolved = Resolved(pending, lastObserved, rollback);
            using var resolvedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.SaveAsync(resolved, resolvedDeadline.Token).ConfigureAwait(false);
            return resolved;
        }
        return await SaveRecoveryAsync(pending, lastObserved).ConfigureAwait(false);
    }

    private CycleRecord Resolved(CycleRecord pending, IReadOnlyDictionary<string, PreferenceValue> actual, bool rollback)
    {
        var reverted = Matches(actual, pending.Original);
        return pending with
        {
            ApplyStatus = reverted || rollback ? ApplyStatus.Reverted : ApplyStatus.Verified,
            OperationStage = reverted || rollback ? "rolledBack" : "appliedVerified",
            ObservedReadback = Select(actual, pending.Original),
            Experiment = reverted || rollback ? Historical(pending.Experiment) : pending.Experiment,
            UpdatedUtc = clock.GetUtcNow()
        };
    }

    private static ExperimentSummary Historical(ExperimentSummary experiment) => experiment with
    { Results = experiment.Results.Select(card => card with { Context = ResultContext.Historical }).ToArray() };

    private async Task<CycleRecord> SaveRecoveryAsync(CycleRecord cycle,
        IReadOnlyDictionary<string, PreferenceValue>? actual)
    {
        var recovery = cycle with
        {
            ApplyStatus = ApplyStatus.Unverified,
            OperationStage = "recoveryRequired",
            ObservedReadback = actual is null ? cycle.ObservedReadback : Select(actual, cycle.Original),
            UpdatedUtc = clock.GetUtcNow()
        };
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.SaveAsync(recovery, recoveryDeadline.Token).ConfigureAwait(false);
        return recovery;
    }

    private async Task<CycleRecord> LoadAsync(Guid cycleId, CancellationToken token)
    {
        if (!session.IsValidated) throw Failure(ErrorCodes.TargetNotValidated);
        var versions = session.Versions;
        return await store.ReadAsync(cycleId, new(session.Endpoint.AbsoluteUri, versions.Qbittorrent, versions.WebApi, versions.Libtorrent), token)
            .ConfigureAwait(false) ?? throw Failure(ErrorCodes.BackupInvalid);
    }
    private Plan RequireCurrentPlan(CycleRecord cycle, Guid planId, long revision)
    {
        var plan = review.RequireApproved(planId, revision);
        if (plan.TargetSessionId != sessionId || Fingerprint(plan) != Fingerprint(cycle.Plan)
            || PlanBuilder.FingerprintInputs(cycle.Inputs) != plan.InputsFingerprint) throw Failure(ErrorCodes.PlanStale);
        return plan;
    }
    private async Task CancelBeforeCommitAsync(CycleRecord backup, CycleRecord previous, CancellationToken token)
    {
        if (!token.IsCancellationRequested) return;
        // We know no POST was attempted. Preserve the new backup but undo the pending marker.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.SaveAsync(backup with { ApplyStatus = previous.ApplyStatus, OperationStage = previous.OperationStage,
            UpdatedUtc = clock.GetUtcNow() }, deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }
    private static void ValidateSubject(CycleRecord cycle, Guid planId, long revision, string action)
    {
        if (cycle.Plan.Id != planId || cycle.Plan.Revision != revision) throw Failure(ErrorCodes.PlanStale);
        if (action == "ApplyPlan" && (cycle.ApplyStatus != ApplyStatus.NotApplied || cycle.OperationStage != "planReady"))
            throw Failure(ErrorCodes.RecoveryRequired);
        if (action == "Rollback" && (cycle.Original.Count == 0 || cycle.ApplyStatus is ApplyStatus.NotApplied or ApplyStatus.Reverted))
            throw Failure(ErrorCodes.BackupInvalid);
    }
    private void Consume(CycleRecord cycle, Guid planId, long revision, string action, string token)
    {
        if (!consents.Remove(token, out var consent) || consent.Expires <= clock.GetUtcNow()
            || consent.CycleId != cycle.CycleId || consent.PlanId != planId || consent.Revision != revision
            || consent.Action != action || consent.RecordFingerprint != Fingerprint(cycle)) throw Failure(ErrorCodes.ConfirmationExpired);
    }
    private static bool Matches(IReadOnlyDictionary<string, PreferenceValue> actual, IReadOnlyDictionary<string, PreferenceValue> expected)
        => expected.All(p => actual.TryGetValue(p.Key, out var value) && value == p.Value);
    private static Dictionary<string, PreferenceValue> Select(IReadOnlyDictionary<string, PreferenceValue> actual,
        IReadOnlyDictionary<string, PreferenceValue> keys) => keys.Keys.Where(actual.ContainsKey).ToDictionary(k => k, k => actual[k], StringComparer.Ordinal);
    private static string Fingerprint<T>(T record) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record, Protocol.Json)));
    private static QbittorrentException Failure(string code) => new(code);
    public void Dispose() { gate.Dispose(); }
}
