using System.Text.RegularExpressions;
using System.Text.Json;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Desktop.Platform;

namespace QFrey.Desktop.Bridge;

public sealed partial class BridgeDispatcher
{
    private ObservedHardware? observedHardware;

    // Internal seam lets tests exercise the operation lifecycle without touching host hardware.
    internal Func<string?, HardwareObservation> HardwareProbe { get; set; } = WindowsPlatformProbe.ReadHardware;

    private Task<string> StartHardwareDetectionAsync(CommandEnvelope command, DetectHardwarePayload payload,
        string canonical, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target is null || session?.IsValidated != true || command.TargetSessionId != target.SessionId)
            return Task.FromResult(Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale"));
        if (command.ExpectedRevision != revision)
            return Task.FromResult(Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale"));
        if (activeOperation is not null)
            return Task.FromResult(Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict"));

        string? volumePath = null;
        if (payload.VolumeToken is not null) volumePath = nativeSelections.ConsumeVolume(payload.VolumeToken);
        var operationId = Guid.NewGuid();
        var operationSessionId = target.SessionId;
        var localTarget = target.IsLocal;
        var operationCancellation = new CancellationTokenSource();
        activeOperation = new(operationId, OperationKind.HardwareDetection, "hardwareDetection", null, true);
        measurementCancellation = operationCancellation;
        revision++;
        measurementTask = Task.Run(() => RunHardwareDetectionAsync(operationId, operationSessionId,
            localTarget, payload.VolumeToken, volumePath, operationCancellation));

        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return Task.FromResult(accepted);
    }

    private async Task RunHardwareDetectionAsync(Guid operationId, Guid sessionId, bool localTarget,
        string? volumeToken, string? volumePath, CancellationTokenSource cancellation)
    {
        ObservedHardware result;
        try
        {
            if (cancellation.IsCancellationRequested)
                result = UnavailableHardware("hardwareDetectionCancelled", volumeToken);
            else if (!localTarget)
                result = UnavailableHardware("remoteTarget", volumeToken);
            else
                result = MapHardwareObservation(HardwareProbe(volumePath), volumeToken, timeProvider.GetUtcNow());

            // Native system probes cannot be interrupted safely. Keep this operation active until the probe returns,
            // then report cancellation instead of publishing a result the caller asked to discard.
            if (cancellation.IsCancellationRequested)
                result = UnavailableHardware("hardwareDetectionCancelled", volumeToken);
        }
        catch (Exception)
        {
            result = cancellation.IsCancellationRequested
                ? UnavailableHardware("hardwareDetectionCancelled", volumeToken)
                : ErrorHardware("hardwareProbeFailed", volumeToken);
        }

        string? message = null;
        await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (activeOperation?.Id == operationId && target?.SessionId == sessionId)
                {
                    observedHardware = result;
                    activeOperation = null;
                    if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
                    revision++;
                    message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                        NextEventSequence(), revision, Snapshot()), Protocol.Json);
                }
            }
            finally { gate.Release(); }

            if (message is not null)
            {
                try { await PublishSnapshotAsync(message).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
        finally
        {
            commandAdmission.Release();
            cancellation.Dispose();
        }
    }

    internal static ObservedHardware MapHardwareObservation(HardwareObservation observation, string? volumeToken,
        DateTimeOffset sampledAtUtc)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var timestamp = sampledAtUtc.ToUniversalTime();
        var ram = MapReading(observation.RamBytes, timestamp, static value => value,
            "ramUnavailable", requirePositive: true, maxValue: 9_007_199_254_740_991d);
        var logical = MapReading(observation.LogicalCpuCount, timestamp, static value => value,
            "cpuCountUnavailable", requirePositive: true, maxValue: 4096);
        var performance = MapReading(observation.PerformanceCpuCount, timestamp, static value => value,
            "performanceCoresUnavailable", requirePositive: true, maxValue: 4096);

        StorageType? storage = null;
        var storageReason = SafeReason(observation.StorageReasonCode, "storageUnavailable");
        if (observation.StorageStatus == "fresh" && observation.StorageType is { } storageName)
        {
            if (Enum.TryParse<StorageType>(storageName, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            { storage = parsed; storageReason = null; }
            else storageReason = "storageTypeInvalid";
        }
        else if (observation.StorageStatus is not ("unavailable" or "error" or "stale"))
            storageReason = "storageStatusInvalid";

        var facts = new HardwareFacts(ram, logical, performance, storage, storageReason, SafeVolumeToken(volumeToken));
        var reasons = new[] { Reason(ram), Reason(logical), Reason(performance), storageReason }
            .Where(reason => reason is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        return new(TryCreateInputs(ram, logical, performance, storage), facts, reasons);
    }

    private static ObservedHardware UnavailableHardware(string reason, string? volumeToken) =>
        new(null, new(new UnavailableReading(reason), new UnavailableReading(reason), new UnavailableReading(reason),
            null, reason, SafeVolumeToken(volumeToken)), [reason]);

    private static ObservedHardware ErrorHardware(string reason, string? volumeToken) =>
        new(null, new(new ErrorReading(reason), new ErrorReading(reason), new ErrorReading(reason),
            null, reason, SafeVolumeToken(volumeToken)), [reason]);

    private static MetricReading MapReading<T>(HardwareReading<T> reading, DateTimeOffset atUtc,
        Func<T, double> convert, string unavailableReason, bool requirePositive, double maxValue) where T : struct
    {
        var reason = SafeReason(reading.ReasonCode, unavailableReason);
        if (reading.Status == "unavailable") return new UnavailableReading(reason);
        if (reading.Status == "error") return new ErrorReading(reason);
        if (reading.Status is not ("fresh" or "stale")) return new ErrorReading("hardwareStatusInvalid");
        if (reading.Value is not T value) return new ErrorReading("hardwareReadingMissing");
        var number = convert(value);
        if (!double.IsFinite(number) || number > maxValue || requirePositive && number <= 0)
            return new ErrorReading("hardwareReadingInvalid");
        return reading.Status == "fresh"
            ? new FreshReading(number, atUtc)
            : new StaleReading(number, atUtc, SafeReason(reading.ReasonCode, "hardwareReadingStale"));
    }

    private static HardwareInputs? TryCreateInputs(MetricReading ram, MetricReading logical,
        MetricReading performance, StorageType? storage)
    {
        if (storage is null || ram is not FreshReading { Value: > 0 } ramReading
            || logical is not FreshReading { Value: > 0 } logicalReading
            || performance is not FreshReading { Value: > 0 } performanceReading)
            return null;
        var ramGiB = Math.Ceiling(ramReading.Value / (1024d * 1024 * 1024));
        if (!double.IsFinite(ramGiB) || ramGiB is < 1 or > 1_048_576
            || logicalReading.Value > 4096 || performanceReading.Value > logicalReading.Value)
            return null;
        var hardware = new HardwareInputs(storage.Value, (int)ramGiB, (int)logicalReading.Value,
            true, (int)performanceReading.Value, InputSource.LocalDetection);
        var inputs = new DraftInputs(new(1, 1, ConnectionType.Fiber, false, "", false,
            InputSource.Manual, InputSource.Manual), hardware,
            new(TrackerType.Public, UserRole.Leecher, EnvironmentProfile.System), null);
        try { CommandPayloads.ValidateInputs(inputs); return hardware; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Reason(MetricReading reading) => reading switch
    {
        UnavailableReading unavailable => unavailable.ReasonCode,
        ErrorReading error => error.ReasonCode,
        StaleReading stale => stale.ReasonCode,
        _ => null
    };

    private static string SafeReason(string? reason, string fallback) =>
        reason is { Length: > 0 and <= 64 } && Regex.IsMatch(reason, "^[a-zA-Z][a-zA-Z0-9_-]*$", RegexOptions.CultureInvariant)
            ? reason : fallback;

    private static string? SafeVolumeToken(string? token) =>
        token is { Length: <= 128 } && Regex.IsMatch(token, "^[A-Fa-f0-9]+$", RegexOptions.CultureInvariant)
            ? token : null;
}
