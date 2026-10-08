import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canRestoreLegacyBackup, getRestoreConfirmation, LegacyRestorePanel } from '../src/components/LegacyRestorePanel';
import { enRestoreMessages, ruRestoreMessages, translateRestore } from '../src/components/restoreMessages';

const confirmation = (overrides: Partial<NonNullable<AppSnapshot['confirmations']>[number]> = {}) => ({
  token: 'secret-confirm-token', targetSessionId: 'session-1', revision: 7, expiresUtc: '2030-01-01T00:00:00Z',
  actionId: 'RestoreLegacyBackup', description: { key: 'confirmations.restoreLegacyBackup', parameters: {} }, ...overrides
});
const restore = (overrides: Partial<NonNullable<AppSnapshot['restore']>> = {}): NonNullable<AppSnapshot['restore']> => ({
  selectionToken: 'secret-selection-token', displayName: 'backup.json', sourceCycleId: 'cycle-legacy-1', review: {
    isLegacy: true, targetMatches: true, canRestore: true, alreadyOriginal: false, fingerprint: 'private-fingerprint', blockReasonCodes: [],
    differences: [{ key: 'disk_cache', original: 64, intended: 128, current: 128, disposition: 'restoreRequired' }]
  }, ...overrides
});
const makeSnapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 7, preferences: { locale: 'en-US', theme: 'system' },
  connection: 'validated', target: { sessionId: 'session-1', endpoint: 'http://localhost:8080', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0.0', isLocal: true },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [], availableActions: [],
  confirmations: [confirmation()], restore: restore(), ...overrides
});
const render = (snapshot: AppSnapshot) => renderToStaticMarkup(createElement(LegacyRestorePanel, {
  snapshot, onReview: async () => {}, onRestore: async () => {}
}));

describe('LegacyRestorePanel', () => {
  it('shows a read-only value diff, while keeping tokens and fingerprints out of markup', () => {
    const html = render(makeSnapshot());
    expect(html).toContain('backup.json');
    expect(html).toContain('cycle-legacy-1');
    expect(html).toContain('disk_cache');
    expect(html).toContain('Disk cache');
    expect(html).toContain('128');
    expect(html).toContain('64');
    expect(html).toContain('Restore original settings');
    expect(html).not.toContain('secret-confirm-token');
    expect(html).not.toContain('secret-selection-token');
    expect(html).not.toContain('private-fingerprint');
  });

  it('explains already-original and blocked reviews with localized reasons, not raw reason codes', () => {
    const original = makeSnapshot({ restore: restore({ review: { ...restore().review, canRestore: false, alreadyOriginal: true,
      blockReasonCodes: ['TARGET_MISMATCH'], differences: [{ ...restore().review.differences[0]!, disposition: 'alreadyOriginal' }] } }) });
    const html = render({ ...original, preferences: { locale: 'ru-RU', theme: 'system' } });
    expect(html).toContain('Текущие значения уже совпадают с исходными');
    expect(html).toContain('Эта копия относится к другому клиенту qBittorrent.');
    expect(html).not.toContain('TARGET_MISMATCH');
    expect(html).toMatch(/<button type="button" disabled="">Восстановить исходные настройки…<\/button>/);
  });

  it('fails closed for conflicts, mismatched targets, blocked reviews, existing originals, or active operations', () => {
    const cases: AppSnapshot[] = [
      makeSnapshot({ restore: restore({ review: { ...restore().review, differences: [{ ...restore().review.differences[0]!, disposition: 'conflict' }] } }) }),
      makeSnapshot({ restore: restore({ review: { ...restore().review, targetMatches: false } }) }),
      makeSnapshot({ restore: restore({ review: { ...restore().review, canRestore: false } }) }),
      makeSnapshot({ restore: restore({ review: { ...restore().review, alreadyOriginal: true } }) }),
      makeSnapshot({ activeOperation: { id: 'op', kind: 'restore', stage: 'writing', progress: null, cancellable: false } })
    ];
    for (const value of cases) {
      expect(canRestoreLegacyBackup(value)).toBe(false);
      expect(render(value)).toContain('disabled');
    }
  });

  it('requires a unique, unexpired confirmation for the exact target session and revision', () => {
    const base = makeSnapshot();
    expect(getRestoreConfirmation(base, Date.parse('2029-12-31T23:59:59Z'))?.token).toBe('secret-confirm-token');
    expect(getRestoreConfirmation(base, Date.parse('2030-01-01T00:00:00Z'))).toBeNull();
    expect(getRestoreConfirmation(makeSnapshot({ confirmations: [confirmation({ revision: 6 })] }))).toBeNull();
    expect(getRestoreConfirmation(makeSnapshot({ confirmations: [confirmation({ targetSessionId: 'other' })] }))).toBeNull();
    expect(getRestoreConfirmation(makeSnapshot({ confirmations: [confirmation(), confirmation()] }))).toBeNull();
    expect(getRestoreConfirmation(makeSnapshot({ connection: 'degraded' }))).toBeNull();
  });

  it('renders hostile backup labels and setting names as text without exposing a path or invoking mutations', () => {
    const onRestore = vi.fn(async () => {});
    const hostile = makeSnapshot({ restore: restore({ displayName: '<img src=x onerror=alert(1)>',
      review: { ...restore().review, differences: [{ ...restore().review.differences[0]!, key: '<script>bad</script>' }] } }) });
    const html = renderToStaticMarkup(createElement(LegacyRestorePanel, { snapshot: hostile, onReview: async () => {}, onRestore }));
    expect(html).toContain('&lt;img');
    expect(html).toContain('&lt;script&gt;');
    expect(html).not.toContain('<script>bad</script>');
    expect(onRestore).not.toHaveBeenCalled();
  });

  it('keeps translation keys and placeholders aligned and localizes unknown keys', () => {
    expect(Object.keys(ruRestoreMessages).sort()).toEqual(Object.keys(enRestoreMessages).sort());
    const placeholders = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enRestoreMessages) as (keyof typeof enRestoreMessages)[])
      expect(placeholders(ruRestoreMessages[key])).toEqual(placeholders(enRestoreMessages[key]));
    expect(translateRestore('ru-RU', 'restore.reason.futureCode')).toBe(ruRestoreMessages['restore.unknown']);
  });
});
