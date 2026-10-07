using System;
using System.Globalization;
using System.Linq;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Workloads;

public enum OwnedWorkloadAction { Stop, Delete, Start }
public enum ServerPathSafety { Unknown, VerifiedNoSymlink, Symlink, Mismatch }

/// <summary>Complete inventory from one validated target; partial lists cannot establish ownership.</summary>
public sealed record WorkloadInventoryEvidence(TargetIdentity Target, bool Complete, TorrentIdentityEvidence[]? Torrents);

/// <summary>Only fields required to prove exact ownership; save paths are opaque server-side strings.</summary>
public sealed record TorrentIdentityEvidence(string? Hash, string? Name, string? TotalBytesDecimal,
    string? ServerSavePath, string? Category, string[]? Tags, ServerPathSafety PathSafety);

/// <summary>A newly generated add intent. It cannot be built from an existing live torrent.</summary>
public sealed record NewWorkloadIntent(TargetIdentity Target, string CatalogueId, TorrentMetadataInfo VerifiedMetadata,
    string Category, string Tag, string ServerSavePath);
public sealed record OwnedWorkloadJournalData(Guid Id, TargetIdentity Target, string CatalogueId,
    string CatalogueConsentIdentity, string Hash, string Name, string TotalBytesDecimal, string Category,
    string Tag, string ServerSavePath);

public sealed record WorkloadJournalDecision(OwnedWorkloadJournal? Journal, string? ReasonCode)
{
    public bool Allowed => Journal is not null;
}

public sealed record WorkloadAuthorization(OwnedWorkloadPermit? Permit, string? ReasonCode)
{
    public bool Allowed => Permit is not null;
}

/// <summary>
/// Durable identity proof created only after a complete preflight shows no matching hash or ownership markers.
/// Journal persistence/flush is the caller's responsibility before sending the add request.
/// </summary>
public sealed class OwnedWorkloadJournal
{
    private const int MaxInventoryTorrents = 50_000;
    private const int MaxTagsPerTorrent = 256;
    private const int MaxTagLength = 256;
    private const int MaxCategoryLength = 256;
    private OwnedWorkloadJournal(Guid id, TargetIdentity target, string catalogueId, string catalogueConsentIdentity,
        string hash, string name, string totalBytesDecimal, string category, string tag, string serverSavePath)
    {
        Id = id;
        Target = target;
        CatalogueId = catalogueId;
        CatalogueConsentIdentity = catalogueConsentIdentity;
        Hash = hash;
        Name = name;
        TotalBytesDecimal = totalBytesDecimal;
        Category = category;
        Tag = tag;
        ServerSavePath = serverSavePath;
    }

    public Guid Id { get; }
    public TargetIdentity Target { get; }
    public string CatalogueId { get; }
    public string CatalogueConsentIdentity { get; }
    public string Hash { get; }
    public string Name { get; }
    public string TotalBytesDecimal { get; }
    public string Category { get; }
    public string Tag { get; }
    public string ServerSavePath { get; }

    public OwnedWorkloadJournalData ToData() => new(Id, Target, CatalogueId, CatalogueConsentIdentity, Hash, Name,
        TotalBytesDecimal, Category, Tag, ServerSavePath);

    /// <summary>Rehydrates a persisted journal only when every field still matches the current official catalogue.</summary>
    public static WorkloadJournalDecision Restore(OwnedWorkloadJournalData? data)
    {
        if (data is null || data.Id == Guid.Empty || !ValidTarget(data.Target) ||
            !ValidMarker(data.Category) || !ValidTag(data.Tag) || !IsCanonicalHash(data.Hash) ||
            !ValidServerPath(data.ServerSavePath))
            return new(null, "WORKLOAD_JOURNAL_INVALID");
        WorkloadCatalogueEntry entry;
        try { entry = WorkloadCatalogue.Get(data.CatalogueId); }
        catch (ArgumentException) { return new(null, "WORKLOAD_JOURNAL_INVALID"); }
        if (!string.Equals(data.CatalogueConsentIdentity, WorkloadCatalogue.ConsentIdentity(entry), StringComparison.Ordinal) ||
            !string.Equals(data.Name, entry.FileName, StringComparison.Ordinal) ||
            !string.Equals(data.TotalBytesDecimal, entry.SizeBytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            entry.ExpectedV1InfoHash.Length > 0 && !string.Equals(data.Hash, entry.ExpectedV1InfoHash, StringComparison.Ordinal))
            return new(null, "WORKLOAD_JOURNAL_INVALID");
        return new(new OwnedWorkloadJournal(data.Id, data.Target, entry.Id, data.CatalogueConsentIdentity,
            data.Hash, data.Name, data.TotalBytesDecimal, data.Category, data.Tag, data.ServerSavePath), null);
    }

    public static WorkloadJournalDecision CreateNewAddIntent(NewWorkloadIntent intent, WorkloadInventoryEvidence preflight)
    {
        if (intent is null || preflight is null || intent.Target is null || intent.VerifiedMetadata is null)
            return new(null, "WORKLOAD_OWNERSHIP_EVIDENCE_MISSING");
        if (!ValidTarget(intent.Target) || !ValidTarget(preflight.Target) || !SameTarget(intent.Target, preflight.Target))
            return new(null, "WORKLOAD_TARGET_MISMATCH");
        var inventoryError = ValidateInventory(preflight);
        if (inventoryError is not null) return new(null, inventoryError);

        WorkloadCatalogueEntry entry;
        try { entry = WorkloadCatalogue.ValidateMetadata(intent.CatalogueId, intent.VerifiedMetadata); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        { return new(null, "WORKLOAD_METADATA_NOT_VERIFIED"); }

        if (!ValidMarker(intent.Category) || !ValidTag(intent.Tag) || !ValidServerPath(intent.ServerSavePath))
            return new(null, "WORKLOAD_INTENT_INVALID");
        var hash = intent.VerifiedMetadata.V1InfoHash;
        if (!IsCanonicalHash(hash)) return new(null, "WORKLOAD_METADATA_NOT_VERIFIED");

        if (preflight.Torrents!.Any(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.Category, intent.Category, StringComparison.Ordinal)
            || x.Tags?.Contains(intent.Tag, StringComparer.Ordinal) == true))
            return new(null, "WORKLOAD_ALREADY_PRESENT");

        return new(new OwnedWorkloadJournal(Guid.NewGuid(), intent.Target, entry.Id, WorkloadCatalogue.ConsentIdentity(entry),
            hash, entry.FileName, entry.SizeBytes.ToString(CultureInfo.InvariantCulture), intent.Category, intent.Tag,
            intent.ServerSavePath), null);
    }

    public WorkloadAuthorization Authorize(OwnedWorkloadAction action, TargetIdentity currentTarget,
        WorkloadInventoryEvidence inventory)
    {
        if (!Enum.IsDefined(action)) return new(null, "WORKLOAD_ACTION_INVALID");
        if (!ValidTarget(currentTarget) || !SameTarget(Target, currentTarget) || inventory is null ||
            !ValidTarget(inventory.Target) || !SameTarget(Target, inventory.Target))
            return new(null, "WORKLOAD_TARGET_MISMATCH");
        var inventoryError = ValidateInventory(inventory);
        if (inventoryError is not null) return new(null, inventoryError);
        var restored = Restore(ToData());
        if (!restored.Allowed || Id == Guid.Empty)
            return new(null, "WORKLOAD_JOURNAL_INVALID");

        var hashMatches = inventory.Torrents!.Where(x => string.Equals(x.Hash, Hash, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hashMatches.Length == 0) return new(null, "WORKLOAD_NOT_FOUND");
        if (hashMatches.Length != 1) return new(null, "WORKLOAD_AMBIGUOUS");
        var live = hashMatches[0];
        if (inventory.Torrents!.Any(x => !ReferenceEquals(x, live) &&
            (string.Equals(x.Category, Category, StringComparison.Ordinal) || x.Tags?.Contains(Tag, StringComparer.Ordinal) == true)))
            return new(null, "WORKLOAD_AMBIGUOUS");
        if (live.Tags is null || !live.Tags.Contains(Tag, StringComparer.Ordinal)) return new(null, "WORKLOAD_TAG_MISMATCH");
        if (!string.Equals(live.Category, Category, StringComparison.Ordinal)) return new(null, "WORKLOAD_CATEGORY_MISMATCH");
        if (!string.Equals(live.Name, Name, StringComparison.Ordinal)) return new(null, "WORKLOAD_NAME_MISMATCH");
        if (!string.Equals(live.TotalBytesDecimal, TotalBytesDecimal, StringComparison.Ordinal)) return new(null, "WORKLOAD_SIZE_MISMATCH");
        if (!string.Equals(live.ServerSavePath, ServerSavePath, StringComparison.Ordinal)) return new(null, "WORKLOAD_PATH_MISMATCH");
        if (action == OwnedWorkloadAction.Delete)
        {
            if (live.PathSafety == ServerPathSafety.Symlink) return new(null, "WORKLOAD_PATH_SYMLINK");
            if (live.PathSafety != ServerPathSafety.VerifiedNoSymlink) return new(null, "WORKLOAD_PATH_UNVERIFIED");
        }

        return new(new OwnedWorkloadPermit(Id, action, Target, Hash, Category, Tag, ServerSavePath, Name, TotalBytesDecimal), null);
    }

    private static bool ValidTarget(TargetIdentity? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.Endpoint) || target.Endpoint.Length > 2048
            || !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || target.Endpoint.IndexOfAny(['?', '#']) >= 0
            || string.IsNullOrWhiteSpace(target.QbittorrentVersion) || target.QbittorrentVersion.Length > 64
            || string.IsNullOrWhiteSpace(target.ApiVersion) || target.ApiVersion.Length > 64
            || string.IsNullOrWhiteSpace(target.LibtorrentVersion) || target.LibtorrentVersion.Length > 64) return false;
        try { Compatibility.Validate(target.QbittorrentVersion, target.ApiVersion, target.LibtorrentVersion); return true; }
        catch (QbittorrentException) { return false; }
    }

    private static string? ValidateInventory(WorkloadInventoryEvidence inventory)
    {
        if (!inventory.Complete || inventory.Torrents is null || inventory.Torrents.Length > MaxInventoryTorrents)
            return "WORKLOAD_INVENTORY_INCOMPLETE";
        var hashes = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var torrent in inventory.Torrents)
        {
            if (torrent is null || !IsHexHash(torrent.Hash) || torrent.Category is null || torrent.Category.Length > MaxCategoryLength
                || torrent.Tags is null || torrent.Tags.Length > MaxTagsPerTorrent
                || torrent.Tags.Any(tag => tag is null || tag.Length > MaxTagLength)
                || torrent.Name?.Length > 256 || torrent.TotalBytesDecimal?.Length > 20 || torrent.ServerSavePath?.Length > 2048)
                return "WORKLOAD_INVENTORY_INCOMPLETE";
            if (!hashes.Add(torrent.Hash!)) return "WORKLOAD_AMBIGUOUS";
        }
        return null;
    }

    private static bool SameTarget(TargetIdentity left, TargetIdentity right) =>
        string.Equals(left.Endpoint, right.Endpoint, StringComparison.Ordinal) &&
        string.Equals(left.QbittorrentVersion, right.QbittorrentVersion, StringComparison.Ordinal) &&
        string.Equals(left.ApiVersion, right.ApiVersion, StringComparison.Ordinal) &&
        string.Equals(left.LibtorrentVersion, right.LibtorrentVersion, StringComparison.Ordinal);

    private static bool ValidMarker(string? value) => value is not null && value.StartsWith("qfrey-test-", StringComparison.Ordinal) &&
        value.Length == 43 && value.AsSpan(11).ToArray().All(IsLowerHex);

    private static bool ValidTag(string? value) => ValidMarker(value);
    private static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static bool IsCanonicalHash(string? hash) => hash is { Length: 40 } && hash.All(IsLowerHex);
    private static bool IsHexHash(string? hash) => hash is { Length: 40 } && hash.All(Uri.IsHexDigit);

    private static bool ValidServerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl)) return false;
        var driveRooted = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '/' or '\\';
        var posixRooted = path[0] == '/';
        var uncRooted = path.StartsWith("\\\\", StringComparison.Ordinal) && path.Length > 2;
        if (!driveRooted && !posixRooted && !uncRooted) return false;
        return !path.Split(['/', '\\'], StringSplitOptions.None).Any(component => component is "." or "..");
    }
}

/// <summary>Operation-scoped result of comparing a creation journal with current, complete server evidence.</summary>
public sealed class OwnedWorkloadPermit
{
    internal OwnedWorkloadPermit(Guid journalId, OwnedWorkloadAction action, TargetIdentity target, string hash,
        string category, string tag, string serverSavePath, string name, string totalBytesDecimal)
    {
        JournalId = journalId;
        Action = action;
        Target = target;
        Hash = hash;
        Category = category;
        Tag = tag;
        ServerSavePath = serverSavePath;
        Name = name;
        TotalBytesDecimal = totalBytesDecimal;
    }

    public Guid JournalId { get; }
    public OwnedWorkloadAction Action { get; }
    public TargetIdentity Target { get; }
    public string Hash { get; }
    public string Category { get; }
    public string Tag { get; }
    public string ServerSavePath { get; }
    public string Name { get; }
    public string TotalBytesDecimal { get; }
}
