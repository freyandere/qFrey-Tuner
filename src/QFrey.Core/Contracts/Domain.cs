using System.Text.Json;
using System.Text.Json.Serialization;

namespace QFrey.Core.Contracts;

public enum ConnectionType { Fiber, CableDsl, Mobile4G }
public enum StorageType { Hdd, SsdSata, Nvme }
public enum EnvironmentProfile { System, Portable, Truenas, Nas, Docker, Seedbox }
public enum TrackerType { Public, Private }
public enum UserRole { Leecher, Seeder, Uploader }
public enum InputSource { Manual, Measured, LocalDetection }
public enum DataStatus { Fresh, Stale, Unavailable, Error }
public enum RecommendationType { Number, Bool, Enum, Interface }
public enum SupportStatus { Supported, MissingOptional, MissingRequired, Incompatible }
public enum ResultKind { MetricComparison, Observation, AppliedChange, Limitation, Recovery }
public enum Severity { Neutral, Info, Warning, Error }
public enum ErrorSeverity { Info, Warning, Error }
public enum RetryPolicy { Safe, ReconcileFirst, None }
public enum ResultContext { Historical, CurrentExperiment }
public enum MeasurementKind { Baseline, After }
public enum WorkloadKind { Existing, Owned }
public enum ReportFormat { Json, Html }
public enum OperationKind { NetworkTest, HardwareDetection, Measurement, PrepareWorkload, StopWorkload, DeleteWorkload, Apply, Rollback, Restore, StartTarget, StopTarget, RestartTarget }

// qBittorrent preference values are int/bool/string. Floats and boolean-as-int are never interchangeable.
[JsonConverter(typeof(PreferenceValueConverter))]
public abstract record PreferenceValue;
public sealed record IntegerPreference(int Value) : PreferenceValue;
public sealed record BooleanPreference(bool Value) : PreferenceValue;
public sealed record StringPreference(string Value) : PreferenceValue;
public sealed class PreferenceValueConverter : JsonConverter<PreferenceValue>
{
    public override PreferenceValue Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt32(out var value) => new IntegerPreference(value),
        JsonTokenType.True => new BooleanPreference(true),
        JsonTokenType.False => new BooleanPreference(false),
        JsonTokenType.String => new StringPreference(reader.GetString()!),
        _ => throw new JsonException("INVALID_PREFERENCE_TYPE")
    };
    public override void Write(Utf8JsonWriter writer, PreferenceValue value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case IntegerPreference integer: writer.WriteNumberValue(integer.Value); break;
            case BooleanPreference boolean: writer.WriteBooleanValue(boolean.Value); break;
            case StringPreference text: writer.WriteStringValue(text.Value); break;
            default: throw new JsonException("INVALID_PREFERENCE_TYPE");
        }
    }
}

public sealed record NetworkInputs(double DownloadMbps, double UploadMbps, ConnectionType ConnectionType,
    bool UseVpn, string VpnInterface, bool IspThrottling, InputSource DownloadSource, InputSource UploadSource);
public sealed record HardwareInputs(StorageType StorageType, int RamGiB, int CpuCores, bool IsHybridCpu,
    int PerformanceCores, InputSource Source);
public sealed record UsageInputs(TrackerType TrackerType, UserRole UserRole, EnvironmentProfile Environment);
public sealed record DraftInputs(NetworkInputs Network, HardwareInputs Hardware, UsageInputs Usage, int? ProposedPort);
[JsonConverter(typeof(MessageParameterConverter))]
public abstract record MessageParameter;
public sealed record NumberParameter(double Value) : MessageParameter;
public sealed record TextParameter(string Value) : MessageParameter;
public sealed class MessageParameterConverter : JsonConverter<MessageParameter>
{
    public override bool HandleNull => true;
    public override MessageParameter Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetDouble(out var value) && double.IsFinite(value) && Math.Abs(value) <= 9007199254740991 => new NumberParameter(value),
        JsonTokenType.String => new TextParameter(reader.GetString()!),
        _ => throw new JsonException("INVALID_MESSAGE_PARAMETER")
    };
    public override void Write(Utf8JsonWriter writer, MessageParameter value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case NumberParameter number when double.IsFinite(number.Value) && Math.Abs(number.Value) <= 9007199254740991: writer.WriteNumberValue(number.Value); break;
            case TextParameter text: writer.WriteStringValue(text.Value); break;
            default: throw new JsonException("INVALID_MESSAGE_PARAMETER");
        }
    }
}
public sealed record LocalizedMessage(string Key, IReadOnlyDictionary<string, MessageParameter> Parameters);
public sealed record AllowedRange(double Minimum, double Maximum, double Step);
public sealed record AllowedValue(PreferenceValue Value, string LabelKey);
public sealed record Recommendation(string Id, string GroupId, string ApiKey, ResultCategory Category,
    string TitleKey, LocalizedMessage Reason, Evidence Evidence, PreferenceValue? CurrentValue,
    PreferenceValue ProposedValue, string Unit, RecommendationType ValueType, bool Editable,
    AllowedRange? AllowedRange, AllowedValue[] AllowedValues, SupportStatus SupportStatus, bool Selected,
    string[] CautionCodes);
public sealed record PlanSelection(string GroupId, bool Selected, IReadOnlyDictionary<string, PreferenceValue> Overrides);
public sealed record PlanOmission(string ApiKey, bool Required, string ReasonCode);
public sealed record Plan(Guid Id, Guid TargetSessionId, long Revision, DateTimeOffset CreatedUtc,
    string InputsFingerprint, string BaselineFingerprint, bool PreviewOnly, bool Applicable, bool Approved,
    string[] BlockReasonCodes, Recommendation[] Recommendations, PlanOmission[] Omissions,
    IReadOnlyDictionary<string, PreferenceValue> Original, IReadOnlyDictionary<string, PreferenceValue> Proposed);
public sealed record WorkloadReference(Guid Id, WorkloadKind Kind, string[] Hashes);
public sealed record WorkloadSummary(WorkloadReference Reference, string Name, string TotalBytesDecimal,
    string ServerSavePath, string? CatalogueId, bool OwnershipVerified, string[] ReasonCodes);
public sealed record MeasurementSample(double ElapsedMs, MeasurementStatus Status, string[] ReasonCodes,
    MetricReading SessionDownload, MetricReading SessionUpload, MetricReading WorkloadDownload,
    MetricReading WorkloadUpload, MetricReading Seeds, MetricReading Peers, int? Active, int? Stalled, int? Errors);
public sealed record MeasurementSummary(Guid Id, MeasurementKind Kind, MeasurementStatus Status,
    DateTimeOffset StartedUtc, string AnalysisVersion, MetricScope Scope, string[] ReasonCodes,
    int SampleCount, double DurationMs, HistoricalValue MeanDownload, HistoricalValue MedianDownload,
    HistoricalValue StandardDeviation, HistoricalValue ZeroSamplePercent);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ComparisonContent), "comparison")]
[JsonDerivedType(typeof(ObservationContent), "observation")]
[JsonDerivedType(typeof(ChangeContent), "change")]
[JsonDerivedType(typeof(LimitationContent), "limitation")]
[JsonDerivedType(typeof(RecoveryContent), "recovery")]
public abstract record ResultContent;
public sealed record ComparisonContent(ComparisonValue Comparison) : ResultContent;
public sealed record ObservationContent(string MetricId, HistoricalValue Value, MetricUnit Unit, MetricScope Scope) : ResultContent;
public sealed record ChangeContent(string ApiKey, PreferenceValue Before, PreferenceValue Intended,
    PreferenceValue? Observed, ApplyStatus Status) : ResultContent;
public sealed record LimitationContent(string[] ReasonCodes) : ResultContent;
public sealed record RecoveryContent(string[] ConflictKeys, string[] ActionIds, bool BackupAvailable) : ResultContent;
public sealed record ResultCard(string Id, ResultCategory Category, ResultKind Kind, string TitleKey,
    LocalizedMessage Explanation, Evidence Evidence, Severity Severity, ResultContext Context,
    Guid CycleId, DateTimeOffset? MeasuredAtUtc, ResultContent Content);
public sealed record ExperimentSummary(Guid? CycleId, DraftInputs? RunInputs, WorkloadSummary? Workload,
    MeasurementSummary? Baseline, MeasurementSummary? After, Plan? Plan, ResultCard[] Results);
public sealed record CycleSummary(Guid CycleId, DateTimeOffset CreatedUtc, string Endpoint,
    string QbittorrentVersion, ExperimentPhase Phase, ApplyStatus ApplyStatus, int ChangeCount);
public sealed record HistoryPage(CycleSummary[] Items, string? NextCursor);
public sealed record TargetIdentity(string Endpoint, string QbittorrentVersion, string ApiVersion, string LibtorrentVersion);
public sealed record TorrentRunContext(string Hash, string State, double? Progress);
public sealed record MeasurementContext(IReadOnlyDictionary<string, PreferenceValue> Preferences,
    string PreferencesFingerprint, string[] ActiveHashes, TorrentRunContext[] SelectedTorrents);
public sealed record CycleRecord(int SchemaVersion, string AnalysisVersion, Guid CycleId,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, TargetIdentity Target, DraftInputs Inputs,
    Plan Plan, IReadOnlyDictionary<string, PreferenceValue> Original,
    IReadOnlyDictionary<string, PreferenceValue> IntendedApplied,
    IReadOnlyDictionary<string, PreferenceValue> ObservedReadback, string OperationStage,
    ApplyStatus ApplyStatus, ExperimentSummary Experiment, MeasurementSample[] BaselineSamples,
    MeasurementSample[] AfterSamples)
{
    // Older schema-2 history remains readable; missing context cannot authorize a new experiment.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MeasurementContext? BaselineContext { get; init; }
}
public sealed record ConfirmationSummary(string Token, Guid TargetSessionId, long Revision,
    DateTimeOffset ExpiresUtc, string ActionId, LocalizedMessage Description);
public sealed record SnapshotEvent(Guid? SessionId, Guid? OperationId, long Sequence, long Revision, AppSnapshot Snapshot);
public sealed record AcceptedOperation(Guid OperationId, long Revision);
public sealed record NativeSelection(string Token, string Purpose, string DisplayName, DateTimeOffset ExpiresUtc);
public sealed record ObservedHardware(HardwareInputs? Inputs, HardwareFacts Facts, string[] ReasonCodes);
public sealed record HardwareFacts(MetricReading RamBytes, MetricReading LogicalCpuCount,
    MetricReading PerformanceCpuCount, StorageType? StorageType, string? StorageReasonCode, string? VolumeToken);
public sealed record TargetInterface(string Id, string Name);
public sealed record NetworkTestResult(double DownloadBytesPerSecond, double UploadBytesPerSecond,
    DateTimeOffset MeasuredUtc, string[] ReasonCodes);
