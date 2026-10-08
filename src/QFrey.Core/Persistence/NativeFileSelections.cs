using System.Security.Cryptography;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Persistence;

// Only the native picker may register a path. Wire commands receive opaque, short-lived tokens.
public sealed class NativeFileSelections(TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, (string Purpose, string Path, DateTimeOffset Expires)> selections = [];

    public NativeSelection Register(string purpose, string nativePath)
    {
        if (purpose is not ("restore" or "exportJson" or "exportHtml" or "volume")) throw Invalid();
        var path = Path.GetFullPath(nativePath);
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var key in selections.Where(row => row.Value.Expires <= now).Select(row => row.Key).ToArray())
                selections.Remove(key);
            if (selections.Count >= 32) throw new QbittorrentException(ErrorCodes.OperationConflict);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = now.AddMinutes(10);
            selections.Add(token, (purpose, path, expires));
            return new(token, purpose, Path.GetFileName(path), expires);
        }
    }

    private string Consume(string token, string purpose)
    {
        lock (sync)
        {
            if (!selections.Remove(token, out var selected) || selected.Purpose != purpose
                || selected.Expires <= clock.GetUtcNow()) throw Invalid();
            return selected.Path;
        }
    }

    public async Task ExportAsync(string token, ReportFormat format, CycleRecord cycle, Locale locale,
        CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(format)) throw Invalid();
        var path = Consume(token, format == ReportFormat.Json ? "exportJson" : "exportHtml");
        var artifact = format == ReportFormat.Json ? CycleReport.ToJson(cycle) : CycleReport.ToHtml(cycle, locale);
        // Same-directory replacement keeps an existing selected report intact on cancellation/failure.
        var temporary = path + ".qfrey-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(artifact.Bytes, cancellation).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<byte[]> ReadLegacyAsync(string token, CancellationToken cancellation = default)
    {
        var path = Consume(token, "restore");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > LegacyCycleImporter.MaxImportBytes) throw Invalid();
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        if (stream.ReadByte() != -1) throw Invalid();
        return bytes;
    }

    public string ConsumeVolume(string token) => Consume(token, "volume");
    private static QbittorrentException Invalid() => new(ErrorCodes.InvalidCommand);
}
