using System.Net;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public class ConnectionBridgeTests
{
    [Fact]
    public async Task BriefTelemetryStateLockWaitsButSecondUserCommandConflicts()
    {
        using var bridge = new BridgeDispatcher(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid()));
        var stateGate = (SemaphoreSlim)typeof(BridgeDispatcher).GetField("gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        await stateGate.WaitAsync();
        var first = bridge.DispatchAsync(Request("Initialize", new InitializePayload(1)), default);
        try
        {
            Assert.False(first.IsCompleted);
            var second = Read(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(1)), default));
            Assert.Equal(ErrorCodes.OperationConflict, second.Error!.Code);
        }
        finally { stateGate.Release(); }
        Assert.True(Read(await first.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
    }

    [Fact]
    public async Task EmptyHistoryReadsOfflineWithoutCreatingStateAndMissingCycleIsBackupError()
    {
        var root = Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root);
        var json = await bridge.DispatchAsync(Request("ListHistory", new ListHistoryPayload(null, 50)), default);
        var history = JsonSerializer.Deserialize<CommandReply<HistoryPage>>(json, Protocol.Json)!;
        Assert.True(history.Ok); Assert.Empty(history.Data!.Items); Assert.Null(history.Data.NextCursor);
        Assert.False(Directory.Exists(root));
        var missing = Read(await bridge.DispatchAsync(Request("ReadCycle", new ReadCyclePayload(Guid.NewGuid())), default));
        Assert.Equal(ErrorCodes.BackupInvalid, missing.Error!.Code);
        Assert.Equal("errors.backupInvalid", missing.Error.MessageKey);
        Assert.False(Directory.Exists(root));
    }
    private sealed class Api : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Segments.Last() != "info") Calls++;
            var body = request.RequestUri!.Segments.Last() switch {
                "version" => "v5.2.0", "webapiVersion" => "2.15.0", "buildInfo" => """{"libtorrent":"2.0.11"}""",
                "preferences" => """{"up_limit":0,"dl_limit":0,"max_connec":500,"max_connec_per_torrent":100,"dht":true,"pex":true,"lsd":true,"encryption":0}""",
                "info" => """{"dl_info_speed":123,"up_info_speed":45,"dht_nodes":7}""",
                "networkInterfaceList" => """[{"name":"Ethernet","value":"eth0"}]""",
                _ => throw new InvalidOperationException("Unexpected write") };
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
    private static string Request(string command, object payload, long? revision = null, Guid? session = null, Guid? request = null) =>
        JsonSerializer.Serialize(new { protocolVersion = 1, requestId = request ?? Guid.NewGuid(), command,
            targetSessionId = session, expectedRevision = revision, payload }, Protocol.Json);
    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    [Fact]
    public async Task PreviewRequiresCurrentSessionAndCannotBeApprovedWithoutBaseline()
    {
        using var bridge = new BridgeDispatcher(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid()),
            (p,t) => QbittorrentSession.ConnectAsync(p,t,new Api()));
        var connected = Read(await bridge.DispatchAsync(Request("Connect", new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), 0), default));
        // Use the same validated input contract as the calculator parity suite.
        var inputs = JsonSerializer.Deserialize<DraftInputs>("""
            {"network":{"downloadMbps":100,"uploadMbps":50,"connectionType":"fiber","useVpn":false,"vpnInterface":"","ispThrottling":false,"downloadSource":"manual","uploadSource":"manual"},
             "hardware":{"ramGiB":16,"cpuCores":8,"isHybridCpu":false,"performanceCores":0,"storageType":"ssdSata","source":"manual"},
             "usage":{"trackerType":"public","userRole":"leecher","environment":"system"},"proposedPort":null}
            """, Protocol.Json)!;
        var preview = Read(await bridge.DispatchAsync(Request("BuildPlan", new BuildPlanPayload(inputs, []), 1, connected.Data!.Target!.SessionId), default));
        Assert.True(preview.Ok, preview.Error?.Code);
        var plan = preview.Data!.Experiment!.Plan!;
        Assert.True(plan.PreviewOnly); Assert.False(plan.Approved); Assert.False(plan.Applicable);
        var reload = Read(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(1)), default));
        Assert.Equal(plan.Id, reload.Data!.Experiment!.Plan!.Id);
        var approved = Read(await bridge.DispatchAsync(Request("AcceptPlan", new AcceptPlanPayload(plan.Id, plan.Revision), preview.Revision, connected.Data.Target.SessionId), default));
        Assert.False(approved.Ok);
        var stale = Read(await bridge.DispatchAsync(Request("BuildPlan", new BuildPlanPayload(inputs, []), 1, connected.Data.Target.SessionId), default));
        Assert.Equal(ErrorCodes.PlanStale, stale.Error!.Code);
    }

    [Fact]
    public async Task ConnectionIsReadOnlyIdempotentAndReloadRestoresIdentity()
    {
        var api = new Api();
        var root = Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root, (p,t) => QbittorrentSession.ConnectAsync(p,t,api));
        var request = Request("Connect", new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), 0);
        var originalReply = await bridge.DispatchAsync(request, default);
        var connected = Read(originalReply);
        Assert.True(connected.Ok); Assert.Equal(ConnectionState.Validated, connected.Data!.Connection);
        Assert.True(connected.Data.Target!.IsLocal); Assert.Equal(4, api.Calls);
        Assert.Equal(originalReply, await bridge.DispatchAsync(request, default));
        Assert.Equal(4, api.Calls);
        var reload = Read(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(1)), default));
        Assert.Equal(connected.Data.Target, reload.Data!.Target);
        Assert.False(Directory.Exists(root));
        var stale = Read(await bridge.DispatchAsync(Request("Disconnect", new EmptyPayload(), 1, Guid.NewGuid()), default));
        Assert.Equal(ErrorCodes.SessionStale, stale.Error!.Code);
        var disconnected = Read(await bridge.DispatchAsync(Request("Disconnect", new EmptyPayload(), 1, connected.Data.Target.SessionId), default));
        Assert.True(disconnected.Ok); Assert.Null(disconnected.Data!.Target); Assert.Equal(2, disconnected.Revision);
    }

    [Fact]
    public async Task FailedReconnectInvalidatesPreviousTargetAndNeverPersistsCredentials()
    {
        var attempts = 0;
        var root = Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root, (p,t) => ++attempts == 1
            ? QbittorrentSession.ConnectAsync(p,t,new Api())
            : Task.FromException<QbittorrentSession>(new QbittorrentException(ErrorCodes.AuthenticationFailed)));
        await bridge.DispatchAsync(Request("Connect", new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), 0), default);
        var failed = await bridge.DispatchAsync(Request("Connect", new ConnectPayload("http://127.0.0.1:54322", new PasswordAuthentication("mock", "never-save-this")), 1), default);
        Assert.DoesNotContain("never-save-this", failed);
        Assert.Equal(ErrorCodes.AuthenticationFailed, Read(failed).Error!.Code);
        var reload = Read(await bridge.DispatchAsync(Request("Initialize", new InitializePayload(1)), default));
        Assert.Null(reload.Data!.Target); Assert.Equal(ConnectionState.Disconnected, reload.Data.Connection);
        Assert.False(Directory.Exists(root));
    }
}
