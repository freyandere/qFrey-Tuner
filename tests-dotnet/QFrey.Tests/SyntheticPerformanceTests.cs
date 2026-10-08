using System.Diagnostics;
using System.Text.Json;
using QFrey.Core.Metrics;
using Xunit;
using Xunit.Abstractions;

namespace QFrey.Tests;

public sealed class SyntheticPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void AggregationReportsSyntheticTimingWithoutClaimingApplicationPerformance()
    {
        var rows = new List<object>();
        foreach (var count in new[] { 100, 1000, 5000 })
        {
            var hashes = Enumerable.Range(1, count).Select(index => index.ToString("x40")).ToArray();
            var payload = JsonSerializer.SerializeToUtf8Bytes(hashes.Select(hash => new
                { hash, state = "downloading", dlspeed = 1048576, upspeed = 1024, num_seeds = 2, num_leechs = 3, progress = 0.25 }));
            using var document = JsonDocument.Parse(payload);
            var sampledAt = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
            for (var warmup = 0; warmup < 20; warmup++) TorrentTelemetry.Aggregate(document.RootElement, hashes, sampledAt);
            var elapsed = new double[200];
            for (var sample = 0; sample < elapsed.Length; sample++)
            {
                var start = Stopwatch.GetTimestamp();
                var aggregated = TorrentTelemetry.Aggregate(document.RootElement, hashes, sampledAt);
                elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert.Equal(count, aggregated.Context.SelectedHashes.Count);
            }
            var sorted = elapsed.Order().ToArray();
            rows.Add(new { torrents = count, rawMilliseconds = elapsed, medianMs = (sorted[99] + sorted[100]) / 2,
                p95Ms = sorted[189], maximumMs = sorted[^1] });
        }
        var report = new { method = "TorrentTelemetry.Aggregate only; pre-parsed synthetic JSON; 20 warmups and 200 timed calls; assertions outside timing",
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logicalProcessors = Environment.ProcessorCount, buildConfiguration =
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            measuredUtc = DateTimeOffset.UtcNow, measurements = rows,
            limitations = "Does not measure API, JSON parsing, UI, process tree memory, startup or real download performance. No Python baseline comparison." };
        output.WriteLine(JsonSerializer.Serialize(report));
    }
}
