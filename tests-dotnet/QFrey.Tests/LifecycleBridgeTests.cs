using System.Net;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Platform;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using QFrey.Desktop.Platform;
using Xunit;

namespace QFrey.Tests;

public sealed class LifecycleBridgeTests
{
    [Theory]
    [InlineData("wrongToken", ErrorCodes.ConfirmationExpired)]
    [InlineData("wrongAction", ErrorCodes.ConfirmationExpired)]
    [InlineData("wrongSession", ErrorCodes.SessionStale)]
    [InlineData("wrongRevision", ErrorCodes.PlanStale)]
    [InlineData("expired", ErrorCodes.ConfirmationExpired)]
    [InlineData("ownerChanged", ErrorCodes.OwnerUnavailable)]
    public async Task InvalidConsentNeverShutsDown(string change, string expected)
    {
        var clock = new TestClock();
        var api = new Api();
        var owner = new ProcessIdentity(123, 456);
        using var bridge = NewBridge(api, clock, () => owner);
        var connected = await Connect(bridge);
        var confirmed = await Confirm(bridge, connected);
        Assert.Equal(0, api.Shutdowns);
        if (change == "expired") clock.Now = clock.Now.AddMinutes(3);
        if (change == "ownerChanged") owner = owner with { StartTimeUtcTicks = 789 };
        var token = change == "wrongToken" ? "missing" : Assert.Single(confirmed.Data!.Confirmations).Token;
        var failed = Read(await bridge.DispatchAsync(Request(change == "wrongAction" ? "RestartTarget" : "StopTarget",
            new LifecyclePayload(token), change == "wrongSession" ? Guid.NewGuid() : connected.Data!.Target!.SessionId,
            change == "wrongRevision" ? confirmed.Revision + 1 : confirmed.Revision), default));
        Assert.False(failed.Ok);
        Assert.Equal(expected, failed.Error!.Code);
        if (change == "wrongAction")
        {
            var reused = Read(await bridge.DispatchAsync(Request("StopTarget", new LifecyclePayload(token),
                connected.Data!.Target!.SessionId, confirmed.Revision), default));
            Assert.Equal(ErrorCodes.ConfirmationExpired, reused.Error!.Code);
        }
        Assert.Equal(0, api.Shutdowns);
        await bridge.ShutdownAsync();
    }

    [Theory]
    [InlineData(TargetLifecycleStatus.Stopped)]
    [InlineData(TargetLifecycleStatus.TimedOut)]
    [InlineData(TargetLifecycleStatus.ShutdownFailed)]
    public async Task LifecycleFinishesWithOnePostAndInvalidatesOldConnection(TargetLifecycleStatus status)
    {
        var api = new Api();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = NewBridge(api, new TestClock(), () => new(123, 456), async (_, _, shutdown, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            await shutdown(token);
            return new(status, "testLifecycleResult");
        });
        var completed = new TaskCompletionSource<AppSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.SnapshotPublished += json =>
        {
            var snapshot = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json)!;
            if (snapshot.OperationId is not null && snapshot.Snapshot.ActiveOperation is null) completed.TrySetResult(snapshot.Snapshot);
            return Task.CompletedTask;
        };
        var connected = await Connect(bridge);
        var confirmed = await Confirm(bridge, connected);
        var token = Assert.Single(confirmed.Data!.Confirmations).Token;
        var request = Request("StopTarget", new LifecyclePayload(token), connected.Data!.Target!.SessionId, confirmed.Revision);
        var acceptedJson = await bridge.DispatchAsync(request, default);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(acceptedJson, Protocol.Json)!;
        Assert.True(accepted.Ok, accepted.Error?.Code);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(acceptedJson, await bridge.DispatchAsync(request, default));
        var conflict = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
            new RequestConfirmationPayload("StopTarget", null, null, null), connected.Data.Target.SessionId, accepted.Revision), default));
        Assert.Equal(ErrorCodes.OperationConflict, conflict.Error!.Code);
        release.SetResult();
        var final = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(final.Target);
        Assert.Equal(ConnectionState.Disconnected, final.Connection);
        Assert.Null(final.ActiveOperation);
        Assert.Empty(final.Confirmations);
        Assert.Equal(1, api.Shutdowns);
        if (status != TargetLifecycleStatus.Stopped) Assert.Contains(final.AvailableActions, a => a.Id == "operation.error");
        await bridge.ShutdownAsync();
    }

    [Fact]
    public async Task OwnerChangeImmediatelyBeforeShutdownBlocksPost()
    {
        var api = new Api();
        var owner = new ProcessIdentity(123, 456);
        using var bridge = NewBridge(api, new TestClock(), () => owner, async (_, _, shutdown, token) =>
        {
            owner = owner with { StartTimeUtcTicks = 789 };
            await shutdown(token);
            return new(TargetLifecycleStatus.Stopped, "unexpected");
        });
        var completed = new TaskCompletionSource<AppSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bridge.SnapshotPublished += json =>
        {
            var snapshot = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json)!;
            if (snapshot.OperationId is not null && snapshot.Snapshot.ActiveOperation is null) completed.TrySetResult(snapshot.Snapshot);
            return Task.CompletedTask;
        };
        var connected = await Connect(bridge);
        var confirmed = await Confirm(bridge, connected);
        var accepted = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await bridge.DispatchAsync(Request("StopTarget",
            new LifecyclePayload(Assert.Single(confirmed.Data!.Confirmations).Token), connected.Data!.Target!.SessionId, confirmed.Revision), default), Protocol.Json)!;
        Assert.True(accepted.Ok);
        var final = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(final.AvailableActions, action => action.ReasonCode == ErrorCodes.OwnerUnavailable);
        Assert.True(await bridge.ShutdownAsync());
        Assert.Equal(0, api.Shutdowns);
    }

    private static BridgeDispatcher NewBridge(Api api, TimeProvider clock, Func<ProcessIdentity> owner,
        Func<string, bool, Func<CancellationToken, Task>, CancellationToken, Task<TargetLifecycleResult>>? runner = null) =>
        new(Path.Combine(Path.GetTempPath(), "lifecycle-tests-" + Guid.NewGuid()),
            (p, t) => QbittorrentSession.ConnectAsync(p, t, api), clock: clock,
            runLifecycle: runner ?? (async (_, _, shutdown, token) => { await shutdown(token); return new(TargetLifecycleStatus.Stopped, "test"); }),
            lifecycleOwner: (_, _) => new(OwnerLookupStatus.Available, owner(), null, false, null));

    private static async Task<Reply> Connect(BridgeDispatcher bridge) => Read(await bridge.DispatchAsync(
        Request("Connect", new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), null, 0), default));
    private static async Task<Reply> Confirm(BridgeDispatcher bridge, Reply connected)
    {
        Assert.True(connected.Ok, connected.Error?.Code);
        var reply = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
            new RequestConfirmationPayload("StopTarget", null, null, null), connected.Data!.Target!.SessionId, connected.Revision), default));
        Assert.True(reply.Ok, reply.Error?.Code);
        return reply;
    }
    private static string Request(string command, object payload, Guid? session, long revision) => JsonSerializer.Serialize(
        new { protocolVersion = 1, requestId = Guid.NewGuid(), command, payload, targetSessionId = session, expectedRevision = revision }, Protocol.Json);
    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Api : HttpMessageHandler
    {
        public int Shutdowns;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = request.RequestUri!.Segments.Last();
            if (endpoint == "shutdown") { Assert.Equal(HttpMethod.Post, request.Method); Interlocked.Increment(ref Shutdowns); }
            else Assert.Equal(HttpMethod.Get, request.Method);
            var body = endpoint switch
            {
                "version" => "v5.2.0", "webapiVersion" => "2.15.0", "buildInfo" => """{"libtorrent":"2.0.11"}""",
                "preferences" => """{"up_limit":0,"dl_limit":0,"max_connec":500,"max_connec_per_torrent":100,"dht":true,"pex":true,"lsd":true,"encryption":0}""",
                "info" => """{"dl_info_speed":123,"up_info_speed":45,"dht_nodes":7}""",
                "shutdown" => "", _ => throw new InvalidOperationException("Unexpected API call")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
