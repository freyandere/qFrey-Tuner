import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canOperateTarget, isCurrentLifecycleConfirmation, lifecycleSnapshotKey, TargetLifecyclePanel, type LifecycleConfirmation, type TargetLifecycleAction } from '../src/components/TargetLifecyclePanel';
import { enLifecycleMessages, ruLifecycleMessages, translateLifecycle } from '../src/components/lifecycleMessages';

const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 3,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session', endpoint: 'http://localhost:8080', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: true },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [],
  availableActions: ['StopTarget', 'RestartTarget'].map(id => ({ id, enabled: true, reasonCode: null, messageKey: '' })),
  ...overrides,
});
const now = Date.parse('2026-10-08T00:00:00Z');
const pending = (): LifecycleConfirmation => ({ action: 'StopTarget', summary: {
  token: 'opaque', targetSessionId: 'session', revision: 3, expiresUtc: '2026-10-08T00:01:00Z', actionId: 'StopTarget',
  description: { key: 'confirmation.lifecycle.stop', parameters: { endpoint: 'http://localhost:8080', processId: 12 } },
} });

describe('target lifecycle controls', () => {
  it('requires exact enabled backend action, safe idle state and a validated local target', () => {
    const value = snapshot();
    expect(canOperateTarget(value, 'StopTarget')).toBe(true);
    expect(canOperateTarget(snapshot({ availableActions: [{ id: 'StartTarget', enabled: true, reasonCode: null, messageKey: '' }] }), 'StartTarget' as TargetLifecycleAction)).toBe(false);
    expect(canOperateTarget(snapshot({ applyStatus: 'reverted' }), 'RestartTarget')).toBe(true);
    for (const patch of [ { connection: 'degraded' as const }, { target: null },
      { target: { ...value.target!, isLocal: false } }, { phase: 'recoveryRequired' as const },
      { applyStatus: 'pending' as const }, { applyStatus: 'unverified' as const }, { applyStatus: 'verified' as const },
      { availableActions: [] }, { availableActions: [{ id: 'stopTarget', enabled: true, reasonCode: null, messageKey: '' }] },
      { availableActions: [{ id: 'StopTarget', enabled: false, reasonCode: 'OWNER_UNAVAILABLE', messageKey: '' }] },
      { activeOperation: { id: 'busy', kind: 'measurement' as const, stage: 'sampling', progress: null, cancellable: true } } ]) {
      expect(canOperateTarget(snapshot(patch), 'StopTarget')).toBe(false);
    }
  });

  it('binds consent to exact action, target, revision, expiry and backend-provided process description', () => {
    const review = pending();
    expect(isCurrentLifecycleConfirmation(review, snapshot(), now)).toBe(true);
    for (const patch of [{ token: ' ' }, { actionId: 'RestartTarget' }, { targetSessionId: 'other' }, { revision: 2 },
      { expiresUtc: 'bad-date' }, { expiresUtc: '2026-10-08T00:00:00Z' },
      { description: { key: 'confirmation.lifecycle.restart', parameters: review.summary.description.parameters } },
      ...[0, -1, 1.5, '12', undefined].map(processId => ({ description: {
        key: 'confirmation.lifecycle.stop', parameters: { endpoint: 'http://localhost:8080', processId },
      } })), { description: { key: 'confirmation.lifecycle.stop', parameters: { endpoint: 'http://other:8080', processId: 12 } } }]) {
      expect(isCurrentLifecycleConfirmation({ ...review, summary: { ...review.summary, ...patch } } as LifecycleConfirmation, snapshot(), now)).toBe(false);
    }
    expect(isCurrentLifecycleConfirmation(review, snapshot({ revision: 4 }), now)).toBe(false);
    expect(isCurrentLifecycleConfirmation(review, snapshot({ target: { ...snapshot().target!, endpoint: 'http://localhost:9090' } }), now)).toBe(false);
    const restart: LifecycleConfirmation = { action: 'RestartTarget', summary: { ...review.summary, actionId: 'RestartTarget',
      description: { ...review.summary.description, key: 'confirmation.lifecycle.restart' } } };
    expect(isCurrentLifecycleConfirmation(restart, snapshot(), now)).toBe(true);
  });

  it('invalidates in-flight review on safety transitions but not locale or metrics updates', () => {
    const value = snapshot();
    const key = lifecycleSnapshotKey(value);
    for (const patch of [{ revision: 4 }, { connection: 'connecting' as const }, { phase: 'recoveryRequired' as const },
      { applyStatus: 'verified' as const }, { target: { ...value.target!, sessionId: 'other' } },
      { target: { ...value.target!, endpoint: 'http://localhost:9090' } }, { availableActions: [] },
      { activeOperation: { id: 'busy', kind: 'stopTarget' as const, stage: 'starting', progress: null, cancellable: false } }]) {
      expect(lifecycleSnapshotKey(snapshot(patch))).not.toBe(key);
    }
    expect(lifecycleSnapshotKey(snapshot({ preferences: { locale: 'ru-RU', theme: 'light' } }))).toBe(key);
  });

  it('renders stop/restart only, disables remote control and explains backend owner blocks', () => {
    const render = (value: AppSnapshot) => renderToStaticMarkup(createElement(TargetLifecyclePanel, {
      snapshot: value, onReview: async () => pending().summary, onRun: async () => {},
    }));
    const html = render(snapshot());
    expect(html).toContain('Review process stop');
    expect(html).toContain('Review process restart');
    expect(html).not.toContain('disabled=""');
    expect(html).not.toContain('StartTarget');
    const blocked = render(snapshot({ target: { ...snapshot().target!, isLocal: false },
      availableActions: [{ id: 'RestartTarget', enabled: false, reasonCode: 'OWNER_UNAVAILABLE', messageKey: '' }] }));
    expect(blocked.match(/disabled=""/g)).toHaveLength(2);
    expect(blocked).toContain('original launch parameters');
  });

  it('keeps RU/EN confirmation keys, endpoint and process placeholders aligned', () => {
    expect(Object.keys(ruLifecycleMessages).sort()).toEqual(Object.keys(enLifecycleMessages).sort());
    for (const key of Object.keys(enLifecycleMessages) as (keyof typeof enLifecycleMessages)[]) {
      const placeholders = (value: string) => [...value.matchAll(/\{(\w+)\}/g)].map(match => match[1]).sort();
      expect(placeholders(ruLifecycleMessages[key])).toEqual(placeholders(enLifecycleMessages[key]));
    }
    expect(translateLifecycle('ru-RU', 'confirmation.lifecycle.restart', { endpoint: 'http://localhost:8080', processId: 12 })).toContain('12');
    expect(translateLifecycle('en-US', 'confirmation.lifecycle.restart', pending().summary.description.parameters)).toContain('original launch parameters');
  });
});
