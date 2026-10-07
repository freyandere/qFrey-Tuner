using QFrey.Core.Contracts;

namespace QFrey.Core.Metrics;

public sealed record MeasurementRunResult(MeasurementSummary Summary, MeasurementSample[] Samples);

// Consumes the existing collector; it never creates a second polling loop.
public sealed class MeasurementRun
{
    private const int SampleCount = 60;
    private readonly Guid sessionId;
    private readonly Guid measurementId;
    private readonly MeasurementKind kind;
    private readonly MetricScope scope;
    private readonly TimeProvider clock;
    private readonly long origin;
    private readonly DateTimeOffset startedUtc;
    private readonly Func<LiveSample, CancellationToken, ValueTask<string[]>> validateContext;
    private readonly List<MeasurementSample> samples = new(70);
    private readonly object sync = new();
    private readonly TaskCompletionSource<MeasurementRunResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long? previousSequence;
    private DateTimeOffset? previousDownload, previousUpload;
    private int measured;
    public Task<MeasurementRunResult> Completion => completion.Task;

    public MeasurementRun(Guid sessionId, Guid measurementId, MeasurementKind kind, MetricScope scope,
        Func<LiveSample, CancellationToken, ValueTask<string[]>> validateContext, TimeProvider? clock = null)
    {
        if (sessionId == Guid.Empty || measurementId == Guid.Empty || !Enum.IsDefined(kind)
            || scope is not (MetricScope.Session or MetricScope.Workload)) throw new ArgumentException("INVALID_MEASUREMENT_INPUT");
        this.sessionId = sessionId; this.measurementId = measurementId; this.kind = kind; this.scope = scope;
        this.validateContext = validateContext ?? throw new ArgumentNullException(nameof(validateContext));
        this.clock = clock ?? TimeProvider.System;
        origin = this.clock.GetTimestamp(); startedUtc = this.clock.GetUtcNow();
    }

    // Serialized by the sole collector. Terminal results complete once and remain immutable.
    public async ValueTask ConsumeAsync(LiveSample live, CancellationToken token)
    {
        int count;
        lock (sync) { if (completion.Task.IsCompleted) return; count = samples.Count; }
        if (token.IsCancellationRequested) { Cancel(); return; }
        var elapsed = clock.GetElapsedTime(origin).TotalMilliseconds;
        var reasons = new List<string>();
        if (live.SessionId != sessionId) reasons.Add("SESSION_STALE");
        if (previousSequence is long sequence && live.Sequence != sequence + 1) reasons.Add("SAMPLE_GAP");
        previousSequence = live.Sequence;
        if (elapsed - count * 1000 > 2000 || count >= 90) reasons.Add("SAMPLING_SCHEDULE_LATE");
        var sessionDownload = Read("transfer.download", MetricScope.Session, MetricUnit.BytesPerSecond);
        var sessionUpload = Read("transfer.upload", MetricScope.Session, MetricUnit.BytesPerSecond);
        var workloadDownload = Read("workload.download", MetricScope.Workload, MetricUnit.BytesPerSecond);
        var workloadUpload = Read("workload.upload", MetricScope.Workload, MetricUnit.BytesPerSecond);
        var download = scope == MetricScope.Session ? sessionDownload : workloadDownload;
        var upload = scope == MetricScope.Session ? sessionUpload : workloadUpload;
        ValidateReading(download, ref previousDownload);
        ValidateReading(upload, ref previousUpload);
        if (count == 0 && download is FreshReading dl && upload is FreshReading ul && dl.Value + ul.Value <= 0)
            reasons.Add("NO_ACTIVE_TRAFFIC");
        try { reasons.AddRange(await validateContext(live, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancel(); return; }
        catch (Exception) { reasons.Add("RUN_CONTEXT_UNAVAILABLE"); }
        var status = reasons.Count > 0 ? MeasurementStatus.Invalid
            : elapsed < 10_000 ? MeasurementStatus.WarmingUp : MeasurementStatus.Sampling;
        lock (sync)
        {
            if (completion.Task.IsCompleted) return; // Cancel can complete while the context guard awaits I/O.
            samples.Add(new(elapsed, status, reasons.Distinct(StringComparer.Ordinal).ToArray(), sessionDownload,
                sessionUpload, workloadDownload, workloadUpload, Read("peers.seeds", MetricScope.Workload, MetricUnit.Count),
                Read("peers.total", MetricScope.Workload, MetricUnit.Count), Count("workload.active"), Count("workload.stalled"), Count("workload.errors")));
            if (status == MeasurementStatus.Sampling) measured++;
            if (status == MeasurementStatus.Invalid || measured == SampleCount) Finish();
        }

        MetricReading Read(string id, MetricScope expectedScope, MetricUnit unit)
        {
            var matches = live.Metrics.Where(m => m.Id == id).ToArray();
            return matches.Length == 1 && matches[0].Scope == expectedScope && matches[0].Unit == unit
                ? matches[0].Reading : new UnavailableReading("FIELD_MISSING_OR_AMBIGUOUS");
        }
        int? Count(string id) => Read(id, MetricScope.Workload, MetricUnit.Count) is FreshReading r
            && r.Value >= 0 && r.Value <= int.MaxValue && r.Value == Math.Truncate(r.Value) ? (int)r.Value : null;
        void ValidateReading(MetricReading reading, ref DateTimeOffset? previous)
        {
            if (reading is not FreshReading r || !double.IsFinite(r.Value) || r.Value < 0
                || r.SampledAtUtc.Offset != TimeSpan.Zero || r.SampledAtUtc > live.SampledAtUtc
                || live.SampledAtUtc - r.SampledAtUtc > TimeSpan.FromSeconds(3)
                || previous is { } last && r.SampledAtUtc <= last)
            { reasons.Add("UNKNOWN_MEASUREMENT_FIELD"); return; }
            previous = r.SampledAtUtc;
        }
    }

    public void Cancel()
    {
        lock (sync)
        {
            if (completion.Task.IsCompleted) return;
            var unknown = new UnavailableReading("MEASUREMENT_CANCELLED");
            var elapsed = Math.Max(clock.GetElapsedTime(origin).TotalMilliseconds, (samples.LastOrDefault()?.ElapsedMs ?? -1) + .001);
            samples.Add(new(elapsed, MeasurementStatus.Cancelled, ["MEASUREMENT_CANCELLED"], unknown, unknown, unknown, unknown, unknown, unknown, null, null, null));
            Finish();
        }
    }
    private void Finish()
    {
        var raw = samples.ToArray();
        completion.TrySetResult(new(MeasurementAnalysis.Analyze(measurementId, kind, startedUtc, scope, raw), raw));
    }
}
