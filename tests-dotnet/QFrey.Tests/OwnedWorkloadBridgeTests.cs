using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;
using QFrey.Desktop.Bridge;
using Xunit;

namespace QFrey.Tests;

public sealed class OwnedWorkloadBridgeTests
{
    private const string Endpoint = "http://127.0.0.1:8080/";
    private static readonly WorkloadCatalogueEntry Catalogue = WorkloadCatalogue.Get("ubuntu");
    private static readonly byte[] MetadataBytes = Torrent(Catalogue.FileName, Catalogue.SizeBytes);
    private static readonly string Hash = TorrentMetadata.Parse(MetadataBytes, Catalogue.FileName).V1InfoHash;

    [Fact]
    public async Task PrepareNeedsExplicitBoundConsentAndDoesNotFetchOrWriteWhenTokenIsReusedOrChanged()
    {
        var root = NewRoot();
        var api = new WorkloadApi();
        var bridge = NewBridge(root, api);
        var metadataFetches = 0;
        bridge.WorkloadMetadataFetch = (_, _) =>
        {
            Interlocked.Increment(ref metadataFetches);
            return Task.FromResult(MetadataBytes);
        };

        try
        {
            var connected = await Connect(bridge);
            var target = connected.Data!.Target!;
            var confirmation = await ConfirmPrepare(bridge, target.SessionId, connected.Revision,
                Catalogue.Id, "/srv/qfrey-tests");
            Assert.True(confirmation.Ok, confirmation.Error?.Code);
            var token = Assert.Single(confirmation.Data!.Confirmations).Token;

            var changedPath = Read(await bridge.DispatchAsync(Request("PrepareWorkload",
                new PrepareWorkloadPayload(Catalogue.Id, "/srv/other", token), target.SessionId,
                confirmation.Revision), default));
            Assert.False(changedPath.Ok);
            Assert.Equal(ErrorCodes.ConfirmationExpired, changedPath.Error!.Code);

            var reusedToken = Read(await bridge.DispatchAsync(Request("PrepareWorkload",
                new PrepareWorkloadPayload(Catalogue.Id, "/srv/qfrey-tests", token), target.SessionId,
                confirmation.Revision), default));
            Assert.False(reusedToken.Ok);
            Assert.Equal(ErrorCodes.ConfirmationExpired, reusedToken.Error!.Code);
            Assert.Equal(0, Volatile.Read(ref metadataFetches));
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
        }
        finally { await Close(bridge, root); }
    }

    [Fact]
    public async Task ConfirmedPreparePersistsJournalBeforeOnePausedAddAndPublishesVerifiedIdentity()
    {
        var root = NewRoot();
        var api = new WorkloadApi { DurableJournalRoot = Path.Combine(root, "workloads") };
        var bridge = NewBridge(root, api);
        var eventChannel = AttachEvents(bridge);
        var fetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchCount = 0;
        bridge.WorkloadMetadataFetch = async (_, token) =>
        {
            Interlocked.Increment(ref fetchCount);
            fetchEntered.TrySetResult();
            await releaseFetch.Task.WaitAsync(token);
            return MetadataBytes;
        };

        try
        {
            var connected = await Connect(bridge);
            var target = connected.Data!.Target!;
            var confirmation = await ConfirmPrepare(bridge, target.SessionId, connected.Revision,
                Catalogue.Id, "/srv/qfrey-tests");
            Assert.True(confirmation.Ok, confirmation.Error?.Code);
            var token = Assert.Single(confirmation.Data!.Confirmations).Token;
            var request = Request("PrepareWorkload", new PrepareWorkloadPayload(Catalogue.Id,
                "/srv/qfrey-tests", token), target.SessionId, confirmation.Revision);

            var accepted = ReadAccepted(await bridge.DispatchAsync(request, default));
            Assert.True(accepted.Ok, accepted.Error?.Code);
            await fetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duplicate = ReadAccepted(await bridge.DispatchAsync(request, default));
            Assert.True(duplicate.Ok, duplicate.Error?.Code);
            Assert.Equal(accepted.Data, duplicate.Data);
            Assert.Equal(1, Volatile.Read(ref fetchCount));
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST");

            releaseFetch.TrySetResult();
            var completed = await ReadCompletion(eventChannel.Reader, accepted.Data!.OperationId);
            Assert.Equal(WorkloadKind.Owned, completed.Snapshot.Experiment!.Workload!.Reference.Kind);
            Assert.True(completed.Snapshot.Experiment.Workload.OwnershipVerified);
            Assert.Equal(Hash, Assert.Single(completed.Snapshot.Experiment.Workload.Reference.Hashes));
            Assert.True(api.DurableAtAdd);
            var add = Assert.Single(api.Calls,
                call => call.Method == "POST" && call.Path.EndsWith("torrents/add", StringComparison.Ordinal));
            Assert.Contains("name=stopped", add.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/start", StringComparison.Ordinal));
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/resume", StringComparison.Ordinal));
        }
        finally
        {
            releaseFetch.TrySetResult();
            await Close(bridge, root);
        }
    }

    [Fact]
    public async Task LostAddResponseRetainsRecoveryJournalAndIsNeverRetried()
    {
        var root = NewRoot();
        var api = new WorkloadApi { LoseAddResponse = true, DurableJournalRoot = Path.Combine(root, "workloads") };
        var bridge = NewBridge(root, api);
        var eventChannel = AttachEvents(bridge);
        bridge.WorkloadMetadataFetch = (_, _) => Task.FromResult(MetadataBytes);

        try
        {
            var connected = await Connect(bridge);
            var target = connected.Data!.Target!;
            var confirmation = await ConfirmPrepare(bridge, target.SessionId, connected.Revision,
                Catalogue.Id, "/srv/qfrey-tests");
            var consent = Assert.Single(confirmation.Data!.Confirmations);
            var accepted = ReadAccepted(await bridge.DispatchAsync(Request("PrepareWorkload",
                new PrepareWorkloadPayload(Catalogue.Id, "/srv/qfrey-tests", consent.Token), target.SessionId,
                confirmation.Revision), default));
            Assert.True(accepted.Ok, accepted.Error?.Code);

            var completed = await ReadCompletion(eventChannel.Reader, accepted.Data!.OperationId);
            Assert.Null(completed.Snapshot.Experiment?.Workload);
            Assert.Contains(completed.Snapshot.AvailableActions,
                action => action.ReasonCode == ErrorCodes.ApiUnavailable || action.MessageKey == "errors.workloadNotOwned");
            Assert.Single(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/add", StringComparison.Ordinal));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "workloads"), "*.json"));
        }
        finally { await Close(bridge, root); }
    }

    [Fact]
    public async Task DeleteCannotObtainConfirmationWithoutPathProofAndNeverPosts()
    {
        var root = NewRoot();
        var api = new WorkloadApi();
        var bridge = NewBridge(root, api);

        try
        {
            var connected = await Connect(bridge);
            var target = connected.Data!.Target!;
            var denied = Read(await bridge.DispatchAsync(Request("RequestConfirmation",
                new RequestConfirmationPayload("DeleteOwnedWorkload", null, null, Guid.NewGuid()), target.SessionId,
                connected.Revision), default));
            Assert.False(denied.Ok);
            Assert.Equal(ErrorCodes.WorkloadNotOwned, denied.Error!.Code);
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
        }
        finally { await Close(bridge, root); }
    }

    private static BridgeDispatcher NewBridge(string root, WorkloadApi api) => new(root,
        (payload, token) => QbittorrentSession.ConnectAsync(payload, token, api));

    private static async Task<Reply> Connect(BridgeDispatcher bridge)
    {
        var result = Read(await bridge.DispatchAsync(Request("Connect",
            new ConnectPayload(Endpoint, new BypassAuthentication()), revision: 0), default));
        Assert.True(result.Ok, result.Error?.Code);
        return result;
    }

    private static async Task<Reply> ConfirmPrepare(BridgeDispatcher bridge, Guid sessionId, long revision,
        string catalogueId, string savePath) => Read(await bridge.DispatchAsync(Request("RequestConfirmation",
            new RequestConfirmationPayload("PrepareWorkload", null, null, null, catalogueId, savePath), sessionId,
            revision), default));

    private static Channel<SnapshotEvent> AttachEvents(BridgeDispatcher bridge)
    {
        var channel = Channel.CreateUnbounded<SnapshotEvent>();
        bridge.SnapshotPublished += json =>
        {
            var item = JsonSerializer.Deserialize<SnapshotEvent>(json, Protocol.Json);
            if (item is not null) channel.Writer.TryWrite(item);
            return Task.CompletedTask;
        };
        return channel;
    }

    private static async Task<SnapshotEvent> ReadCompletion(ChannelReader<SnapshotEvent> events, Guid operationId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var item = await events.ReadAsync(deadline.Token);
            if (item.OperationId == operationId && item.Snapshot.ActiveOperation is null) return item;
        }
    }

    private static string Request(string command, object payload, Guid? sessionId = null, long? revision = null,
        Guid? requestId = null) => JsonSerializer.Serialize(new
    {
        protocolVersion = Protocol.Version,
        requestId = requestId ?? Guid.NewGuid(),
        command,
        targetSessionId = sessionId,
        expectedRevision = revision,
        payload
    }, Protocol.Json);

    private static Reply Read(string json) => JsonSerializer.Deserialize<Reply>(json, Protocol.Json)!;
    private static CommandReply<AcceptedOperation> ReadAccepted(string json) =>
        JsonSerializer.Deserialize<CommandReply<AcceptedOperation>>(json, Protocol.Json)!;
    private static byte[] Torrent(string name, long length) => Encoding.UTF8.GetBytes(
        $"d4:infod6:lengthi{length}e4:name{Encoding.UTF8.GetByteCount(name)}:{name}ee");

    private static async Task Close(BridgeDispatcher bridge, string root)
    {
        await bridge.ShutdownAsync();
        bridge.Dispose();
        var full = Path.GetFullPath(root);
        var parent = Path.GetFullPath(Path.Combine(ProjectRoot(), ".cache", "tests", "owned-workload-bridge"));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the test cache.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(ProjectRoot(), ".cache", "tests", "owned-workload-bridge", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }

    private sealed class WorkloadApi : HttpMessageHandler
    {
        public string Inventory { get; private set; } = "[]";
        public bool LoseAddResponse { get; init; }
        public string? DurableJournalRoot { get; init; }
        public bool DurableAtAdd { get; private set; }
        public ConcurrentQueue<(string Method, string Path, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Calls.Enqueue((request.Method.Method, path, body));
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
                return Json(Inventory);
            if (path.EndsWith("torrents/add", StringComparison.Ordinal))
            {
                DurableAtAdd = DurableJournalRoot is not null && Directory.Exists(DurableJournalRoot)
                    && Directory.GetFiles(DurableJournalRoot, "*.json").Length == 1;
                if (LoseAddResponse) throw new HttpRequestException("simulated lost response");
                var journalFile = Directory.GetFiles(DurableJournalRoot!, "*.json").Single();
                var journal = JsonSerializer.Deserialize<OwnedWorkloadJournalData>(
                    File.ReadAllText(journalFile), Protocol.Json)!;
                Inventory = JsonSerializer.Serialize(new[] { new
                {
                    hash = journal.Hash,
                    name = journal.Name,
                    total_size = long.Parse(journal.TotalBytesDecimal, System.Globalization.CultureInfo.InvariantCulture),
                    save_path = journal.ServerSavePath,
                    category = journal.Category,
                    tags = journal.Tag,
                    state = "stoppedDL",
                    progress = 0.0
                } });
                return Json("");
            }
            var response = path.Split('/').Last() switch
            {
                "version" => "v5.2.0",
                "webapiVersion" => "2.15.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => "{\"max_connec\":500,\"max_connec_per_torrent\":100,\"up_limit\":0,\"dl_limit\":0,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0}",
                "info" => "{}",
                "networkInterfaceList" => "[]",
                _ => ""
            };
            return Json(response);
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
