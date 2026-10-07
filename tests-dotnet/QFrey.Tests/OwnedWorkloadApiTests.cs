using System.Net;
using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class OwnedWorkloadApiTests
{
    private const string Name = "ubuntu-22.04.5-desktop-amd64.iso";
    private const string OtherHash = "1111111111111111111111111111111111111111";
    private const string Endpoint = "http://qb.example.test/";
    private static readonly TargetIdentity Target = new(Endpoint, "v5.2.0", "2.15.0", "2.0.11");
    private static readonly WorkloadCatalogueEntry Catalogue = WorkloadCatalogue.Get("ubuntu");

    [Fact]
    public async Task AddRevalidatesMetadataAndPreflightThenSendsPausedSingleMultipartAndReturnsUnverified()
    {
        var bytes = Torrent(Name, Catalogue.SizeBytes);
        var metadata = TorrentMetadata.Parse(bytes, Name);
        var api = new WorkloadApi();
        using var session = await Connect(api);
        var journal = Journal(metadata, api);

        var result = await session.AddOwnedWorkloadAsync(journal, bytes, default);

        Assert.Equal(metadata.V1InfoHash, result.Hash);
        Assert.Equal(OwnedWorkloadAddStatus.AcceptedUnverified, result.Status);
        var post = Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal("/api/v2/torrents/add", post.Path);
        Assert.Contains("name=torrents", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("filename=" + Name, post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=savepath", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=category", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=tags", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=autoTMM", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=stopped", post.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("false", post.Body, StringComparison.Ordinal);
        Assert.Contains("true", post.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy_password", post.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddRejectsBadMetadataAndExistingHashBeforeAnyPost()
    {
        var bytes = Torrent(Name, Catalogue.SizeBytes);
        var metadata = TorrentMetadata.Parse(bytes, Name);
        var api = new WorkloadApi();
        using var session = await Connect(api);
        var journal = Journal(metadata, api);

        await Assert.ThrowsAsync<QbittorrentException>(() => session.AddOwnedWorkloadAsync(journal, Torrent(Name, 5), default));
        api.Torrents = Inventory(journal);
        await Assert.ThrowsAsync<QbittorrentException>(() => session.AddOwnedWorkloadAsync(journal, bytes, default));
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task LostAddResponseIsNotRetriedOrReportedAsAccepted()
    {
        var bytes = Torrent(Name, Catalogue.SizeBytes);
        var metadata = TorrentMetadata.Parse(bytes, Name);
        var api = new WorkloadApi { LoseAddResponse = true };
        using var session = await Connect(api);
        var journal = Journal(metadata, api);

        await Assert.ThrowsAsync<QbittorrentException>(() => session.AddOwnedWorkloadAsync(journal, bytes, default));

        Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.Equal(1, api.Calls.Count(call => call.Method == "POST" && call.Path.EndsWith("torrents/add", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("v4.6.7", "pause", "resume")]
    [InlineData("v5.2.0", "stop", "start")]
    public async Task StartAndStopUseVersionCorrectEndpointsAfterFreshOwnershipProof(string version, string stop, string start)
    {
        var api = new WorkloadApi { Version = version };
        using var session = await Connect(api);
        var journal = Journal(TorrentMetadata.Parse(Torrent(Name, Catalogue.SizeBytes), Name), api, version);
        api.Torrents = Inventory(journal);

        await session.ChangeOwnedWorkloadAsync(journal, OwnedWorkloadAction.Stop, false, null, default);
        await session.ChangeOwnedWorkloadAsync(journal, OwnedWorkloadAction.Start, false, null, default);

        Assert.Contains(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/" + stop, StringComparison.Ordinal));
        Assert.Contains(api.Calls, call => call.Method == "POST" && call.Path.EndsWith("torrents/" + start, StringComparison.Ordinal));
        Assert.All(api.Calls.Where(call => call.Method == "POST"), call => Assert.Contains("hashes=", call.Body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteNeverPostsUnlessNonSymlinkPathSafetyIsProvenAndNeverDeletesFiles(bool callbackReturnsSafe)
    {
        var api = new WorkloadApi();
        using var session = await Connect(api);
        var journal = Journal(TorrentMetadata.Parse(Torrent(Name, Catalogue.SizeBytes), Name), api);
        api.Torrents = Inventory(journal);
        Func<string, CancellationToken, Task<ServerPathSafety>>? pathCheck = callbackReturnsSafe
            ? (path, _) => Task.FromResult(path == journal.ServerSavePath ? ServerPathSafety.VerifiedNoSymlink : ServerPathSafety.Mismatch)
            : null;

        if (!callbackReturnsSafe)
        {
            await Assert.ThrowsAsync<QbittorrentException>(() => session.ChangeOwnedWorkloadAsync(
                journal, OwnedWorkloadAction.Delete, false, pathCheck, default));
            Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
            return;
        }

        await session.ChangeOwnedWorkloadAsync(journal, OwnedWorkloadAction.Delete, false, pathCheck, default);
        var delete = Assert.Single(api.Calls, call => call.Method == "POST");
        Assert.EndsWith("torrents/delete", delete.Path);
        Assert.Contains("deleteFiles=false", delete.Body);
        Assert.Contains("hashes=" + journal.Hash, delete.Body);
        await Assert.ThrowsAsync<QbittorrentException>(() => session.ChangeOwnedWorkloadAsync(
            journal, OwnedWorkloadAction.Delete, true, pathCheck, default));
        Assert.Single(api.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task RecoveryInventoryIsReadOnlyAndMalformedInventoryFailsClosed()
    {
        var api = new WorkloadApi { Torrents = "[{\"hash\":\"" + OtherHash + "\"}]" };
        using var session = await Connect(api);
        await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadRecoveryOwnedWorkloadInventoryAsync(default));
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task PreflightDetectsMarkerAfterCommaSpaceAndRecoveryRejectsDuplicateHashes()
    {
        var bytes = Torrent(Name, Catalogue.SizeBytes);
        var api = new WorkloadApi();
        using var session = await Connect(api);
        var journal = Journal(TorrentMetadata.Parse(bytes, Name), api);
        api.Torrents = Inventory(journal).Replace(journal.Hash, OtherHash)
            .Replace("\"category\":\"" + journal.Category + "\"", "\"category\":\"other\"")
            .Replace("\"tags\":\"" + journal.Tag + "\"", "\"tags\":\"other, " + journal.Tag + "\"");
        await Assert.ThrowsAsync<QbittorrentException>(() => session.AddOwnedWorkloadAsync(journal, bytes, default));
        var row = Inventory(journal)[1..^1];
        api.Torrents = "[" + row + "," + row + "]";
        await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadRecoveryOwnedWorkloadInventoryAsync(default));
        Assert.DoesNotContain(api.Calls, call => call.Method == "POST");
    }

    private static async Task<QbittorrentSession> Connect(WorkloadApi api) => await QbittorrentSession.ConnectAsync(
        new(Endpoint, new BypassAuthentication()), default, api);

    private static OwnedWorkloadJournal Journal(TorrentMetadataInfo metadata, WorkloadApi api, string? version = null)
    {
        var currentTarget = Target with { QbittorrentVersion = version ?? api.Version };
        var nonce = Guid.NewGuid().ToString("N");
        var marker = "qfrey-test-" + nonce;
        var evidence = new WorkloadInventoryEvidence(currentTarget, true, []);
        var intent = new NewWorkloadIntent(currentTarget, Catalogue.Id, metadata, marker, marker, "/srv/qfrey-test");
        return OwnedWorkloadJournal.CreateNewAddIntent(intent, evidence).Journal!;
    }

    private static string Inventory(OwnedWorkloadJournal journal) => "[{"
        + "\"hash\":\"" + journal.Hash + "\","
        + "\"name\":\"" + journal.Name + "\","
        + "\"total_size\":" + journal.TotalBytesDecimal + ","
        + "\"save_path\":\"" + journal.ServerSavePath + "\","
        + "\"category\":\"" + journal.Category + "\","
        + "\"tags\":\"" + journal.Tag + "\"}]";

    private static byte[] Torrent(string name, long length) => Encoding.UTF8.GetBytes(
        $"d4:infod6:lengthi{length}e4:name{Encoding.UTF8.GetByteCount(name)}:{name}ee");

    private sealed class WorkloadApi : HttpMessageHandler
    {
        public string Version { get; set; } = "v5.2.0";
        public string Torrents { get; set; } = "[]";
        public bool LoseAddResponse { get; set; }
        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, body));
            if (path.EndsWith("torrents/add", StringComparison.Ordinal) && LoseAddResponse)
                throw new HttpRequestException("simulated lost mock response");
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent(Torrents) };
            var responseBody = path.Split('/').Last() switch
            {
                "version" => Version,
                "webapiVersion" => "2.15.0",
                "buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "preferences" => "{\"max_connec\":500,\"max_connec_per_torrent\":100,\"up_limit\":0,\"dl_limit\":0,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0}",
                _ => ""
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
        }
    }
}
