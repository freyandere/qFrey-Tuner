using System.Collections.ObjectModel;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;

namespace QFrey.Core.Tuning;

public sealed record MappedPreferences(IReadOnlyDictionary<string, PreferenceValue> Values,
    IReadOnlyDictionary<string, PreferenceValue> Candidates, PlanOmission[] Omissions);

// Canonical Web API units, matching Python recommended_preferences. This does not authorize a write.
public static class PreferenceMapping
{
    public static MappedPreferences Map(OptimizedSettings settings, IReadOnlyDictionary<string, PreferenceValue> current, int libtorrentMajor)
    {
        var mapped = MapCore(settings, current, libtorrentMajor);
        string[] required = ["up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "dht", "pex", "lsd", "encryption"];
        if (required.Any(key => !mapped.Values.ContainsKey(key))
            || settings.NetworkInterface.Length > 0 && !mapped.Values.ContainsKey("current_network_interface")
            || settings.RandomizePort && (!mapped.Values.ContainsKey("listen_port") || !mapped.Values.ContainsKey("random_port")))
            throw new QbittorrentException(ErrorCodes.SchemaIncompatible);
        return mapped;
    }

    // Preview mode keeps schema omissions visible so PlanBuilder can block required ones.
    public static MappedPreferences MapForPlan(OptimizedSettings settings, IReadOnlyDictionary<string, PreferenceValue> current, int libtorrentMajor) =>
        MapCore(settings, current, libtorrentMajor);

    private static MappedPreferences MapCore(OptimizedSettings settings, IReadOnlyDictionary<string, PreferenceValue> current, int libtorrentMajor)
    {
        if (libtorrentMajor is not (1 or 2)) throw new QbittorrentException(ErrorCodes.VersionIncompatible);
        var values = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, PreferenceValue>(StringComparer.Ordinal);
        var omissions = new List<PlanOmission> { new("super_seeding", false, "PER_TORRENT_SETTING") };
        string[] required = ["up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "dht", "pex", "lsd", "encryption"];
        if (settings.NetworkInterface.Length > 0) required = [.. required, "current_network_interface"];
        if (settings.RandomizePort) required = [.. required, "listen_port", "random_port"];
        Add("up_limit", Speed(settings.GlobalUploadLimitKibS));
        Add("dl_limit", Speed(settings.GlobalDownloadLimitKibS));
        Add("max_connec", settings.MaxConnectionsGlobal); Add("max_connec_per_torrent", settings.MaxConnectionsPerTorrent);
        Add("max_uploads", settings.UploadSlotsGlobal); Add("max_uploads_per_torrent", settings.UploadSlotsPerTorrent);
        Bool("limit_utp_rate", true); Bool("queueing_enabled", true);
        Add("max_active_downloads", settings.MaxActiveDownloads); Add("max_active_uploads", settings.MaxActiveUploads);
        Add("max_active_torrents", settings.MaxActiveTorrents); Bool("preallocate_all", settings.PreAllocateDisk);
        Add("async_io_threads", settings.AsyncIoThreads);
        Add("disk_io_read_mode", settings.EnableOsCache ? 1 : 0); Add("disk_io_write_mode", settings.EnableOsCache ? 1 : 0);
        Add("bittorrent_protocol", settings.ProtocolMode == TorrentProtocolMode.TcpOnly ? 1 : 0);
        Add("send_buffer_watermark", settings.SendBufferWatermarkKb); Add("send_buffer_low_watermark", settings.SendBufferLowWatermarkKb);
        Add("send_buffer_watermark_factor", settings.SendBufferFactor); Add("socket_backlog_size", settings.SocketBacklogSize);
        Add("connection_speed", settings.OutgoingConnectionsPerSecond); Add("encryption", settings.EncryptionMode == EncryptionMode.Require ? 1 : 0);
        Bool("anonymous_mode", settings.AnonymousMode); Bool("dht", settings.EnableDht); Bool("pex", settings.EnablePex); Bool("lsd", settings.EnableLsd);
        if (libtorrentMajor == 1)
        { Add("disk_cache", settings.DiskCacheMb); Bool("enable_coalesce_read_write", settings.CoalesceReadsWrites); }
        else
        {
            Unsupported("disk_cache", new IntegerPreference(settings.DiskCacheMb));
            Unsupported("enable_coalesce_read_write", new BooleanPreference(settings.CoalesceReadsWrites));
        }
        if (settings.NetworkInterface.Length > 0) Put("current_network_interface", new StringPreference(settings.NetworkInterface));
        else omissions.Add(new("current_network_interface", false, "EXISTING_BINDING_PRESERVED"));
        if (settings.RandomizePort)
        {
            if (settings.ListeningPort is not (>= 49152 and <= 65535)) throw new QbittorrentException(ErrorCodes.InvalidOverride);
            // One concrete port chosen in the immutable plan; qBittorrent must not randomize it again.
            Add("listen_port", settings.ListeningPort.Value); Bool("random_port", false);
        }
        return new(new ReadOnlyDictionary<string, PreferenceValue>(values),
            new ReadOnlyDictionary<string, PreferenceValue>(candidates), [.. omissions]);

        void Add(string key, int value) => Put(key, new IntegerPreference(value));
        void Bool(string key, bool value) => Put(key, new BooleanPreference(value));
        void Unsupported(string key, PreferenceValue value)
        {
            candidates.Add(key, value);
            omissions.Add(new(key, false, "LIBTORRENT_2_UNSUPPORTED"));
        }
        void Put(string key, PreferenceValue value)
        {
            candidates.Add(key, value);
            if (current.TryGetValue(key, out var original) && original?.GetType() == value.GetType()) values.Add(key, value);
            else omissions.Add(new(key, required.Contains(key, StringComparer.Ordinal),
                current.ContainsKey(key) ? "SETTING_TYPE_INCOMPATIBLE" : "SETTING_UNSUPPORTED"));
        }
    }
    private static int Speed(int kibPerSecond)
    {
        var bytesPerSecond = (long)kibPerSecond * 1024;
        if (bytesPerSecond < 0 || bytesPerSecond > int.MaxValue) throw new QbittorrentException(ErrorCodes.InvalidOverride);
        return (int)bytesPerSecond;
    }
}
