import type { AppError, AppSnapshot, Command, Reply, Request } from '../contracts/protocol';
import type { ProductCommand, HistoryPage, ExperimentSummary, AcceptedOperation, NativeSelection } from '../contracts/domain';
import { en } from '../i18n/messages';
import { experiment, confirmation, finite, uuid, timestamp, acceptedOperation, metricReading, hardware, networkTest } from '../contracts/validation';
interface WebView { postMessage(message: Request): void; addEventListener(type: 'message', handler: (event: MessageEvent<unknown>) => void): void }
declare global { interface Window { chrome?: { webview?: WebView } } }
const pending = new Map<string, { resolve: (data: unknown) => void; validate: (data: unknown, revision: number) => boolean; reject: (error: Error) => void; timer: ReturnType<typeof setTimeout> }>();
let revision: number | null = null;
let listening = false;
let initialized = false;
let sessionId: string | null = null;
let sequence = -1;
const subscribers = new Set<(snapshot: AppSnapshot) => void>();

export function subscribeSnapshots(listener: (snapshot: AppSnapshot) => void): () => void {
  subscribers.add(listener);
  return () => { subscribers.delete(listener); };
}

const connectionStates = ['disconnected', 'connecting', 'validated', 'degraded', 'incompatible'];
const phases = ['draft', 'baselineReady', 'planReady', 'applying', 'appliedVerified', 'afterReady', 'completed', 'recoveryRequired', 'rolledBack'];
const applyStatuses = ['notApplied', 'pending', 'verified', 'unverified', 'reverted'];
const metricUnits = ['bytesPerSecond', 'bytes', 'milliseconds', 'count', 'percent'];
const metricScopes = ['session', 'workload', 'localProcess', 'localHost', 'endpoint'];
const severities = ['info', 'warning', 'error'];
const retryOptions = ['safe', 'reconcileFirst', 'none'];
const isRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);
const isString = (value: unknown): value is string => typeof value === 'string' && value.length > 0;
const isFiniteNumber = finite;
const isSafeRevision = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0;
const isOneOf = (value: unknown, options: readonly string[]): boolean => typeof value === 'string' && options.includes(value);

export function isSnapshot(value: unknown, replyRevision: number): value is AppSnapshot {
  if (!isRecord(value) || value.protocolVersion !== 1 || value.schemaVersion !== 2 || !isString(value.appVersion)
    || value.revision !== replyRevision || !isSafeRevision(value.revision) || !isRecord(value.preferences)
    || !isOneOf(value.preferences.locale, ['ru-RU', 'en-US']) || !isOneOf(value.preferences.theme, ['system', 'dark', 'light'])
    || !isOneOf(value.connection, connectionStates) || !isOneOf(value.phase, phases) || !isOneOf(value.applyStatus, applyStatuses)
    || !Array.isArray(value.metrics) || value.metrics.length > 600 || !Array.isArray(value.availableActions) || value.availableActions.length > 128) return false;
  if (value.experiment !== undefined && value.experiment !== null && !experiment(value.experiment)) return false;
  if (value.confirmations !== undefined && (!Array.isArray(value.confirmations) || value.confirmations.length > 32 || !value.confirmations.every(confirmation))) return false;
  if (value.hardware !== undefined && value.hardware !== null && !hardware(value.hardware)) return false;
  if (value.networkTest !== undefined && value.networkTest !== null && !networkTest(value.networkTest)) return false;
  if (value.interfaces !== undefined && (!Array.isArray(value.interfaces) || value.interfaces.length > 128
    || !value.interfaces.every(i => isRecord(i) && isString(i.id) && isString(i.name)))) return false;
  if (value.workloadCatalogue !== undefined && (!Array.isArray(value.workloadCatalogue) || value.workloadCatalogue.length > 16
    || !value.workloadCatalogue.every(item => {
      if (!isRecord(item) || !isString(item.id) || item.id.length > 64 || !isString(item.name) || item.name.length > 256
        || !isString(item.totalBytesDecimal) || !/^[1-9][0-9]{0,19}$/.test(item.totalBytesDecimal)
        || BigInt(item.totalBytesDecimal) > 18446744073709551615n || !isString(item.metadataSource)) return false;
      try { const source = new URL(item.metadataSource); return source.protocol === 'https:' && !source.username && !source.password; }
      catch { return false; }
    }))) return false;
  if (value.restore !== undefined && value.restore !== null) {
    const restore = value.restore;
    if (!isRecord(restore) || !isString(restore.selectionToken) || restore.selectionToken.length > 128
      || !isString(restore.displayName) || restore.displayName.length > 512 || !uuid(restore.sourceCycleId)
      || !isRecord(restore.review)) return false;
    const review = restore.review;
    if (![review.isLegacy, review.targetMatches, review.canRestore, review.alreadyOriginal].every(flag => typeof flag === 'boolean')
      || !isString(review.fingerprint) || !/^[a-fA-F0-9]{64}$/.test(review.fingerprint)
      || !Array.isArray(review.blockReasonCodes) || review.blockReasonCodes.length > 32
      || !review.blockReasonCodes.every(code => isString(code) && code.length <= 128)
      || !Array.isArray(review.differences) || review.differences.length > 64
      || !review.differences.every(diff => isRecord(diff) && isString(diff.key) && diff.key.length <= 128
        && isOneOf(diff.disposition, ['alreadyOriginal', 'restoreRequired', 'conflict', 'missingLiveValue',
          'invalidBackupValue', 'invalidLiveValue', 'invalidIntendedValue', 'intendedUnknown'])
        && [diff.original, diff.intended, diff.current].every(preference => preference === null || typeof preference === 'boolean'
          || typeof preference === 'string' && preference.length <= 256
          || typeof preference === 'number' && Number.isInteger(preference) && preference >= -2147483648 && preference <= 2147483647))) return false;
  }

  if (value.target !== null && (!isRecord(value.target) || !uuid(value.target.sessionId) || !isString(value.target.endpoint)
    || !isString(value.target.qbittorrentVersion) || !isString(value.target.apiVersion) || !isString(value.target.libtorrentVersion)
    || typeof value.target.isLocal !== 'boolean')) return false;
  if (value.activeOperation !== null && (!isRecord(value.activeOperation) || !uuid(value.activeOperation.id)
    || !isOneOf(value.activeOperation.kind, ['networkTest', 'hardwareDetection', 'measurement', 'prepareWorkload', 'stopWorkload', 'deleteWorkload', 'apply', 'rollback', 'restore', 'startTarget', 'stopTarget', 'restartTarget']) || !isString(value.activeOperation.stage)
    || (value.activeOperation.progress !== null && (!isFiniteNumber(value.activeOperation.progress) || value.activeOperation.progress < 0 || value.activeOperation.progress > 100))
    || typeof value.activeOperation.cancellable !== 'boolean')) return false;

  return Array.from(value.metrics).every(metric => {
    if (!isRecord(metric) || !isString(metric.id) || !isOneOf(metric.unit, metricUnits) || !isOneOf(metric.scope, metricScopes)
      || !isString(metric.source) || !isRecord(metric.reading)) return false;
    return metricReading(metric.reading);
  }) && Array.from(value.availableActions).every(action => isRecord(action) && isString(action.id) && typeof action.enabled === 'boolean'
    && (action.reasonCode === null || isString(action.reasonCode)) && isString(action.messageKey));
}

function isReply(value: unknown, validate: (data: unknown, revision: number) => boolean): value is Reply & (
  | { ok: true; data: AppSnapshot; error: null }
  | { ok: false; data: null; error: AppError }
) {
  if (!isRecord(value) || !isString(value.requestId) || typeof value.ok !== 'boolean' || !isSafeRevision(value.revision)) return false;
  if (value.ok) return value.error === null && validate(value.data, value.revision);
  const error = value.error;
  if (isRecord(error) && error.parameters !== undefined && (!isRecord(error.parameters)
    || !Object.values(error.parameters).every(p => typeof p === 'string' || isFiniteNumber(p)))) return false;
  return value.data === null && isRecord(error) && isString(error.code) && isString(error.messageKey)
    && isOneOf(error.severity, severities) && isOneOf(error.retry, retryOptions)
    && Array.isArray(error.recoveryActionIds) && Array.from(error.recoveryActionIds).every(isString) && isString(error.correlationId);
}

function errorKey(error: Reply['error']): string {
  return error && Object.hasOwn(en, error.messageKey) && error.messageKey.startsWith('errors.') ? error.messageKey : 'errors.unknown';
}

export function sendCommand(command: Command | Extract<ProductCommand, { command: 'Connect' | 'Disconnect' | 'BuildPlan' | 'AcceptPlan' | 'CancelOperation' | 'ExportReport' | 'RequestConfirmation' | 'KeepChanges' }>): Promise<AppSnapshot> {
  return sendRequest(command, isSnapshot);
}
export function isHistoryPage(value: unknown): value is HistoryPage {
  return isRecord(value) && Array.isArray(value.items) && value.items.length <= 100
    && (value.nextCursor === null || typeof value.nextCursor === 'string' && value.nextCursor.length <= 256)
    && value.items.every(item => isRecord(item) && uuid(item.cycleId) && timestamp(item.createdUtc)
      && isString(item.endpoint) && isString(item.qbittorrentVersion) && isOneOf(item.phase, phases)
      && isOneOf(item.applyStatus, applyStatuses) && Number.isSafeInteger(item.changeCount) && (item.changeCount as number) >= 0);
}
export const listHistory = (cursor: string | null = null): Promise<HistoryPage> => sendRequest({ command: 'ListHistory', payload: { cursor, pageSize: 50 } }, isHistoryPage);
export const readCycle = (cycleId: string): Promise<ExperimentSummary> => sendRequest({ command: 'ReadCycle', payload: { cycleId } }, experiment);
export const selectNativeFile = (purpose: NativeSelection['purpose']): Promise<NativeSelection | null> =>
  sendRequest({ command: 'SelectNativeFile', payload: { purpose } }, (value): value is NativeSelection | null => value === null
    || isRecord(value) && value.purpose === purpose && typeof value.token === 'string' && /^[A-F0-9]{64}$/.test(value.token)
      && typeof value.displayName === 'string' && value.displayName.length <= 256 && timestamp(value.expiresUtc));
export const sendOperation = (command: Extract<ProductCommand, { command: 'StartMeasurement' | 'ApplyPlan' | 'Rollback' | 'RestoreLegacyBackup' | 'PrepareWorkload' | 'DetectLocalHardware' }>): Promise<AcceptedOperation> =>
  sendRequest(command, (value, replyRevision): value is AcceptedOperation => acceptedOperation(value) && value.revision === replyRevision);

function sendRequest<T>(command: Command | ProductCommand, validate: (data: unknown, revision: number) => data is T): Promise<T> {
  const webview = window.chrome?.webview;
  if (!webview) return Promise.reject(new Error('errors.hostUnavailable'));
  if (!listening) {
    webview.addEventListener('message', event => {
      const value: unknown = event.data;
      if (!isRecord(value)) return;
      if (typeof value.requestId !== 'string') {
        if (!initialized || value.sessionId !== sessionId || !(value.operationId === null || uuid(value.operationId))
          || !isSafeRevision(value.sequence) || value.sequence <= sequence || !isSafeRevision(value.revision)
          || revision === null || value.revision < revision || !isSnapshot(value.snapshot, value.revision)
          || (value.snapshot.target?.sessionId ?? null) !== sessionId) return;
        sequence = value.sequence; revision = value.revision;
        for (const listener of subscribers) listener(value.snapshot);
        return;
      }
      const item = pending.get(value.requestId);
      if (!item) return;
      pending.delete(value.requestId); clearTimeout(item.timer);
      if (!isReply(value, item.validate)) {
        item.reject(new Error('errors.invalidCommand')); return;
      }
      const reply = value;
      if (!reply.ok) {
        item.reject(new Error(errorKey(reply.error))); return;
      }
      const snapshotReply = isSnapshot(reply.data, reply.revision);
      if (snapshotReply && revision !== null && reply.revision < revision) { item.reject(new Error('errors.planStale')); return; }
      revision = Math.max(revision ?? 0, reply.revision);
      if (snapshotReply) {
        const newSession = reply.data.target?.sessionId ?? null;
        if (newSession !== sessionId) sequence = -1;
        sessionId = newSession; initialized = true;
      }
      item.resolve(reply.data);
    });
    listening = true;
  }
  if (pending.size >= 16) return Promise.reject(new Error('errors.operationConflict'));
  const requestId = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    const timeout = command.command === 'SelectNativeFile' ? 600000
      : command.command === 'Connect' || command.command === 'BuildPlan' || command.command === 'StartMeasurement' ? 45000 : 15000;
    const timer = setTimeout(() => { pending.delete(requestId); reject(new Error('errors.timeout')); }, timeout);
    pending.set(requestId, { resolve: data => resolve(data as T), validate, reject, timer });
    try { webview.postMessage({ ...command, protocolVersion: 1, requestId, targetSessionId: sessionId, expectedRevision: revision }); }
    catch { clearTimeout(timer); pending.delete(requestId); reject(new Error('errors.hostUnavailable')); }
  });
}
