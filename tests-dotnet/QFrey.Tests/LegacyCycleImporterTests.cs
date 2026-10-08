using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using Xunit;

namespace QFrey.Tests;

public sealed class LegacyCycleImporterTests
{
    [Fact]
    public void ImportsOnlyKnownLegacyDataAndLeavesUnknownSecretsOut()
    {
        var json = File.ReadAllBytes(Path.Combine(ProjectRoot(), "tests-contract", "fixtures", "legacy", "legacy-cycle.json"));
        var imported = LegacyCycleImporter.Import(json, Guid.NewGuid());

        Assert.Equal("http://127.0.0.1:8080", imported.Target.Endpoint);
        Assert.Equal(500, Assert.IsType<IntegerPreference>(imported.Original["max_connec"]).Value);
        Assert.Null(imported.ObservedReadback);
        Assert.Null(imported.After);
        Assert.Equal(2, imported.Baseline!.Samples!.Length);
        Assert.Contains("LEGACY_APPLY_READBACK_NOT_RECORDED", imported.Limitations);
        Assert.Null(imported.MeasurementPreferences);
    }

    [Fact]
    public void RejectsCredentialUrlTargetMismatchFutureSchemaAndOversizedInput()
    {
        var baseJson = "{\"host\":\"http://127.0.0.1:8080\",\"qBittorrent\":\"5.1.0\",\"api\":\"2.11.0\",\"libtorrent\":\"2.0.11\",\"created_or_updated_utc\":\"2026-10-05T00:00:00Z\"}";
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => LegacyCycleImporter.Import(Encoding.UTF8.GetBytes(baseJson.Replace("http://127", "http://user:pass@127")), id));
        Assert.Throws<InvalidDataException>(() => LegacyCycleImporter.Import(Encoding.UTF8.GetBytes(baseJson), id,
            new TargetIdentity("http://127.0.0.1:8080", "5.2.0", "2.11.0", "2.0.11")));
        Assert.Throws<InvalidDataException>(() => LegacyCycleImporter.Import(Encoding.UTF8.GetBytes(baseJson.Replace("{\"host\"", "{\"schemaVersion\":2,\"host\"")), id));
        Assert.Throws<InvalidDataException>(() => LegacyCycleImporter.Import(new byte[LegacyCycleImporter.MaxImportBytes + 1], id));
    }

    [Fact]
    public void OmitsSecretsUnknownPreferencesAndAbsentHistoricalValues()
    {
        const string json = "{\"host\":\"http://127.0.0.1\",\"qBittorrent\":\"5.1.0\",\"api\":\"2.11.0\",\"libtorrent\":\"2.0.11\",\"created_or_updated_utc\":\"2026-10-05T00:00:00Z\",\"baseline\":{\"preferences\":{\"max_connec\":5,\"web_ui_password\":\"secret\"}},\"original\":{\"web_ui_password\":\"secret\",\"max_connec\":5}}";
        var imported = LegacyCycleImporter.Import(Encoding.UTF8.GetBytes(json), Guid.NewGuid());

        Assert.Null(imported.Baseline!.MeanDownloadMiBPerSecond);
        Assert.DoesNotContain("web_ui_password", imported.Original.Keys);
        Assert.Null(imported.ObservedReadback);
    }

    private static string ProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Project root not found.");
    }
}
