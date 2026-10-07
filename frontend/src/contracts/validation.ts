import type { Plan, ExperimentSummary, ResultCard, HistoricalValue, MeasurementSummary, AcceptedOperation } from './domain';

export const record = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v);
export const text = (v: unknown): v is string => typeof v === 'string' && v.length > 0;
export const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v) && Math.abs(v) <= Number.MAX_SAFE_INTEGER;
export const integer = (v: unknown): v is number => Number.isSafeInteger(v);
export const revision = (v: unknown): v is number => integer(v) && v >= 0;
export const oneOf = (v: unknown, values: readonly string[]): boolean => typeof v === 'string' && values.includes(v);
export const timestamp = (v: unknown): v is string => typeof v === 'string' && /^[0-9]{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(v)
  && !v.startsWith('0000') && Number.isFinite(Date.parse(v)) && new Date(v).toISOString().slice(0, 19) === v.slice(0, 19);
export const uuid = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v) && v !== '00000000-0000-0000-0000-000000000000';
const list = (v: unknown, check: (item: unknown) => boolean, cap = 600): boolean => Array.isArray(v) && v.length <= cap && v.every(check);
const strings = (v: unknown): boolean => list(v, text);
const nullable = (v: unknown, check: (item: unknown) => boolean): boolean => v === null || check(v);
const preference = (v: unknown): boolean => typeof v === 'boolean' || typeof v === 'string' || (integer(v) && v >= -2147483648 && v <= 2147483647);
const preferences = (v: unknown): boolean => record(v) && Object.keys(v).length <= 128 && Object.values(v).every(preference);
const parameters = (v: unknown): boolean => record(v) && Object.values(v).every(p => typeof p === 'string' || finite(p));
const message = (v: unknown): boolean => record(v) && text(v.key) && parameters(v.parameters);
const categories = ['throughput', 'stability', 'ramp', 'connectivity', 'resources', 'configuration', 'limitations'];
const evidences = ['observed', 'derived', 'heuristic', 'userProvided'];
const units = ['bytesPerSecond', 'bytes', 'milliseconds', 'count', 'percent'];
const scopes = ['session', 'workload', 'localProcess', 'localHost', 'endpoint'];
const applyStatuses = ['notApplied', 'pending', 'verified', 'unverified', 'reverted'];
const measurementStatuses = ['pending', 'warmingUp', 'sampling', 'valid', 'invalid', 'cancelled'];
const inputSources = ['manual', 'measured', 'localDetection'];

export function metricReading(v: unknown): boolean {
  if (!record(v)) return false;
  switch (v.status) {
    case 'fresh': return finite(v.value) && timestamp(v.sampledAtUtc);
    case 'stale': return finite(v.lastValue) && timestamp(v.sampledAtUtc) && text(v.reasonCode);
    case 'unavailable': case 'error': return text(v.reasonCode);
    default: return false;
  }
}
export function hardware(v: unknown): boolean {
  if (!record(v) || !record(v.facts) || !strings(v.reasonCodes)) return false;
  const f = v.facts;
  if (!metricReading(f.ramBytes) || !metricReading(f.logicalCpuCount) || !metricReading(f.performanceCpuCount)
    || !nullable(f.storageType, s => oneOf(s, ['hdd', 'ssdSata', 'nvme'])) || !nullable(f.storageReasonCode, text) || !nullable(f.volumeToken, text)) return false;
  return v.inputs === null || hardwareInputs(v.inputs);
}
function hardwareInputs(h: unknown): boolean {
  return record(h) && oneOf(h.storageType, ['hdd', 'ssdSata', 'nvme']) && integer(h.ramGiB) && h.ramGiB >= 1 && h.ramGiB <= 1048576
    && integer(h.cpuCores) && h.cpuCores >= 1 && h.cpuCores <= 4096 && typeof h.isHybridCpu === 'boolean'
    && integer(h.performanceCores) && h.performanceCores >= 0 && h.performanceCores <= h.cpuCores && oneOf(h.source, inputSources);
}
export function networkTest(v: unknown): boolean {
  return record(v) && finite(v.downloadBytesPerSecond) && v.downloadBytesPerSecond >= 0 && finite(v.uploadBytesPerSecond) && v.uploadBytesPerSecond >= 0
    && timestamp(v.measuredUtc) && strings(v.reasonCodes);
}

export function historical(v: unknown): v is HistoricalValue {
  return record(v) && (v.quality === 'valid' ? finite(v.value) && timestamp(v.measuredAtUtc)
    : oneOf(v.quality, ['invalid', 'notMeasured']) && strings(v.reasonCodes));
}
export function inputs(v: unknown): boolean {
  if (!record(v) || !record(v.network) || !record(v.hardware) || !record(v.usage)) return false;
  const n = v.network, h = v.hardware, u = v.usage;
  return finite(n.downloadMbps) && n.downloadMbps > 0 && n.downloadMbps <= 100000 && finite(n.uploadMbps) && n.uploadMbps > 0 && n.uploadMbps <= 100000
    && oneOf(n.connectionType, ['fiber', 'cableDsl', 'mobile4G']) && typeof n.useVpn === 'boolean' && typeof n.vpnInterface === 'string' && n.vpnInterface.length <= 256
    && typeof n.ispThrottling === 'boolean' && oneOf(n.downloadSource, inputSources) && oneOf(n.uploadSource, inputSources)
    && hardwareInputs(h)
    && oneOf(u.trackerType, ['public', 'private']) && oneOf(u.userRole, ['leecher', 'seeder', 'uploader'])
    && oneOf(u.environment, ['system', 'portable', 'truenas', 'nas', 'docker', 'seedbox'])
    && (v.proposedPort === null || integer(v.proposedPort) && v.proposedPort >= 49152 && v.proposedPort <= 65535);
}
function recommendation(v: unknown): boolean {
  if (!record(v)) return false;
  return text(v.id) && text(v.groupId) && text(v.apiKey) && oneOf(v.category, categories) && text(v.titleKey) && message(v.reason)
    && oneOf(v.evidence, evidences) && nullable(v.currentValue, preference) && preference(v.proposedValue) && text(v.unit)
    && oneOf(v.valueType, ['number', 'bool', 'enum', 'interface']) && typeof v.editable === 'boolean'
    && nullable(v.allowedRange, r => record(r) && finite(r.minimum) && finite(r.maximum) && r.minimum <= r.maximum && finite(r.step) && r.step > 0)
    && list(v.allowedValues, a => record(a) && preference(a.value) && text(a.labelKey))
    && oneOf(v.supportStatus, ['supported', 'missingOptional', 'missingRequired', 'incompatible']) && typeof v.selected === 'boolean' && strings(v.cautionCodes);
}
export function plan(v: unknown): v is Plan {
  return record(v) && uuid(v.id) && uuid(v.targetSessionId) && revision(v.revision) && timestamp(v.createdUtc)
    && text(v.inputsFingerprint) && typeof v.baselineFingerprint === 'string' && typeof v.previewOnly === 'boolean'
    && typeof v.applicable === 'boolean' && typeof v.approved === 'boolean' && strings(v.blockReasonCodes)
    && list(v.recommendations, recommendation, 128)
    && list(v.omissions, o => record(o) && text(o.apiKey) && typeof o.required === 'boolean' && text(o.reasonCode), 128)
    && preferences(v.original) && preferences(v.proposed);
}
function comparison(v: unknown): boolean {
  return record(v) && text(v.metricId) && oneOf(v.category, categories) && oneOf(v.unit, units) && historical(v.before) && historical(v.after)
    && nullable(v.absoluteDelta, finite) && nullable(v.relativeDeltaPercent, finite)
    && oneOf(v.verdict, ['observedImprovement', 'observedRegression', 'inconclusive', 'notComparable', 'notMeasured'])
    && strings(v.reasonCodes) && oneOf(v.evidence, evidences) && oneOf(v.scope, scopes);
}
export function resultCard(v: unknown): v is ResultCard {
  if (!record(v) || !record(v.content) || !text(v.id) || !oneOf(v.category, categories) || !text(v.titleKey) || !message(v.explanation)
    || !oneOf(v.evidence, evidences) || !oneOf(v.severity, ['neutral', 'info', 'warning', 'error'])
    || !oneOf(v.context, ['historical', 'currentExperiment']) || !uuid(v.cycleId) || !nullable(v.measuredAtUtc, timestamp)) return false;
  const c = v.content;
  switch (v.kind) {
    case 'metricComparison': return c.type === 'comparison' && comparison(c.comparison);
    case 'observation': return c.type === 'observation' && text(c.metricId) && historical(c.value) && oneOf(c.unit, units) && oneOf(c.scope, scopes);
    case 'appliedChange': return c.type === 'change' && text(c.apiKey) && preference(c.before) && preference(c.intended) && nullable(c.observed, preference) && oneOf(c.status, applyStatuses);
    case 'limitation': return c.type === 'limitation' && strings(c.reasonCodes);
    case 'recovery': return c.type === 'recovery' && strings(c.conflictKeys) && strings(c.actionIds) && typeof c.backupAvailable === 'boolean';
    default: return false;
  }
}
function workloadReference(v: unknown): boolean {
  return record(v) && uuid(v.id) && oneOf(v.kind, ['existing', 'owned']) && list(v.hashes, h => typeof h === 'string' && /^[a-f0-9]{40}$/i.test(h), 5000) && Array.isArray(v.hashes) && v.hashes.length > 0;
}
export function measurement(v: unknown): v is MeasurementSummary {
  return record(v) && uuid(v.id) && oneOf(v.kind, ['baseline', 'after']) && oneOf(v.status, measurementStatuses) && timestamp(v.startedUtc)
    && text(v.analysisVersion) && oneOf(v.scope, scopes) && strings(v.reasonCodes) && revision(v.sampleCount) && finite(v.durationMs) && v.durationMs >= 0
    && historical(v.meanDownload) && historical(v.medianDownload) && historical(v.standardDeviation) && historical(v.zeroSamplePercent);
}
export function acceptedOperation(v: unknown): v is AcceptedOperation {
  return record(v) && uuid(v.operationId) && revision(v.revision);
}
export function experiment(v: unknown): v is ExperimentSummary {
  return record(v) && nullable(v.cycleId, uuid) && nullable(v.runInputs, inputs)
    && nullable(v.workload, w => record(w) && workloadReference(w.reference) && text(w.name) && typeof w.totalBytesDecimal === 'string' && /^\d+$/.test(w.totalBytesDecimal)
      && text(w.serverSavePath) && nullable(w.catalogueId, text) && typeof w.ownershipVerified === 'boolean' && strings(w.reasonCodes))
    && nullable(v.baseline, measurement) && nullable(v.after, measurement) && nullable(v.plan, plan) && list(v.results, resultCard, 128);
}
export function confirmation(v: unknown): boolean {
  return record(v) && text(v.token) && uuid(v.targetSessionId) && revision(v.revision) && timestamp(v.expiresUtc) && text(v.actionId) && message(v.description);
}
