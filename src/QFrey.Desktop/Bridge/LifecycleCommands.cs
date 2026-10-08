using System.Security.Cryptography;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Platform;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Platform;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private sealed record LifecycleConsent(string Token, string Action, Guid SessionId, long Revision,
        DateTimeOffset ExpiresUtc, TargetSummary Target, ProcessIdentity Owner);
    private readonly List<LifecycleConsent> lifecycleConfirmations = [];

    private void RevokeLifecycleConfirmations() => lifecycleConfirmations.Clear();

    private ProcessOwnerObservation ObserveLifecycleOwner(string endpoint, bool restart) =>
        (lifecycleOwner ?? ((e, capture) => WindowsPlatformProbe.FindOwner(e, capture)))(endpoint, restart);

    private void RequireLifecycleTarget(CommandEnvelope command)
    {
        RequireMutationTarget(command);
        if (activeOperation is not null || shuttingDown) throw new QbittorrentException(ErrorCodes.OperationConflict);
        if (recoveryBlocked || pendingOwnedWorkloadId is not null
            || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            throw new QbittorrentException(ErrorCodes.RecoveryRequired);
        if (!target!.IsLocal) throw new QbittorrentException(ErrorCodes.OwnerUnavailable);
    }

    private async Task<string> RequestLifecycleConfirmationAsync(CommandEnvelope command,
        RequestConfirmationPayload payload, string canonical, CancellationToken token)
    {
        RequireLifecycleTarget(command);
        if (payload.ActionId is not ("StopTarget" or "RestartTarget"))
            throw new QbittorrentException(ErrorCodes.CommandNotImplemented);
        if (payload.PlanId is not null || payload.CycleId is not null || payload.WorkloadId is not null
            || payload.CatalogueId is not null || payload.ServerSavePath is not null || payload.SelectionToken is not null)
            throw new QbittorrentException(ErrorCodes.InvalidCommand);
        await session!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        VerifyLifecycleVersions(session, target!);
        var owner = ObserveLifecycleOwner(target!.Endpoint, payload.ActionId == "RestartTarget");
        if (owner.Status != OwnerLookupStatus.Available || owner.Owner is not { } identity
            || identity.ProcessId <= 0 || identity.StartTimeUtcTicks <= 0
            || payload.ActionId == "RestartTarget" && owner.RestartBlocked)
            throw new QbittorrentException(ErrorCodes.OwnerUnavailable);
        var now = timeProvider.GetUtcNow();
        var consent = new LifecycleConsent(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            payload.ActionId, target.SessionId, revision, now.AddMinutes(2), target, identity);
        lifecycleConfirmations.RemoveAll(item => item.ExpiresUtc <= now);
        if (lifecycleConfirmations.Count >= 32) lifecycleConfirmations.RemoveAt(0);
        lifecycleConfirmations.Add(consent);
        confirmations = [.. confirmations.Where(item => item.ExpiresUtc > now).TakeLast(31),
            new ConfirmationSummary(consent.Token, consent.SessionId, revision, consent.ExpiresUtc, consent.Action,
                new LocalizedMessage(consent.Action == "StopTarget" ? "confirmation.lifecycle.stop" : "confirmation.lifecycle.restart",
                    new Dictionary<string, MessageParameter> { ["endpoint"] = new TextParameter(target.Endpoint),
                        ["processId"] = new NumberParameter(identity.ProcessId) }))];
        var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, Snapshot(), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private static void VerifyLifecycleVersions(QbittorrentSession current, TargetSummary expected)
    {
        if (current.Endpoint.AbsoluteUri != expected.Endpoint || current.Versions.Qbittorrent != expected.QbittorrentVersion
            || current.Versions.WebApi != expected.ApiVersion || current.Versions.Libtorrent != expected.LibtorrentVersion)
            throw new QbittorrentException(ErrorCodes.SessionStale);
    }

    private async Task<string> StartLifecycleAsync(CommandEnvelope command, LifecyclePayload payload,
        string canonical, CancellationToken token)
    {
        RequireLifecycleTarget(command);
        if (command.Command is not ("StopTarget" or "RestartTarget"))
            throw new QbittorrentException(ErrorCodes.CommandNotImplemented);
        var consent = lifecycleConfirmations.FirstOrDefault(item => item.Token == payload.ConfirmationToken);
        if (consent is not null) lifecycleConfirmations.Remove(consent);
        confirmations = confirmations.Where(item => item.Token != payload.ConfirmationToken).ToArray();
        if (consent is null || consent.Action != command.Command || consent.SessionId != target!.SessionId
            || consent.Revision != revision || consent.Target != target || consent.ExpiresUtc <= timeProvider.GetUtcNow())
            throw new QbittorrentException(ErrorCodes.ConfirmationExpired);
        await session!.RevalidateVersionsAsync(token).ConfigureAwait(false);
        VerifyLifecycleVersions(session, consent.Target);
        VerifyLifecycleOwner(consent);
        var operationId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        var operationSession = session;
        RevokeConfirmations();
        activeOperation = new(operationId, command.Command == "StopTarget" ? OperationKind.StopTarget : OperationKind.RestartTarget,
            "starting", null, false);
        measurementCancellation = cancellation;
        revision++;
        measurementTask = Task.Run(() => RunLifecycleAsync(operationId, operationSession, consent, cancellation));
        var reply = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true, revision,
            new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, reply);
        return reply;
    }

    private void VerifyLifecycleOwner(LifecycleConsent consent)
    {
        var owner = ObserveLifecycleOwner(consent.Target.Endpoint, consent.Action == "RestartTarget");
        if (owner.Status != OwnerLookupStatus.Available || owner.Owner is not { } identity
            || !TargetOwnership.SameProcess(consent.Owner, identity)
            || consent.Action == "RestartTarget" && owner.RestartBlocked)
            throw new QbittorrentException(ErrorCodes.OwnerUnavailable);
    }

    private async Task RunLifecycleAsync(Guid operationId, QbittorrentSession operationSession,
        LifecycleConsent consent, CancellationTokenSource cancellation)
    {
        string? failure = null;
        try
        {
            await PauseDashboardAsync(cancellation.Token).ConfigureAwait(false);
            async Task Shutdown(CancellationToken token)
            {
                await operationSession.ShutdownAsync(token, () =>
                {
                    VerifyLifecycleVersions(operationSession, consent.Target);
                    VerifyLifecycleOwner(consent);
                }).ConfigureAwait(false);
            }
            var runner = runLifecycle ?? ((endpoint, restart, shutdown, token) => restart
                ? new TargetLifecycle().RestartAsync(endpoint, shutdown, token: token)
                : new TargetLifecycle().StopAsync(endpoint, shutdown, token: token));
            var result = await runner(consent.Target.Endpoint, consent.Action == "RestartTarget", Shutdown, cancellation.Token).ConfigureAwait(false);
            failure = result.Status switch
            {
                TargetLifecycleStatus.Stopped or TargetLifecycleStatus.Restarted => null,
                TargetLifecycleStatus.Blocked => ErrorCodes.OwnerUnavailable,
                TargetLifecycleStatus.Cancelled => ErrorCodes.Cancelled,
                TargetLifecycleStatus.TimedOut => ErrorCodes.ApiTimeout,
                _ => ErrorCodes.ApiUnavailable
            };
        }
        catch (QbittorrentException error) { failure = error.Code; }
        catch (OperationCanceledException) { failure = ErrorCodes.Cancelled; }
        catch (Exception) { failure = ErrorCodes.ApiUnavailable; }
        finally
        {
            string? message = null;
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (activeOperation?.Id == operationId && target?.SessionId == consent.SessionId && ReferenceEquals(session, operationSession))
                {
                    // A lost shutdown reply cannot prove the old connection is still usable. Require a fresh connect.
                    RevokeConfirmations();
                    operationSession.Dispose(); session = null; target = null; metrics = []; review = null; draft = null;
                    experiment = null; currentCycle = null; selections = []; observedHardware = null;
                    ownedWorkloadCandidates = []; networkTest = null; pendingOwnedWorkloadId = null;
                    activeOperation = null; measurementCancellation = null;
                    availableActions = failure is null ? [] : [new("operation.error", false, failure, ErrorMessageKey(failure))];
                    revision++;
                    message = JsonSerializer.Serialize(new SnapshotEvent(consent.SessionId, operationId, NextEventSequence(), revision, Snapshot()), Protocol.Json);
                }
            }
            finally { gate.Release(); cancellation.Dispose(); }
            if (message is not null) { try { await PublishSnapshotAsync(message).ConfigureAwait(false); } catch (Exception) { } }
        }
    }
}
