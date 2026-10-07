using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;

public class CalculatorTests
{
    private static readonly string FixtureDirectory = FindFixtureDirectory();
    [Fact]
    public void DirectCallsRejectUndefinedInputEnums()
    {
        var inputs = new DraftInputs(new(500, 100, (ConnectionType)999, false, "", false, InputSource.Manual, InputSource.Manual),
            new(StorageType.Nvme, 16, 8, false, 0, InputSource.Manual), new(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);
        Assert.Throws<JsonException>(() => Calculator.Calculate(inputs));
    }

    public static IEnumerable<object[]> GoldenCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "legacy", "calculator-matrix.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("id").GetString()!, item.GetProperty("input").GetRawText(), item.GetProperty("output").GetRawText()];
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void MatchesLegacyNumericEnumAndBooleanOutputs(string caseId, string inputJson, string outputJson)
    {
        using var input = JsonDocument.Parse(inputJson);
        using var output = JsonDocument.Parse(outputJson);
        var actual = Calculator.Calculate(ToDraftInputs(input.RootElement));
        var expected = output.RootElement;

        Assert.Equal(I(expected, "global_upload_limit_kib_s"), actual.GlobalUploadLimitKibS);
        Assert.Equal(I(expected, "global_download_limit_kib_s"), actual.GlobalDownloadLimitKibS);
        Assert.Equal(I(expected, "upload_slots_global"), actual.UploadSlotsGlobal);
        Assert.Equal(I(expected, "upload_slots_per_torrent"), actual.UploadSlotsPerTorrent);
        Assert.Equal(I(expected, "max_connections_global"), actual.MaxConnectionsGlobal);
        Assert.Equal(I(expected, "max_connections_per_torrent"), actual.MaxConnectionsPerTorrent);
        Assert.Equal(I(expected, "max_active_downloads"), actual.MaxActiveDownloads);
        Assert.Equal(I(expected, "max_active_uploads"), actual.MaxActiveUploads);
        Assert.Equal(I(expected, "max_active_torrents"), actual.MaxActiveTorrents);
        Assert.Equal(I(expected, "disk_cache_mb"), actual.DiskCacheMb);
        Assert.Equal(B(expected, "enable_os_cache"), actual.EnableOsCache);
        Assert.Equal(B(expected, "pre_allocate_disk"), actual.PreAllocateDisk);
        Assert.Equal(I(expected, "async_io_threads"), actual.AsyncIoThreads);
        Assert.Equal(B(expected, "coalesce_reads_writes"), actual.CoalesceReadsWrites);
        Assert.Equal(expected.GetProperty("protocol_mode").GetString() switch
        {
            "TCP_ONLY" => TorrentProtocolMode.TcpOnly,
            "UTP_TCP" => TorrentProtocolMode.UtpTcp,
            var value => throw new InvalidDataException($"Unknown protocol mode in {caseId}: {value}")
        }, actual.ProtocolMode);
        Assert.Equal(I(expected, "send_buffer_watermark_kb"), actual.SendBufferWatermarkKb);
        Assert.Equal(I(expected, "send_buffer_low_watermark_kb"), actual.SendBufferLowWatermarkKb);
        Assert.Equal(I(expected, "send_buffer_factor"), actual.SendBufferFactor);
        Assert.Equal(I(expected, "socket_backlog_size"), actual.SocketBacklogSize);
        Assert.Equal(I(expected, "outgoing_connections_per_second"), actual.OutgoingConnectionsPerSecond);
        Assert.Equal(expected.GetProperty("encryption_mode").GetString() switch
        {
            "PREFER" => EncryptionMode.Prefer,
            "REQUIRE" => EncryptionMode.Require,
            var value => throw new InvalidDataException($"Unknown encryption mode in {caseId}: {value}")
        }, actual.EncryptionMode);
        Assert.Equal(B(expected, "anonymous_mode"), actual.AnonymousMode);
        Assert.Equal(B(expected, "enable_dht"), actual.EnableDht);
        Assert.Equal(B(expected, "enable_pex"), actual.EnablePex);
        Assert.Equal(B(expected, "enable_lsd"), actual.EnableLsd);
        Assert.Equal(expected.GetProperty("network_interface").GetString(), actual.NetworkInterface);
        Assert.Equal(B(expected, "super_seeding"), actual.SuperSeeding);

        var legacyPort = expected.GetProperty("listening_port").GetString()!;
        var isRandom = legacyPort.StartsWith("Random (", StringComparison.Ordinal);
        Assert.Equal(isRandom, actual.RandomizePort);
        Assert.Equal(isRandom ? int.Parse(legacyPort[8..^1]) : null, actual.ListeningPort);
    }

    [Fact]
    public void RandomPortRequiresAndUsesTheExplicitDraftValue()
    {
        var input = new DraftInputs(
            new NetworkInputs(500, 100, ConnectionType.Fiber, false, "", true, InputSource.Manual, InputSource.Manual),
            new HardwareInputs(StorageType.Nvme, 16, 8, false, 0, InputSource.Manual),
            new UsageInputs(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);
        Assert.Throws<ArgumentException>(() => Calculator.Calculate(input));
        Assert.Equal(55000, Calculator.Calculate(input with { ProposedPort = 55000 }).ListeningPort);
    }

    private static DraftInputs ToDraftInputs(JsonElement input)
    {
        var network = input.GetProperty("network");
        var hardware = input.GetProperty("hardware");
        var usage = input.GetProperty("usage");
        return new DraftInputs(
            new NetworkInputs(D(network, "download_speed_mbps"), D(network, "upload_speed_mbps"),
                EnumValue(network.GetProperty("connection_type").GetString()!, new Dictionary<string, ConnectionType>
                { ["FIBER"] = ConnectionType.Fiber, ["CABLE_DSL"] = ConnectionType.CableDsl, ["MOBILE_4G"] = ConnectionType.Mobile4G }),
                B(network, "use_vpn"), S(network, "vpn_interface"), B(network, "isp_throttling"), InputSource.Manual, InputSource.Manual),
            new HardwareInputs(EnumValue(S(hardware, "storage_type"), new Dictionary<string, StorageType>
                { ["HDD"] = StorageType.Hdd, ["SSD_SATA"] = StorageType.SsdSata, ["NVME"] = StorageType.Nvme }),
                I(hardware, "ram_gb"), I(hardware, "cpu_cores"), B(hardware, "is_hybrid_cpu"), I(hardware, "p_cores"), InputSource.Manual),
            new UsageInputs(EnumValue(S(usage, "tracker_type"), new Dictionary<string, TrackerType>
                { ["PUBLIC"] = TrackerType.Public, ["PRIVATE"] = TrackerType.Private }),
                EnumValue(S(usage, "user_role"), new Dictionary<string, UserRole>
                { ["LEECHER"] = UserRole.Leecher, ["SEEDER"] = UserRole.Seeder, ["UPLOADER"] = UserRole.Uploader }),
                EnumValue(S(usage, "environment"), new Dictionary<string, EnvironmentProfile>
                { ["SYSTEM"] = EnvironmentProfile.System, ["PORTABLE"] = EnvironmentProfile.Portable, ["TRUENAS"] = EnvironmentProfile.Truenas,
                  ["NAS"] = EnvironmentProfile.Nas, ["DOCKER"] = EnvironmentProfile.Docker, ["SEEDBOX"] = EnvironmentProfile.Seedbox })),
            55000);
    }

    private static T EnumValue<T>(string value, IReadOnlyDictionary<string, T> map) => map.TryGetValue(value, out var result)
        ? result : throw new InvalidDataException($"Unknown legacy {typeof(T).Name}: {value}");
    private static int I(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static double D(JsonElement value, string name) => value.GetProperty(name).GetDouble();
    private static bool B(JsonElement value, string name) => value.GetProperty(name).GetBoolean();
    private static string S(JsonElement value, string name) => value.GetProperty(name).GetString()!;

    private static string FindFixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests-contract", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate tests-contract/fixtures from the test output directory.");
    }
}
