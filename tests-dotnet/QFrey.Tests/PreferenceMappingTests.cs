using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;
using Xunit;

namespace QFrey.Tests;
public class PreferenceMappingTests
{
    private static readonly OptimizedSettings Settings = Calculator.Calculate(new(
        new(500, 100, ConnectionType.Fiber, true, "wg0", true, InputSource.Manual, InputSource.Manual),
        new(StorageType.Nvme, 16, 8, false, 0, InputSource.Manual),
        new(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), 55000));

    [Fact]
    public void CanonicalPayloadMatchesLegacyFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests-contract"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests-contract/fixtures/legacy/settings-payloads.json")));
        var payloads = new Dictionary<string, object>();
        foreach (var (version, major) in new[] { ("1.2.19", 1), ("2.0.11", 2) })
        {
            var expected = fixture.RootElement.GetProperty(version).GetProperty("payload");
            var current = Compatibility.ReadPreferences(expected, major);
            var mapped = PreferenceMapping.Map(Settings, current, major);
            Assert.Equal(expected.EnumerateObject().Count(), mapped.Values.Count);
            foreach (var property in expected.EnumerateObject())
                Assert.Equal(JsonSerializer.Deserialize<PreferenceValue>(property.Value.GetRawText(), Protocol.Json), mapped.Values[property.Name]);
            Assert.Equal(new IntegerPreference(55000), mapped.Values["listen_port"]);
            Assert.Equal(new BooleanPreference(false), mapped.Values["random_port"]);
            payloads.Add(version, new { payload = mapped.Values });
        }
        if (Environment.GetEnvironmentVariable("QFREY_SCHEMA_EXPORT_PATH") is { Length: > 0 } exportPath)
        {
            var fullPath = Path.GetFullPath(exportPath, directory.FullName);
            var cachePath = Path.GetFullPath(Path.Combine(directory.FullName, ".cache")) + Path.DirectorySeparatorChar;
            Assert.True(fullPath.StartsWith(cachePath, StringComparison.OrdinalIgnoreCase), "Schema exports must stay inside repository .cache");
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(payloads, Protocol.Json), new System.Text.UTF8Encoding(false));
        }
    }

    [Theory]
    [InlineData(1, false, TorrentProtocolMode.UtpTcp, EncryptionMode.Prefer, 0)]
    [InlineData(2, true, TorrentProtocolMode.TcpOnly, EncryptionMode.Require, 1)]
    public void ApiUnitsEnumsAndEngineExclusionsMatchTaggedContract(int major, bool osCache,
        TorrentProtocolMode protocol, EncryptionMode encryption, int enumValue)
    {
        // release-4.6.0 sessionimpl.cpp: API speeds are B/s, buffers stay KiB, disk cache stays MiB.
        // release-5.1.0 session.h: OS cache 0/1 and BTProtocol Both=0, TCP=1.
        // release-4.6.0 sessionimpl.cpp setEncryption: prefer=0, require=1.
        var settings = Settings with { GlobalUploadLimitKibS = 123, GlobalDownloadLimitKibS = 456,
            EnableOsCache = osCache, ProtocolMode = protocol, EncryptionMode = encryption,
            SendBufferWatermarkKb = 789, SendBufferLowWatermarkKb = 23, DiskCacheMb = 64 };
        var current = PreferenceMapping.MapForPlan(settings, new Dictionary<string, PreferenceValue>(), major).Candidates;
        var mapped = PreferenceMapping.Map(settings, current, major).Values;
        Assert.Equal(new IntegerPreference(123 * 1024), mapped["up_limit"]);
        Assert.Equal(new IntegerPreference(456 * 1024), mapped["dl_limit"]);
        Assert.Equal(new IntegerPreference(789), mapped["send_buffer_watermark"]);
        Assert.Equal(new IntegerPreference(23), mapped["send_buffer_low_watermark"]);
        Assert.Equal(new IntegerPreference(osCache ? 1 : 0), mapped["disk_io_read_mode"]);
        Assert.Equal(new IntegerPreference(osCache ? 1 : 0), mapped["disk_io_write_mode"]);
        Assert.Equal(new IntegerPreference(enumValue), mapped["bittorrent_protocol"]);
        Assert.Equal(new IntegerPreference(enumValue), mapped["encryption"]);
        if (major == 1)
        {
            Assert.Equal(new IntegerPreference(64), mapped["disk_cache"]);
            Assert.Equal(new BooleanPreference(settings.CoalesceReadsWrites), mapped["enable_coalesce_read_write"]);
        }
        else
        {
            Assert.False(mapped.ContainsKey("disk_cache"));
            Assert.False(mapped.ContainsKey("enable_coalesce_read_write"));
        }
    }

    [Fact]
    public void MissingPrivacyOrHalfPortGroupBlocksPartialMapping()
    {
        // Build a complete synthetic schema from the legacy settings fixture above.
        var current = new Dictionary<string, PreferenceValue>();
        foreach (var key in new[] { "up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "encryption", "listen_port" }) current[key] = new IntegerPreference(0);
        foreach (var key in new[] { "dht", "pex", "lsd", "random_port" }) current[key] = new BooleanPreference(true);
        Assert.Equal(ErrorCodes.SchemaIncompatible, Assert.Throws<QbittorrentException>(() => PreferenceMapping.Map(Settings, current, 2)).Code);
        current["current_network_interface"] = new StringPreference("");
        current.Remove("random_port");
        Assert.Equal(ErrorCodes.SchemaIncompatible, Assert.Throws<QbittorrentException>(() => PreferenceMapping.Map(Settings, current, 2)).Code);
    }

    [Fact]
    public void SpeedConversionCannotWrapAnOutOfRangeApiInteger()
    {
        Assert.Equal(ErrorCodes.InvalidOverride, Assert.Throws<QbittorrentException>(() =>
            PreferenceMapping.Map(Settings with { GlobalUploadLimitKibS = int.MaxValue }, new Dictionary<string, PreferenceValue>(), 2)).Code);
    }
}
