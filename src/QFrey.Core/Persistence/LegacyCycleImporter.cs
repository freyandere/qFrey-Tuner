using System.Globalization;
using System.Text.Json;
using QFrey.Core.Contracts;

namespace QFrey.Core.Persistence;

public sealed record LegacySample(double? ElapsedSeconds, double? DownloadBytesPerSecond, double? UploadBytesPerSecond, int? DhtNodes);
public sealed record LegacyMeasurement(double? MeanDownloadMiBPerSecond, double? MeanUploadMiBPerSecond,
    double? DownloadStdDevBytesPerSecond, double? UploadStdDevBytesPerSecond, LegacySample[]? Samples,
    IReadOnlyDictionary<string, PreferenceValue>? Preferences);
public sealed record LegacyCycleImport(Guid CycleId, DateTimeOffset CreatedUtc, TargetIdentity Target,
    IReadOnlyDictionary<string, PreferenceValue> Original, IReadOnlyDictionary<string, PreferenceValue>? IntendedApplied,
    IReadOnlyDictionary<string, PreferenceValue>? ObservedReadback,
    IReadOnlyDictionary<string, PreferenceValue>? MeasurementPreferences, LegacyMeasurement? Baseline,
    LegacyMeasurement? After, bool? LegacyVerifiedFlag, string[] Limitations);

public static class LegacyCycleImporter
{
    public const int MaxImportBytes = 4 * 1024 * 1024;

    public static LegacyCycleImport Import(ReadOnlyMemory<byte> utf8Json, Guid cycleId, TargetIdentity? expectedTarget = null)
    {
        if (cycleId == Guid.Empty || utf8Json.Length is 0 or > MaxImportBytes) throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = 16 });
            Protocol.RejectDuplicateProperties(doc.RootElement);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasSchemaVersion(root)) throw Invalid();
            var endpoint = String(root, "host", 2048);
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw Invalid();
            var target = new TargetIdentity(endpoint, String(root, "qBittorrent", 64), String(root, "api", 64), String(root, "libtorrent", 64));
            if (expectedTarget is not null && target != expectedTarget) throw Invalid();
            var created = ReadTimestamp(root, "created_or_updated_utc");
            var original = ReadPreferences(root, "original") ?? new Dictionary<string, PreferenceValue>();
            var intended = ReadPreferences(root, "applied");
            var after = ReadMeasurement(root, "optimized");
            // Python persisted a preference snapshot before after-sampling, not an apply-time readback.
            return new LegacyCycleImport(cycleId, created, target, original, intended, null, after?.Preferences,
                ReadMeasurement(root, "baseline"), after, ReadBoolean(root, "verified"),
                ["LEGACY_PLAN_INPUTS_UNMAPPABLE", "LEGACY_APPLY_READBACK_NOT_RECORDED", "LEGACY_WORKLOAD_AND_FAILURE_FIELDS_DISCARDED"]);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static LegacyMeasurement? ReadMeasurement(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        return new LegacyMeasurement(OptionalNumber(value, "mean_download_mib_s"), OptionalNumber(value, "mean_upload_mib_s"),
            OptionalNumber(value, "download_stddev"), OptionalNumber(value, "upload_stddev"), ReadSamples(value),
            ReadPreferences(value, "preferences"));
    }

    private static LegacySample[]? ReadSamples(JsonElement measurement)
    {
        if (!measurement.TryGetProperty("samples", out var samples)) return null;
        if (samples.ValueKind != JsonValueKind.Array || samples.GetArrayLength() > 100_000) throw Invalid();
        return samples.EnumerateArray().Select(sample =>
        {
            if (sample.ValueKind != JsonValueKind.Object) throw Invalid();
            return new LegacySample(OptionalNumber(sample, "elapsed"), OptionalNumber(sample, "download"), OptionalNumber(sample, "upload"), OptionalInt(sample, "dht_nodes"));
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, PreferenceValue>? ReadPreferences(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > 64) throw Invalid();
        var values = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        {
            if (!CycleStore.IsSafePreferenceKey(item.Name)) continue;
            PreferenceValue preference = item.Value.ValueKind switch
            {
                JsonValueKind.Number when item.Value.TryGetInt32(out var number) => new IntegerPreference(number),
                JsonValueKind.True => new BooleanPreference(true),
                JsonValueKind.False => new BooleanPreference(false),
                JsonValueKind.String when item.Name == "current_network_interface" && item.Value.GetString()!.Length <= 256
                    => new StringPreference(item.Value.GetString()!),
                _ => throw Invalid()
            };
            if (!CycleStore.IsSafePreferenceValue(item.Name, preference)) throw Invalid();
            values[item.Name] = preference;
        }
        return values;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root, string name)
    {
        var raw = String(root, name, 64);
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw Invalid();
        return value;
    }

    private static bool HasSchemaVersion(JsonElement root) => root.TryGetProperty("schemaVersion", out _);
    private static string String(JsonElement root, string name, int max) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.Length <= max ? text : throw Invalid();
    private static bool? ReadBoolean(JsonElement root, string name) => !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
        ? null : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid();
    private static double? OptionalNumber(JsonElement value, string name) => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null
        ? null : item.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 ? number : throw Invalid();
    private static int? OptionalInt(JsonElement value, string name) => !value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null
        ? null : item.TryGetInt32(out var number) && number >= 0 ? number : throw Invalid();
    private static InvalidDataException Invalid() => new("Legacy cycle is invalid or incompatible.");
}
