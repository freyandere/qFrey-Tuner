using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Workloads;

namespace QFrey.Core.Qbittorrent;

public enum OwnedWorkloadAddStatus { AcceptedUnverified }
public sealed record OwnedWorkloadAddResult(string Hash, OwnedWorkloadAddStatus Status);

public sealed partial class QbittorrentSession
{
    private const int MaxOwnedInventory = 50_000;

    /// <summary>Fresh, bounded read-only inventory for ownership recovery. It never grants write authority.</summary>
    internal async Task<WorkloadInventoryEvidence> ReadRecoveryOwnedWorkloadInventoryAsync(CancellationToken token)
    {
        await RevalidateVersionsCoreAsync(token).ConfigureAwait(false);
        return await ReadOwnedWorkloadInventoryCoreAsync(token, recoveryRead: true).ConfigureAwait(false);
    }

    /// <summary>Readback after uncertain writes; exact saved versions are checked without restoring write authority.</summary>
    internal async Task<TorrentTelemetryEvidence> ReadRecoveryOwnedWorkloadMetricsAsync(
        IReadOnlyCollection<string> selectedHashes, CancellationToken token)
    {
        await RevalidateVersionsCoreAsync(token).ConfigureAwait(false);
        var response = await SendAsync(HttpMethod.Get, "torrents/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        return TorrentTelemetry.Aggregate(document.RootElement, selectedHashes, time.GetUtcNow());
    }

    /// <summary>
    /// Adds only catalogue-verified metadata after a fresh complete preflight. The journal must already be durable.
    /// A successful response is accepted, not verified; callers must read the server inventory before claiming ownership.
    /// </summary>
    internal async Task<OwnedWorkloadAddResult> AddOwnedWorkloadAsync(OwnedWorkloadJournal journal,
        ReadOnlyMemory<byte> torrentBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var restored = OwnedWorkloadJournal.Restore(journal.ToData());
        if (!restored.Allowed || !SameTarget(CurrentTarget(), journal.Target))
            throw new QbittorrentException(ErrorCodes.InvalidCommand);
        WorkloadCatalogueEntry entry;
        TorrentMetadataInfo metadata;
        try
        {
            entry = WorkloadCatalogue.Get(journal.CatalogueId);
            metadata = TorrentMetadata.Parse(torrentBytes.Span, entry.FileName);
            entry = WorkloadCatalogue.ValidateMetadata(entry.Id, metadata);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or NotSupportedException)
        { throw new QbittorrentException(ErrorCodes.InvalidCommand); }
        if (!string.Equals(metadata.Name, journal.Name, StringComparison.Ordinal)
            || !string.Equals(metadata.TotalBytes.ToString(CultureInfo.InvariantCulture), journal.TotalBytesDecimal, StringComparison.Ordinal)
            || !string.Equals(metadata.V1InfoHash, journal.Hash, StringComparison.Ordinal))
            throw new QbittorrentException(ErrorCodes.InvalidCommand);

        await RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (!SameTarget(CurrentTarget(), journal.Target)) throw new QbittorrentException(ErrorCodes.SessionStale);
        var inventory = await ReadOwnedWorkloadInventoryCoreAsync(token).ConfigureAwait(false);
        var preflight = OwnedWorkloadJournal.CreateNewAddIntent(
            new(journal.Target, entry.Id, metadata, journal.Category, journal.Tag, journal.ServerSavePath), inventory);
        if (!preflight.Allowed || preflight.Journal!.Hash != journal.Hash || preflight.Journal.Category != journal.Category
            || preflight.Journal.Tag != journal.Tag || preflight.Journal.ServerSavePath != journal.ServerSavePath)
            throw new QbittorrentException(preflight.ReasonCode == "WORKLOAD_ALREADY_PRESENT" ? ErrorCodes.OperationConflict : ErrorCodes.InvalidCommand);

        token.ThrowIfCancellationRequested();
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(torrentBytes.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        multipart.Add(file, "torrents", journal.Name);
        multipart.Add(new StringContent(journal.ServerSavePath, Encoding.UTF8), "savepath");
        multipart.Add(new StringContent(journal.Category, Encoding.UTF8), "category");
        multipart.Add(new StringContent(journal.Tag, Encoding.UTF8), "tags");
        multipart.Add(new StringContent("false", Encoding.UTF8), "autoTMM");
        multipart.Add(new StringContent("true", Encoding.UTF8), Versions.Qbittorrent.StartsWith("v4.", StringComparison.OrdinalIgnoreCase) ? "paused" : "stopped");
        await SendAsync(HttpMethod.Post, "torrents/add", multipart, token).ConfigureAwait(false);
        return new(journal.Hash, OwnedWorkloadAddStatus.AcceptedUnverified);
    }

    /// <summary>Freshly proves identity before one non-retried action. Delete always keeps deleteFiles=false.</summary>
    internal async Task ChangeOwnedWorkloadAsync(OwnedWorkloadJournal journal, OwnedWorkloadAction action,
        bool deleteFiles, Func<string, CancellationToken, Task<ServerPathSafety>>? pathSafetyEvidence,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        if (!Enum.IsDefined(action) || deleteFiles)
            throw new QbittorrentException(ErrorCodes.InvalidCommand);
        var restored = OwnedWorkloadJournal.Restore(journal.ToData());
        if (!restored.Allowed || !SameTarget(CurrentTarget(), journal.Target))
            throw new QbittorrentException(ErrorCodes.InvalidCommand);

        await RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (!SameTarget(CurrentTarget(), journal.Target)) throw new QbittorrentException(ErrorCodes.SessionStale);
        var inventory = await ReadOwnedWorkloadInventoryCoreAsync(token).ConfigureAwait(false);
        if (action == OwnedWorkloadAction.Delete && pathSafetyEvidence is not null && inventory.Torrents is not null)
        {
            var index = Array.FindIndex(inventory.Torrents, item => string.Equals(item.Hash, journal.Hash, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var item = inventory.Torrents[index];
                if (string.Equals(item.ServerSavePath, journal.ServerSavePath, StringComparison.Ordinal))
                    inventory.Torrents[index] = item with { PathSafety = await pathSafetyEvidence(journal.ServerSavePath, token).ConfigureAwait(false) };
            }
        }
        var authorization = journal.Authorize(action, CurrentTarget(), inventory);
        if (!authorization.Allowed) throw new QbittorrentException(ErrorCodes.OperationConflict);
        token.ThrowIfCancellationRequested();

        if (action == OwnedWorkloadAction.Delete)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["hashes"] = authorization.Permit!.Hash, ["deleteFiles"] = "false" });
            await SendAsync(HttpMethod.Post, "torrents/delete", form, token).ConfigureAwait(false);
            return;
        }

        var isLegacy = Versions.Qbittorrent.StartsWith("v4.", StringComparison.OrdinalIgnoreCase);
        var path = action switch
        {
            OwnedWorkloadAction.Stop when isLegacy => "torrents/pause",
            OwnedWorkloadAction.Stop => "torrents/stop",
            OwnedWorkloadAction.Start when isLegacy => "torrents/resume",
            OwnedWorkloadAction.Start => "torrents/start",
            _ => throw new QbittorrentException(ErrorCodes.InvalidCommand)
        };
        using var request = new FormUrlEncodedContent(new Dictionary<string, string> { ["hashes"] = authorization.Permit!.Hash });
        await SendAsync(HttpMethod.Post, path, request, token).ConfigureAwait(false);
    }

    private async Task<WorkloadInventoryEvidence> ReadOwnedWorkloadInventoryCoreAsync(CancellationToken token,
        bool recoveryRead = false)
    {
        if (!recoveryRead && !IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "torrents/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxOwnedInventory)
            throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        var items = new List<TorrentIdentityEvidence>(root.GetArrayLength());
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in root.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("total_size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetUInt64(out var bytes)
                || !row.TryGetProperty("save_path", out var savePath) || savePath.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("category", out var category) || category.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.String)
                throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            // The tagged WebUI serializer joins tags with ", ". Preserve exact marker detection in every position.
            string[] tagValues = tags.GetString()!.Length == 0 ? [] : tags.GetString()!.Split(',', StringSplitOptions.TrimEntries);
            if (tagValues.Length > 256 || tagValues.Any(tag => tag.Length > 256))
                throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            if (hash.GetString() is not { Length: 40 } hashText || !hashText.All(Uri.IsHexDigit)
                || !hashes.Add(hashText) || name.GetString()!.Length > 256
                || savePath.GetString()!.Length > 2048 || category.GetString()!.Length > 256)
                throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            items.Add(new(hash.GetString(), name.GetString(), bytes.ToString(CultureInfo.InvariantCulture),
                savePath.GetString(), category.GetString(), tagValues, ServerPathSafety.Unknown));
        }
        return new(CurrentTarget(), true, [.. items]);
    }

    private TargetIdentity CurrentTarget() => new(Endpoint.AbsoluteUri, Versions.Qbittorrent,
        Versions.WebApi, Versions.Libtorrent);

    private static bool SameTarget(TargetIdentity a, TargetIdentity b) => a == b;
}
