import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canOperateOwnedWorkload, isCurrentOwnedConfirmation, OwnedWorkloadControls, ownedMeasurementReference, type OwnedConfirmation } from '../src/components/OwnedWorkloadControls';
import { enOwnedActionMessages, ruOwnedActionMessages, translateOwnedAction } from '../src/components/ownedActionMessages';

const hash = 'A'.repeat(40);
const snapshot = (): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 3,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session', endpoint: 'http://localhost:8080', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: true },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [],
  availableActions: ['StartOwnedWorkload', 'StopOwnedWorkload'].map(id => ({ id, enabled: true, reasonCode: null, messageKey: '' })),
  experiment: { cycleId: null, runInputs: null, baseline: null, after: null, plan: null, results: [],
    workload: { reference: { id: 'owned', kind: 'owned', hashes: [hash] }, name: '<Test image>', totalBytesDecimal: '1234567890123456789',
      serverSavePath: '/test image', catalogueId: 'official', ownershipVerified: true, reasonCodes: [] } },
});
const now = Date.parse('2026-10-08T00:00:00Z');
const pending = (): OwnedConfirmation => ({
  action: 'StartOwnedWorkload', workload: { id: 'owned', kind: 'owned', hashes: [hash] },
  summary: { token: 'opaque', targetSessionId: 'session', revision: 3, expiresUtc: '2026-10-08T00:01:00Z',
    actionId: 'StartOwnedWorkload', description: { key: 'confirmation.workload.start', parameters: {} } },
});

describe('owned workload controls', () => {
  it('uses exact authoritative identity and hash without lowercasing or mutating it', () => {
    const value = snapshot();
    const reference = ownedMeasurementReference(value)!;
    expect(reference).toEqual(value.experiment!.workload!.reference);
    reference.hashes[0] = 'b'.repeat(40);
    expect(value.experiment!.workload!.reference.hashes).toEqual([hash]);
    for (const patch of [{ ownershipVerified: false }, { reference: { id: '', kind: 'owned' as const, hashes: [hash] } },
      { reference: { id: 'owned', kind: 'existing' as const, hashes: [hash] } },
      { reference: { id: 'owned', kind: 'owned' as const, hashes: [hash, 'b'.repeat(40)] } }]) {
      expect(ownedMeasurementReference({ ...value, experiment: { ...value.experiment!, workload: { ...value.experiment!.workload!, ...patch } } })).toBeNull();
    }
  });

  it('allows prepared stopped workloads only when the backend exposes the action and no conflicting operation exists', () => {
    const value = snapshot();
    expect(canOperateOwnedWorkload(value, 'StartOwnedWorkload')).toBe(true);
    expect(canOperateOwnedWorkload(value, 'StopOwnedWorkload')).toBe(true);
    for (const patch of [ { connection: 'degraded' as const }, { target: null }, { phase: 'recoveryRequired' as const },
      { applyStatus: 'unverified' as const }, { applyStatus: 'pending' as const }, { availableActions: [] },
      { availableActions: [{ id: 'StartOwnedWorkload', enabled: false, reasonCode: 'stopped', messageKey: '' }] },
      { activeOperation: { id: 'measurement', kind: 'measurement' as const, stage: 'sampling', progress: 25, cancellable: true } } ]) {
      expect(canOperateOwnedWorkload({ ...value, ...patch }, 'StartOwnedWorkload')).toBe(false);
    }
  });

  it('rejects stale, expired, mismatched or empty confirmations', () => {
    const value = snapshot();
    const review = pending();
    expect(isCurrentOwnedConfirmation(review, value, now)).toBe(true);
    for (const patch of [{ actionId: 'StopOwnedWorkload' }, { targetSessionId: 'other' }, { revision: 2 },
      { token: ' ' }, { expiresUtc: 'invalid' }, { expiresUtc: '2026-10-08T00:00:00Z' }]) {
      expect(isCurrentOwnedConfirmation({ ...review, summary: { ...review.summary, ...patch } }, value, now)).toBe(false);
    }
    expect(isCurrentOwnedConfirmation({ ...review, workload: { ...review.workload, id: 'other' } }, value, now)).toBe(false);
    expect(isCurrentOwnedConfirmation({ ...review, workload: { ...review.workload, hashes: ['a'.repeat(40)] } }, value, now)).toBe(false);
    expect(isCurrentOwnedConfirmation(review, { ...value, revision: 4 }, now)).toBe(false);
  });

  it('shows full size and remote path without delete or measurement cancellation actions', () => {
    const html = renderToStaticMarkup(createElement(OwnedWorkloadControls, {
      snapshot: snapshot(), onReview: async () => pending().summary, onAction: async () => {},
    }));
    expect(html).toContain('1234567890123456789');
    expect(html).toContain('/test image');
    expect(html).toContain('&lt;Test image&gt;');
    expect(html).toContain('Review download start');
    expect(html).toContain('Review stopping');
    expect(html).not.toContain('Delete');
    expect(html).not.toContain('Cancel measurement');
  });

  it('keeps localized consent and placeholders aligned', () => {
    expect(Object.keys(ruOwnedActionMessages).sort()).toEqual(Object.keys(enOwnedActionMessages).sort());
    for (const key of Object.keys(enOwnedActionMessages) as (keyof typeof enOwnedActionMessages)[]) {
      const placeholders = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
      expect(placeholders(ruOwnedActionMessages[key])).toEqual(placeholders(enOwnedActionMessages[key]));
    }
    expect(translateOwnedAction('ru-RU', 'path', { path: '/remote' })).toContain('/remote');
  });

  it('shows explicit choices during ambiguous recovery without exposing start or stop', () => {
    const value = snapshot();
    const first = value.experiment!.workload!;
    const second = { ...first, name: 'Second workload', reference: { ...first.reference, id: 'second' } };
    const html = renderToStaticMarkup(createElement(OwnedWorkloadControls, {
      snapshot: { ...value, phase: 'recoveryRequired', experiment: null, ownedWorkloadCandidates: [first, second] },
      onReview: async () => pending().summary, onAction: async () => {}, onSelect: async () => {},
    }));
    expect(html).toContain('Several verified managed workloads');
    expect(html).toContain('Second workload');
    expect(html.match(/Select this workload/g)).toHaveLength(2);
    expect(html).not.toContain('Review download start');
    expect(html).not.toContain('Review stopping');
    expect(html).not.toContain('disabled');
    const busy = renderToStaticMarkup(createElement(OwnedWorkloadControls, {
      snapshot: { ...value, ownedWorkloadCandidates: [first, second], applyStatus: 'verified' },
      onReview: async () => pending().summary, onAction: async () => {}, onSelect: async () => {},
    }));
    expect(busy.match(/disabled/g)).toHaveLength(2);
  });
});
