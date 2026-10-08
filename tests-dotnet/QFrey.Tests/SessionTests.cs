using System.Net;
using System.Net.Sockets;
using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;
public class SessionTests
{
    [Fact]
    public async Task ExistingWorkloadReadsActualMetadataAndKeepsLargeBytesExact()
    {
        var hash = new string('a', 40);
        var api = new MockApi { TorrentsJson = "[{\"hash\":\"" + hash + "\",\"state\":\"downloading\",\"name\":\"Existing\",\"save_path\":\"/remote/data\",\"total_size\":9007199254740993}]" };
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var reference = new WorkloadReference(Guid.NewGuid(), WorkloadKind.Existing, [hash.ToUpperInvariant()]);
        var summary = await session.ReadExistingWorkloadAsync(reference, default);
        Assert.Equal("9007199254740993", summary.TotalBytesDecimal);
        Assert.Equal("/remote/data", summary.ServerSavePath);
        Assert.Equal("Existing", summary.Name);
        Assert.False(summary.OwnershipVerified);
        Assert.Equal(hash, Assert.Single(summary.Reference.Hashes));
        Assert.All(api.Calls, call => Assert.Equal("GET", call.Method));
        api.TorrentsJson = "[]";
        await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadExistingWorkloadAsync(reference, default));
    }
    private const string Preferences = """{"max_connec":500,"max_connec_per_torrent":100,"up_limit":0,"dl_limit":0,"dht":true,"pex":true,"lsd":false,"encryption":0,"proxy_password":"never-persist"}""";
    private sealed class MockApi(bool requireLogin = false, HttpStatusCode? failure = null, HttpRequestError? transportError = null) : HttpMessageHandler
    {
        public List<(string Method, string Path, string? Authorization, string? Referer, string Body)> Calls { get; } = [];
        public bool BlockPreferences { get; set; }
        public string TransferJson { get; set; } = """{"dl_info_speed":1234,"up_info_speed":0,"dht_nodes":17}""";
        public string InterfacesJson { get; set; } = """[{"name":"Ethernet","value":"eth0"}]""";
        public string Version { get; set; } = "v5.2.0";
        public string TorrentsJson { get; set; } = "[]";
        public string PreferencesJson { get; set; } = Preferences;
        public bool LoseWriteResponse { get; set; }
        public TaskCompletionSource BlockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((request.Method.Method, path, request.Headers.Authorization?.ToString(), request.Headers.Referrer?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            if (transportError is { } requestError) throw new HttpRequestException(requestError, "private server diagnostic", new Exception("private inner diagnostic"), null);
            if (BlockPreferences && path.EndsWith("app/preferences"))
            { BlockStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (failure is { } status) return new(status) { Content = new StringContent("secret server error must not escape") };
            if (path.EndsWith("app/version") && requireLogin && Calls.Count == 1) return new(HttpStatusCode.Forbidden) { Content = new StringContent("Forbidden") };
            if (path.EndsWith("app/setPreferences") && LoseWriteResponse) throw new HttpRequestException("lost response");
            if (path.EndsWith("torrents/info")) return new(HttpStatusCode.OK) { Content = new StringContent(TorrentsJson) };
            var body = path.Split('/').Last() switch { "version" => Version, "webapiVersion" => "2.15.0", "buildInfo" => """{"libtorrent":"2.0.11"}""", "preferences" => PreferencesJson, "login" => "Ok.", "info" => TransferJson, "networkInterfaceList" => InterfacesJson, "setPreferences" => "", _ => throw new InvalidOperationException("Unexpected API request") };
            return new(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
    [Fact]
    public async Task MeasurementSourceReadsSequentiallyAndUnknownSchedulerDoesNotBecomeFalse()
    {
        var hash = new string('a', 40);
        var api = new MockApi { TorrentsJson = "[{\"hash\":\"" + hash + "\",\"state\":\"downloading\",\"dlspeed\":123,\"upspeed\":0,\"num_seeds\":2,\"num_leechs\":3}]" };
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var snapshot = await session.ReadMeasurementSnapshotAsync([hash], default);
        Assert.Null(snapshot.SchedulerEnabled); Assert.Null(snapshot.AlternativeLimitsEnabled);
        Assert.DoesNotContain("proxy_password", snapshot.Preferences.Keys);
        Assert.Equal(new[] { "/api/v2/app/preferences", "/api/v2/torrents/info", "/api/v2/transfer/info" }, api.Calls.TakeLast(3).Select(c => c.Path));
        api.PreferencesJson = Preferences[..^1] + ",\"scheduler_enabled\":false}";
        api.TransferJson = "{\"dl_info_speed\":123,\"up_info_speed\":0,\"use_alt_speed_limits\":false}";
        snapshot = await session.ReadMeasurementSnapshotAsync([hash], default);
        Assert.False(snapshot.SchedulerEnabled); Assert.False(snapshot.AlternativeLimitsEnabled);
        Assert.All(api.Calls, c => Assert.Equal("GET", c.Method));
    }
    [Fact]
    public async Task SelectedTorrentMetricsUseActualReadOnlyApiWithoutSubstitutingSessionTraffic()
    {
        var hash = new string('a', 40);
        var api = new MockApi { TorrentsJson = "[{\"hash\":\"" + hash + "\",\"state\":\"downloading\",\"dlspeed\":123,\"upspeed\":0,\"num_seeds\":2,\"num_leechs\":3}]" };
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var evidence = await session.ReadTorrentMetricsAsync([hash], default);
        Assert.Equal(123, Assert.IsType<FreshReading>(evidence.Metrics[0].Reading).Value);
        Assert.Equal(MetricScope.Workload, evidence.Metrics[0].Scope);
        Assert.Equal(5, Assert.IsType<FreshReading>(evidence.Metrics[3].Reading).Value);
        Assert.True(evidence.Context.SelectedMatchesActive);
        Assert.All(api.Calls, c => Assert.Equal("GET", c.Method));
        Assert.Contains(api.Calls, c => c.Path == "/api/v2/torrents/info");
        api.TorrentsJson = "[]";
        evidence = await session.ReadTorrentMetricsAsync([hash], default);
        Assert.All(evidence.Metrics, m => Assert.IsType<UnavailableReading>(m.Reading));
    }
    [Fact]
    public async Task WriteTransportRevalidatesAndPostsOnlyTypedAllowlistedDiffWithoutRetry()
    {
        var api = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var expected = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(500), ["dht"] = new BooleanPreference(true) };
        var intended = new Dictionary<string, PreferenceValue> { ["max_connec"] = new IntegerPreference(321), ["dht"] = new BooleanPreference(false) };
        await session.WritePreferencesAsync(intended, expected, default);
        var post = Assert.Single(api.Calls, c => c.Path.EndsWith("setPreferences"));
        Assert.Equal("POST", post.Method);
        var decoded = WebUtility.UrlDecode(post.Body);
        Assert.Equal("json={\"max_connec\":321,\"dht\":false}", decoded);
        Assert.DoesNotContain("never-persist", decoded);
        api.LoseWriteResponse = true;
        await Assert.ThrowsAsync<QbittorrentException>(() => session.WritePreferencesAsync(intended, expected, default));
        Assert.Equal(2, api.Calls.Count(c => c.Path.EndsWith("setPreferences")));
    }
    [Theory]
    [InlineData("drift")][InlineData("unknown")][InlineData("type")][InlineData("version")]
    public async Task WriteTransportRejectsUnsafeChangesBeforePost(string scenario)
    {
        var api = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var key = scenario == "unknown" ? "proxy_password" : "max_connec";
        var expected = new Dictionary<string, PreferenceValue> { [key] = new IntegerPreference(scenario == "drift" ? 499 : 500) };
        var intended = new Dictionary<string, PreferenceValue> { [key] = scenario == "type" ? new BooleanPreference(false) : new IntegerPreference(321) };
        if (scenario == "version") api.Version = "v5.3.0";
        await Assert.ThrowsAsync<QbittorrentException>(() => session.WritePreferencesAsync(intended, expected, default));
        Assert.DoesNotContain(api.Calls, c => c.Path.EndsWith("setPreferences"));
    }
    [Fact]
    public async Task TransferMetricsPreserveApiUnitsScopeAndMissingIsNotZero()
    {
        var api = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        var metrics = await session.ReadTransferMetricsAsync(default);
        Assert.Equal(1234, Assert.IsType<FreshReading>(metrics[0].Reading).Value);
        Assert.Equal(MetricUnit.BytesPerSecond, metrics[0].Unit); Assert.Equal(MetricScope.Session, metrics[0].Scope);
        Assert.Equal(0, Assert.IsType<FreshReading>(metrics[1].Reading).Value);
        Assert.Equal(17, Assert.IsType<FreshReading>(metrics[2].Reading).Value);
        Assert.Equal(MetricScope.Endpoint, metrics[3].Scope);
        api.TransferJson = "{}";
        metrics = await session.ReadTransferMetricsAsync(default);
        Assert.All(metrics.Take(3), m => Assert.IsType<UnavailableReading>(m.Reading));
        Assert.True(session.IsValidated);
    }
    [Theory]
    [InlineData("true")][InlineData("-1")][InlineData("1.5")][InlineData("9007199254740992")][InlineData("null")]
    public async Task InvalidTransferMetricDoesNotBecomeAValidZero(string value)
    {
        var api = new MockApi { TransferJson = "{\"dl_info_speed\":" + value + "}" };
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        Assert.IsType<ErrorReading>((await session.ReadTransferMetricsAsync(default))[0].Reading);
    }
    [Fact]
    public async Task InterfacesUseCanonicalValueAndRejectAmbiguousDuplicateIds()
    {
        var api = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, api);
        Assert.Equal(new TargetInterface("eth0", "Ethernet"), Assert.Single(await session.ReadInterfacesAsync(default)));
        api.InterfacesJson = """[{"name":"A","value":"same"},{"name":"B","value":"same"}]""";
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadInterfacesAsync(default));
        Assert.Equal(ErrorCodes.SchemaIncompatible, error.Code);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BypassAndApiKeyDoNotSubmitEmptyLogin(bool key)
    {
        var mock = new MockApi();
        Authentication auth = key ? new ApiKeyAuthentication("test-api-key") : new BypassAuthentication();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", auth), default, mock);
        Assert.True(session.IsValidated);
        Assert.Equal(4, mock.Calls.Count);
        Assert.All(mock.Calls, call => { Assert.Equal("GET", call.Method); Assert.Equal(key ? "Bearer test-api-key" : null, call.Authorization); Assert.Equal("http://qb.example.test/", call.Referer); });
        Assert.DoesNotContain("proxy_password", (await session.ReadPreferencesAsync(default)).Keys);
        Assert.NotNull(session.LastRequestDurationMilliseconds);
    }
    [Fact]
    public async Task PasswordLoginOnlyAfterProtectedProbe()
    {
        var mock = new MockApi(true);
        using var session = await QbittorrentSession.ConnectAsync(new("https://qb.example.test", new PasswordAuthentication("test-user", "test-password")), default, mock);
        Assert.Equal("GET", mock.Calls[0].Method);
        Assert.Equal("/api/v2/auth/login", mock.Calls[1].Path);
        Assert.Equal("POST", mock.Calls[1].Method);
        Assert.Contains("username=test-user", mock.Calls[1].Body);
        Assert.Contains("password=test-password", mock.Calls[1].Body);
        Assert.Equal(6, mock.Calls.Count);
    }
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ErrorCodes.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Redirect, ErrorCodes.RedirectRejected)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.ApiUnavailable)]
    public async Task ErrorResponsesAreRedactedAndNeverRetried(HttpStatusCode status, string code)
    {
        var mock = new MockApi(failure: status);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, mock));
        Assert.Equal(code, error.Code);
        Assert.Equal(code, error.Message);
        Assert.Null(error.InnerException);
        Assert.Single(mock.Calls);
    }
    [Fact]
    public async Task CancellationBeforeConnectMakesNoRequest()
    {
        var mock = new MockApi();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), cancellation.Token, mock));
        Assert.Empty(mock.Calls);
    }

    [Fact]
    public async Task MalformedCredentialHeaderNeverEscapesOrReachesTransport()
    {
        var mock = new MockApi();
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => QbittorrentSession.ConnectAsync(
            new("http://qb.example.test", new ApiKeyAuthentication("mock-secret\r\nInjected: value")), default, mock));
        Assert.Equal(ErrorCodes.AuthenticationFailed, error.Message);
        Assert.Empty(mock.Calls);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, ErrorCodes.TlsRejected)]
    [InlineData(HttpRequestError.ConnectionError, ErrorCodes.ApiUnavailable)]
    public async Task TransportExceptionsExposeOnlyStableRedactedCodes(HttpRequestError transportError, string code)
    {
        var mock = new MockApi(transportError: transportError);
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => QbittorrentSession.ConnectAsync(new("https://qb.example.test", new BypassAuthentication()), default, mock));
        Assert.Equal(code, error.Message); Assert.Null(error.InnerException); Assert.Single(mock.Calls);
    }

    [Fact]
    public async Task ApiDeadlineAbortsOneReadWithoutRetry()
    {
        var mock = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, mock);
        mock.BlockPreferences = true;
        var error = await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadPreferencesAsync(default));
        Assert.Equal(ErrorCodes.ApiTimeout, error.Code);
        Assert.Equal(5, mock.Calls.Count);
        Assert.False(session.IsValidated);
    }

    [Fact]
    public async Task RequestsAreSerializedBoundedAndCancelledWithoutLeakingQueueSlots()
    {
        var mock = new MockApi();
        using var session = await QbittorrentSession.ConnectAsync(new("http://qb.example.test", new BypassAuthentication()), default, mock);
        mock.BlockPreferences = true;
        using var cancellation = new CancellationTokenSource();
        var active = session.ReadPreferencesAsync(cancellation.Token);
        await mock.BlockStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting = Enumerable.Range(0, 15).Select(_ => session.ReadPreferencesAsync(cancellation.Token)).ToArray();
        var overflow = await Assert.ThrowsAsync<QbittorrentException>(() => session.ReadPreferencesAsync(cancellation.Token));
        Assert.Equal(ErrorCodes.OperationConflict, overflow.Code);
        Assert.Equal(5, mock.Calls.Count); // Four validation GETs, then exactly one active read.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        foreach (var queued in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(session.IsValidated);
    }

    [Fact]
    public async Task RealLoopbackTransportKeepsLoginCookiesAndReconnectIsIsolated()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var observed = new List<string>();
        var server = Task.Run(async () =>
        {
            // Each connection has a bounded request and is explicitly closed by this mock server.
            for (var index = 0; index < 10; index++)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(deadline.Token) ?? throw new InvalidOperationException();
                var headers = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) headers.Add(line);
                var lengthHeader = headers.FirstOrDefault(header => header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (lengthHeader is not null)
                {
                    var remaining = int.Parse(lengthHeader.Split(':')[1]);
                    var body = new char[remaining];
                    while (remaining > 0)
                    {
                        var read = await reader.ReadAsync(body.AsMemory(0, remaining), deadline.Token);
                        if (read == 0) throw new EndOfStreamException();
                        remaining -= read;
                    }
                }
                observed.Add(first + "\n" + string.Join("\n", headers));
                var path = first.Split(' ')[1];
                var denied = index == 0;
                var response = denied ? "Forbidden" : path.Split('/').Last() switch
                {
                    "login" => "Ok.", "version" => "v5.2.0", "webapiVersion" => "2.15.0",
                    "buildInfo" => "{\"libtorrent\":\"2.0.11\"}", "preferences" => Preferences,
                    _ => throw new InvalidOperationException("Unexpected path")
                };
                var cookie = path.EndsWith("auth/login") ? "Set-Cookie: SID=mock-session; Path=/\r\n" : "";
                var bytes = Encoding.UTF8.GetBytes(response);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(denied ? "403 Forbidden" : "200 OK")}\r\n{cookie}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
            }
        }, deadline.Token);
        using (var first = await QbittorrentSession.ConnectAsync(new(endpoint, new PasswordAuthentication("mock", "test-only")), deadline.Token))
            Assert.True(first.IsValidated);
        using (var second = await QbittorrentSession.ConnectAsync(new(endpoint, new BypassAuthentication()), deadline.Token))
            Assert.True(second.IsValidated);
        await server.WaitAsync(deadline.Token);
        Assert.DoesNotContain("Cookie:", observed[0]);
        Assert.Contains("POST /api/v2/auth/login", observed[1]);
        Assert.All(observed.Skip(2).Take(4), request => Assert.Contains("Cookie: SID=mock-session", request));
        Assert.All(observed.Skip(6), request => Assert.DoesNotContain("Cookie:", request));
        Assert.All(observed, request => Assert.Contains($"Referer: {endpoint}/", request));
    }
}
