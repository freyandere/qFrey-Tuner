using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Persistence;

public sealed record LegacySourceCopy(int SchemaVersion, Guid SourceCycleId, string Sha256, byte[] OriginalUtf8Json);

// Preserve exact source bytes only after checking the whole file, including fields discarded by the importer.
public sealed class LegacySourceArchive(string trustedRoot)
{
    private readonly AtomicJsonStore files = new(trustedRoot);
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly Regex SensitiveKey = new("password|passwd|credential|secret|authorization|cookie|api[_-]?key|token|username",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SensitiveText = new("(?:authorization\\s*[:=]|bearer\\s+[A-Za-z0-9]|password\\s*[:=]|api[_-]?key\\s*[:=]|token\\s*[:=])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public async Task SaveAsync(Guid sourceId, ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (sourceId == Guid.Empty || bytes.Length is 0 or > LegacyCycleImporter.MaxImportBytes) throw Invalid();
        // This archive is specifically legacy; future schemas never fall through into it.
        _ = LegacyCycleImporter.Import(bytes, sourceId);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { Check(document.RootElement); }
        catch (RegexMatchTimeoutException) { throw Invalid(); }
        var copy = new LegacySourceCopy(1, sourceId, Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), bytes.ToArray());
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var prior = await files.ReadAsync<LegacySourceCopy>(sourceId, token).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.SchemaVersion != 1 || prior.SourceCycleId != sourceId || prior.Sha256 != copy.Sha256
                    || !prior.OriginalUtf8Json.AsSpan().SequenceEqual(copy.OriginalUtf8Json)) throw Invalid();
                return;
            }
            await files.WriteAsync(sourceId, copy, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (SensitiveKey.IsMatch(property.Name)) throw Invalid();
                Check(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Check(item);
        else if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (SensitiveText.IsMatch(text)) throw Invalid();
            if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
                && (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query))) throw Invalid();
        }
    }

    private static QbittorrentException Invalid() => new(QFrey.Core.Contracts.ErrorCodes.BackupInvalid);
}
