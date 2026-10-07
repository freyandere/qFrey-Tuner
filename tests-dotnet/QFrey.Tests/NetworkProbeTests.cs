using System.Collections.Concurrent;
using System.Net;
using QFrey.Core.Metrics;
using Xunit;

namespace QFrey.Tests;

public sealed class NetworkProbeTests
{
    [Fact]
    public void ExposesConsentBudgetForLegacyParallelPayloadSizes()
    {
        var budget = NetworkProbe.MaximumTraffic;
        Assert.Equal(3, budget.ServerSelectionRequests);
        Assert.Equal(8, budget.DownloadRequests);
        Assert.Equal(20 * 1024 * 1024, budget.DownloadChunkBytes);
        Assert.Equal(6, budget.UploadRequests);
        Assert.Equal(10 * 1024 * 1024, budget.UploadChunkBytes);
        Assert.Equal(17, budget.MaximumRequestCount);
        Assert.Equal(220L * 1024 * 1024, budget.MaximumPayloadBytes);
    }

    [Fact]
    public async Task MeasuresExactTransferredBytesWithBoundedConcurrencyAndFixedHttpsHosts()
    {
        var clock = new FakeTimeProvider();
        var allDownloadsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCount = 0;
        var handler = new ProbeHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Response(request, HttpStatusCode.OK);
            clock.Advance(TimeSpan.FromSeconds(1));
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref downloadCount) == NetworkProbe.DownloadConnections)
                    allDownloadsStarted.TrySetResult();
                await allDownloadsStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                return Response(request, HttpStatusCode.OK, new ByteStreamContent(NetworkProbe.DownloadChunkBytes));
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(10L * 1024 * 1024, request.Content!.Headers.ContentLength!.Value);
            await Task.Yield();
            return Response(request, HttpStatusCode.OK);
        });

        using var probe = new NetworkProbe(handler, clock);
        var result = await probe.RunAsync();

        Assert.Equal("cloudflare", result.ServerId);
        Assert.Equal(NetworkProbeStatus.Measured, result.Download.Status);
        Assert.Equal(160L * 1024 * 1024, result.Download.BytesTransferred!.Value);
        Assert.Equal(20d * 1024 * 1024, result.Download.BytesPerSecond!.Value);
        Assert.Equal(8_000d, result.Download.DurationMilliseconds!.Value);
        Assert.Equal(NetworkProbeStatus.Measured, result.Upload.Status);
        Assert.Equal(60L * 1024 * 1024, result.Upload.BytesTransferred!.Value);
        Assert.Equal(10d * 1024 * 1024, result.Upload.BytesPerSecond!.Value);
        Assert.Equal(6_000d, result.Upload.DurationMilliseconds!.Value);
        Assert.Equal(NetworkProbe.DownloadConnections, handler.MaximumActive);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Contains(request.RequestUri.Host, new[]
            {
                "speed.cloudflare.com", "azspeedtest.blob.core.windows.net", "storage.googleapis.com"
            });
        });
        Assert.Equal(17, handler.Requests.Length);
        Assert.Equal(8, handler.Requests.Count(x => x.Method == HttpMethod.Get));
        Assert.Equal(6, handler.Requests.Count(x => x.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task HttpErrorsAndRedirectsAreFailuresAndNeverReportedAsZeroSpeed()
    {
        var clock = new FakeTimeProvider();
        var handler = new ProbeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Head) return Task.FromResult(Response(request, HttpStatusCode.OK));
            var response = Response(request, request.Method == HttpMethod.Get ? HttpStatusCode.Redirect : HttpStatusCode.ServiceUnavailable);
            response.Headers.Location = new Uri("https://example.invalid/redirect");
            return Task.FromResult(response);
        });

        using var probe = new NetworkProbe(handler, clock);
        var result = await probe.RunAsync();

        Assert.Equal(NetworkProbeStatus.Error, result.Download.Status);
        Assert.Null(result.Download.BytesPerSecond);
        Assert.Equal("NETWORK_REDIRECT_REJECTED", result.Download.ReasonCode);
        Assert.Equal(NetworkProbeStatus.Error, result.Upload.Status);
        Assert.Null(result.Upload.BytesPerSecond);
        Assert.Null(result.Upload.BytesTransferred);
        Assert.Equal("NETWORK_HTTP_ERROR", result.Upload.ReasonCode);
        Assert.DoesNotContain(handler.Requests, x => x.RequestUri!.Host == "example.invalid");
    }

    [Fact]
    public async Task TooShortMeasurementIsUnknownInsteadOfFabricatedZero()
    {
        var clock = new FakeTimeProvider();
        var handler = new ProbeHandler((request, _) => Task.FromResult(request.Method switch
        {
            var method when method == HttpMethod.Head => Response(request, HttpStatusCode.OK),
            var method when method == HttpMethod.Get => Response(request, HttpStatusCode.OK,
                new ByteStreamContent(NetworkProbe.DownloadChunkBytes)),
            _ => Response(request, HttpStatusCode.OK)
        }));

        using var probe = new NetworkProbe(handler, clock);
        var result = await probe.RunAsync();

        Assert.Equal(NetworkProbeStatus.Unknown, result.Download.Status);
        Assert.Equal("NETWORK_DURATION_TOO_SHORT", result.Download.ReasonCode);
        Assert.Null(result.Download.BytesPerSecond);
        Assert.Equal(160L * 1024 * 1024, result.Download.BytesTransferred!.Value);
        Assert.Equal(NetworkProbeStatus.Unknown, result.Upload.Status);
        Assert.Equal("NETWORK_DURATION_TOO_SHORT", result.Upload.ReasonCode);
        Assert.Null(result.Upload.BytesPerSecond);
    }

    [Fact]
    public async Task CancellationDuringDownloadPropagatesAndPreventsUploadStage()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ProbeHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Head) return Response(request, HttpStatusCode.OK);
            if (request.Method == HttpMethod.Get)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Response(request, HttpStatusCode.OK);
        });
        using var probe = new NetworkProbe(handler, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();

        var running = probe.RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ASecondRunOnTheSameProbeIsRejectedWhileTheFirstRunIsActive()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ProbeHandler(async (request, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(request, HttpStatusCode.OK);
        });
        using var probe = new NetworkProbe(handler, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var running = probe.RunAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status,
        HttpContent? content = null) => new(status) { RequestMessage = request, Content = content ?? new ByteArrayContent([]) };

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private readonly ConcurrentBag<HttpRequestMessage> requests = [];
        private int active;
        private int maximumActive;
        public HttpRequestMessage[] Requests => requests.ToArray();
        public int MaximumActive => maximumActive;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(current);
            try { return await send(request, cancellationToken).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref active); }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var old = Volatile.Read(ref maximumActive);
                if (old >= value || Interlocked.CompareExchange(ref maximumActive, value, old) == old) return;
            }
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    private sealed class ByteStreamContent(int length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new ZeroStream(length));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new ZeroStream(length));
    }

    private sealed class ZeroStream : Stream
    {
        private readonly int streamLength;
        private int remaining;

        public ZeroStream(int length)
        {
            streamLength = length;
            remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => streamLength;
        public override long Position { get => streamLength - remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, remaining);
            Array.Clear(buffer, offset, read);
            remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Clear();
            remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
