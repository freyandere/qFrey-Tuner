import type { AppSnapshot, ApplyStatus, ExperimentPhase, Locale, MetricReading, MetricScope, MetricUnit } from './protocol';

export type PreferenceValue = number | boolean | string; // Numbers must be int32 at the API boundary.
export type ResultCategory = 'throughput' | 'stability' | 'ramp' | 'connectivity' | 'resources' | 'configuration' | 'limitations';
export type Evidence = 'observed' | 'derived' | 'heuristic' | 'userProvided';
export type Verdict = 'observedImprovement' | 'observedRegression' | 'inconclusive' | 'notComparable' | 'notMeasured';
export type MeasurementStatus = 'pending' | 'warmingUp' | 'sampling' | 'valid' | 'invalid' | 'cancelled';
export type InputSource = 'manual' | 'measured' | 'localDetection';
export type LocalizedMessage = { key: string; parameters: Record<string, string | number> };
export type HistoricalValue = { quality: 'valid'; value: number; measuredAtUtc: string }
  | { quality: 'invalid' | 'notMeasured'; reasonCodes: string[] };
export interface ComparisonValue {
  metricId: string; category: ResultCategory; unit: MetricUnit; before: HistoricalValue; after: HistoricalValue;
  absoluteDelta: number | null; relativeDeltaPercent: number | null; verdict: Verdict;
  reasonCodes: string[]; evidence: Evidence; scope: MetricScope;
}
export interface DraftInputs {
  network: { downloadMbps: number; uploadMbps: number; connectionType: 'fiber' | 'cableDsl' | 'mobile4G';
    useVpn: boolean; vpnInterface: string; ispThrottling: boolean; downloadSource: InputSource; uploadSource: InputSource };
  hardware: { storageType: 'hdd' | 'ssdSata' | 'nvme'; ramGiB: number; cpuCores: number; isHybridCpu: boolean; performanceCores: number; source: InputSource };
  usage: { trackerType: 'public' | 'private'; userRole: 'leecher' | 'seeder' | 'uploader'; environment: 'system' | 'portable' | 'truenas' | 'nas' | 'docker' | 'seedbox' };
  proposedPort: number | null;
}
export interface Recommendation {
  id: string; groupId: string; apiKey: string; category: ResultCategory; titleKey: string; reason: LocalizedMessage;
  evidence: Evidence; currentValue: PreferenceValue | null; proposedValue: PreferenceValue; unit: string;
  valueType: 'number' | 'bool' | 'enum' | 'interface'; editable: boolean;
  allowedRange: { minimum: number; maximum: number; step: number } | null;
  allowedValues: { value: PreferenceValue; labelKey: string }[];
  supportStatus: 'supported' | 'missingOptional' | 'missingRequired' | 'incompatible'; selected: boolean; cautionCodes: string[];
}
export interface PlanSelection { groupId: string; selected: boolean; overrides: Record<string, PreferenceValue> }
export interface NativeSelection { token: string; purpose: 'restore' | 'exportJson' | 'exportHtml' | 'volume'; displayName: string; expiresUtc: string }
export interface Plan {
  id: string; targetSessionId: string; revision: number; createdUtc: string; inputsFingerprint: string; baselineFingerprint: string;
  previewOnly: boolean; applicable: boolean; approved: boolean; blockReasonCodes: string[];
  recommendations: Recommendation[]; omissions: { apiKey: string; required: boolean; reasonCode: string }[];
  original: Record<string, PreferenceValue>; proposed: Record<string, PreferenceValue>;
}
export interface WorkloadReference { id: string; kind: 'existing' | 'owned'; hashes: string[] }
export interface WorkloadSummary { reference: WorkloadReference; name: string; totalBytesDecimal: string; serverSavePath: string; catalogueId: string | null; ownershipVerified: boolean; reasonCodes: string[] }
export interface MeasurementSample {
  elapsedMs: number; status: MeasurementStatus; reasonCodes: string[]; sessionDownload: MetricReading; sessionUpload: MetricReading;
  workloadDownload: MetricReading; workloadUpload: MetricReading; seeds: MetricReading; peers: MetricReading;
  active: number | null; stalled: number | null; errors: number | null;
}
export interface MeasurementSummary {
  id: string; kind: 'baseline' | 'after'; status: MeasurementStatus; startedUtc: string; analysisVersion: string; scope: MetricScope;
  reasonCodes: string[]; sampleCount: number; durationMs: number; meanDownload: HistoricalValue; medianDownload: HistoricalValue;
  standardDeviation: HistoricalValue; zeroSamplePercent: HistoricalValue;
}
export type ResultContent = { type: 'comparison'; comparison: ComparisonValue }
  | { type: 'observation'; metricId: string; value: HistoricalValue; unit: MetricUnit; scope: MetricScope }
  | { type: 'change'; apiKey: string; before: PreferenceValue; intended: PreferenceValue; observed: PreferenceValue | null; status: ApplyStatus }
  | { type: 'limitation'; reasonCodes: string[] }
  | { type: 'recovery'; conflictKeys: string[]; actionIds: string[]; backupAvailable: boolean };
export interface ResultCard {
  id: string; category: ResultCategory; kind: 'metricComparison' | 'observation' | 'appliedChange' | 'limitation' | 'recovery';
  titleKey: string; explanation: LocalizedMessage; evidence: Evidence; severity: 'neutral' | 'info' | 'warning' | 'error';
  context: 'historical' | 'currentExperiment'; cycleId: string; measuredAtUtc: string | null; content: ResultContent;
}
export interface ExperimentSummary { cycleId: string | null; runInputs: DraftInputs | null; workload: WorkloadSummary | null; baseline: MeasurementSummary | null; after: MeasurementSummary | null; plan: Plan | null; results: ResultCard[] }
export interface CycleSummary { cycleId: string; createdUtc: string; endpoint: string; qbittorrentVersion: string; phase: ExperimentPhase; applyStatus: ApplyStatus; changeCount: number }
export interface HistoryPage { items: CycleSummary[]; nextCursor: string | null }
export interface TargetIdentity { endpoint: string; qbittorrentVersion: string; apiVersion: string; libtorrentVersion: string }
export interface MeasurementContext { preferences: Record<string, PreferenceValue>; preferencesFingerprint: string; activeHashes: string[]; selectedTorrents: { hash: string; state: string; progress: number | null }[] }
export interface CycleRecord {
  schemaVersion: 2; analysisVersion: string; cycleId: string; createdUtc: string; updatedUtc: string; target: TargetIdentity;
  inputs: DraftInputs; plan: Plan; original: Record<string, PreferenceValue>; intendedApplied: Record<string, PreferenceValue>;
  observedReadback: Record<string, PreferenceValue>; operationStage: string; applyStatus: ApplyStatus; experiment: ExperimentSummary;
  baselineSamples: MeasurementSample[]; afterSamples: MeasurementSample[];
  baselineContext?: MeasurementContext | null;
}
export interface ConfirmationSummary { token: string; targetSessionId: string; revision: number; expiresUtc: string; actionId: string; description: LocalizedMessage }
export interface SnapshotEvent { sessionId: string | null; operationId: string | null; sequence: number; revision: number; snapshot: AppSnapshot }
export interface AcceptedOperation { operationId: string; revision: number }
export interface ObservedHardware { inputs: DraftInputs['hardware'] | null; facts: HardwareFacts; reasonCodes: string[] }
export interface HardwareFacts { ramBytes: MetricReading; logicalCpuCount: MetricReading; performanceCpuCount: MetricReading; storageType: DraftInputs['hardware']['storageType'] | null; storageReasonCode: string | null; volumeToken: string | null }
export interface TargetInterface { id: string; name: string }
export interface NetworkTestResult { downloadBytesPerSecond: number | null; uploadBytesPerSecond: number | null; measuredUtc: string; reasonCodes: string[] }
export type Authentication = { kind: 'bypass' } | { kind: 'password'; username: string; password: string } | { kind: 'apiKey'; apiKey: string };
export type ProductCommand =
  | { command: 'Connect'; payload: { endpoint: string; auth: Authentication } }
  | { command: 'Disconnect'; payload: Record<string, never> }
  | { command: 'DetectLocalHardware'; payload: { volumeToken: string | null } }
  | { command: 'RunNetworkTest'; payload: { confirmationToken: string } }
  | { command: 'BuildPlan'; payload: { inputs: DraftInputs; selections: PlanSelection[] } }
  | { command: 'AcceptPlan'; payload: { planId: string; revision: number } }
  | { command: 'StartMeasurement'; payload: { kind: 'baseline' | 'after'; workload: WorkloadReference } }
  | { command: 'CancelOperation'; payload: { operationId: string } }
  | { command: 'PrepareWorkload'; payload: { catalogueId: string; serverSavePath: string; approvalToken: string } }
  | { command: 'StopOwnedWorkload'; payload: { workloadId: string; confirmationToken: string } }
  | { command: 'StartOwnedWorkload'; payload: { workloadId: string; confirmationToken: string } }
  | { command: 'SelectOwnedWorkload'; payload: { workloadId: string } }
  | { command: 'DeleteOwnedWorkload'; payload: { workloadId: string; deleteFiles: boolean; confirmationToken: string } }
  | { command: 'ApplyPlan'; payload: { planId: string; expectedRevision: number; confirmationToken: string } }
  | { command: 'Rollback'; payload: { cycleId: string; expectedRevision: number; confirmationToken: string } }
  | { command: 'RestoreLegacyBackup'; payload: { selectionToken: string; confirmationToken: string } }
  | { command: 'KeepChanges'; payload: { cycleId: string; applyStatus: 'verified' } }
  | { command: 'ListHistory'; payload: { cursor: string | null; pageSize: number } }
  | { command: 'ReadCycle'; payload: { cycleId: string } }
  | { command: 'ExportReport'; payload: { cycleId: string; format: 'json' | 'html'; destinationToken: string; locale: Locale } }
  | { command: 'StartTarget'; payload: { confirmationToken: string } }
  | { command: 'StopTarget'; payload: { confirmationToken: string } }
  | { command: 'RestartTarget'; payload: { confirmationToken: string } }
  | { command: 'RequestConfirmation'; payload: { actionId: string; planId: string | null; cycleId: string | null; workloadId: string | null; catalogueId?: string | null; serverSavePath?: string | null; selectionToken?: string | null } }
  | { command: 'SelectNativeFile'; payload: { purpose: 'restore' | 'exportJson' | 'exportHtml' | 'volume' } };
