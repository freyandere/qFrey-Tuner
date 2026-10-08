using QFrey.Core.Contracts;
using QFrey.Core.Metrics;

namespace QFrey.Core.Qbittorrent;

public sealed partial class QbittorrentSession
{
    // Recheck the exact desktop owner after version reads, immediately before committing.
    // A lost reply must not retry shutdown or retain write authority on the old session.
    public async Task ShutdownAsync(CancellationToken token, Action? beforeCommit = null)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        await RevalidateVersionsAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { await SendAsync(HttpMethod.Post, "app/shutdown", null, token, beforeSend: beforeCommit).ConfigureAwait(false); }
        finally { IsValidated = false; }
    }

    public async Task<bool> ReadNetworkIdleEvidenceAsync(CancellationToken token)
    {
        if (!IsValidated) throw new QbittorrentException(ErrorCodes.TargetNotValidated);
        var response = await SendAsync(HttpMethod.Get, "torrents/info", null, token).ConfigureAwait(false);
        using var document = Parse(response.Bytes);
        if (!TorrentTelemetry.IsIdleInventory(document.RootElement)) return false;
        var metrics = await ReadTransferMetricsAsync(token).ConfigureAwait(false);
        return metrics.Single(metric => metric.Id == "transfer.download").Reading is FreshReading { Value: 0 }
            && metrics.Single(metric => metric.Id == "transfer.upload").Reading is FreshReading { Value: 0 };
    }
}
