using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class MetadataDownloaderTests
{
    private static readonly WorkloadCatalogueEntry Ubuntu = WorkloadCatalogue.Get("ubuntu");
    private static byte[] ValidUbuntu() => Encoding.UTF8.GetBytes(
        $"d4:infod6:lengthi{Ubuntu.SizeBytes}e4:name{Encoding.UTF8.GetByteCount(Ubuntu.FileName)}:{Ubuntu.FileName}ee");

    [Fact]
    public async Task DownloadsOnlyAfterMetadataHasPassedNameAndSizeValidation()
    {
        var raw = ValidUbuntu();
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        using var downloader = new MetadataDownloader(handler);

        Assert.Equal(raw, await downloader.DownloadAsync(Ubuntu));
        Assert.Single(handler.Requests);
        Assert.Equal("https", handler.Requests[0].Scheme);
        Assert.Equal("releases.ubuntu.com", handler.Requests[0].Host);
    }

    [Fact]
    public async Task FollowsBoundedExternalHttpsRedirectButRejectsDowngradeAndLoop()
    {
        var raw = ValidUbuntu();
        var handler = new SequenceHandler(index => index == 0
            ? Redirect(HttpStatusCode.Found, "https://mirror.example.test/metadata.torrent")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        using (var downloader = new MetadataDownloader(handler))
            Assert.Equal(raw, await downloader.DownloadAsync(Ubuntu));
        Assert.Equal("mirror.example.test", handler.Requests[1].Host);

        handler = new SequenceHandler(_ => Redirect(HttpStatusCode.Found, "http://mirror.example.test/file"));
        using (var downloader = new MetadataDownloader(handler))
            Assert.Equal("METADATA_REDIRECT_UNSAFE", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu))).Code);
        Assert.Single(handler.Requests);

        handler = new SequenceHandler(_ => Redirect(HttpStatusCode.Found, Ubuntu.SourceUrl));
        using (var downloader = new MetadataDownloader(handler))
            Assert.Equal("METADATA_REDIRECT_LOOP", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu))).Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StopsAfterFiveRedirectsAndDisposesEachResponse()
    {
        var handler = new SequenceHandler(index => Redirect(HttpStatusCode.Found, $"https://mirror.example.test/next/{index}"));
        using var downloader = new MetadataDownloader(handler);

        Assert.Equal("METADATA_REDIRECT_LIMIT", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu))).Code);
        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Responses, response => Assert.True(response.IsDisposed));
    }

    [Fact]
    public async Task RejectsHttpErrorsOversizeDeclaredLengthTruncationAndNonTorrentBody()
    {
        (Func<HttpResponseMessage> Response, string Code)[] cases =
        [
            (() => new(HttpStatusCode.Unauthorized), "METADATA_HTTP_STATUS"),
            (() => new(HttpStatusCode.OK) { Content = new DeclaredLengthContent(TorrentMetadata.MaximumBytes + 1) }, "METADATA_TOO_LARGE"),
            (() => new(HttpStatusCode.OK) { Content = new DeclaredLengthContent(ValidUbuntu(), ValidUbuntu().Length + 1) }, "METADATA_LENGTH_MISMATCH"),
            (() => new(HttpStatusCode.OK) { Content = new ByteArrayContent("<html>not a torrent</html>"u8.ToArray()) }, "METADATA_REJECTED"),
            (() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ValidUbuntu()[..^2]) }, "METADATA_REJECTED")
        ];

        foreach (var (response, code) in cases)
        {
            using var downloader = new MetadataDownloader(new SequenceHandler(_ => response()));
            Assert.Equal(code, (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu))).Code);
        }
    }

    [Fact]
    public async Task RejectsChangedOfficialSizeAndKaliInfoHashBeforeReturningBytes()
    {
        var wrongSize = Encoding.UTF8.GetBytes(
            $"d4:infod6:lengthi1e4:name{Encoding.UTF8.GetByteCount(Ubuntu.FileName)}:{Ubuntu.FileName}ee");
        using (var downloader = new MetadataDownloader(new SequenceHandler(_ => Ok(wrongSize))))
            Assert.Equal("METADATA_REJECTED", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu))).Code);

        var kali = WorkloadCatalogue.Get("kali");
        var wrongHash = Encoding.UTF8.GetBytes(
            $"d4:infod6:lengthi{kali.SizeBytes}e4:name{Encoding.UTF8.GetByteCount(kali.FileName)}:{kali.FileName}ee");
        using (var downloader = new MetadataDownloader(new SequenceHandler(_ => Ok(wrongHash))))
            Assert.Equal("METADATA_REJECTED", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(kali))).Code);
    }

    [Fact]
    public async Task RejectsFabricatedCatalogueSelectionAndPassesCallerCancellation()
    {
        var forged = Ubuntu with { SourceUrl = "https://127.0.0.1/private" };
        var handler = new SequenceHandler(_ => Ok(ValidUbuntu()));
        using var downloader = new MetadataDownloader(handler);
        Assert.Equal("WORKLOAD_SELECTION_INVALID", (await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(forged))).Code);
        Assert.Empty(handler.Requests);

        using var cancellation = new CancellationTokenSource();
        handler = new SequenceHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok(ValidUbuntu());
        });
        using var cancellableDownloader = new MetadataDownloader(handler);
        cancellation.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellableDownloader.DownloadAsync(Ubuntu, cancellation.Token));
    }

    [Fact]
    public void PublicAddressFilterFailsClosedForSpecialIpv4AndIpv6Ranges()
    {
        Assert.True(IsPublic("8.8.8.8"));
        Assert.False(IsPublic("10.0.0.1"));
        Assert.False(IsPublic("169.254.1.1"));
        Assert.False(IsPublic("127.0.0.1"));
        Assert.True(IsPublic("2001:4860:4860::8888"));
        Assert.False(IsPublic("::"));
        Assert.False(IsPublic("::1"));
        Assert.False(IsPublic("fc00::1"));
        Assert.False(IsPublic("fe80::1"));
        Assert.False(IsPublic("2001:db8::1"));
        Assert.False(IsPublic("2001:2::1"));
        Assert.False(IsPublic("2001:0:4136:e378:8000:63bf:3fff:fdd2")); // Teredo with embedded IPv4.
        Assert.False(IsPublic("2002:a00:1::1")); // 6to4 encoding a private IPv4 destination.
        Assert.False(IsPublic("3fff:fff::1"));
        Assert.False(IsPublic("64:ff9b::808:808")); // NAT64 well-known translation prefix.
        Assert.True(IsPublic("::ffff:8.8.8.8"));
        Assert.False(IsPublic("::ffff:192.168.1.1"));
    }

    [Fact]
    public async Task CapsProcessWideActiveAndQueuedDownloadsAndCancellationReleasesCapacity()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedRequests = 0;
        var handler = new SequenceHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref startedRequests) == 2) started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok(ValidUbuntu());
        });
        using var downloader = new MetadataDownloader(handler);
        using var cancellation = new CancellationTokenSource();
        var firstFour = Enumerable.Range(0, 4).Select(_ => downloader.DownloadAsync(Ubuntu, cancellation.Token)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var overflow = await Assert.ThrowsAsync<MetadataDownloadException>(() => downloader.DownloadAsync(Ubuntu));
        Assert.Equal("METADATA_BUSY", overflow.Code);
        Assert.Equal(2, handler.Requests.Count); // The other two are queued behind the active pair.

        cancellation.Cancel();
        foreach (var pending in firstFour)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Redirect(HttpStatusCode status, string location) => new(status) { Headers = { Location = new Uri(location) } };
    private static bool IsPublic(string address) => (bool)typeof(MetadataDownloader)
        .GetMethod("IsPublicAddress", BindingFlags.Static | BindingFlags.NonPublic)!
        .Invoke(null, [IPAddress.Parse(address)])!;

    private sealed class SequenceHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int count;
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> asyncResponse = (index, _) => Task.FromResult(response(index));
        public List<Uri> Requests { get; } = [];
        public List<TrackedResponse> Responses { get; } = [];
        public SequenceHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> response)
            : this(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)) => asyncResponse = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var index = count++;
            var result = await asyncResponse(index, cancellationToken);
            var tracked = new TrackedResponse(result);
            Responses.Add(tracked);
            return tracked;
        }
    }

    private sealed class TrackedResponse : HttpResponseMessage
    {
        public TrackedResponse(HttpResponseMessage response) : base(response.StatusCode)
        {
            foreach (var header in response.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
            Content = response.Content;
            response.Content = null;
            response.Dispose();
        }
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class DeclaredLengthContent : HttpContent
    {
        private readonly byte[] bytes;
        public DeclaredLengthContent(long length) : this([], length) { }
        public DeclaredLengthContent(byte[] bytes, long declaredLength) => (this.bytes, Headers.ContentLength) = (bytes, declaredLength);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
