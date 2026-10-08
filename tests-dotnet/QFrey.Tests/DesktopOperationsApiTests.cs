using System.Net;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class DesktopOperationsApiTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShutdownRevalidatesThenPostsOnceAndInvalidatesEvenWhenReplyIsLost(bool loseReply)
    {
        var api = new Api { LoseShutdownReply = loseReply };
        using var session = await Connect(api);
        api.Calls.Clear();
        var checkedOwner = false;
        async Task Shutdown() => await session.ShutdownAsync(default, () =>
        {
            Assert.Equal(new[] { "GET app/version", "GET app/webapiVersion", "GET app/buildInfo" }, api.Calls);
            checkedOwner = true;
        });
        if (loseReply) await Assert.ThrowsAsync<QbittorrentException>(Shutdown);
        else await Shutdown();
        Assert.True(checkedOwner);
        Assert.False(session.IsValidated);
        Assert.Equal("POST app/shutdown", api.Calls.Last());
        Assert.Single(api.Calls, call => call == "POST app/shutdown");
        await Assert.ThrowsAsync<QbittorrentException>(() => session.ShutdownAsync(default));
        Assert.Single(api.Calls, call => call == "POST app/shutdown");
    }

    [Theory]
    [InlineData("owner")] [InlineData("version")] [InlineData("cancel")]
    public async Task ShutdownRejectsChangedOwnerVersionOrCancellationBeforePost(string scenario)
    {
        var api = new Api();
        using var session = await Connect(api);
        if (scenario == "version") api.Version = "5.3.0";
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") cancellation.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => session.ShutdownAsync(cancellation.Token, () =>
        {
            if (scenario == "owner") throw new QbittorrentException(ErrorCodes.OwnerUnavailable);
        }));
        Assert.DoesNotContain(api.Calls, call => call.StartsWith("POST", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]", "{\"dl_info_speed\":0,\"up_info_speed\":0}", true)]
    [InlineData("[]", "{\"dl_info_speed\":1,\"up_info_speed\":0}", false)]
    [InlineData("[]", "{\"dl_info_speed\":0,\"up_info_speed\":1}", false)]
    [InlineData("[]", "{}", false)]
    [InlineData("[]", "{\"dl_info_speed\":false,\"up_info_speed\":0}", false)]
    [InlineData("[]", "{\"dl_info_speed\":0,\"up_info_speed\":-1}", false)]
    [InlineData("{}", "{\"dl_info_speed\":0,\"up_info_speed\":0}", false)]
    public async Task IdleEvidenceNeedsFullInventoryAndFreshKnownZeroRates(string torrents, string transfer, bool expected)
    {
        var api = new Api { Torrents = torrents, Transfer = transfer };
        using var session = await Connect(api);
        Assert.Equal(expected, await session.ReadNetworkIdleEvidenceAsync(default));
        Assert.All(api.Calls, call => Assert.StartsWith("GET", call));
    }

    [Theory]
    [InlineData("stoppedDL", true)] [InlineData("pausedUP", true)]
    [InlineData("uploading", false)] [InlineData("downloading", false)]
    [InlineData("stalledDL", false)] [InlineData("stalledUP", false)]
    [InlineData("forcedDL", false)] [InlineData("forcedUP", false)]
    [InlineData("metaDL", false)] [InlineData("forcedMetaDL", false)]
    [InlineData("futureState", false)]
    public void IdleInventoryRejectsActiveMetadataAndUnknownStates(string state, bool expected)
    {
        using var json = JsonDocument.Parse("[{\"hash\":\"" + new string('a', 40) + "\",\"state\":\"" + state + "\"}]");
        Assert.Equal(expected, TorrentTelemetry.IsIdleInventory(json.RootElement));
    }

    [Fact]
    public void IdleInventoryRejectsAmbiguousMalformedAndOversizedRows()
    {
        var row = "{\"hash\":\"" + new string('a', 40) + "\",\"state\":\"stoppedDL\"}";
        foreach (var text in new[] { "[" + row + "," + row.Replace(new string('a', 40), new string('A', 40)) + "]",
            "[{\"hash\":\"bad\",\"state\":\"stoppedDL\"}]", "[null]", "[{\"hash\":\"" + new string('a', 40) + "\"}]",
            "[" + string.Join(",", Enumerable.Repeat(row, TorrentTelemetry.MaximumTorrents + 1)) + "]" })
        {
            using var json = JsonDocument.Parse(text);
            Assert.False(TorrentTelemetry.IsIdleInventory(json.RootElement));
        }
    }

    private static Task<QbittorrentSession> Connect(Api api) => QbittorrentSession.ConnectAsync(
        new("http://qb.example.test", new BypassAuthentication()), default, api);

    private sealed class Api : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string Version { get; set; } = "5.2.0";
        public string Torrents { get; set; } = "[]";
        public string Transfer { get; set; } = "{\"dl_info_speed\":0,\"up_info_speed\":0}";
        public bool LoseShutdownReply { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath[8..];
            Calls.Add(request.Method.Method + " " + path);
            if (path == "app/shutdown" && LoseShutdownReply) throw new HttpRequestException("lost reply");
            var body = path switch
            {
                "app/version" => Version, "app/webapiVersion" => "2.15.0", "app/buildInfo" => "{\"libtorrent\":\"2.0.11\"}",
                "app/preferences" => "{\"max_connec\":500,\"max_connec_per_torrent\":100,\"up_limit\":0,\"dl_limit\":0,\"dht\":true,\"pex\":true,\"lsd\":false,\"encryption\":0}",
                "torrents/info" => Torrents, "transfer/info" => Transfer, "app/shutdown" => "",
                _ => throw new InvalidOperationException("Unexpected request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
