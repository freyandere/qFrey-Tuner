using System.Text;
using System.Text.Json;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class LegacySourceArchiveTests
{
    private static byte[] Source(string extra = "") => Encoding.UTF8.GetBytes("""
        {"host":"http://127.0.0.1:8080","qBittorrent":"v5.1.0","api":"2.11.0","libtorrent":"2.0.11",
        "created_or_updated_utc":"2026-10-07T00:00:00Z","original":{"max_connec":500},"applied":{"max_connec":800}
        """ + extra + "}");

    [Fact]
    public async Task ImmutableArchivePreservesExactBytesAndRejectsReplacement()
    {
        var root = Root(); var archive = new LegacySourceArchive(root); var id = Guid.NewGuid(); var bytes = Source();
        await archive.SaveAsync(id, bytes);
        var copy = await new AtomicJsonStore(root).ReadAsync<LegacySourceCopy>(id);
        Assert.Equal(bytes, copy!.OriginalUtf8Json);
        await archive.SaveAsync(id, bytes);
        await Assert.ThrowsAsync<QbittorrentException>(() => archive.SaveAsync(id, Source(",\"extra\":1")));
        Assert.Equal(bytes, (await new AtomicJsonStore(root).ReadAsync<LegacySourceCopy>(id))!.OriginalUtf8Json);
    }

    [Theory]
    [InlineData(",\"ignored\":{\"password\":\"synthetic-secret\"}")]
    [InlineData(",\"ignored\":{\"token\":\"synthetic-secret\"}")]
    [InlineData(",\"ignored\":{\"authToken\":\"synthetic-secret\"}")]
    [InlineData(",\"ignored\":\"access_token=synthetic-secret\"")]
    [InlineData(",\"ignored\":\"Authorization: Bearer synthetic-secret\"")]
    [InlineData(",\"ignored\":\"https://user:synthetic-secret@example.test/\"")]
    [InlineData(",\"schemaVersion\":99")]
    public async Task CredentialsInDiscardedFieldsAndFutureSchemasAreNeverArchived(string extra)
    {
        var root = Root(); var archive = new LegacySourceArchive(root);
        await Assert.ThrowsAnyAsync<Exception>(() => archive.SaveAsync(Guid.NewGuid(), Source(extra)));
        Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root).Any());
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, ".cache", "tests", "legacy-source", Guid.NewGuid().ToString("N"));
    }
}
