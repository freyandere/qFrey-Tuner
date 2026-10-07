namespace QFrey.Core.Metrics;

// Captures only safe, read-only facts needed to decide whether two measurements
// describe the same controlled workload and preferences.
public sealed record MeasurementContinuitySnapshot(
    string? SafePreferencesFingerprint,
    bool? SchedulerEnabled,
    bool? AlternativeSpeedLimitsEnabled,
    TorrentTelemetryContext? TorrentContext);

public static class MeasurementContinuity
{
    private static readonly HashSet<string> DownloadingStates = new(StringComparer.Ordinal)
    { "downloading", "forcedDL" };

    public static IReadOnlyList<string> Validate(MeasurementContinuitySnapshot before, MeasurementContinuitySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(current);
        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(before.SafePreferencesFingerprint)
            || string.IsNullOrWhiteSpace(current.SafePreferencesFingerprint))
            reasons.Add("PREFERENCES_FINGERPRINT_UNKNOWN");
        else if (!StringComparer.Ordinal.Equals(before.SafePreferencesFingerprint, current.SafePreferencesFingerprint))
            reasons.Add("RELEVANT_PREFERENCES_CHANGED");

        CheckDisabled(before.SchedulerEnabled, current.SchedulerEnabled, "SCHEDULER", reasons);
        CheckDisabled(before.AlternativeSpeedLimitsEnabled, current.AlternativeSpeedLimitsEnabled, "ALTERNATIVE_LIMITS", reasons);
        CheckTorrentContinuity(before.TorrentContext, current.TorrentContext, reasons);
        return Array.AsReadOnly(reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void CheckDisabled(bool? before, bool? current, string name, List<string> reasons)
    {
        if (before is null || current is null) reasons.Add(name + "_STATE_UNKNOWN");
        else if (before.Value || current.Value) reasons.Add(name + "_ENABLED");
    }

    private static void CheckTorrentContinuity(TorrentTelemetryContext? before, TorrentTelemetryContext? current,
        List<string> reasons)
    {
        if (before is null || current is null)
        {
            reasons.Add("TORRENT_CONTEXT_UNKNOWN");
            return;
        }
        if (before.ReasonCode is not null || current.ReasonCode is not null)
        {
            if (before.ReasonCode == "SELECTED_TORRENT_MISSING" || current.ReasonCode == "SELECTED_TORRENT_MISSING")
                reasons.Add("SELECTED_TORRENT_MISSING");
            else reasons.Add("TORRENT_CONTEXT_INVALID");
        }

        if (before.ActiveHashes is null || current.ActiveHashes is null)
            reasons.Add("ACTIVE_HASH_SET_UNKNOWN");
        else if (!SameSet(before.ActiveHashes, current.ActiveHashes))
            reasons.Add("ACTIVE_HASH_SET_CHANGED");

        if (before.SelectedHashes is null || before.SelectedHashes.Count == 0)
        {
            reasons.Add("SELECTED_HASH_SET_UNKNOWN");
            return;
        }
        if (current.SelectedHashes is null || current.SelectedHashes.Count == 0)
        {
            reasons.Add("SELECTED_HASH_SET_UNKNOWN");
            if (before.SelectedHashes.Any(hash => current.SelectedHashes?.Contains(hash, StringComparer.OrdinalIgnoreCase) != true))
                reasons.Add("SELECTED_TORRENT_MISSING");
            return;
        }
        if (!SameSet(before.SelectedHashes, current.SelectedHashes)) reasons.Add("SELECTED_HASH_SET_CHANGED");

        var beforeTorrents = Index(before.SelectedTorrents);
        var currentTorrents = Index(current.SelectedTorrents);
        if (beforeTorrents is null || currentTorrents is null)
        {
            reasons.Add("SELECTED_TORRENT_METADATA_UNKNOWN");
            return;
        }

        foreach (var hash in before.SelectedHashes)
        {
            if (!beforeTorrents.TryGetValue(hash, out var original) || !currentTorrents.TryGetValue(hash, out var now))
            {
                reasons.Add("SELECTED_TORRENT_MISSING");
                continue;
            }
            if (original.State is null || now.State is null)
            {
                reasons.Add("SELECTED_TORRENT_STATE_UNKNOWN");
                continue;
            }
            if (!DownloadingStates.Contains(original.State)) continue;

            if (!KnownProgress(original.Progress) || !KnownProgress(now.Progress))
            {
                reasons.Add("SELECTED_DOWNLOAD_PROGRESS_UNKNOWN");
                continue;
            }
            if (original.Progress is >= 1 || now.Progress is >= 1 || !DownloadingStates.Contains(now.State))
                reasons.Add("ORIGINAL_DOWNLOAD_COMPLETED_OR_STOPPED");
        }
    }

    private static Dictionary<string, SelectedTorrentMetadata>? Index(IReadOnlyList<SelectedTorrentMetadata>? rows)
    {
        if (rows is null) return null;
        var map = new Dictionary<string, SelectedTorrentMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row is null || row.Hash is not { Length: 40 } || !row.Hash.All(Uri.IsHexDigit)
                || !map.TryAdd(row.Hash, row)) return null;
        }
        return map;
    }

    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        if (left.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))
            || right.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))) return false;
        var set = new HashSet<string>(left, StringComparer.OrdinalIgnoreCase);
        return set.Count == left.Count && right.All(set.Contains);
    }

    private static bool KnownProgress(double? progress) => progress is double value && double.IsFinite(value) && value is >= 0 and <= 1;
}
