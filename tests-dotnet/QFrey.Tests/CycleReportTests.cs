using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using Xunit;

namespace QFrey.Tests;

public sealed class CycleReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Attack = "<img src=x onerror=alert(1)>";

    [Fact]
    public void JsonExportKeepsNumericInputsTypedAndIncludesPreferencesAndRawSamples()
    {
        var record = Record();
        var artifact = CycleReport.ToJson(record);
        var json = Encoding.UTF8.GetString(artifact.Bytes);
        using var document = JsonDocument.Parse(artifact.Bytes);
        var root = document.RootElement;

        Assert.Equal(".json", artifact.Extension);
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(500, root.GetProperty("inputs").GetProperty("network").GetProperty("downloadMbps").GetDouble());
        Assert.Equal("sampling", root.GetProperty("baselineSamples")[0].GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("afterSamples").GetArrayLength());
        Assert.Equal(500, root.GetProperty("original").GetProperty("max_connec").GetInt32());
        Assert.Contains("sessionDownload", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirmationToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sessionCookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(Locale.RuRu, "Отчёт цикла", "МиБ/с")]
    [InlineData(Locale.EnUs, "Cycle report", "MiB/s")]
    public void HtmlLocalizesSummaryAndEscapesEveryUntrustedString(Locale locale, string title, string unit)
    {
        var html = Encoding.UTF8.GetString(CycleReport.ToHtml(Record(), locale).Bytes);

        Assert.Contains(title, html);
        Assert.Contains(unit, html);
        Assert.Contains("default-src 'none';", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NO_BASELINE_TRAFFIC", html);
    }

    [Fact]
    public void MissingMessageTranslationsAreReportedInsteadOfClaimedComplete()
    {
        var missing = CycleReport.GetUntranslatedMessageKeys(Record());
        Assert.Contains("results.unknownCard", missing);
        Assert.Contains("results.unknownExplanation", missing);
    }

    [Fact]
    public void HtmlUsesCatalogTemplateAndLocalizesNamedParameters()
    {
        var record = Record();
        var parameters = new Dictionary<string, MessageParameter>
        {
            ["profile"] = new TextParameter("nvme"),
            ["cacheMb"] = new NumberParameter(256)
        };
        var results = record.Experiment.Results.Select(result => result with
        {
            TitleKey = "metrics.download",
            Explanation = new LocalizedMessage("calculator.explanation.diskCache", parameters)
        }).ToArray();
        record = record with { Experiment = record.Experiment with { Results = results } };

        Assert.Empty(CycleReport.GetUntranslatedMessageKeys(record, Locale.RuRu));
        var html = Encoding.UTF8.GetString(CycleReport.ToHtml(record, Locale.RuRu).Bytes);

        Assert.Contains("Настройка дискового кэша", html);
        Assert.Contains("накопитель NVMe", html);
        Assert.DoesNotContain("{profile}", html);
        Assert.DoesNotContain("{cacheMb}", html);
        Assert.DoesNotContain("Непереведённые ключи сообщений", html);
    }

    [Fact]
    public void RejectsInvalidCycleAndBoundsJsonWhileWriting()
    {
        var invalid = Record() with { SchemaVersion = 99 };
        Assert.ThrowsAny<Exception>(() => CycleReport.ToJson(invalid));

        var hugeSample = Sample(0) with { ReasonCodes = [new string('x', CycleReport.MaximumBytes + 1)] };
        var oversized = Record() with { AfterSamples = [hugeSample] };
        var error = Assert.Throws<InvalidDataException>(() => CycleReport.ToJson(oversized));
        Assert.Equal("REPORT_TOO_LARGE", error.Message);
    }

    private static CycleRecord Record()
    {
        var id = Guid.Parse("a302db9d-4c32-4d4e-b088-b6f021acee55");
        var preferences = new Dictionary<string, PreferenceValue>
        {
            ["max_connec"] = new IntegerPreference(500),
            ["current_network_interface"] = new StringPreference(Attack)
        };
        var plan = new Plan(Guid.NewGuid(), Guid.NewGuid(), 1, Now, new string('a', 64), new string('b', 64),
            false, true, true, [], [], [], preferences, preferences);
        var workload = new WorkloadSummary(new WorkloadReference(Guid.NewGuid(), WorkloadKind.Owned, [new string('c', 40)]),
            Attack, "1234567", Attack, "ubuntu", true, []);
        var baseline = Measurement(MeasurementKind.Baseline, MetricScope.Session, 2 * 1024 * 1024);
        var after = Measurement(MeasurementKind.After, MetricScope.Session, 3 * 1024 * 1024);
        var results = new[]
        {
            Card(id, "results.download", ResultKind.MetricComparison, new ComparisonContent(new ComparisonValue(
                "transfer.download", ResultCategory.Throughput, MetricUnit.BytesPerSecond,
                new ValidHistoricalValue(2 * 1024 * 1024, Now), new NotMeasuredValue(["NO_BASELINE_TRAFFIC"]),
                null, null, Verdict.NotComparable, ["NO_BASELINE_TRAFFIC"], Evidence.Derived, MetricScope.Session))),
            Card(id, "results.observation", ResultKind.Observation,
                new ObservationContent("peer-count", new NotMeasuredValue(["UNKNOWN_MEASUREMENT_FIELD"]), MetricUnit.Count, MetricScope.Workload)),
            Card(id, "results.change", ResultKind.AppliedChange,
                new ChangeContent("max_connec", new IntegerPreference(500), new IntegerPreference(600), null, ApplyStatus.Pending)),
            Card(id, "results.limitation", ResultKind.Limitation, new LimitationContent(["INSUFFICIENT_VALID_SAMPLES"])),
            Card(id, "results.recovery", ResultKind.Recovery, new RecoveryContent(["max_connec"], ["Rollback"], true)),
            Card(id, "results.unknownCard", ResultKind.Limitation, new LimitationContent([]), "results.unknownExplanation")
        };
        var experiment = new ExperimentSummary(id, null, workload, baseline, after, plan, results);
        var inputs = new DraftInputs(
            new NetworkInputs(500, 100, ConnectionType.Fiber, false, Attack, false, InputSource.Manual, InputSource.Measured),
            new HardwareInputs(StorageType.Nvme, 16, 8, false, 8, InputSource.LocalDetection),
            new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);
        return new CycleRecord(2, "measurement-1", id, Now, Now, new("http://127.0.0.1:8080", "5.2.3", "2.11", "2.0"),
            inputs, plan, preferences, preferences, preferences, "completed", ApplyStatus.Verified, experiment,
            [Sample(0)], [Sample(0)]);
    }

    private static ResultCard Card(Guid cycleId, string title, ResultKind kind, ResultContent content,
        string explanationKey = "results.comparisonCaveat") => new(title, ResultCategory.Throughput, kind, title,
        new LocalizedMessage(explanationKey, new Dictionary<string, MessageParameter> { ["serverText"] = new TextParameter(Attack) }),
        Evidence.Derived, Severity.Info, ResultContext.Historical, cycleId, Now, content);

    private static MeasurementSummary Measurement(MeasurementKind kind, MetricScope scope, double mean) =>
        new(Guid.NewGuid(), kind, MeasurementStatus.Valid, Now, "measurement-1", scope, [], 1, 0,
            new ValidHistoricalValue(mean, Now), new ValidHistoricalValue(mean, Now),
            new ValidHistoricalValue(0, Now), new ValidHistoricalValue(0, Now));

    private static MeasurementSample Sample(double elapsed) => new(elapsed, MeasurementStatus.Sampling, [],
        new FreshReading(100, Now), new FreshReading(10, Now), new FreshReading(100, Now), new FreshReading(10, Now),
        new FreshReading(0, Now), new FreshReading(1, Now), 1, 0, 0);
}
