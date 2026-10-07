using System.Threading.Channels;
using QFrey.Core.Contracts;

namespace QFrey.Core.Metrics;

public sealed record LiveSample(Guid SessionId, long Sequence, DateTimeOffset SampledAtUtc, IReadOnlyList<LiveMetric> Metrics);

// One target source; measurement receives each sample before latest-only UI coalescing.
public sealed class LiveCollector
{
    private readonly Func<CancellationToken, Task<LiveMetric[]>> read;
    private readonly TimeProvider time;
    private readonly Guid sessionId;
    private readonly Queue<LiveSample> history = new();
    private readonly object historyLock = new();
    private readonly Channel<LiveSample> latest = Channel.CreateBounded<LiveSample>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = true });
    private int running;
    public ChannelReader<LiveSample> Latest => latest.Reader;
    public LiveSample[] History { get { lock (historyLock) return history.ToArray(); } }
    public LiveCollector(Guid sessionId, Func<CancellationToken, Task<LiveMetric[]>> read, TimeProvider? time = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException(nameof(sessionId));
        this.sessionId = sessionId; this.read = read; this.time = time ?? TimeProvider.System;
    }
    public async Task RunAsync(Func<LiveSample, CancellationToken, ValueTask>? measurement, CancellationToken token)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException(ErrorCodes.OperationConflict);
        long sequence = 0;
        LiveMetric[] previous = [
            new("transfer.download", MetricUnit.BytesPerSecond, MetricScope.Session, "transfer/info:dl_info_speed", new UnavailableReading("NOT_SAMPLED")),
            new("transfer.upload", MetricUnit.BytesPerSecond, MetricScope.Session, "transfer/info:up_info_speed", new UnavailableReading("NOT_SAMPLED")),
            new("session.dhtNodes", MetricUnit.Count, MetricScope.Session, "transfer/info:dht_nodes", new UnavailableReading("NOT_SAMPLED"))];
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        try
        {
            do
            {
                LiveMetric[] metrics;
                try { metrics = await read(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    metrics = previous.Select(metric => metric with { Reading = metric.Reading switch {
                        FreshReading r when time.GetUtcNow() - r.SampledAtUtc <= TimeSpan.FromSeconds(metric.Scope == MetricScope.LocalProcess ? 6 : 3) => r,
                        FreshReading r => new StaleReading(r.Value, r.SampledAtUtc, "SOURCE_UNAVAILABLE"),
                        StaleReading r => r,
                        _ => new ErrorReading("SOURCE_UNAVAILABLE") } }).ToArray();
                }
                var sample = new LiveSample(sessionId, sequence++, time.GetUtcNow(), Array.AsReadOnly(metrics.ToArray()));
                if (measurement is not null) await measurement(sample, token).ConfigureAwait(false);
                lock (historyLock) { history.Enqueue(sample); while (history.Count > 600) history.Dequeue(); }
                latest.Writer.TryWrite(sample);
                previous = metrics;
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { latest.Writer.TryComplete(); }
    }
}
