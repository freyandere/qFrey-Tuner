using System.Net;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;

public sealed class MutationCoordinatorTests
{
    [Fact]
    public async Task LegacyBaselineWithoutRecordedContextCannotAuthorizePost()
    {
        await using var fixture = await Fixture.CreateAsync(includeContext: false);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.ApplyAsync());
        Assert.Equal(ErrorCodes.BaselineRequired, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task ApplyPersistsBackupBeforeSinglePostAndVerifiesReadback()
    {
        await using var fixture = await Fixture.CreateAsync();
        CycleRecord? pendingAtPost = null;
        fixture.Backend.BeforePost = async () => pendingAtPost = await fixture.Store.ReadAsync(fixture.Record.CycleId);

        var result = await fixture.ApplyAsync();

        Assert.NotNull(pendingAtPost);
        Assert.Equal(ApplyStatus.Pending, pendingAtPost!.ApplyStatus);
        Assert.Equal("applying", pendingAtPost.OperationStage);
        Assert.Equal(fixture.Plan.Original, pendingAtPost.Original);
        Assert.Equal(ApplyStatus.Verified, result.ApplyStatus);
        Assert.Equal("appliedVerified", result.OperationStage);
        Assert.Equal(fixture.Plan.Original, result.Original);
        Assert.Equal(fixture.Plan.Proposed, result.ObservedReadback);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(ApplyStatus.Verified, (await fixture.Store.ReadAsync(result.CycleId))!.ApplyStatus);
    }

    [Fact]
    public async Task PartialPostMismatchPersistsLastObservedValuesWithoutRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.ApplyPosts = false;
        fixture.Backend.BeforePost = () =>
        {
            fixture.Backend.Set("up_limit", new IntegerPreference(2048));
            return Task.CompletedTask;
        };

        var result = await fixture.ApplyAsync();

        Assert.Equal(ApplyStatus.Unverified, result.ApplyStatus);
        Assert.Equal("recoveryRequired", result.OperationStage);
        Assert.Equal(fixture.Plan.Original, result.Original);
        Assert.Equal(new IntegerPreference(2048), result.ObservedReadback["up_limit"]);
        Assert.Equal(1, fixture.Backend.PostCount);
        var persisted = (await fixture.Store.ReadAsync(result.CycleId))!;
        Assert.Equal(ApplyStatus.Unverified, persisted.ApplyStatus);
        Assert.Equal(new IntegerPreference(2048), persisted.ObservedReadback["up_limit"]);
    }

    [Fact]
    public async Task LostPostResponseIsReadBackAndVerifiedWithoutRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.LosePostResponse = true;
        var result = await fixture.ApplyAsync();
        Assert.Equal(ApplyStatus.Verified, result.ApplyStatus);
        Assert.Equal("appliedVerified", result.OperationStage);
        Assert.Equal(fixture.Plan.Proposed, result.ObservedReadback);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(ApplyStatus.Verified, (await fixture.Store.ReadAsync(result.CycleId))!.ApplyStatus);
    }

    [Fact]
    public async Task LostPostResponseWithUnchangedValuesResolvesAsRevertedWithoutRetry()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.ApplyPosts = false;
        fixture.Backend.LosePostResponse = true;

        var result = await fixture.ApplyAsync();

        Assert.Equal(ApplyStatus.Reverted, result.ApplyStatus);
        Assert.Equal("rolledBack", result.OperationStage);
        Assert.Equal(fixture.Plan.Original, result.ObservedReadback);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task RollbackRefusesExternalDriftWithoutPosting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var applied = await fixture.ApplyAsync();
        Assert.Equal(ApplyStatus.Verified, applied.ApplyStatus);
        fixture.Backend.Set("up_limit", new IntegerPreference(2048));
        var confirmation = await fixture.ConfirmAsync("Rollback");
        var versionReads = fixture.Backend.VersionReadCount;

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.RollbackAsync(
            applied.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token));

        Assert.Equal(ErrorCodes.RollbackConflict, error.Code);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(new IntegerPreference(2048), fixture.Backend.Get("up_limit"));
        Assert.Equal(versionReads + 1, fixture.Backend.VersionReadCount);
        var persisted = (await fixture.Store.ReadAsync(applied.CycleId))!;
        Assert.Equal(ApplyStatus.Unverified, persisted.ApplyStatus);
        Assert.Equal("recoveryRequired", persisted.OperationStage);
        Assert.Equal(new IntegerPreference(2048), persisted.ObservedReadback["up_limit"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredReadbackPersistsHistoricalResults(bool alreadyRestored)
    {
        await using var fixture = await Fixture.CreateAsync();
        var applied = await fixture.ApplyAsync();
        var card = new ResultCard("measured", ResultCategory.Limitations, ResultKind.Limitation,
            "results.limitations", new("results.limitations", new Dictionary<string, MessageParameter>()),
            Evidence.Observed, Severity.Info, ResultContext.CurrentExperiment, applied.CycleId, null,
            new LimitationContent(["TEST_LIMITATION"]));
        await fixture.Store.SaveAsync(applied with { Experiment = applied.Experiment with { Results = [card] } });
        if (alreadyRestored)
            foreach (var pair in applied.Original) fixture.Backend.Set(pair.Key, pair.Value);
        var confirmation = await fixture.ConfirmAsync("Rollback");
        var restored = await fixture.Coordinator.RollbackAsync(applied.CycleId, fixture.Plan.Id,
            fixture.Plan.Revision, confirmation.Token);
        Assert.Equal(ApplyStatus.Reverted, restored.ApplyStatus);
        Assert.Equal(ResultContext.Historical, Assert.Single(restored.Experiment.Results).Context);
        Assert.Equal(ResultContext.Historical, Assert.Single((await fixture.Store.ReadAsync(applied.CycleId))!.Experiment.Results).Context);
        Assert.Equal(alreadyRestored ? 1 : 2, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task ExpiredAndStaleConfirmationsDoNotPostAndConsumedConfirmationCannotPostAgain()
    {
        await using (var expired = await Fixture.CreateAsync())
        {
            var confirmation = await expired.ConfirmAsync();
            expired.Clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
            var error = await Assert.ThrowsAsync<QbittorrentException>(() => expired.Coordinator.ApplyAsync(
                expired.Record.CycleId, expired.Plan.Id, expired.Plan.Revision, confirmation.Token));
            Assert.Equal(ErrorCodes.ConfirmationExpired, error.Code);
            Assert.Equal(0, expired.Backend.PostCount);
        }

        await using (var stale = await Fixture.CreateAsync())
        {
            var confirmation = await stale.ConfirmAsync();
            var error = await Assert.ThrowsAsync<QbittorrentException>(() => stale.Coordinator.ApplyAsync(
                stale.Record.CycleId, stale.Plan.Id, stale.Plan.Revision + 1, confirmation.Token));
            Assert.Equal(ErrorCodes.PlanStale, error.Code);
            Assert.Equal(0, stale.Backend.PostCount);
        }

        await using (var reused = await Fixture.CreateAsync())
        {
            var confirmation = await reused.ConfirmAsync();
            var result = await reused.Coordinator.ApplyAsync(reused.Record.CycleId, reused.Plan.Id, reused.Plan.Revision, confirmation.Token);
            Assert.Equal(ApplyStatus.Verified, result.ApplyStatus);
            var error = await Assert.ThrowsAsync<QbittorrentException>(() => reused.Coordinator.ApplyAsync(
                reused.Record.CycleId, reused.Plan.Id, reused.Plan.Revision, confirmation.Token));
            Assert.Equal(ErrorCodes.RecoveryRequired, error.Code);
            Assert.Equal(1, reused.Backend.PostCount);
        }
    }

    [Fact]
    public async Task CancellationBeforeApplyDoesNotPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var confirmation = await fixture.ConfirmAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.ApplyAsync(
            fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token, cancellation.Token));

        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.Equal(ApplyStatus.NotApplied, (await fixture.Store.ReadAsync(fixture.Record.CycleId))!.ApplyStatus);
    }

    [Fact]
    public async Task ConcurrentSecondApplyIsRejectedWhileFirstPostIsPending()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.BlockPost = true;
        var confirmation = await fixture.ConfirmAsync();
        var first = fixture.Coordinator.ApplyAsync(fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token);
        try
        {
            await fixture.Backend.PostStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ApplyAsync(
                fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token));
            Assert.Equal(ErrorCodes.OperationConflict, error.Code);
        }
        finally { fixture.Backend.ReleasePost.TrySetResult(); }

        Assert.Equal(ApplyStatus.Verified, (await first).ApplyStatus);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task KeepChangesCompletesCycleAndRetainsOriginalWithoutAnotherPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var applied = await fixture.ApplyAsync();

        var kept = await fixture.Coordinator.KeepChangesAsync(applied.CycleId);

        Assert.Equal(ApplyStatus.Verified, kept.ApplyStatus);
        Assert.Equal("completed", kept.OperationStage);
        Assert.Equal(fixture.Plan.Original, kept.Original);
        Assert.Equal(fixture.Plan.Proposed, kept.IntendedApplied);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task BackupPersistenceFailurePreventsPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var confirmation = await fixture.ConfirmAsync();
        FileStream? locked = null;
        fixture.Backend.BeforePreferences = count =>
        {
            if (count == 2)
                locked = new FileStream(fixture.RecordPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Task.CompletedTask;
        };

        QbittorrentException error;
        try
        {
            error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ApplyAsync(
                fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token));
        }
        finally { locked?.Dispose(); }

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
        var persisted = (await fixture.Store.ReadAsync(fixture.Record.CycleId))!;
        Assert.Equal(ApplyStatus.NotApplied, persisted.ApplyStatus);
        Assert.Empty(persisted.Original);
    }

    [Fact]
    public async Task VerifiedPersistenceFailureNeverReportsVerifiedAndRetainsPendingBackup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var confirmation = await fixture.ConfirmAsync();
        FileStream? locked = null;
        fixture.Backend.BeforePost = () =>
        {
            locked = new FileStream(fixture.RecordPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Task.CompletedTask;
        };

        QbittorrentException error;
        try
        {
            error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ApplyAsync(
                fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision, confirmation.Token));
        }
        finally { locked?.Dispose(); }

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(fixture.Plan.Proposed, fixture.Backend.Snapshot()
            .Where(pair => fixture.Plan.Proposed.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        var persisted = (await fixture.Store.ReadAsync(fixture.Record.CycleId))!;
        Assert.Equal(ApplyStatus.Pending, persisted.ApplyStatus);
        Assert.Equal("applying", persisted.OperationStage);
        Assert.Equal(fixture.Plan.Original, persisted.Original);
    }

    [Fact]
    public async Task CallerCancellationDuringPostIsDeferredThroughReadback()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.BlockPost = true;
        var confirmation = await fixture.ConfirmAsync();
        using var cancellation = new CancellationTokenSource();
        var apply = fixture.Coordinator.ApplyAsync(fixture.Record.CycleId, fixture.Plan.Id, fixture.Plan.Revision,
            confirmation.Token, cancellation.Token);
        await fixture.Backend.PostStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        fixture.Backend.ReleasePost.TrySetResult();
        var result = await apply;

        Assert.Equal(ApplyStatus.Verified, result.ApplyStatus);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task FullBaselineDriftOnUnchangedKeyBetweenReadsPreventsPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var key = fixture.Backend.Keys.First(key => !fixture.Plan.Original.ContainsKey(key) && !fixture.Plan.Proposed.ContainsKey(key));
        var before = fixture.Backend.Get(key);
        PreferenceValue drift = before switch
        {
            IntegerPreference integer => new IntegerPreference(integer.Value == 0 ? 1 : integer.Value - 1),
            BooleanPreference boolean => new BooleanPreference(!boolean.Value),
            StringPreference text => new StringPreference(text.Value + "-changed"),
            _ => throw new InvalidOperationException("Unexpected mock preference type")
        };
        fixture.Backend.BeforePreferences = count =>
        {
            if (count == 3) fixture.Backend.Set(key, drift);
            return Task.CompletedTask;
        };

        var result = await fixture.ApplyAsync();

        Assert.Equal(ApplyStatus.Reverted, result.ApplyStatus);
        Assert.Equal("rolledBack", result.OperationStage);
        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.Equal(drift, fixture.Backend.Get(key));
        Assert.Equal(fixture.Plan.Original, result.Original);
        Assert.Equal(fixture.Plan.Original, result.ObservedReadback);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const string QbVersion = "5.1.0";
        private const string ApiVersion = "2.11.0";
        private const string LibtorrentVersion = "2.0.11";
        private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public readonly ManualClock Clock;
        public readonly Backend Backend;
        public readonly CycleStore Store;
        public readonly string Root;
        public readonly PlanReview Review;
        public readonly Plan Plan;
        public readonly CycleRecord Record;
        public QbittorrentSession Session { get; private set; }
        public MutationCoordinator Coordinator { get; private set; }

        private Fixture(Backend backend, CycleStore store, string root, PlanReview review, Plan plan, CycleRecord record,
            QbittorrentSession session, MutationCoordinator coordinator, ManualClock clock)
        {
            Backend = backend; Store = store; Root = root; Review = review; Plan = plan; Record = record;
            Session = session; Coordinator = coordinator; Clock = clock;
        }
        public string RecordPath => Path.Combine(Root, Record.CycleId.ToString("N") + ".json");

        public static async Task<Fixture> CreateAsync(bool includeContext = true)
        {
            var current = ReadPreferences();
            current["up_limit"] = new IntegerPreference(1234);
            var backend = new Backend(current);
            var root = Path.Combine(ProjectRoot(), ".cache", "tests", "mutation-coordinator", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var store = new CycleStore(root);
            var clock = new ManualClock(Start);
            var sessionId = Guid.NewGuid();
            var session = await ConnectAsync(backend);
            var inputs = Inputs();
            var plan = PlanBuilder.Build(sessionId, 3, Start, inputs, current, 2,
                [new("wg0", "wg0"), new("eth0", "Ethernet")], current, []);
            Assert.True(plan.Applicable);
            var review = new PlanReview(sessionId);
            review.Replace(plan);
            plan = review.Approve(plan.Id, plan.Revision);
            var baselineSamples = Enumerable.Range(0, 30).Select(i => Sample(i * 1000, Start)).ToArray();
            var baseline = MeasurementAnalysis.Analyze(Guid.NewGuid(), MeasurementKind.Baseline, Start,
                MetricScope.Session, baselineSamples);
            Assert.Equal(MeasurementStatus.Valid, baseline.Status);
            var target = new TargetIdentity(session.Endpoint.AbsoluteUri, QbVersion, ApiVersion, LibtorrentVersion);
            var experiment = new ExperimentSummary(null, inputs, null, baseline, null, plan, []);
            var cycleId = Guid.NewGuid();
            experiment = experiment with { CycleId = cycleId };
            var record = new CycleRecord(Protocol.SchemaVersion, MeasurementAnalysis.Version, cycleId, Start, Start,
                target, inputs, plan, new Dictionary<string, PreferenceValue>(), new Dictionary<string, PreferenceValue>(),
                new Dictionary<string, PreferenceValue>(), "planReady", ApplyStatus.NotApplied, experiment, baselineSamples, [])
            {
                BaselineContext = new(current, PlanBuilder.FingerprintPreferences(current),
                    [new string('a', 40)], [new(new string('a', 40), "downloading", .1)])
            };
            if (!includeContext) record = record with { BaselineContext = null };
            await store.SaveAsync(record);
            var coordinator = CreateCoordinator(session, sessionId, review, store, clock);
            return new Fixture(backend, store, root, review, plan, record, session, coordinator, clock);
        }

        public async Task ReconnectAsync()
        {
            Coordinator.Dispose();
            Session.Dispose();
            Session = await ConnectAsync(Backend);
            Coordinator = CreateCoordinator(Session, Plan.TargetSessionId, Review, Store, Clock);
        }

        public async Task<CycleRecord> ApplyAsync()
        {
            var confirmation = await ConfirmAsync();
            return await Coordinator.ApplyAsync(Record.CycleId, Plan.Id, Plan.Revision, confirmation.Token);
        }

        public Task<ConfirmationSummary> ConfirmAsync(string action = "ApplyPlan") =>
            Coordinator.RequestConfirmationAsync(Record.CycleId, Plan.Id, Plan.Revision, action);

        public ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            Session.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async Task<QbittorrentSession> ConnectAsync(Backend backend) =>
            await QbittorrentSession.ConnectAsync(new("http://127.0.0.1:8080", new BypassAuthentication()), default,
                new ApiHandler(backend));

        private static MutationCoordinator CreateCoordinator(QbittorrentSession session, Guid sessionId,
            PlanReview review, CycleStore store, TimeProvider clock) => new(session, sessionId, review, store,
                (_, _) => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask, clock);

        private static MeasurementSample Sample(double elapsed, DateTimeOffset start)
        {
            var at = start.AddMilliseconds(elapsed);
            return new(elapsed, MeasurementStatus.Sampling, [], new FreshReading(10, at), new FreshReading(2, at),
                new FreshReading(10, at), new FreshReading(2, at), new FreshReading(3, at), new FreshReading(4, at), 1, 0, 0);
        }

        private static DraftInputs Inputs() => new(
            new(500, 100, ConnectionType.Fiber, true, "wg0", true, InputSource.Manual, InputSource.Manual),
            new(StorageType.Nvme, 16, 8, false, 0, InputSource.Manual),
            new(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), 55000);

        private static Dictionary<string, PreferenceValue> ReadPreferences()
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRoot(),
                "tests-contract/fixtures/legacy/settings-payloads.json")));
            return Compatibility.ReadPreferences(doc.RootElement.GetProperty("2.0.11").GetProperty("payload"), 2)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        private static string ProjectRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan amount) => Now += amount;
    }

    private sealed class Backend(IReadOnlyDictionary<string, PreferenceValue> preferences)
    {
        private readonly object sync = new();
        private readonly Dictionary<string, PreferenceValue> values = preferences.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);
        public bool ApplyPosts { get; set; } = true;
        public bool LosePostResponse { get; set; }
        public bool BlockPost { get; set; }
        public Func<Task>? BeforePost { get; set; }
        public Func<int, Task>? BeforePreferences { get; set; }
        public TaskCompletionSource PostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CountPost() => Interlocked.Increment(ref postCount);
        private int preferenceReadCount;
        private int versionReadCount;
        public int CountPreferenceRead() => Interlocked.Increment(ref preferenceReadCount);
        public int CountVersionRead() => Interlocked.Increment(ref versionReadCount);
        public int VersionReadCount => Volatile.Read(ref versionReadCount);
        public string[] Keys { get { lock (sync) return values.Keys.ToArray(); } }
        public string PreferencesJson()
        {
            lock (sync) return JsonSerializer.Serialize(values, Protocol.Json);
        }
        public PreferenceValue Get(string key) { lock (sync) return values[key]; }
        public void Set(string key, PreferenceValue value) { lock (sync) values[key] = value; }
        public Dictionary<string, PreferenceValue> Snapshot() { lock (sync) return new(values, StringComparer.Ordinal); }
        public void ApplyJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            lock (sync)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    values[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.True or JsonValueKind.False => new BooleanPreference(property.Value.GetBoolean()),
                        JsonValueKind.Number => new IntegerPreference(property.Value.GetInt32()),
                        JsonValueKind.String => new StringPreference(property.Value.GetString()!),
                        _ => throw new InvalidOperationException("Unexpected mock preference value")
                    };
            }
        }
    }

    private sealed class ApiHandler(Backend backend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Method == HttpMethod.Get && endpoint == "version") backend.CountVersionRead();
            if (request.Method == HttpMethod.Post && endpoint == "setPreferences")
            {
                backend.CountPost();
                if (backend.BeforePost is { } check) await check();
                backend.PostStarted.TrySetResult();
                if (backend.BlockPost) await backend.ReleasePost.Task.WaitAsync(token);
                if (backend.ApplyPosts)
                {
                    var form = await request.Content!.ReadAsStringAsync(token);
                    var decoded = WebUtility.UrlDecode(form);
                    if (!decoded.StartsWith("json=", StringComparison.Ordinal)) throw new InvalidOperationException("Malformed mock form");
                    backend.ApplyJson(decoded[5..]);
                }
                if (backend.LosePostResponse) throw new HttpRequestException("synthetic lost POST response");
                return Text("");
            }
            if (request.Method == HttpMethod.Get && endpoint == "preferences")
            {
                var read = backend.CountPreferenceRead();
                if (backend.BeforePreferences is { } beforePreferences) await beforePreferences(read);
            }
            var body = endpoint switch
            {
                "version" => "5.1.0",
                "webapiVersion" => "2.11.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => backend.PreferencesJson(),
                _ => throw new InvalidOperationException($"Unexpected API endpoint {endpoint}")
            };
            return Text(body);
        }

        private static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK)
            { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
