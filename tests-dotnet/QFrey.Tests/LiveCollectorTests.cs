using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;
public class LiveCollectorTests
{
    private sealed class StepClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);
        private Action? tick;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { tick = () => callback(state); return new TimerHandle(); }
        public void Advance() { now += TimeSpan.FromSeconds(1); tick!(); }
        private sealed class TimerHandle : ITimer
        { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    [Fact]
    public async Task BoundedUiDropsDoNotLoseMeasurementSamplesOrGrowHistory()
    {
        using var cancel = new CancellationTokenSource();
        var clock = new StepClock();
        var arrivals = System.Threading.Channels.Channel.CreateUnbounded<long>();
        var count = 0;
        var collector = new LiveCollector(Guid.NewGuid(), _ => { count++; return Task.FromResult(Array.Empty<LiveMetric>()); }, clock);
        var run = collector.RunAsync((sample, _) => { arrivals.Writer.TryWrite(sample.Sequence); return ValueTask.CompletedTask; }, cancel.Token);
        Assert.Equal(0, await arrivals.Reader.ReadAsync());
        for (var i = 1; i <= 601; i++)
        {
            clock.Advance();
            Assert.Equal(i, await arrivals.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        cancel.Cancel(); await run;
        Assert.Equal(602, count); Assert.Equal(600, collector.History.Length);
        Assert.True(collector.Latest.TryRead(out var last)); Assert.Equal(601, last!.Sequence);
        Assert.False(collector.Latest.TryRead(out _));
    }
    [Fact]
    public async Task OneSourceFeedsMeasurementAndUiAndCancelsGracefully()
    {
        using var cancel = new CancellationTokenSource();
        var readCount = 0;
        var source = new[] { new LiveMetric("transfer.download", MetricUnit.BytesPerSecond, MetricScope.Session,
            "transfer/info:dl_info_speed", new FreshReading(42, DateTimeOffset.UtcNow)) };
        var collector = new LiveCollector(Guid.NewGuid(), _ => { readCount++; return Task.FromResult(source); });
        LiveSample? measured = null;
        await collector.RunAsync((sample, _) => { measured = sample; cancel.Cancel(); return ValueTask.CompletedTask; }, cancel.Token);
        Assert.Equal(1, readCount); Assert.NotNull(measured);
        Assert.True(collector.Latest.TryRead(out var ui)); Assert.Same(measured, ui);
        source[0] = source[0] with { Reading = new FreshReading(999, DateTimeOffset.UtcNow) };
        Assert.Equal(42, Assert.IsType<FreshReading>(measured.Metrics[0].Reading).Value);
        Assert.Single(collector.History);
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.RunAsync(null, default));
    }
    [Fact]
    public async Task CancelledReadDoesNotPublishAnInventedSample()
    {
        using var cancel = new CancellationTokenSource();
        var collector = new LiveCollector(Guid.NewGuid(), async token => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, token); return []; });
        await collector.RunAsync(null, cancel.Token);
        Assert.Empty(collector.History); Assert.False(collector.Latest.TryRead(out _));
    }
    [Fact]
    public async Task MeasurementFailureIsObservedAndNotSilentlyDropped()
    {
        var collector = new LiveCollector(Guid.NewGuid(), _ => Task.FromResult(Array.Empty<LiveMetric>()));
        await Assert.ThrowsAsync<IOException>(() => collector.RunAsync((_,_) => throw new IOException("synthetic sink failure"), default));
        Assert.Empty(collector.History); Assert.False(collector.Latest.TryRead(out _));
        Assert.True(collector.Latest.Completion.IsCompleted);
    }
}
