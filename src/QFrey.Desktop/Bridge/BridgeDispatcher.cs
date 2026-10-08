using System.Globalization;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using QFrey.Core.Contracts;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Metrics;
using QFrey.Core.Tuning;
using QFrey.Core.Persistence;
using QFrey.Desktop.Platform;

namespace QFrey.Desktop.Bridge;
public sealed partial class BridgeDispatcher(string dataRoot, Func<ConnectPayload, CancellationToken, Task<QbittorrentSession>>? connect = null,
    Func<string, Func<LiveMetric[]>>? localMetrics = null, TimeProvider? clock = null,
    Func<string, Locale, CancellationToken, Task<string?>>? selectFile = null,
    Func<string, bool, Func<CancellationToken, Task>, CancellationToken, Task<TargetLifecycleResult>>? runLifecycle = null,
    Func<string, bool, ProcessOwnerObservation>? lifecycleOwner = null,
    Func<CancellationToken, Task<NetworkProbeResult>>? runNetworkProbe = null) : IDisposable
{
    private readonly TimeProvider timeProvider = clock ?? TimeProvider.System;
    private readonly NativeFileSelections nativeSelections = new(clock);
    private readonly CycleStore cycles = new(Path.Combine(dataRoot, "cycles"));
    private QbittorrentSession? session;
    private TargetSummary? target;
    private PlanReview? review;
    private DraftInputs? draft;
    private PlanSelection[] selections = [];
    private ExperimentSummary? experiment;
    private CycleRecord? currentCycle;
    private ActiveOperation? activeOperation;
    private CancellationTokenSource? measurementCancellation;
    private Task measurementTask = Task.CompletedTask;
    private volatile bool disposed;
    private bool shuttingDown;
    private CancellationTokenSource? collectorCancellation;
    private Task collectorTask = Task.CompletedTask;
    private LiveMetric[] metrics = [];
    private AvailableAction[] availableActions = [];
    private long eventSequence;
    public event Func<string, Task>? SnapshotPublished;
    public Locale CurrentLocale => preferences.Locale == "ru-RU" ? Locale.RuRu : Locale.EnUs;
    public async Task<bool> ShutdownAsync()
    {
        Task active, collector;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            shuttingDown = true;
            measurementCancellation?.Cancel();
            collectorCancellation?.Cancel();
            active = measurementTask; collector = collectorTask;
        }
        finally { gate.Release(); }
        try { await Task.WhenAll(active, collector).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
        catch (Exception) { return active.IsCompleted && collector.IsCompleted; }
    }
    public void Dispose()
    {
        if (activeOperation is { Kind: not OperationKind.Measurement } && !measurementTask.IsCompleted)
            throw new InvalidOperationException(ErrorCodes.OperationConflict);
        mutation?.Dispose();
        legacyRestoreCoordinator?.Dispose();
        disposed = true;
        measurementCancellation?.Cancel();
        collectorCancellation?.Cancel();
        session?.Dispose();
        _ = measurementTask.ContinueWith(task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
    public static bool IsAllowedSource(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == "https" && parsed.Host == "qfrey.local" && parsed.IsDefaultPort
        && string.IsNullOrEmpty(parsed.UserInfo) && parsed.AbsolutePath == "/index.html";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim commandAdmission = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (string Command, string Reply)> completed = new();
    private readonly Queue<Guid> order = [];
    private UiPreferences preferences = new(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru-RU" : "en-US", ThemePreference.System);
    private long revision;
    private bool loaded;
    public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> DispatchAsync(string json, CancellationToken token)
    {
        CommandEnvelope command;
        try { command = Protocol.Parse(json); }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { return Failure(Guid.Empty, "INVALID_COMMAND", "errors.invalidCommand"); }
        if (command.Command is not ("Initialize" or "SetUiPreferences" or "Connect" or "Disconnect" or "BuildPlan" or "AcceptPlan" or "ListHistory" or "ReadCycle" or "StartMeasurement" or "CancelOperation" or "SelectNativeFile" or "ExportReport" or "RequestConfirmation" or "ApplyPlan" or "Rollback" or "RestoreLegacyBackup" or "KeepChanges" or "DetectLocalHardware" or "PrepareWorkload" or "StartOwnedWorkload" or "StopOwnedWorkload" or "DeleteOwnedWorkload" or "SelectOwnedWorkload" or "StopTarget" or "RestartTarget" or "RunNetworkTest"))
            return Failure(command.RequestId, "COMMAND_NOT_IMPLEMENTED", "errors.commandNotImplemented");
        var payloadValue = CommandPayloads.Read(command);
        var normalizedPayload = JsonSerializer.SerializeToElement(payloadValue, payloadValue.GetType(), Protocol.Json);
        // Retain only a digest: a reconnect request contains credentials.
        var canonical = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command with { Payload = normalizedPayload }, Protocol.Json))));
        if (!await commandAdmission.WaitAsync(0, token))
        {
            // Replays cannot wait for an operation that may itself await the caller's response.
            if (completed.TryGetValue(command.RequestId, out var cached))
                return cached.Command == canonical ? cached.Reply : Failure(command.RequestId, "DUPLICATE_REQUEST_CONFLICT", "errors.duplicateRequest");
            return Failure(command.RequestId, "OPERATION_CONFLICT", "errors.operationConflict");
        }
        var stateAcquired = false;
        try
        {
            // A brief telemetry publish is not a conflicting user operation.
            await gate.WaitAsync(token); stateAcquired = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed.TryGetValue(command.RequestId, out var previous))
                return previous.Command == canonical ? previous.Reply : Failure(command.RequestId, "DUPLICATE_REQUEST_CONFLICT", "errors.duplicateRequest");
            if (shuttingDown && command.Command is not ("Initialize" or "SetUiPreferences" or "ListHistory" or "ReadCycle"))
                return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
            if (activeOperation is not null && command.Command is ("Connect" or "Disconnect" or "BuildPlan" or "AcceptPlan" or "StartMeasurement"))
                return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
            if (!loaded)
            {
                var path = Path.Combine(dataRoot, "ui.json");
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 4096) throw new JsonException();
                    var saved = JsonSerializer.Deserialize<UiPreferences>(await File.ReadAllTextAsync(path, token), Protocol.Json);
                    if (saved is null || saved.Locale is not ("ru-RU" or "en-US")) throw new JsonException();
                    preferences = saved;
                }
                loaded = true;
            }
            if (command.Command == "StartMeasurement")
                return ((StartMeasurementPayload)payloadValue).Kind == MeasurementKind.After
                    ? await StartAfterMeasurementAsync(command, (StartMeasurementPayload)payloadValue, canonical, token).ConfigureAwait(false)
                    : await StartMeasurementAsync(command, (StartMeasurementPayload)payloadValue, canonical, token).ConfigureAwait(false);
            if (payloadValue is RequestConfirmationPayload confirmation)
                return confirmation.ActionId == "RestoreLegacyBackup"
                    ? await RequestLegacyRestoreConfirmationAsync(command, confirmation, canonical, token).ConfigureAwait(false)
                    : confirmation.ActionId is "StopTarget" or "RestartTarget"
                    ? await RequestLifecycleConfirmationAsync(command, confirmation, canonical, token).ConfigureAwait(false)
                    : confirmation.ActionId == "RunNetworkTest"
                    ? await RequestNetworkConfirmationAsync(command, confirmation, canonical, token).ConfigureAwait(false)
                    : confirmation.ActionId is "PrepareWorkload" or "StartOwnedWorkload" or "StopOwnedWorkload" or "DeleteOwnedWorkload"
                    ? await RequestOwnedWorkloadConfirmationAsync(command, confirmation, canonical, token).ConfigureAwait(false)
                    : await RequestMutationConfirmationAsync(command, confirmation, canonical, token).ConfigureAwait(false);
            if (payloadValue is LifecyclePayload lifecycle)
                return await StartLifecycleAsync(command, lifecycle, canonical, token).ConfigureAwait(false);
            if (payloadValue is NetworkTestPayload network)
                return await StartNetworkTestAsync(command, network, canonical, token).ConfigureAwait(false);
            if (payloadValue is SelectOwnedWorkloadPayload ownedSelection)
                return await SelectOwnedWorkloadAsync(command, ownedSelection, canonical, token).ConfigureAwait(false);
            if (payloadValue is RestoreBackupPayload restore)
                return await StartLegacyRestoreAsync(command, restore, canonical, token).ConfigureAwait(false);
            if (payloadValue is PrepareWorkloadPayload preparation)
                return await StartOwnedPrepareAsync(command, preparation, canonical, token).ConfigureAwait(false);
            if (payloadValue is StopWorkloadPayload stopWorkload)
                return await StartOwnedWorkloadActionAsync(command, stopWorkload, canonical, token).ConfigureAwait(false);
            if (payloadValue is DeleteWorkloadPayload deleteWorkload)
                return await StartOwnedWorkloadActionAsync(command, deleteWorkload, canonical, token).ConfigureAwait(false);
            if (command.Command is "ApplyPlan" or "Rollback")
                return await StartMutationAsync(command, payloadValue, canonical, token).ConfigureAwait(false);
            if (payloadValue is KeepChangesPayload keep)
                return await KeepMutationAsync(command, keep, canonical, token).ConfigureAwait(false);
            if (payloadValue is DetectHardwarePayload hardware)
                return await StartHardwareDetectionAsync(command, hardware, canonical, token).ConfigureAwait(false);
            if (payloadValue is SelectFilePayload selection)
            {
                if (selectFile is null) return Failure(command.RequestId, ErrorCodes.InvalidCommand, "errors.commandNotImplemented");
                var locale = preferences.Locale == "ru-RU" ? Locale.RuRu : Locale.EnUs;
                gate.Release(); stateAcquired = false;
                var chosen = await selectFile(selection.Purpose, locale, token).ConfigureAwait(false);
                await gate.WaitAsync(token); stateAcquired = true;
                ObjectDisposedException.ThrowIf(disposed, this);
                var selected = chosen is null ? null : nativeSelections.Register(selection.Purpose, chosen);
                var selectionReply = JsonSerializer.Serialize(new CommandReply<NativeSelection?>(command.RequestId, true, revision, selected, null), Protocol.Json);
                Cache(command.RequestId, canonical, selectionReply);
                return selectionReply;
            }
            if (payloadValue is ExportReportPayload export)
            {
                var cycle = await new CycleStore(Path.Combine(dataRoot, "cycles")).ReadAsync(export.CycleId, cancellationToken: token)
                    ?? throw new QbittorrentException(ErrorCodes.BackupInvalid);
                gate.Release(); stateAcquired = false;
                using var exportDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                exportDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                await nativeSelections.ExportAsync(export.DestinationToken, export.Format, cycle, export.Locale, exportDeadline.Token).ConfigureAwait(false);
                await gate.WaitAsync(token); stateAcquired = true;
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            if (command.Command == "CancelOperation")
            {
                if (target is null || session?.IsValidated != true || command.TargetSessionId != target.SessionId)
                    return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
                if (command.ExpectedRevision != revision) return Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale");
                var cancellation = (CancelOperationPayload)payloadValue;
                var operation = activeOperation;
                if (operation is null || operation.Id != cancellation.OperationId || !operation.Cancellable || measurementCancellation is null)
                    return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
                activeOperation = operation with { Stage = "cancelling", Cancellable = false };
                revision++;
                measurementCancellation.Cancel();
            }
            if (command.Command is "Connect" or "Disconnect")
            {
                if (command.ExpectedRevision != revision) return Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale");
                if (command.Command == "Disconnect" && command.TargetSessionId != target?.SessionId)
                    return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
                // Invalidate the previous target even if new authentication fails.
                RevokeConfirmations();
                collectorCancellation?.Cancel();
                await collectorTask;
                collectorCancellation?.Dispose(); collectorCancellation = null;
                session?.Dispose(); session = null; target = null; metrics = []; review = null; draft = null;
                experiment = null; currentCycle = null; activeOperation = null; availableActions = []; selections = []; observedHardware = null;
                pendingOwnedWorkloadId = null;
                ownedWorkloadCandidates = []; networkTest = null;
                completed.Clear(); order.Clear(); revision++;
                if (command.Command == "Connect")
                {
                    var candidate = await (connect ?? ((p, t) => QbittorrentSession.ConnectAsync(p, t)))((ConnectPayload)payloadValue, token);
                    if (disposed || token.IsCancellationRequested) { candidate.Dispose(); token.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(BridgeDispatcher)); }
                    session = candidate;
                    var versions = candidate.Versions;
                    target = new(Guid.NewGuid(), candidate.Endpoint.AbsoluteUri, versions.Qbittorrent, versions.WebApi,
                        versions.Libtorrent, candidate.Endpoint.IsLoopback);
                    review = new PlanReview(target.SessionId);
                    eventSequence = 0;
                    await LoadRecoveryAsync(token).ConfigureAwait(false);
                    recoveryBlocked |= await LoadLegacyRestoreRecoveryAsync(token).ConfigureAwait(false);
                    await LoadOwnedWorkloadRecoveryAsync(token).ConfigureAwait(false);
                    StartDashboardCollector(candidate, target.SessionId);
                }
            }
            if (command.Command is "BuildPlan" or "AcceptPlan")
            {
                if (target is null || session?.IsValidated != true || command.TargetSessionId != target.SessionId)
                    return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
                if (command.ExpectedRevision != revision) return Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale");
                if (recoveryBlocked || pendingOwnedWorkloadId is not null) throw new QbittorrentException(ErrorCodes.RecoveryRequired);
                if (command.Command == "BuildPlan" && currentCycle is { } finished
                    && (finished.ApplyStatus == ApplyStatus.Reverted || finished.OperationStage == "completed"))
                {
                    currentCycle = null; experiment = null;
                    RevokeConfirmations();
                }
                if (currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
                    throw new QbittorrentException(ErrorCodes.RecoveryRequired);
                RevokeConfirmations();
                if (payloadValue is BuildPlanPayload build)
                {
                    var current = await session.ReadPreferencesAsync(token);
                    var interfaces = await session.ReadInterfacesAsync(token);
                    // A preference read is not a measured baseline. Preview cannot authorize writes.
                    if (currentCycle is not null && currentCycle.Inputs != build.Inputs)
                    {
                        // A baseline belongs to its exact input draft. Keep the old cycle in history,
                        // but do not let a changed draft reuse its authority or rewrite that record.
                        currentCycle = null;
                        experiment = null;
                        review = new PlanReview(target.SessionId);
                    }
                    if (currentCycle is { ApplyStatus: ApplyStatus.NotApplied, Original.Count: > 0 } backedUp)
                    {
                        // Preserve the first durable backup if cancellation preceded POST; edits use a new cycle.
                        var nextId = Guid.NewGuid();
                        var now = timeProvider.GetUtcNow();
                        currentCycle = backedUp with { CycleId = nextId, CreatedUtc = now, UpdatedUtc = now,
                            Original = new Dictionary<string, PreferenceValue>(), IntendedApplied = new Dictionary<string, PreferenceValue>(),
                            ObservedReadback = new Dictionary<string, PreferenceValue>(),
                            Experiment = backedUp.Experiment with { CycleId = nextId } };
                    }
                    var baseline = currentCycle?.Experiment.Baseline?.Status == MeasurementStatus.Valid
                        && currentCycle.BaselineContext is not null ? currentCycle.BaselineContext.Preferences : null;
                    var proposed = PlanBuilder.Build(target.SessionId, revision + 1, timeProvider.GetUtcNow(),
                        build.Inputs, current, session.Versions.LibtorrentMajor, interfaces, baseline, build.Selections);
                    review!.Replace(proposed);
                    draft = build.Inputs;
                    selections = build.Selections.Select(selection => selection with
                        { Overrides = selection.Overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }).ToArray();
                    revision++;
                    experiment = new(currentCycle?.CycleId, draft, currentCycle?.Experiment.Workload,
                        currentCycle?.Experiment.Baseline, currentCycle?.Experiment.After, proposed, currentCycle?.Experiment.Results ?? []);
                    await SaveCurrentPlanAsync(proposed, token).ConfigureAwait(false);
                }
                else if (payloadValue is AcceptPlanPayload approval)
                {
                    var approved = review!.Approve(approval.PlanId, approval.Revision);
                    revision++;
                    experiment = new(currentCycle?.CycleId, draft, currentCycle?.Experiment.Workload,
                        currentCycle?.Experiment.Baseline, currentCycle?.Experiment.After, approved, currentCycle?.Experiment.Results ?? []);
                    await SaveCurrentPlanAsync(approved, token).ConfigureAwait(false);
                }
            }
            if (command.Command == "SetUiPreferences")
            {
                if (command.ExpectedRevision != revision) return Failure(command.RequestId, "PLAN_STALE", "errors.planStale");
                var payload = command.Payload.Deserialize<SetUiPreferencesPayload>(Protocol.Json)!;
                var proposed = new UiPreferences(payload.Locale, payload.Theme);
                Directory.CreateDirectory(dataRoot);
                var temporary = Path.Combine(dataRoot, "ui.json.tmp");
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                { await JsonSerializer.SerializeAsync(stream, proposed, Protocol.Json, token); await stream.FlushAsync(token); stream.Flush(true); }
                File.Move(temporary, Path.Combine(dataRoot, "ui.json"), true);
                preferences = proposed;
                revision++;
            }
            if (payloadValue is ListHistoryPayload history)
            {
                var page = await new CycleStore(Path.Combine(dataRoot, "cycles")).ListAsync(history.Cursor, history.PageSize, token);
                return JsonSerializer.Serialize(new CommandReply<HistoryPage>(command.RequestId, true, revision, page, null), Protocol.Json);
            }
            if (payloadValue is ReadCyclePayload read)
            {
                var cycle = await new CycleStore(Path.Combine(dataRoot, "cycles")).ReadAsync(read.CycleId, cancellationToken: token)
                    ?? throw new QbittorrentException(ErrorCodes.BackupInvalid);
                var historical = cycle.Experiment with { Results = cycle.Experiment.Results.Select(card => card with { Context = ResultContext.Historical }).ToArray() };
                var historicalReply = JsonSerializer.Serialize(new CommandReply<ExperimentSummary>(command.RequestId, true, revision, historical, null), Protocol.Json);
                if (Encoding.UTF8.GetByteCount(historicalReply) > Protocol.MaxSnapshotBytes) throw new QbittorrentException(ErrorCodes.BackupInvalid);
                return historicalReply;
            }
            var snapshot = Snapshot();
            var reply = JsonSerializer.Serialize(new Reply(command.RequestId, true, revision, snapshot, null), Protocol.Json);
            if (Encoding.UTF8.GetByteCount(reply) > Protocol.MaxSnapshotBytes) throw new InvalidOperationException();
            Cache(command.RequestId, canonical, reply);
            if (command.Command == "Initialize") Initialized.TrySetResult();
            return reply;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return Failure(command.RequestId, "PERSISTENCE_FAILED", "errors.persistenceFailed"); }
        catch (OperationCanceledException) { throw; }
        catch (QbittorrentException error)
        { return Failure(command.RequestId, error.Code, ErrorMessageKey(error.Code)); }
        catch (Exception)
        { return Failure(command.RequestId, ErrorCodes.UnexpectedFailure, "errors.unknown"); }
        finally { if (stateAcquired) gate.Release(); commandAdmission.Release(); }
    }

    private static string ErrorMessageKey(string code) => code switch {
            ErrorCodes.AuthenticationFailed => "errors.authenticationFailed", ErrorCodes.VersionIncompatible => "errors.versionIncompatible",
            ErrorCodes.SchemaIncompatible => "errors.schemaIncompatible", ErrorCodes.ApiTimeout => "errors.apiTimeout",
            ErrorCodes.TlsRejected => "errors.tlsRejected", ErrorCodes.RedirectRejected => "errors.redirectRejected",
            ErrorCodes.InvalidEndpoint => "errors.invalidEndpoint", ErrorCodes.OperationConflict => "errors.operationConflict",
            ErrorCodes.InvalidOverride => "errors.invalidOverride", ErrorCodes.PlanNotApproved => "errors.planNotApproved",
            ErrorCodes.PlanStale => "errors.planStale", ErrorCodes.SessionStale => "errors.sessionStale",
            ErrorCodes.BackupInvalid => "errors.backupInvalid", ErrorCodes.PersistenceFailed => "errors.persistenceFailed",
            ErrorCodes.BaselineRequired => "errors.baselineRequired", ErrorCodes.PreferenceDrift => "errors.preferenceDrift",
            ErrorCodes.ApplyUnverified => "errors.applyUnverified", ErrorCodes.RollbackConflict => "errors.rollbackConflict",
            ErrorCodes.RecoveryRequired => "errors.recoveryRequired", ErrorCodes.ConfirmationExpired => "errors.confirmationExpired",
            ErrorCodes.MeasurementInvalid => "errors.measurementInvalid",
            ErrorCodes.OwnerUnavailable => "errors.ownerUnavailable",
            ErrorCodes.InvalidCommand => "errors.invalidCommand",
            "MEASUREMENT_CANCELLED" => "errors.operationCancelled",
            _ => "errors.apiUnavailable" };

    private AppSnapshot Snapshot()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";
        var phase = activeOperation?.Kind is OperationKind.Apply or OperationKind.Rollback or OperationKind.Restore ? ExperimentPhase.Applying
            : recoveryBlocked || pendingOwnedWorkloadId is not null || currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified ? ExperimentPhase.RecoveryRequired
            : currentCycle is null ? ExperimentPhase.Draft
            : Enum.TryParse<ExperimentPhase>(currentCycle.OperationStage, ignoreCase: true, out var currentPhase) ? currentPhase : ExperimentPhase.Draft;
        return new(1, 2, version, revision, preferences, target is null ? ConnectionState.Disconnected
            : session?.IsValidated == true ? ConnectionState.Validated : ConnectionState.Degraded, target,
            phase, currentCycle?.ApplyStatus ?? ApplyStatus.NotApplied, activeOperation, metrics, MutationActions())
        { Experiment = experiment ?? (draft is null ? null : new ExperimentSummary(null, draft, null, null, null, review?.Snapshot, [])), Confirmations = confirmations, Hardware = observedHardware, Restore = restorePreview,
            NetworkTest = networkTest, OwnedWorkloadCandidates = ownedWorkloadCandidates,
            WorkloadCatalogue = QFrey.Core.Workloads.WorkloadCatalogue.Entries.Select(entry => new WorkloadCatalogueSummary(entry.Id,
                entry.Label, entry.SizeBytes.ToString(CultureInfo.InvariantCulture), entry.SourceUrl)).ToArray() };
    }

    private async Task<string> StartMeasurementAsync(CommandEnvelope command, StartMeasurementPayload payload,
        string canonical, CancellationToken token)
    {
        var currentTarget = target;
        var currentSession = session;
        var inputs = draft;
        if (currentTarget is null || currentSession?.IsValidated != true || command.TargetSessionId != currentTarget.SessionId)
            return Failure(command.RequestId, ErrorCodes.SessionStale, "errors.sessionStale");
        if (command.ExpectedRevision != revision) return Failure(command.RequestId, ErrorCodes.PlanStale, "errors.planStale");
        if (activeOperation is not null) return Failure(command.RequestId, ErrorCodes.OperationConflict, "errors.operationConflict");
        if (pendingOwnedWorkloadId is not null) return Failure(command.RequestId, ErrorCodes.RecoveryRequired, "errors.recoveryRequired");
        if (recoveryBlocked) throw new QbittorrentException(ErrorCodes.RecoveryRequired);
        if (currentCycle?.ApplyStatus is ApplyStatus.Pending or ApplyStatus.Unverified or ApplyStatus.Verified)
            throw new QbittorrentException(ErrorCodes.RecoveryRequired);
        if (payload.Kind != MeasurementKind.Baseline || inputs is null || payload.Workload.Kind is not (WorkloadKind.Existing or WorkloadKind.Owned))
            return Failure(command.RequestId, ErrorCodes.MeasurementInvalid, "errors.measurementInvalid");

        var workload = payload.Workload.Kind == WorkloadKind.Owned
            ? await ResolveOwnedMeasurementAsync(payload.Workload, token).ConfigureAwait(false)
            : await currentSession.ReadExistingWorkloadAsync(payload.Workload, token).ConfigureAwait(false);
        RevokeConfirmations();
        var operationId = Guid.NewGuid();
        var cycleId = Guid.NewGuid();
        var operationCancellation = new CancellationTokenSource();
        var operationSessionId = currentTarget.SessionId;
        var operationTarget = currentTarget;
        var operationSession = currentSession;
        var operationInputs = inputs;
        var operationSelections = selections.Select(selection => selection with
            { Overrides = selection.Overrides.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }).ToArray();
        activeOperation = new(operationId, OperationKind.Measurement, "baseline", null, true);
        revision++;

        collectorCancellation?.Cancel();
        await collectorTask.ConfigureAwait(false);
        collectorCancellation?.Dispose();
        collectorCancellation = null;
        collectorTask = Task.CompletedTask;
        measurementCancellation = operationCancellation;
        measurementTask = Task.Run(() => RunBaselineMeasurementAsync(operationId, cycleId, operationSessionId,
            operationTarget, operationSession, operationInputs, operationSelections, workload, operationCancellation));

        var accepted = JsonSerializer.Serialize(new CommandReply<AcceptedOperation>(command.RequestId, true,
            revision, new(operationId, revision), null), Protocol.Json);
        Cache(command.RequestId, canonical, accepted);
        return accepted;
    }

    private async Task RunBaselineMeasurementAsync(Guid operationId, Guid cycleId, Guid sessionId,
        TargetSummary operationTarget, QbittorrentSession operationSession, DraftInputs inputs, PlanSelection[] selections,
        WorkloadSummary workload, CancellationTokenSource cancellation)
    {
        try
        {
            Func<LiveMetric[]>? local = null;
            try { local = localMetrics?.Invoke(operationSession.Endpoint.AbsoluteUri); }
            catch { /* Local metrics are optional and never block the read-only server measurement. */ }
            var capture = await MeasurementRunner.RunAsync(operationSession, sessionId, operationId,
                MeasurementKind.Baseline, workload.Reference.Hashes,
                (sample, token) => PublishMeasurementSampleAsync(operationId, sessionId, sample, local, token),
                cancellation.Token, timeProvider).ConfigureAwait(false);
            TargetInterface[] interfaces = [];
            if (capture.Measurement.Summary.Status == MeasurementStatus.Valid && !cancellation.IsCancellationRequested)
            {
                try { interfaces = await operationSession.ReadInterfacesAsync(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception) { /* An unavailable interface blocks only interface-dependent plan entries. */ }
            }

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (disposed || activeOperation?.Id != operationId || target?.SessionId != sessionId) return;
                // Cancel accepted before this boundary must remain cancelled, including after final API reads.
                if (cancellation.IsCancellationRequested) capture = MeasurementRunner.MarkCancelled(capture);
                activeOperation = activeOperation with { Stage = "completing", Cancellable = false };
                var planRevision = revision + 1;
                var record = ExperimentCommands.CreateBaselineCycle(capture, workload, inputs, operationTarget,
                    cycleId, planRevision, timeProvider.GetUtcNow(), operationSession.Versions.LibtorrentMajor, interfaces, selections);
                using var saveDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await new CycleStore(Path.Combine(dataRoot, "cycles")).SaveAsync(record, saveDeadline.Token).ConfigureAwait(false);
                currentCycle = record;
                experiment = record.Experiment;
                review?.Replace(record.Plan);
                revision++;
                availableActions = record.Experiment.Baseline?.Status == MeasurementStatus.Valid
                    ? [] : [new("measurement", false, "MEASUREMENT_INVALID", "errors.measurementInvalid")];
            }
            finally { gate.Release(); }
        }
        catch (Exception)
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!disposed && activeOperation?.Id == operationId)
                    availableActions = [new("measurement", false, "MEASUREMENT_INVALID", "errors.measurementInvalid")];
            }
            finally { gate.Release(); }
        }
        finally
        {
            string? completionEvent = null;
            await commandAdmission.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (activeOperation?.Id == operationId)
                    {
                        activeOperation = null;
                        revision++;
                        completionEvent = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId,
                            NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    }
                    if (ReferenceEquals(measurementCancellation, cancellation)) measurementCancellation = null;
                }
                finally { gate.Release(); }

                if (completionEvent is not null)
                {
                    try { await PublishSnapshotAsync(completionEvent).ConfigureAwait(false); }
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
            finally
            {
                commandAdmission.Release();
                cancellation.Dispose();
            }
        }
    }

    private async ValueTask PublishMeasurementSampleAsync(Guid operationId, Guid sessionId, LiveSample sample,
        Func<LiveMetric[]>? local, CancellationToken token)
    {
        if (local is not null)
        {
            try { sample = sample with { Metrics = Array.AsReadOnly(sample.Metrics.Concat(local()).ToArray()) }; }
            catch { /* Do not turn an optional process sampler failure into a bad server sample. */ }
        }
        string? message = null;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!disposed && activeOperation is { } operation && operation.Id == operationId && target?.SessionId == sessionId)
            {
                metrics = sample.Metrics.ToArray();
                var stage = sample.Sequence < 10 ? "baselineWarmup" : "baselineSampling";
                var progress = Math.Clamp((sample.Sequence + 1) * 100d / 70d, 0, 100);
                activeOperation = operation with { Stage = stage, Progress = progress };
                message = JsonSerializer.Serialize(new SnapshotEvent(sessionId, operationId, NextEventSequence(),
                    revision, Snapshot()), Protocol.Json);
            }
        }
        finally { gate.Release(); }
        if (message is not null) await PublishSnapshotAsync(message).ConfigureAwait(false);
    }

    private async Task SaveCurrentPlanAsync(Plan plan, CancellationToken token)
    {
        if (currentCycle is null || draft is null || draft != currentCycle.Inputs) return;
        var stage = currentCycle.Experiment.Baseline?.Status == MeasurementStatus.Valid
            ? plan.Applicable ? "planReady" : "baselineReady" : "draft";
        var updated = currentCycle with
        {
            Plan = plan,
            UpdatedUtc = timeProvider.GetUtcNow(),
            OperationStage = stage,
            Experiment = experiment ?? (currentCycle.Experiment with { Plan = plan })
        };
        await new CycleStore(Path.Combine(dataRoot, "cycles")).SaveAsync(updated, token).ConfigureAwait(false);
        currentCycle = updated;
        experiment = updated.Experiment;
    }

    private void Cache(Guid requestId, string canonical, string reply)
    {
        if (!completed.TryAdd(requestId, (canonical, reply))) throw new InvalidOperationException("DUPLICATE_REQUEST_CONFLICT");
        order.Enqueue(requestId);
        while (order.Count > 256) completed.TryRemove(order.Dequeue(), out _);
    }

    private long NextEventSequence() => Interlocked.Increment(ref eventSequence) - 1;

    private void StartDashboardCollector(QbittorrentSession source, Guid sessionId)
    {
        collectorCancellation?.Dispose();
        collectorCancellation = new CancellationTokenSource();
        var token = collectorCancellation.Token;
        collectorTask = Task.Run(() => CollectAsync(source, sessionId, token));
    }

    private async Task PublishSnapshotAsync(string message)
    {
        if (SnapshotPublished is not { } publish) return;
        foreach (Func<string, Task> handler in publish.GetInvocationList())
            await handler(message).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private async Task CollectAsync(QbittorrentSession source, Guid sessionId, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var local = localMetrics?.Invoke(source.Endpoint.AbsoluteUri);
        var collector = new LiveCollector(sessionId, async cancellation =>
        {
            var transfer = await source.ReadTransferMetricsAsync(cancellation).ConfigureAwait(false);
            return local is null ? transfer : [.. transfer, .. local()];
        }, timeProvider);
        var run = collector.RunAsync(null, stop.Token);
        try
        {
            await foreach (var sample in collector.Latest.ReadAllAsync(token))
            {
                if (!await gate.WaitAsync(0, token)) continue;
                string? message = null;
                try
                {
                    if (!disposed && target?.SessionId == sample.SessionId)
                    {
                        metrics = sample.Metrics.ToArray();
                        message = JsonSerializer.Serialize(new SnapshotEvent(sample.SessionId, null, NextEventSequence(), revision, Snapshot()), Protocol.Json);
                    }
                }
                finally { gate.Release(); }
                if (message is not null) await PublishSnapshotAsync(message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // A disappearing UI sink stops this owned collector; never leave an unobserved task.
        catch (Exception) { }
        finally { stop.Cancel(); await run; }
    }

    private string Failure(Guid requestId, string code, string key) => JsonSerializer.Serialize(new Reply(requestId, false, revision, null,
        new AppError(code, key, ErrorSeverity.Error, RetryPolicy.None, [], Guid.NewGuid())), Protocol.Json);
}
