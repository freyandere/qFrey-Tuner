using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private async Task<string> StartAfterMeasurementAsync(CommandEnvelope command, StartMeasurementPayload payload,
        string canonical, CancellationToken token)
    {
        var operationSession = session;
        var operationTarget = target;
        if (operationTarget is null || operationSession?.IsValidated != true || command.TargetSessionId != operationTarget.SessionId)
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        if (command.ExpectedRevision != revision) return Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale");
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (payload.Kind != MeasurementKind.After || currentCycle is null || currentCycle.ApplyStatus != ApplyStatus.Verified
            || currentCycle.OperationStage is not ("appliedVerified" or "afterReady")
            || currentCycle.Experiment.After?.Status == MeasurementStatus.Valid)
            throw new QbittorrentException(ErrorCodes.BaselineRequired);
        if (recoveryBlocked) throw new QbittorrentException(ErrorCodes.RecoveryRequired);

        var expectedTarget = new TargetIdentity(operationTarget.Endpoint, operationTarget.QbittorrentVersion,
            operationTarget.ApiVersion, operationTarget.LibtorrentVersion);
        var cycle = await cycles.ReadAsync(currentCycle.CycleId, expectedTarget, token).ConfigureAwait(false)
            ?? throw new QbittorrentException(ErrorCodes.BaselineRequired);
        var expectedPreferences = ValidateAfterCycle(cycle, operationTarget.SessionId, experiment?.Plan, payload.Workload);

        var operationSessionId = operationTarget.SessionId;
        var wasCollecting = collectorCancellation is not null;
        await PauseDashboardAsync(token).ConfigureAwait(false);
        MeasurementSourceSnapshot initial;
        try
        {
            await operationSession.RevalidateVersionsAsync(token).ConfigureAwait(false);
            initial = await operationSession.ReadMeasurementSnapshotAsync(cycle.Experiment.Workload!.Reference.Hashes, token).ConfigureAwait(false);
            ValidateAfterSnapshot(cycle, expectedPreferences, initial);
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            if (wasCollecting && !disposed && !shuttingDown && operationSession.IsValidated
                && target?.SessionId == operationSessionId && ReferenceEquals(session, operationSession))
                StartDashboardCollector(operationSession, operationSessionId);
            throw;
        }

        RevokeConfirmations();
        var operationId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        activeOperation = new(operationId, OperationKind.Measurement, "after", null, true);
        currentCycle = cycle;
        experiment = cycle.Experiment;
        measurementCancellation = cancellation;
        revision++;
        measurementTask = Task.Run(() => RunAfterMeasurementAsync(operationId, operationSessionId,
            operationSession, cycle, expectedPreferences, cancellation));

        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId,
            true, revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private async Task RunAfterMeasurementAsync(Guid operationId, Guid sessionId, QbittorrentSession operationSession,
        CycleRecord cycle, IReadOnlyDictionary<string, PreferenceValue> expectedPreferences, CancellationTokenSource cancellation)
    {
        try
        {
            Func<LiveMetric[]>? local = null;
            try { local = localMetrics?.Invoke(operationSession.Endpoint.AbsoluteUri); }
            catch { /* Optional local metrics never invalidate server measurement. */ }
            var hashes = cycle.Experiment.Workload!.Reference.Hashes;
            var capture = await MeasurementRunner.RunAsync(operationSession, sessionId, operationId,
                MeasurementKind.After, hashes,
                (sample, token) => PublishAfterSampleAsync(operationId, sessionId, sample, local, token),
                cancellation.Token, timeProvider).ConfigureAwait(false);

            if (cancellation.IsCancellationRequested) capture = MeasurementRunner.MarkCancelled(capture);
            else if (capture.FinalSnapshot is { } final)
            {
                try
                {
                    ValidateAfterSnapshot(cycle, expectedPreferences, capture.InitialSnapshot);
                    ValidateAfterSnapshot(cycle, expectedPreferences, final);
                }
                catch (QbittorrentException failure)
                {
                    capture = InvalidateAfterCapture(capture, failure.Code);
                }
            }

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (disposed || activeOperation?.Id != operationId || target?.SessionId != sessionId) return;
                if (cancellation.IsCancellationRequested) capture = MeasurementRunner.MarkCancelled(capture);
                activeOperation = activeOperation with { Stage = "completing", Cancellable = false };
                var baseline = cycle.Experiment.Baseline!;
                var results = MeasurementAnalysis.Compare(cycle.CycleId, baseline, cycle.BaselineSamples,
                    capture.Measurement.Summary, capture.Measurement.Samples, timeProvider.GetUtcNow())
                    .Select(card => card with { Context = ResultContext.CurrentExperiment }).ToArray();
                var validAfter = capture.Measurement.Summary.Status == MeasurementStatus.Valid
                    && capture.Measurement.Summary.AnalysisVersion == MeasurementAnalysis.Version;
                var updated = cycle with
                {
                    UpdatedUtc = timeProvider.GetUtcNow(),
                    OperationStage = validAfter ? "afterReady" : "appliedVerified",
                    ApplyStatus = ApplyStatus.Verified,
                    AfterSamples = capture.Measurement.Samples,
                    Experiment = cycle.Experiment with { After = capture.Measurement.Summary, Results = results }
                };
                using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await cycles.SaveAsync(updated, saveDeadline.Token).ConfigureAwait(false);
                currentCycle = updated;
                experiment = updated.Experiment;
                availableActions = [];
            }
            finally { gate.Release(); }
        }
        catch (QbittorrentException error) { await FinishAfterFailureAsync(operationId, sessionId, error.Code).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await FinishAfterFailureAsync(operationId, sessionId, "MEASUREMENT_CANCELLED").ConfigureAwait(false);
        }
        catch (Exception)
        {
            await FinishAfterFailureAsync(operationId, sessionId, ErrorCodes.MeasurementInvalid).ConfigureAwait(false);
        }
        finally
        {
            string? completionEvent = null;
            await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (activeOperation?.Id == operationId)
                    {
                        activeOperation = null;
                        revision++;
                        completionEvent = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                            NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    }
                    if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
                }
                finally { gate.Release(); }

                if (completionEvent is not null)
                {
                    try { await PublishSnapshotAsync(completionEvent).ConfigureAwait(false); }
                    catch (Exception) { }
                }

                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (!disposed && !shuttingDown && target?.SessionId == sessionId && ReferenceEquals(session, operationSession))
                        StartDashboardCollector(operationSession, sessionId);
                }
                finally { gate.Release(); }
            }
            finally
            {
                commandAdmission.Release();
                cancellation.Dispose();
            }
        }
    }

    private async Task FinishAfterFailureAsync(Guid operationId, Guid sessionId, string code)
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
                availableActions = [new("operation.error", false, code, ErrorMessageKey(code))];
        }
        finally { gate.Release(); }
    }

    private async ValueTask PublishAfterSampleAsync(Guid operationId, Guid sessionId, LiveSample sample,
        Func<LiveMetric[]>? local, CancellationToken token)
    {
        if (local is not null)
        {
            try { sample = sample with { Metrics = Array.AsReadOnly(sample.Metrics.Concat(local()).ToArray()) }; }
            catch { /* Optional process metrics do not affect the server sample. */ }
        }
        string? message = null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!disposed && activeOperation is { } operation && operation.Id == operationId && target?.SessionId == sessionId)
            {
                metrics = sample.Metrics.ToArray();
                var progress = Math.Clamp((sample.Sequence + 1) * 100d / 70d, 0, 100);
                activeOperation = operation with { Stage = sample.Sequence < 10 ? "warmingUp" : "sampling", Progress = progress };
                message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId, NextEventSequence(), revision, Snapshot()), Protocol.Json);
            }
        }
        finally { gate.Release(); }
        if (message is not null) await PublishSnapshotAsync(message).ConfigureAwait(false);
    }

    internal static Dictionary<string, PreferenceValue> ValidateAfterCycle(CycleRecord cycle, Guid sessionId,
        Plan? currentPlan, WorkloadReference requested)
    {
        var baseline = cycle.Experiment.Baseline;
        var context = cycle.BaselineContext;
        var workload = cycle.Experiment.Workload;
        var reference = workload?.Reference;
        if (cycle.ApplyStatus != ApplyStatus.Verified || cycle.OperationStage is not ("appliedVerified" or "afterReady")
            || cycle.AnalysisVersion != MeasurementAnalysis.Version || cycle.Plan.TargetSessionId != sessionId
            || currentPlan is null || currentPlan.Id != cycle.Plan.Id || currentPlan.Revision != cycle.Plan.Revision
            || currentPlan.TargetSessionId != sessionId || !cycle.Plan.Applicable || cycle.Plan.PreviewOnly
            || baseline is null || baseline.Kind != MeasurementKind.Baseline || baseline.Status != MeasurementStatus.Valid
            || baseline.AnalysisVersion != MeasurementAnalysis.Version || baseline.Id == Guid.Empty
            || MeasurementAnalysis.Analyze(baseline.Id, baseline.Kind, baseline.StartedUtc, baseline.Scope, cycle.BaselineSamples).Status != MeasurementStatus.Valid
            || context is null || reference is null || reference.Kind != WorkloadKind.Existing || requested is null
            || requested.Kind != WorkloadKind.Existing || requested.Id != reference.Id || requested.Hashes is null
            || !requested.Hashes.SequenceEqual(reference.Hashes, StringComparer.OrdinalIgnoreCase)
            || context.Preferences is null || context.PreferencesFingerprint != PlanBuilder.FingerprintPreferences(context.Preferences)
            || cycle.Plan.BaselineFingerprint != context.PreferencesFingerprint)
            throw new QbittorrentException(ErrorCodes.BaselineRequired);

        var hashes = reference.Hashes;
        var selected = context.SelectedTorrents;
        var active = context.ActiveHashes;
        if (hashes is not { Length: > 0 and <= 5000 }
            || hashes.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))
            || hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != hashes.Length
            || selected is not { Length: > 0 and <= 5000 } || active is not { Length: > 0 and <= 5000 }
            || selected.Any(row => row is null || row.Hash is not { Length: 40 } || !row.Hash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(row.State) || row.State.Length > 32
                || row.Progress is double progress && (!double.IsFinite(progress) || progress is < 0 or > 1))
            || active.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))
            || selected.Select(row => row.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length
            || active.Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length
            || !selected.Select(row => row.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(hashes)
            || selected.Any(row => !active.Contains(row.Hash, StringComparer.OrdinalIgnoreCase)))
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);

        if (cycle.IntendedApplied.Count != cycle.Plan.Proposed.Count
            || cycle.Plan.Proposed.Any(pair => !cycle.IntendedApplied.TryGetValue(pair.Key, out var intended) || intended != pair.Value))
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
        var expected = context.Preferences.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (key, value) in cycle.IntendedApplied)
        {
            if (!expected.ContainsKey(key)) throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
            expected[key] = value;
        }
        return expected;
    }

    internal static void ValidateAfterSnapshot(CycleRecord cycle, IReadOnlyDictionary<string, PreferenceValue> expected,
        MeasurementSourceSnapshot snapshot)
    {
        if (!SamePreferences(snapshot.Preferences, expected)) throw new QbittorrentException(ErrorCodes.PreferenceDrift);
        if (snapshot.SchedulerEnabled is not false || snapshot.AlternativeLimitsEnabled is not false)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
        var context = cycle.BaselineContext!;
        var selected = context.SelectedTorrents;
        var baselineTorrents = new TorrentTelemetryContext(selected.Select(row => row.Hash).ToArray(), context.ActiveHashes,
            null, null)
        {
            SelectedTorrents = selected.Select(row => new SelectedTorrentMetadata(row.Hash, row.State,
                null, row.Progress, null)).ToArray()
        };
        var baseline = new MeasurementContinuitySnapshot(PlanBuilder.FingerprintPreferences(expected), false, false, baselineTorrents);
        var current = new MeasurementContinuitySnapshot(PlanBuilder.FingerprintPreferences(snapshot.Preferences),
            snapshot.SchedulerEnabled, snapshot.AlternativeLimitsEnabled, snapshot.Torrents.Context);
        if (MeasurementContinuity.Validate(baseline, current).Count != 0)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
    }

    private static bool SamePreferences(IReadOnlyDictionary<string, PreferenceValue> actual,
        IReadOnlyDictionary<string, PreferenceValue> expected) => actual.Count == expected.Count
        && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static MeasurementCapture InvalidateAfterCapture(MeasurementCapture capture, string reason)
    {
        var elapsed = (capture.Measurement.Samples.LastOrDefault()?.ElapsedMs ?? 0) + .001;
        var unavailable = new UnavailableReading("MEASUREMENT_INVALID");
        var terminal = new MeasurementSample(elapsed, MeasurementStatus.Invalid, [reason], unavailable, unavailable,
            unavailable, unavailable, unavailable, unavailable, null, null, null);
        var samples = capture.Measurement.Samples.Append(terminal).ToArray();
        var summary = MeasurementAnalysis.Analyze(capture.Measurement.Summary.Id, MeasurementKind.After,
            capture.Measurement.Summary.StartedUtc, capture.Measurement.Summary.Scope, samples);
        return capture with { Measurement = new(summary, samples) };
    }
}
