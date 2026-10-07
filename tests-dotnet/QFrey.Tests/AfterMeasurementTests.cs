using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class AfterMeasurementTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ValidatesExactFrozenWorkloadAndBuildsFullAppliedPreferenceExpectation()
    {
        var cycle = ValidCycle();
        var expected = BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan, cycle.Experiment.Workload!.Reference);
        Assert.Equal(cycle.BaselineContext!.Preferences.Count, expected.Count);
        Assert.Equal(600, Assert.IsType<IntegerPreference>(expected["max_connec"]).Value);
        BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, Snapshot(expected));
    }

    [Fact]
    public void RejectsDifferentWorkloadIdKindOrHashes()
    {
        var cycle = ValidCycle();
        var reference = cycle.Experiment.Workload!.Reference;
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan, reference with { Id = Guid.NewGuid() })).Code);
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan,
                reference with { Hashes = ["b".PadRight(40, 'b')] })).Code);
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan, reference with { Kind = WorkloadKind.Owned })).Code);
    }

    [Fact]
    public void RequiresExactMergedPreferenceReadbackAndExplicitlyDisabledLimits()
    {
        var cycle = ValidCycle();
        var expected = BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan, cycle.Experiment.Workload!.Reference);
        var drifted = expected.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        drifted["max_connec"] = new IntegerPreference(601);
        Assert.Equal(ErrorCodes.PreferenceDrift, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, Snapshot(drifted))).Code);
        Assert.Equal(ErrorCodes.MeasurementInvalid, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, Snapshot(expected, scheduler: true))).Code);
        Assert.Equal(ErrorCodes.MeasurementInvalid, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, Snapshot(expected, alternative: null))).Code);
    }

    [Fact]
    public void RejectsChangedActiveSetAndStoppedOrCompletedBaselineTorrent()
    {
        var cycle = ValidCycle();
        var expected = BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan, cycle.Experiment.Workload!.Reference);
        var changedActive = Snapshot(expected, activeHashes: ["b".PadRight(40, 'b')]);
        Assert.Equal(ErrorCodes.MeasurementInvalid, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, changedActive)).Code);
        var stopped = Snapshot(expected, selectedState: "pausedDL");
        Assert.Equal(ErrorCodes.MeasurementInvalid, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, stopped)).Code);
        var completed = Snapshot(expected, selectedProgress: 1);
        Assert.Equal(ErrorCodes.MeasurementInvalid, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterSnapshot(cycle, expected, completed)).Code);
    }

    [Fact]
    public void RejectsUnverifiedOrStaleCurrentCycleAndPlan()
    {
        var cycle = ValidCycle();
        var reference = cycle.Experiment.Workload!.Reference;
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle with { ApplyStatus = ApplyStatus.Unverified }, SessionId, cycle.Plan, reference)).Code);
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle, SessionId, cycle.Plan with { Revision = cycle.Plan.Revision + 1 }, reference)).Code);
        Assert.Equal(ErrorCodes.BaselineRequired, Assert.Throws<QbittorrentException>(() =>
            BridgeDispatcher.ValidateAfterCycle(cycle, Guid.NewGuid(), cycle.Plan, reference)).Code);
    }

    private static Guid SessionId { get; } = Guid.NewGuid();

    private static CycleRecord ValidCycle()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var baselinePreferences = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal)
        {
            ["max_connec"] = new IntegerPreference(500), ["dht"] = new BooleanPreference(true)
        };
        var context = new MeasurementContext(baselinePreferences, PlanBuilder.FingerprintPreferences(baselinePreferences),
            [Hash], [new TorrentRunContext(Hash, "downloading", .2)]);
        var baselineSamples = Enumerable.Range(0, 40).Select(index => new MeasurementSample(index * 1000,
            MeasurementStatus.Sampling, [], Reading(100, now.AddSeconds(index)), Reading(10, now.AddSeconds(index)),
            Reading(100, now.AddSeconds(index)), Reading(10, now.AddSeconds(index)), Reading(2, now.AddSeconds(index)),
            Reading(3, now.AddSeconds(index)), 1, 0, 0)).ToArray();
        var baseline = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, now,
            MetricScope.Workload, baselineSamples);
        var cycleId = Guid.NewGuid();
        var plan = new Plan(Guid.NewGuid(), SessionId, 4, now, new string('a', 64), context.PreferencesFingerprint,
            false, true, true, [], [], [], new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) },
            new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(600) });
        var reference = new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [Hash]);
        var workload = new WorkloadSummary(reference, "Existing torrent", "1024", "/downloads", null, false, []);
        var experiment = new ExperimentSummary(cycleId, null, workload, baseline, null, plan, []);
        var target = new TargetIdentity("http://127.0.0.1:8080", "5.2.0", "2.15.0", "2.0.11");
        var original = plan.Original;
        var intended = plan.Proposed;
        return new CycleRecord(2, MeasurementAnalysis.Version, cycleId, now, now, target,
            new DraftInputs(new NetworkInputs(100, 20, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
                new HardwareInputs(StorageType.SsdSata, 16, 8, false, 8, InputSource.Manual),
                new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null),
            plan, original, intended, intended, "appliedVerified", ApplyStatus.Verified, experiment, baselineSamples, [])
        { BaselineContext = context };
    }

    private static MeasurementSourceSnapshot Snapshot(IReadOnlyDictionary<string, PreferenceValue> preferences,
        bool? scheduler = false, bool? alternative = false, IReadOnlyList<string>? activeHashes = null,
        string selectedState = "downloading", double? selectedProgress = .3)
    {
        var active = activeHashes ?? [Hash];
        var context = new TorrentTelemetryContext([Hash], active, active.Contains(Hash, StringComparer.OrdinalIgnoreCase), null)
        {
            SelectedTorrents = [new(Hash, selectedState, null, selectedProgress, null)]
        };
        return new(preferences, scheduler, alternative, new TorrentTelemetryEvidence(context, []), []);
    }

    private static FreshReading Reading(double value, DateTimeOffset sampledAt) => new(value, sampledAt);
}
