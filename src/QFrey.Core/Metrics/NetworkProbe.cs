using System.Net;
using System.Net.Http.Headers;

namespace QFrey.Core.Metrics;

public enum NetworkProbeStatus { Measured, Unknown, Error }

public sealed record NetworkProbeReading(NetworkProbeStatus Status, double? BytesPerSecond,
    long? BytesTransferred, double? DurationMilliseconds, string? ReasonCode);

public sealed record NetworkTrafficBudget(int ServerSelectionRequests, int DownloadRequests, int DownloadChunkBytes,
    int UploadRequests, int UploadChunkBytes)
{
    public int MaximumRequestCount => ServerSelectionRequests + DownloadRequests + UploadRequests;
    public long MaximumPayloadBytes => (long)DownloadRequests * DownloadChunkBytes + (long)UploadRequests * UploadChunkBytes;
}

public sealed record NetworkProbeResult(DateTimeOffset MeasuredUtc, string? ServerId, string? ServerName,
    NetworkProbeReading Download, NetworkProbeReading Upload, NetworkTrafficBudget TrafficBudget);

/// <summary>
/// Explicitly invoked public-network throughput probe. It only sends requests to the fixed HTTPS endpoints
/// below; the injected handler/time provider constructor exists solely for in-process tests.
/// </summary>
public sealed class NetworkProbe : IDisposable
{
    public const int DownloadConnections = 8;
    public const int DownloadChunkBytes = 20 * 1024 * 1024;
    public const int UploadConnections = 6;
    public const int UploadChunkBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumMeasurement = TimeSpan.FromMilliseconds(100);
    private static readonly NetworkTrafficBudget Budget = new(3, DownloadConnections, DownloadChunkBytes,
        UploadConnections, UploadChunkBytes);
    private static readonly ProbeServer[] Servers =
    [
        new("cloudflare", "Cloudflare (Global)", new("https://speed.cloudflare.com/__down"), new("https://speed.cloudflare.com/__up"), true),
        new("azure-eu", "Microsoft Azure (EU)", new("https://azspeedtest.blob.core.windows.net/speedtest/100MB.bin"), null, false),
        new("google-us", "Google Cloud (US)", new("https://storage.googleapis.com/gcd-speedtest/100MB.bin"), null, false)
    ];

    private readonly HttpClient client;
    private readonly TimeProvider time;
    private bool disposed;
    private int running;

    public static NetworkTrafficBudget MaximumTraffic => Budget;

    public NetworkProbe() : this(CreateSafeHandler(), TimeProvider.System) { }

    internal NetworkProbe(HttpMessageHandler handler, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(timeProvider);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        time = timeProvider;
    }

    /// <summary>Runs only when explicitly called by the owning operation coordinator.</summary>
    public async Task<NetworkProbeResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("NETWORK_TEST_ALREADY_RUNNING");
        try
        {
            var server = await SelectServerAsync(cancellationToken).ConfigureAwait(false);
            var download = server is null
                ? Failed(NetworkProbeStatus.Error, "NETWORK_SERVER_UNAVAILABLE")
                : await MeasureDownloadAsync(server, cancellationToken).ConfigureAwait(false);

            var uploadServer = Servers.First(x => x.SupportsUpload);
            var upload = uploadServer.UploadUri is null
                ? Failed(NetworkProbeStatus.Unknown, "UPLOAD_UNSUPPORTED")
                : await MeasureUploadAsync(uploadServer, cancellationToken).ConfigureAwait(false);

            return new(time.GetUtcNow(), server?.Id, server?.Name, download, upload, Budget);
        }
        finally { Volatile.Write(ref running, 0); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Dispose();
    }

    private async Task<ProbeServer?> SelectServerAsync(CancellationToken token)
    {
        ProbeServer? best = null;
        var bestTicks = long.MaxValue;
        foreach (var server in Servers)
        {
            var started = time.GetTimestamp();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, server.DownloadUri);
                using var timeout = CreateTimeout(token, ServerTimeout);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri != server.DownloadUri) continue;
                var elapsed = time.GetElapsedTime(started).Ticks;
                if (elapsed < bestTicks)
                {
                    best = server;
                    bestTicks = elapsed;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
        }
        return best;
    }

    private async Task<NetworkProbeReading> MeasureDownloadAsync(ProbeServer server, CancellationToken token)
    {
        var started = time.GetTimestamp();
        var chunks = await Task.WhenAll(Enumerable.Range(0, DownloadConnections)
            .Select(_ => DownloadChunkAsync(server, token))).ConfigureAwait(false);
        var duration = time.GetElapsedTime(started);
        var transferred = chunks.Sum(x => x.Bytes);
        var failure = chunks.FirstOrDefault(x => x.ReasonCode is not null)?.ReasonCode;
        if (failure is not null) return new(NetworkProbeStatus.Error, null, transferred, duration.TotalMilliseconds, failure);
        return Reading(transferred, duration);
    }

    private async Task<ChunkResult> DownloadChunkAsync(ProbeServer server, CancellationToken token)
    {
        var uri = DownloadRequestUri(server);
        var total = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        AddNoCacheHeaders(request);
        using var timeout = CreateTimeout(token, RequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uri) return new(0, "NETWORK_REDIRECT_REJECTED");
            if (IsRedirect(response.StatusCode)) return new(0, "NETWORK_REDIRECT_REJECTED");
            if (!response.IsSuccessStatusCode) return new(0, "NETWORK_HTTP_ERROR");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[512 * 1024];
            while (total < DownloadChunkBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, DownloadChunkBytes - total)), timeout.Token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            return new(total, null);
        }
        catch (HttpRequestException) { return new(total, "NETWORK_HTTP_ERROR"); }
        catch (IOException) { return new(total, "NETWORK_READ_ERROR"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(total, "NETWORK_TIMEOUT"); }
    }

    private async Task<NetworkProbeReading> MeasureUploadAsync(ProbeServer server, CancellationToken token)
    {
        if (server.UploadUri is null) return Failed(NetworkProbeStatus.Unknown, "UPLOAD_UNSUPPORTED");
        var payload = new byte[UploadChunkBytes];
        Array.Fill(payload, (byte)'0');
        var started = time.GetTimestamp();
        var chunks = await Task.WhenAll(Enumerable.Range(0, UploadConnections)
            .Select(_ => UploadChunkAsync(server.UploadUri, payload, token))).ConfigureAwait(false);
        var duration = time.GetElapsedTime(started);
        var failure = chunks.FirstOrDefault(x => !x.Success)?.ReasonCode;
        if (failure is not null) return new(NetworkProbeStatus.Error, null, null, duration.TotalMilliseconds, failure);
        var transferred = chunks.Sum(_ => (long)payload.Length);
        return Reading(transferred, duration);
    }

    private async Task<ChunkResult> UploadChunkAsync(Uri uri, byte[] payload, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(payload) };
        AddNoCacheHeaders(request);
        using var timeout = CreateTimeout(token, RequestTimeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != uri) return new(0, "NETWORK_REDIRECT_REJECTED");
            if (IsRedirect(response.StatusCode)) return new(0, "NETWORK_REDIRECT_REJECTED");
            return response.StatusCode == HttpStatusCode.OK
                ? new(payload.Length, null)
                : new(0, "NETWORK_HTTP_ERROR");
        }
        catch (HttpRequestException) { return new(0, "NETWORK_HTTP_ERROR"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(0, "NETWORK_TIMEOUT"); }
    }

    private static CancellationTokenSource CreateTimeout(CancellationToken token, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(timeout);
        return source;
    }

    private static NetworkProbeReading Reading(long bytes, TimeSpan duration)
    {
        if (bytes <= 0) return Failed(NetworkProbeStatus.Error, "NETWORK_NO_BYTES", bytes, duration.TotalMilliseconds);
        if (duration <= MinimumMeasurement) return Failed(NetworkProbeStatus.Unknown, "NETWORK_DURATION_TOO_SHORT", bytes, duration.TotalMilliseconds);
        var rate = bytes / duration.TotalSeconds;
        if (!double.IsFinite(rate)) return Failed(NetworkProbeStatus.Error, "NETWORK_RATE_INVALID", bytes, duration.TotalMilliseconds);
        return new(NetworkProbeStatus.Measured, rate, bytes, duration.TotalMilliseconds, null);
    }

    private static NetworkProbeReading Failed(NetworkProbeStatus status, string reason, long? bytes = null, double? durationMs = null) =>
        new(status, null, bytes, durationMs, reason);

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static Uri DownloadRequestUri(ProbeServer server)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var separator = string.IsNullOrEmpty(server.DownloadUri.Query) ? "?" : "&";
        var query = server.IsCloudflare ? $"bytes={DownloadChunkBytes}&cb={nonce}" : $"cb={nonce}";
        return new Uri(server.DownloadUri + separator + query, UriKind.Absolute);
    }

    private static void AddNoCacheHeaders(HttpRequestMessage request)
    {
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Pragma.ParseAdd("no-cache");
    }

    private static HttpMessageHandler CreateSafeHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    private sealed record ProbeServer(string Id, string Name, Uri DownloadUri, Uri? UploadUri, bool IsCloudflare)
    {
        public bool SupportsUpload => UploadUri is not null;
    }

    private sealed record ChunkResult(long Bytes, string? ReasonCode)
    {
        public bool Success => ReasonCode is null;
    }
}
