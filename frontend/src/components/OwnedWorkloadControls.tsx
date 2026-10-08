import { useEffect, useRef, useState } from 'react';
import type { ConfirmationSummary, WorkloadReference } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { Button, Card, Dialog, StatusBanner } from './base';
import { parseExistingHashes } from './experimentMessages';
import { translateOwnedAction } from './ownedActionMessages';

export type OwnedWorkloadAction = 'StartOwnedWorkload' | 'StopOwnedWorkload';
export type OwnedWorkloadControlsProps = {
  snapshot: AppSnapshot;
  onReview: (action: OwnedWorkloadAction, workloadId: string) => Promise<ConfirmationSummary>;
  onAction: (action: OwnedWorkloadAction, workloadId: string, token: string) => Promise<void>;
  onSelect?: (workloadId: string) => Promise<void>;
};
export type OwnedConfirmation = { action: OwnedWorkloadAction; workload: WorkloadReference; summary: ConfirmationSummary };

export function ownedMeasurementReference(snapshot: AppSnapshot): WorkloadReference | null {
  const workload = snapshot.experiment?.workload;
  if (workload?.reference.kind !== 'owned' || !workload.ownershipVerified || !workload.reference.id.trim()
    || workload.reference.hashes.length !== 1 || !parseExistingHashes(workload.reference.hashes.join(' ')).valid) return null;
  return { ...workload.reference, hashes: [...workload.reference.hashes] };
}
export function canOperateOwnedWorkload(snapshot: AppSnapshot, action: OwnedWorkloadAction): boolean {
  return ownedMeasurementReference(snapshot) !== null && snapshot.connection === 'validated' && snapshot.target !== null
    && snapshot.activeOperation === null && snapshot.phase !== 'recoveryRequired'
    && snapshot.applyStatus !== 'pending' && snapshot.applyStatus !== 'unverified'
    && snapshot.availableActions.some(item => item.id === action && item.enabled);
}
export function isCurrentOwnedConfirmation(pending: OwnedConfirmation, snapshot: AppSnapshot, now = Date.now()): boolean {
  const workload = ownedMeasurementReference(snapshot);
  const expiry = Date.parse(pending.summary.expiresUtc);
  return canOperateOwnedWorkload(snapshot, pending.action) && workload?.id === pending.workload.id
    && workload.hashes.length === pending.workload.hashes.length
    && workload.hashes.every((hash, index) => hash === pending.workload.hashes[index])
    && pending.summary.actionId === pending.action && pending.summary.targetSessionId === snapshot.target?.sessionId
    && pending.summary.revision === snapshot.revision && pending.summary.token.trim().length > 0
    && Number.isFinite(expiry) && expiry > now;
}

export function OwnedWorkloadControls({ snapshot, onReview, onAction, onSelect }: OwnedWorkloadControlsProps) {
  const locale = snapshot.preferences.locale;
  const t = (key: Parameters<typeof translateOwnedAction>[1], parameters?: Record<string, string | number>) => translateOwnedAction(locale, key, parameters);
  const [pending, setPending] = useState<OwnedConfirmation | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<'failed' | 'stale' | 'accepted' | null>(null);
  const [now, setNow] = useState(Date.now());
  const current = useRef(snapshot);
  current.current = snapshot;
  useEffect(() => { setPending(null); }, [snapshot.revision, snapshot.target?.sessionId, snapshot.experiment?.workload?.reference.id]);
  useEffect(() => {
    if (!pending) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [pending]);

  const review = async (action: OwnedWorkloadAction) => {
    const initial = current.current;
    const workload = ownedMeasurementReference(initial);
    if (busy || !workload || !canOperateOwnedWorkload(initial, action)) return;
    setBusy(true); setPending(null); setMessage(null);
    try {
      const summary = await onReview(action, workload.id);
      const candidate = { action, workload, summary };
      if (initial.target?.sessionId !== current.current.target?.sessionId || initial.revision !== current.current.revision
        || !isCurrentOwnedConfirmation(candidate, current.current)) { setMessage('stale'); return; }
      setPending(candidate); setNow(Date.now());
    } catch { setMessage('failed'); }
    finally { setBusy(false); }
  };
  const confirm = async () => {
    if (busy || !pending) return;
    if (!isCurrentOwnedConfirmation(pending, current.current)) { setPending(null); setMessage('stale'); return; }
    setPending(null); setBusy(true); setMessage(null);
    try { await onAction(pending.action, pending.workload.id, pending.summary.token); setMessage('accepted'); }
    catch { setMessage('failed'); }
    finally { setBusy(false); }
  };
  const workload = snapshot.experiment?.workload;
  const candidates = snapshot.ownedWorkloadCandidates ?? [];
  if (candidates.length > 0) return <Card title={t('title')}>
    <p>{t('selectHint')}</p>
    {candidates.map(candidate => <div key={candidate.reference.id}>
      <p>{candidate.name}</p><p>{t('size', { bytes: candidate.totalBytesDecimal })}</p>
      <p>{t('path', { path: candidate.serverSavePath })}</p>
      <p>{candidate.reference.hashes.join(', ')}</p>
      <Button disabled={busy || !onSelect || snapshot.connection !== 'validated' || !snapshot.target
        || snapshot.activeOperation !== null || snapshot.applyStatus === 'pending' || snapshot.applyStatus === 'unverified'
        || snapshot.applyStatus === 'verified' || snapshot.experiment?.baseline != null || !candidate.ownershipVerified}
        onClick={() => {
          if (busy || !onSelect) return;
          setBusy(true); setMessage(null);
          void onSelect(candidate.reference.id).catch(() => setMessage('failed')).finally(() => setBusy(false));
        }}>{t('select')}</Button>
    </div>)}
    {message === 'failed' && <StatusBanner severity="error">{t('failed')}</StatusBanner>}
  </Card>;
  if (workload?.reference.kind !== 'owned') return null;
  return <Card title={t('title')}>
    <p>{workload.name}</p>
    <p>{t('size', { bytes: workload.totalBytesDecimal })}</p>
    <p>{t('path', { path: workload.serverSavePath })}</p>
    <p>{t('measurement')}</p>
    {(['StartOwnedWorkload', 'StopOwnedWorkload'] as const).map(action => <Button key={action}
      disabled={busy || !canOperateOwnedWorkload(snapshot, action)} onClick={() => void review(action)}>
      {t(action === 'StartOwnedWorkload' ? 'start' : 'stop')}
    </Button>)}
    {!canOperateOwnedWorkload(snapshot, 'StartOwnedWorkload') && !canOperateOwnedWorkload(snapshot, 'StopOwnedWorkload')
      && <StatusBanner>{t('unavailable')}</StatusBanner>}
    {message && <StatusBanner severity={message === 'accepted' ? 'info' : 'error'}>{t(message)}</StatusBanner>}
    <Dialog open={pending !== null} title={t('confirmTitle')} onClose={() => setPending(null)}>
      {pending && <>
        <p>{t(pending.action === 'StartOwnedWorkload' ? 'confirmation.workload.start' : 'confirmation.workload.stop', {
          name: workload.name, sizeBytes: workload.totalBytesDecimal, serverSavePath: workload.serverSavePath,
          endpoint: snapshot.target?.endpoint ?? '—',
        })}</p>
        {!isCurrentOwnedConfirmation(pending, snapshot, now) && <p role="status">{t('stale')}</p>}
        <Button disabled={busy || !isCurrentOwnedConfirmation(pending, snapshot, now)} onClick={() => void confirm()}>{t('confirm')}</Button>
        <Button disabled={busy} onClick={() => setPending(null)}>{t('cancel')}</Button>
      </>}
    </Dialog>
  </Card>;
}
