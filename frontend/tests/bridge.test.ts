import { afterEach, expect, test, vi } from 'vitest';
import initializeReply from '../../tests-contract/fixtures/protocol/initialize-success.json';
import wrongProtocolReply from '../../tests-contract/fixtures/protocol/initialize-wrong-protocol.json';
import missingFieldReply from '../../tests-contract/fixtures/protocol/initialize-missing-field.json';
import type { AppSnapshot, Command, Request } from '../src/contracts/protocol';

type MessageHandler = (event: MessageEvent<unknown>) => void;
function host() {
  let handler: MessageHandler | undefined;
  const sent: Request[] = [];
  const webview = {
    postMessage: (message: Request) => sent.push(message),
    addEventListener: (_type: 'message', listener: MessageHandler) => { handler = listener; },
  };
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview } } });
  return {
    sent,
    reply: (value: unknown) => handler?.({ data: value } as MessageEvent<unknown>),
  };
}
async function bridge() {
  vi.resetModules();
  return import('../src/bridge/client');
}
const initialize: Command = { command: 'Initialize', payload: { protocolVersion: 1 } };

test('only the accepted lifecycle operation can clear the active target through an event', async () => {
  const h = host(); const client = await bridge();
  const sessionId = '11111111-1111-1111-1111-111111111111';
  const operationId = '22222222-2222-2222-2222-222222222222';
  const snapshot: AppSnapshot = { ...initializeReply.data as AppSnapshot, revision: 1, connection: 'validated',
    target: { sessionId, endpoint: 'http://localhost:8080/', qbittorrentVersion: '5.1.4', apiVersion: '2.11.4', libtorrentVersion: '2.0.11', isLocal: true } };
  const initialized = client.sendCommand(initialize);
  h.reply({ requestId: h.sent[0].requestId, ok: true, revision: 1, data: snapshot, error: null }); await initialized;
  const listener = vi.fn(); client.subscribeSnapshots(listener);
  const stopped = client.sendOperation({ command: 'StopTarget', payload: { confirmationToken: 'confirmed' } });
  h.reply({ requestId: h.sent[1].requestId, ok: true, revision: 2, data: { operationId, revision: 2 }, error: null }); await stopped;
  const terminal: AppSnapshot = { ...snapshot, revision: 3, connection: 'disconnected', target: null, activeOperation: null };
  const event = { sessionId, operationId, revision: 3, sequence: 1, snapshot: terminal };
  h.reply({ ...event, operationId: '33333333-3333-3333-3333-333333333333' });
  h.reply({ ...event, sessionId: '33333333-3333-3333-3333-333333333333' });
  h.reply({ ...event, snapshot: { ...terminal, activeOperation: { id: operationId, kind: 'stopTarget', stage: 'starting', progress: null, cancellable: false } } });
  expect(listener).not.toHaveBeenCalled();
  h.reply(event);
  expect(listener).toHaveBeenCalledTimes(1);
  expect(listener).toHaveBeenCalledWith(terminal);
  h.reply(event); expect(listener).toHaveBeenCalledTimes(1);
  const reconnect = client.sendCommand({ command: 'Connect', payload: { endpoint: 'http://localhost:8080/', auth: { kind: 'bypass' } } });
  expect(h.sent[2].targetSessionId).toBeNull();
  expect(h.sent[2].expectedRevision).toBe(3);
  h.reply({ requestId: h.sent[2].requestId, ok: true, revision: 4, data: { ...snapshot, revision: 4 }, error: null }); await reconnect;
});

test('history responses use their own validator and preserve the active target revision', async () => {
  const h = host(); const client = await bridge();
  const initialized = client.sendCommand(initialize);
  h.reply({ ...initializeReply, requestId: h.sent[0].requestId }); await initialized;
  const page = client.listHistory();
  h.reply({ requestId: h.sent[1].requestId, ok: true, revision: initializeReply.revision, data: { items: [], nextCursor: null }, error: null });
  await expect(page).resolves.toEqual({ items: [], nextCursor: null });
  const malformed = client.listHistory();
  h.reply({ requestId: h.sent[2].requestId, ok: true, revision: initializeReply.revision, data: initializeReply.data, error: null });
  await expect(malformed).rejects.toThrow('errors.invalidCommand');
  const reload = client.sendCommand(initialize);
  h.reply({ requestId: h.sent[3].requestId, ok: true, revision: initializeReply.revision, data: { items: [], nextCursor: null }, error: null });
  await expect(reload).rejects.toThrow('errors.invalidCommand');
});
test('read-only history survives a newer preferences reply without rolling back revision', async () => {
  const h = host(); const client = await bridge();
  const initialized = client.sendCommand(initialize);
  h.reply({ ...initializeReply, requestId: h.sent[0].requestId }); await initialized;
  const page = client.listHistory();
  const preferences = client.sendCommand({ command: 'SetUiPreferences', payload: { locale: 'en-US', theme: 'light' } });
  const revision = initializeReply.revision + 1;
  h.reply({ ...initializeReply, requestId: h.sent[2].requestId, revision, data: { ...initializeReply.data, revision } }); await preferences;
  h.reply({ requestId: h.sent[1].requestId, ok: true, revision: initializeReply.revision, data: { items: [], nextCursor: null }, error: null });
  await expect(page).resolves.toEqual({ items: [], nextCursor: null });
  const reload = client.sendCommand(initialize);
  expect(h.sent[3].expectedRevision).toBe(revision);
  h.reply({ ...initializeReply, requestId: h.sent[3].requestId, revision, data: { ...initializeReply.data, revision } }); await reload;
});

afterEach(() => {
  vi.useRealTimers();
  delete (globalThis as { window?: Window }).window;
});

test('rejects when the desktop host is unavailable', async () => {
  Object.defineProperty(globalThis, 'window', { configurable: true, value: {} });
  const { sendCommand } = await bridge();
  await expect(sendCommand(initialize)).rejects.toThrow('errors.hostUnavailable');
});

test('sends Initialize and resolves its valid snapshot', async () => {
  const h = host();
  const { sendCommand } = await bridge();
  const result = sendCommand(initialize);
  expect(h.sent[0]).toMatchObject({ ...initialize, protocolVersion: 1, targetSessionId: null, expectedRevision: null });
  h.reply({ ...initializeReply, requestId: h.sent[0].requestId });
  await expect(result).resolves.toMatchObject(initializeReply.data as AppSnapshot);
});

test('rejects an out-of-order revision', async () => {
  const h = host();
  const { sendCommand } = await bridge();
  const first = sendCommand(initialize);
  h.reply({ ...initializeReply, requestId: h.sent[0].requestId, revision: 4, data: { ...initializeReply.data, revision: 4 } });
  await first;
  const second = sendCommand(initialize);
  h.reply({ ...initializeReply, requestId: h.sent[1].requestId, revision: 3, data: { ...initializeReply.data, revision: 3 } });
  await expect(second).rejects.toThrow('errors.planStale');
});

test('preserves backend error message keys', async () => {
  const h = host();
  const { sendCommand } = await bridge();
  const result = sendCommand(initialize);
  h.reply({ requestId: h.sent[0].requestId, ok: false, revision: 0, data: null, error: {
    code: 'PLAN_STALE', messageKey: 'errors.planStale', severity: 'warning', retry: 'safe', recoveryActionIds: [], correlationId: 'test',
  } });
  await expect(result).rejects.toThrow('errors.planStale');
});

test('uses a safe fallback for unknown backend error keys', async () => {
  const h = host();
  const { sendCommand } = await bridge();
  const result = sendCommand(initialize);
  h.reply({ requestId: h.sent[0].requestId, ok: false, revision: 0, data: null, error: {
    code: 'NEW_ERROR', messageKey: 'errors.notInThisBuild', severity: 'error', retry: 'none', recoveryActionIds: [], correlationId: 'test',
  } });
  await expect(result).rejects.toThrow('errors.unknown');
});

const invalidSnapshots: Array<[string, unknown]> = [
  ['wrong protocol', wrongProtocolReply.data],
  ['missing required field', missingFieldReply.data],
  ['malformed nested preferences', { ...initializeReply.data, preferences: { locale: 'fr-FR', theme: 'blue' } }],
  ['mismatched revision', { ...initializeReply.data, revision: 1 }],
];
test.each(invalidSnapshots)('rejects malformed snapshot: %s', async (_name, data) => {
  const h = host();
  const { sendCommand } = await bridge();
  const result = sendCommand(initialize);
  h.reply({ ...initializeReply, requestId: h.sent[0].requestId, data });
  await expect(result).rejects.toThrow('errors.invalidCommand');
});
