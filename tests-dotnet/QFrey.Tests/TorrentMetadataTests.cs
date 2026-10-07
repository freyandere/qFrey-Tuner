using System.Security.Cryptography;
using System.Text;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class TorrentMetadataTests
{
    private const string Name = "ubuntu-22.04.5-desktop-amd64.iso";

    [Fact]
    public void ParsesSingleFileAndHashesExactInfoSlice()
    {
        var raw = Torrent(Name, 123);
        var info = Encoding.ASCII.GetBytes($"d6:lengthi123e4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}e");
        var parsed = TorrentMetadata.Parse(raw, Name);

        Assert.Equal(Name, parsed.Name);
        Assert.Equal(123, parsed.TotalBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(info)), parsed.V1InfoHash);
    }

    [Theory]
    [InlineData("../ubuntu.iso")]
    [InlineData("folder/ubuntu.iso")]
    [InlineData("folder\\ubuntu.iso")]
    [InlineData("C:ubuntu.iso")]
    public void RejectsUnsafeOrUnexpectedNames(string name) =>
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Torrent(name, 1), name));

    [Fact]
    public void RejectsMultiFileDuplicateUnsortedAndTrailingData()
    {
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.ASCII.GetBytes(
            $"d4:infod5:filesle6:lengthi1e4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}ee"), Name));
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.ASCII.GetBytes(
            $"d4:infod6:lengthi1e6:lengthi2e4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}ee"), Name));
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.ASCII.GetBytes(
            $"d4:infod4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}6:lengthi1eee"), Name));
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse([.. Torrent(Name, 1), (byte)'x'], Name));
    }

    [Fact]
    public void RejectsMalformedNumbersNestingAndOversizedInput()
    {
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(Encoding.ASCII.GetBytes("d4:infod6:lengthi01e4:name" + Encoding.UTF8.GetByteCount(Name) + ":" + Name + "ee"), Name));
        var tooDeep = Encoding.ASCII.GetBytes("d4:info" + new string('l', TorrentMetadata.MaximumDepth + 1) + "ee" + new string('e', TorrentMetadata.MaximumDepth + 1) + "e");
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(tooDeep, Name));
        Assert.Throws<FormatException>(() => TorrentMetadata.Parse(new byte[TorrentMetadata.MaximumBytes + 1], Name));
    }

    [Fact]
    public void RejectsV2InsteadOfClaimingV1Compatibility()
    {
        var raw = Encoding.ASCII.GetBytes($"d4:infod6:lengthi1e12:meta versioni2e4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}ee");
        Assert.Throws<NotSupportedException>(() => TorrentMetadata.Parse(raw, Name));
    }

    private static byte[] Torrent(string name, long length) => Encoding.UTF8.GetBytes(
        $"d4:infod6:lengthi{length}e4:name{Encoding.UTF8.GetByteCount(name)}:{name}ee");
}
