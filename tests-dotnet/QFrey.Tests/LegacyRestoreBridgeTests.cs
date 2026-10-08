using System.Net;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class LegacyRestoreBridgeTests
{
    [Fact]
    public async Task ReviewArchivesExactSafeSourceAndBlockedReviewNeverIssuesConsentOrPost()
    {
        await using var fixture = await Fixture.CreateAsync(liveConnections: 777);
        var selection = await fixture.SelectSourceAsync();

        var reply = ReadReply(await fixture.Bridge.DispatchAsync(fixture.Request("RequestConfirmation",
            new RequestConfirmationPayload("RestoreLegacyBackup", null, null, null, SelectionToken: selection.Token), fixture.SessionId), default));

        Assert.True(reply.Ok, $"{reply.Error?.Code} {reply.Error?.MessageKey}");
        Assert.NotNull(reply.Data?.Restore);
        Assert.False(reply.Data!.Restore!.Review.CanRestore);
        Assert.DoesNotContain(reply.Data.Confirmations, item => item.ActionId == "RestoreLegacyBackup");
        Assert.Equal(0, fixture.Backend.PostCount);
        Assert.DoesNotContain(fixture.SourcePath, JsonSerializer.Serialize(reply, Protocol.Json), StringComparison.Ordinal);
        var archived = await new AtomicJsonStore(Path.Combine(fixture.Root, "legacy-sources"))
            .ReadAsync<LegacySourceCopy>(reply.Data.Restore.SourceCycleId);
        Assert.NotNull(archived);
        Assert.Equal(fixture.SourceBytes, archived!.OriginalUtf8Json);
    }

    [Fact]
    public async Task AcceptedRestoreUsesNativeSelectionAndOneVerifiedPostWithoutCreatingCycle()
    {
        await using var fixture = await Fixture.CreateAsync(liveConnections: 800);
        var selection = await fixture.SelectSourceAsync();
        var review = ReadReply(await fixture.Bridge.DispatchAsync(fixture.Request("RequestConfirmation",
            new RequestConfirmationPayload("RestoreLegacyBackup", null, null, null, SelectionToken: selection.Token), fixture.SessionId), default));
        Assert.True(review.Ok, $"{review.Error?.Code} {review.Error?.MessageKey}");
        Assert.True(review.Data!.Restore!.Review.CanRestore);
        var confirmation = Assert.Single(review.Data.Confirmations, item => item.ActionId == "RestoreLegacyBackup");
        Assert.Equal(review.Data.Target!.SessionId, confirmation.TargetSessionId);
        Assert.Equal(review.Data.Revision, confirmation.Revision);
        Assert.DoesNotContain(fixture.SourcePath, JsonSerializer.Serialize(review, Protocol.Json), StringComparison.Ordinal);

        var finished = new TaskCompletionSource<SnapshotEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bridge.SnapshotPublished += message =>
        {
            var value = JsonSerializer.Deserialize<SnapshotEvent>(message, Protocol.Json)!;
            if (value.OperationId is not null && value.Snapshot.ActiveOperation is null) finished.TrySetResult(value);
            return Task.CompletedTask;
        };
        var acceptedJson = await fixture.Bridge.DispatchAsync(fixture.Request("RestoreLegacyBackup",
            new RestoreBackupPayload(selection.Token, confirmation.Token), review.Data.Target.SessionId, review.Data.Revision), default);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(acceptedJson, Protocol.Json)!;
        Assert.True(accepted.Ok);

        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(accepted.Data!.OperationId, final.OperationId);
        Assert.Null(final.Snapshot.ActiveOperation);
        Assert.Equal(new IntegerPreference(500), fixture.Backend.Get("max_connec"));
        Assert.Equal(1, fixture.Backend.PostCount);
        Assert.Empty(Directory.Exists(Path.Combine(fixture.Root, "cycles"))
            ? Directory.GetFiles(Path.Combine(fixture.Root, "cycles"), "*.json") : []);
        var journalFiles = Directory.GetFiles(Path.Combine(fixture.Root, "legacy-restore-journals"), "*.json");
        var journal = await new AtomicJsonStore(Path.Combine(fixture.Root, "legacy-restore-journals"))
            .ReadAsync<LegacyRestoreJournal>(Guid.ParseExact(Path.GetFileNameWithoutExtension(journalFiles.Single()), "N"));
        Assert.Equal("verified", journal!.Status);
    }

    [Fact]
    public async Task ExpiredBridgeConsentCannotStartRestore()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = await Fixture.CreateAsync(liveConnections: 800, clock);
        var selection = await fixture.SelectSourceAsync();
        var review = ReadReply(await fixture.Bridge.DispatchAsync(fixture.Request("RequestConfirmation",
            new RequestConfirmationPayload("RestoreLegacyBackup", null, null, null, SelectionToken: selection.Token), fixture.SessionId), default));
        Assert.True(review.Ok, $"{review.Error?.Code} {review.Error?.MessageKey}");
        var confirmation = Assert.Single(review.Data!.Confirmations, item => item.ActionId == "RestoreLegacyBackup");
        clock.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));

        var response = await fixture.Bridge.DispatchAsync(fixture.Request("RestoreLegacyBackup",
            new RestoreBackupPayload(selection.Token, confirmation.Token), review.Data.Target!.SessionId, review.Data.Revision), default);

        Assert.False(JsonDocument.Parse(response).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Theory]
    [InlineData(500, "http://127.0.0.1:8080/", false, "verified")]
    [InlineData(800, "http://127.0.0.1:8080/", false, "notApplied")]
    [InlineData(700, "http://127.0.0.1:8080/", true, "unverified")]
    [InlineData(800, "http://127.0.0.1:9090/", false, "pending")]
    public async Task RecoveryScannerReconcilesOnlyItsTargetWithoutAnyPost(int liveConnections,
        string endpoint, bool blocked, string expectedStatus)
    {
        await using var fixture = await Fixture.CreateAsync(liveConnections);
        var root = Path.Combine(fixture.Root, "legacy-restore-journals");
        var store = new AtomicJsonStore(root);
        var id = Guid.NewGuid();
        var map = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal) { ["max_connec"] = new IntegerPreference(500) };
        await store.WriteAsync(id, new LegacyRestoreJournal(1, id, Guid.NewGuid(), fixture.TargetIdentity with { Endpoint = endpoint },
            map, new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(800) },
            new Dictionary<string, PreferenceValue>(), "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        Assert.Equal(blocked, await fixture.Bridge.LoadLegacyRestoreRecoveryAsync(default));
        Assert.Equal(expectedStatus, (await store.ReadAsync<LegacyRestoreJournal>(id))!.Status);
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    [Fact]
    public async Task RecoveryScannerRejectsMalformedJournalWithoutPosting()
    {
        await using var fixture = await Fixture.CreateAsync(liveConnections: 800);
        var root = Path.Combine(fixture.Root, "legacy-restore-journals");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, $"{Guid.NewGuid():N}.json"), "{\"schemaVersion\":99}");
        Assert.True(await fixture.Bridge.LoadLegacyRestoreRecoveryAsync(default));
        Assert.Equal(0, fixture.Backend.PostCount);
    }

    private static Reply ReadReply(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required string SourcePath { get; init; }
        public required byte[] SourceBytes { get; init; }
        public required Backend Backend { get; init; }
        public required BridgeDispatcher Bridge { get; init; }
        public required TargetIdentity TargetIdentity { get; init; }
        public Guid SessionId { get; private set; }
        private long revision;

        public static async Task<Fixture> CreateAsync(int liveConnections, TimeProvider? clock = null)
        {
            var root = Path.Combine(ProjectRoot(), ".cache", "tests-dotnet", "legacy-restore-bridge", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "selected backup.json");
            var sourceBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["host"] = "http://127.0.0.1:8080/", ["qBittorrent"] = "5.1.0", ["api"] = "2.11.0",
                ["libtorrent"] = "2.0.11", ["created_or_updated_utc"] = "2026-10-07T10:00:00Z",
                ["original"] = new Dictionary<string, int> { ["max_connec"] = 500 },
                ["applied"] = new Dictionary<string, int> { ["max_connec"] = 800 }
            });
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var backend = new Backend(liveConnections);
            BridgeDispatcher? bridge = null;
            bridge = new BridgeDispatcher(root,
                (payload, token) => QbittorrentSession.ConnectAsync(payload, token, new ApiHandler(backend)),
                clock: clock,
                selectFile: (_, _, _) => Task.FromResult<string?>(sourcePath));
            var fixture = new Fixture { Root = root, SourcePath = sourcePath, SourceBytes = sourceBytes,
                Backend = backend, Bridge = bridge,
                TargetIdentity = new("http://127.0.0.1:8080/", "5.1.0", "2.11.0", "2.0.11") };
            await fixture.InitializeAndConnectAsync();
            return fixture;
        }

        public async Task<NativeSelection> SelectSourceAsync()
        {
            var json = await Bridge.DispatchAsync(Request("SelectNativeFile", new SelectFilePayload("restore")), default);
            var reply = JsonSerializer.Deserialize<CommandReply<NativeSelection?>>(json, Protocol.Json)!;
            Assert.True(reply.Ok);
            return reply.Data!;
        }

        public string Request(string name, object payload, Guid? target = null, long? expectedRevision = null, Guid? id = null) =>
            JsonSerializer.Serialize(new CommandEnvelope(Protocol.Version, id ?? Guid.NewGuid(), name, target,
                expectedRevision ?? revision, JsonSerializer.SerializeToElement(payload, payload.GetType(), Protocol.Json)), Protocol.Json);

        private async Task InitializeAndConnectAsync()
        {
            _ = await Bridge.DispatchAsync(Request("Initialize", new InitializePayload(Protocol.Version)), default);
            var connected = ReadReply(await Bridge.DispatchAsync(Request("Connect",
                new ConnectPayload("http://127.0.0.1:8080", new BypassAuthentication())), default));
            Assert.True(connected.Ok);
            SessionId = connected.Data!.Target!.SessionId;
            revision = connected.Data.Revision;
        }

        public ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
        public void Advance(TimeSpan by) => current += by;
    }

    private sealed class Backend(int liveConnections)
    {
        private readonly object sync = new();
        private readonly Dictionary<string, PreferenceValue> values = new(StringComparer.Ordinal)
        {
            ["up_limit"] = new IntegerPreference(102400), ["dl_limit"] = new IntegerPreference(102400),
            ["max_connec"] = new IntegerPreference(liveConnections), ["max_connec_per_torrent"] = new IntegerPreference(100),
            ["encryption"] = new IntegerPreference(1), ["dht"] = new BooleanPreference(true),
            ["pex"] = new BooleanPreference(true), ["lsd"] = new BooleanPreference(false)
        };
        private int postCount;
        public int PostCount => Volatile.Read(ref postCount);
        public void Set(string key, PreferenceValue value) { lock (sync) values[key] = value; }
        public PreferenceValue Get(string key) { lock (sync) return values[key]; }
        private string PreferencesJson() { lock (sync) return JsonSerializer.Serialize(values, Protocol.Json); }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var endpoint = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (request.Method == HttpMethod.Post && endpoint == "setPreferences")
            {
                Interlocked.Increment(ref postCount);
                var form = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(token));
                using var document = JsonDocument.Parse(form[5..]);
                lock (sync)
                    foreach (var item in document.RootElement.EnumerateObject())
                    {
                        var value = item.Value.ValueKind switch
                        {
                            JsonValueKind.Number => new IntegerPreference(item.Value.GetInt32()) as PreferenceValue,
                            JsonValueKind.True => new BooleanPreference(true),
                            JsonValueKind.False => new BooleanPreference(false),
                            JsonValueKind.String => new StringPreference(item.Value.GetString()!),
                            _ => throw new InvalidOperationException()
                        };
                        values[item.Name] = value!;
                    }
                return Text("");
            }
            var body = endpoint switch
            {
                "version" => "5.1.0",
                "webapiVersion" => "2.11.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => PreferencesJson(),
                "info" => "{\"dl_info_speed\":0,\"up_info_speed\":0,\"dht_nodes\":0,\"use_alt_speed_limits\":false}",
                "torrents" => "[]",
                _ => throw new InvalidOperationException($"Unexpected endpoint: {endpoint}")
            };
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
