import { useEffect, useRef, useState } from 'react';
import type { ConfirmationSummary } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { Button, Card, Dialog, StatusBanner } from './base';
import { translateLifecycle } from './lifecycleMessages';

export type TargetLifecycleAction = 'StopTarget' | 'RestartTarget';
export type TargetLifecyclePanelProps = {
  snapshot: AppSnapshot;
  onReview: (action: TargetLifecycleAction) => Promise<ConfirmationSummary>;
  onRun: (action: TargetLifecycleAction, token: string) => Promise<void>;
};
export type LifecycleConfirmation = { action: TargetLifecycleAction; summary: ConfirmationSummary };
const descriptionKey = (action: TargetLifecycleAction) => action === 'StopTarget' ? 'confirmation.lifecycle.stop' : 'confirmation.lifecycle.restart';

export function canOperateTarget(snapshot: AppSnapshot, action: TargetLifecycleAction): boolean {
  return (action === 'StopTarget' || action === 'RestartTarget')
    && snapshot.connection === 'validated' && snapshot.target?.isLocal === true
    && snapshot.activeOperation === null && snapshot.phase !== 'recoveryRequired'
    && (snapshot.applyStatus === 'notApplied' || snapshot.applyStatus === 'reverted')
    && snapshot.availableActions.some(item => item.id === action && item.enabled);
}

export function isCurrentLifecycleConfirmation(pending: LifecycleConfirmation, snapshot: AppSnapshot, now = Date.now()): boolean {
  const { summary, action } = pending;
  const expires = Date.parse(summary.expiresUtc);
  const processId = summary.description.parameters.processId;
  return canOperateTarget(snapshot, action) && summary.actionId === action
    && summary.targetSessionId === snapshot.target?.sessionId && summary.revision === snapshot.revision
    && summary.token.trim().length > 0 && Number.isFinite(expires) && expires > now
    && summary.description.key === descriptionKey(action)
    && summary.description.parameters.endpoint === snapshot.target?.endpoint
    && typeof processId === 'number' && Number.isSafeInteger(processId) && processId > 0;
}

// Observe every safety-state transition, including a change away and back while a review is in flight.
export function lifecycleSnapshotKey(snapshot: AppSnapshot): string {
  return JSON.stringify([snapshot.revision, snapshot.target?.sessionId, snapshot.target?.endpoint,
    snapshot.target?.isLocal, snapshot.connection, snapshot.phase, snapshot.applyStatus, snapshot.activeOperation?.id,
    canOperateTarget(snapshot, 'StopTarget'), canOperateTarget(snapshot, 'RestartTarget')]);
}

export function TargetLifecyclePanel({ snapshot, onReview, onRun }: TargetLifecyclePanelProps) {
  const t = (key: Parameters<typeof translateLifecycle>[1], parameters?: Record<string, string | number>) => translateLifecycle(snapshot.preferences.locale, key, parameters);
  const [pending, setPending] = useState<LifecycleConfirmation | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<'failed' | 'stale' | 'accepted' | null>(null);
  const [now, setNow] = useState(Date.now());
  const current = useRef(snapshot);
  current.current = snapshot;
  const key = lifecycleSnapshotKey(snapshot);
  const safety = useRef({ key, generation: 0 });
  if (safety.current.key !== key) safety.current = { key, generation: safety.current.generation + 1 };
  const mounted = useRef(true);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; safety.current.generation++; }; }, []);
  useEffect(() => { setPending(null); }, [key]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [pending]);

  const review = async (action: TargetLifecycleAction) => {
    if (busy || !canOperateTarget(current.current, action)) return;
    const generation = safety.current.generation;
    setBusy(true); setPending(null); setMessage(null);
    try {
      const candidate = { action, summary: await onReview(action) };
      if (!mounted.current) return;
      if (generation !== safety.current.generation || !isCurrentLifecycleConfirmation(candidate, current.current)) { setMessage('stale'); return; }
      setPending(candidate); setNow(Date.now());
    } catch { if (mounted.current) setMessage('failed'); }
    finally { if (mounted.current) setBusy(false); }
  };
  const confirm = async () => {
    if (busy || !pending) return;
    if (!isCurrentLifecycleConfirmation(pending, current.current)) { setPending(null); setMessage('stale'); return; }
    setPending(null); setBusy(true); setMessage(null);
    try { await onRun(pending.action, pending.summary.token); if (mounted.current) setMessage('accepted'); }
    catch { if (mounted.current) setMessage('failed'); }
    finally { if (mounted.current) setBusy(false); }
  };
  return <Card title={t('title')}>
    <p>{snapshot.target?.endpoint ?? '—'}</p>
    {(['StopTarget', 'RestartTarget'] as const).map(action => {
      const available = canOperateTarget(snapshot, action);
      const reason = snapshot.availableActions.find(item => item.id === action)?.reasonCode;
      const reasonKey = reason === 'OWNER_UNAVAILABLE' ? 'ownerUnavailable' : reason === 'RECOVERY_REQUIRED' ? 'recoveryRequired'
        : reason === 'OPERATION_CONFLICT' ? 'operationConflict' : 'unavailable';
      return <div key={action}>
        <Button disabled={busy || !available} onClick={() => void review(action)}>{t(action === 'StopTarget' ? 'stop' : 'restart')}</Button>
        {!available && <StatusBanner>{t(reasonKey)}</StatusBanner>}
      </div>;
    })}
    {message && <StatusBanner severity={message === 'accepted' ? 'info' : 'error'}>{t(message)}</StatusBanner>}
    <Dialog open={pending !== null} title={t('confirmTitle')} onClose={() => setPending(null)}>
      {pending && <>
        <p>{t(descriptionKey(pending.action), pending.summary.description.parameters)}</p>
        {!isCurrentLifecycleConfirmation(pending, snapshot, now) && <p role="status">{t('stale')}</p>}
        <Button disabled={busy || !isCurrentLifecycleConfirmation(pending, snapshot, now)} onClick={() => void confirm()}>{t('confirm')}</Button>
        <Button disabled={busy} onClick={() => setPending(null)}>{t('cancel')}</Button>
      </>}
    </Dialog>
  </Card>;
}
