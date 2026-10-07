import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { ConfirmationSummary, Plan } from '../src/contracts/domain';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canApprovePlan, canApplyPlan, canKeepSettings, canRollback, isCurrentConfirmation, MutationControls } from '../src/components/MutationControls';
import { enMutationMessages, ruMutationMessages } from '../src/components/mutationMessages';

const plan = (overrides: Partial<Plan> = {}): Plan => ({
  id: 'plan-1', targetSessionId: 'session-1', revision: 7, createdUtc: '2026-10-07T00:00:00Z',
  inputsFingerprint: 'inputs', baselineFingerprint: 'baseline', previewOnly: false, applicable: true, approved: false,
  blockReasonCodes: [], recommendations: [], omissions: [], original: { up_speed: 10 }, proposed: { up_speed: 20 }, ...overrides
});
const baseline = { id: 'baseline-1', kind: 'baseline' as const, status: 'valid' as const, startedUtc: '2026-10-07T00:00:00Z',
  analysisVersion: '1', scope: 'workload' as const, reasonCodes: [], sampleCount: 5, durationMs: 5000,
  meanDownload: { quality: 'notMeasured' as const, reasonCodes: [] }, medianDownload: { quality: 'notMeasured' as const, reasonCodes: [] },
  standardDeviation: { quality: 'notMeasured' as const, reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured' as const, reasonCodes: [] } };
const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 9,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session-1', endpoint: 'https://example.test', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: false },
  phase: 'planReady', applyStatus: 'notApplied', activeOperation: null, metrics: [],
  availableActions: ['AcceptPlan', 'ApplyPlan', 'Rollback', 'KeepChanges'].map(id => ({ id, enabled: true, reasonCode: null, messageKey: '' })),
  experiment: { cycleId: 'cycle-1', runInputs: null, workload: null, baseline, after: null, plan: plan(), results: [] },
  ...overrides
});
const confirmation = (overrides: Partial<ConfirmationSummary> = {}): ConfirmationSummary => ({
  token: 'opaque-token', targetSessionId: 'session-1', revision: 7, expiresUtc: '2026-10-07T00:01:00Z',
  actionId: 'ApplyPlan', description: { key: 'confirmations.apply', parameters: { count: 1, endpoint: 'https://example.test' } }, ...overrides
});

describe('MutationControls guards', () => {
  it('requires target/session and an applicable canonical plan for approval', () => {
    expect(canApprovePlan(snapshot()).allowed).toBe(true);
    expect(canApprovePlan(snapshot({ connection: 'disconnected' })).reason).toBe('target');
    const mismatch = snapshot({ experiment: { ...snapshot().experiment!, plan: plan({ targetSessionId: 'other' }) } });
    expect(canApprovePlan(mismatch).allowed).toBe(false);
    expect(canApprovePlan(snapshot({ experiment: { ...snapshot().experiment!, plan: plan({ previewOnly: true }) } })).reason).toBe('preview');
  });

  it('requires a valid baseline, cycle, approval and idle operation before apply', () => {
    expect(canApplyPlan(snapshot()).allowed).toBe(false);
    const approved = snapshot({ experiment: { ...snapshot().experiment!, plan: plan({ approved: true }) } });
    expect(canApplyPlan(approved).allowed).toBe(true);
    expect(canApplyPlan(snapshot({ experiment: { ...snapshot().experiment!, baseline: { ...baseline, status: 'invalid' } } })).reason).toBe('baseline');
    expect(canApplyPlan(snapshot({ experiment: { ...approved.experiment!, plan: plan({ approved: true }) }, activeOperation: { id: 'op', kind: 'apply', stage: 'starting', progress: null, cancellable: true } })).reason).toBe('busy');
    expect(canApplyPlan(snapshot({ experiment: { ...approved.experiment!, cycleId: null } })).reason).toBe('cycle');
  });

  it('limits rollback and keep to their recoverable, verified states', () => {
    expect(canRollback(snapshot({ applyStatus: 'pending' })).allowed).toBe(true);
    expect(canRollback(snapshot({ applyStatus: 'reverted' })).allowed).toBe(false);
    expect(canRollback(snapshot({ experiment: { ...snapshot().experiment!, cycleId: null } })).allowed).toBe(false);
    expect(canKeepSettings(snapshot({ applyStatus: 'verified', phase: 'afterReady' })).allowed).toBe(true);
    expect(canKeepSettings(snapshot({ applyStatus: 'verified', phase: 'planReady' })).allowed).toBe(false);
    expect(canKeepSettings(snapshot({ applyStatus: 'unverified', phase: 'afterReady' })).allowed).toBe(false);
  });

  it('rejects a confirmation for a changed target, plan revision, action or expiry', () => {
    const pending = { action: 'ApplyPlan' as const, summary: confirmation(), planId: 'plan-1', planRevision: 7, cycleId: 'cycle-1', targetSessionId: 'session-1' };
    expect(isCurrentConfirmation(pending, snapshot(), Date.parse('2026-10-07T00:00:30Z'))).toBe(true);
    expect(isCurrentConfirmation(pending, snapshot({ target: { ...snapshot().target!, sessionId: 'other' } }), Date.parse('2026-10-07T00:00:30Z'))).toBe(false);
    expect(isCurrentConfirmation(pending, snapshot({ experiment: { ...snapshot().experiment!, plan: plan({ revision: 8 }) } }), Date.parse('2026-10-07T00:00:30Z'))).toBe(false);
    expect(isCurrentConfirmation({ ...pending, summary: confirmation({ actionId: 'Rollback' }) }, snapshot(), Date.parse('2026-10-07T00:00:30Z'))).toBe(false);
    expect(isCurrentConfirmation(pending, snapshot(), Date.parse('2026-10-07T00:01:00Z'))).toBe(false);
  });

  it('does not present enabled mutation controls when backend actions are unavailable', () => {
    const value = snapshot({ availableActions: [] });
    const html = renderToStaticMarkup(createElement(MutationControls, {
      snapshot: value, onApprove: async () => {}, onRequest: async () => confirmation(), onApply: async () => {}, onRollback: async () => {}, onKeep: async () => {}
    }));
    expect(html).toContain('disabled=""');
    expect(html).toContain(enMutationMessages['mutation.unavailable']);
  });

  it('keeps all owned message keys and placeholders aligned in RU and EN', () => {
    expect(Object.keys(ruMutationMessages).sort()).toEqual(Object.keys(enMutationMessages).sort());
    const placeholders = (text: string) => [...text.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enMutationMessages) as (keyof typeof enMutationMessages)[])
      expect(placeholders(ruMutationMessages[key])).toEqual(placeholders(enMutationMessages[key]));
  });

  it('shows the completed outcome instead of stale apply controls after rollback', () => {
    const html = renderToStaticMarkup(createElement(MutationControls, {
      snapshot: snapshot({ applyStatus: 'reverted', phase: 'rolledBack', availableActions: [] }),
      onApprove: async () => {}, onRequest: async () => confirmation(), onApply: async () => {},
      onRollback: async () => {}, onKeep: async () => {}
    }));
    expect(html).toContain(enMutationMessages['mutation.closedReverted']);
    expect(html).not.toContain(enMutationMessages['mutation.unavailable']);
    expect(html).not.toContain('>Approve plan</button>');
  });
});
