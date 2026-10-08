using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QFrey.Core.Workloads;

public sealed record WorkloadCatalogueEntry(string Id, string Label, string FileName, string SourceUrl,
    long SizeBytes, string ExpectedV1InfoHash);
public sealed record WorkloadRecommendation(double SpeedMbps, long RequiredBytes, int RequiredSeconds,
    WorkloadCatalogueEntry? Selected, double UbuntuSeconds, double KaliSeconds);

/// <summary>Known public single-file images used by opt-in measurements. Size is user consent identity.</summary>
public static class WorkloadCatalogue
{
    public const int WarmupSeconds = 10;
    public const int SampleSeconds = 60;
    public const int SafetySeconds = 20;
    public const int RequiredSeconds = WarmupSeconds + SampleSeconds + SafetySeconds;

    private static readonly WorkloadCatalogueEntry[] entries =
    [
        new("ubuntu", "Ubuntu 22.04.5", "ubuntu-22.04.5-desktop-amd64.iso",
            "https://releases.ubuntu.com/22.04.5/ubuntu-22.04.5-desktop-amd64.iso.torrent", 4_762_707_968, ""),
        new("kali", "Kali Linux Everything 2026.2", "kali-linux-2026.2-installer-everything-amd64.iso",
            "https://cdimage.kali.org/kali-2026.2/kali-linux-2026.2-installer-everything-amd64.iso.torrent", 14_522_228_736,
            "1f7482c9dad2653af474a806a93e6c266e1a1b3c")
    ];
    private static readonly IReadOnlyList<WorkloadCatalogueEntry> readOnlyEntries = Array.AsReadOnly(entries);

    public static IReadOnlyList<WorkloadCatalogueEntry> Entries => readOnlyEntries;

    public static WorkloadCatalogueEntry Get(string id) =>
        entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException("WORKLOAD_UNKNOWN", nameof(id));

    public static WorkloadRecommendation Recommend(double downloadMbps)
    {
        if (!double.IsFinite(downloadMbps) || downloadMbps <= 0)
            throw new ArgumentOutOfRangeException(nameof(downloadMbps), "WORKLOAD_SPEED_INVALID");
        var bytesPerSecond = downloadMbps * 1_000_000d / 8d;
        var requiredExact = bytesPerSecond * RequiredSeconds;
        var requiredBytes = requiredExact >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(requiredExact);
        return new(downloadMbps, requiredBytes, RequiredSeconds, entries.FirstOrDefault(x => x.SizeBytes >= requiredBytes),
            entries[0].SizeBytes / bytesPerSecond, entries[1].SizeBytes / bytesPerSecond);
    }

    /// <summary>Stable confirmation identity; any catalogue, source, filename, hash, or size change requires consent again.</summary>
    public static string ConsentIdentity(WorkloadCatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var canonical = string.Join('\n', entry.Id, entry.Label, entry.FileName, entry.SourceUrl,
            entry.SizeBytes.ToString(CultureInfo.InvariantCulture), entry.ExpectedV1InfoHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static WorkloadCatalogueEntry ValidateMetadata(string id, TorrentMetadataInfo metadata)
    {
        var entry = Get(id);
        if (!string.Equals(metadata.Name, entry.FileName, StringComparison.Ordinal) || metadata.TotalBytes != entry.SizeBytes ||
            entry.ExpectedV1InfoHash.Length > 0 && !string.Equals(metadata.V1InfoHash, entry.ExpectedV1InfoHash, StringComparison.Ordinal))
            throw new FormatException("WORKLOAD_METADATA_CHANGED");
        return entry;
    }
}
