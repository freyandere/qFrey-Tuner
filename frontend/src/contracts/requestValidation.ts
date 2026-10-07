import type { Request } from './protocol';
import { finite, inputs, integer, oneOf, record, revision, text, uuid } from './validation';

const exact = (value: Record<string, unknown>, keys: readonly string[]): boolean => Object.keys(value).length === keys.length && keys.every(k => Object.hasOwn(value, k));
const int32 = (value: unknown): boolean => integer(value) && value >= -2147483648 && value <= 2147483647;
const token = (value: unknown): boolean => text(value) && value.trim().length > 0;
const preference = (value: unknown): boolean => typeof value === 'boolean' || typeof value === 'string' || int32(value);

export function isRequest(value: unknown): value is Request {
  if (!record(value) || !exact(value, ['protocolVersion', 'requestId', 'command', 'targetSessionId', 'expectedRevision', 'payload'])
    || value.protocolVersion !== 1 || !uuid(value.requestId) || !(value.targetSessionId === null || uuid(value.targetSessionId))
    || !(value.expectedRevision === null || revision(value.expectedRevision)) || !record(value.payload)) return false;
  const p = value.payload;
  if (!oneOf(value.command, ['Initialize', 'SetUiPreferences', 'Connect', 'ListHistory', 'ReadCycle', 'ExportReport', 'SelectNativeFile'])
    && (!uuid(value.targetSessionId) || !revision(value.expectedRevision))) return false;
  switch (value.command) {
    case 'Initialize': return exact(p, ['protocolVersion']) && p.protocolVersion === 1;
    case 'SetUiPreferences': return exact(p, ['locale', 'theme']) && oneOf(p.locale, ['ru-RU', 'en-US']) && oneOf(p.theme, ['system', 'dark', 'light']);
    case 'Connect': {
      if (!exact(p, ['endpoint', 'auth']) || !text(p.endpoint) || !record(p.auth)) return false;
      try { const url = new URL(p.endpoint); if (!oneOf(url.protocol, ['http:', 'https:']) || url.username || url.password || url.search || url.hash) return false; }
      catch { return false; }
      const a = p.auth;
      return a.kind === 'bypass' && exact(a, ['kind'])
        || a.kind === 'password' && exact(a, ['kind', 'username', 'password']) && token(a.username) && text(a.password)
        || a.kind === 'apiKey' && exact(a, ['kind', 'apiKey']) && token(a.apiKey);
    }
    case 'Disconnect': return exact(p, []);
    case 'DetectLocalHardware': return exact(p, ['volumeToken']) && (p.volumeToken === null || typeof p.volumeToken === 'string');
    case 'RunNetworkTest':
    case 'StartTarget':
    case 'StopTarget':
    case 'RestartTarget': return exact(p, ['confirmationToken']) && token(p.confirmationToken);
    case 'BuildPlan': {
      if (!exact(p, ['inputs', 'selections']) || !inputs(p.inputs) || !record(p.inputs) || !exact(p.inputs, ['network', 'hardware', 'usage', 'proposedPort'])
        || !record(p.inputs.network) || !exact(p.inputs.network, ['downloadMbps', 'uploadMbps', 'connectionType', 'useVpn', 'vpnInterface', 'ispThrottling', 'downloadSource', 'uploadSource'])
        || !record(p.inputs.hardware) || !exact(p.inputs.hardware, ['storageType', 'ramGiB', 'cpuCores', 'isHybridCpu', 'performanceCores', 'source'])
        || !record(p.inputs.usage) || !exact(p.inputs.usage, ['trackerType', 'userRole', 'environment'])
        || !Array.isArray(p.selections) || p.selections.length > 128) return false;
      const ids = new Set<string>();
      return p.selections.every(s => {
        if (!record(s) || !exact(s, ['groupId', 'selected', 'overrides']) || !token(s.groupId) || typeof s.selected !== 'boolean'
          || !record(s.overrides) || Object.keys(s.overrides).length > 64 || !Object.values(s.overrides).every(preference) || ids.has(s.groupId as string)) return false;
        ids.add(s.groupId as string); return true;
      });
    }
    case 'AcceptPlan': return exact(p, ['planId', 'revision']) && uuid(p.planId) && revision(p.revision);
    case 'StartMeasurement': {
      if (!exact(p, ['kind', 'workload']) || !oneOf(p.kind, ['baseline', 'after']) || !record(p.workload)) return false;
      const w = p.workload;
      return exact(w, ['id', 'kind', 'hashes']) && uuid(w.id) && oneOf(w.kind, ['existing', 'owned']) && Array.isArray(w.hashes)
        && w.hashes.length > 0 && w.hashes.length <= 5000 && w.hashes.every(h => typeof h === 'string' && /^[a-f0-9]{40}$/i.test(h))
        && new Set(w.hashes.map(h => (h as string).toLowerCase())).size === w.hashes.length;
    }
    case 'CancelOperation': return exact(p, ['operationId']) && uuid(p.operationId);
    case 'PrepareWorkload': return exact(p, ['catalogueId', 'serverSavePath', 'approvalToken']) && token(p.catalogueId) && token(p.serverSavePath) && token(p.approvalToken);
    case 'StopOwnedWorkload': return exact(p, ['workloadId', 'confirmationToken']) && uuid(p.workloadId) && token(p.confirmationToken);
    case 'DeleteOwnedWorkload': return exact(p, ['workloadId', 'deleteFiles', 'confirmationToken']) && uuid(p.workloadId) && typeof p.deleteFiles === 'boolean' && token(p.confirmationToken);
    case 'ApplyPlan': return exact(p, ['planId', 'expectedRevision', 'confirmationToken']) && uuid(p.planId) && revision(p.expectedRevision) && token(p.confirmationToken);
    case 'Rollback': return exact(p, ['cycleId', 'expectedRevision', 'confirmationToken']) && uuid(p.cycleId) && revision(p.expectedRevision) && token(p.confirmationToken);
    case 'RestoreLegacyBackup': return exact(p, ['selectionToken', 'confirmationToken']) && token(p.selectionToken) && token(p.confirmationToken);
    case 'KeepChanges': return exact(p, ['cycleId', 'applyStatus']) && uuid(p.cycleId) && p.applyStatus === 'verified';
    case 'ListHistory': return exact(p, ['cursor', 'pageSize']) && (p.cursor === null || typeof p.cursor === 'string' && p.cursor.length <= 256) && integer(p.pageSize) && p.pageSize >= 1 && p.pageSize <= 100;
    case 'ReadCycle': return exact(p, ['cycleId']) && uuid(p.cycleId);
    case 'ExportReport': return exact(p, ['cycleId', 'format', 'destinationToken', 'locale']) && uuid(p.cycleId) && oneOf(p.format, ['json', 'html']) && token(p.destinationToken) && oneOf(p.locale, ['ru-RU', 'en-US']);
    case 'RequestConfirmation': return ['actionId', 'planId', 'cycleId', 'workloadId'].every(k => Object.hasOwn(p, k))
      && Object.keys(p).every(k => ['actionId', 'planId', 'cycleId', 'workloadId', 'catalogueId', 'serverSavePath', 'selectionToken'].includes(k))
      && oneOf(p.actionId, ['ApplyPlan', 'Rollback', 'RestoreLegacyBackup', 'RunNetworkTest', 'PrepareWorkload', 'StopOwnedWorkload', 'DeleteOwnedWorkload', 'StartTarget', 'StopTarget', 'RestartTarget'])
      && [p.planId, p.cycleId, p.workloadId].every(id => id === null || uuid(id))
      && (p.actionId === 'PrepareWorkload'
        ? token(p.catalogueId) && (p.catalogueId as string).length <= 64 && token(p.serverSavePath)
          && (p.serverSavePath as string).length <= 2048 && !/[\u0000-\u001f\u007f]/.test(p.serverSavePath as string)
          && p.planId === null && p.cycleId === null && p.workloadId === null
        : p.catalogueId == null && p.serverSavePath == null)
      && (p.actionId === 'RestoreLegacyBackup'
        ? token(p.selectionToken) && (p.selectionToken as string).length <= 128
          && p.planId === null && p.cycleId === null && p.workloadId === null
        : p.selectionToken == null);
    case 'SelectNativeFile': return exact(p, ['purpose']) && oneOf(p.purpose, ['restore', 'exportJson', 'exportHtml', 'volume']);
    default: return false;
  }
}

export function parseRequest(json: string): Request {
  if (new TextEncoder().encode(json).length > 64 * 1024) throw new Error('COMMAND_TOO_LARGE');
  // JSON.parse's reviver checks finite wire numbers and duplicate keys are rejected by the authoritative C# boundary.
  const value: unknown = JSON.parse(json, (_key, v: unknown) => { if (typeof v === 'number' && !finite(v)) throw new Error('UNSAFE_NUMBER'); return v; });
  const depth = (v: unknown, level = 0): void => {
    if (v !== null && typeof v === 'object') {
      if (level >= 16) throw new Error('COMMAND_TOO_DEEP');
      for (const child of Object.values(v)) depth(child, level + 1);
    }
  };
  depth(value);
  if (!isRequest(value)) throw new Error('INVALID_COMMAND');
  return value;
}
