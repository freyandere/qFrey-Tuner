import { useEffect, useRef, useState } from 'react';
import type { AppSnapshot } from '../contracts/protocol';
import type { ConfirmationSummary, Plan } from '../contracts/domain';
import { translate } from '../i18n/messages';
import { Button, Card, Dialog, StatusBanner } from './base';
import { translateMutation } from './mutationMessages';

export type MutationAction = 'ApplyPlan' | 'Rollback';
export type MutationControlsProps = {
  snapshot: AppSnapshot;
  onApprove: (planId: string, revision: number) => Promise<void>;
  onRequest: (action: MutationAction, cycleId: string, planId: string) => Promise<ConfirmationSummary>;
  onApply: (planId: string, planRevision: number, token: string) => Promise<void>;
  onRollback: (cycleId: string, planRevision: number, token: string) => Promise<void>;
  onKeep: (cycleId: string) => Promise<void>;
};

type Decision = { allowed: boolean; reason: 'ok' | 'unavailable' | 'target' | 'baseline' | 'preview' | 'plan' | 'approved' | 'busy' | 'cycle' | 'applied' | 'rollback' | 'keep' };
type PendingConfirmation = { action: MutationAction; summary: ConfirmationSummary; planId: string; planRevision: number; cycleId: string; targetSessionId: string };

function actionAvailable(snapshot: AppSnapshot, action: string): boolean {
  return snapshot.availableActions.some(item => item.id === action && item.enabled);
}
function canonicalTarget(snapshot: AppSnapshot, plan: Plan | null): plan is Plan {
  return snapshot.connection === 'validated' && snapshot.target !== null && plan !== null
    && plan.id.length > 0 && plan.targetSessionId === snapshot.target.sessionId && Number.isSafeInteger(plan.revision) && plan.revision > 0;
}
function validBaseline(snapshot: AppSnapshot): boolean {
  return snapshot.experiment?.baseline?.kind === 'baseline' && snapshot.experiment.baseline.status === 'valid';
}
function planApplicable(plan: Plan | null): boolean {
  return plan !== null && plan.applicable && !plan.previewOnly && plan.blockReasonCodes.length === 0;
}
export function canApprovePlan(snapshot: AppSnapshot): Decision {
  const plan = snapshot.experiment?.plan ?? null;
  if (!actionAvailable(snapshot, 'AcceptPlan')) return { allowed: false, reason: 'unavailable' };
  if (!canonicalTarget(snapshot, plan)) return { allowed: false, reason: 'target' };
  if (!planApplicable(plan)) return { allowed: false, reason: plan?.previewOnly ? 'preview' : 'plan' };
  if (plan.approved) return { allowed: false, reason: 'approved' };
  return { allowed: true, reason: 'ok' };
}
export function canApplyPlan(snapshot: AppSnapshot): Decision {
  const plan = snapshot.experiment?.plan ?? null;
  if (!actionAvailable(snapshot, 'ApplyPlan')) return { allowed: false, reason: 'unavailable' };
  if (!canonicalTarget(snapshot, plan)) return { allowed: false, reason: 'target' };
  if (!validBaseline(snapshot)) return { allowed: false, reason: 'baseline' };
  if (!planApplicable(plan)) return { allowed: false, reason: plan?.previewOnly ? 'preview' : 'plan' };
  if (!plan.approved) return { allowed: false, reason: 'approved' };
  if (snapshot.applyStatus !== 'notApplied') return { allowed: false, reason: 'applied' };
  if (!snapshot.experiment?.cycleId) return { allowed: false, reason: 'cycle' };
  if (snapshot.activeOperation !== null) return { allowed: false, reason: 'busy' };
  return { allowed: true, reason: 'ok' };
}
export function canRollback(snapshot: AppSnapshot): Decision {
  const plan = snapshot.experiment?.plan ?? null;
  if (!actionAvailable(snapshot, 'Rollback')) return { allowed: false, reason: 'unavailable' };
  if (!canonicalTarget(snapshot, plan)) return { allowed: false, reason: 'target' };
  if (!snapshot.experiment?.cycleId) return { allowed: false, reason: 'cycle' };
  if (!['pending', 'unverified', 'verified'].includes(snapshot.applyStatus)) return { allowed: false, reason: 'rollback' };
  if (snapshot.activeOperation !== null) return { allowed: false, reason: 'busy' };
  return { allowed: true, reason: 'ok' };
}
export function canKeepSettings(snapshot: AppSnapshot): Decision {
  if (!actionAvailable(snapshot, 'KeepChanges')) return { allowed: false, reason: 'unavailable' };
  if (!snapshot.experiment?.cycleId) return { allowed: false, reason: 'cycle' };
  if (snapshot.applyStatus !== 'verified' || !['appliedVerified', 'afterReady'].includes(snapshot.phase)) return { allowed: false, reason: 'keep' };
  if (snapshot.activeOperation !== null) return { allowed: false, reason: 'busy' };
  return { allowed: true, reason: 'ok' };
}
export function isCurrentConfirmation(pending: PendingConfirmation, snapshot: AppSnapshot, now = Date.now()): boolean {
  const plan = snapshot.experiment?.plan;
  const expiry = Date.parse(pending.summary.expiresUtc);
  return Number.isFinite(expiry) && expiry > now && snapshot.target?.sessionId === pending.targetSessionId
    && plan?.id === pending.planId && plan.revision === pending.planRevision && pending.summary.targetSessionId === pending.targetSessionId
    && pending.summary.revision === pending.planRevision && pending.summary.actionId === pending.action
    && snapshot.experiment?.cycleId === pending.cycleId && pending.summary.token.length > 0;
}

function reasonText(snapshot: AppSnapshot, decision: Decision): string {
  const locale = snapshot.preferences.locale;
  const map: Record<Decision['reason'], keyof typeof import('./mutationMessages').enMutationMessages | null> = {
    ok: null, unavailable: 'mutation.unavailable', target: 'mutation.invalidTarget', baseline: 'mutation.baselineRequired',
    preview: 'mutation.planPreview', plan: 'mutation.planNotApplicable', approved: 'mutation.planApproved', busy: 'mutation.busy',
    cycle: 'mutation.noCycle', applied: 'mutation.alreadyApplied', rollback: 'mutation.rollbackUnavailable', keep: 'mutation.keepUnavailable'
  };
  const key = map[decision.reason];
  return key ? translateMutation(locale, key) : '';
}

export function MutationControls({ snapshot, onApprove, onRequest, onApply, onRollback, onKeep }: MutationControlsProps) {
  const plan = snapshot.experiment?.plan ?? null;
  const cycleId = snapshot.experiment?.cycleId ?? null;
  const sessionId = snapshot.target?.sessionId ?? null;
  const locale = snapshot.preferences.locale;
  const [pending, setPending] = useState<PendingConfirmation | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [now, setNow] = useState(Date.now());
  const current = useRef(snapshot);
  current.current = snapshot;

  useEffect(() => { setPending(null); setError(null); }, [plan?.id, plan?.revision, cycleId, sessionId]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [pending]);

  const approve = async () => {
    const decision = canApprovePlan(current.current);
    if (!decision.allowed || !plan) { setError(reasonText(current.current, decision)); return; }
    setBusy(true); setError(null);
    try { await onApprove(plan.id, plan.revision); }
    catch { setError(translateMutation(locale, 'mutation.failed')); }
    finally { setBusy(false); }
  };
  const request = async (action: MutationAction) => {
    const state = current.current;
    const decision = action === 'ApplyPlan' ? canApplyPlan(state) : canRollback(state);
    const currentPlan = state.experiment?.plan;
    const currentCycle = state.experiment?.cycleId;
    const currentSession = state.target?.sessionId;
    if (!decision.allowed || !currentPlan || !currentCycle || !currentSession) { setError(reasonText(state, decision)); return; }
    setBusy(true); setError(null); setPending(null);
    try {
      const summary = await onRequest(action, currentCycle, currentPlan.id);
      const next = current.current;
      const unchanged = next.experiment?.plan?.id === currentPlan.id && next.experiment.plan.revision === currentPlan.revision
        && next.experiment.cycleId === currentCycle && next.target?.sessionId === currentSession;
      const candidate = { action, summary, planId: currentPlan.id, planRevision: currentPlan.revision, cycleId: currentCycle, targetSessionId: currentSession };
      if (!unchanged || !isCurrentConfirmation(candidate, next)) { setError(translateMutation(next.preferences.locale, 'mutation.stale')); return; }
      setPending(candidate); setNow(Date.now());
    } catch { setError(translateMutation(current.current.preferences.locale, 'mutation.failed')); }
    finally { setBusy(false); }
  };
  const confirm = async () => {
    if (!pending) return;
    const state = current.current;
    const decision = pending.action === 'ApplyPlan' ? canApplyPlan(state) : canRollback(state);
    if (!decision.allowed || !isCurrentConfirmation(pending, state)) {
      setPending(null); setError(translateMutation(state.preferences.locale, Date.parse(pending.summary.expiresUtc) <= Date.now() ? 'mutation.expired' : 'mutation.stale')); return;
    }
    setBusy(true); setError(null);
    try {
      if (pending.action === 'ApplyPlan') await onApply(pending.planId, pending.planRevision, pending.summary.token);
      else await onRollback(pending.cycleId, pending.planRevision, pending.summary.token);
      setPending(null);
    } catch { setError(translateMutation(current.current.preferences.locale, 'mutation.failed')); }
    finally { setBusy(false); }
  };
  const keep = async () => {
    const decision = canKeepSettings(current.current);
    const id = current.current.experiment?.cycleId;
    if (!decision.allowed || !id) { setError(reasonText(current.current, decision)); return; }
    setBusy(true); setError(null);
    try { await onKeep(id); }
    catch { setError(translateMutation(current.current.preferences.locale, 'mutation.failed')); }
    finally { setBusy(false); }
  };

  const approveDecision = canApprovePlan(snapshot);
  const applyDecision = canApplyPlan(snapshot);
  const rollbackDecision = canRollback(snapshot);
  const keepDecision = canKeepSettings(snapshot);
  const blockedReasons = [...new Set([
    ...(plan && !approveDecision.allowed ? [reasonText(snapshot, approveDecision)] : []),
    ...(plan && !applyDecision.allowed ? [reasonText(snapshot, applyDecision)] : []),
    ...(plan && !rollbackDecision.allowed && snapshot.applyStatus !== 'notApplied' ? [reasonText(snapshot, rollbackDecision)] : []),
    ...(!keepDecision.allowed && snapshot.applyStatus === 'verified' ? [reasonText(snapshot, keepDecision)] : []),
  ])];
  const validPending = pending !== null && isCurrentConfirmation(pending, snapshot, now);
  const expires = pending ? Date.parse(pending.summary.expiresUtc) : 0;
  const expiresLabel = Number.isFinite(expires) ? new Intl.DateTimeFormat(locale, { timeStyle: 'medium' }).format(expires) : '—';

  if (snapshot.applyStatus === 'reverted' || snapshot.phase === 'completed')
    return <Card title={translateMutation(locale, 'mutation.title')}><StatusBanner>
      {translateMutation(locale, snapshot.applyStatus === 'reverted' ? 'mutation.closedReverted' : 'mutation.closedKept')}
    </StatusBanner></Card>;

  return <Card title={translateMutation(locale, 'mutation.title')}>
    {!plan && <StatusBanner>{translateMutation(locale, 'mutation.planUnavailable')}</StatusBanner>}
    {blockedReasons.map(reason => <p className="field-hint" key={reason}>{reason}</p>)}
    {plan?.approved && <p className="field-hint">{translateMutation(locale, 'mutation.approvedNotice')}</p>}
    <div className="form-actions">
    {plan && <>
      <Button disabled={busy || !approveDecision.allowed} onClick={approve}>{busy ? translateMutation(locale, 'mutation.approving') : translateMutation(locale, 'mutation.approve')}</Button>
      <Button disabled={busy || !applyDecision.allowed} onClick={() => void request('ApplyPlan')}>{translateMutation(locale, 'mutation.apply')}</Button>
      <Button disabled={busy || !rollbackDecision.allowed} onClick={() => void request('Rollback')}>{translateMutation(locale, 'mutation.rollback')}</Button>
    </>}
    <Button disabled={busy || !keepDecision.allowed} onClick={() => void keep()}>{translateMutation(locale, 'mutation.keep')}</Button>
    </div>
    {snapshot.applyStatus === 'verified' && <p className="field-hint">{translateMutation(locale, 'mutation.keepNotice')}</p>}
    {error && <StatusBanner severity="error">{error}</StatusBanner>}
    <Dialog open={pending !== null} title={translateMutation(locale, 'mutation.confirmTitle')} onClose={() => setPending(null)}>
      {pending && <>
        <p>{translate(locale, pending.summary.description.key, pending.summary.description.parameters)}</p>
        <p>{translateMutation(locale, now >= expires ? 'mutation.expired' : 'mutation.expires', { time: expiresLabel })}</p>
        <Button disabled={busy || !validPending} onClick={() => void confirm()}>{translateMutation(locale, 'mutation.confirm')}</Button>
        <Button disabled={busy} onClick={() => setPending(null)}>{translateMutation(locale, 'mutation.cancel')}</Button>
      </>}
    </Dialog>
  </Card>;
}
