using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class BridgeMeasurementTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Preferences = "{\"up_limit\":0,\"dl_limit\":0,\"max_connec\":500,\"max_connec_per_torrent\":100,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0,\"scheduler_enabled\":false}";

    [Fact]
    public async Task ValidBaselinePersistsContextAndKeepsEventSequenceAcrossCollectorRestart()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi();
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        var root = Path.Combine(Path.GetTempPath(), "bridge-measurement-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root, (p, t) => QbittorrentSession.ConnectAsync(p, t, api, clock), clock: clock);
        bridge.SnapshotPublished += json =>
        {
            var parsed = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (parsed is not null) events.Writer.TryWrite(parsed);
            return Task.CompletedTask;
        };

        var connected = await ConnectWithDraft(bridge);
        var accepted = await Start(bridge, connected.Target!.SessionId, connected.Revision);

        var operationEvents = 0;
        SnapshotEvent? completed = null;
        while (operationEvents < 70)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId != accepted.OperationId) continue;
            operationEvents++;
            if (operationEvents < 70) clock.Advance(TimeSpan.FromSeconds(1));
        }
        while (completed is null)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId == accepted.OperationId && item.Snapshot.ActiveOperation is null) completed = item;
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        SnapshotEvent? resumedDashboard = null;
        while (resumedDashboard is null)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId is null) resumedDashboard = item;
        }

        var historyJson = await bridge.DispatchAsync(Request("ListHistory", new ListHistoryPayload(null, 10)), default);
        var history = JsonSerializer.Deserialize<CommandReply<HistoryPage>>(historyJson, Protocol.Json)!;
        Assert.True(history.Ok, history.Error?.Code);
        var cycleId = Assert.Single(history.Data!.Items).CycleId;
        var cycleJson = await bridge.DispatchAsync(Request("ReadCycle", new ReadCyclePayload(cycleId)), default);
        var cycle = JsonSerializer.Deserialize<CommandReply<ExperimentSummary>>(cycleJson, Protocol.Json)!;

        Assert.True(cycle.Ok, cycle.Error?.Code);
        Assert.Equal(MeasurementStatus.Valid, cycle.Data!.Baseline!.Status);
        Assert.Equal(60, cycle.Data.Baseline.SampleCount);
        Assert.Equal(70, (await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(cycleId))!.BaselineSamples.Length);
        var persisted = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(cycleId);
        Assert.NotNull(persisted!.BaselineContext);
        Assert.Equal(new[] { Hash }, persisted.BaselineContext!.SelectedTorrents.Select(torrent => torrent.Hash).ToArray());
        Assert.True(persisted.Plan.BaselineFingerprint.Length > 0);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
        Assert.Equal(0, api.WriteCalls);
        Assert.True(resumedDashboard.Sequence > completed!.Sequence);
    }

    [Fact]
    public async Task MeasurementIsAcceptedAsOperationBlocksConflictingCommandsAndCanBeCancelled()
    {
        var api = new MeasurementApi(blockThirdPreferencesRead: true);
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        var root = Path.Combine(Path.GetTempPath(), "bridge-measurement-cancel-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root, (p, t) => QbittorrentSession.ConnectAsync(p, t, api));
        bridge.SnapshotPublished += json =>
        {
            var parsed = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (parsed is not null) events.Writer.TryWrite(parsed);
            return Task.CompletedTask;
        };

        var connected = await ConnectWithDraft(bridge);
        var accepted = await Start(bridge, connected.Target!.SessionId, connected.Revision);
        await api.MeasurementSnapshotBlocked.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var conflict = Read(await bridge.DispatchAsync(Request("BuildPlan", new BuildPlanPayload(Draft(), []),
            accepted.Revision, connected.Target.SessionId), default));
        var historyJson = await bridge.DispatchAsync(Request("ListHistory", new ListHistoryPayload(null, 10)), default);
        var history = JsonSerializer.Deserialize<CommandReply<HistoryPage>>(historyJson, Protocol.Json)!;
        Assert.Equal(ErrorCodes.OperationConflict, conflict.Error!.Code);
        Assert.True(history.Ok, history.Error?.Code);

        var theme = Read(await bridge.DispatchAsync(Request("SetUiPreferences",
            new SetUiPreferencesPayload("en-US", ThemePreference.Dark), accepted.Revision), default));
        Assert.True(theme.Ok, theme.Error?.Code);

        var cancelled = Read(await bridge.DispatchAsync(Request("CancelOperation", new CancelOperationPayload(accepted.OperationId),
            theme.Revision, connected.Target.SessionId), default));
        Assert.True(cancelled.Ok, cancelled.Error?.Code);
        Assert.Equal("cancelling", cancelled.Data!.ActiveOperation!.Stage);
        SnapshotEvent? completion = null;
        while (completion is null)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId == accepted.OperationId && item.Snapshot.ActiveOperation is null) completion = item;
        }

        Assert.Equal(0, api.WriteCalls);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
        Assert.Empty((await new CycleStore(Path.Combine(root, "cycles")).ListAsync(null, 10)).Items);
    }

    [Fact]
    public async Task CancelAfterValidRunnerBeforeCycleSavePersistsCancelledRawEvidenceWithoutAuthority()
    {
        var clock = new ManualTimeProvider();
        var api = new MeasurementApi(blockSecondInterfaceRead: true);
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        var root = Path.Combine(Path.GetTempPath(), "bridge-measurement-boundary-cancel-" + Guid.NewGuid());
        using var bridge = new BridgeDispatcher(root, (p, t) => QbittorrentSession.ConnectAsync(p, t, api, clock), clock: clock);
        bridge.SnapshotPublished += json =>
        {
            var parsed = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (parsed is not null) events.Writer.TryWrite(parsed);
            return Task.CompletedTask;
        };

        var connected = await ConnectWithDraft(bridge);
        var accepted = await Start(bridge, connected.Target!.SessionId, connected.Revision);
        var samples = 0;
        while (samples < 70)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId != accepted.OperationId || item.Snapshot.ActiveOperation is null) continue;
            samples++;
            if (samples < 70) clock.Advance(TimeSpan.FromSeconds(1));
        }

        await api.InterfaceReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = Read(await bridge.DispatchAsync(Request("CancelOperation", new CancelOperationPayload(accepted.OperationId),
            accepted.Revision, connected.Target.SessionId), default));
        Assert.True(cancel.Ok, cancel.Error?.Code);

        SnapshotEvent? completion = null;
        while (completion is null)
        {
            var item = await ReadEvent(events.Reader);
            if (item.OperationId == accepted.OperationId && item.Snapshot.ActiveOperation is null) completion = item;
        }

        var history = await new CycleStore(Path.Combine(root, "cycles")).ListAsync(null, 10);
        var cycleId = Assert.Single(history.Items).CycleId;
        var cycle = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(cycleId);
        Assert.NotNull(cycle);
        Assert.Equal(MeasurementStatus.Cancelled, cycle!.Experiment.Baseline!.Status);
        Assert.Null(cycle.BaselineContext);
        Assert.Contains(cycle.BaselineSamples, sample => sample.Status == MeasurementStatus.Cancelled);
        Assert.Equal(0, api.WriteCalls);
        Assert.All(api.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
    }

    private static async Task<AppSnapshot> ConnectWithDraft(BridgeDispatcher bridge)
    {
        var connected = Read(await bridge.DispatchAsync(Request("Connect",
            new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), 0), default));
        Assert.True(connected.Ok, connected.Error?.Code);
        var snapshot = connected.Data!;
        var planned = Read(await bridge.DispatchAsync(Request("BuildPlan", new BuildPlanPayload(Draft(), []),
            snapshot.Revision, snapshot.Target!.SessionId), default));
        Assert.True(planned.Ok, planned.Error?.Code);
        return planned.Data!;
    }

    private static async Task<AcceptedOperation> Start(BridgeDispatcher bridge, Guid sessionId, long revision)
    {
        var reply = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(
            await bridge.DispatchAsync(Request("StartMeasurement",
                new StartMeasurementPayload(MeasurementKind.Baseline,
                    new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [Hash])), revision, sessionId), default), Protocol.Json)!;
        Assert.True(reply.Ok, reply.Error?.Code);
        Assert.NotNull(reply.Data);
        return reply.Data!;
    }

    private static DraftInputs Draft() => new(
        new NetworkInputs(100, 50, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
        new HardwareInputs(StorageType.SsdSata, 16, 8, false, 0, InputSource.Manual),
        new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);

    private static string Request(string command, object payload, long? revision = null, Guid? session = null) =>
        JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid(), command,
            targetSessionId = session, expectedRevision = revision, payload }, Protocol.Json);
    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static async Task<SnapshotEvent> ReadEvent(ChannelReader<SnapshotEvent> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class MeasurementApi(bool blockThirdPreferencesRead = false, bool blockSecondInterfaceRead = false) : HttpMessageHandler
    {
        private int preferenceReads;
        private int interfaceReads;
        public ConcurrentQueue<(HttpMethod Method, string Path)> Calls { get; } = new();
        public int WriteCalls { get; private set; }
        public TaskCompletionSource MeasurementSnapshotBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InterfaceReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Enqueue((request.Method, request.RequestUri!.AbsolutePath));
            if (request.Method != HttpMethod.Get) WriteCalls++;
            var route = string.Join('/', request.RequestUri.AbsolutePath.Trim('/').Split('/').TakeLast(2));
            if (blockThirdPreferencesRead && route == "app/preferences" && Interlocked.Increment(ref preferenceReads) == 3)
            {
                MeasurementSnapshotBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (blockSecondInterfaceRead && route == "app/networkInterfaceList" && Interlocked.Increment(ref interfaceReads) == 2)
            {
                InterfaceReadBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var body = route switch
            {
                "app/version" => "v5.2.0",
                "app/webapiVersion" => "2.15.0",
                "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => Preferences,
                "app/networkInterfaceList" => "[]",
                "transfer/info" => "{\"dl_info_speed\":100,\"up_info_speed\":20,\"dht_nodes\":7,\"use_alt_speed_limits\":false}",
                "torrents/info" => "[{\"hash\":\"" + Hash + "\",\"state\":\"downloading\",\"progress\":0.2,\"dlspeed\":100,\"upspeed\":20,\"num_seeds\":2,\"num_leechs\":3,\"name\":\"payload\",\"total_size\":1024,\"save_path\":\"/downloads\"}]",
                _ => throw new InvalidOperationException("Unexpected API call: " + route)
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long timestamp;
        private DateTimeOffset now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return timestamp; }
        public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (sync) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            ManualTimer[] current;
            lock (sync) { timestamp += duration.Ticks; now += duration; current = timers.ToArray(); }
            foreach (var timer in current) timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool active;
            public bool Change(TimeSpan dueTime, TimeSpan period) { active = dueTime != Timeout.InfiniteTimeSpan; return true; }
            public void Fire() { if (active) callback(state); }
            public void Dispose() => active = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
