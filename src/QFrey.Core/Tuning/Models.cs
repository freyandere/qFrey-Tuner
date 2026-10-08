using QFrey.Core.Contracts;

namespace QFrey.Core.Tuning;

public enum TorrentProtocolMode { TcpOnly, UtpTcp }
public enum EncryptionMode { Prefer, Require }

public sealed record CalculatorNotice(string Code, IReadOnlyDictionary<string, MessageParameter> Parameters);

public sealed record OptimizedSettings(
    int GlobalUploadLimitKibS,
    int GlobalDownloadLimitKibS,
    int UploadSlotsGlobal,
    int UploadSlotsPerTorrent,
    int MaxConnectionsGlobal,
    int MaxConnectionsPerTorrent,
    int MaxActiveDownloads,
    int MaxActiveUploads,
    int MaxActiveTorrents,
    int DiskCacheMb,
    bool EnableOsCache,
    bool PreAllocateDisk,
    int AsyncIoThreads,
    bool CoalesceReadsWrites,
    TorrentProtocolMode ProtocolMode,
    int SendBufferWatermarkKb,
    int SendBufferLowWatermarkKb,
    int SendBufferFactor,
    int SocketBacklogSize,
    int OutgoingConnectionsPerSecond,
    int? ListeningPort,
    bool RandomizePort,
    EncryptionMode EncryptionMode,
    bool AnonymousMode,
    bool EnableDht,
    bool EnablePex,
    bool EnableLsd,
    string NetworkInterface,
    bool SuperSeeding,
    CalculatorNotice[] Warnings,
    IReadOnlyDictionary<string, CalculatorNotice> Explanations);
