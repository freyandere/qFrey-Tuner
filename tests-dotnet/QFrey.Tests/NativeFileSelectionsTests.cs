using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class NativeFileSelectionsTests
{
    [Fact]
    public async Task ExportUsesOnlySelectedDestinationAndTokenIsSingleUse()
    {
        var root = Root();
        var path = Path.Combine(root, "report.json");
        await File.WriteAllTextAsync(path, "old report");
        var selections = new NativeFileSelections();
        var selected = selections.Register("exportJson", path);
        Assert.Equal("report.json", selected.DisplayName);
        Assert.DoesNotContain(root, selected.Token);
        var record = CycleStoreTests.Record(Guid.NewGuid(), new("http://127.0.0.1:8080", "5.1.0", "2.11.0", "2.0.11"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selections.ExportAsync(selected.Token,
            ReportFormat.Json, record, Locale.EnUs, cancelled.Token));
        Assert.Equal("old report", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        await Assert.ThrowsAsync<QbittorrentException>(() => selections.ExportAsync(selected.Token, ReportFormat.Json, record, Locale.EnUs));
        selected = selections.Register("exportJson", path);
        await selections.ExportAsync(selected.Token, ReportFormat.Json, record, Locale.EnUs);
        Assert.Equal(CycleReport.ToJson(record).Bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WrongPurposeExpiryAndOversizedImportDoNotGrantReadAuthority()
    {
        var clock = new Clock();
        var selections = new NativeFileSelections(clock);
        var path = Path.Combine(Root(), "legacy.json");
        await File.WriteAllTextAsync(path, "{}");
        var wrong = selections.Register("exportHtml", path);
        await Assert.ThrowsAsync<QbittorrentException>(() => selections.ReadLegacyAsync(wrong.Token));
        var expired = selections.Register("restore", path);
        clock.Now = clock.Now.AddMinutes(10);
        await Assert.ThrowsAsync<QbittorrentException>(() => selections.ReadLegacyAsync(expired.Token));
        using (var stream = File.OpenWrite(path)) stream.SetLength(LegacyCycleImporter.MaxImportBytes + 1);
        var large = selections.Register("restore", path);
        await Assert.ThrowsAsync<QbittorrentException>(() => selections.ReadLegacyAsync(large.Token));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        var root = Path.Combine(directory!.FullName, ".cache", "tests", "native-selections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
