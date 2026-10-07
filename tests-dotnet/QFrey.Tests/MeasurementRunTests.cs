using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;

public sealed class MeasurementRunTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Measurement = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CapturesTenWarmupAndSixtyMeasuredSamplesAtActualElapsedTimes()
    {
        var clock = new SampleClock(Start);
        var contextCalls = 0;
        var run = NewRun(clock, (_, _) => { contextCalls++; return ValueTask.FromResult(Array.Empty<string>()); });

        for (var sequence = 0; sequence < 70; sequence++)
        {
            await run.ConsumeAsync(Sample(sequence, clock.Now), default);
            if (sequence < 69) clock.Advance(TimeSpan.FromSeconds(1));
        }

        var result = await run.Completion;
        Assert.Equal(70, result.Samples.Length);
        Assert.Equal(70, contextCalls);
        Assert.All(result.Samples.Take(10), sample => Assert.Equal(MeasurementStatus.WarmingUp, sample.Status));
        Assert.All(result.Samples.Skip(10), sample => Assert.Equal(MeasurementStatus.Sampling, sample.Status));
        Assert.Equal(9_000d, result.Samples[9].ElapsedMs);
        Assert.Equal(10_000d, result.Samples[10].ElapsedMs);
        Assert.Equal(69_000d, result.Samples[^1].ElapsedMs);
        Assert.Equal(60, result.Summary.SampleCount);
        Assert.Equal(MeasurementStatus.Valid, result.Summary.Status);
        Assert.Equal(69_000d, result.Summary.DurationMs);
    }

    [Fact]
    public async Task SequenceGapTerminatesRunAsInvalid()
    {
        var clock = new SampleClock(Start);
        var run = NewRun(clock);
        await run.ConsumeAsync(Sample(4, clock.Now), default);
        clock.Advance(TimeSpan.FromSeconds(1));

        await run.ConsumeAsync(Sample(6, clock.Now), default);

        var result = await run.Completion;
        Assert.Equal(2, result.Samples.Length);
        Assert.Equal(MeasurementStatus.Invalid, result.Samples[1].Status);
        Assert.Contains("SAMPLE_GAP", result.Samples[1].ReasonCodes);
        Assert.Equal(MeasurementStatus.Invalid, result.Summary.Status);
    }

    [Fact]
    public async Task StaleOrDuplicateFreshReadingInvalidatesTheRun()
    {
        var staleClock = new SampleClock(Start);
        var staleRun = NewRun(staleClock);
        var stale = Sample(0, staleClock.Now, readingAt: staleClock.Now.AddSeconds(-4));
        await staleRun.ConsumeAsync(stale, default);
        Assert.Contains("UNKNOWN_MEASUREMENT_FIELD", (await staleRun.Completion).Samples[0].ReasonCodes);

        var duplicateClock = new SampleClock(Start);
        var duplicateRun = NewRun(duplicateClock);
        await duplicateRun.ConsumeAsync(Sample(0, duplicateClock.Now), default);
        duplicateClock.Advance(TimeSpan.FromSeconds(1));
        await duplicateRun.ConsumeAsync(Sample(1, duplicateClock.Now, readingAt: Start), default);
        var duplicate = await duplicateRun.Completion;
        Assert.Equal(MeasurementStatus.Invalid, duplicate.Samples[1].Status);
        Assert.Contains("UNKNOWN_MEASUREMENT_FIELD", duplicate.Samples[1].ReasonCodes);
    }

    [Fact]
    public async Task MoreThanTwoSecondsBehindScheduleInvalidatesTheRun()
    {
        var clock = new SampleClock(Start);
        var run = NewRun(clock);
        await run.ConsumeAsync(Sample(0, clock.Now), default);
        clock.Advance(TimeSpan.FromSeconds(1));
        await run.ConsumeAsync(Sample(1, clock.Now), default);
        clock.Advance(TimeSpan.FromSeconds(4));

        await run.ConsumeAsync(Sample(2, clock.Now), default);

        var result = await run.Completion;
        Assert.Equal(MeasurementStatus.Invalid, result.Samples[^1].Status);
        Assert.Contains("SAMPLING_SCHEDULE_LATE", result.Samples[^1].ReasonCodes);
    }

    [Fact]
    public async Task ContextFailureInvalidatesRatherThanDroppingTheSample()
    {
        var clock = new SampleClock(Start);
        var run = NewRun(clock, (_, _) => throw new IOException("synthetic context failure"));

        await run.ConsumeAsync(Sample(0, clock.Now), default);

        var result = await run.Completion;
        Assert.Single(result.Samples);
        Assert.Equal(MeasurementStatus.Invalid, result.Samples[0].Status);
        Assert.Contains("RUN_CONTEXT_UNAVAILABLE", result.Samples[0].ReasonCodes);
    }

    [Fact]
    public async Task CancellationBeforeConsumeCompletesOnceWithoutAppendingLaterSamples()
    {
        var clock = new SampleClock(Start);
        var run = NewRun(clock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await run.ConsumeAsync(Sample(0, clock.Now), cancellation.Token);
        await run.ConsumeAsync(Sample(1, clock.Now), cancellation.Token);

        var result = await run.Completion;
        Assert.Single(result.Samples);
        Assert.Equal(MeasurementStatus.Cancelled, result.Samples[0].Status);
    }

    [Fact]
    public async Task CancellationWhileContextIsAwaitedCompletesOnceWithoutAppendingLateSample()
    {
        var clock = new SampleClock(Start);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = NewRun(clock, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return [];
        });
        var consume = run.ConsumeAsync(Sample(0, clock.Now), default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        run.Cancel();
        release.TrySetResult();
        await consume;
        await run.ConsumeAsync(Sample(1, clock.Now), default);

        var result = await run.Completion;
        Assert.Single(result.Samples);
        Assert.Equal(MeasurementStatus.Cancelled, result.Samples[0].Status);
        Assert.Equal("MEASUREMENT_CANCELLED", result.Samples[0].ReasonCodes.Single());
    }

    private static MeasurementRun NewRun(SampleClock clock,
        Func<LiveSample, CancellationToken, ValueTask<string[]>>? validate = null) =>
        new(Session, Measurement, MeasurementKind.Baseline, MetricScope.Session,
            validate ?? ((_, _) => ValueTask.FromResult(Array.Empty<string>())), clock);

    private static LiveSample Sample(long sequence, DateTimeOffset sampledAt, DateTimeOffset? readingAt = null)
    {
        var time = readingAt ?? sampledAt;
        LiveMetric Metric(string id, MetricUnit unit, MetricScope scope, double value) =>
            new(id, unit, scope, "test", new FreshReading(value, time));
        return new(Session, sequence, sampledAt,
        [
            Metric("transfer.download", MetricUnit.BytesPerSecond, MetricScope.Session, 10),
            Metric("transfer.upload", MetricUnit.BytesPerSecond, MetricScope.Session, 1),
            Metric("workload.download", MetricUnit.BytesPerSecond, MetricScope.Workload, 8),
            Metric("workload.upload", MetricUnit.BytesPerSecond, MetricScope.Workload, 1),
            Metric("peers.seeds", MetricUnit.Count, MetricScope.Workload, 2),
            Metric("peers.total", MetricUnit.Count, MetricScope.Workload, 3),
            Metric("workload.active", MetricUnit.Count, MetricScope.Workload, 1),
            Metric("workload.stalled", MetricUnit.Count, MetricScope.Workload, 0),
            Metric("workload.errors", MetricUnit.Count, MetricScope.Workload, 0)
        ]);
    }

    private sealed class SampleClock(DateTimeOffset now) : TimeProvider
    {
        private long ticks;
        public DateTimeOffset Now { get; private set; } = now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) { ticks += amount.Ticks; Now += amount; }
    }
}
