using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Persistence;

public sealed class CycleStore
{
    private const int MaxHistoryPage = 100;
    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
    { "limit_utp_rate", "queueing_enabled", "preallocate_all", "anonymous_mode", "dht", "pex", "lsd", "enable_coalesce_read_write", "random_port" };
    private static readonly HashSet<string> IntegerKeys = new(StringComparer.Ordinal)
    {
        "up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "max_uploads", "max_uploads_per_torrent",
        "max_active_downloads", "max_active_uploads", "max_active_torrents", "async_io_threads", "disk_io_read_mode",
        "disk_io_write_mode", "bittorrent_protocol", "send_buffer_watermark", "send_buffer_low_watermark",
        "send_buffer_watermark_factor", "socket_backlog_size", "connection_speed", "encryption", "disk_cache", "listen_port"
    };
    private readonly AtomicJsonStore records;
    private readonly string root;
    // ponytail: per-instance writer lock; use a cross-process lock only if multiple app instances can write one store.
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public CycleStore(string trustedRoot)
    {
        records = new AtomicJsonStore(trustedRoot);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
    }

    public async Task SaveAsync(CycleRecord cycle, CancellationToken cancellationToken = default)
    {
        Validate(cycle);
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var previous = await records.ReadAsync<CycleRecord>(cycle.CycleId, cancellationToken);
            if (previous is not null)
            {
                Validate(previous);
                // The baseline proof is captured once; later writes cannot replace its authority.
                if (!SameBaseline(previous.BaselineContext, cycle.BaselineContext)) throw Failed();
                var firstBackup = previous.ApplyStatus == ApplyStatus.NotApplied && previous.Original.Count == 0
                    && cycle.ApplyStatus == ApplyStatus.Pending && cycle.OperationStage == "applying";
                if (previous.CycleId != cycle.CycleId || previous.CreatedUtc != cycle.CreatedUtc || previous.Target != cycle.Target
                    || !firstBackup && (previous.Original.Count != cycle.Original.Count
                    || previous.Original.Any(pair => !cycle.Original.TryGetValue(pair.Key, out var value) || value != pair.Value))) throw Failed();
            }
            await records.WriteAsync(cycle.CycleId, cycle, cancellationToken);
        }
        finally { writeGate.Release(); }
    }

    private static bool SameBaseline(MeasurementContext? previous, MeasurementContext? next)
    {
        if (previous is null || next is null) return previous is null && next is null;
        return previous.PreferencesFingerprint == next.PreferencesFingerprint
            && previous.ActiveHashes.Order(StringComparer.Ordinal).SequenceEqual(next.ActiveHashes.Order(StringComparer.Ordinal))
            && previous.SelectedTorrents.OrderBy(row => row.Hash, StringComparer.Ordinal)
                .SequenceEqual(next.SelectedTorrents.OrderBy(row => row.Hash, StringComparer.Ordinal));
    }

    public async Task<CycleRecord?> ReadAsync(Guid cycleId, TargetIdentity? expectedTarget = null,
        CancellationToken cancellationToken = default)
    {
        if (cycleId == Guid.Empty) throw new ArgumentException("Cycle id is required.", nameof(cycleId));
        var cycle = await records.ReadAsync<CycleRecord>(cycleId, cancellationToken);
        if (cycle is null) return null;
        Validate(cycle);
        if (cycle.CycleId != cycleId || expectedTarget is not null && cycle.Target != expectedTarget) throw Failed();
        return cycle;
    }

    public async Task<HistoryPage> ListAsync(string? cursor = null, int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > MaxHistoryPage) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var after = string.Empty;
        if (cursor is not null)
        {
            if (cursor.Length > 256 || !Guid.TryParseExact(DecodeCursor(cursor), "N", out var decoded) || decoded == Guid.Empty)
                throw new ArgumentException("Invalid history cursor.", nameof(cursor));
            after = decoded.ToString("N");
        }

        // ponytail: sort filenames for stable cursors; add a compact index only if history reaches tens of thousands.
        // Only this page's records and series are deserialized.
        var ids = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is not null && Guid.TryParseExact(name, "N", out _) && StringComparer.Ordinal.Compare(name, after) > 0)
                .Order(StringComparer.Ordinal).Take(pageSize + 1).ToArray()
            : [];
        var hasMore = ids.Length > pageSize;
        var items = new List<CycleSummary>(Math.Min(ids.Length, pageSize));
        foreach (var name in ids.Take(pageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.ParseExact(name!, "N");
            var cycle = await ReadAsync(id, cancellationToken: cancellationToken);
            if (cycle is not null) items.Add(Summarize(cycle));
        }
        var next = hasMore && ids.Length > 0 ? EncodeCursor(ids[pageSize - 1]!) : null;
        return new HistoryPage(items.ToArray(), next);
    }

    public static void Validate(CycleRecord cycle)
    {
        if (cycle is null || cycle.SchemaVersion != Protocol.SchemaVersion || cycle.CycleId == Guid.Empty
            || cycle.AnalysisVersion is null || cycle.AnalysisVersion.Length is 0 or > 64
            || !Utc(cycle.CreatedUtc) || !Utc(cycle.UpdatedUtc) || cycle.UpdatedUtc < cycle.CreatedUtc
            || !ValidTarget(cycle.Target) || cycle.Inputs is null || cycle.Plan is null || cycle.Experiment is null
            || !Enum.IsDefined(cycle.ApplyStatus) || cycle.OperationStage is null || cycle.OperationStage.Length > 64
            || !Enum.GetValues<ExperimentPhase>().Any(phase => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(phase.ToString()) == cycle.OperationStage)
            || cycle.BaselineSamples is null || cycle.AfterSamples is null
            || cycle.BaselineSamples.Length > 100_000 || cycle.AfterSamples.Length > 100_000)
            throw Failed();
        try { CommandPayloads.ValidateInputs(cycle.Inputs); }
        catch (JsonException) { throw Failed(); }
        ValidatePlan(cycle.Plan);
        ValidatePreferences(cycle.Original);
        ValidatePreferences(cycle.IntendedApplied);
        ValidatePreferences(cycle.ObservedReadback);
        if (cycle.ApplyStatus != ApplyStatus.NotApplied && (cycle.Original.Count == 0
            || !SameValues(cycle.Plan.Original, cycle.Original) || !IsSubset(cycle.IntendedApplied, cycle.Original)
            || !IsSubset(cycle.ObservedReadback, cycle.Original) || !SameValues(cycle.Plan.Proposed, cycle.IntendedApplied))) throw Failed();
        ValidateExperiment(cycle.Experiment, cycle.CycleId);
        if (cycle.Experiment.RunInputs is { } runInputs)
        {
            try { CommandPayloads.ValidateInputs(runInputs); }
            catch (JsonException) { throw Failed(); }
            if (runInputs != cycle.Inputs) throw Failed();
        }
        if (cycle.Experiment.Workload is { } workload) ValidateWorkload(workload);
        if (cycle.Experiment.Plan is { } experimentPlan)
        {
            ValidatePlan(experimentPlan);
            if (experimentPlan.Id != cycle.Plan.Id) throw Failed();
        }
        foreach (var sample in cycle.BaselineSamples.Concat(cycle.AfterSamples)) ValidateSample(sample);
        if (cycle.BaselineContext is { } context)
        {
            ValidatePreferences(context.Preferences);
            if (!Fingerprint(context.PreferencesFingerprint)
                || QFrey.Core.Tuning.PlanBuilder.FingerprintPreferences(context.Preferences) != context.PreferencesFingerprint
                || context.ActiveHashes is null || context.ActiveHashes.Length is < 1 or > 5000
                || context.ActiveHashes.Any(hash => !ValidHash(hash))
                || context.ActiveHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != context.ActiveHashes.Length
                || context.SelectedTorrents is null || context.SelectedTorrents.Length is < 1 or > 5000
                || context.SelectedTorrents.Any(t => t is null || !ValidHash(t.Hash) || string.IsNullOrWhiteSpace(t.State)
                    || t.State.Length > 32 || t.Progress is double progress && (!double.IsFinite(progress) || progress is < 0 or > 1))
                || context.SelectedTorrents.Select(t => t.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != context.SelectedTorrents.Length)
                throw Failed();
        }
    }
    private static bool ValidHash(string? hash) => hash is { Length: 40 } && hash.All(Uri.IsHexDigit);

    private static void ValidatePlan(Plan plan)
    {
        if (plan.Id == Guid.Empty || plan.TargetSessionId == Guid.Empty || !Utc(plan.CreatedUtc)
            || !CommandPayloads.SafeRevision(plan.Revision) || !Fingerprint(plan.InputsFingerprint)
            || !Fingerprint(plan.BaselineFingerprint) && !(plan.PreviewOnly && plan.BaselineFingerprint == string.Empty)
            || plan.PreviewOnly && (plan.Approved || plan.Applicable)
            || plan.Recommendations is null || plan.Omissions is null
            || plan.BlockReasonCodes is null || plan.Recommendations.Length > 128 || plan.Omissions.Length > 128
            || plan.Original is null || plan.Proposed is null || plan.Original.Count != plan.Proposed.Count
            || plan.Original.Keys.Any(key => !plan.Proposed.ContainsKey(key)))
            throw Failed();
        if (plan.BlockReasonCodes.Any(code => !StableCode(code))) throw Failed();
        if (plan.Recommendations.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 128
                || string.IsNullOrWhiteSpace(r.GroupId) || r.GroupId.Length > 128
                || string.IsNullOrWhiteSpace(r.ApiKey) || !IsSafePreferenceKey(r.ApiKey) || !Enum.IsDefined(r.Category)
                || !Enum.IsDefined(r.Evidence) || !Enum.IsDefined(r.ValueType) || !Enum.IsDefined(r.SupportStatus)
                || r.ApiKey == "random_port" && r.ProposedValue is BooleanPreference { Value: true }
                || !ValueMatches(r.ApiKey, r.ProposedValue) || r.CurrentValue is not null && !ValueMatches(r.ApiKey, r.CurrentValue)
                || r.ValueType != (r.ApiKey == "current_network_interface" ? RecommendationType.Interface
                    : BooleanKeys.Contains(r.ApiKey) ? RecommendationType.Bool
                    : r.ApiKey is "bittorrent_protocol" or "encryption" or "disk_io_read_mode" or "disk_io_write_mode"
                        ? RecommendationType.Enum : RecommendationType.Number)
                || r.Reason is null || r.Reason.Parameters is null || r.Reason.Parameters.Count > 32
                || r.Reason.Parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                    || pair.Value is NumberParameter number && !double.IsFinite(number.Value)
                    || pair.Value is NumberParameter numberParam && Math.Abs(numberParam.Value) > 9007199254740991
                    || pair.Value is TextParameter text && (text.Value is null || text.Value.Length > 256)
                    || pair.Value is not (NumberParameter or TextParameter))
                || r.AllowedValues is null || r.AllowedValues.Length > 128 || r.AllowedValues.Any(v => v is null || !ValueMatches(r.ApiKey, v.Value))
                || r.AllowedRange is { } range && (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum)
                    || !double.IsFinite(range.Step) || range.Minimum > range.Maximum || range.Step <= 0)
                || r.CautionCodes is null || r.CautionCodes.Length > 64)
            || plan.Omissions.Any(o => o is null || !(IsSafePreferenceKey(o.ApiKey)
                    || o.ApiKey == "super_seeding" && o.ReasonCode == "PER_TORRENT_SETTING")
                || !StableCode(o.ReasonCode))) throw Failed();
        ValidatePreferences(plan.Original);
        ValidatePreferences(plan.Proposed);
    }

    private static void ValidateExperiment(ExperimentSummary experiment, Guid cycleId)
    {
        if (experiment.CycleId is not null && experiment.CycleId != cycleId || experiment.Results is null || experiment.Results.Length > 128 || experiment.Results.Any(r => r is null || r.CycleId != cycleId
            || !Enum.IsDefined(r.Category) || !Enum.IsDefined(r.Kind) || !Enum.IsDefined(r.Evidence) || !Enum.IsDefined(r.Severity)
            || !Enum.IsDefined(r.Context) || string.IsNullOrWhiteSpace(r.Id) || r.Explanation is null || r.Explanation.Parameters is null
            || r.MeasuredAtUtc is { } time && !Utc(time) || !ValidResultContent(r.Kind, r.Content)))
            throw Failed();
        if (experiment.Baseline is { } baseline) ValidateMeasurement(baseline);
        if (experiment.After is { } after) ValidateMeasurement(after);
    }

    private static void ValidateWorkload(WorkloadSummary workload)
    {
        var reference = workload.Reference;
        if (reference is null || reference.Id == Guid.Empty || !Enum.IsDefined(reference.Kind) || reference.Hashes is null
            || reference.Hashes.Length is < 1 or > 5000 || reference.Hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reference.Hashes.Length
            || reference.Hashes.Any(hash => hash is null || hash.Length != 40 || !hash.All(Uri.IsHexDigit))
            || string.IsNullOrWhiteSpace(workload.Name) || workload.Name.Length > 256
            || workload.TotalBytesDecimal is null || workload.TotalBytesDecimal.Length > 20
            || !ulong.TryParse(workload.TotalBytesDecimal, out _) || workload.ServerSavePath is null || workload.ServerSavePath.Length > 2048
            || workload.CatalogueId?.Length > 128 || workload.ReasonCodes is null || workload.ReasonCodes.Length > 64)
            throw Failed();
    }

    private static void ValidateMeasurement(MeasurementSummary measurement)
    {
        if (measurement.Id == Guid.Empty || !Utc(measurement.StartedUtc) || !Enum.IsDefined(measurement.Kind)
            || !Enum.IsDefined(measurement.Status) || !Enum.IsDefined(measurement.Scope) || measurement.SampleCount < 0
            || measurement.SampleCount > 100_000 || !FiniteNonnegative(measurement.DurationMs) || string.IsNullOrEmpty(measurement.AnalysisVersion)
            || measurement.ReasonCodes is null)
            throw Failed();
        ValidateHistorical(measurement.MeanDownload);
        ValidateHistorical(measurement.MedianDownload);
        ValidateHistorical(measurement.StandardDeviation);
        ValidateHistorical(measurement.ZeroSamplePercent);
    }

    private static void ValidateHistorical(HistoricalValue value)
    {
        if (value is ValidHistoricalValue valid && (!double.IsFinite(valid.Value) || !Utc(valid.MeasuredAtUtc))
            || value is InvalidHistoricalValue invalid && invalid.ReasonCodes is null
            || value is NotMeasuredValue notMeasured && notMeasured.ReasonCodes is null
            || value is not (ValidHistoricalValue or InvalidHistoricalValue or NotMeasuredValue)) throw Failed();
    }

    private static bool ValidResultContent(ResultKind kind, ResultContent content) => kind switch
    {
        ResultKind.MetricComparison => content is ComparisonContent { Comparison: { } comparison }
            && Enum.IsDefined(comparison.Category) && Enum.IsDefined(comparison.Unit) && Enum.IsDefined(comparison.Verdict)
            && Enum.IsDefined(comparison.Evidence) && Enum.IsDefined(comparison.Scope)
            && IsValidHistorical(comparison.Before) && IsValidHistorical(comparison.After)
            && (comparison.AbsoluteDelta is null || double.IsFinite(comparison.AbsoluteDelta.Value))
            && (comparison.RelativeDeltaPercent is null || double.IsFinite(comparison.RelativeDeltaPercent.Value)),
        ResultKind.Observation => content is ObservationContent { Value: { } value } && Enum.IsDefined(((ObservationContent)content).Unit)
            && Enum.IsDefined(((ObservationContent)content).Scope) && IsValidHistorical(value),
        ResultKind.AppliedChange => content is ChangeContent change && IsSafePreferenceKey(change.ApiKey)
            && ValueMatches(change.ApiKey, change.Before) && ValueMatches(change.ApiKey, change.Intended)
            && (change.Observed is null || ValueMatches(change.ApiKey, change.Observed)) && Enum.IsDefined(change.Status),
        ResultKind.Limitation => content is LimitationContent { ReasonCodes: not null },
        ResultKind.Recovery => content is RecoveryContent { ConflictKeys: not null, ActionIds: not null },
        _ => false
    };

    private static bool IsValidHistorical(HistoricalValue value) => value switch
    {
        ValidHistoricalValue valid => double.IsFinite(valid.Value) && Utc(valid.MeasuredAtUtc),
        InvalidHistoricalValue invalid => invalid.ReasonCodes is not null,
        NotMeasuredValue missing => missing.ReasonCodes is not null,
        _ => false
    };

    private static void ValidateSample(MeasurementSample sample)
    {
        if (sample is null || !FiniteNonnegative(sample.ElapsedMs) || !Enum.IsDefined(sample.Status) || sample.ReasonCodes is null)
            throw Failed();
        foreach (var reading in new[] { sample.SessionDownload, sample.SessionUpload, sample.WorkloadDownload,
                     sample.WorkloadUpload, sample.Seeds, sample.Peers })
            if (reading is FreshReading fresh && (!FiniteNonnegative(fresh.Value) || !Utc(fresh.SampledAtUtc))
                || reading is StaleReading stale && (!FiniteNonnegative(stale.LastValue) || !Utc(stale.SampledAtUtc) || string.IsNullOrEmpty(stale.ReasonCode))
                || reading is UnavailableReading unavailable && string.IsNullOrEmpty(unavailable.ReasonCode)
                || reading is ErrorReading error && string.IsNullOrEmpty(error.ReasonCode)
                || reading is not (FreshReading or StaleReading or UnavailableReading or ErrorReading)) throw Failed();
    }

    private static void ValidatePreferences(IReadOnlyDictionary<string, PreferenceValue> values)
    {
        if (values is null || values.Count > 64 || values.Any(pair => !IsSafePreferenceKey(pair.Key) || !ValueMatches(pair.Key, pair.Value))) throw Failed();
    }

    private static bool ValidTarget(TargetIdentity? target)
    {
        if (target is null || target.Endpoint is null || target.Endpoint.Length > 2048
            || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
        return !string.IsNullOrWhiteSpace(target.QbittorrentVersion) && target.QbittorrentVersion.Length <= 64
            && !string.IsNullOrWhiteSpace(target.ApiVersion) && target.ApiVersion.Length <= 64
            && !string.IsNullOrWhiteSpace(target.LibtorrentVersion) && target.LibtorrentVersion.Length <= 64;
    }

    private static CycleSummary Summarize(CycleRecord cycle)
    {
        var phase = Enum.GetValues<ExperimentPhase>().First(candidate =>
            System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(candidate.ToString()) == cycle.OperationStage);
        return new CycleSummary(cycle.CycleId, cycle.CreatedUtc, cycle.Target.Endpoint, cycle.Target.QbittorrentVersion,
            phase, cycle.ApplyStatus, cycle.Original.Count);
    }

    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    public static bool IsSafePreferenceKey(string key) => BooleanKeys.Contains(key) || IntegerKeys.Contains(key) || key == "current_network_interface";
    public static bool IsSafePreferenceValue(string key, PreferenceValue? value) => ValueMatches(key, value);
    private static bool ValueMatches(string key, PreferenceValue? value) => value switch
    {
        BooleanPreference when BooleanKeys.Contains(key) => true,
        IntegerPreference integer when IntegerKeys.Contains(key) => key switch
        {
            "disk_io_read_mode" => integer.Value is 0 or 1,
            "disk_io_write_mode" or "bittorrent_protocol" or "encryption" => integer.Value is >= 0 and <= 2,
            "up_limit" or "dl_limit" => integer.Value >= 0,
            "max_connec" or "max_connec_per_torrent" or "max_uploads" or "max_uploads_per_torrent" => integer.Value >= -1,
            "max_active_downloads" or "max_active_uploads" or "max_active_torrents" or "disk_cache" => integer.Value >= -1,
            "async_io_threads" => integer.Value is >= 1 and <= 1024,
            "listen_port" => integer.Value is >= 0 and <= 65535,
            _ => integer.Value >= 0
        },
        StringPreference text when key == "current_network_interface" => text.Value is not null && text.Value.Length <= 256,
        _ => false
    };
    private static bool SameValues(IReadOnlyDictionary<string, PreferenceValue> expected, IReadOnlyDictionary<string, PreferenceValue> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));
    private static bool IsSubset(IReadOnlyDictionary<string, PreferenceValue> subset, IReadOnlyDictionary<string, PreferenceValue> superset) =>
        subset.All(pair => superset.ContainsKey(pair.Key));
    private static bool StableCode(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');
    private static bool Fingerprint(string value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool FiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static string EncodeCursor(string id) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(id));
    private static string DecodeCursor(string value)
    {
        try { return System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { return string.Empty; }
    }
    private static QbittorrentException Failed() => new(ErrorCodes.PersistenceFailed);
}
