using System.Text;
using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class OwnedWorkloadStoreTests
{
    private static readonly TargetIdentity Target = new("http://127.0.0.1:8080/", "v5.2.0", "2.15.0", "2.0.11");
    private static readonly WorkloadCatalogueEntry Catalogue = WorkloadCatalogue.Get("ubuntu");
    private const string Name = "ubuntu-22.04.5-desktop-amd64.iso";

    [Fact]
    public async Task PersistsOnlyValidatedJournalAndRoundTripsAfterStoreRecreation()
    {
        var (store, root) = NewStore();
        var journal = CreateJournal();

        await store.SaveAsync(journal);
        var reopened = new OwnedWorkloadStore(root);
        var restored = await reopened.ReadAsync(journal.Id);

        Assert.Equal(journal.ToData(), restored!.ToData());
        Assert.True(File.Exists(Path.Combine(root, journal.Id.ToString("N") + ".json")));
        var json = await File.ReadAllTextAsync(Path.Combine(root, journal.Id.ToString("N") + ".json"));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await reopened.ReadAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ExistingIdCannotBeReplacedWithDifferentTargetOrJournalIdentity()
    {
        var (store, _) = NewStore();
        var original = CreateJournal();
        await store.SaveAsync(original);
        var replacement = OwnedWorkloadJournal.Restore(original.ToData() with
        { Target = Target with { Endpoint = "http://other-host:8080/" } }).Journal!;

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.SaveAsync(replacement));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(original.ToData(), (await store.ReadAsync(original.Id))!.ToData());
    }

    [Fact]
    public async Task RecoveryEnumerationIsBoundedAndRejectsUnexpectedJournalNames()
    {
        var (store, root) = NewStore();
        var first = CreateJournal();
        await store.SaveAsync(first);
        Assert.Equal(first.ToData(), Assert.Single(await store.ReadAllAsync()).ToData());
        await store.SaveAsync(CreateJournal());
        await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAllAsync(maximum: 1));
        await File.WriteAllTextAsync(Path.Combine(root, "unexpected.json"), "{}");
        await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAllAsync());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"id\":\"00000000000000000000000000000000\",\"extra\":true}")]
    public async Task CorruptOrUnknownShapeFailsClosed(string contents)
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N") + ".json"), contents);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync(id));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task OversizedJournalFailsClosed()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        await using (var file = new FileStream(Path.Combine(root, id.ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write))
            file.SetLength(AtomicJsonStore.MaxFileBytes + 1L);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync(id));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task CancellationDoesNotReplaceAnExistingJournal()
    {
        var (store, root) = NewStore();
        var journal = CreateJournal();
        await store.SaveAsync(journal);
        var path = Path.Combine(root, journal.Id.ToString("N") + ".json");
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(journal, cancellation.Token));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    private static OwnedWorkloadJournal CreateJournal()
    {
        var torrent = Encoding.UTF8.GetBytes($"d4:infod6:lengthi{Catalogue.SizeBytes}e4:name{Encoding.UTF8.GetByteCount(Name)}:{Name}ee");
        var metadata = TorrentMetadata.Parse(torrent, Name);
        var nonce = Guid.NewGuid().ToString("N");
        var marker = "qfrey-test-" + nonce;
        var intent = new NewWorkloadIntent(Target, Catalogue.Id, metadata, marker, marker, "/srv/qfrey-test");
        return OwnedWorkloadJournal.CreateNewAddIntent(intent, new(Target, true, [])).Journal!;
    }

    private static (OwnedWorkloadStore Store, string Root) NewStore()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Project root not found.");
        var root = Path.Combine(directory.FullName, ".cache", "tests", "owned-workload-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (new OwnedWorkloadStore(root), root);
    }
}
