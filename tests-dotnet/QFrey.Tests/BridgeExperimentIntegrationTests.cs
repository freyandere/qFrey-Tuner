using System.Net;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class BridgeExperimentIntegrationTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task DispatchRunsBaselineApplyAfterComparisonAndRollbackOnMockOnlyTarget()
    {
        var root = NewRoot();
        var clock = new SyntheticClock();
        var api = new ExperimentApi();
        var events = Channel.CreateUnbounded<SnapshotEvent>();
        using var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api, clock), clock: clock);
        bridge.SnapshotPublished += message =>
        {
            var item = JsonSerializer.Deserialize<SnapshotEvent>(message, Protocol.Json);
            if (item is not null) events.Writer.TryWrite(item);
            return Task.CompletedTask;
        };

        try
        {
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload("http://127.0.0.1:54321", new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var initial = connected.Data!;
            var sessionId = initial.Target!.SessionId;
            var inputs = TestInputs();
            var built = Read(await bridge.DispatchAsync(Request("BuildPlan", new BuildPlanPayload(inputs, []), sessionId, initial.Revision), default));
            Assert.True(built.Ok, built.Error?.Code);

            var workload = new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [Hash]);
            var baselineAccepted = await StartMeasurement(bridge, MeasurementKind.Baseline, workload, sessionId, built.Revision);
            var baselineSnapshot = await CompleteMeasurement(events.Reader, clock, baselineAccepted.OperationId, 70);
            var baselineCycleId = baselineSnapshot.Experiment!.CycleId!.Value;
            var baselineCycle = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(baselineCycleId);
            Assert.NotNull(baselineCycle);
            Assert.Equal(MeasurementStatus.Valid, baselineCycle!.Experiment.Baseline!.Status);
            Assert.Equal(70, baselineCycle.BaselineSamples.Length);
            Assert.NotNull(baselineCycle.BaselineContext);
            Assert.True(baselineCycle.Plan.Applicable);
            Assert.NotEmpty(baselineCycle.Plan.Proposed);

            var approved = Read(await bridge.DispatchAsync(Request("AcceptPlan",
                new AcceptPlanPayload(baselineCycle.Plan.Id, baselineCycle.Plan.Revision), sessionId, baselineSnapshot.Revision), default));
            Assert.True(approved.Ok, approved.Error?.Code);
            Assert.True(approved.Data!.Experiment!.Plan!.Approved);

            var applyConfirmationReply = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("ApplyPlan", baselineCycle.Plan.Id, baselineCycleId, null), sessionId, approved.Revision), default));
            Assert.True(applyConfirmationReply.Ok, applyConfirmationReply.Error?.Code);
            var applyToken = Assert.Single(applyConfirmationReply.Data!.Confirmations!);
            Assert.Equal("ApplyPlan", applyToken.ActionId);
            Assert.Equal(baselineCycle.Plan.Revision, applyToken.Revision);

            var applyAccepted = await StartMutation(bridge, "ApplyPlan",
                new ApplyPlanPayload(baselineCycle.Plan.Id, baselineCycle.Plan.Revision, applyToken.Token), sessionId, applyConfirmationReply.Revision);
            var applied = await CompleteOperation(events.Reader, applyAccepted.OperationId);
            Assert.Equal(ApplyStatus.Verified, applied.ApplyStatus);
            Assert.Equal(ExperimentPhase.AppliedVerified, applied.Phase);
            var appliedCycle = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(baselineCycleId);
            Assert.Equal(ApplyStatus.Verified, appliedCycle!.ApplyStatus);
            Assert.True(api.WriteCalls >= 1);
            Assert.Equal(appliedCycle.IntendedApplied.OrderBy(x => x.Key).ToArray(),
                appliedCycle.ObservedReadback.OrderBy(x => x.Key).ToArray());

            var afterAccepted = await StartMeasurement(bridge, MeasurementKind.After,
                appliedCycle.Experiment.Workload!.Reference, sessionId, applied.Revision);
            var afterSnapshot = await CompleteMeasurement(events.Reader, clock, afterAccepted.OperationId, 70);
            var afterCycle = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(baselineCycleId);
            Assert.NotNull(afterCycle);
            Assert.Equal(ApplyStatus.Verified, afterCycle!.ApplyStatus);
            Assert.Equal("afterReady", afterCycle.OperationStage);
            Assert.Equal(MeasurementStatus.Valid, afterCycle.Experiment.After!.Status);
            Assert.Equal(70, afterCycle.AfterSamples.Length);
            Assert.Equal(2, afterCycle.Experiment.Results.Length);
            Assert.All(afterCycle.Experiment.Results, card =>
            {
                Assert.Equal(ResultKind.MetricComparison, card.Kind);
                Assert.Equal(ResultContext.CurrentExperiment, card.Context);
                var comparison = Assert.IsType<ComparisonContent>(card.Content).Comparison;
                Assert.NotEqual(Verdict.NotComparable, comparison.Verdict);
            });
            Assert.Equal(ExperimentPhase.AfterReady, afterSnapshot.Phase);

            var rollbackConfirmationReply = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("Rollback", afterCycle.Plan.Id, baselineCycleId, null), sessionId, afterSnapshot.Revision), default));
            Assert.True(rollbackConfirmationReply.Ok, rollbackConfirmationReply.Error?.Code);
            var rollbackToken = Assert.Single(rollbackConfirmationReply.Data!.Confirmations!);
            Assert.Equal("Rollback", rollbackToken.ActionId);
            var rollbackAccepted = await StartMutation(bridge, "Rollback",
                new RollbackPayload(baselineCycleId, afterCycle.Plan.Revision, rollbackToken.Token), sessionId, rollbackConfirmationReply.Revision);
            var reverted = await CompleteOperation(events.Reader, rollbackAccepted.OperationId);
            Assert.Equal(ApplyStatus.Reverted, reverted.ApplyStatus);
            Assert.Equal(ExperimentPhase.RolledBack, reverted.Phase);
            var revertedCycle = await new CycleStore(Path.Combine(root, "cycles")).ReadAsync(baselineCycleId);
            Assert.Equal(ApplyStatus.Reverted, revertedCycle!.ApplyStatus);
            Assert.Equal("rolledBack", revertedCycle.OperationStage);
            Assert.NotNull(revertedCycle.Experiment.After);
            Assert.Equal(2, api.WriteCalls);
            Assert.All(api.Calls, call => Assert.True(call.Method == HttpMethod.Get || call.Method == HttpMethod.Post));
            Assert.Equal(2, api.Calls.Count(call => call.Method == HttpMethod.Post));
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    private static async Task<AcceptedOperation> StartMeasurement(BridgeDispatcher bridge, MeasurementKind kind,
        WorkloadReference workload, Guid sessionId, long revision)
    {
        var reply = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await bridge.DispatchAsync(Request("StartMeasurement",
            new StartMeasurementPayload(kind, workload), sessionId, revision), default), Protocol.Json)!;
        Assert.True(reply.Ok, reply.Error?.Code);
        return reply.Data!;
    }

    private static async Task<AcceptedOperation> StartMutation(BridgeDispatcher bridge, string command, object payload,
        Guid sessionId, long revision)
    {
        var reply = JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(await bridge.DispatchAsync(Request(command,
            payload, sessionId, revision), default), Protocol.Json)!;
        Assert.True(reply.Ok, reply.Error?.Code);
        return reply.Data!;
    }

    private static async Task<AppSnapshot> CompleteMeasurement(ChannelReader<SnapshotEvent> reader, SyntheticClock clock,
        Guid operationId, int expectedSamples)
    {
        var count = 0;
        while (count < expectedSamples)
        {
            var item = await ReadEvent(reader);
            if (item.OperationId != operationId || item.Snapshot.ActiveOperation is null) continue;
            count++;
            if (count < expectedSamples) clock.Advance(TimeSpan.FromSeconds(1));
        }
        return await CompleteOperation(reader, operationId);
    }

    private static async Task<AppSnapshot> CompleteOperation(ChannelReader<SnapshotEvent> reader, Guid operationId)
    {
        while (true)
        {
            var item = await ReadEvent(reader);
            if (item.OperationId == operationId && item.Snapshot.ActiveOperation is null) return item.Snapshot;
        }
    }

    private static async Task<SnapshotEvent> ReadEvent(ChannelReader<SnapshotEvent> reader) =>
        await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    private static DraftInputs TestInputs() => new(
        new NetworkInputs(900, 60, ConnectionType.Fiber, false, "", false, InputSource.Manual, InputSource.Manual),
        new HardwareInputs(StorageType.Nvme, 16, 8, false, 8, InputSource.Manual),
        new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);

    private static string Request(string command, object payload, Guid? sessionId = null, long? revision = null) =>
        JsonSerializer.Serialize(new { protocolVersion = Protocol.Version, requestId = Guid.NewGuid(), command,
            targetSessionId = sessionId, expectedRevision = revision, payload }, Protocol.Json);
    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static string NewRoot()
    {
        var parent = Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-experiment-integration");
        Directory.CreateDirectory(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-experiment-integration"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the dedicated integration-test cache.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private sealed class ExperimentApi : HttpMessageHandler
    {
        private readonly object sync = new();
        private JsonObject preferences = ReadFixture();
        private int writes;
        public int WriteCalls => Volatile.Read(ref writes);
        public ConcurrentQueue<(HttpMethod Method, string Path)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Enqueue((request.Method, path));
            var route = string.Join('/', path.Trim('/').Split('/').TakeLast(2));
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref writes);
                if (route != "app/setPreferences") throw new InvalidOperationException("Unexpected write endpoint: " + route);
                var form = await request.Content!.ReadAsStringAsync(token);
                var encoded = form.Split('&').Single(part => part.StartsWith("json=", StringComparison.Ordinal))[5..];
                var json = Uri.UnescapeDataString(encoded.Replace('+', ' '));
                var values = JsonNode.Parse(json)!.AsObject();
                lock (sync) foreach (var pair in values) preferences[pair.Key] = pair.Value?.DeepClone();
                return Response("");
            }
            var body = route switch
            {
                "app/version" => "v5.2.0",
                "app/webapiVersion" => "2.15.0",
                "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => ReadPreferences(),
                "app/networkInterfaceList" => "[{\"value\":\"eth0\",\"name\":\"Ethernet\"}]",
                "transfer/info" => TransferInfo(),
                "torrents/info" => TorrentInfo(),
                _ => throw new InvalidOperationException("Unexpected mock API route: " + route)
            };
            return Response(body);
        }

        private string ReadPreferences() { lock (sync) return preferences.ToJsonString(); }
        private string TransferInfo() => JsonSerializer.Serialize(new
        { dl_info_speed = 125000, up_info_speed = 20000, dht_nodes = 7, use_alt_speed_limits = false });
        private string TorrentInfo() => "[{\"hash\":\"" + Hash + "\",\"state\":\"downloading\",\"progress\":0.2,\"dlspeed\":125000,\"upspeed\":20000,\"num_seeds\":8,\"num_leechs\":12,\"name\":\"synthetic payload\",\"total_size\":1048576,\"save_path\":\"/mock/downloads\"}]";
        private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static JsonObject ReadFixture()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRoot(),
                "tests-contract/fixtures/legacy/settings-payloads.json")));
            var result = JsonNode.Parse(document.RootElement.GetProperty("2.0.11").GetProperty("payload").GetRawText())!.AsObject();
            result["scheduler_enabled"] = false;
            return result;
        }
    }

    private sealed class SyntheticClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<SyntheticTimer> timers = [];
        private long timestamp;
        private DateTimeOffset now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return timestamp; }
        public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SyntheticTimer(callback, state);
            lock (sync) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            SyntheticTimer[] active;
            lock (sync) { timestamp += duration.Ticks; now += duration; active = timers.ToArray(); }
            foreach (var timer in active) timer.Fire();
        }
        private sealed class SyntheticTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool active;
            public bool Change(TimeSpan dueTime, TimeSpan period) { active = dueTime != Timeout.InfiniteTimeSpan; return true; }
            public void Fire() { if (active) callback(state); }
            public void Dispose() => active = false;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
