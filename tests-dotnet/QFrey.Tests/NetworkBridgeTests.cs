using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class NetworkBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PartialProbePreservesUnknownAndRejectsInvalidRates()
    {
        var mapped = BridgeDispatcher.MapNetworkProbe(Result(
            new(NetworkProbeStatus.Measured, 1000, 1000, 1000, null),
            new(NetworkProbeStatus.Unknown, null, null, null, "UPLOAD_UNSUPPORTED")));
        Assert.Equal(1000, mapped.DownloadBytesPerSecond);
        Assert.Null(mapped.UploadBytesPerSecond);
        Assert.Equal(Now, mapped.MeasuredUtc);
        Assert.Equal(["UPLOAD_UNSUPPORTED"], mapped.ReasonCodes);
        var invalid = BridgeDispatcher.MapNetworkProbe(Result(
            new(NetworkProbeStatus.Measured, double.NaN, null, null, null),
            new(NetworkProbeStatus.Measured, 0, null, null, null)));
        Assert.Null(invalid.DownloadBytesPerSecond);
        Assert.Null(invalid.UploadBytesPerSecond);
        Assert.Equal(["NETWORK_RATE_INVALID"], invalid.ReasonCodes);
    }

    [Fact]
    public async Task OnlyConfirmedStartRunsProbeAndConsentCannotBeReused()
    {
        using var fixture = new Fixture();
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var missing = Read(await fixture.Bridge.DispatchAsync(Request("RunNetworkTest", new NetworkTestPayload("missing"),
            target, connected.Revision), default));
        Assert.Equal(ErrorCodes.ConfirmationExpired, missing.Error!.Code);
        Assert.Equal(0, fixture.ProbeCalls);
        var confirmation = await fixture.Confirm(target, connected.Revision);
        Assert.Equal(0, fixture.ProbeCalls);
        var consent = Assert.Single(confirmation.Data!.Confirmations);
        Assert.Equal("network.confirmTraffic", consent.Description.Key);
        Assert.Equal(new NumberParameter(220), consent.Description.Parameters["maxPayloadMiB"]);
        Assert.Equal(new NumberParameter(17), consent.Description.Parameters["maxRequests"]);
        var start = Request("RunNetworkTest", new NetworkTestPayload(consent.Token), target, confirmation.Revision);
        var acceptedJson = await fixture.Bridge.DispatchAsync(start, default);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(acceptedJson, Protocol.Json)!;
        Assert.True(accepted.Ok, accepted.Error?.Code);
        Assert.Equal(acceptedJson, await fixture.Bridge.DispatchAsync(start, default));
        var completed = await fixture.Completion(accepted.Data!.OperationId);
        Assert.Equal(1, fixture.ProbeCalls);
        Assert.Equal(1000, completed.Snapshot.NetworkTest!.DownloadBytesPerSecond);
        Assert.Null(completed.Snapshot.NetworkTest.UploadBytesPerSecond);
        var reuse = Read(await fixture.Bridge.DispatchAsync(Request("RunNetworkTest", new NetworkTestPayload(consent.Token),
            target, completed.Revision), default));
        Assert.Equal(ErrorCodes.ConfirmationExpired, reuse.Error!.Code);
        Assert.Equal(1, fixture.ProbeCalls);
        Assert.Equal(0, fixture.Api.PostCalls);
        await fixture.Close();
    }

    [Theory]
    [InlineData("{\"dl_info_speed\":1,\"up_info_speed\":0}", "[]")]
    [InlineData("{\"dl_info_speed\":0}", "[]")]
    [InlineData("{\"dl_info_speed\":0,\"up_info_speed\":0}", "[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"downloading\"}]")]
    [InlineData("{\"dl_info_speed\":0,\"up_info_speed\":0}", "[{\"hash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"futureState\"}]")]
    public async Task TrafficOrUnknownEvidencePreventsPublicProbe(string transfer, string torrents)
    {
        using var fixture = new Fixture();
        fixture.Api.Transfer = transfer;
        fixture.Api.Torrents = torrents;
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var confirmation = await fixture.Confirm(target, connected.Revision);
        var consent = Assert.Single(confirmation.Data!.Confirmations);
        var denied = Read(await fixture.Bridge.DispatchAsync(Request("RunNetworkTest", new NetworkTestPayload(consent.Token),
            target, confirmation.Revision), default));
        Assert.Equal(ErrorCodes.OperationConflict, denied.Error!.Code);
        Assert.Equal(0, fixture.ProbeCalls);
        await fixture.Close();
    }

    [Fact]
    public async Task ExpiredAndChangedRevisionConsentNeverRunsProbe()
    {
        using var fixture = new Fixture();
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var confirmation = await fixture.Confirm(target, connected.Revision);
        fixture.Clock.Now = Now.AddMinutes(3);
        var denied = Read(await fixture.Bridge.DispatchAsync(Request("RunNetworkTest",
            new NetworkTestPayload(Assert.Single(confirmation.Data!.Confirmations).Token), target, confirmation.Revision), default));
        Assert.Equal(ErrorCodes.ConfirmationExpired, denied.Error!.Code);
        fixture.Clock.Now = Now;
        confirmation = await fixture.Confirm(target, denied.Revision);
        var preferences = Read(await fixture.Bridge.DispatchAsync(Request("SetUiPreferences",
            new SetUiPreferencesPayload("en-US", ThemePreference.System), null, confirmation.Revision), default));
        denied = Read(await fixture.Bridge.DispatchAsync(Request("RunNetworkTest",
            new NetworkTestPayload(Assert.Single(confirmation.Data!.Confirmations).Token), target, preferences.Revision), default));
        Assert.Equal(ErrorCodes.ConfirmationExpired, denied.Error!.Code);
        Assert.Equal(0, fixture.ProbeCalls);
        await fixture.Close();
    }

    [Fact]
    public async Task NewTorrentTrafficCancelsProbeBeforeReleasingOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var confirmation = await fixture.Confirm(target, connected.Revision);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await fixture.Bridge.DispatchAsync(
            Request("RunNetworkTest", new NetworkTestPayload(Assert.Single(confirmation.Data!.Confirmations).Token),
                target, confirmation.Revision), default), Protocol.Json)!;
        Assert.True(accepted.Ok, accepted.Error?.Code);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Api.Transfer = "{\"dl_info_speed\":1,\"up_info_speed\":0}";
        var completion = await fixture.Completion(accepted.Data!.OperationId);
        Assert.Equal(["NETWORK_TORRENT_TRAFFIC"], completion.Snapshot.NetworkTest!.ReasonCodes);
        Assert.Null(completion.Snapshot.NetworkTest.DownloadBytesPerSecond);
        Assert.Null(completion.Snapshot.NetworkTest.UploadBytesPerSecond);
        await fixture.Close();
    }

    [Theory]
    [InlineData(false, "NETWORK_TORRENT_TRAFFIC")]
    [InlineData(true, "NETWORK_TRAFFIC_UNKNOWN")]
    public async Task FastProbeRequiresFinalIdleEvidence(bool failedRead, string reason)
    {
        Api api = null!;
        using var fixture = new Fixture(_ =>
        {
            api.Transfer = "{\"dl_info_speed\":1,\"up_info_speed\":0}";
            api.ThrowTransfer = failedRead;
            return Task.FromResult(Result(new(NetworkProbeStatus.Measured, 1000, 1000, 1000, null),
                new(NetworkProbeStatus.Measured, 1000, 1000, 1000, null)));
        });
        api = fixture.Api;
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var confirmation = await fixture.Confirm(target, connected.Revision);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await fixture.Bridge.DispatchAsync(
            Request("RunNetworkTest", new NetworkTestPayload(Assert.Single(confirmation.Data!.Confirmations).Token),
                target, confirmation.Revision), default), Protocol.Json)!;
        Assert.True(accepted.Ok, accepted.Error?.Code);
        var completion = await fixture.Completion(accepted.Data!.OperationId);
        Assert.Equal([reason], completion.Snapshot.NetworkTest!.ReasonCodes);
        Assert.Null(completion.Snapshot.NetworkTest.DownloadBytesPerSecond);
        Assert.Null(completion.Snapshot.NetworkTest.UploadBytesPerSecond);
        Assert.Equal(1, fixture.ProbeCalls);
        await fixture.Close();
    }

    [Fact]
    public async Task CancellationKeepsOperationBusyUntilRunnerStopsAndPublishesUnknown()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await release.Task; }
            throw new InvalidOperationException();
        });
        var connected = await fixture.Connect();
        var target = connected.Data!.Target!.SessionId;
        var confirmation = await fixture.Confirm(target, connected.Revision);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await fixture.Bridge.DispatchAsync(
            Request("RunNetworkTest", new NetworkTestPayload(Assert.Single(confirmation.Data!.Confirmations).Token),
                target, confirmation.Revision), default), Protocol.Json)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = Read(await fixture.Bridge.DispatchAsync(Request("CancelOperation",
            new CancelOperationPayload(accepted.Data!.OperationId), target, accepted.Revision), default));
        Assert.True(cancelled.Ok);
        Assert.NotNull(cancelled.Data!.ActiveOperation);
        var conflict = await fixture.Confirm(target, cancelled.Revision);
        Assert.Equal(ErrorCodes.OperationConflict, conflict.Error!.Code);
        release.SetResult();
        var completion = await fixture.Completion(accepted.Data.OperationId);
        Assert.Null(completion.Snapshot.NetworkTest!.DownloadBytesPerSecond);
        Assert.Null(completion.Snapshot.NetworkTest.UploadBytesPerSecond);
        Assert.Equal(["NETWORK_TEST_CANCELLED"], completion.Snapshot.NetworkTest.ReasonCodes);
        await fixture.Close();
    }

    private static NetworkProbeResult Result(NetworkProbeReading download, NetworkProbeReading upload) =>
        new(Now, "test", "mock", download, upload, NetworkProbe.MaximumTraffic);

    private static string Request(string name, object payload, Guid? target = null, long? revision = null) =>
        JsonSerializer.Serialize(new { protocolVersion = Protocol.Version, requestId = Guid.NewGuid(), command = name,
            targetSessionId = target, expectedRevision = revision, payload }, Protocol.Json);
    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        public readonly Api Api = new();
        public readonly FakeClock Clock = new();
        public readonly BridgeDispatcher Bridge;
        public int ProbeCalls;
        private readonly Channel<SnapshotEvent> events = Channel.CreateUnbounded<SnapshotEvent>();
        public Fixture(Func<CancellationToken, Task<NetworkProbeResult>>? probe = null)
        {
            root = Path.Combine(ProjectRoot(), ".cache", "tests", "network-bridge", Guid.NewGuid().ToString("N"));
            Bridge = new(root, (payload, token) => QbittorrentSession.ConnectAsync(payload, token, Api, Clock), clock: Clock,
                runNetworkProbe: token =>
                {
                    Interlocked.Increment(ref ProbeCalls);
                    return probe?.Invoke(token) ?? Task.FromResult(Result(new(NetworkProbeStatus.Measured, 1000, 1000, 1000, null),
                        new(NetworkProbeStatus.Unknown, null, null, null, "UPLOAD_UNSUPPORTED")));
                });
            Bridge.SnapshotPublished += json =>
            { events.Writer.TryWrite(JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json)!); return Task.CompletedTask; };
        }
        public async Task<Reply> Connect()
        {
            var connected = Read(await Bridge.DispatchAsync(Request("Connect", new ConnectPayload("http://localhost:8080",
                new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            return connected;
        }
        public async Task<Reply> Confirm(Guid target, long revision) => Read(await Bridge.DispatchAsync(Request("RequestConfirmation",
            new RequestConfirmationPayload("RunNetworkTest", null, null, null), target, revision), default));
        public async Task<SnapshotEvent> Completion(Guid id)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (true)
            {
                var snapshot = await events.Reader.ReadAsync(deadline.Token);
                if (snapshot.OperationId == id && snapshot.Snapshot.ActiveOperation is null) return snapshot;
            }
        }
        public Task<bool> Close() => Bridge.ShutdownAsync();
        public void Dispose()
        {
            Bridge.Dispose();
            var full = Path.GetFullPath(root);
            if (Path.GetFileName(Path.GetDirectoryName(full)) != "network-bridge") throw new InvalidOperationException();
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = NetworkBridgeTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Api : HttpMessageHandler
    {
        private static readonly string Preferences = ReadPreferences();
        public string Transfer = "{\"dl_info_speed\":0,\"up_info_speed\":0}";
        public string Torrents = "[]";
        public bool ThrowTransfer;
        public int PostCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post) Interlocked.Increment(ref PostCalls);
            if (ThrowTransfer && request.RequestUri!.AbsolutePath == "/api/v2/transfer/info")
                throw new HttpRequestException("mock transfer unavailable");
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/api/v2/app/version" => "v5.2.0",
                "/api/v2/app/webapiVersion" => "2.15.0",
                "/api/v2/app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "/api/v2/app/preferences" => Preferences,
                "/api/v2/transfer/info" => Transfer,
                "/api/v2/torrents/info" => Torrents,
                _ => "[]"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        private static string ReadPreferences()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRoot(),
                "tests-contract/fixtures/legacy/settings-payloads.json")));
            return document.RootElement.GetProperty("2.0.11").GetProperty("payload").GetRawText();
        }
    }
}
