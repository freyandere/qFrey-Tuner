using System.Text.Json;
using Xunit;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;

namespace QFrey.Tests;

public sealed class TorrentTelemetryTests
{
    [Fact]
    public void ForcedMetadataStateIsKnownButDoesNotBecomePayloadActivity()
    {
        using var json = Parse($"[{Torrent(A, "downloading", 4, 5, 2, 3)},{Torrent(B, "forcedMetaDL", 0, 0, 0, 0)}]");
        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);
        Assert.Equal(new[] { A }, result.Context.ActiveHashes);
        Assert.True(result.Context.SelectedMatchesActive);
    }
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset SampledAt = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AggregatesOnlySelectedHashesAndReportsActiveSetSeparately()
    {
        using var json = Parse($"[{Torrent(A, "downloading", 4, 5, 2, 3)},{Torrent(B, "stalledUP", 90, 80, 7, 8)}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);

        Assert.Equal(new[] { A }, result.Context.SelectedHashes);
        Assert.Equal(new[] { A, B }, result.Context.ActiveHashes);
        Assert.False(result.Context.SelectedMatchesActive);
        AssertFresh(result, "workload.download", 4);
        AssertFresh(result, "workload.upload", 5);
        AssertFresh(result, "peers.seeds", 2);
        AssertFresh(result, "peers.total", 5);
        AssertFresh(result, "workload.active", 1);
        AssertFresh(result, "workload.stalled", 0);
        AssertFresh(result, "workload.errors", 0);
    }

    [Fact]
    public void PreservesValidZeroes()
    {
        using var json = Parse($"[{Torrent(A, "pausedDL", 0, 0, 0, 0)}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);

        Assert.All(result.Metrics, metric => Assert.Equal(0, Assert.IsType<FreshReading>(metric.Reading).Value));
        Assert.True(result.Context.SelectedMatchesActive == false);
    }

    [Fact]
    public void MissingRequiredPeerFieldIsUnknownRatherThanZero()
    {
        using var json = Parse($"[{{\"hash\":\"{A}\",\"state\":\"downloading\",\"dlspeed\":1,\"upspeed\":0,\"num_seeds\":2}}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);

        AssertFresh(result, "workload.download", 1);
        Assert.Equal("FIELD_MISSING", Assert.IsType<UnavailableReading>(Get(result, "peers.total").Reading).ReasonCode);
    }

    [Fact]
    public void RejectsUnsafeAndOverflowingSpeedValues()
    {
        using var tooLarge = Parse($"[{Torrent(A, "downloading", 9007199254740992L, 0, 0, 0)}]");
        using var overflow = Parse($"[{Torrent(A, "downloading", 9007199254740991L, 0, 0, 0)},{Torrent(B, "uploading", 1, 0, 0, 0)}]");

        Assert.Equal("INVALID_METRIC_VALUE", Assert.IsType<ErrorReading>(Get(TorrentTelemetry.Aggregate(tooLarge.RootElement, [A], SampledAt), "workload.download").Reading).ReasonCode);
        Assert.Equal("METRIC_SUM_OVERFLOW", Assert.IsType<ErrorReading>(Get(TorrentTelemetry.Aggregate(overflow.RootElement, [A, B], SampledAt), "workload.download").Reading).ReasonCode);
    }

    [Fact]
    public void MissingSelectedTorrentMakesSelectedMetricsUnavailable()
    {
        using var json = Parse($"[{Torrent(A, "downloading", 1, 1, 1, 1)}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [B], SampledAt);

        Assert.All(result.Metrics, metric => Assert.IsType<UnavailableReading>(metric.Reading));
        Assert.Equal("SELECTED_TORRENT_MISSING", result.Context.ReasonCode);
    }

    [Fact]
    public void DuplicateOrInvalidSelectedHashesAreRejectedAsUnknown()
    {
        using var json = Parse($"[{Torrent(A, "downloading", 1, 1, 1, 1)}]");

        var duplicate = TorrentTelemetry.Aggregate(json.RootElement, [A, A.ToUpperInvariant()], SampledAt);
        var malformed = TorrentTelemetry.Aggregate(json.RootElement, ["not-a-hash"], SampledAt);

        Assert.Equal("INVALID_SELECTED_HASHES", Assert.IsType<ErrorReading>(Get(duplicate, "workload.download").Reading).ReasonCode);
        Assert.Equal("INVALID_SELECTED_HASHES", Assert.IsType<ErrorReading>(Get(malformed, "workload.download").Reading).ReasonCode);
    }

    [Fact]
    public void UnknownStateDoesNotPretendTheActiveSetIsComplete()
    {
        using var json = Parse($"[{Torrent(A, "downloading", 1, 1, 1, 1)},{Torrent(B, "futureNewState", 1, 1, 1, 1)}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);

        Assert.Null(result.Context.ActiveHashes);
        Assert.Null(result.Context.SelectedMatchesActive);
        Assert.Equal("UNKNOWN_TORRENT_STATE", result.Context.ReasonCode);
        AssertFresh(result, "workload.active", 1);
    }

    [Fact]
    public void SelectedProgressIsTypedAndMissingOrInvalidProgressStaysUnknown()
    {
        using var valid = Parse($"[{{\"hash\":\"{A}\",\"state\":\"downloading\",\"progress\":0.25,\"dlspeed\":1,\"upspeed\":0,\"num_seeds\":1,\"num_leechs\":0}}]");
        using var missing = Parse($"[{{\"hash\":\"{A}\",\"state\":\"downloading\",\"dlspeed\":1,\"upspeed\":0,\"num_seeds\":1,\"num_leechs\":0}}]");
        using var invalid = Parse($"[{{\"hash\":\"{A}\",\"state\":\"downloading\",\"progress\":1.1,\"dlspeed\":1,\"upspeed\":0,\"num_seeds\":1,\"num_leechs\":0}}]");

        var validTorrent = Assert.Single(TorrentTelemetry.Aggregate(valid.RootElement, [A], SampledAt).Context.SelectedTorrents!);
        var missingTorrent = Assert.Single(TorrentTelemetry.Aggregate(missing.RootElement, [A], SampledAt).Context.SelectedTorrents!);
        var invalidTorrent = Assert.Single(TorrentTelemetry.Aggregate(invalid.RootElement, [A], SampledAt).Context.SelectedTorrents!);

        Assert.Equal(0.25, validTorrent.Progress);
        Assert.Null(validTorrent.ProgressReasonCode);
        Assert.Null(missingTorrent.Progress);
        Assert.Equal("FIELD_MISSING", missingTorrent.ProgressReasonCode);
        Assert.Null(invalidTorrent.Progress);
        Assert.Equal("INVALID_PROGRESS_VALUE", invalidTorrent.ProgressReasonCode);
    }

    [Fact]
    public void StateCountersFailUnknownWhenSelectedStateIsUnrecognized()
    {
        using var json = Parse($"[{{\"hash\":\"{A}\",\"state\":\"futureNewState\",\"dlspeed\":1,\"upspeed\":1,\"num_seeds\":1,\"num_leechs\":1}}]");

        var result = TorrentTelemetry.Aggregate(json.RootElement, [A], SampledAt);

        Assert.Equal("UNKNOWN_TORRENT_STATE", Assert.IsType<ErrorReading>(Get(result, "workload.active").Reading).ReasonCode);
        AssertFresh(result, "workload.download", 1);
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);
    private static string Torrent(string hash, string state, long download, long upload, long seeds, long leechers) =>
        $"{{\"hash\":\"{hash}\",\"state\":\"{state}\",\"dlspeed\":{download},\"upspeed\":{upload},\"num_seeds\":{seeds},\"num_leechs\":{leechers}}}";
    private static LiveMetric Get(TorrentTelemetryEvidence result, string id) => result.Metrics.Single(metric => metric.Id == id);
    private static void AssertFresh(TorrentTelemetryEvidence result, string id, double expected)
    {
        var metric = Get(result, id);
        Assert.Equal(MetricScope.Workload, metric.Scope);
        var reading = Assert.IsType<FreshReading>(metric.Reading);
        Assert.Equal(expected, reading.Value);
        Assert.Equal(SampledAt, reading.SampledAtUtc);
    }
}
