using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QFrey.Core.Contracts;

public enum Locale { [JsonStringEnumMemberName("ru-RU")] RuRu, [JsonStringEnumMemberName("en-US")] EnUs }
public enum ThemePreference { System, Dark, Light }
public enum ConnectionState { Disconnected, Connecting, Validated, Degraded, Incompatible }
public enum ExperimentPhase { Draft, BaselineReady, PlanReady, Applying, AppliedVerified, AfterReady, Completed, RecoveryRequired, RolledBack }
public enum MeasurementStatus { Pending, WarmingUp, Sampling, Valid, Invalid, Cancelled }
public enum ApplyStatus { NotApplied, Pending, Verified, Unverified, Reverted }
public enum ResultCategory { Throughput, Stability, Ramp, Connectivity, Resources, Configuration, Limitations }
public enum MetricUnit { BytesPerSecond, Bytes, Milliseconds, Count, Percent }
public enum MetricScope { Session, Workload, LocalProcess, LocalHost, Endpoint }
public enum Evidence { Observed, Derived, Heuristic, UserProvided }
public enum Verdict { ObservedImprovement, ObservedRegression, Inconclusive, NotComparable, NotMeasured }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(FreshReading), "fresh")]
[JsonDerivedType(typeof(StaleReading), "stale")]
[JsonDerivedType(typeof(UnavailableReading), "unavailable")]
[JsonDerivedType(typeof(ErrorReading), "error")]
public abstract record MetricReading;
public sealed record FreshReading(double Value, DateTimeOffset SampledAtUtc) : MetricReading;
public sealed record StaleReading(double LastValue, DateTimeOffset SampledAtUtc, string ReasonCode) : MetricReading;
public sealed record UnavailableReading(string ReasonCode) : MetricReading;
public sealed record ErrorReading(string ReasonCode) : MetricReading;
public sealed record LiveMetric(string Id, MetricUnit Unit, MetricScope Scope, string Source, MetricReading Reading);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "quality")]
[JsonDerivedType(typeof(ValidHistoricalValue), "valid")]
[JsonDerivedType(typeof(InvalidHistoricalValue), "invalid")]
[JsonDerivedType(typeof(NotMeasuredValue), "notMeasured")]
public abstract record HistoricalValue;
public sealed record ValidHistoricalValue(double Value, DateTimeOffset MeasuredAtUtc) : HistoricalValue;
public sealed record InvalidHistoricalValue(string[] ReasonCodes) : HistoricalValue;
public sealed record NotMeasuredValue(string[] ReasonCodes) : HistoricalValue;
public sealed record ComparisonValue(string MetricId, ResultCategory Category, MetricUnit Unit,
    HistoricalValue Before, HistoricalValue After, double? AbsoluteDelta, double? RelativeDeltaPercent,
    Verdict Verdict, string[] ReasonCodes, Evidence Evidence, MetricScope Scope);

public sealed record UiPreferences(string Locale, ThemePreference Theme);
public sealed record AvailableAction(string Id, bool Enabled, string? ReasonCode, string MessageKey)
{
    public IReadOnlyDictionary<string, MessageParameter> Parameters { get; init; } = new Dictionary<string, MessageParameter>();
}
public sealed record ActiveOperation(Guid Id, OperationKind Kind, string Stage, double? Progress, bool Cancellable);
public sealed record TargetSummary(Guid SessionId, string Endpoint, string QbittorrentVersion,
    string ApiVersion, string LibtorrentVersion, bool IsLocal);
public sealed record AppSnapshot(int ProtocolVersion, int SchemaVersion, string AppVersion, long Revision,
    UiPreferences Preferences, ConnectionState Connection, TargetSummary? Target, ExperimentPhase Phase,
    ApplyStatus ApplyStatus, ActiveOperation? ActiveOperation, LiveMetric[] Metrics, AvailableAction[] AvailableActions)
{
    public ExperimentSummary? Experiment { get; init; }
    public ConfirmationSummary[] Confirmations { get; init; } = [];
    public ObservedHardware? Hardware { get; init; }
    public NetworkTestResult? NetworkTest { get; init; }
    public TargetInterface[] Interfaces { get; init; } = [];
    public WorkloadCatalogueSummary[] WorkloadCatalogue { get; init; } = [];
    public RestorePreview? Restore { get; init; }
}
public sealed record WorkloadCatalogueSummary(string Id, string Name, string TotalBytesDecimal, string MetadataSource);
public sealed record RestorePreview(string SelectionToken, string DisplayName, Guid SourceCycleId,
    QFrey.Core.Persistence.RestoreReview Review);
public sealed record AppError(string Code, string MessageKey, ErrorSeverity Severity, RetryPolicy Retry, string[] RecoveryActionIds, Guid CorrelationId)
{
    public IReadOnlyDictionary<string, MessageParameter> Parameters { get; init; } = new Dictionary<string, MessageParameter>();
}
public sealed record Reply(Guid RequestId, bool Ok, long Revision, AppSnapshot? Data, AppError? Error);
public sealed record CommandReply<T>(Guid RequestId, bool Ok, long Revision, T? Data, AppError? Error);
public sealed record InitializePayload(int ProtocolVersion);
public sealed record SetUiPreferencesPayload(string Locale, ThemePreference Theme);
public sealed record CommandEnvelope(int ProtocolVersion, Guid RequestId, string Command,
    Guid? TargetSessionId, long? ExpectedRevision, JsonElement Payload)
{ public override string ToString() => $"CommandEnvelope {{ Command = {Command}, RequestId = {RequestId}, Payload = [redacted] }}"; }

public static class Protocol
{
    public const int Version = 1;
    public const int SchemaVersion = 2;
    public const int MaxCommandBytes = 64 * 1024;
    public const int MaxSnapshotBytes = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcTimestampConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    // This validates the contract only. Hosts must separately reject commands without guarded handlers.
    public static CommandEnvelope Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxCommandBytes) throw new JsonException("COMMAND_TOO_LARGE");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var root = document.RootElement;
        foreach (var name in new[] { "protocolVersion", "requestId", "command", "targetSessionId", "expectedRevision", "payload" })
            if (!root.TryGetProperty(name, out _)) throw new JsonException("INVALID_COMMAND");
        var command = JsonSerializer.Deserialize<CommandEnvelope>(json, Json) ?? throw new JsonException("INVALID_COMMAND");
        if (command.ProtocolVersion != Version || command.RequestId == Guid.Empty || command.ExpectedRevision is < 0 or > 9007199254740991)
            throw new JsonException("INVALID_COMMAND");
        CommandPayloads.Read(command);
        return command;
    }

    public static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("DUPLICATE_PROPERTY");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    public static AppSnapshot ParseSnapshot(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes) throw new JsonException("SNAPSHOT_TOO_LARGE");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        ValidateWireNumbers(document.RootElement);
        // Snapshots can add optional fields within protocol v1; commands remain strict.
        var readOptions = new JsonSerializerOptions(Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
        var snapshot = JsonSerializer.Deserialize<AppSnapshot>(json, readOptions) ?? throw new JsonException("INVALID_SNAPSHOT");
        if (snapshot.ProtocolVersion != Version || snapshot.SchemaVersion != SchemaVersion || !CommandPayloads.SafeRevision(snapshot.Revision)
            || snapshot.Preferences.Locale is not ("ru-RU" or "en-US") || snapshot.Metrics.Length > 600 || snapshot.AvailableActions.Length > 128
            || snapshot.Confirmations.Length > 32 || snapshot.Metrics.Any(m => m is null) || snapshot.AvailableActions.Any(a => a is null)
            || snapshot.Confirmations.Any(c => c is null) || snapshot.Interfaces.Length > 128
            || snapshot.Interfaces.Any(i => i is null || string.IsNullOrWhiteSpace(i.Id) || string.IsNullOrWhiteSpace(i.Name)) || snapshot.Target?.SessionId == Guid.Empty
            || snapshot.ActiveOperation is { } operation && (operation.Id == Guid.Empty || operation.Progress is < 0 or > 100))
            throw new JsonException("INVALID_SNAPSHOT");
        if (snapshot.WorkloadCatalogue is null || snapshot.WorkloadCatalogue.Length > 16
            || snapshot.WorkloadCatalogue.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 64
                || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256
                || !ulong.TryParse(item.TotalBytesDecimal, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size == 0
                || size.ToString(System.Globalization.CultureInfo.InvariantCulture) != item.TotalBytesDecimal
                || !Uri.TryCreate(item.MetadataSource, UriKind.Absolute, out var source) || source.Scheme != "https"
                || !string.IsNullOrEmpty(source.UserInfo))) throw new JsonException("INVALID_WORKLOAD_CATALOGUE");
        if (snapshot.NetworkTest is { } network && (network.DownloadBytesPerSecond < 0 || network.UploadBytesPerSecond < 0))
            throw new JsonException("INVALID_NETWORK_TEST");
        if (snapshot.Restore is { } restore && (string.IsNullOrWhiteSpace(restore.SelectionToken) || restore.SelectionToken.Length > 128
            || restore.DisplayName is null || restore.DisplayName.Length > 512 || restore.SourceCycleId == Guid.Empty
            || restore.Review is null || restore.Review.Fingerprint is not { Length: 64 } fingerprint || !fingerprint.All(Uri.IsHexDigit)
            || restore.Review.BlockReasonCodes is null || restore.Review.BlockReasonCodes.Count > 32
            || restore.Review.BlockReasonCodes.Any(code => string.IsNullOrWhiteSpace(code) || code.Length > 128)
            || restore.Review.Differences is null || restore.Review.Differences.Count > 64
            || restore.Review.Differences.Any(diff => diff is null || !Enum.IsDefined(diff.Disposition)
                || !QFrey.Core.Persistence.CycleStore.IsSafePreferenceKey(diff.Key)
                || new[] { diff.Original, diff.Intended, diff.Current }.Any(value => value is not null
                    && !QFrey.Core.Persistence.CycleStore.IsSafePreferenceValue(diff.Key, value))))) throw new JsonException("INVALID_RESTORE_REVIEW");
        foreach (var metric in snapshot.Metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Id) || string.IsNullOrWhiteSpace(metric.Source)) throw new JsonException("INVALID_METRIC");
            if (metric.Reading is UnavailableReading { ReasonCode.Length: 0 } or ErrorReading { ReasonCode.Length: 0 }
                or StaleReading { ReasonCode.Length: 0 }) throw new JsonException("INVALID_METRIC");
            var time = metric.Reading switch { FreshReading r => r.SampledAtUtc, StaleReading r => r.SampledAtUtc, _ => (DateTimeOffset?)null };
            if (time?.Offset != TimeSpan.Zero && time is not null) throw new JsonException("NON_UTC_TIMESTAMP");
        }
        if (snapshot.Experiment is { } experiment)
        {
            if (experiment.RunInputs is { } inputs) CommandPayloads.ValidateInputs(inputs);
            if (experiment.Plan is { } plan && (!CommandPayloads.SafeRevision(plan.Revision) || plan.Id == Guid.Empty || plan.TargetSessionId == Guid.Empty
                || plan.Recommendations.Length > 128 || plan.Omissions.Length > 128 || plan.Recommendations.Any(r => r is null)
                || plan.Omissions.Any(o => o is null) || plan.Original.Values.Any(v => v is null) || plan.Proposed.Values.Any(v => v is null))) throw new JsonException("INVALID_PLAN");
            if (experiment.Results.Length > 128 || experiment.Results.Any(c => c is null)) throw new JsonException("INVALID_RESULT");
            foreach (var card in experiment.Results)
            {
                var matches = card.Kind switch
                {
                    ResultKind.MetricComparison => card.Content is ComparisonContent,
                    ResultKind.Observation => card.Content is ObservationContent,
                    ResultKind.AppliedChange => card.Content is ChangeContent,
                    ResultKind.Limitation => card.Content is LimitationContent,
                    ResultKind.Recovery => card.Content is RecoveryContent,
                    _ => false
                };
                if (!matches || card.CycleId == Guid.Empty) throw new JsonException("INVALID_RESULT");
            }
        }
        return snapshot;
    }

    private static void ValidateWireNumbers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var value) || !double.IsFinite(value) || Math.Abs(value) > 9007199254740991)
                    throw new JsonException("UNSAFE_NUMBER");
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) ValidateWireNumbers(property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray()) ValidateWireNumbers(child);
                break;
        }
    }
}

public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (text is null || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            || !reader.TryGetDateTimeOffset(out var value) || value.Offset != TimeSpan.Zero)
            throw new JsonException("NON_UTC_TIMESTAMP");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUniversalTime());
}
