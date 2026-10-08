import type { ConfirmationSummary, ExperimentSummary, ProductCommand, ObservedHardware, NetworkTestResult, TargetInterface, WorkloadSummary } from './domain';
export type Locale = 'ru-RU' | 'en-US';
export type ThemePreference = 'system' | 'dark' | 'light';
export type ConnectionState = 'disconnected' | 'connecting' | 'validated' | 'degraded' | 'incompatible';
export type ExperimentPhase = 'draft' | 'baselineReady' | 'planReady' | 'applying' | 'appliedVerified' | 'afterReady' | 'completed' | 'recoveryRequired' | 'rolledBack';
export type ApplyStatus = 'notApplied' | 'pending' | 'verified' | 'unverified' | 'reverted';
export type MetricReading = { status: 'fresh'; value: number; sampledAtUtc: string }
  | { status: 'stale'; lastValue: number; sampledAtUtc: string; reasonCode: string }
  | { status: 'unavailable' | 'error'; reasonCode: string };
export type MetricUnit = 'bytesPerSecond' | 'bytes' | 'milliseconds' | 'count' | 'percent';
export type MetricScope = 'session' | 'workload' | 'localProcess' | 'localHost' | 'endpoint';
export interface LiveMetric { id: string; unit: MetricUnit; scope: MetricScope; source: string; reading: MetricReading }
export interface AppSnapshot {
  protocolVersion: 1; schemaVersion: 2; appVersion: string; revision: number;
  preferences: { locale: Locale; theme: ThemePreference }; connection: ConnectionState;
  target: null | { sessionId: string; endpoint: string; qbittorrentVersion: string; apiVersion: string; libtorrentVersion: string; isLocal: boolean };
  phase: ExperimentPhase; applyStatus: ApplyStatus;
  activeOperation: null | { id: string; kind: 'networkTest' | 'hardwareDetection' | 'measurement' | 'prepareWorkload' | 'stopWorkload' | 'deleteWorkload' | 'apply' | 'rollback' | 'restore' | 'startTarget' | 'stopTarget' | 'restartTarget' | 'startWorkload'; stage: string; progress: number | null; cancellable: boolean };
  metrics: LiveMetric[];
  availableActions: { id: string; enabled: boolean; reasonCode: string | null; messageKey: string }[];
  experiment?: ExperimentSummary | null;
  confirmations?: ConfirmationSummary[];
  hardware?: ObservedHardware | null;
  networkTest?: NetworkTestResult | null;
  interfaces?: TargetInterface[];
  workloadCatalogue?: { id: string; name: string; totalBytesDecimal: string; metadataSource: string }[];
  ownedWorkloadCandidates?: WorkloadSummary[] | null;
  restore?: { selectionToken: string; displayName: string; sourceCycleId: string; review: {
    isLegacy: boolean; targetMatches: boolean; canRestore: boolean; alreadyOriginal: boolean; fingerprint: string;
    blockReasonCodes: string[]; differences: { key: string; original: number | boolean | string | null;
      intended: number | boolean | string | null; current: number | boolean | string | null;
      disposition: 'alreadyOriginal' | 'restoreRequired' | 'conflict' | 'missingLiveValue' | 'invalidBackupValue'
        | 'invalidLiveValue' | 'invalidIntendedValue' | 'intendedUnknown' }[];
  } } | null;
}
export interface AppError { code: string; messageKey: string; severity: 'info' | 'warning' | 'error'; retry: 'safe' | 'reconcileFirst' | 'none'; recoveryActionIds: string[]; correlationId: string; parameters?: Record<string, string | number> }
export interface Reply { requestId: string; ok: boolean; revision: number; data: AppSnapshot | null; error: AppError | null }
export type Command = { command: 'Initialize'; payload: { protocolVersion: 1 } }
  | { command: 'SetUiPreferences'; payload: { locale: Locale; theme: ThemePreference } };
export type Request = (Command | ProductCommand) & { protocolVersion: 1; requestId: string; targetSessionId: string | null; expectedRevision: number | null };
export type CommandReply<T> = { requestId: string; ok: true; revision: number; data: T; error: null }
  | { requestId: string; ok: false; revision: number; data: null; error: AppError };
