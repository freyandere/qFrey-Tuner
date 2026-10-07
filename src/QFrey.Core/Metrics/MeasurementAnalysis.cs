using QFrey.Core.Contracts;

namespace QFrey.Core.Metrics;

public static class MeasurementAnalysis
{
    public const string Version = "measurement-1";
    private const int MinimumSamples = 30;

    public static MeasurementSummary Analyze(Guid id, MeasurementKind kind, DateTimeOffset startedUtc,
        MetricScope scope, IReadOnlyList<MeasurementSample> samples)
    {
        if (id == Guid.Empty || startedUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(kind)
            || scope is not (MetricScope.Session or MetricScope.Workload) || samples is null || samples.Count > 100_000)
            throw new ArgumentException("INVALID_MEASUREMENT_INPUT");

        var eligible = new List<(double Download, double Upload)>();
        var reasons = new List<string>();
        double previousElapsed = -1;
        foreach (var sample in samples)
        {
            if (sample is null || !double.IsFinite(sample.ElapsedMs) || sample.ElapsedMs < 0 || sample.ElapsedMs <= previousElapsed
                || !Enum.IsDefined(sample.Status) || sample.ReasonCodes is null)
                throw new ArgumentException("INVALID_MEASUREMENT_SAMPLE");
            previousElapsed = sample.ElapsedMs;
            if (sample.Status is MeasurementStatus.Invalid or MeasurementStatus.Cancelled)
            {
                reasons.AddRange(sample.ReasonCodes);
                reasons.Add(sample.Status == MeasurementStatus.Cancelled ? "MEASUREMENT_CANCELLED" : "MEASUREMENT_INVALID");
                continue;
            }
            if (sample.Status is not (MeasurementStatus.Sampling or MeasurementStatus.Valid)) continue;
            if (sample.ReasonCodes.Length != 0)
            {
                reasons.AddRange(sample.ReasonCodes);
                reasons.Add("UNKNOWN_MEASUREMENT_FIELD");
                continue;
            }
            var downloadReading = scope == MetricScope.Session ? sample.SessionDownload : sample.WorkloadDownload;
            var uploadReading = scope == MetricScope.Session ? sample.SessionUpload : sample.WorkloadUpload;
            var download = Value(downloadReading);
            var upload = Value(uploadReading);
            if (download is null || upload is null)
            {
                reasons.Add("UNKNOWN_MEASUREMENT_FIELD");
                continue;
            }
            eligible.Add((download.Value, upload.Value));
        }

        var values = eligible.Select(x => x.Download).ToArray();
        var valid = values.Length >= MinimumSamples;
        if (!valid) reasons.Add("INSUFFICIENT_VALID_SAMPLES");
        if (eligible.Count == 0 || eligible.Max(x => x.Download + x.Upload) <= 0)
        {
            valid = false;
            reasons.Add("NO_TRANSFER_ACTIVITY");
        }
        if (reasons.Count != 0) valid = false;
        reasons = reasons.Distinct(StringComparer.Ordinal).ToList();

        var summary = new MeasurementSummary(id, kind, valid ? MeasurementStatus.Valid : MeasurementStatus.Invalid,
            startedUtc, Version, scope, reasons.ToArray(), values.Length,
            samples.Count == 0 ? 0 : samples[^1].ElapsedMs - samples[0].ElapsedMs,
            Historical(values, StatisticsMean, startedUtc, samples, valid, reasons),
            Historical(values, Median, startedUtc, samples, valid, reasons),
            Historical(values, PopulationDeviation, startedUtc, samples, valid, reasons),
            Historical(values, ZeroPercent, startedUtc, samples, valid, reasons));
        return summary;
    }

    public static ResultCard[] Compare(Guid cycleId, MeasurementSummary baseline, IReadOnlyList<MeasurementSample> baselineSamples,
        MeasurementSummary after, IReadOnlyList<MeasurementSample> afterSamples, DateTimeOffset measuredAtUtc)
    {
        if (cycleId == Guid.Empty || baseline is null || after is null || baselineSamples is null || afterSamples is null
            || measuredAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("INVALID_COMPARISON_INPUT");
        if (!StringComparer.Ordinal.Equals(baseline.AnalysisVersion, Version)
            || !StringComparer.Ordinal.Equals(after.AnalysisVersion, Version))
            return [Card("download", cycleId, measuredAtUtc, NotComparable("transfer.download", baseline.Scope, "ANALYSIS_VERSION_MISMATCH")),
                Card("upload", cycleId, measuredAtUtc, NotComparable("transfer.upload", baseline.Scope, "ANALYSIS_VERSION_MISMATCH"))];
        baseline = Analyze(baseline.Id, baseline.Kind, baseline.StartedUtc, baseline.Scope, baselineSamples);
        after = Analyze(after.Id, after.Kind, after.StartedUtc, after.Scope, afterSamples);
        return [Card("download", cycleId, measuredAtUtc,
                CompareMetric("transfer.download", baseline, baselineSamples, after, afterSamples, true)),
            Card("upload", cycleId, measuredAtUtc,
                CompareMetric("transfer.upload", baseline, baselineSamples, after, afterSamples, false))];
    }

    private static ComparisonValue CompareMetric(string id, MeasurementSummary before, IReadOnlyList<MeasurementSample> beforeSamples,
        MeasurementSummary after, IReadOnlyList<MeasurementSample> afterSamples, bool download)
    {
        var reasons = new List<string>();
        if (before.Kind != MeasurementKind.Baseline || after.Kind != MeasurementKind.After
            || before.Status != MeasurementStatus.Valid || after.Status != MeasurementStatus.Valid || before.Scope != after.Scope
            || before.Scope is not (MetricScope.Session or MetricScope.Workload))
        {
            reasons.Add(before.Scope != after.Scope ? "SCOPE_MISMATCH" : "MEASUREMENT_INVALID");
            return new(id, ResultCategory.Throughput, MetricUnit.BytesPerSecond,
                InvalidValue(before, reasons[0]), InvalidValue(after, reasons[0]), null, null, Verdict.NotComparable,
                reasons.ToArray(), Evidence.Derived, before.Scope);
        }

        var left = ValuesAsDoubles(beforeSamples, before.Scope, download);
        var right = ValuesAsDoubles(afterSamples, after.Scope, download);
        if (left.Length < MinimumSamples || right.Length < MinimumSamples)
        {
            reasons.Add("INSUFFICIENT_VALID_SAMPLES");
            return new(id, ResultCategory.Throughput, MetricUnit.BytesPerSecond,
                InvalidValue(before, reasons[0]), InvalidValue(after, reasons[0]), null, null, Verdict.NotComparable,
                reasons.ToArray(), Evidence.Derived, before.Scope);
        }

        var beforeMean = StatisticsMean(left);
        var afterMean = StatisticsMean(right);
        var delta = afterMean - beforeMean;
        double? relative = beforeMean > 0 ? delta / beforeMean * 100 : null;
        Verdict verdict;
        if (beforeMean <= 0)
        {
            reasons.Add("NO_BASELINE_TRAFFIC");
            verdict = Verdict.Inconclusive;
        }
        else
        {
            var threshold = Math.Max(beforeMean * .05,
                2 * Math.Sqrt(Math.Pow(PopulationDeviation(left), 2) / left.Length
                    + Math.Pow(PopulationDeviation(right), 2) / right.Length));
            verdict = Math.Abs(delta) <= threshold ? Verdict.Inconclusive
                : delta > 0 ? Verdict.ObservedImprovement : Verdict.ObservedRegression;
            if (verdict == Verdict.Inconclusive) reasons.Add("WITHIN_NOISE_THRESHOLD");
        }
        reasons.Add("CORRELATED_NOISE_NOT_CAUSAL");
        return new(id, ResultCategory.Throughput, MetricUnit.BytesPerSecond,
            new ValidHistoricalValue(beforeMean, before.StartedUtc + TimeSpan.FromMilliseconds(before.DurationMs)),
            new ValidHistoricalValue(afterMean, after.StartedUtc + TimeSpan.FromMilliseconds(after.DurationMs)),
            delta, relative, verdict, reasons.ToArray(), Evidence.Derived, before.Scope);
    }

    private static ResultCard Card(string id, Guid cycleId, DateTimeOffset measuredAt, ComparisonValue value) =>
        new($"measurement.{id}", value.Category, ResultKind.MetricComparison, $"metrics.{id}",
            new("results.comparisonCaveat", new Dictionary<string, MessageParameter>()), value.Evidence, Severity.Neutral,
            ResultContext.Historical, cycleId, measuredAt, new ComparisonContent(value));

    private static ComparisonValue NotComparable(string id, MetricScope scope, string reason) =>
        new(id, ResultCategory.Throughput, MetricUnit.BytesPerSecond, new NotMeasuredValue([reason]),
            new NotMeasuredValue([reason]), null, null, Verdict.NotComparable, [reason], Evidence.Derived, scope);

    private static double?[] Values(IReadOnlyList<MeasurementSample> samples, MetricScope scope, bool download) => samples
        .Where(x => (x.Status is MeasurementStatus.Sampling or MeasurementStatus.Valid) && x.ReasonCodes.Length == 0)
        .Select(x => Value(scope == MetricScope.Session
            ? download ? x.SessionDownload : x.SessionUpload
            : download ? x.WorkloadDownload : x.WorkloadUpload))
        .Where(x => x is not null).ToArray();

    private static double[] ValuesAsDoubles(IReadOnlyList<MeasurementSample> samples, MetricScope scope, bool download) =>
        Values(samples, scope, download).Select(x => x!.Value).ToArray();

    private static double? Value(MetricReading reading) => reading switch
    {
        FreshReading { Value: >= 0 } fresh when double.IsFinite(fresh.Value) && fresh.SampledAtUtc.Offset == TimeSpan.Zero => fresh.Value,
        _ => null
    };

    private static HistoricalValue Historical(double[] values, Func<double[], double> calculate, DateTimeOffset startedUtc,
        IReadOnlyList<MeasurementSample> samples, bool valid, IReadOnlyList<string> reasons) => values.Length == 0
        ? new NotMeasuredValue(["NO_VALID_SAMPLES"])
        : !valid ? new InvalidHistoricalValue(reasons.ToArray())
        : new ValidHistoricalValue(calculate(values), startedUtc + TimeSpan.FromMilliseconds(samples[^1].ElapsedMs));
    private static HistoricalValue InvalidValue(MeasurementSummary summary, string reason) => new NotMeasuredValue([reason]);
    private static double StatisticsMean(double[] values) => values.Average();
    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
    private static double PopulationDeviation(double[] values)
    {
        var mean = StatisticsMean(values);
        return Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Length);
    }
    private static double ZeroPercent(double[] values) => values.Count(x => x == 0) * 100d / values.Length;
}
