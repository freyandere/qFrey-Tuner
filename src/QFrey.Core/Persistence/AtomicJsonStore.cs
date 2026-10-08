using System.IO;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Persistence;

public sealed class AtomicJsonStore
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    private readonly string root;

    public AtomicJsonStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory) || rootDirectory.StartsWith("\\\\", StringComparison.Ordinal)
            || rootDirectory.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("A fully qualified local directory is required.", nameof(rootDirectory));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
    }

    public async Task WriteAsync<T>(Guid identity, T value, CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] contents;
        try
        {
            ArgumentNullException.ThrowIfNull(value);
            contents = JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json);
            if (contents.Length > MaxFileBytes) throw new JsonException();
        }
        catch (Exception error) when (IsPersistenceError(error))
        {
            throw Failed();
        }

        var destination = PathFor(identity);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(root);
            temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            temporary = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsPersistenceError(error))
        {
            throw Failed();
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task<T?> ReadAsync<T>(Guid identity, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            await using var source = new FileStream(PathFor(identity), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > MaxFileBytes) throw new JsonException();
            using var buffer = new MemoryStream((int)source.Length);
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var count = await source.ReadAsync(chunk, cancellationToken);
                if (count == 0) break;
                if (buffer.Length + count > MaxFileBytes) throw new JsonException();
                await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
            }
            buffer.Position = 0;
            using var document = await JsonDocument.ParseAsync(buffer,
                new JsonDocumentOptions { MaxDepth = Protocol.Json.MaxDepth }, cancellationToken);
            Protocol.RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Deserialize<T>(Protocol.Json) ?? throw new JsonException();
        }
        catch (FileNotFoundException) { return default; }
        catch (DirectoryNotFoundException) { return default; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsPersistenceError(error))
        {
            throw Failed();
        }
    }

    private string PathFor(Guid identity) => Path.Combine(root, identity.ToString("N") + ".json");

    private static bool IsPersistenceError(Exception error) => error is IOException or UnauthorizedAccessException
        or JsonException or NotSupportedException or ArgumentException or InvalidOperationException;

    private static QbittorrentException Failed() => new(ErrorCodes.PersistenceFailed);
}
