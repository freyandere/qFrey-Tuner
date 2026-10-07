using System.Net;
using System.Net.Sockets;
using System.Text;

// Isolated test fixture only. No qBittorrent installation, downloads, settings or files.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
using var stop = new CancellationTokenSource();
var server = ServeAsync();
await Console.In.ReadLineAsync();
stop.Cancel(); listener.Stop();
await server;

async Task ServeAsync()
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var request = await reader.ReadLineAsync(stop.Token);
            for (var line = await reader.ReadLineAsync(stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(stop.Token)) { }
            var path = request?.Split(' ').ElementAtOrDefault(1)?.Split('?')[0];
            var body = path switch {
                "/api/v2/app/version" => "v5.2.0", "/api/v2/app/webapiVersion" => "2.15.0",
                "/api/v2/app/buildInfo" => """{"libtorrent":"2.0.11"}""",
                "/api/v2/app/preferences" => """{"up_limit":0,"dl_limit":0,"max_connec":500,"max_connec_per_torrent":100,"dht":true,"pex":true,"lsd":false,"encryption":0}""",
                "/api/v2/transfer/info" => """{"dl_info_speed":1048576,"up_info_speed":262144,"dht_nodes":17}""",
                "/api/v2/app/networkInterfaceList" => """[{"name":"Test adapter","value":"test0"}]""",
                _ => null };
            var bytes = Encoding.UTF8.GetBytes(body ?? "Unsupported mock request");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(body is null ? "404 Not Found" : "200 OK")}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, stop.Token); await stream.WriteAsync(bytes, stop.Token);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    catch (SocketException) when (stop.IsCancellationRequested) { }
}
