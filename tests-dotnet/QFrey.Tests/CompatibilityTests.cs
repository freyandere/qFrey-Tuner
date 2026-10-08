using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;
public class CompatibilityTests
{
    [Theory]
    [InlineData("v4.6.0", "2.8.0", "1.2.19")]
    [InlineData("v4.6.7", "2.11.0", "2.0.11")]
    [InlineData("v5.0.0", "2.11.0", "2.0.11")]
    [InlineData("v5.1.0", "2.11.0", "2.0.11")]
    [InlineData("v5.2.3", "2.15.1", "1.2.20")]
    [InlineData("v5.2.3.post1", "2.15.1", "1.2.20+vendor.1")]
    [InlineData("v5.2.0+local.dev", "2.15.1.post", "2.0.11.0.1")]
    public void ReviewedStableVersions(string qb, string api, string lt) => Assert.Equal(lt[0] - '0', Compatibility.Validate(qb, api, lt).LibtorrentMajor);
    [Theory]
    [InlineData("v4.5.5", "2.8.0", "1.2.19")]
    [InlineData("v4.7.0", "2.11.0", "2.0.11")]
    [InlineData("v5.3.0", "2.15.1", "2.0.11")]
    [InlineData("v6.0.0", "2.15.1", "2.0.11")]
    [InlineData("v5.2.0rc1", "2.15.1", "2.0.11")]
    [InlineData("unknown", "2.11.0", "2.0.11")]
    [InlineData("v5.1.0", "2.7.0", "2.0.11")]
    [InlineData("v5.1.0", "3.0.0", "2.0.11")]
    [InlineData("v5.1.0", "2.11.0rc1", "2.0.11")]
    [InlineData("v5.1.0", "bad", "2.0.11")]
    [InlineData("v5.1.0", "2.11.0", "3.0.0")]
    [InlineData("v5.1.0", "2.11.0", "2.bad")]
    [InlineData("v5.1.0", "2.11.0", "1.")]
    [InlineData("v5.1.0", "2.11.0", "2.0.11.dev1")]
    [InlineData("v5.1.0", "2.11.0", "2")]
    [InlineData("v5.3.0+local", "2.15.0", "2.0.11")]
    public void UnsupportedVersionsStayClosed(string qb, string api, string lt) => Assert.Equal(ErrorCodes.VersionIncompatible, Assert.Throws<QbittorrentException>(() => Compatibility.Validate(qb, api, lt)).Code);

    private const string Live = """{"up_limit":0,"dl_limit":0,"max_connec":500,"max_connec_per_torrent":100,"dht":true,"pex":true,"lsd":false,"encryption":0,"disk_cache":-1,"enable_coalesce_read_write":true,"current_network_interface":"","proxy_password":"secret","mail_notification_password":"secret"}""";
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void SanitizedBranchPreferences(int major, bool legacy)
    {
        using var json = JsonDocument.Parse(Live);
        var values = Compatibility.ReadPreferences(json.RootElement, major);
        Assert.Equal(legacy, values.ContainsKey("disk_cache"));
        Assert.Equal(legacy, values.ContainsKey("enable_coalesce_read_write"));
        Assert.IsType<BooleanPreference>(values["dht"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(values, Protocol.Json));
        Assert.DoesNotContain("proxy_password", values.Keys);
    }
    [Theory]
    [InlineData("500", "true")]
    [InlineData("500", "500.0")]
    [InlineData("500", "2147483648")]
    [InlineData("\"dht\":true", "\"dht\":1")]
    [InlineData("\"max_connec\":500,", "")]
    [InlineData("\"encryption\":0", "\"encryption\":3")]
    public void LiveSchemaRejectsIncorrectTypesAndRequiredOmissions(string from, string to)
    {
        using var json = JsonDocument.Parse(Live.Replace(from, to));
        Assert.Equal(ErrorCodes.SchemaIncompatible, Assert.Throws<QbittorrentException>(() => Compatibility.ReadPreferences(json.RootElement, 1)).Code);
    }
}
