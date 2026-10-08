using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using QFrey.Core.Contracts;

namespace QFrey.Core.Qbittorrent;

public sealed record ValidatedVersions(string Qbittorrent, string WebApi, string Libtorrent, int LibtorrentMajor);
public sealed class QbittorrentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public static partial class Compatibility
{
    // Stable post/local suffixes are accepted by the Python packaging.version gate too.
    // Prerelease/dev suffixes before '+' remain excluded; local build labels do not widen a minor branch.
    [GeneratedRegex(@"^v?(?<release>\d+\.\d+(?:\.\d+)*)(?:(?:[._-]?(?:post|rev|r)[._-]?\d*)|(?:-\d+))?(?:\+[a-z0-9]+(?:[._-][a-z0-9]+)*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StableVersion();

    public static ValidatedVersions Validate(string qbittorrent, string api, string libtorrent)
    {
        var qb = Parse(qbittorrent);
        var web = Parse(api);
        var lt = Parse(libtorrent);
        if (!(qb.Major == 4 && qb.Minor == 6 || qb.Major == 5 && qb.Minor is >= 0 and <= 2)
            || web.Major != 2 || web.Minor < 8 || lt.Major is not (1 or 2))
            throw new QbittorrentException(ErrorCodes.VersionIncompatible);
        return new(qbittorrent, api, libtorrent, lt.Major);
    }
    private static Version Parse(string value)
    {
        if (value is null || value.Length > 64) throw new QbittorrentException(ErrorCodes.VersionIncompatible);
        var match = StableVersion().Match(value);
        var release = match.Groups["release"].Value.Split('.');
        if (!match.Success || release.Length < 2 || !int.TryParse(release[0], out var major) || !int.TryParse(release[1], out var minor))
            throw new QbittorrentException(ErrorCodes.VersionIncompatible);
        return new Version(major, minor);
    }

    // Only this allowlist can leave the API layer. Raw preferences include proxy/mail credentials.
    private static readonly HashSet<string> BooleanKeys = ["limit_utp_rate", "queueing_enabled", "preallocate_all",
        "anonymous_mode", "dht", "pex", "lsd", "enable_coalesce_read_write", "random_port"];
    private static readonly HashSet<string> IntegerKeys = ["up_limit", "dl_limit", "max_connec", "max_connec_per_torrent",
        "max_uploads", "max_uploads_per_torrent", "max_active_downloads", "max_active_uploads", "max_active_torrents",
        "async_io_threads", "disk_io_read_mode", "disk_io_write_mode", "bittorrent_protocol", "send_buffer_watermark",
        "send_buffer_low_watermark", "send_buffer_watermark_factor", "socket_backlog_size", "connection_speed",
        "encryption", "disk_cache", "listen_port"];
    private static readonly string[] RequiredKeys = ["up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "dht", "pex", "lsd", "encryption"];

    public static IReadOnlyDictionary<string, PreferenceValue> ReadPreferences(JsonElement root, int libtorrentMajor)
    {
        if (root.ValueKind != JsonValueKind.Object || libtorrentMajor is not (1 or 2)) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        var values = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var key = property.Name;
            if (libtorrentMajor == 2 && key is "disk_cache" or "enable_coalesce_read_write") continue;
            if (BooleanKeys.Contains(key))
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
                if (!values.TryAdd(key, new BooleanPreference(property.Value.GetBoolean()))) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            }
            else if (IntegerKeys.Contains(key))
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value)) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
                if (key is "disk_io_read_mode" && value is not (0 or 1)
                    || key is "disk_io_write_mode" or "bittorrent_protocol" or "encryption" && value is not (0 or 1 or 2))
                    throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
                if (!values.TryAdd(key, new IntegerPreference(value))) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            }
            else if (key == "current_network_interface")
            {
                if (property.Value.ValueKind != JsonValueKind.String || !values.TryAdd(key, new StringPreference(property.Value.GetString()!)))
                    throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
            }
        }
        if (RequiredKeys.Any(key => !values.ContainsKey(key))) throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        return new ReadOnlyDictionary<string, PreferenceValue>(values);
    }
}
