using System.Text.Json;
using System.Text.Json.Serialization;

namespace QFrey.Core.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BypassAuthentication), "bypass")]
[JsonDerivedType(typeof(PasswordAuthentication), "password")]
[JsonDerivedType(typeof(ApiKeyAuthentication), "apiKey")]
public abstract record Authentication;
public sealed record BypassAuthentication : Authentication;
public sealed record PasswordAuthentication(string Username, string Password) : Authentication
{ public override string ToString() => "PasswordAuthentication [redacted]"; }
public sealed record ApiKeyAuthentication(string ApiKey) : Authentication
{ public override string ToString() => "ApiKeyAuthentication [redacted]"; }
public sealed record ConnectPayload(string Endpoint, Authentication Auth)
{ public override string ToString() => "ConnectPayload [redacted]"; }
public sealed record EmptyPayload;
public sealed record DetectHardwarePayload(string? VolumeToken);
public sealed record NetworkTestPayload(string ConfirmationToken);
public sealed record BuildPlanPayload(DraftInputs Inputs, PlanSelection[] Selections);
public sealed record AcceptPlanPayload(Guid PlanId, long Revision);
public sealed record StartMeasurementPayload(MeasurementKind Kind, WorkloadReference Workload);
public sealed record CancelOperationPayload(Guid OperationId);
public sealed record PrepareWorkloadPayload(string CatalogueId, string ServerSavePath, string ApprovalToken);
public sealed record StopWorkloadPayload(Guid WorkloadId, string ConfirmationToken);
public sealed record DeleteWorkloadPayload(Guid WorkloadId, bool DeleteFiles, string ConfirmationToken);
public sealed record ApplyPlanPayload(Guid PlanId, long ExpectedRevision, string ConfirmationToken);
public sealed record RollbackPayload(Guid CycleId, long ExpectedRevision, string ConfirmationToken);
public sealed record RestoreBackupPayload(string SelectionToken, string ConfirmationToken);
public sealed record KeepChangesPayload(Guid CycleId, ApplyStatus ApplyStatus);
public sealed record ListHistoryPayload(string? Cursor, int PageSize);
public sealed record ReadCyclePayload(Guid CycleId);
public sealed record ExportReportPayload(Guid CycleId, ReportFormat Format, string DestinationToken, Locale Locale);
public sealed record LifecyclePayload(string ConfirmationToken);
// Token requests do not mutate. Native selections return opaque tokens, never arbitrary local paths.
public sealed record RequestConfirmationPayload(string ActionId, Guid? PlanId, Guid? CycleId, Guid? WorkloadId,
    string? CatalogueId = null, string? ServerSavePath = null, string? SelectionToken = null);
public sealed record SelectFilePayload(string Purpose);

public static class CommandPayloads
{
    public static object Read(CommandEnvelope envelope)
    {
        object payload = envelope.Command switch
        {
            "Initialize" => Decode<InitializePayload>(envelope),
            "SetUiPreferences" => Decode<SetUiPreferencesPayload>(envelope),
            "Connect" => Decode<ConnectPayload>(envelope),
            "Disconnect" => Decode<EmptyPayload>(envelope),
            "DetectLocalHardware" => Decode<DetectHardwarePayload>(envelope),
            "RunNetworkTest" => Decode<NetworkTestPayload>(envelope),
            "BuildPlan" => Decode<BuildPlanPayload>(envelope),
            "AcceptPlan" => Decode<AcceptPlanPayload>(envelope),
            "StartMeasurement" => Decode<StartMeasurementPayload>(envelope),
            "CancelOperation" => Decode<CancelOperationPayload>(envelope),
            "PrepareWorkload" => Decode<PrepareWorkloadPayload>(envelope),
            "StopOwnedWorkload" => Decode<StopWorkloadPayload>(envelope),
            "DeleteOwnedWorkload" => Decode<DeleteWorkloadPayload>(envelope),
            "ApplyPlan" => Decode<ApplyPlanPayload>(envelope),
            "Rollback" => Decode<RollbackPayload>(envelope),
            "RestoreLegacyBackup" => Decode<RestoreBackupPayload>(envelope),
            "KeepChanges" => Decode<KeepChangesPayload>(envelope),
            "ListHistory" => Decode<ListHistoryPayload>(envelope),
            "ReadCycle" => Decode<ReadCyclePayload>(envelope),
            "ExportReport" => Decode<ExportReportPayload>(envelope),
            "StartTarget" or "StopTarget" or "RestartTarget" => Decode<LifecyclePayload>(envelope),
            "RequestConfirmation" => Decode<RequestConfirmationPayload>(envelope),
            "SelectNativeFile" => Decode<SelectFilePayload>(envelope),
            _ => throw new JsonException("UNKNOWN_COMMAND")
        };
        if (envelope.Command is not ("Initialize" or "SetUiPreferences" or "Connect" or "ListHistory" or "ReadCycle" or "ExportReport" or "SelectNativeFile"))
        {
            if (envelope.TargetSessionId is null || envelope.TargetSessionId == Guid.Empty || envelope.ExpectedRevision is null)
                throw new JsonException("SESSION_REQUIRED");
        }
        Validate(payload);
        if (payload is RequestConfirmationPayload confirmation
            && (confirmation.PlanId == Guid.Empty || confirmation.CycleId == Guid.Empty || confirmation.WorkloadId == Guid.Empty))
            throw new JsonException("INVALID_PAYLOAD");
        if (payload is RequestConfirmationPayload preparation &&
            (preparation.ActionId == "PrepareWorkload"
                ? string.IsNullOrWhiteSpace(preparation.CatalogueId) || preparation.CatalogueId.Length > 64
                  || string.IsNullOrWhiteSpace(preparation.ServerSavePath) || preparation.ServerSavePath.Length > 2048
                  || preparation.ServerSavePath.Any(char.IsControl) || preparation.PlanId is not null
                  || preparation.CycleId is not null || preparation.WorkloadId is not null
                : preparation.CatalogueId is not null || preparation.ServerSavePath is not null))
            throw new JsonException("INVALID_PAYLOAD");
        if (payload is RequestConfirmationPayload restore &&
            (restore.ActionId == "RestoreLegacyBackup"
                ? string.IsNullOrWhiteSpace(restore.SelectionToken) || restore.SelectionToken.Length > 128
                  || restore.PlanId is not null || restore.CycleId is not null || restore.WorkloadId is not null
                : restore.SelectionToken is not null)) throw new JsonException("INVALID_PAYLOAD");
        return payload;
    }

    private static T Decode<T>(CommandEnvelope envelope) where T : class =>
        envelope.Payload.Deserialize<T>(Protocol.Json) ?? throw new JsonException("INVALID_PAYLOAD");

    private static void Validate(object payload)
    {
        switch (payload)
        {
            case InitializePayload p when p.ProtocolVersion != Protocol.Version:
                throw new JsonException("PROTOCOL_UNSUPPORTED");
            case SetUiPreferencesPayload p when p.Locale is not ("ru-RU" or "en-US"):
                throw new JsonException("INVALID_PREFERENCES");
            case ConnectPayload p:
                if (!Uri.TryCreate(p.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                    || p.Auth is null) throw new JsonException("INVALID_ENDPOINT");
                if (p.Auth is PasswordAuthentication a && (string.IsNullOrWhiteSpace(a.Username) || string.IsNullOrEmpty(a.Password))
                    || p.Auth is ApiKeyAuthentication k && string.IsNullOrWhiteSpace(k.ApiKey)) throw new JsonException("AUTH_REQUIRED");
                break;
            case BuildPlanPayload p:
                ValidateInputs(p.Inputs);
                if (p.Selections is null || p.Selections.Length > 128 || p.Selections.Any(s => s is null) || p.Selections.Select(s => s.GroupId).Distinct().Count() != p.Selections.Length
                    || p.Selections.Any(s => string.IsNullOrWhiteSpace(s.GroupId) || s.Overrides is null || s.Overrides.Count > 64 || s.Overrides.Values.Any(v => v is null)))
                    throw new JsonException("INVALID_SELECTION");
                break;
            case AcceptPlanPayload p when p.PlanId == Guid.Empty || !SafeRevision(p.Revision):
                throw new JsonException("INVALID_PAYLOAD");
            case ApplyPlanPayload p when p.PlanId == Guid.Empty || !SafeRevision(p.ExpectedRevision) || string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case RollbackPayload p when p.CycleId == Guid.Empty || !SafeRevision(p.ExpectedRevision) || string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case CancelOperationPayload p when p.OperationId == Guid.Empty:
                throw new JsonException("INVALID_PAYLOAD");
            case KeepChangesPayload p when p.CycleId == Guid.Empty || p.ApplyStatus != ApplyStatus.Verified:
                throw new JsonException("INVALID_PAYLOAD");
            case ReadCyclePayload p when p.CycleId == Guid.Empty:
                throw new JsonException("INVALID_PAYLOAD");
            case ListHistoryPayload p when p.PageSize is < 1 or > 100 || p.Cursor?.Length > 256:
                throw new JsonException("INVALID_PAYLOAD");
            case ExportReportPayload p when p.CycleId == Guid.Empty || string.IsNullOrWhiteSpace(p.DestinationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case NetworkTestPayload p when string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case PrepareWorkloadPayload p when string.IsNullOrWhiteSpace(p.CatalogueId) || string.IsNullOrWhiteSpace(p.ServerSavePath) || string.IsNullOrWhiteSpace(p.ApprovalToken):
                throw new JsonException("INVALID_PAYLOAD");
            case StopWorkloadPayload p when p.WorkloadId == Guid.Empty || string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case DeleteWorkloadPayload p when p.WorkloadId == Guid.Empty || string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case RestoreBackupPayload p when string.IsNullOrWhiteSpace(p.SelectionToken) || string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case LifecyclePayload p when string.IsNullOrWhiteSpace(p.ConfirmationToken):
                throw new JsonException("INVALID_PAYLOAD");
            case StartMeasurementPayload p:
                if (p.Workload is null || p.Workload.Id == Guid.Empty || p.Workload.Hashes is null || p.Workload.Hashes.Length is < 1 or > 5000
                    || p.Workload.Hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Workload.Hashes.Length
                    || p.Workload.Hashes.Any(h => h is null || h.Length != 40 || !h.All(Uri.IsHexDigit))) throw new JsonException("INVALID_WORKLOAD");
                break;
            case RequestConfirmationPayload p when p.ActionId is not ("ApplyPlan" or "Rollback" or "RestoreLegacyBackup" or "RunNetworkTest" or "PrepareWorkload" or "StopOwnedWorkload" or "DeleteOwnedWorkload" or "StartTarget" or "StopTarget" or "RestartTarget"):
                throw new JsonException("INVALID_PAYLOAD");
            case SelectFilePayload p when p.Purpose is not ("restore" or "exportJson" or "exportHtml" or "volume"):
                throw new JsonException("INVALID_PAYLOAD");
        }
    }

    public static bool SafeRevision(long revision) => revision is >= 0 and <= 9007199254740991;
    public static void ValidateInputs(DraftInputs inputs)
    {
        if (inputs?.Network is not { } network || inputs.Hardware is not { } hardware || inputs.Usage is null
            || !double.IsFinite(network.DownloadMbps) || !double.IsFinite(network.UploadMbps)
            || network.DownloadMbps is <= 0 or > 100000 || network.UploadMbps is <= 0 or > 100000
            || network.VpnInterface is null || network.VpnInterface.Length > 256
            || hardware.RamGiB is < 1 or > 1048576 || hardware.CpuCores is < 1 or > 4096
            || hardware.PerformanceCores < 0 || hardware.PerformanceCores > hardware.CpuCores
            || inputs.ProposedPort is not (null or >= 49152 and <= 65535)) throw new JsonException("INVALID_INPUTS");
        if (!Enum.IsDefined(network.ConnectionType) || !Enum.IsDefined(network.DownloadSource) || !Enum.IsDefined(network.UploadSource)
            || !Enum.IsDefined(hardware.StorageType) || !Enum.IsDefined(hardware.Source)
            || !Enum.IsDefined(inputs.Usage.TrackerType) || !Enum.IsDefined(inputs.Usage.UserRole) || !Enum.IsDefined(inputs.Usage.Environment))
            throw new JsonException("INVALID_INPUTS");
    }
}
