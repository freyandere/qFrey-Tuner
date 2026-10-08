using System.Security.Cryptography;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private sealed record NetworkConfirmation(string Token, Guid SessionId, long Revision, DateTimeOffset ExpiresUtc);
    private readonly List<NetworkConfirmation> networkConfirmations = [];
    private NetworkTestResult? networkTest;

    private void RevokeNetworkConfirmations() => networkConfirmations.Clear();

    private string? NetworkAdmissionError(CommandEnvelope command)
    {
        if (!TryRequireOwnedTarget(command, out _, out var error)) return error;
        if (activeOperation is not null)
            return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        return null;
    }

    private Task<string> RequestNetworkConfirmationAsync(CommandEnvelope command,
        RequestConfirmationPayload payload, string canonical, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (NetworkAdmissionError(command) is { } error) return Task.FromResult(error);
        if (payload.ActionId != "RunNetworkTest" || payload.PlanId is not null || payload.CycleId is not null
            || payload.WorkloadId is not null || payload.CatalogueId is not null || payload.ServerSavePath is not null
            || payload.SelectionToken is not null)
            return Task.FromResult(Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.invalidCommand"));
        var now = timeProvider.GetUtcNow();
        var consent = new NetworkConfirmation(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            target!.SessionId, revision, now.AddMinutes(2));
        networkConfirmations.RemoveAll(item => item.ExpiresUtc <= now);
        if (networkConfirmations.Count >= 32) networkConfirmations.RemoveAt(0);
        networkConfirmations.Add(consent);
        var budget = NetworkProbe.MaximumTraffic;
        var summary = new ConfirmationSummary(consent.Token, consent.SessionId, consent.Revision,
            consent.ExpiresUtc, payload.ActionId, new("network.confirmTraffic", new Dictionary<string, MessageParameter>
            {
                ["maxPayloadMiB"] = new NumberParameter(budget.MaximumPayloadBytes / (1024d * 1024)),
                ["maxRequests"] = new NumberParameter(budget.MaximumRequestCount)
            }));
        confirmations = [.. confirmations.Where(item => item.ExpiresUtc > now).TakeLast(31), summary];
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return Task.FromResult(reply);
    }

    private async Task<string> StartNetworkTestAsync(CommandEnvelope command, NetworkTestPayload payload,
        string canonical, CancellationToken token)
    {
        if (NetworkAdmissionError(command) is { } error) return error;
        var consent = networkConfirmations.Find(item => item.Token == payload.ConfirmationToken);
        if (consent is not null) networkConfirmations.Remove(consent);
        confirmations = confirmations.Where(item => item.Token != payload.ConfirmationToken).ToArray();
        if (consent is null || consent.SessionId != target!.SessionId || consent.Revision != revision
            || consent.ExpiresUtc <= timeProvider.GetUtcNow())
            return Failure(command.RequestId, ErrorCodes.ConfirmationExpired, "errors.confirmationExpired");
        var operationSession = session!;
        await operationSession.RevalidateVersionsAsync(token).ConfigureAwait(false);
        if (CurrentOwnedTarget(operationSession) != CurrentOwnedTarget(target!))
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        try
        {
            await PauseDashboardAsync(token).ConfigureAwait(false);
            if (!await operationSession.ReadNetworkIdleEvidenceAsync(token).ConfigureAwait(false))
            {
                StartDashboardCollector(operationSession, target!.SessionId);
                return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
            }
        }
        catch
        {
            StartDashboardCollector(operationSession, target!.SessionId);
            throw;
        }
        RevokeConfirmations();
        var operationId = Guid.NewGuid();
        var sessionId = target!.SessionId;
        var cancellation = new CancellationTokenSource();
        activeOperation = new(operationId, OperationKind.NetworkTest, "networkTest", null, true);
        networkTest = null;
        measurementCancellation = cancellation;
        revision++;
        measurementTask = Task.Run(() => RunNetworkTestAsync(operationId, sessionId, operationSession, cancellation));
        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private static async Task<NetworkProbeResult> RunDefaultNetworkProbeAsync(CancellationToken token)
    {
        using var probe = new NetworkProbe();
        return await probe.RunAsync(token).ConfigureAwait(false);
    }

    private async Task RunNetworkTestAsync(Guid operationId, Guid sessionId,
        QbittorrentSession operationSession, CancellationTokenSource cancellation)
    {
        NetworkTestResult result;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        string? trafficReason = null;
        try
        {
            var probeTask = (runNetworkProbe ?? RunDefaultNetworkProbeAsync)(deadline.Token);
            try
            {
                while (!probeTask.IsCompleted)
                {
                    var tick = Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
                    if (await Task.WhenAny(probeTask, tick).ConfigureAwait(false) == probeTask) break;
                    await tick.ConfigureAwait(false);
                    if (!await operationSession.ReadNetworkIdleEvidenceAsync(deadline.Token).ConfigureAwait(false))
                    { trafficReason = "NETWORK_TORRENT_TRAFFIC"; deadline.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) { deadline.Cancel(); }
            catch (Exception) { trafficReason = "NETWORK_TRAFFIC_UNKNOWN"; deadline.Cancel(); }
            var observed = await probeTask.ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                if (!await operationSession.ReadNetworkIdleEvidenceAsync(deadline.Token).ConfigureAwait(false))
                    trafficReason = "NETWORK_TORRENT_TRAFFIC";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { trafficReason = "NETWORK_TRAFFIC_UNKNOWN"; }
            result = MapNetworkProbe(observed);
        }
        catch (OperationCanceledException)
        {
            result = new(null, null, timeProvider.GetUtcNow(),
                [cancellation.IsCancellationRequested ? "NETWORK_TEST_CANCELLED" : "NETWORK_TIMEOUT"]);
        }
        catch (Exception) { result = new(null, null, timeProvider.GetUtcNow(), ["NETWORK_TEST_FAILED"]); }
        if (trafficReason is not null) result = new(null, null, timeProvider.GetUtcNow(), [trafficReason]);

        string? message = null;
        await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!disposed && activeOperation?.Id == operationId && target?.SessionId == sessionId
                    && ReferenceEquals(session, operationSession))
                {
                    networkTest = cancellation.IsCancellationRequested
                        ? new(null, null, timeProvider.GetUtcNow(), ["NETWORK_TEST_CANCELLED"]) : result;
                    activeOperation = null;
                    revision++;
                    message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                        NextEventSequence(), revision, Snapshot()), Protocol.Json);
                }
                if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
            }
            finally { gate.Release(); }
            if (message is not null)
            {
                try { await PublishSnapshotAsync(message).ConfigureAwait(false); }
                catch (Exception) { }
            }
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!disposed && !shuttingDown && target?.SessionId == sessionId && ReferenceEquals(session, operationSession))
                    StartDashboardCollector(operationSession, sessionId);
            }
            finally { gate.Release(); }
        }
        finally { commandAdmission.Release(); cancellation.Dispose(); }
    }

    internal static NetworkTestResult MapNetworkProbe(NetworkProbeResult result)
    {
        var reasons = new List<string>();
        double? Rate(NetworkProbeReading reading)
        {
            if (reading.Status == NetworkProbeStatus.Measured && reading.BytesPerSecond is { } rate
                && double.IsFinite(rate) && rate > 0) return rate;
            reasons.Add(SafeReason(reading.ReasonCode, "NETWORK_RATE_INVALID"));
            return null;
        }
        var download = Rate(result.Download);
        var upload = Rate(result.Upload);
        return new(download, upload, result.MeasuredUtc.ToUniversalTime(), reasons.Distinct(StringComparer.Ordinal).ToArray());
    }
}
