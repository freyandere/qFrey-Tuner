using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;

public sealed class MeasurementContinuityTests
{
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void EqualSafePreferencesAndUnchangedDownloadingWorkloadRemainComparable()
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var current = Snapshot(Context([A], [A], Torrent(A, "downloading", .75)));

        Assert.Empty(MeasurementContinuity.Validate(before, current));
    }

    [Fact]
    public void RelevantPreferenceDriftInvalidatesContinuity()
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var changedFingerprint = Snapshot(Context([A], [A], Torrent(A, "downloading", .25))) with
        { SafePreferencesFingerprint = "safe-sha256-2" };

        Assert.Contains("RELEVANT_PREFERENCES_CHANGED", MeasurementContinuity.Validate(before, changedFingerprint));
    }

    [Fact]
    public void UnknownOrEnabledSchedulerFlagsInvalidateContinuity()
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var unknown = before with { SchedulerEnabled = null };
        var enabled = before with { AlternativeSpeedLimitsEnabled = true };

        Assert.Contains("SCHEDULER_STATE_UNKNOWN", MeasurementContinuity.Validate(before, unknown));
        Assert.Contains("ALTERNATIVE_LIMITS_ENABLED", MeasurementContinuity.Validate(before, enabled));
    }

    [Fact]
    public void ChangedActiveSetInvalidatesContinuity()
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var current = Snapshot(Context([A], [A, B], Torrent(A, "downloading", .25)));

        Assert.Contains("ACTIVE_HASH_SET_CHANGED", MeasurementContinuity.Validate(before, current));
    }

    [Theory]
    [InlineData("uploading", 1.0)]
    [InlineData("stoppedDL", .5)]
    [InlineData("pausedDL", .5)]
    public void OriginalDownloadCompletedOrStoppedInvalidatesContinuity(string currentState, double progress)
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var current = Snapshot(Context([A], [A], Torrent(A, currentState, progress)));

        Assert.Contains("ORIGINAL_DOWNLOAD_COMPLETED_OR_STOPPED", MeasurementContinuity.Validate(before, current));
    }

    [Fact]
    public void MissingSelectedHashOrUnknownProgressIsInvalidRatherThanComparable()
    {
        var before = Snapshot(Context([A], [A], Torrent(A, "downloading", .25)));
        var missing = Snapshot(Context([A], [A], Torrent(A, "downloading", .25))) with
        { TorrentContext = Context([], [A]) };
        var unknownProgress = Snapshot(Context([A], [A], Torrent(A, "downloading", .25))) with
        { TorrentContext = Context([A], [A], Torrent(A, "downloading", null, "FIELD_MISSING")) };

        Assert.Contains("SELECTED_TORRENT_MISSING", MeasurementContinuity.Validate(before, missing));
        Assert.Contains("SELECTED_DOWNLOAD_PROGRESS_UNKNOWN", MeasurementContinuity.Validate(before, unknownProgress));
    }

    private static MeasurementContinuitySnapshot Snapshot(TorrentTelemetryContext context) =>
        new("safe-sha256-1", false, false, context);

    private static TorrentTelemetryContext Context(string[] selected, string[]? active, params SelectedTorrentMetadata[] torrents) =>
        new(Array.AsReadOnly(selected), active is null ? null : Array.AsReadOnly(active), true, null)
        { SelectedTorrents = Array.AsReadOnly(torrents) };

    private static SelectedTorrentMetadata Torrent(string hash, string state, double? progress, string? progressReason = null) =>
        new(hash, state, null, progress, progressReason);
}
