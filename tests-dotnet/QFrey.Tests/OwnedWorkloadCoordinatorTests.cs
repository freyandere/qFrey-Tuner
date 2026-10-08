using System.Net;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class OwnedWorkloadCoordinatorTests
{
    private const string Endpoint = "http://127.0.0.1:8080/";
    private const string Name = "ubuntu-22.04.5-desktop-amd64.iso";
    private const string Category = "qfrey-test-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tag = "qfrey-test-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherHash = "1111111111111111111111111111111111111111";
    private static readonly WorkloadCatalogueEntry Catalogue = WorkloadCatalogue.Get("ubuntu");
    private static readonly TargetIdentity Target = new(Endpoint, "v5.2.0", "2.15.0", "2.0.11");
    private static readonly byte[] TorrentBytes = Torrent(Name, Catalogue.SizeBytes);
    private static readonly TorrentMetadataInfo Metadata = TorrentMetadata.Parse(TorrentBytes, Name);

    [Fact]
    public async Task DurableJournalExistsBeforeOneAddAndFreshInventoryVerifiesIdentity()
    {
        var (root, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { DurableJournalRoot = root };
        api.OnAdd = () =>
        {
            var files = Directory.GetFiles(root, "*.json");
            Assert.Single(files);
            using var persisted = JsonDocument.Parse(File.ReadAllBytes(files[0]));
            Assert.Equal(Metadata.V1InfoHash, persisted.RootElement.GetProperty("hash").GetString());
            api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        };
        using var session = await Connect(api);

        var result = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);

        Assert.Equal(OwnedWorkloadPrepareStatus.IdentityVerified, result.Status);
        Assert.Null(result.ReasonCode);
        Assert.Equal(result.JournalId, result.Journal.Id);
        Assert.NotNull(await store.ReadAsync(result.JournalId));
        Assert.Single(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/add", StringComparison.Ordinal));
        Assert.True(api.DurableAtAdd);
        Assert.Contains(api.Calls, call => call.Method == "GET" && call.Path.EndsWith("torrents/info", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsentMismatchFailsBeforeReadingOrWritingTarget()
    {
        var (root, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        using var session = await Connect(api);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => coordinator.PrepareAsync(session,
            Catalogue.Id, "/srv/qfrey-test", "not-the-consent-identity", TorrentBytes, default));

        Assert.Equal(ErrorCodes.InvalidCommand, error.Code);
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST" || call.Path.EndsWith("torrents/info", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(root, "*.json"));
    }

    [Fact]
    public async Task JournalPersistenceFailurePreventsAddPost()
    {
        var root = NewRoot();
        var blocker = Path.Combine(root, "root-is-a-file");
        await File.WriteAllTextAsync(blocker, "block");
        var coordinator = new OwnedWorkloadCoordinator(new OwnedWorkloadStore(blocker), MarkerFactory());
        var api = new WorkloadApi();
        using var session = await Connect(api);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => coordinator.PrepareAsync(session,
            Catalogue.Id, "/srv/qfrey-test", WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task LostAddResponseLeavesJournalAndDoesNotRetry()
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { LoseAddResponse = true };
        using var session = await Connect(api);

        var result = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);

        Assert.Equal(OwnedWorkloadPrepareStatus.RecoveryRequired, result.Status);
        Assert.Equal(ErrorCodes.ApiUnavailable, result.ReasonCode);
        Assert.NotNull(await store.ReadAsync(result.JournalId));
        Assert.Single(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingCategoryOrTagCollisionFailsBeforeJournalOrPost()
    {
        var (root, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi { Torrents = Inventory(OtherHash, "other.iso", 123, "/srv/other", Category, "ordinary") };
        using var session = await Connect(api);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => coordinator.PrepareAsync(session,
            Catalogue.Id, "/srv/qfrey-test", WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default));

        Assert.Equal(ErrorCodes.OperationConflict, error.Code);
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
        Assert.Empty(Directory.GetFiles(root, "*.json"));
    }

    [Fact]
    public async Task ImmediateGateRejectsConcurrentPrepareAndReleasesAfterFirstOperation()
    {
        var (root, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi { BlockFirstInventory = true };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var first = coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        await api.InventoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => coordinator.PrepareAsync(session,
            Catalogue.Id, "/srv/qfrey-test", WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default));
        Assert.Equal(ErrorCodes.OperationConflict, error.Code);

        api.ReleaseInventory.TrySetResult();
        var result = await first;
        Assert.Equal(OwnedWorkloadPrepareStatus.IdentityVerified, result.Status);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Single(Directory.GetFiles(root, "*.json"));
    }

    [Fact]
    public async Task ReconcileIsReadOnlyAndStopVerifiesStateWhileDeleteRemainsDenied()
    {
        var (root, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        var before = api.Calls.Count(call => call.Method == "POST");

        var reconciliation = await coordinator.ReconcileAsync(session, prepared.JournalId, default);
        var changed = await coordinator.ChangeAsync(session, prepared.JournalId, OwnedWorkloadAction.Stop, false, default);
        var deniedDelete = await coordinator.ChangeAsync(session, prepared.JournalId, OwnedWorkloadAction.Delete, false, default);

        Assert.Equal(OwnedWorkloadReconciliationStatus.IdentityVerified, reconciliation.Status);
        Assert.Equal(OwnedWorkloadActionStatus.AcceptedVerified, changed.Status);
        Assert.Equal(OwnedWorkloadActionStatus.Denied, deniedDelete.Status);
        Assert.Equal("WORKLOAD_PATH_UNVERIFIED", deniedDelete.ReasonCode);
        Assert.Equal(before + 1, api.Calls.Count(call => call.Method == "POST"));
        Assert.Contains(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/stop", StringComparison.Ordinal));
        Assert.NotEmpty(Directory.GetFiles(root, "*.json"));
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
    }

    [Theory]
    [InlineData("v4.6.7", "pausedDL", true)]
    [InlineData("v4.6.7", "pausedUP", true)]
    [InlineData("v5.0.0", "stoppedDL", true)]
    [InlineData("v5.1.0", "stoppedUP", true)]
    [InlineData("v5.2.0", "stoppedDL", true)]
    [InlineData("v5.2.0", "stoppedUP", true)]
    [InlineData("v4.6.7", "stoppedDL", false)]
    [InlineData("v5.2.0", "pausedDL", false)]
    [InlineData("v5.2.0", "downloading", false)]
    [InlineData("v5.2.0", "queuedDL", false)]
    [InlineData("v5.2.0", "checkingResumeData", false)]
    [InlineData("v5.2.0", "error", false)]
    [InlineData("v5.2.0", "futureState", false)]
    [InlineData("v5.2.0", null, false)]
    public async Task PrepareAndRecoveryRequireFreshStoppedState(string version, string? state, bool verified)
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { Version = version };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes,
            "/srv/qfrey-test", Category, Tag, state);
        using var session = await Connect(api);

        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        var recovered = await coordinator.ReconcileAsync(session, prepared.JournalId, default);

        Assert.Equal(verified ? OwnedWorkloadPrepareStatus.IdentityVerified : OwnedWorkloadPrepareStatus.RecoveryRequired, prepared.Status);
        Assert.Equal(verified ? OwnedWorkloadReconciliationStatus.IdentityVerified : OwnedWorkloadReconciliationStatus.RecoveryRequired, recovered.Status);
        Assert.Equal(verified ? null : "WORKLOAD_STOPPED_STATE_NOT_OBSERVED", prepared.ReasonCode);
        Assert.Equal(prepared.ReasonCode, recovered.ReasonCode);
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal(6, api.Calls.Count(call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MissingTorrentDuringStoppedReadbackRequiresRecoveryWithoutAnotherPost()
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes,
            "/srv/qfrey-test", Category, Tag);
        api.OnInventory = count => { if (count == 4) api.Torrents = "[]"; };
        using var session = await Connect(api);

        var result = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);

        Assert.Equal(OwnedWorkloadPrepareStatus.RecoveryRequired, result.Status);
        Assert.Equal("WORKLOAD_STOPPED_STATE_NOT_OBSERVED", result.ReasonCode);
        Assert.NotNull(await store.ReadAsync(result.JournalId));
        Assert.Single(api.Calls, call => call.Method == "POST");
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("uploading", true)]
    [InlineData("forcedDL", true)]
    [InlineData("forcedUP", true)]
    [InlineData("stalledDL", true)]
    [InlineData("stalledUP", true)]
    [InlineData("queuedDL", false)]
    [InlineData("queuedUP", false)]
    [InlineData("metaDL", false)]
    [InlineData("forcedMetaDL", false)]
    [InlineData("stoppedDL", false)]
    [InlineData("checkingDL", false)]
    [InlineData("futureState", false)]
    [InlineData(null, false)]
    public async Task MeasurementVerificationRequiresFreshActiveStateAndNeverWrites(string? state, bool verified)
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag, state);
        api.Calls.Clear();

        var result = await coordinator.VerifyForMeasurementAsync(session, prepared.JournalId, default);

        Assert.Equal(verified ? OwnedWorkloadReconciliationStatus.IdentityVerified : OwnedWorkloadReconciliationStatus.RecoveryRequired, result.Status);
        Assert.Equal(verified ? null : "WORKLOAD_ACTIVE_STATE_NOT_OBSERVED", result.ReasonCode);
        Assert.Equal(prepared.JournalId, result.JournalId);
        Assert.NotNull(result.Journal);
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
        Assert.Equal(2, api.Calls.Count(call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeasurementVerificationRejectsChangedIdentityOrTarget(bool changeTarget)
    {
        var (_, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category,
            changeTarget ? Tag : "ordinary", "downloading");
        if (changeTarget) api.Version = "v5.1.0";
        api.Calls.Clear();

        var result = await coordinator.VerifyForMeasurementAsync(session, prepared.JournalId, default);

        Assert.NotEqual(OwnedWorkloadReconciliationStatus.IdentityVerified, result.Status);
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
    }

    [Theory]
    [InlineData("v4.6.7", OwnedWorkloadAction.Stop, "pausedDL", false)]
    [InlineData("v5.2.0", OwnedWorkloadAction.Stop, "stoppedUP", false)]
    [InlineData("v4.6.7", OwnedWorkloadAction.Start, "downloading", false)]
    [InlineData("v5.2.0", OwnedWorkloadAction.Start, "stalledDL", false)]
    [InlineData("v5.2.0", OwnedWorkloadAction.Start, "downloading", true)]
    [InlineData("v5.2.0", OwnedWorkloadAction.Stop, "stoppedDL", true)]
    public async Task ActionReadsBackStateEvenAfterLostResponseWithoutReplayingPost(string version,
        OwnedWorkloadAction action, string state, bool loseResponse)
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { Version = version, LoseChangeResponse = loseResponse };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag,
            version.StartsWith("v4.", StringComparison.Ordinal) ? "pausedDL" : "stoppedDL");
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.OnChange = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag, state);
        api.Calls.Clear();

        var result = await coordinator.ChangeAsync(session, prepared.JournalId, action, false, default);

        Assert.Equal(OwnedWorkloadActionStatus.AcceptedVerified, result.Status);
        Assert.Null(result.ReasonCode);
        Assert.Equal(!loseResponse, session.IsValidated);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal(3, api.Calls.Count(call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal)));
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
        if (loseResponse)
        {
            var second = await coordinator.ChangeAsync(session, prepared.JournalId, action, false, default);
            Assert.Equal(OwnedWorkloadActionStatus.RecoveryRequired, second.Status);
            Assert.Equal(ErrorCodes.TargetNotValidated, second.ReasonCode);
            Assert.Single(api.Calls, call => call.Method == "POST");
        }
    }

    [Theory]
    [InlineData(OwnedWorkloadAction.Start, "queuedDL", false)]
    [InlineData(OwnedWorkloadAction.Start, "stoppedDL", true)]
    [InlineData(OwnedWorkloadAction.Stop, "downloading", false)]
    [InlineData(OwnedWorkloadAction.Stop, "pausedDL", true)]
    public async Task UnobservedActionStateRequiresRecoveryAfterBoundedReadbacks(OwnedWorkloadAction action,
        string state, bool loseResponse)
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { LoseChangeResponse = loseResponse };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.OnChange = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag, state);
        api.Calls.Clear();

        var result = await coordinator.ChangeAsync(session, prepared.JournalId, action, false, default);

        Assert.Equal(OwnedWorkloadActionStatus.RecoveryRequired, result.Status);
        Assert.Equal(action == OwnedWorkloadAction.Start ? "WORKLOAD_ACTIVE_STATE_NOT_OBSERVED" : "WORKLOAD_STOPPED_STATE_NOT_OBSERVED", result.ReasonCode);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal(7, api.Calls.Count(call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal)));
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
    }

    [Fact]
    public async Task StartCanObserveDelayedStateTransitionWithoutAnotherPost()
    {
        var (_, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.OnInventory = count =>
        {
            if (count == 9) api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes,
                "/srv/qfrey-test", Category, Tag, "downloading");
        };
        api.Calls.Clear();

        var result = await coordinator.ChangeAsync(session, prepared.JournalId, OwnedWorkloadAction.Start, false, default);

        Assert.Equal(OwnedWorkloadActionStatus.AcceptedVerified, result.Status);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal(5, api.Calls.Count(call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LostActionResponseCannotVerifyChangedOwnership()
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi { LoseChangeResponse = true };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.OnChange = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes,
            "/srv/qfrey-test", Category, "ordinary", "downloading");
        api.Calls.Clear();

        var result = await coordinator.ChangeAsync(session, prepared.JournalId, OwnedWorkloadAction.Start, false, default);

        Assert.Equal(OwnedWorkloadActionStatus.RecoveryRequired, result.Status);
        Assert.Equal("WORKLOAD_IDENTITY_NOT_OBSERVED", result.ReasonCode);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
    }

    [Fact]
    public async Task LostActionResponseRejectsChangedVersionsBeforeRecoveryInventory()
    {
        var (_, _, coordinator) = NewCoordinator();
        var api = new WorkloadApi { LoseChangeResponse = true };
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.OnChange = () => api.Version = "v5.1.0";
        api.Calls.Clear();

        var result = await coordinator.ChangeAsync(session, prepared.JournalId, OwnedWorkloadAction.Stop, false, default);

        Assert.Equal(OwnedWorkloadActionStatus.RecoveryRequired, result.Status);
        Assert.Equal(ErrorCodes.SessionStale, result.ReasonCode);
        Assert.False(session.IsValidated);
        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Single(api.Calls, call => call.Path.EndsWith("torrents/info", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellingMeasurementVerificationDoesNotStopOrDeleteWorkload()
    {
        var (_, store, coordinator) = NewCoordinator();
        var api = new WorkloadApi();
        api.OnAdd = () => api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag);
        using var session = await Connect(api);
        var prepared = await coordinator.PrepareAsync(session, Catalogue.Id, "/srv/qfrey-test",
            WorkloadCatalogue.ConsentIdentity(Catalogue), TorrentBytes, default);
        api.Torrents = Inventory(Metadata.V1InfoHash, Name, Catalogue.SizeBytes, "/srv/qfrey-test", Category, Tag, "downloading");
        using var cancellation = new CancellationTokenSource();
        api.OnInventory = _ => cancellation.Cancel();
        api.Calls.Clear();

        var result = await coordinator.VerifyForMeasurementAsync(session, prepared.JournalId, cancellation.Token);

        Assert.Equal(OwnedWorkloadReconciliationStatus.RecoveryRequired, result.Status);
        Assert.Equal(ErrorCodes.Cancelled, result.ReasonCode);
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
        Assert.NotNull(await store.ReadAsync(prepared.JournalId));
    }

    private static async Task<QbittorrentSession> Connect(WorkloadApi api) => await QbittorrentSession.ConnectAsync(
        new(Endpoint, new BypassAuthentication()), default, api);

    private static (string Root, OwnedWorkloadStore Store, OwnedWorkloadCoordinator Coordinator) NewCoordinator()
    {
        var root = Path.Combine(NewRoot(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new OwnedWorkloadStore(root);
        return (root, store, new OwnedWorkloadCoordinator(store, MarkerFactory()));
    }

    private static Func<string> MarkerFactory()
    {
        var values = new Queue<string>([Category, Tag]);
        return () => values.Dequeue();
    }

    private static string NewRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Project root not found.");
        var root = Path.Combine(directory.FullName, ".cache", "tests", "owned-workload-coordinator");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Inventory(string hash, string name, long size, string path, string category, string tag,
        string? state = "stoppedDL") => JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["hash"] = hash, ["name"] = name, ["total_size"] = size, ["save_path"] = path,
                ["category"] = category, ["tags"] = tag, ["state"] = state
            }
        });

    private static byte[] Torrent(string name, long length) => Encoding.UTF8.GetBytes(
        $"d4:infod6:lengthi{length}e4:name{Encoding.UTF8.GetByteCount(name)}:{name}ee");

    private sealed class WorkloadApi : HttpMessageHandler
    {
        public string Torrents { get; set; } = "[]";
        public bool LoseAddResponse { get; set; }
        public bool LoseChangeResponse { get; set; }
        public bool BlockFirstInventory { get; set; }
        public bool DurableAtAdd { get; private set; }
        public string? DurableJournalRoot { get; set; }
        public Action? OnAdd { get; set; }
        public Action? OnChange { get; set; }
        public Action<int>? OnInventory { get; set; }
        public string Version { get; set; } = "v5.2.0";
        public TaskCompletionSource InventoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInventory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        private int inventoryCount;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Calls.Add((request.Method.Method, path, body));
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
            {
                var count = Interlocked.Increment(ref inventoryCount);
                OnInventory?.Invoke(count);
                if (BlockFirstInventory && count == 1)
                {
                    InventoryStarted.TrySetResult();
                    await ReleaseInventory.Task.WaitAsync(token);
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(Torrents) };
            }
            if (path.EndsWith("torrents/add", StringComparison.Ordinal))
            {
                DurableAtAdd = DurableJournalRoot is not null && Directory.Exists(DurableJournalRoot)
                    && Directory.GetFiles(DurableJournalRoot, "*.json").Length > 0;
                if (LoseAddResponse) throw new HttpRequestException("simulated lost add response");
                OnAdd?.Invoke();
            }
            if (request.Method == HttpMethod.Post && (path.EndsWith("torrents/stop", StringComparison.Ordinal)
                || path.EndsWith("torrents/pause", StringComparison.Ordinal) || path.EndsWith("torrents/start", StringComparison.Ordinal)
                || path.EndsWith("torrents/resume", StringComparison.Ordinal)))
            {
                OnChange?.Invoke();
                if (LoseChangeResponse) throw new HttpRequestException("simulated lost change response");
            }
            var response = path.Split('/').Last() switch
            {
                "version" => Version,
                "webapiVersion" => "2.15.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => "{\"max_connec\":500,\"max_connec_per_torrent\":100,\"up_limit\":0,\"dl_limit\":0,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0}",
                _ => ""
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }
}
