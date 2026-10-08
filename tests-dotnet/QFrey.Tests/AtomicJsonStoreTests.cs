using QFrey.Core.Contracts;
using QFrey.Core.Persistence;
using QFrey.Core.Qbittorrent;
using Xunit;

namespace QFrey.Tests;

public sealed class AtomicJsonStoreTests
{
    private sealed record TestRecord(int Revision, string Name);
    private sealed record UnsupportedRecord(Action Callback);

    [Fact]
    public async Task RoundTripsAndAtomicallyOverwritesTypedRecords()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var first = new TestRecord(1, "original");
        var replacement = new TestRecord(2, "replacement");

        await store.WriteAsync(id, first);
        Assert.Equal(first, await store.ReadAsync<TestRecord>(id));
        await store.WriteAsync(id, replacement);

        Assert.Equal(replacement, await store.ReadAsync<TestRecord>(id));
        Assert.Single(Directory.GetFiles(root, id.ToString("N") + ".json"));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task LockedDestinationFailsWithoutChangingOriginal()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var original = new TestRecord(1, "original");
        await store.WriteAsync(id, original);

        var error = await Assert.ThrowsAsync<QbittorrentException>(async () =>
        {
            using var locked = new FileStream(Path.Combine(root, id.ToString("N") + ".json"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            await store.WriteAsync(id, new TestRecord(2, "replacement"));
        });

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(original, await store.ReadAsync<TestRecord>(id));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("{\"revision\":1,")]
    [InlineData("{\"revision\":1,\"name\":\"a\",\"name\":\"b\"}")]
    [InlineData("{\"revision\":1,\"name\":\"ok\",\"extra\":true}")]
    [InlineData("null")]
    public async Task RejectsTruncatedDuplicateUnknownAndNullJson(string contents)
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N") + ".json"), contents);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync<TestRecord>(id));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task RejectsOversizedInput()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var path = Path.Combine(root, id.ToString("N") + ".json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(AtomicJsonStore.MaxFileBytes + 1L);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync<TestRecord>(id));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task RejectsExcessiveDepth()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 17)) + "0" + new string('}', 17);
        await File.WriteAllTextAsync(Path.Combine(root, id.ToString("N") + ".json"), nested);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() => store.ReadAsync<Dictionary<string, object>>(id));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task RejectsOversizedWriteWithoutReplacingOriginal()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var original = new TestRecord(1, "original");
        await store.WriteAsync(id, original);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() =>
            store.WriteAsync(id, new TestRecord(2, new string('x', AtomicJsonStore.MaxFileBytes))));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(original, await store.ReadAsync<TestRecord>(id));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task SerializationFailurePreservesOriginalAndRemovesOwnedTemporaryFile()
    {
        var (store, root) = NewStore();
        var id = Guid.NewGuid();
        var original = new TestRecord(1, "original");
        await store.WriteAsync(id, original);

        var error = await Assert.ThrowsAsync<QbittorrentException>(() =>
            store.WriteAsync(id, new UnsupportedRecord(() => { })));

        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
        Assert.Equal(original, await store.ReadAsync<TestRecord>(id));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task PreCancelledWriteLeavesOriginalAndMissingRecordReturnsNull()
    {
        var (store, _) = NewStore();
        var id = Guid.NewGuid();
        var original = new TestRecord(1, "original");
        await store.WriteAsync(id, original);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WriteAsync(id, new TestRecord(2, "replacement"), cancellation.Token));

        Assert.Equal(original, await store.ReadAsync<TestRecord>(id));
        Assert.Null(await store.ReadAsync<TestRecord>(Guid.NewGuid()));
    }

    [Fact]
    public void RequiresFullyQualifiedLocalRoot()
    {
        Assert.Throws<ArgumentException>(() => new AtomicJsonStore("relative/path"));
    }

    private static (AtomicJsonStore Store, string Root) NewStore()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "pyproject.toml")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Project root not found.");
        var root = Path.Combine(directory.FullName, ".cache", "tests", "atomic-json-store", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (new AtomicJsonStore(root), root);
    }
}
