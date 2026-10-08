using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace QFrey.Core.Workloads;

public sealed class MetadataDownloadException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Fetches and verifies catalogue torrent metadata without ambient cookies, credentials, or proxy settings.</summary>
public sealed class MetadataDownloader : IDisposable
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestAndBodyTimeout = TimeSpan.FromSeconds(20);
    private readonly HttpClient client;
    private static readonly SemaphoreSlim activeDownloads = new(2, 2);
    private static int admittedDownloads;

    public MetadataDownloader(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private MetadataDownloader()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = ConnectToPublicAddressAsync
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static MetadataDownloader CreateDefault() => new();

    public async Task<byte[]> DownloadAsync(WorkloadCatalogueEntry selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var official = WorkloadCatalogue.Get(selected.Id);
        if (!string.Equals(WorkloadCatalogue.ConsentIdentity(official), WorkloadCatalogue.ConsentIdentity(selected), StringComparison.Ordinal))
            throw new MetadataDownloadException("WORKLOAD_SELECTION_INVALID");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestAndBodyTimeout);
        if (Interlocked.Increment(ref admittedDownloads) > 4)
        {
            Interlocked.Decrement(ref admittedDownloads);
            throw new MetadataDownloadException("METADATA_BUSY");
        }
        var acquired = false;
        try
        {
            await activeDownloads.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            var uri = ValidateUri(new Uri(official.SourceUrl, UriKind.Absolute));
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
            {
                if (!visited.Add(uri.AbsoluteUri)) throw new MetadataDownloadException("METADATA_REDIRECT_LOOP");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == MaximumRedirects || response.Headers.Location is null)
                        throw new MetadataDownloadException("METADATA_REDIRECT_LIMIT");
                    uri = ValidateUri(response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(uri, response.Headers.Location));
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new MetadataDownloadException("METADATA_HTTP_STATUS");

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is > TorrentMetadata.MaximumBytes)
                    throw new MetadataDownloadException("METADATA_TOO_LARGE");
                var raw = await ReadBoundedAsync(response.Content, declaredLength, deadline.Token).ConfigureAwait(false);
                var parsed = TorrentMetadata.Parse(raw, official.FileName);
                WorkloadCatalogue.ValidateMetadata(official.Id, parsed);
                return raw;
            }
            throw new MetadataDownloadException("METADATA_REDIRECT_LIMIT");
        }
        catch (MetadataDownloadException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new MetadataDownloadException("METADATA_TIMEOUT"); }
        catch (NotSupportedException) { throw new MetadataDownloadException("METADATA_FORMAT_UNSUPPORTED"); }
        catch (FormatException) { throw new MetadataDownloadException("METADATA_REJECTED"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SecurityException or UriFormatException or ArgumentException)
        { throw new MetadataDownloadException("METADATA_TRANSPORT_FAILED"); }
        finally
        {
            if (acquired) activeDownloads.Release();
            Interlocked.Decrement(ref admittedDownloads);
        }
    }

    public void Dispose() => client.Dispose();

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long? declaredLength, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > TorrentMetadata.MaximumBytes)
                throw new MetadataDownloadException("METADATA_TOO_LARGE");
            buffer.Write(chunk, 0, read);
        }
        if (declaredLength.HasValue && buffer.Length != declaredLength.Value)
            throw new MetadataDownloadException("METADATA_LENGTH_MISMATCH");
        if (buffer.Length == 0) throw new MetadataDownloadException("METADATA_EMPTY");
        return buffer.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || !string.IsNullOrEmpty(uri.Fragment))
            throw new MetadataDownloadException("METADATA_REDIRECT_UNSAFE");
        return uri;
    }

    // A custom connector pins the validated public DNS result to the socket, preventing a second lookup
    // from rebinding a permitted HTTPS hostname to a local/private address.
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        { throw new HttpRequestException("Metadata host resolution failed."); }
        var publicAddresses = addresses.Where(IsPublicAddress).ToArray();
        if (publicAddresses.Length == 0 || publicAddresses.Length != addresses.Length)
            throw new HttpRequestException("Metadata host resolved to a restricted address.");

        Exception? last = null;
        foreach (var address in publicAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
                if (ex is OperationCanceledException) throw;
            }
        }
        throw new HttpRequestException("Metadata connection failed.", last);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // Fail closed for special-purpose protocol/tunnel space, including embedded IPv4 routes.
            // https://www.iana.org/assignments/iana-ipv6-special-registry
            var globalUnicast = (bytes[0] & 0xE0) == 0x20;
            var documentation = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8;
            var protocolAssignments = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 1; // 2001::/23
            var sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
            var newerDocumentation = bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0; // 3fff::/20
            return globalUnicast && !documentation && !protocolAssignments && !sixToFour && !newerDocumentation;
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 0) &&
            !(b[0] == 192 && b[1] == 168) &&
            !(b[0] == 192 && b[1] == 0 && b[2] == 2) &&
            !(b[0] == 192 && b[1] == 88 && b[2] == 99) &&
            !(b[0] == 198 && b[1] is 18 or 19) &&
            !(b[0] == 198 && b[1] == 51 && b[2] == 100) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
}
