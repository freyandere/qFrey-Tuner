import { afterEach, expect, test, vi } from 'vitest';
import initialize from '../../tests-contract/fixtures/protocol/initialize-success.json';
import type { Request } from '../src/contracts/protocol';
afterEach(() => { vi.useRealTimers(); delete (globalThis as { window?: Window }).window; });
test('snapshot subscription ignores pre-initialize, foreign session, stale sequence and stale revision', async () => {
  vi.resetModules();
  let receive: (event: MessageEvent<unknown>) => void = () => {};
  let sent: Request | undefined;
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview: {
    addEventListener: (_: string, handler: typeof receive) => { receive = handler; },
    postMessage: (request: Request) => { sent = request; },
  } } } });
  const { sendCommand, subscribeSnapshots } = await import('../src/bridge/client');
  const updates = vi.fn();
  const unsubscribe = subscribeSnapshots(updates);
  const pending = sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } });
  const event = { sessionId: null, operationId: null, sequence: 1, revision: 1, snapshot: { ...initialize.data, revision: 1 } };
  receive({ data: event } as MessageEvent<unknown>);
  expect(updates).not.toHaveBeenCalled();
  receive({ data: { ...initialize, requestId: sent!.requestId } } as MessageEvent<unknown>);
  await pending;
  receive({ data: { ...event, sessionId: '00000000-0000-0000-0000-000000000201' } } as MessageEvent<unknown>);
  expect(updates).not.toHaveBeenCalled();
  receive({ data: event } as MessageEvent<unknown>);
  expect(updates).toHaveBeenCalledTimes(1);
  receive({ data: event } as MessageEvent<unknown>);
  receive({ data: { ...event, sequence: 2, revision: 0, snapshot: initialize.data } } as MessageEvent<unknown>);
  expect(updates).toHaveBeenCalledTimes(1);
  unsubscribe();
  receive({ data: { ...event, sequence: 2 } } as MessageEvent<unknown>);
  expect(updates).toHaveBeenCalledTimes(1);
});
test('a failing post releases its pending slot and timeout', async () => {
  vi.resetModules(); vi.useFakeTimers();
  Object.defineProperty(globalThis, 'window', { configurable: true, value: { chrome: { webview: {
    addEventListener: () => {}, postMessage: () => { throw new Error('transport down'); },
  } } } });
  const { sendCommand } = await import('../src/bridge/client');
  for (let i = 0; i < 20; i++) await expect(sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } })).rejects.toThrow('errors.hostUnavailable');
  expect(vi.getTimerCount()).toBe(0);
});
