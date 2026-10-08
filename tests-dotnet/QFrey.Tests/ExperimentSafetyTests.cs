using System.Net;
using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class ExperimentSafetyTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Started = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StableBaselineContextMatchesFreshReadOnlyState()
    {
        var api = new SafetyApi();
        using var session = await Connect(api);

        await ExperimentCommands.ValidateBaselineAsync(Cycle(session), session, default);

        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task OwnedBaselineRequiresFreshOwnershipVerifierAndPreservesItsFailure()
    {
        var api = new SafetyApi();
        using var session = await Connect(api);
        var existing = Cycle(session);
        var workload = existing.Experiment.Workload!;
        var owned = workload with { Reference = workload.Reference with { Kind = WorkloadKind.Owned }, OwnershipVerified = true };
        var cycle = existing with { Experiment = existing.Experiment with { Workload = owned } };
        await AssertBaselineRequired(cycle, session);
        var verified = 0;
        await ExperimentCommands.ValidateBaselineAsync(cycle, session, default, (reference, _) =>
        {
            Assert.Equal(owned.Reference, reference);
            verified++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, verified);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() =>
            ExperimentCommands.ValidateBaselineAsync(cycle, session, default,
                (_, _) => throw new QbittorrentException(ErrorCodes.WorkloadNotOwned)));
        Assert.Equal(ErrorCodes.WorkloadNotOwned, error.Code);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task MissingBaselineOrFrozenExistingWorkloadIsRejected()
    {
        var api = new SafetyApi();
        using var session = await Connect(api);
        var cycle = Cycle(session);

        var missingContext = cycle with { BaselineContext = null };
        var noWorkload = cycle with { Experiment = cycle.Experiment with { Workload = null } };
        var wrongKind = cycle with { Experiment = cycle.Experiment with
            { Workload = cycle.Experiment.Workload! with { Reference = new(Guid.NewGuid(), WorkloadKind.Owned, [Hash]) } } };

        await AssertBaselineRequired(missingContext, session);
        await AssertBaselineRequired(noWorkload, session);
        await AssertBaselineRequired(wrongKind, session);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Fact]
    public async Task PreferenceFingerprintDriftIsRejected()
    {
        var api = new SafetyApi();
        using var session = await Connect(api);
        var cycle = Cycle(session);
        api.MaxConnections = 501;

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => ExperimentCommands.ValidateBaselineAsync(cycle, session, default));
        Assert.Equal(ErrorCodes.PreferenceDrift, error.Code);
    }

    [Theory]
    [InlineData("stoppedDL", 0.2)]
    [InlineData("downloading", 1.0)]
    public async Task StoppedOrCompletedSelectedDownloadIsRejected(string state, double progress)
    {
        var api = new SafetyApi { State = state, Progress = progress };
        using var session = await Connect(api);

        await AssertMeasurementInvalid(Cycle(session), session);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EnabledSchedulingOrAlternativeLimitsAreRejected(bool scheduler, bool alternativeLimits)
    {
        var api = new SafetyApi { SchedulerEnabled = scheduler, AlternativeLimitsEnabled = alternativeLimits };
        using var session = await Connect(api);

        await AssertMeasurementInvalid(Cycle(session), session);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnknownSchedulingOrAlternativeLimitStateIsRejected(bool schedulerUnknown, bool limitsUnknown)
    {
        var api = new SafetyApi { SchedulerUnknown = schedulerUnknown, AlternativeLimitsUnknown = limitsUnknown };
        using var session = await Connect(api);

        await AssertMeasurementInvalid(Cycle(session), session);
    }

    [Fact]
    public async Task ChangedActiveHashSetIsRejected()
    {
        var api = new SafetyApi { AddOtherActiveTorrent = true };
        using var session = await Connect(api);

        await AssertMeasurementInvalid(Cycle(session), session);
    }

    [Fact]
    public async Task FrozenWorkloadMustMatchPersistedSelectedContext()
    {
        var api = new SafetyApi();
        using var session = await Connect(api);
        var cycle = Cycle(session);
        var changed = cycle with { Experiment = cycle.Experiment with
            { Workload = cycle.Experiment.Workload! with { Reference = new(Guid.NewGuid(), WorkloadKind.Existing, [OtherHash]) } } };

        await AssertMeasurementInvalid(changed, session);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    private static async Task AssertBaselineRequired(CycleRecord cycle, QbittorrentSession session)
    {
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => ExperimentCommands.ValidateBaselineAsync(cycle, session, default));
        Assert.Equal(ErrorCodes.BaselineRequired, error.Code);
    }

    private static async Task AssertMeasurementInvalid(CycleRecord cycle, QbittorrentSession session)
    {
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => ExperimentCommands.ValidateBaselineAsync(cycle, session, default));
        Assert.Equal(ErrorCodes.MeasurementInvalid, error.Code);
    }

    private static Task<QbittorrentSession> Connect(SafetyApi api) =>
        QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);

    private static CycleRecord Cycle(QbittorrentSession session)
    {
        var prefs = SafePreferences();
        var fingerprint = PlanBuilder.FingerprintPreferences(prefs);
        var cycleId = Guid.NewGuid();
        var baseline = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Started,
            MetricScope.Workload, Samples());
        var workload = new WorkloadSummary(new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [Hash]),
            "existing workload", "1000", "/downloads", null, false, []);
        var context = new MeasurementContext(prefs, fingerprint, [Hash], [new(Hash, "downloading", .2)]);
        var plan = new Plan(Guid.NewGuid(), Guid.NewGuid(), 1, Started, new string('a', 64), fingerprint,
            false, true, false, [], [], [], prefs, prefs);
        var inputs = Inputs();
        var experiment = new ExperimentSummary(cycleId, inputs, workload, baseline, null, plan, []);
        var versions = session.Versions;
        return new CycleRecord(2, MeasurementAnalysis.Version, cycleId, Started, Started,
            new(session.Endpoint.AbsoluteUri, versions.Qbittorrent, versions.WebApi, versions.Libtorrent),
            inputs, plan, prefs, prefs, prefs, "baselineReady", ApplyStatus.NotApplied, experiment, Samples(), [])
        { BaselineContext = context };
    }

    private static DraftInputs Inputs() => new(
        new NetworkInputs(500, 100, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
        new HardwareInputs(StorageType.Nvme, 16, 8, false, 8, InputSource.Manual),
        new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);

    private static Dictionary<string, PreferenceValue> SafePreferences() => new(StringComparer.Ordinal)
    {
        ["up_limit"] = new IntegerPreference(0), ["dl_limit"] = new IntegerPreference(0),
        ["max_connec"] = new IntegerPreference(500), ["max_connec_per_torrent"] = new IntegerPreference(100),
        ["dht"] = new BooleanPreference(true), ["pex"] = new BooleanPreference(true),
        ["lsd"] = new BooleanPreference(false), ["encryption"] = new IntegerPreference(0)
    };

    private static MeasurementSample[] Samples() => Enumerable.Range(0, 40).Select(index =>
    {
        var at = Started.AddSeconds(index);
        var reading = new FreshReading(100, at);
        return new MeasurementSample(index * 1000d, MeasurementStatus.Sampling, [], reading, reading, reading, reading,
            reading, reading, 1, 0, 0);
    }).ToArray();

    private sealed class SafetyApi : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Calls { get; } = [];
        public int MaxConnections { get; set; } = 500;
        public bool? SchedulerEnabled { get; set; } = false;
        public bool? AlternativeLimitsEnabled { get; set; } = false;
        public bool SchedulerUnknown { get; set; }
        public bool AlternativeLimitsUnknown { get; set; }
        public string State { get; set; } = "downloading";
        public double Progress { get; set; } = .2;
        public bool AddOtherActiveTorrent { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            var route = string.Join('/', request.RequestUri.AbsolutePath.Split('/').TakeLast(2));
            var body = route switch
            {
                "app/version" => "v5.2.0",
                "app/webapiVersion" => "2.15.0",
                "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => PreferencesJson(),
                "transfer/info" => TransferJson(),
                "torrents/info" => TorrentsJson(),
                _ => throw new InvalidOperationException("Unexpected API request: " + route)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private string PreferencesJson()
        {
            var values = $"\"up_limit\":0,\"dl_limit\":0,\"max_connec\":{MaxConnections},\"max_connec_per_torrent\":100,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0";
            if (!SchedulerUnknown) values += ",\"scheduler_enabled\":" + (SchedulerEnabled == true ? "true" : "false");
            return "{" + values + "}";
        }
        private string TransferJson() => AlternativeLimitsUnknown
            ? "{\"dl_info_speed\":100,\"up_info_speed\":20,\"dht_nodes\":7}"
            : $"{{\"dl_info_speed\":100,\"up_info_speed\":20,\"dht_nodes\":7,\"use_alt_speed_limits\":{(AlternativeLimitsEnabled == true ? "true" : "false")}}}";
        private string TorrentsJson()
        {
            var rows = $"{{\"hash\":\"{Hash}\",\"state\":\"{State}\",\"progress\":{Progress.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"dlspeed\":100,\"upspeed\":20,\"num_seeds\":2,\"num_leechs\":3}}";
            if (AddOtherActiveTorrent)
                rows += $",{{\"hash\":\"{OtherHash}\",\"state\":\"downloading\",\"progress\":0.1,\"dlspeed\":100,\"upspeed\":20,\"num_seeds\":2,\"num_leechs\":3}}";
            return "[" + rows + "]";
        }
    }
}
