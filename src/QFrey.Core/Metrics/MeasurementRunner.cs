using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;

namespace QFrey.Core.Metrics;

public sealed record MeasurementCapture(
    MeasurementRunResult Measurement,
    MeasurementSourceSnapshot InitialSnapshot,
    MeasurementSourceSnapshot? FinalSnapshot);

// Runs a bounded measurement over the session's one sequential read loop. It never
// writes qBittorrent settings, workload state, or files.
public static class MeasurementRunner
{
    public static MeasurementCapture MarkCancelled(MeasurementCapture capture) =>
        capture with { Measurement = AsCancelled(capture.Measurement), FinalSnapshot = null };

    public static async Task<MeasurementCapture> RunAsync(
        QbittorrentSession session,
        Guid sessionId,
        Guid measurementId,
        MeasurementKind kind,
        IReadOnlyCollection<string> selectedHashes,
        Func<LiveSample, CancellationToken, ValueTask> publish,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selectedHashes);
        ArgumentNullException.ThrowIfNull(publish);
        if (sessionId == Guid.Empty || measurementId == Guid.Empty
            || kind is not (MeasurementKind.Baseline or MeasurementKind.After)
            || selectedHashes.Count is < 1 or > TorrentTelemetry.MaximumSelectedHashes)
            throw new ArgumentException("INVALID_MEASUREMENT_INPUT");

        var selected = Array.AsReadOnly(selectedHashes.ToArray());
        var clock = timeProvider ?? TimeProvider.System;
        await session.RevalidateVersionsAsync(cancellationToken).ConfigureAwait(false);
        var initial = await session.ReadMeasurementSnapshotAsync(selected, cancellationToken).ConfigureAwait(false);
        var initialContinuity = ToContinuity(initial);
        var initialReasons = Validate(initialContinuity, initialContinuity);
        MeasurementSourceSnapshot? latestSnapshot = null;
        var run = NewRun(initialContinuity);
        if (initialReasons.Length > 0)
        {
            latestSnapshot = initial;
            var sample = new LiveSample(sessionId, 0, clock.GetUtcNow(), Array.AsReadOnly(initial.Metrics.ToArray()));
            await run.ConsumeAsync(sample, cancellationToken).ConfigureAwait(false);
            var invalid = await run.Completion.ConfigureAwait(false);
            return new(invalid, initial, initial);
        }

        MeasurementRun NewRun(MeasurementContinuitySnapshot baseline) => new(
            sessionId, measurementId, kind, MetricScope.Workload,
            (sample, _) =>
            {
                var snapshot = latestSnapshot;
                string[] reasons = snapshot is null
                    ? ["RUN_CONTEXT_UNAVAILABLE"]
                    : Validate(baseline, ToContinuity(snapshot));
                return ValueTask.FromResult(reasons);
            }, clock);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var collector = new LiveCollector(sessionId, async token =>
        {
            latestSnapshot = null;
            try
            {
                latestSnapshot = await session.ReadMeasurementSnapshotAsync(selected, token).ConfigureAwait(false);
                return latestSnapshot.Metrics;
            }
            catch
            {
                latestSnapshot = null;
                throw;
            }
        }, clock);

        Exception? pipelineFailure = null;
        try
        {
            await collector.RunAsync(async (sample, token) =>
            {
                await run.ConsumeAsync(sample, token).ConfigureAwait(false);
                await publish(sample, token).ConfigureAwait(false);
                if (run.Completion.IsCompleted) linked.Cancel();
            }, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            pipelineFailure = error;
        }
        finally
        {
            if (!run.Completion.IsCompleted) run.Cancel();
        }

        if (pipelineFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pipelineFailure).Throw();
        var result = await run.Completion.ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
            return new(AsCancelled(result), initial, null);

        MeasurementSourceSnapshot final;
        try
        {
            await session.RevalidateVersionsAsync(cancellationToken).ConfigureAwait(false);
            final = await session.ReadMeasurementSnapshotAsync(selected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(AsCancelled(result), initial, null);
        }
        catch (Exception)
        {
            return new(Invalidate(result, ["FINAL_CONTEXT_UNAVAILABLE"]), initial, null);
        }

        var finalReasons = Validate(initialContinuity, ToContinuity(final));
        return finalReasons.Length == 0
            ? new(result, initial, final)
            : new(Invalidate(result, finalReasons), initial, final);
    }

    private static MeasurementContinuitySnapshot ToContinuity(MeasurementSourceSnapshot snapshot) => new(
        PlanBuilder.FingerprintPreferences(snapshot.Preferences), snapshot.SchedulerEnabled,
        snapshot.AlternativeLimitsEnabled, snapshot.Torrents.Context);

    private static string[] Validate(MeasurementContinuitySnapshot before, MeasurementContinuitySnapshot current)
    {
        var reasons = MeasurementContinuity.Validate(before, current).ToList();
        CheckSelectedActive(before.TorrentContext, reasons);
        CheckSelectedActive(current.TorrentContext, reasons);
        return reasons.Distinct(StringComparer.Ordinal).ToArray();

        static void CheckSelectedActive(TorrentTelemetryContext? context, List<string> reasons)
        {
            if (context?.ActiveHashes is null || context.SelectedHashes is null)
            {
                if (!reasons.Contains("ACTIVE_HASH_SET_UNKNOWN", StringComparer.Ordinal)) reasons.Add("ACTIVE_HASH_SET_UNKNOWN");
                return;
            }
            if (context.SelectedHashes.Any(hash => !context.ActiveHashes.Contains(hash, StringComparer.OrdinalIgnoreCase)))
                reasons.Add("SELECTED_WORKLOAD_NOT_ACTIVE");
        }
    }

    private static MeasurementRunResult Invalidate(MeasurementRunResult result, string[] reasons)
    {
        if (reasons.Length == 0) return result;
        var unknown = new UnavailableReading("MEASUREMENT_INVALID");
        var elapsed = (result.Samples.LastOrDefault()?.ElapsedMs ?? 0) + .001;
        var terminal = new MeasurementSample(elapsed, MeasurementStatus.Invalid, reasons,
            unknown, unknown, unknown, unknown, unknown, unknown, null, null, null);
        var samples = result.Samples.Append(terminal).ToArray();
        var summary = MeasurementAnalysis.Analyze(result.Summary.Id, result.Summary.Kind,
            result.Summary.StartedUtc, result.Summary.Scope, samples);
        return new(summary, samples);
    }

    private static MeasurementRunResult AsCancelled(MeasurementRunResult result)
    {
        if (result.Samples.Any(sample => sample.Status == MeasurementStatus.Cancelled))
            return result with { Summary = result.Summary with { Status = MeasurementStatus.Cancelled } };
        var unknown = new UnavailableReading("MEASUREMENT_CANCELLED");
        var elapsed = (result.Samples.LastOrDefault()?.ElapsedMs ?? -1) + .001;
        var terminal = new MeasurementSample(elapsed, MeasurementStatus.Cancelled, ["MEASUREMENT_CANCELLED"],
            unknown, unknown, unknown, unknown, unknown, unknown, null, null, null);
        var samples = result.Samples.Append(terminal).ToArray();
        var summary = MeasurementAnalysis.Analyze(result.Summary.Id, result.Summary.Kind,
            result.Summary.StartedUtc, result.Summary.Scope, samples) with { Status = MeasurementStatus.Cancelled };
        return new(summary, samples);
    }
}
