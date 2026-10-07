using QFrey.Core.Contracts;

namespace QFrey.Core.Tuning;

public static class Calculator
{
    public static OptimizedSettings Calculate(DraftInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        CommandPayloads.ValidateInputs(inputs);
        var network = inputs.Network ?? throw new ArgumentException("Network inputs are required.", nameof(inputs));
        var hardware = inputs.Hardware ?? throw new ArgumentException("Hardware inputs are required.", nameof(inputs));
        var usage = inputs.Usage ?? throw new ArgumentException("Usage inputs are required.", nameof(inputs));
        var isPrivate = usage.TrackerType == TrackerType.Private;
        var isSeedbox = usage.Environment == EnvironmentProfile.Seedbox;
        var isTrueNas = usage.Environment == EnvironmentProfile.Truenas;
        var isNas = usage.Environment == EnvironmentProfile.Nas;
        var isDocker = usage.Environment == EnvironmentProfile.Docker;
        var warnings = new List<CalculatorNotice>();
        var explanations = new Dictionary<string, CalculatorNotice>(StringComparer.Ordinal);

        var uploadLimit = (int)(network.UploadMbps * 1_000_000 / 8 * 0.8 / 1024);
        Explain("upload_limit", "calculator.explanation.uploadLimit");
        Explain("download_limit", "calculator.explanation.downloadLimit");

        int uploadSlotsGlobal, uploadSlotsPerTorrent;
        if (isPrivate)
        {
            uploadSlotsGlobal = Clamp(50, 20, 100);
            uploadSlotsPerTorrent = Clamp(6, 4, 8);
            Explain("upload_slots", "calculator.explanation.privateUploadSlots");
            Warn("calculator.warning.privateTrackerUploadSlots");
        }
        else if (isSeedbox)
        {
            uploadSlotsGlobal = 200;
            uploadSlotsPerTorrent = 50;
            Explain("upload_slots", "calculator.explanation.seedboxUploadSlots");
        }
        else
        {
            (uploadSlotsGlobal, uploadSlotsPerTorrent) = usage.UserRole switch
            {
                UserRole.Seeder => (Math.Max(50, uploadLimit / 8), Math.Max(10, uploadLimit / 20)),
                UserRole.Uploader => (Math.Max(50, uploadLimit / 5), Math.Max(15, uploadLimit / 15)),
                _ => (Math.Max(30, uploadLimit / 10), Math.Max(5, uploadLimit / 30))
            };
            uploadSlotsGlobal = Clamp(uploadSlotsGlobal, 1, 2000);
            uploadSlotsPerTorrent = Clamp(uploadSlotsPerTorrent, 1, 500);
            Explain("upload_slots", "calculator.explanation.roleUploadSlots", ("role", Text(RoleCode(usage.UserRole))));
        }

        int connections, connectionsPerTorrent;
        if (isPrivate)
        {
            connections = 200;
            connectionsPerTorrent = 50;
            Explain("max_connections", "calculator.explanation.privateConnections");
        }
        else if (isSeedbox)
        {
            connections = 2000;
            connectionsPerTorrent = 500;
            Explain("max_connections", "calculator.explanation.seedboxConnections");
            Warn("calculator.warning.seedboxMaxConnections");
        }
        else if (network.DownloadMbps < 100)
        {
            connections = 200;
            connectionsPerTorrent = 50;
            Explain("max_connections", "calculator.explanation.connectionsBelow100");
        }
        else if (network.DownloadMbps < 500)
        {
            connections = 500;
            connectionsPerTorrent = 125;
            Explain("max_connections", "calculator.explanation.connectionsBelow500");
        }
        else
        {
            connections = 1000;
            connectionsPerTorrent = 250;
            Explain("max_connections", "calculator.explanation.connectionsAtLeast500");
        }
        connections = Clamp(connections, 1, 2000);
        connectionsPerTorrent = Clamp(connectionsPerTorrent, 1, 2000);

        var activeDownloads = network.DownloadMbps < 50 ? 2 : network.DownloadMbps < 300 ? 5 : 10;
        var activeUploads = network.DownloadMbps < 50 ? 3 : network.DownloadMbps < 300 ? 8 : 15;
        if (usage.UserRole == UserRole.Seeder) activeUploads = (int)(activeUploads * 1.5);
        var activeTorrents = activeDownloads + activeUploads;
        Explain("queue", "calculator.explanation.queue", ("downloads", Number(activeDownloads)), ("uploads", Number(activeUploads)));

        int diskCache;
        bool osCache = true, preAllocate = true;
        string diskProfile;
        if (isTrueNas)
        {
            diskCache = 0;
            preAllocate = false;
            diskProfile = "trueNas";
            Explain("pre_allocate", "calculator.explanation.trueNasPreAllocation");
            Warn("calculator.warning.trueNasCachePolicy");
        }
        else if (isNas)
        {
            diskCache = 512;
            osCache = false;
            diskProfile = "nas";
            Warn("calculator.warning.nasOsCacheDisabled");
        }
        else if (isDocker)
        {
            diskCache = -1;
            diskProfile = "docker";
        }
        else if (isSeedbox)
        {
            diskCache = hardware.RamGiB >= 32 ? 4096 : hardware.RamGiB >= 16 ? 2048 : 1024;
            diskProfile = "seedbox";
        }
        else if (hardware.StorageType == StorageType.Hdd)
        {
            diskCache = hardware.RamGiB >= 16 ? 2048 : hardware.RamGiB >= 8 ? 1024 : 512;
            diskProfile = "hdd";
        }
        else if (hardware.StorageType == StorageType.SsdSata)
        {
            diskCache = hardware.RamGiB >= 8 ? 512 : 256;
            diskProfile = "ssdSata";
        }
        else
        {
            diskCache = -1;
            diskProfile = "nvme";
        }
        Explain("disk_cache", "calculator.explanation.diskCache", ("profile", Text(diskProfile)), ("cacheMb", Number(diskCache)));

        var asyncIo = 4 * (hardware.IsHybridCpu && hardware.PerformanceCores > 0 ? hardware.PerformanceCores : hardware.CpuCores);
        Explain("async_io", "calculator.explanation.asyncIo", ("threads", Number(asyncIo)));
        Explain("coalesce", "calculator.explanation.coalesce");

        int sendBuffer, lowBuffer, sendFactor, backlog, outgoing;
        TorrentProtocolMode protocol;
        if (isSeedbox)
        {
            sendBuffer = 16000; lowBuffer = 160; sendFactor = 150; backlog = 1024; outgoing = 1000;
            protocol = TorrentProtocolMode.TcpOnly;
            Explain("socket_backlog", "calculator.explanation.seedboxSocketBacklog", ("size", Number(backlog)));
            Explain("protocol", "calculator.explanation.seedboxTcpOnly");
        }
        else if (isDocker)
        {
            sendBuffer = 500; lowBuffer = 16; sendFactor = 100; backlog = 30; outgoing = 100;
            protocol = TorrentProtocolMode.TcpOnly;
            Explain("protocol", "calculator.explanation.dockerTcpOnly");
        }
        else if (network.UploadMbps > 500)
        {
            sendBuffer = 8000; lowBuffer = 160; sendFactor = 120; backlog = 200; outgoing = 500;
            protocol = network.ConnectionType == ConnectionType.Fiber ? TorrentProtocolMode.TcpOnly : TorrentProtocolMode.UtpTcp;
            Explain("send_buffer", "calculator.explanation.sendBuffer", ("sizeKiB", Number(sendBuffer)));
        }
        else if (network.UploadMbps > 100)
        {
            sendBuffer = 5000; lowBuffer = 160; sendFactor = 120; backlog = 100; outgoing = 200;
            protocol = TorrentProtocolMode.UtpTcp;
            Explain("send_buffer", "calculator.explanation.sendBuffer", ("sizeKiB", Number(sendBuffer)));
        }
        else
        {
            sendBuffer = 500; lowBuffer = 16; sendFactor = 100; backlog = 30; outgoing = 100;
            protocol = TorrentProtocolMode.UtpTcp;
            Explain("send_buffer", "calculator.explanation.sendBufferDefault");
        }
        if (network.ConnectionType == ConnectionType.Fiber && !isDocker)
        {
            protocol = TorrentProtocolMode.TcpOnly;
            Explain("protocol", "calculator.explanation.fiberTcpOnly");
        }

        int? listeningPort = null;
        if (network.IspThrottling)
        {
            if (inputs.ProposedPort is not (>= 49152 and <= 65535))
                throw new ArgumentException("A proposed high port is required when ISP throttling is enabled.", nameof(inputs));
            listeningPort = inputs.ProposedPort;
            Warn("calculator.warning.randomPort");
            Explain("port", "calculator.explanation.randomPort");
        }
        else Explain("port", "calculator.explanation.keepPort");

        var encryption = network.IspThrottling ? EncryptionMode.Require : EncryptionMode.Prefer;
        Explain("encryption", network.IspThrottling ? "calculator.explanation.requireEncryption" : "calculator.explanation.preferEncryption");
        if (network.IspThrottling) Warn("calculator.warning.requireEncryption");

        var anonymous = !isPrivate;
        Explain("anonymous", isPrivate ? "calculator.explanation.privateAnonymousDisabled" : "calculator.explanation.publicAnonymousEnabled");
        if (isPrivate) Warn("calculator.warning.privateTrackerAnonymousMode");
        var enableDht = !isPrivate;
        var enablePex = !isPrivate;
        var enableLsd = !isPrivate;
        Explain("dht_pex_lsd", isPrivate ? "calculator.explanation.privateDhtPexLsdDisabled" : "calculator.explanation.publicDhtPexLsdEnabled");
        if (isPrivate) Warn("calculator.warning.privateTrackerDhtPexLsd");

        var networkInterface = "";
        if (network.UseVpn || isDocker)
        {
            if (!string.IsNullOrEmpty(network.VpnInterface))
            {
                networkInterface = network.VpnInterface;
                Warn("calculator.warning.vpnInterfaceBound", ("interface", Text(networkInterface)));
                Explain("vpn_interface", "calculator.explanation.bindInterface", ("interface", Text(networkInterface)));
            }
            else if (isDocker)
            {
                networkInterface = "tun0";
                Warn("calculator.warning.dockerDefaultInterface");
                Explain("vpn_interface", "calculator.explanation.dockerDefaultInterface");
            }
            else Warn("calculator.warning.vpnInterfaceMissing");
        }

        var superSeeding = usage.UserRole == UserRole.Uploader;
        Explain("super_seeding", superSeeding ? "calculator.explanation.superSeedingEnabled" : "calculator.explanation.superSeedingDisabled");
        if (superSeeding) Warn("calculator.warning.superSeeding");

        return new OptimizedSettings(uploadLimit, 0, uploadSlotsGlobal, uploadSlotsPerTorrent,
            connections, connectionsPerTorrent, activeDownloads, activeUploads, activeTorrents,
            diskCache, osCache, preAllocate, asyncIo, true, protocol, sendBuffer, lowBuffer,
            sendFactor, backlog, outgoing, listeningPort, network.IspThrottling, encryption,
            anonymous, enableDht, enablePex, enableLsd, networkInterface, superSeeding,
            [.. warnings], explanations);

        void Warn(string code, params (string Key, MessageParameter Value)[] parameters) => warnings.Add(new(code, Params(parameters)));
        void Explain(string property, string code, params (string Key, MessageParameter Value)[] parameters) => explanations[property] = new(code, Params(parameters));
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(value, maximum));
    private static string RoleCode(UserRole role) => role switch { UserRole.Seeder => "seeder", UserRole.Uploader => "uploader", _ => "leecher" };
    private static NumberParameter Number(double value) => new(value);
    private static TextParameter Text(string value) => new(value);
    private static IReadOnlyDictionary<string, MessageParameter> Params((string Key, MessageParameter Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
}
