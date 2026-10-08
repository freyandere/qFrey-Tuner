using QFrey.Core.Contracts;
using QFrey.Core.Metrics;
using QFrey.Core.Qbittorrent;
using QFrey.Core.Tuning;

namespace QFrey.Desktop.Bridge;

internal static class ExperimentCommands
{
    public static async Task ValidateBaselineAsync(CycleRecord cycle, QbittorrentSession session, CancellationToken token,
        Func<WorkloadReference, CancellationToken, Task>? validateOwned = null)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(session);

        var summary = cycle.Experiment?.Baseline;
        var context = cycle.BaselineContext;
        var workload = cycle.Experiment?.Workload;
        var reference = workload?.Reference;
        var validSamples = false;
        if (summary is not null && cycle.BaselineSamples is not null)
        {
            try
            {
                validSamples = MeasurementAnalysis.Analyze(summary.Id, summary.Kind, summary.StartedUtc,
                    summary.Scope, cycle.BaselineSamples).Status == MeasurementStatus.Valid;
            }
            catch (ArgumentException) { }
        }
        if (summary is null || summary.Id == Guid.Empty || summary.Kind != MeasurementKind.Baseline || summary.Status != MeasurementStatus.Valid
            || summary.AnalysisVersion != MeasurementAnalysis.Version
            || !validSamples
            || context is null || reference is null || reference.Kind is not (WorkloadKind.Existing or WorkloadKind.Owned)
            || reference.Kind == WorkloadKind.Owned && (validateOwned is null || workload!.OwnershipVerified != true)
            || reference.Id == Guid.Empty
            || reference.Hashes is not { Length: > 0 and <= 5000 }
            || reference.Hashes.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))
            || reference.Hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reference.Hashes.Length)
            throw new QbittorrentException(ErrorCodes.BaselineRequired);

        if (reference.Kind == WorkloadKind.Owned)
            await validateOwned!(reference, token).ConfigureAwait(false);

        var selected = context.SelectedTorrents;
        var active = context.ActiveHashes;
        if (cycle.Plan is null || context.Preferences is null || context.PreferencesFingerprint != PlanBuilder.FingerprintPreferences(context.Preferences)
            || cycle.Plan.BaselineFingerprint != context.PreferencesFingerprint
            || selected is not { Length: > 0 and <= 5000 } || active is not { Length: > 0 and <= 5000 }
            || selected.Any(row => row is null || row.Hash is not { Length: 40 } || !row.Hash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(row.State) || row.State.Length > 32
                || row.Progress is double progress && (!double.IsFinite(progress) || progress is < 0 or > 1))
            || active.Any(hash => hash is not { Length: 40 } || !hash.All(Uri.IsHexDigit))
            || selected.Select(row => row.Hash).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length
            || active.Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length
            || !new HashSet<string>(selected.Select(row => row.Hash), StringComparer.OrdinalIgnoreCase)
                .SetEquals(reference.Hashes)
            || selected.Any(row => !active.Contains(row.Hash, StringComparer.OrdinalIgnoreCase)))
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);

        var baselineTorrents = new TorrentTelemetryContext(selected.Select(row => row.Hash).ToArray(), active,
            null, null)
        {
            SelectedTorrents = selected.Select(row => new SelectedTorrentMetadata(row.Hash, row.State,
                null, row.Progress, null)).ToArray()
        };
        // A valid baseline could only have been captured with both controls explicitly disabled.
        var baseline = new MeasurementContinuitySnapshot(context.PreferencesFingerprint, false, false, baselineTorrents);
        if (MeasurementContinuity.Validate(baseline, baseline).Count != 0)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);

        await session.RevalidateVersionsAsync(token).ConfigureAwait(false);
        var versions = session.Versions;
        if (cycle.Target.Endpoint != session.Endpoint.AbsoluteUri
            || cycle.Target.QbittorrentVersion != versions.Qbittorrent
            || cycle.Target.ApiVersion != versions.WebApi
            || cycle.Target.LibtorrentVersion != versions.Libtorrent)
            throw new QbittorrentException(ErrorCodes.BaselineRequired);
        var snapshot = await session.ReadMeasurementSnapshotAsync(reference.Hashes, token).ConfigureAwait(false);
        if (PlanBuilder.FingerprintPreferences(snapshot.Preferences) != context.PreferencesFingerprint)
            throw new QbittorrentException(ErrorCodes.PreferenceDrift);
        if (snapshot.SchedulerEnabled is not false || snapshot.AlternativeLimitsEnabled is not false)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);

        var current = new MeasurementContinuitySnapshot(PlanBuilder.FingerprintPreferences(snapshot.Preferences),
            snapshot.SchedulerEnabled, snapshot.AlternativeLimitsEnabled, snapshot.Torrents.Context);
        if (MeasurementContinuity.Validate(baseline, current).Count != 0)
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);
    }

    public static MeasurementContext? CreateBaselineContext(MeasurementCapture capture)
    {
        if (capture.Measurement.Summary.Status != MeasurementStatus.Valid) return null;
        var snapshot = capture.InitialSnapshot;
        var telemetry = snapshot.Torrents.Context;
        if (telemetry.ActiveHashes is not { Count: > 0 and <= 5000 } active
            || telemetry.SelectedTorrents is not { Count: > 0 and <= 5000 } selected
            || selected.Any(row => row.State is null))
            throw new QbittorrentException(ErrorCodes.MeasurementInvalid);

        return new(snapshot.Preferences, PlanBuilder.FingerprintPreferences(snapshot.Preferences), active.ToArray(),
            selected.Select(row => new TorrentRunContext(row.Hash, row.State!, row.Progress)).ToArray());
    }

    public static CycleRecord CreateBaselineCycle(MeasurementCapture capture, WorkloadSummary workload,
        DraftInputs inputs, TargetSummary target, Guid cycleId, long planRevision,
        DateTimeOffset createdUtc, int libtorrentMajor, IReadOnlyList<TargetInterface> interfaces,
        PlanSelection[] selections)
    {
        var validBaseline = capture.Measurement.Summary.Status == MeasurementStatus.Valid;
        var preferences = capture.InitialSnapshot.Preferences;
        var plan = PlanBuilder.Build(target.SessionId, planRevision, createdUtc, inputs, preferences,
            libtorrentMajor, interfaces, validBaseline ? preferences : null, selections);
        var phase = validBaseline
            ? plan.Applicable ? ExperimentPhase.PlanReady : ExperimentPhase.BaselineReady
            : ExperimentPhase.Draft;
        var experiment = new ExperimentSummary(cycleId, inputs, workload,
            capture.Measurement.Summary, null, plan, []);
        var cycle = new CycleRecord(Protocol.SchemaVersion, MeasurementAnalysis.Version, cycleId,
            createdUtc, createdUtc, new(target.Endpoint, target.QbittorrentVersion, target.ApiVersion, target.LibtorrentVersion),
            inputs, plan, new Dictionary<string, PreferenceValue>(), new Dictionary<string, PreferenceValue>(),
            new Dictionary<string, PreferenceValue>(), System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(phase.ToString()),
            ApplyStatus.NotApplied, experiment, capture.Measurement.Samples, []);
        return cycle with { BaselineContext = CreateBaselineContext(capture) };
    }
}
