using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;

public class MeasurementAnalysisTests
{
    [Theory]
    [InlineData(MeasurementStatus.Invalid)]
    [InlineData(MeasurementStatus.Cancelled)]
    public void TerminalReasonsSurviveAnalysisAndAreNotRepeated(MeasurementStatus status)
    {
        var samples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        samples[^1] = samples[^1] with { Status = status, ReasonCodes = ["RELEVANT_PREFERENCES_CHANGED", "RELEVANT_PREFERENCES_CHANGED"] };
        var result = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Workload, samples);
        Assert.Equal(MeasurementStatus.Invalid, result.Status);
        Assert.Single(result.ReasonCodes, code => code == "RELEVANT_PREFERENCES_CHANGED");
    }
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SummaryMatchesPopulationStatisticsAndCountsOnlyFreshMeasurementSamples()
    {
        var samples = Enumerable.Range(0, 31).Select(i => Sample(i * 1000, i == 0 ? 9 : (i - 1) % 3 * 2 * 1024 * 1024, 0)).ToArray();
        samples[0] = samples[0] with { Status = MeasurementStatus.WarmingUp, SessionDownload = new StaleReading(9, Start, "OLD") };
        var result = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, samples);
        Assert.Equal(MeasurementStatus.Valid, result.Status);
        Assert.Equal(30, result.SampleCount);
        Assert.Equal(MeasurementAnalysis.Version, result.AnalysisVersion);
        Assert.Equal(2 * 1024 * 1024, Assert.IsType<ValidHistoricalValue>(result.MeanDownload).Value);
        Assert.Equal(2 * 1024 * 1024, Assert.IsType<ValidHistoricalValue>(result.MedianDownload).Value);
        Assert.Equal(Math.Sqrt(8d / 3) * 1024 * 1024, Assert.IsType<ValidHistoricalValue>(result.StandardDeviation).Value, 6);
        Assert.Equal(100d / 3, Assert.IsType<ValidHistoricalValue>(result.ZeroSamplePercent).Value, 6);
    }

    [Fact]
    public void ComparisonUsesConservativeNoiseBoundaryAndTypedCards()
    {
        var beforeSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 5)).ToArray();
        var afterSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 11, 5)).ToArray();
        var before = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, beforeSamples);
        var after = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.After, Start.AddMinutes(1), MetricScope.Session, afterSamples);
        var results = MeasurementAnalysis.Compare(Guid.NewGuid(), before, beforeSamples, after, afterSamples, Start.AddMinutes(2));
        Assert.Equal(2, results.Length);
        var comparison = Assert.IsType<ComparisonContent>(results[0].Content).Comparison;
        Assert.Equal(Verdict.ObservedImprovement, comparison.Verdict);
        Assert.Equal(Evidence.Derived, comparison.Evidence);
        Assert.Contains("CORRELATED_NOISE_NOT_CAUSAL", comparison.ReasonCodes);
        Assert.Equal(10, comparison.RelativeDeltaPercent);
        Assert.Equal(Verdict.Inconclusive, Assert.IsType<ComparisonContent>(results[1].Content).Comparison.Verdict);
    }

    [Fact]
    public void ZeroBaselineIsInconclusiveAndNoBaselineTrafficIsExplicit()
    {
        var beforeSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 0, 2)).ToArray();
        var afterSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        var before = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, beforeSamples);
        var after = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.After, Start.AddMinutes(1), MetricScope.Session, afterSamples);
        var comparison = Assert.IsType<ComparisonContent>(MeasurementAnalysis.Compare(Guid.NewGuid(), before, beforeSamples,
            after, afterSamples, Start.AddMinutes(2))[0].Content).Comparison;
        Assert.Equal(Verdict.Inconclusive, comparison.Verdict);
        Assert.Null(comparison.RelativeDeltaPercent);
        Assert.Contains("NO_BASELINE_TRAFFIC", comparison.ReasonCodes);
    }

    [Fact]
    public void FewerThanThirtyFreshPointsCannotProduceValidSummary()
    {
        var samples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 0)).ToArray();
        samples[^1] = samples[^1] with { Status = MeasurementStatus.Invalid };
        var result = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, samples);
        Assert.Equal(MeasurementStatus.Invalid, result.Status);
        Assert.Contains("INSUFFICIENT_VALID_SAMPLES", result.ReasonCodes);
    }

    [Fact]
    public void MissingUploadReadingInvalidatesOtherwiseFreshDownloadSamples()
    {
        var samples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        samples[10] = samples[10] with { SessionUpload = new UnavailableReading("NOT_RECORDED") };
        var result = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, samples);
        Assert.Equal(MeasurementStatus.Invalid, result.Status);
        Assert.Contains("UNKNOWN_MEASUREMENT_FIELD", result.ReasonCodes);
    }

    [Fact]
    public void OneStalePointInvalidatesThirtyOneSampleWindow()
    {
        var samples = Enumerable.Range(0, 31).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        samples[7] = samples[7] with { SessionUpload = new StaleReading(1, Start, "STALE") };
        var result = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, samples);
        Assert.Equal(MeasurementStatus.Invalid, result.Status);
        Assert.Contains("UNKNOWN_MEASUREMENT_FIELD", result.ReasonCodes);
    }

    [Fact]
    public void SampleReasonsAndTerminalStatusesInvalidateAndUndefinedStatusThrows()
    {
        var withReason = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        withReason[4] = withReason[4] with { ReasonCodes = ["SOURCE_GAP"] };
        Assert.Equal(MeasurementStatus.Invalid, MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline,
            Start, MetricScope.Session, withReason).Status);

        var cancelled = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        cancelled[4] = cancelled[4] with { Status = MeasurementStatus.Cancelled };
        Assert.Equal(MeasurementStatus.Invalid, MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline,
            Start, MetricScope.Session, cancelled).Status);

        var undefined = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        undefined[4] = undefined[4] with { Status = (MeasurementStatus)999 };
        Assert.Throws<ArgumentException>(() => MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline,
            Start, MetricScope.Session, undefined));
    }

    [Fact]
    public void ComparisonRejectsDifferentAnalysisVersionsAndRevalidatesRawSeries()
    {
        var beforeSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 10, 1)).ToArray();
        var afterSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, 12, 1)).ToArray();
        var before = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start, MetricScope.Session, beforeSamples);
        var after = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.After, Start.AddMinutes(1), MetricScope.Session, afterSamples);
        var mismatch = MeasurementAnalysis.Compare(Guid.NewGuid(), before with { AnalysisVersion = "old" }, beforeSamples,
            after, afterSamples, Start.AddMinutes(2));
        Assert.All(mismatch, result =>
        {
            var comparison = Assert.IsType<ComparisonContent>(result.Content).Comparison;
            Assert.Equal(Verdict.NotComparable, comparison.Verdict);
            Assert.Contains("ANALYSIS_VERSION_MISMATCH", comparison.ReasonCodes);
        });

        var sameUnsupportedVersion = MeasurementAnalysis.Compare(Guid.NewGuid(),
            before with { AnalysisVersion = "legacy-analysis" }, beforeSamples,
            after with { AnalysisVersion = "legacy-analysis" }, afterSamples, Start.AddMinutes(2));
        Assert.All(sameUnsupportedVersion, result =>
        {
            var comparison = Assert.IsType<ComparisonContent>(result.Content).Comparison;
            Assert.Equal(Verdict.NotComparable, comparison.Verdict);
            Assert.Contains("ANALYSIS_VERSION_MISMATCH", comparison.ReasonCodes);
            Assert.Null(comparison.AbsoluteDelta);
            Assert.Null(comparison.RelativeDeltaPercent);
            Assert.IsType<NotMeasuredValue>(comparison.Before);
            Assert.IsType<NotMeasuredValue>(comparison.After);
        });

        afterSamples[0] = afterSamples[0] with { SessionUpload = new ErrorReading("INVALID") };
        var revalidated = MeasurementAnalysis.Compare(Guid.NewGuid(), before, beforeSamples, after, afterSamples, Start.AddMinutes(2));
        Assert.All(revalidated, result => Assert.Equal(Verdict.NotComparable,
            Assert.IsType<ComparisonContent>(result.Content).Comparison.Verdict));
    }

    private static MeasurementSample Sample(double elapsed, double download, double upload) => new(elapsed,
        MeasurementStatus.Sampling, [],
        new FreshReading(download, Start.AddMilliseconds(elapsed)), new FreshReading(upload, Start.AddMilliseconds(elapsed)),
        new FreshReading(download, Start.AddMilliseconds(elapsed)), new FreshReading(upload, Start.AddMilliseconds(elapsed)),
        new UnavailableReading("NOT_RECORDED"), new UnavailableReading("NOT_RECORDED"), null, null, null);
}
