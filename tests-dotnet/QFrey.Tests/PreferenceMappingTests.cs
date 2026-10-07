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

    [Theory]
    [InlineData("1.2.19", 1)] [InlineData("2.0.11", 2)]
    public void CanonicalPayloadMatchesLegacyFixture(string version, int major)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests-contract"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests-contract/fixtures/legacy/settings-payloads.json")));
        var expected = fixture.RootElement.GetProperty(version).GetProperty("payload");
        var current = Compatibility.ReadPreferences(expected, major);
        var mapped = PreferenceMapping.Map(Settings, current, major);
        Assert.Equal(expected.EnumerateObject().Count(), mapped.Values.Count);
        foreach (var property in expected.EnumerateObject())
            Assert.Equal(JsonSerializer.Deserialize<PreferenceValue>(property.Value.GetRawText(), Protocol.Json), mapped.Values[property.Name]);
        Assert.Equal(new IntegerPreference(55000), mapped.Values["listen_port"]);
        Assert.Equal(new BooleanPreference(false), mapped.Values["random_port"]);
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
