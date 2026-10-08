import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { ConfirmationSummary, NetworkTestResult } from '../src/contracts/domain';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canRunNetworkTest, canUseNetworkResult, isCurrentNetworkConfirmation, isMeasuredNetworkSpeed, NetworkTestPanel } from '../src/components/NetworkTestPanel';
import { enNetworkMessages, ruNetworkMessages, translateNetwork } from '../src/components/networkMessages';

const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 12,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session-1', endpoint: 'http://localhost:8080/', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: false },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [], availableActions: [], ...overrides,
});
const confirmation = (overrides: Partial<ConfirmationSummary> = {}): ConfirmationSummary => ({
  token: 'one-use-token', targetSessionId: 'session-1', revision: 12,
  expiresUtc: '2026-10-08T12:01:00Z', actionId: 'RunNetworkTest',
  description: { key: 'confirmation.networkTest', parameters: {} }, ...overrides,
});
const result = (overrides: Partial<NetworkTestResult> = {}): NetworkTestResult => ({
  downloadBytesPerSecond: 1234567.123456789, uploadBytesPerSecond: null,
  measuredUtc: '2026-10-08T12:00:00Z', reasonCodes: ['NETWORK_TIMEOUT'], ...overrides,
});
const render = (value: AppSnapshot) => renderToStaticMarkup(createElement(NetworkTestPanel, {
  snapshot: value, onReview: async () => confirmation(), onRun: async () => {}, onUse: () => {},
}));

describe('NetworkTestPanel', () => {
  it('discloses local scope, public providers, traffic budget and requests without starting a test', () => {
    let calls = 0;
    const html = renderToStaticMarkup(createElement(NetworkTestPanel, {
      snapshot: snapshot(), onReview: async () => { calls++; return confirmation(); },
      onRun: async () => { calls++; }, onUse: () => { calls++; },
    }));
    expect(html).toContain('this Windows computer');
    expect(html).toContain('remote qBittorrent server');
    expect(html).toContain('Cloudflare, Microsoft Azure and Google Cloud');
    expect(html).toContain('220 MiB');
    expect(html).toContain('17 requests');
    expect(html).toContain('Protocol overhead is additional');
    expect(calls).toBe(0);
  });

  it('blocks disconnected/degraded/busy/recovery targets and explicit disabled backend actions', () => {
    expect(canRunNetworkTest(snapshot())).toBe(true);
    for (const value of [snapshot({ target: null }), snapshot({ connection: 'disconnected' }),
      snapshot({ connection: 'degraded' }), snapshot({ phase: 'recoveryRequired' }),
      snapshot({ applyStatus: 'pending' }), snapshot({ applyStatus: 'unverified' }), snapshot({ applyStatus: 'verified' }),
      snapshot({ activeOperation: { id: 'op', kind: 'measurement', stage: 'sampling', progress: null, cancellable: true } }),
      snapshot({ availableActions: [{ id: 'RunNetworkTest', enabled: false, reasonCode: 'busy', messageKey: 'busy' }] })]) {
      expect(canRunNetworkTest(value)).toBe(false);
      expect(render(value)).toContain('disabled=""');
    }
  });

  it('requires exact action, session, revision, nonempty token and valid unexpired consent', () => {
    const now = Date.parse('2026-10-08T12:00:30Z');
    expect(isCurrentNetworkConfirmation(confirmation(), snapshot(), now)).toBe(true);
    for (const changes of [{ actionId: 'PrepareWorkload' }, { targetSessionId: 'other' }, { revision: 11 },
      { token: ' ' }, { expiresUtc: 'bad-date' }, { expiresUtc: '2026-10-08T12:00:30Z' }])
      expect(isCurrentNetworkConfirmation(confirmation(changes), snapshot(), now)).toBe(false);
    expect(isCurrentNetworkConfirmation(confirmation(), snapshot({ phase: 'recoveryRequired' }), now)).toBe(false);
  });

  it('keeps missing, failed and invalid speeds unknown, with useful partial-result reasons', () => {
    const html = render(snapshot({ networkTest: result() }));
    expect(html).toContain('9.877 Mbps');
    expect(html).toContain('Not measured');
    expect(html).toContain('A test server request timed out');
    expect(html).not.toContain('0 Mbps');
    expect(canUseNetworkResult(snapshot({ networkTest: result() }))).toBe(true);
    expect(canUseNetworkResult(snapshot({ networkTest: result({ downloadBytesPerSecond: null, uploadBytesPerSecond: 45.123456789 }) }))).toBe(true);
    for (const value of [null, undefined, 0, -1, NaN, Infinity]) expect(isMeasuredNetworkSpeed(value)).toBe(false);
    expect(isMeasuredNetworkSpeed(0.0001)).toBe(true);
    expect(canUseNetworkResult(snapshot({ networkTest: result({ downloadBytesPerSecond: null }) }))).toBe(false);
    expect(canUseNetworkResult(snapshot({ networkTest: result({ measuredUtc: 'invalid' }) }))).toBe(false);
    expect(canUseNetworkResult(snapshot({ phase: 'recoveryRequired', networkTest: result() }))).toBe(false);
  });

  it('keeps RU/EN keys and placeholders aligned and uses a useful unknown-reason fallback', () => {
    expect(Object.keys(ruNetworkMessages).sort()).toEqual(Object.keys(enNetworkMessages).sort());
    const placeholders = (text: string) => [...text.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enNetworkMessages) as (keyof typeof enNetworkMessages)[])
      expect(placeholders(ruNetworkMessages[key])).toEqual(placeholders(enNetworkMessages[key]));
    expect(translateNetwork('en-US', 'network.reason.unknownBackendCode')).toContain('Check the connection');
    const html = render(snapshot({ preferences: { locale: 'ru-RU', theme: 'dark' }, networkTest: result() }));
    expect(html).toContain('Не измерено');
    expect(html).toContain('220 MiB');
    expect(html).toContain('до 17 запросов');
  });
});
