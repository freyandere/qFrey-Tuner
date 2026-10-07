using System.Collections.ObjectModel;
using System.Text.Json;
using QFrey.Core.Contracts;

namespace QFrey.Core.Metrics;

public sealed record TorrentTelemetryContext(
    IReadOnlyList<string> SelectedHashes,
    IReadOnlyList<string>? ActiveHashes,
    bool? SelectedMatchesActive,
    string? ReasonCode)
{
    // Optional, bounded metadata for continuity validation. Kept out of the positional
    // constructor so existing snapshot consumers remain source-compatible.
    public IReadOnlyList<SelectedTorrentMetadata>? SelectedTorrents { get; init; }
}

public sealed record SelectedTorrentMetadata(
    string Hash,
    string? State,
    string? StateReasonCode,
    double? Progress,
    string? ProgressReasonCode);

public sealed record TorrentTelemetryEvidence(TorrentTelemetryContext Context, LiveMetric[] Metrics);

public static class TorrentTelemetry
{
    public const int MaximumTorrents = 50_000;
    public const int MaximumSelectedHashes = 5_000;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private readonly record struct SumResult(long? Value, string? ErrorCode = null);

    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "error", "missingFiles", "uploading", "pausedUP", "queuedUP", "stalledUP", "checkingUP", "forcedUP",
        "allocating", "downloading", "metaDL", "forcedMetaDL", "pausedDL", "queuedDL", "stalledDL", "checkingDL", "forcedDL",
        "checkingResumeData", "moving", "stoppedUP", "stoppedDL"
    };

    private static readonly HashSet<string> ActiveStates = new(StringComparer.Ordinal)
    { "downloading", "uploading", "forcedDL", "forcedUP", "stalledDL", "stalledUP" };

    public static TorrentTelemetryEvidence Aggregate(JsonElement torrentsInfo,
        IReadOnlyCollection<string> selectedHashes, DateTimeOffset sampledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(selectedHashes);
        if (sampledAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Sample time must be UTC.", nameof(sampledAtUtc));

        if (!TryNormalizeSelection(selectedHashes, out var selected))
            return Failure([], null, null, "INVALID_SELECTED_HASHES", error: true);
        if (torrentsInfo.ValueKind != JsonValueKind.Array)
            return Failure(selected, null, null, "INVALID_TORRENT_SNAPSHOT", error: true);
        if (torrentsInfo.GetArrayLength() > MaximumTorrents)
            return Failure(selected, null, null, "TORRENT_SNAPSHOT_TOO_LARGE", error: true);

        var byHash = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var active = new List<string>();
        var unknownState = false;
        foreach (var torrent in torrentsInfo.EnumerateArray())
        {
            if (torrent.ValueKind != JsonValueKind.Object
                || !torrent.TryGetProperty("hash", out var hashValue) || hashValue.ValueKind != JsonValueKind.String
                || !TryHash(hashValue.GetString(), out var hash)
                || !byHash.TryAdd(hash, torrent))
                return Failure(selected, null, null, "INVALID_TORRENT_SNAPSHOT", error: true);

            if (!torrent.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String
                || stateValue.GetString() is not { Length: > 0 and <= 32 } state || !KnownStates.Contains(state))
            {
                unknownState = true;
                continue;
            }
            if (ActiveStates.Contains(state)) active.Add(hash);
        }

        var activeHashes = unknownState ? null : Array.AsReadOnly(active.Order(StringComparer.Ordinal).ToArray());
        bool? selectedMatchesActive = activeHashes is null ? null
            : selected.Length == activeHashes.Count && selected.All(activeHashes.Contains);
        var contextReason = unknownState ? "UNKNOWN_TORRENT_STATE" : null;
        if (selected.Any(hash => !byHash.ContainsKey(hash)))
            return Failure(selected, activeHashes, selectedMatchesActive, "SELECTED_TORRENT_MISSING", error: false);

        var rows = selected.Select(hash => byHash[hash]).ToArray();
        var download = Sum(rows, "dlspeed");
        var upload = Sum(rows, "upspeed");
        var seeds = Sum(rows, "num_seeds");
        var leechers = Sum(rows, "num_leechs");
        var states = CountStates(rows);

        var metrics = new[]
        {
            Metric("workload.download", MetricUnit.BytesPerSecond, "dlspeed", Reading(download, sampledAtUtc)),
            Metric("workload.upload", MetricUnit.BytesPerSecond, "upspeed", Reading(upload, sampledAtUtc)),
            Metric("peers.seeds", MetricUnit.Count, "num_seeds", Reading(seeds, sampledAtUtc)),
            Metric("peers.total", MetricUnit.Count, "num_seeds+num_leechs", Add(seeds, leechers, sampledAtUtc)),
            Metric("workload.active", MetricUnit.Count, "state:active", CountReading(states.Active, sampledAtUtc)),
            Metric("workload.stalled", MetricUnit.Count, "state:stalledDL|stalledUP", CountReading(states.Stalled, sampledAtUtc)),
            Metric("workload.errors", MetricUnit.Count, "state:error|missingFiles", CountReading(states.Errors, sampledAtUtc))
        };
        var metadata = selected.Select(hash => ReadMetadata(hash, byHash[hash])).ToArray();
        return new(new TorrentTelemetryContext(ReadOnly(selected), activeHashes, selectedMatchesActive, contextReason)
        { SelectedTorrents = Array.AsReadOnly(metadata) }, metrics);
    }

    private static bool TryNormalizeSelection(IReadOnlyCollection<string> input, out string[] selected)
    {
        selected = [];
        if (input.Count is < 1 or > MaximumSelectedHashes) return false;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in input)
        {
            if (!TryHash(value, out var hash) || !set.Add(hash)) return false;
        }
        selected = set.Order(StringComparer.Ordinal).ToArray();
        return true;
    }

    private static bool TryHash(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: 40 } || !value.All(Uri.IsHexDigit)) return false;
        normalized = value.ToLowerInvariant();
        return true;
    }

    private static SumResult Sum(JsonElement[] rows, string property)
    {
        long sum = 0;
        foreach (var row in rows)
        {
            if (!row.TryGetProperty(property, out var value)) return new(null);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0 || number > MaximumSafeInteger)
                return new(null, "INVALID_METRIC_VALUE");
            if (sum > MaximumSafeInteger - number) return new(null, "METRIC_SUM_OVERFLOW");
            sum += number;
        }
        return new(sum);
    }

    private static (long? Active, long? Stalled, long? Errors) CountStates(JsonElement[] rows)
    {
        long active = 0, stalled = 0, errors = 0;
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("state", out var value) || value.ValueKind != JsonValueKind.String
                || value.GetString() is not { } state || !KnownStates.Contains(state)) return (null, null, null);
            if (ActiveStates.Contains(state)) active++;
            if (state is "stalledDL" or "stalledUP") stalled++;
            if (state is "error" or "missingFiles") errors++;
        }
        return (active, stalled, errors);
    }

    private static SelectedTorrentMetadata ReadMetadata(string hash, JsonElement row)
    {
        string? state = null;
        string? stateReason = null;
        if (!row.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String
            || stateValue.GetString() is not { Length: > 0 and <= 32 } candidate || !KnownStates.Contains(candidate))
            stateReason = "UNKNOWN_TORRENT_STATE";
        else state = candidate;

        double? progress = null;
        string? progressReason = null;
        if (!row.TryGetProperty("progress", out var progressValue)) progressReason = "FIELD_MISSING";
        else if (progressValue.ValueKind != JsonValueKind.Number || !progressValue.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1) progressReason = "INVALID_PROGRESS_VALUE";
        else progress = number;
        return new(hash, state, stateReason, progress, progressReason);
    }

    private static MetricReading Reading(SumResult result, DateTimeOffset sampledAtUtc) => result.Value is long value
        ? new FreshReading(value, sampledAtUtc)
        : result.ErrorCode is { } error ? new ErrorReading(error) : new UnavailableReading("FIELD_MISSING");

    private static MetricReading CountReading(long? count, DateTimeOffset sampledAtUtc) => count is long value
        ? new FreshReading(value, sampledAtUtc) : new ErrorReading("UNKNOWN_TORRENT_STATE");

    private static MetricReading Add(SumResult left, SumResult right, DateTimeOffset sampledAtUtc)
    {
        if (left.Value is null) return Reading(left, sampledAtUtc);
        if (right.Value is null) return Reading(right, sampledAtUtc);
        if (left.Value.Value > MaximumSafeInteger - right.Value.Value) return new ErrorReading("METRIC_SUM_OVERFLOW");
        return new FreshReading(left.Value.Value + right.Value.Value, sampledAtUtc);
    }

    private static LiveMetric Metric(string id, MetricUnit unit, string field, MetricReading reading) =>
        new(id, unit, MetricScope.Workload, "torrents/info:" + field, reading);

    private static TorrentTelemetryEvidence Failure(string[] selected, IReadOnlyList<string>? active, bool? selectedMatchesActive,
        string reason, bool error)
    {
        MetricReading Reading() => error ? new ErrorReading(reason) : new UnavailableReading(reason);
        var reading = Reading();
        var metrics = new[]
        {
            Metric("workload.download", MetricUnit.BytesPerSecond, "dlspeed", reading),
            Metric("workload.upload", MetricUnit.BytesPerSecond, "upspeed", reading),
            Metric("peers.seeds", MetricUnit.Count, "num_seeds", reading),
            Metric("peers.total", MetricUnit.Count, "num_seeds+num_leechs", reading),
            Metric("workload.active", MetricUnit.Count, "state:active", reading),
            Metric("workload.stalled", MetricUnit.Count, "state:stalledDL|stalledUP", reading),
            Metric("workload.errors", MetricUnit.Count, "state:error|missingFiles", reading)
        };
        return new(new TorrentTelemetryContext(ReadOnly(selected), active, selectedMatchesActive, reason), metrics);
    }

    private static IReadOnlyList<string> ReadOnly(string[] values) => Array.AsReadOnly(values.ToArray());
}
