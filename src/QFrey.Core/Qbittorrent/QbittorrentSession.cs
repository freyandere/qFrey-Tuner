using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("QFrey.Tests")]

namespace QFrey.Core.Qbittorrent;

public sealed record MeasurementSourceSnapshot(IReadOnlyDictionary<string, PreferenceValue> Preferences,
    bool? SchedulerEnabled, bool? AlternativeLimitsEnabled, TorrentTelemetryEvidence Torrents, LiveMetric[] Metrics);

// Owns immutable endpoint/auth/cookies. Reconnect creates a new instance; metadata uses a different client.
public sealed partial class QbittorrentSession : IDisposable
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int pendingRequests;
    private volatile bool disposed;
    private volatile bool validated;
    public Uri Endpoint { get; }
    public ValidatedVersions Versions { get; private set; } = null!;
    public bool IsValidated { get => validated; private set => validated = value; }
    public double? LastRequestDurationMilliseconds { get; private set; }

    private QbittorrentSession(ConnectPayload payload, HttpMessageHandler? handler, TimeProvider? time)
    {
        this.time = time ?? TimeProvider.System;
        if (!Uri.TryCreate(payload.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new QbittorrentException(ErrorCodes.InvalidEndpoint);
        Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/'));
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, Credentials = null,
            CookieContainer = new CookieContainer(), UseCookies = true, ConnectTimeout = TimeSpan.FromSeconds(3), MaxConnectionsPerServer = 1 };
        http = new HttpClient(handler) { BaseAddress = new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/api/v2/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Referrer = Endpoint;
        if (payload.Auth is ApiKeyAuthentication key)
        {
            if (string.IsNullOrWhiteSpace(key.ApiKey) || key.ApiKey.Contains('\r') || key.ApiKey.Contains('\n'))
            { http.Dispose(); throw new QbittorrentException(ErrorCodes.AuthenticationFailed); }
            try { http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.ApiKey.Trim()); }
            catch (FormatException) { http.Dispose(); throw new QbittorrentException(ErrorCodes.AuthenticationFailed); }
        }
        else if (payload.Auth is not (PasswordAuthentication or BypassAuthentication))
        { http.Dispose(); throw new QbittorrentException(ErrorCodes.AuthenticationFailed); }
    }

    public static async Task<QbittorrentSession> ConnectAsync(ConnectPayload payload, CancellationToken token, HttpMessageHandler? handler = null,
        TimeProvider? time = null)
    {
        var session = new QbittorrentSession(payload, handler, time);
        try
        {
            var version = await session.SendAsync(HttpMethod.Get, "app/version", null, token, allowAuthenticationFailure: true).ConfigureAwait(false);
            if (version.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                if (payload.Auth is not PasswordAuthentication password || string.IsNullOrWhiteSpace(password.Username) || string.IsNullOrEmpty(password.Password))
                    throw new QbittorrentException(ErrorCodes.AuthenticationFailed);
                using var login = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = password.Username, ["password"] = password.Password });
                var response = await session.SendAsync(HttpMethod.Post, "auth/login", login, token).ConfigureAwait(false);
                if (System.Text.Encoding.UTF8.GetString(response.Bytes).Trim() != "Ok.") throw new QbittorrentException(ErrorCodes.AuthenticationFailed);
                version = await session.SendAsync(HttpMethod.Get, "app/version", null, token).ConfigureAwait(false);
            }
            var api = await session.SendAsync(HttpMethod.Get, "app/webapiVersion", null, token).ConfigureAwait(false);
            var build = await session.SendAsync(HttpMethod.Get, "app/buildInfo", null, token).ConfigureAwait(false);
            using var info = Parse(build.Bytes);
            if (info.RootElement.ValueKind != JsonValueKind.Object || !info.RootElement.TryGetProperty("libtorrent", out var lt) || lt.ValueKind != JsonValueKind.String)
                throw new QbittorrentException(ErrorCodes.VersionIncompatible);
            session.Versions = Compatibility.Validate(System.Text.Encoding.UTF8.GetString(version.Bytes).Trim(),
                System.Text.Encoding.UTF8.GetString(api.Bytes).Trim(), lt.GetString()!);
            await session.ReadPreferencesCoreAsync(token).ConfigureAwait(false);
            session.IsValidated = true;
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public async Task<IReadOnlyDictionary<string, PreferenceValue>> ReadPreferencesAsync(CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        try { return await ReadPreferencesCoreAsync(token).ConfigureAwait(false); }
        catch (QbittorrentException error) { if (error.Code != ErrorCodes.OperationConflict) IsValidated = false; throw; }
    }
    public async Task<LiveMetric[]> ReadTransferMetricsAsync(CancellationToken token)
        => (await ReadTransferSnapshotAsync(token).ConfigureAwait(false)).Metrics;

    private async Task<(LiveMetric[] Metrics, bool? AlternativeLimits)> ReadTransferSnapshotAsync(CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "transfer/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        var sampledAt = time.GetUtcNow();
        return ([Read("transfer.download", "dl_info_speed", MetricUnit.BytesPerSecond),
            Read("transfer.upload", "up_info_speed", MetricUnit.BytesPerSecond),
            Read("session.dhtNodes", "dht_nodes", MetricUnit.Count),
            new("diagnostics.apiDuration", MetricUnit.Milliseconds, MetricScope.Endpoint, "Stopwatch HTTP round trip (queue excluded)",
                LastRequestDurationMilliseconds is double duration ? new FreshReading(duration, sampledAt) : new UnavailableReading("NOT_SAMPLED"))],
            document.RootElement.TryGetProperty("use_alt_speed_limits", out var alternative)
                && alternative.ValueKind is JsonValueKind.True or JsonValueKind.False ? alternative.GetBoolean() : null);
        LiveMetric Read(string id, string key, MetricUnit unit)
        {
            MetricReading reading = !document.RootElement.TryGetProperty(key, out var value)
                ? new UnavailableReading("FIELD_MISSING")
                : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number is >= 0 and <= 9007199254740991
                    ? new FreshReading(number, sampledAt) : new ErrorReading("INVALID_METRIC_TYPE");
            return new(id, unit, MetricScope.Session, "transfer/info:" + key, reading);
        }
    }

    public async Task<TargetInterface[]> ReadInterfacesAsync(CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "app/networkInterfaceList", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 128) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        var result = new List<TargetInterface>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("value", out var id) || id.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString()) || string.IsNullOrWhiteSpace(name.GetString())
                || id.GetString()!.Length > 256 || name.GetString()!.Length > 256 || result.Any(i => i.Id == id.GetString()))
                throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            result.Add(new(id.GetString()!, name.GetString()!));
        }
        return [.. result];
    }
    public async Task<TorrentTelemetryEvidence> ReadTorrentMetricsAsync(IReadOnlyCollection<string> selectedHashes,
        CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "torrents/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        return TorrentTelemetry.Aggregate(document.RootElement, selectedHashes, time.GetUtcNow());
    }
    public async Task<MeasurementSourceSnapshot> ReadMeasurementSnapshotAsync(IReadOnlyCollection<string> selectedHashes,
        CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "app/preferences", null, token).ConfigureAwait(false);
        using var preferences = Parse(response.Bytes);
        var safe = Compatibility.ReadPreferences(preferences.RootElement, Versions.LibtorrentMajor);
        bool? scheduler = preferences.RootElement.TryGetProperty("scheduler_enabled", out var flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
        var torrents = await ReadTorrentMetricsAsync(selectedHashes, token).ConfigureAwait(false);
        var transfer = await ReadTransferSnapshotAsync(token).ConfigureAwait(false);
        return new(safe, scheduler, transfer.AlternativeLimits, torrents, [.. transfer.Metrics, .. torrents.Metrics]);
    }
    public async Task<WorkloadSummary> ReadExistingWorkloadAsync(WorkloadReference reference, CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        if (reference.Id == Guid.Empty || reference.Kind != WorkloadKind.Existing || reference.Hashes is null
            || reference.Hashes.Length is < 1 or > 5000 || reference.Hashes.Any(h => h is not { Length: 40 } || !h.All(Uri.IsHexDigit))
            || reference.Hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reference.Hashes.Length)
            throw new QbittorrentException(ErrorCodes.InvalidCommand);
        var response = await SendAsync(HttpMethod.Get, "torrents/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        var proof = TorrentTelemetry.Aggregate(document.RootElement, reference.Hashes, time.GetUtcNow());
        if (proof.Context.ReasonCode is not null || proof.Context.SelectedTorrents?.Count != reference.Hashes.Length)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
        var selected = reference.Hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(); var paths = new HashSet<string>(StringComparer.Ordinal);
        ulong bytes = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!selected.Contains(item.GetProperty("hash").GetString()!)) continue;
            if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 256 }
                || !item.TryGetProperty("total_size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetUInt64(out var length)
                || !item.TryGetProperty("save_path", out var path) || path.ValueKind != JsonValueKind.String || path.GetString()!.Length > 2048
                || bytes > ulong.MaxValue - length) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            bytes += length; names.Add(name.GetString()!); paths.Add(path.GetString()!);
        }
        var displayName = names.Count == 1 ? names[0] : names[0][..Math.Min(names[0].Length, 230)] + " +" + (names.Count - 1);
        return new(reference with { Hashes = proof.Context.SelectedHashes.ToArray() }, displayName,
            bytes.ToString(System.Globalization.CultureInfo.InvariantCulture), paths.Count == 1 ? paths.Single() : "", null, false,
            paths.Count > 1 ? ["MULTIPLE_SAVE_PATHS"] : []);
    }
    private async Task<IReadOnlyDictionary<string, PreferenceValue>> ReadPreferencesCoreAsync(CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, "app/preferences", null, token).ConfigureAwait(false);
        using var json = Parse(response.Bytes);
        return Compatibility.ReadPreferences(json.RootElement, Versions.LibtorrentMajor);
    }

    // Backend-only transport. The coordinator must persist the backup before calling this.
    // Never retry POST: a lost response cannot establish whether the server committed it.
    internal async Task WritePreferencesAsync(IReadOnlyDictionary<string, PreferenceValue> intended,
        IReadOnlyDictionary<string, PreferenceValue> expected, CancellationToken token, string? expectedFingerprint = null)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        if (intended.Count == 0 || intended.Count > 32 || intended.Count != expected.Count
            || intended.Keys.Any(key => !expected.ContainsKey(key)))
            throw new QbittorrentException(ErrorCodes.InvalidOverride);
        await RevalidateVersionsAsync(token).ConfigureAwait(false);
        var current = await ReadPreferencesAsync(token).ConfigureAwait(false);
        if (expectedFingerprint is not null && Tuning.PlanBuilder.FingerprintPreferences(current) != expectedFingerprint)
            throw new QbittorrentException(ErrorCodes.PreferenceDrift);
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in intended)
        {
            if (!current.TryGetValue(key, out var actual) || actual.GetType() != value.GetType())
                throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            if (actual != expected[key]) throw new QbittorrentException(ErrorCodes.PlanStale);
            payload.Add(key, value switch
            {
                IntegerPreference number => number.Value,
                BooleanPreference boolean => boolean.Value,
                StringPreference text when text.Value.Length <= 256 => text.Value,
                _ => throw new QbittorrentException(ErrorCodes.InvalidOverride)
            });
        }
        token.ThrowIfCancellationRequested();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["json"] = JsonSerializer.Serialize(payload) });
        await SendAsync(HttpMethod.Post, "app/setPreferences", content, token).ConfigureAwait(false);
    }

    public async Task RevalidateVersionsAsync(CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        await RevalidateVersionsCoreAsync(token).ConfigureAwait(false);
    }

    // Read-only recovery after an ambiguous POST. Never restores write authority or retries authentication/POST.
    internal async Task<IReadOnlyDictionary<string, PreferenceValue>> ReadRecoveryPreferencesAsync(CancellationToken token)
    {
        await RevalidateVersionsCoreAsync(token).ConfigureAwait(false);
        return await ReadPreferencesCoreAsync(token).ConfigureAwait(false);
    }

    private async Task RevalidateVersionsCoreAsync(CancellationToken token)
    {
        var version = await SendAsync(HttpMethod.Get, "app/version", null, token).ConfigureAwait(false);
        var api = await SendAsync(HttpMethod.Get, "app/webapiVersion", null, token).ConfigureAwait(false);
        var build = await SendAsync(HttpMethod.Get, "app/buildInfo", null, token).ConfigureAwait(false);
        using var info = Parse(build.Bytes);
        if (info.RootElement.ValueKind != JsonValueKind.Object || !info.RootElement.TryGetProperty("libtorrent", out var lt)
            || lt.ValueKind != JsonValueKind.String) throw new QbittorrentException(ErrorCodes.VersionIncompatible);
        var currentVersions = Compatibility.Validate(System.Text.Encoding.UTF8.GetString(version.Bytes).Trim(),
            System.Text.Encoding.UTF8.GetString(api.Bytes).Trim(), lt.GetString()!);
        if (currentVersions != Versions) { IsValidated = false; throw new QbittorrentException(ErrorCodes.SessionStale); }
    }

    private async Task<(HttpStatusCode Status, byte[] Bytes)> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken token, bool allowAuthenticationFailure = false, Action? beforeSend = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Interlocked.Increment(ref pendingRequests) > 16)
        { Interlocked.Decrement(ref pendingRequests); throw new QbittorrentException(ErrorCodes.OperationConflict); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var clock = new Stopwatch();
        var acquired = false;
        try
        {
            await requests.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            clock.Start(); // API round trip, excluding time waiting in the local queue.
            using var request = new HttpRequestMessage(method, path) { Content = content };
            beforeSend?.Invoke();
            deadline.Token.ThrowIfCancellationRequested();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) throw new QbittorrentException(ErrorCodes.RedirectRejected);
            if (!response.IsSuccessStatusCode && !(allowAuthenticationFailure && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                throw new QbittorrentException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ErrorCodes.AuthenticationFailed : ErrorCodes.ApiUnavailable);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[32768];
            int read;
            while ((read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > MaxResponseBytes) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
                bytes.Write(buffer, 0, read);
            }
            return (response.StatusCode, bytes.ToArray());
        }
        catch (OperationCanceledException)
        {
            IsValidated = false;
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
            throw new QbittorrentException(ErrorCodes.ApiTimeout);
        }
        catch (HttpRequestException error)
        {
            IsValidated = false;
            throw new QbittorrentException(error.HttpRequestError == HttpRequestError.SecureConnectionError ? ErrorCodes.TlsRejected : ErrorCodes.ApiUnavailable);
        }
        catch (QbittorrentException) { IsValidated = false; throw; }
        finally
        {
            if (acquired) { LastRequestDurationMilliseconds = clock.Elapsed.TotalMilliseconds; requests.Release(); }
            Interlocked.Decrement(ref pendingRequests);
        }
    }

    private static JsonDocument Parse(byte[] bytes)
    {
        try
        {
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            try { Protocol.RejectDuplicateProperties(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (JsonException) { throw new QbittorrentException(ErrorCodes.SchemaIncompatible); }
    }
    public void Dispose() { if (!disposed) { disposed = true; IsValidated = false; lifetime.Cancel(); http.Dispose(); } }
}
