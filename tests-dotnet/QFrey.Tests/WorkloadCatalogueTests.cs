using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class WorkloadCatalogueTests
{
    [Theory]
    [InlineData(100, "ubuntu")]
    [InlineData(400, "ubuntu")]
    [InlineData(500, "kali")]
    [InlineData(1000, "kali")]
    [InlineData(2500, null)]
    public void ChoosesSmallestImageThatCoversNinetySeconds(double speed, string? expected)
    {
        var result = WorkloadCatalogue.Recommend(speed);
        Assert.Equal((long)Math.Ceiling(speed * 1_000_000d / 8d * 90), result.RequiredBytes);
        Assert.Equal(90, result.RequiredSeconds);
        Assert.Equal(expected, result.Selected?.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidSpeed(double speed) => Assert.Throws<ArgumentOutOfRangeException>(() => WorkloadCatalogue.Recommend(speed));

    [Fact]
    public void SizeBoundaryMatchesPythonCatalogAndConsentBindsImageSize()
    {
        var ubuntu = WorkloadCatalogue.Get("ubuntu");
        var boundary = ubuntu.SizeBytes * 8d / 1_000_000d / WorkloadCatalogue.RequiredSeconds;
        Assert.Equal("ubuntu", WorkloadCatalogue.Recommend(boundary - 0.000001).Selected?.Id);
        Assert.Equal("kali", WorkloadCatalogue.Recommend(boundary + 0.000001).Selected?.Id);
        var changedSize = ubuntu with { SizeBytes = ubuntu.SizeBytes + 1 };
        Assert.NotEqual(WorkloadCatalogue.ConsentIdentity(ubuntu), WorkloadCatalogue.ConsentIdentity(changedSize));
    }

    [Fact]
    public void ValidatesExpectedMetadataAndRejectsChangedSizeOrHash()
    {
        var kali = WorkloadCatalogue.Get("kali");
        var valid = new TorrentMetadataInfo(kali.FileName, kali.SizeBytes, kali.ExpectedV1InfoHash);
        Assert.Same(kali, WorkloadCatalogue.ValidateMetadata("kali", valid));
        Assert.Throws<FormatException>(() => WorkloadCatalogue.ValidateMetadata("kali", valid with { TotalBytes = 1 }));
        Assert.Throws<FormatException>(() => WorkloadCatalogue.ValidateMetadata("kali", valid with { V1InfoHash = new string('0', 40) }));
    }
}
