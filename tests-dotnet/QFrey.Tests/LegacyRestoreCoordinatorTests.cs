using System.Net;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;

public sealed class LegacyRestoreCoordinatorTests
{
    [Fact]
    public async Task ReviewAndConfirmationAreOneUseAndExpireAfterTwoMinutes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backup = fixture.Backup();
        var review = await fixture.Coordinator.ReviewAsync(backup);
        Assert.True(review.CanRestore);
        var confirmation = await fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        var expired = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.RestoreAsync(confirmation.Token));

        Assert.Equal(ErrorCodes.ConfirmationExpired, expired.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task ConfirmationRejectsThirdPartyDriftBeforePost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backup = fixture.Backup();
        var review = await fixture.Coordinator.ReviewAsync(backup);
        var confirmation = await fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint);
        fixture.Backend.Set("max_connec", new IntegerPreference(777));

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.RestoreAsync(confirmation.Token));

        Assert.Equal(ErrorCodes.PreferenceDrift, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.Equal(new IntegerPreference(777), fixture.Backend.Get("max_connec"));
    }

    [Fact]
    public async Task CancellationAfterPostStartsDoesNotInterruptReadback()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var backup = fixture.Backup();
        var review = await fixture.Coordinator.ReviewAsync(backup);
        var confirmation = await fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint);
        fixture.Backend.BeforePost = () => { cancellation.Cancel(); return Task.CompletedTask; };
        var result = await fixture.Coordinator.RestoreAsync(confirmation.Token, cancellation.Token);
        Assert.Equal("verified", result.Status);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(new IntegerPreference(500), fixture.Backend.Get("max_connec"));
        Assert.True(fixture.Session.IsValidated);
    }

    [Fact]
    public async Task FreshVersionGateRunsBeforeReviewAndPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.QbVersion = "5.2.0";

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReviewAsync(fixture.Backup()));

        Assert.Equal(ErrorCodes.SessionStale, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task PendingJournalIsDurableBeforeOnePostAndVerifiedJournalRetainsBackup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backup = fixture.Backup();
        var review = await fixture.Coordinator.ReviewAsync(backup);
        var confirmation = await fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint);
        fixture.Backend.BeforePost = () =>
        {
            Assert.Single(Directory.GetFiles(fixture.JournalRoot, "*.json"));
            return Task.CompletedTask;
        };

        var result = await fixture.Coordinator.RestoreAsync(confirmation.Token);

        Assert.Equal("verified", result.Status);
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Equal(new IntegerPreference(500), fixture.Backend.Get("max_connec"));
        Assert.Equal(new IntegerPreference(500), result.Original["max_connec"]);
        Assert.Equal(new IntegerPreference(800), result.BeforeRestore["max_connec"]);
        var persisted = (await new AtomicJsonStore(fixture.JournalRoot).ReadAsync<LegacyRestoreJournal>(result.RestoreId))!;
        Assert.Equal("verified", persisted.Status);
        Assert.Equal(result.Original, persisted.Original);

        var replay = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.RestoreAsync(confirmation.Token));
        Assert.Equal(ErrorCodes.ConfirmationExpired, replay.Code);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task PersistenceFailurePreventsPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var blockedRoot = Path.Combine(fixture.JournalRoot, "file-is-not-a-directory");
        Directory.CreateDirectory(fixture.JournalRoot);
        File.WriteAllText(blockedRoot, "occupied");
        using var coordinator = new LegacyRestoreCoordinator(fixture.Session, Guid.NewGuid(), blockedRoot, fixture.Clock);
        var backup = fixture.Backup();
        var review = await coordinator.ReviewAsync(backup);
        var confirmation = await coordinator.RequestConfirmationAsync(backup, review.Fingerprint);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => coordinator.RestoreAsync(confirmation.Token));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task LostPostResponseUsesReadbackAndNeverRepeatsPost()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Backend.LosePostResponse = true;
        var backup = fixture.Backup();
        var review = await fixture.Coordinator.ReviewAsync(backup);
        var confirmation = await fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint);

        var result = await fixture.Coordinator.RestoreAsync(confirmation.Token);

        Assert.Equal("verified", result.Status);
        Assert.Equal(new IntegerPreference(500), result.ObservedReadback["max_connec"]);
        Assert.Equal(1, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task LegacyWithoutIntendedValuesCanBeReviewedButCannotReceiveConfirmation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var backup = fixture.Backup(includeIntended: false);
        var review = await fixture.Coordinator.ReviewAsync(backup);

        Assert.False(review.CanRestore);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.RequestConfirmationAsync(backup, review.Fingerprint));
        Assert.Equal(ErrorCodes.PreferenceDrift, error.Code);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Theory]
    [InlineData("pending", 500, "verified")]
    [InlineData("unverified", 500, "verified")]
    [InlineData("pending", 800, "notApplied")]
    [InlineData("unverified", 777, "unverified")]
    [InlineData("verified", 777, "unverified")]
    public async Task ReconciliationReadsActualAndPreservesBackupWithoutPost(string initialStatus, int live, string expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var journal = fixture.Journal(initialStatus);
        var store = new AtomicJsonStore(fixture.JournalRoot);
        await store.WriteAsync(journal.RestoreId, journal);
        fixture.Backend.Set("max_connec", new IntegerPreference(live));

        var result = await fixture.Coordinator.ReconcileAsync(journal.RestoreId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(new IntegerPreference(live), result.ObservedReadback["max_connec"]);
        Assert.Equal(journal.Original, result.Original);
        Assert.Equal(journal.BeforeRestore, result.BeforeRestore);
        Assert.Equal(journal.SourceCycleId, result.SourceCycleId);
        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.Equal(expectedStatus, (await store.ReadAsync<LegacyRestoreJournal>(journal.RestoreId))!.Status);
    }

    [Fact]
    public async Task PartialRestoreReadbackRemainsUnverified()
    {
        await using var fixture = await Fixture.CreateAsync();
        var journal = fixture.Journal("pending") with
        {
            Original = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500), ["encryption"] = new IntegerPreference(0) },
            BeforeRestore = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(800), ["encryption"] = new IntegerPreference(1) }
        };
        await new AtomicJsonStore(fixture.JournalRoot).WriteAsync(journal.RestoreId, journal);
        fixture.Backend.Set("max_connec", new IntegerPreference(500));

        var result = await fixture.Coordinator.ReconcileAsync(journal.RestoreId);

        Assert.Equal("unverified", result.Status);
        Assert.Equal(2, result.ObservedReadback.Count);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("id")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("secret")]
    [InlineData("type")]
    [InlineData("keys")]
    [InlineData("status")]
    [InlineData("verified")]
    [InlineData("timestamp")]
    public async Task MalformedJournalIsRejectedBeforeTargetRead(string defect)
    {
        await using var fixture = await Fixture.CreateAsync();
        var valid = fixture.Journal("pending");
        var invalid = defect switch
        {
            "schema" => valid with { SchemaVersion = 2 },
            "id" => valid with { RestoreId = Guid.NewGuid() },
            "source" => valid with { SourceCycleId = Guid.Empty },
            "target" => valid with { Target = valid.Target with { Endpoint = "http://user:password@127.0.0.1:8080/" } },
            "secret" => valid with { Original = new Dictionary<string, PreferenceValue> { ["proxy_password"] = new StringPreference("secret") } },
            "type" => valid with { Original = new Dictionary<string, PreferenceValue> { ["max_connec"] = new BooleanPreference(true) } },
            "keys" => valid with { BeforeRestore = new Dictionary<string, PreferenceValue>() },
            "status" => valid with { Status = "futureStatus" },
            "verified" => valid with { Status = "verified" },
            "timestamp" => valid with { UpdatedUtc = valid.CreatedUtc.AddMinutes(-1) },
            _ => throw new InvalidOperationException()
        };
        var store = new AtomicJsonStore(fixture.JournalRoot);
        await store.WriteAsync(valid.RestoreId, invalid);
        var beforeReads = fixture.Backend.GetCount;

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReconcileAsync(valid.RestoreId));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(beforeReads, fixture.Backend.GetCount);
        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.Equal(invalid.Status, (await store.ReadAsync<LegacyRestoreJournal>(valid.RestoreId))!.Status);
    }

    [Fact]
    public async Task OtherTargetOrChangedVersionsNeverUpdateJournal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var store = new AtomicJsonStore(fixture.JournalRoot);
        var journal = fixture.Journal("pending") with { Target = fixture.Journal("pending").Target with { Endpoint = "http://127.0.0.1:8081/" } };
        await store.WriteAsync(journal.RestoreId, journal);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReconcileAsync(journal.RestoreId));
        Assert.Equal(ErrorCodes.SessionStale, error.Code);
        Assert.Equal("pending", (await store.ReadAsync<LegacyRestoreJournal>(journal.RestoreId))!.Status);

        journal = fixture.Journal("pending");
        await store.WriteAsync(journal.RestoreId, journal);
        fixture.Backend.QbVersion = "5.2.0";
        error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReconcileAsync(journal.RestoreId));
        Assert.Equal(ErrorCodes.SessionStale, error.Code);
        Assert.Equal("pending", (await store.ReadAsync<LegacyRestoreJournal>(journal.RestoreId))!.Status);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task VersionChangeDuringReadbackCannotVerifyJournal()
    {
        await using var fixture = await Fixture.CreateAsync();
        var journal = fixture.Journal("pending");
        var store = new AtomicJsonStore(fixture.JournalRoot);
        await store.WriteAsync(journal.RestoreId, journal);
        fixture.Backend.Set("max_connec", new IntegerPreference(500));
        fixture.Backend.OnPreferences = () => fixture.Backend.QbVersion = "5.2.0";

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReconcileAsync(journal.RestoreId));

        Assert.Equal(ErrorCodes.SessionStale, error.Code);
        Assert.Equal("pending", (await store.ReadAsync<LegacyRestoreJournal>(journal.RestoreId))!.Status);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task MissingJournalFailsWithoutTargetReads()
    {
        await using var fixture = await Fixture.CreateAsync();
        var beforeReads = fixture.Backend.GetCount;

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => fixture.Coordinator.ReconcileAsync(Guid.NewGuid()));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(beforeReads, fixture.Backend.GetCount);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly Backend Backend;
        public readonly string JournalRoot;
        public readonly ManualClock Clock;
        public readonly QbittorrentSession Session;
        public readonly LegacyRestoreCoordinator Coordinator;

        private Fixture(Backend backend, string root, ManualClock clock, QbittorrentSession session,
            LegacyRestoreCoordinator coordinator)
        { Backend = backend; JournalRoot = root; Clock = clock; Session = session; Coordinator = coordinator; }

        public static async Task<Fixture> CreateAsync()
        {
            var backend = new Backend();
            var root = Path.Combine(ProjectRoot(), ".cache", "tests-dotnet", "legacy-restore", Guid.NewGuid().ToString("N"));
            var clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
            var session = await QbittorrentSession.ConnectAsync(new("http://127.0.0.1:8080", new BypassAuthentication()), default,
                new ApiHandler(backend));
            var coordinator = new LegacyRestoreCoordinator(session, Guid.NewGuid(), root, clock);
            return new(backend, root, clock, session, coordinator);
        }

        public LegacyCycleImport Backup(bool includeIntended = true)
        {
            var document = new Dictionary<string, object?>
            {
                ["host"] = "http://127.0.0.1:8080/", ["qBittorrent"] = "5.1.0", ["api"] = "2.11.0",
                ["libtorrent"] = "2.0.11", ["created_or_updated_utc"] = "2026-10-07T10:00:00Z",
                ["original"] = new Dictionary<string, int> { ["max_connec"] = 500 }
            };
            if (includeIntended) document["applied"] = new Dictionary<string, int> { ["max_connec"] = 800 };
            return LegacyCycleImporter.Import(JsonSerializer.SerializeToUtf8Bytes(document), Guid.NewGuid());
        }

        public LegacyRestoreJournal Journal(string status)
        {
            var original = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500) };
            var before = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(800) };
            return new(1, Guid.NewGuid(), Guid.NewGuid(), Backup().Target!, original, before,
                status == "verified" ? original : new Dictionary<string, PreferenceValue>(), status,
                Clock.GetUtcNow(), Clock.GetUtcNow());
        }

        public ValueTask DisposeAsync()
        {
            Coordinator.Dispose(); Session.Dispose();
            return ValueTask.CompletedTask;
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
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }

    private sealed class Backend
    {
        private readonly object sync = new();
        private readonly Dictionary<string, PreferenceValue> values = new(StringComparer.Ordinal)
        {
            ["up_limit"] = new IntegerPreference(102400), ["dl_limit"] = new IntegerPreference(102400),
            ["max_connec"] = new IntegerPreference(800), ["max_connec_per_torrent"] = new IntegerPreference(100),
            ["encryption"] = new IntegerPreference(1), ["dht"] = new BooleanPreference(true),
            ["pex"] = new BooleanPreference(true), ["lsd"] = new BooleanPreference(false)
        };
        private int postCount;
        private int getCount;
        public string QbVersion { get; set; } = "5.1.0";
        public bool LosePostResponse { get; set; }
        public Func<Task>? BeforePost { get; set; }
        public Action? OnPreferences { get; set; }
        public int PostCount => Volatile.Read(ref postCount);
        public int GetCount => Volatile.Read(ref getCount);
        public void Set(string key, PreferenceValue value) { lock (sync) values[key] = value; }
        public PreferenceValue Get(string key) { lock (sync) return values[key]; }
        private string PreferencesJson() { lock (sync) return JsonSerializer.Serialize(values, Protocol.Json); }
        private void Apply(string json)
        {
            using var document = JsonDocument.Parse(json);
            lock (sync)
                foreach (var property in document.RootElement.EnumerateObject())
                    values[property.Name] = JsonSerializer.Deserialize<PreferenceValue>(property.Value.GetRawText(), Protocol.Json)!;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref getCount);
            var endpoint = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Method == HttpMethod.Post && endpoint == "setPreferences")
            {
                Interlocked.Increment(ref postCount);
                if (BeforePost is { } before) await before();
                var form = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(token));
                if (!form.StartsWith("json=", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid preference form.");
                Apply(form[5..]);
                if (LosePostResponse) throw new HttpRequestException("synthetic lost response");
                return Text("");
            }
            var body = endpoint switch
            {
                "version" => QbVersion,
                "webapiVersion" => "2.11.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => PreferencesJson(),
                _ => throw new InvalidOperationException($"Unexpected endpoint: {endpoint}")
            };
            if (endpoint == "preferences") OnPreferences?.Invoke();
            return Text(body);
        }
        private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK)
            { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private sealed class ApiHandler(Backend backend) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            backend.SendAsync(request, cancellationToken);
    }
}
