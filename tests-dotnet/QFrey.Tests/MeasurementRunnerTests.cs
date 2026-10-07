using System.Net;
using System.Text;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class MeasurementRunnerTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Preferences = "\"up_limit\":0,\"dl_limit\":0,\"max_connec\":500,\"max_connec_per_torrent\":100,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0,\"scheduler_enabled\":false,\"proxy_password\":\"redacted\"";

    [Fact]
    public async Task RunsSeventyPublishedSamplesOnOneSequentialReadLoopAndRevalidatesAtEnd()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi();
        using var session = await Connect(api, clock);
        var published = Channel.CreateUnbounded<LiveSample>();
        var run = MeasurementRunner.RunAsync(session, Guid.NewGuid(), Guid.NewGuid(), MeasurementKind.Baseline,
            [Hash], (sample, _) => { published.Writer.TryWrite(sample); return ValueTask.CompletedTask; }, default, clock);

        for (var i = 0; i < 70; i++)
        {
            var sample = await ReadPublished(published.Reader, run);
            Assert.Equal(i, sample.Sequence);
            if (i < 69) clock.Advance(TimeSpan.FromSeconds(1));
        }

        var capture = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(MeasurementStatus.Valid, capture.Measurement.Summary.Status);
        Assert.Equal(70, capture.Measurement.Samples.Length);
        Assert.NotNull(capture.FinalSnapshot);
        Assert.Equal(1, api.MaximumConcurrentRequests);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task PreferenceDriftDuringSamplingInvalidatesTheRun()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi();
        using var session = await Connect(api, clock);
        var published = Channel.CreateUnbounded<LiveSample>();
        var run = MeasurementRunner.RunAsync(session, Guid.NewGuid(), Guid.NewGuid(), MeasurementKind.Baseline,
            [Hash], (sample, _) =>
            {
                published.Writer.TryWrite(sample);
                if (sample.Sequence == 0) api.DriftPreferences = true;
                return ValueTask.CompletedTask;
            }, default, clock);

        _ = await ReadPublished(published.Reader, run);
        clock.Advance(TimeSpan.FromSeconds(1));
        var invalidSample = await ReadPublished(published.Reader, run);
        var capture = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, invalidSample.Sequence);
        Assert.Equal(MeasurementStatus.Invalid, capture.Measurement.Summary.Status);
        Assert.Contains("RELEVANT_PREFERENCES_CHANGED", capture.Measurement.Summary.ReasonCodes);
        Assert.NotNull(capture.FinalSnapshot);
    }

    [Fact]
    public async Task FinalReadDriftCannotReturnAValidResult()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi();
        using var session = await Connect(api, clock);
        var published = Channel.CreateUnbounded<LiveSample>();
        var run = MeasurementRunner.RunAsync(session, Guid.NewGuid(), Guid.NewGuid(), MeasurementKind.After,
            [Hash], (sample, _) =>
            {
                published.Writer.TryWrite(sample);
                if (sample.Sequence == 69) api.DriftPreferences = true;
                return ValueTask.CompletedTask;
            }, default, clock);

        for (var i = 0; i < 70; i++)
        {
            _ = await ReadPublished(published.Reader, run);
            if (i < 69) clock.Advance(TimeSpan.FromSeconds(1));
        }
        var capture = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(MeasurementStatus.Invalid, capture.Measurement.Summary.Status);
        Assert.Contains("RELEVANT_PREFERENCES_CHANGED", capture.Measurement.Summary.ReasonCodes);
        Assert.NotNull(capture.FinalSnapshot);
    }

    [Fact]
    public async Task CancellationDuringUiPublishStopsCollectorAndReturnsOneCancelledSample()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi();
        using var session = await Connect(api, clock);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = MeasurementRunner.RunAsync(session, Guid.NewGuid(), Guid.NewGuid(), MeasurementKind.Baseline,
            [Hash], async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, cancellation.Token, clock);
        var publishedOrFinished = await Task.WhenAny(entered.Task, run);
        if (publishedOrFinished == run)
        {
            var earlyCapture = await run;
            var sampleReasons = string.Join(";", earlyCapture.Measurement.Samples.Select(sample =>
                sample.Status + ":" + string.Join('|', sample.ReasonCodes)));
            Assert.Fail($"Runner ended before first publish: {earlyCapture.Measurement.Summary.Status}: {string.Join(',', earlyCapture.Measurement.Summary.ReasonCodes)}; samples={sampleReasons}");
        }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        cancellation.Cancel();
        var capture = await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(MeasurementStatus.Cancelled, capture.Measurement.Summary.Status);
        Assert.Single(capture.Measurement.Samples, sample => sample.Status == MeasurementStatus.Cancelled);
        Assert.Null(capture.FinalSnapshot);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    private static Task<QbittorrentSession> Connect(MeasurementApi api, TimeProvider clock) =>
        QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api, clock);

    private static async Task<LiveSample> ReadPublished(ChannelReader<LiveSample> reader, Task<MeasurementCapture> run)
    {
        var read = reader.ReadAsync().AsTask();
        var finished = await Task.WhenAny(read, run, Task.Delay(TimeSpan.FromSeconds(3)));
        if (finished == run)
        {
            var earlyCapture = await run;
            var sampleReasons = string.Join(";", earlyCapture.Measurement.Samples.Select(sample =>
                sample.Status + ":" + string.Join('|', sample.ReasonCodes)));
            Assert.Fail($"Runner ended before requested publish: {earlyCapture.Measurement.Summary.Status}: {string.Join(',', earlyCapture.Measurement.Summary.ReasonCodes)}; samples={sampleReasons}");
        }
        if (finished != read) throw new TimeoutException("Timed out waiting for measurement sample publication.");
        return await read;
    }

    private sealed class MeasurementApi : HttpMessageHandler
    {
        private int active;
        private int maximumActive;
        private int preferenceReads;
        public List<(HttpMethod Method, string Path)> Calls { get; } = [];
        public bool DriftPreferences { get; set; }
        public int MaximumConcurrentRequests => maximumActive;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var count = Interlocked.Increment(ref active);
            UpdateMaximum(count);
            try
            {
                await Task.Yield();
                var path = request.RequestUri!.AbsolutePath;
                Calls.Add((request.Method, path));
                var route = string.Join('/', path.Split('/').TakeLast(2));
                var body = route switch
                {
                    "app/version" => "v5.2.0",
                    "app/webapiVersion" => "2.15.0",
                    "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                    "app/preferences" => PreferencesBody(),
                    "transfer/info" => "{\"dl_info_speed\":100,\"up_info_speed\":20,\"dht_nodes\":7,\"use_alt_speed_limits\":false}",
                    "torrents/info" => "[{\"hash\":\"" + Hash + "\",\"state\":\"downloading\",\"progress\":0.2,\"dlspeed\":100,\"upspeed\":20,\"num_seeds\":2,\"num_leechs\":3}]",
                    _ => throw new InvalidOperationException("Unexpected read-only API request")
                };
                return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
            finally { Interlocked.Decrement(ref active); }
        }

        private string PreferencesBody()
        {
            var read = Interlocked.Increment(ref preferenceReads);
            var maxConnections = DriftPreferences && read > 2 ? 501 : 500;
            return "{" + Preferences.Replace("\"max_connec\":500", "\"max_connec\":" + maxConnections, StringComparison.Ordinal) + "}";
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximumActive);
                if (current >= value || Interlocked.CompareExchange(ref maximumActive, value, current) == current) return;
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long timestamp;
        private DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return timestamp; }
        public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (sync) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] current;
            lock (sync) { timestamp += duration.Ticks; now += duration; current = timers.ToArray(); }
            foreach (var timer in current) timer.Fire();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool active;
            public bool Change(TimeSpan dueTime, TimeSpan period) { active = dueTime != Timeout.InfiniteTimeSpan; return true; }
            public void Fire() { if (active) callback(state); }
            public void Dispose() => active = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
