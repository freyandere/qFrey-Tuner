namespace QFrey.Core.Contracts;

// Stable codes are persisted; translated text belongs to the presentation/export layer.
public static class ErrorCodes
{
    public const string InvalidCommand = "INVALID_COMMAND";
    public const string UnexpectedFailure = "UNEXPECTED_FAILURE";
    public const string InvalidEndpoint = "INVALID_ENDPOINT";
    public const string ProtocolUnsupported = "PROTOCOL_UNSUPPORTED";
    public const string CommandNotImplemented = "COMMAND_NOT_IMPLEMENTED";
    public const string OperationConflict = "OPERATION_CONFLICT";
    public const string DuplicateRequestConflict = "DUPLICATE_REQUEST_CONFLICT";
    public const string SessionStale = "SESSION_STALE";
    public const string PlanStale = "PLAN_STALE";
    public const string PlanNotApproved = "PLAN_NOT_APPROVED";
    public const string BaselineRequired = "BASELINE_REQUIRED";
    public const string TargetNotValidated = "TARGET_NOT_VALIDATED";
    public const string VersionIncompatible = "VERSION_INCOMPATIBLE";
    public const string SchemaIncompatible = "SCHEMA_INCOMPATIBLE";
    public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
    public const string ApiUnavailable = "API_UNAVAILABLE";
    public const string ApiTimeout = "API_TIMEOUT";
    public const string TlsRejected = "TLS_REJECTED";
    public const string RedirectRejected = "REDIRECT_REJECTED";
    public const string ConfirmationExpired = "CONFIRMATION_EXPIRED";
    public const string InvalidOverride = "INVALID_OVERRIDE";
    public const string PreferenceDrift = "PREFERENCE_DRIFT";
    public const string PersistenceFailed = "PERSISTENCE_FAILED";
    public const string ApplyUnverified = "APPLY_UNVERIFIED";
    public const string RollbackConflict = "ROLLBACK_CONFLICT";
    public const string RecoveryRequired = "RECOVERY_REQUIRED";
    public const string BackupInvalid = "BACKUP_INVALID";
    public const string WorkloadNotOwned = "WORKLOAD_NOT_OWNED";
    public const string MetadataInvalid = "METADATA_INVALID";
    public const string OwnerUnavailable = "OWNER_UNAVAILABLE";
    public const string MeasurementInvalid = "MEASUREMENT_INVALID";
    public const string Cancelled = "CANCELLED";
}
