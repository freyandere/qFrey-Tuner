import { useEffect, useRef, useState } from 'react';
import type { ConfirmationSummary, NetworkTestResult } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { formatDate, formatNumber } from '../i18n/messages';
import { Button, Card, Dialog } from './base';
import { translateNetwork } from './networkMessages';

type Props = {
  snapshot: AppSnapshot;
  onReview: () => Promise<ConfirmationSummary>;
  onRun: (token: string) => Promise<void>;
  onUse: (result: NetworkTestResult) => void;
};

export function canRunNetworkTest(snapshot: AppSnapshot): boolean {
  return snapshot.connection === 'validated' && snapshot.target !== null && snapshot.activeOperation === null
    && snapshot.phase !== 'recoveryRequired' && snapshot.applyStatus !== 'pending' && snapshot.applyStatus !== 'unverified'
    && snapshot.applyStatus !== 'verified'
    && snapshot.availableActions.find(action => action.id === 'RunNetworkTest')?.enabled !== false;
}

export function isCurrentNetworkConfirmation(summary: ConfirmationSummary, snapshot: AppSnapshot, now = Date.now()): boolean {
  const expiry = Date.parse(summary.expiresUtc);
  return canRunNetworkTest(snapshot) && summary.actionId === 'RunNetworkTest'
    && summary.targetSessionId === snapshot.target?.sessionId && summary.revision === snapshot.revision
    && summary.token.trim().length > 0 && Number.isFinite(expiry) && expiry > now;
}

export function isMeasuredNetworkSpeed(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0;
}

export function canUseNetworkResult(snapshot: AppSnapshot): boolean {
  const result = snapshot.networkTest;
  return canRunNetworkTest(snapshot) && result != null && Number.isFinite(Date.parse(result.measuredUtc))
    && (isMeasuredNetworkSpeed(result.downloadBytesPerSecond) || isMeasuredNetworkSpeed(result.uploadBytesPerSecond));
}

export function NetworkTestPanel({ snapshot, onReview, onRun, onUse }: Props) {
  const locale = snapshot.preferences.locale;
  const t = (key: string, parameters?: Readonly<Record<string, string | number>>) => translateNetwork(locale, key, parameters);
  const [pending, setPending] = useState<ConfirmationSummary | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<'stale' | 'failed' | null>(null);
  const [now, setNow] = useState(Date.now());
  const snapshotRef = useRef(snapshot);
  const busyRef = useRef(false);
  snapshotRef.current = snapshot;
  useEffect(() => {
    if (!pending) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [pending]);

  const review = async () => {
    if (busyRef.current || !canRunNetworkTest(snapshotRef.current)) return;
    const sessionId = snapshotRef.current.target?.sessionId;
    const revision = snapshotRef.current.revision;
    busyRef.current = true; setBusy(true); setPending(null); setError(null);
    try {
      const summary = await onReview();
      const current = snapshotRef.current;
      if (sessionId !== current.target?.sessionId || revision !== current.revision || !isCurrentNetworkConfirmation(summary, current)) {
        setError('stale'); return;
      }
      setNow(Date.now()); setPending(summary);
    } catch { setError('failed'); }
    finally { busyRef.current = false; setBusy(false); }
  };
  const run = async () => {
    if (busyRef.current || !pending) return;
    if (!isCurrentNetworkConfirmation(pending, snapshotRef.current)) {
      setPending(null); setError('stale'); return;
    }
    busyRef.current = true; setBusy(true); setPending(null); setError(null);
    try { await onRun(pending.token); }
    catch { setError('failed'); }
    finally { busyRef.current = false; setBusy(false); }
  };
  const stamp = (value: string) => Number.isFinite(Date.parse(value))
    ? formatDate(locale, Date.parse(value), { dateStyle: 'medium', timeStyle: 'short' }) : '—';
  const speed = (value: number | null) => isMeasuredNetworkSpeed(value)
    ? `${formatNumber(locale, value * 8 / 1_000_000, { maximumFractionDigits: 3 })} Mbps` : t('network.unknown');
  const result = snapshot.networkTest;
  const currentConsent = pending !== null && isCurrentNetworkConfirmation(pending, snapshot, now);

  return <Card title={t('network.title')}>
    <p>{t('network.scope')}</p>
    <p>{t('network.traffic')}</p>
    {!canRunNetworkTest(snapshot) && <p role="status">{t('network.unavailable')}</p>}
    {snapshot.activeOperation?.kind === 'networkTest' && <p role="status">{t('network.running')}</p>}
    <Button disabled={busy || !canRunNetworkTest(snapshot)} onClick={() => void review()}>{t('network.review')}</Button>
    {result ? <>
      <p>{t('network.measured', { time: stamp(result.measuredUtc) })}</p>
      <dl><div><dt>{t('network.download')}</dt><dd>{speed(result.downloadBytesPerSecond)}</dd></div>
        <div><dt>{t('network.upload')}</dt><dd>{speed(result.uploadBytesPerSecond)}</dd></div></dl>
      {result.reasonCodes.map((code, index) => <p role="status" key={`${code}-${index}`}>{t(`network.reason.${code.replace(/^(download|upload):/i, '')}`)}</p>)}
      <Button disabled={busy || !canUseNetworkResult(snapshot)} onClick={() => {
        const current = snapshotRef.current;
        if (!busyRef.current && canUseNetworkResult(current) && current.networkTest) onUse(current.networkTest);
      }}>{t('network.use')}</Button>
      <p>{t('network.useHint')}</p>
    </> : <p role="status">{t('network.noResult')}</p>}
    {error && <p role="alert" className="field-error">{t(`network.${error}`)}</p>}
    <Dialog open={pending !== null} title={t('network.confirmTitle')} onClose={() => setPending(null)}>
      {pending && <>
        <p>{t('network.scope')}</p><p>{t('network.traffic')}</p>
        <p>{t('network.expires', { time: stamp(pending.expiresUtc) })}</p>
        {!currentConsent && <p role="alert">{t('network.stale')}</p>}
        <Button disabled={busy || !currentConsent} onClick={() => void run()}>{t('network.run')}</Button>
        <Button disabled={busy} onClick={() => setPending(null)}>{t('network.cancel')}</Button>
      </>}
    </Dialog>
  </Card>;
}
