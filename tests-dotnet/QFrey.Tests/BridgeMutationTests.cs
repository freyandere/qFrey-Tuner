using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class BridgeMutationTests
{
    [Theory]
    [InlineData(ApplyStatus.Pending)]
    [InlineData(ApplyStatus.Verified)]
    public async Task ConnectReconcilesPersistedMutationAndBlocksNewWrites(ApplyStatus initialStatus)
    {
        var root = NewRoot();
        var target = new TargetIdentity("http://127.0.0.1:8080/", "v5.1.0", "2.11.0", "2.0.11");
        var record = RecoveryRecord(Guid.NewGuid(), target, initialStatus);
        var api = new MutationApi(maxConnections: 500);
        using var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api));

        try
        {
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(record);
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload(target.Endpoint, new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var snapshot = Assert.IsType<AppSnapshot>(connected.Data);
            Assert.Equal(ApplyStatus.Verified, snapshot.ApplyStatus);
            Assert.Equal(ExperimentPhase.RecoveryRequired, snapshot.Phase);
            Assert.NotNull(snapshot.Experiment?.Plan);
            Assert.False(snapshot.Experiment!.Plan!.Approved);
            Assert.Equal(snapshot.Target!.SessionId, snapshot.Experiment.Plan.TargetSessionId);

            var build = Read(await bridge.DispatchAsync(Request("BuildPlan",
                new BuildPlanPayload(record.Inputs, []), snapshot.Target.SessionId, connected.Revision), default));
            Assert.False(build.Ok);
            Assert.Equal(ErrorCodes.RecoveryRequired, build.Error!.Code);

            var baseline = Read(await bridge.DispatchAsync(Request("StartMeasurement",
                new StartMeasurementPayload(MeasurementKind.Baseline,
                    new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [new string('a', 40)])),
                snapshot.Target.SessionId, connected.Revision), default));
            Assert.False(baseline.Ok);
            Assert.Equal(ErrorCodes.RecoveryRequired, baseline.Error!.Code);

            var apply = Read(await bridge.DispatchAsync(Request("ApplyPlan",
                new ApplyPlanPayload(record.Plan.Id, record.Plan.Revision, "not-a-confirmation"),
                snapshot.Target.SessionId, connected.Revision), default));
            Assert.False(apply.Ok);
            Assert.Equal(ErrorCodes.RecoveryRequired, apply.Error!.Code);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task ConnectWithVersionMismatchKeepsRecoveryBlockedWithoutPosting()
    {
        var root = NewRoot();
        var target = new TargetIdentity("http://127.0.0.1:8080/", "v5.1.0", "2.11.0", "2.0.11");
        var record = RecoveryRecord(Guid.NewGuid(), target, ApplyStatus.Verified);
        var api = new MutationApi(maxConnections: 500, qbVersion: "5.2.0", apiVersion: "2.15.0");
        using var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api));

        try
        {
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(record);
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload(target.Endpoint, new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var snapshot = Assert.IsType<AppSnapshot>(connected.Data);
            Assert.Equal(ApplyStatus.Verified, snapshot.ApplyStatus);
            Assert.Contains(snapshot.AvailableActions,
                action => action.Id == "operation.error" && action.ReasonCode == ErrorCodes.RecoveryRequired);
            Assert.False(snapshot.Experiment!.Plan!.Approved);
            Assert.Equal(snapshot.Target!.SessionId, snapshot.Experiment.Plan.TargetSessionId);

            var build = Read(await bridge.DispatchAsync(Request("BuildPlan",
                new BuildPlanPayload(record.Inputs, []), snapshot.Target.SessionId, connected.Revision), default));
            Assert.False(build.Ok);
            Assert.Equal(ErrorCodes.RecoveryRequired, build.Error!.Code);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task ConnectReadbackAtOriginalMarksRecordRevertedAndAllowsNewPlan()
    {
        var root = NewRoot();
        var target = new TargetIdentity("http://127.0.0.1:8080/", "v5.1.0", "2.11.0", "2.0.11");
        var record = RecoveryRecord(Guid.NewGuid(), target, ApplyStatus.Pending);
        var api = new MutationApi(maxConnections: 1000);
        using var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api));

        try
        {
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(record);
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload(target.Endpoint, new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var snapshot = Assert.IsType<AppSnapshot>(connected.Data);
            Assert.Equal(ApplyStatus.Reverted, snapshot.ApplyStatus);
            Assert.Equal(ExperimentPhase.RolledBack, snapshot.Phase);

            var build = Read(await bridge.DispatchAsync(Request("BuildPlan",
                new BuildPlanPayload(record.Inputs, []), snapshot.Target!.SessionId, connected.Revision), default));
            Assert.True(build.Ok, build.Error?.Code);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task DispatchRejectsStaleTargetRevisionMissingBaselineAndInvalidRollbackConsentWithoutPost()
    {
        var root = NewRoot();
        var api = new MutationApi();
        using var bridge = new BridgeDispatcher(root,
            (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api));

        try
        {
            var connected = Read(await bridge.DispatchAsync(Request("Connect",
                new ConnectPayload("http://127.0.0.1:8080", new BypassAuthentication()), revision: 0), default));
            Assert.True(connected.Ok, connected.Error?.Code);
            var snapshot = Assert.IsType<AppSnapshot>(connected.Data);
            var sessionId = snapshot.Target!.SessionId;

            var staleTarget = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("Rollback", null, Guid.NewGuid(), null), sessionId: Guid.NewGuid(),
                revision: connected.Revision), default));
            Assert.False(staleTarget.Ok);
            Assert.Equal(ErrorCodes.SessionStale, staleTarget.Error!.Code);

            var staleRevision = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("Rollback", null, Guid.NewGuid(), null), sessionId,
                revision: connected.Revision - 1), default));
            Assert.False(staleRevision.Ok);
            Assert.Equal(ErrorCodes.PlanStale, staleRevision.Error!.Code);

            var noBaseline = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("ApplyPlan", null, null, null), sessionId,
                revision: connected.Revision), default));
            Assert.False(noBaseline.Ok);
            Assert.Equal(ErrorCodes.BackupInvalid, noBaseline.Error!.Code);

            var target = new TargetIdentity(snapshot.Target.Endpoint, snapshot.Target.QbittorrentVersion,
                snapshot.Target.ApiVersion, snapshot.Target.LibtorrentVersion);
            var record = CycleStoreTests.Record(Guid.NewGuid(), target);
            record = record with
            {
                Original = record.Plan.Original,
                IntendedApplied = record.Plan.Proposed,
                ObservedReadback = record.Plan.Proposed,
                ApplyStatus = ApplyStatus.Verified,
                OperationStage = "appliedVerified"
            };
            await new CycleStore(Path.Combine(root, "cycles")).SaveAsync(record);

            var confirmation = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("Rollback", record.Plan.Id, record.CycleId, null), sessionId,
                revision: connected.Revision), default));
            Assert.True(confirmation.Ok, confirmation.Error?.Code);
            var issued = Assert.Single(Assert.IsType<AppSnapshot>(confirmation.Data).Confirmations);
            Assert.Equal("Rollback", issued.ActionId);

            var stalePlan = Read(await bridge.DispatchAsync(Request("Rollback",
                new RollbackPayload(record.CycleId, record.Plan.Revision + 1, issued.Token), sessionId,
                revision: confirmation.Revision), default));
            Assert.False(stalePlan.Ok);
            Assert.Equal(ErrorCodes.PlanStale, stalePlan.Error!.Code);

            var invalidConsent = Read(await bridge.DispatchAsync(Request("Rollback",
                new RollbackPayload(record.CycleId, record.Plan.Revision, "invalid-consent"), sessionId,
                revision: confirmation.Revision), default));
            Assert.False(invalidConsent.Ok);
            Assert.Equal(ErrorCodes.ConfirmationExpired, invalidConsent.Error!.Code);
            Assert.Equal(0, api.WriteCalls);
        }
        finally
        {
            bridge.Dispose();
            DeleteTestRoot(root);
        }
    }

    private static string Request(string command, object payload, Guid? sessionId = null, long? revision = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = Protocol.Version,
            requestId = Guid.NewGuid(),
            command,
            targetSessionId = sessionId,
            expectedRevision = revision,
            payload
        }, Protocol.Json);

    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;

    private static CycleRecord RecoveryRecord(Guid cycleId, TargetIdentity target, ApplyStatus status)
    {
        var record = CycleStoreTests.Record(cycleId, target);
        var original = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal)
            { ["max_connec"] = new IntegerPreference(1000) };
        var intended = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal)
            { ["max_connec"] = new IntegerPreference(500) };
        var plan = record.Plan with { Original = original, Proposed = intended, Approved = true };
        return record with
        {
            Plan = plan,
            Original = original,
            IntendedApplied = intended,
            ObservedReadback = status == ApplyStatus.Verified ? intended : new Dictionary<string, PreferenceValue>(),
            ApplyStatus = status,
            OperationStage = status == ApplyStatus.Pending ? "applying" : "appliedVerified",
            Experiment = record.Experiment with { Plan = plan }
        };
    }

    private static string NewRoot()
    {
        var root = Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-mutations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestRoot(string root)
    {
        var fullPath = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(ProjectRoot(), ".cache", "tests", "bridge-mutations"));
        if (!string.Equals(Path.GetDirectoryName(fullPath), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the test cache.");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private sealed class MutationApi : HttpMessageHandler
    {
        private readonly string preferences;
        private readonly string qbVersion;
        private readonly string apiVersion;
        private int writeCalls;
        public int WriteCalls => Volatile.Read(ref writeCalls);

        public MutationApi(int? maxConnections = null, string qbVersion = "5.1.0", string apiVersion = "2.11.0")
        {
            this.qbVersion = qbVersion;
            this.apiVersion = apiVersion;
            var json = JsonNode.Parse(ReadPreferences())!.AsObject();
            if (maxConnections is int value) json["max_connec"] = value;
            preferences = json.ToJsonString();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref writeCalls);
            var route = string.Join('/', request.RequestUri!.AbsolutePath.Trim('/').Split('/').TakeLast(2));
            var body = route switch
            {
                "app/version" => "v" + qbVersion,
                "app/webapiVersion" => apiVersion,
                "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => preferences,
                "app/networkInterfaceList" => "[]",
                "transfer/info" => "{}",
                "torrents/info" => "[]",
                _ => throw new InvalidOperationException("Unexpected mock API route: " + route)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static string ReadPreferences()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProjectRoot(),
                "tests-contract/fixtures/legacy/settings-payloads.json")));
            return document.RootElement.GetProperty("2.0.11").GetProperty("payload").GetRawText();
        }
    }
}
