import { useEffect, useRef, useState } from 'react';
import type { ConfirmationSummary } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { translate } from '../i18n/messages';
import { translateWorkload } from './workloadMessages';
import { Button, Card, Dialog, StatusBanner } from './base';

type Props = {
  snapshot: AppSnapshot;
  onReview: (catalogueId: string, serverSavePath: string) => Promise<ConfirmationSummary>;
  onPrepare: (catalogueId: string, serverSavePath: string, token: string) => Promise<void>;
};

type PendingReview = { summary: ConfirmationSummary; catalogueId: string; serverSavePath: string; sessionId: string };

export function isAbsoluteServerSavePath(value: string): boolean {
  if (!value || value.trim() !== value || value.length > 2048 || /[\u0000-\u001f\u007f]/.test(value)) return false;
  const driveRooted = /^[A-Za-z]:[\\/]/.test(value);
  const posixRooted = value.startsWith('/');
  const uncRooted = value.startsWith('\\\\') && value.length > 2;
  return (driveRooted || posixRooted || uncRooted)
    && !value.split(/[\\/]/).some(component => component === '.' || component === '..');
}

export function canReviewWorkloadPreparation(snapshot: AppSnapshot): boolean {
  const experiment = snapshot.experiment;
  return snapshot.connection === 'validated' && snapshot.target !== null
    && snapshot.activeOperation === null && (snapshot.workloadCatalogue?.length ?? 0) > 0
    && experiment?.baseline == null && experiment?.after == null
    && experiment?.workload?.reference.kind !== 'owned'
    && snapshot.applyStatus !== 'pending' && snapshot.applyStatus !== 'unverified' && snapshot.applyStatus !== 'verified'
    && snapshot.phase !== 'recoveryRequired';
}

export function isCurrentWorkloadConfirmation(summary: ConfirmationSummary, snapshot: AppSnapshot, now = Date.now()): boolean {
  const expires = Date.parse(summary.expiresUtc);
  return canReviewWorkloadPreparation(snapshot) && snapshot.target !== null
    && summary.actionId === 'PrepareWorkload' && summary.targetSessionId === snapshot.target.sessionId
    && summary.revision === snapshot.revision && summary.token.trim().length > 0
    && Number.isFinite(expires) && expires > now;
}

function formatExactBytes(locale: AppSnapshot['preferences']['locale'], value: string): string {
  return /^\d+$/.test(value) ? new Intl.NumberFormat(locale).format(BigInt(value)) : value;
}

export function WorkloadPreparationPanel({ snapshot, onReview, onPrepare }: Props) {
  const locale = snapshot.preferences.locale;
  const t = (key: string, parameters?: Readonly<Record<string, string | number>>) => translateWorkload(locale, key, parameters);
  const catalogue = snapshot.workloadCatalogue ?? [];
  const [catalogueId, setCatalogueId] = useState(catalogue[0]?.id ?? '');
  const [serverSavePath, setServerSavePath] = useState('');
  const [pending, setPending] = useState<PendingReview | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const snapshotRef = useRef(snapshot);
  const selectionRef = useRef({ catalogueId, serverSavePath });
  snapshotRef.current = snapshot;
  selectionRef.current = { catalogueId, serverSavePath };

  useEffect(() => {
    if (!catalogue.some(entry => entry.id === catalogueId)) setCatalogueId(catalogue[0]?.id ?? '');
  }, [catalogue, catalogueId]);
  useEffect(() => { setPending(null); setError(''); }, [catalogueId, serverSavePath, snapshot.target?.sessionId, snapshot.revision]);

  const selected = catalogue.find(entry => entry.id === catalogueId) ?? null;
  const allowed = canReviewWorkloadPreparation(snapshot);
  const pathValid = isAbsoluteServerSavePath(serverSavePath);
  const canSubmit = allowed && selected !== null && pathValid && !busy;
  const pendingCurrent = pending !== null && selectionRef.current.catalogueId === pending.catalogueId
    && selectionRef.current.serverSavePath === pending.serverSavePath
    && snapshot.target?.sessionId === pending.sessionId
    && isCurrentWorkloadConfirmation(pending.summary, snapshot);

  const review = async () => {
    if (!canSubmit || !selected || !snapshot.target) return;
    const initial = { catalogueId, serverSavePath, sessionId: snapshot.target.sessionId, revision: snapshot.revision };
    setBusy(true); setError(''); setNotice('');
    try {
      const summary = await onReview(initial.catalogueId, initial.serverSavePath);
      const current = snapshotRef.current;
      const selection = selectionRef.current;
      if (selection.catalogueId !== initial.catalogueId || selection.serverSavePath !== initial.serverSavePath
        || current.target?.sessionId !== initial.sessionId || current.revision !== initial.revision
        || !isCurrentWorkloadConfirmation(summary, current)) {
        setError(t('workload.catalogue.confirmationStale'));
        return;
      }
      setPending({ summary, ...initial });
    } catch {
      setError(t('workload.catalogue.reviewFailed'));
    } finally {
      setBusy(false);
    }
  };

  const confirm = async () => {
    if (!pending) return;
    const current = snapshotRef.current;
    const selection = selectionRef.current;
    if (Date.parse(pending.summary.expiresUtc) <= Date.now()) {
      setPending(null); setError(t('workload.catalogue.confirmationExpired')); return;
    }
    if (selection.catalogueId !== pending.catalogueId || selection.serverSavePath !== pending.serverSavePath
      || current.target?.sessionId !== pending.sessionId || !isCurrentWorkloadConfirmation(pending.summary, current)) {
      setPending(null); setError(t('workload.catalogue.confirmationStale')); return;
    }
    setPending(null); setBusy(true); setError(''); setNotice('');
    try {
      await onPrepare(pending.catalogueId, pending.serverSavePath, pending.summary.token);
      setNotice(t('workload.catalogue.requestAccepted'));
    } catch {
      setError(t('workload.catalogue.prepareFailed'));
    } finally {
      setBusy(false);
    }
  };

  const workload = snapshot.experiment?.workload;
  const ownedWorkload = workload?.reference.kind === 'owned' ? workload : null;
  const matchingWorkloadEntry = ownedWorkload?.catalogueId
    ? catalogue.find(entry => entry.id === ownedWorkload.catalogueId) : null;
  const prepareOperation = snapshot.activeOperation?.kind === 'prepareWorkload';

  return <Card title={t('workload.catalogue.title')}>
    {(snapshot.connection !== 'validated' || snapshot.target === null) && <p role="status">{t('workload.catalogue.noTarget')}</p>}
    {snapshot.connection === 'validated' && snapshot.target !== null && catalogue.length === 0 && <p role="status">{t('workload.catalogue.noCatalogue')}</p>}
    {snapshot.connection === 'validated' && snapshot.target !== null && catalogue.length > 0 && !allowed && <p role="status">{t('workload.catalogue.unavailable')}</p>}
    {catalogue.length > 0 && <>
      <div className="field">
        <label htmlFor="workload-catalogue">{t('workload.catalogue.select')}</label>
        <select id="workload-catalogue" value={selected?.id ?? ''} disabled={busy || snapshot.activeOperation !== null || !catalogue.length}
          onChange={event => { setCatalogueId(event.target.value); setError(''); setNotice(''); }}>
          {catalogue.map(entry => <option key={entry.id} value={entry.id}>{entry.name}</option>)}
        </select>
      </div>
      {selected && <dl>
        <div><dt>{t('workload.catalogue.bytes', { bytes: formatExactBytes(locale, selected.totalBytesDecimal) })}</dt><dd>{selected.name}</dd></div>
        <div><dt>{t('workload.catalogue.source')}</dt><dd><code>{selected.metadataSource}</code></dd></div>
      </dl>}
      <div className="field">
        <label htmlFor="workload-save-path">{t('workload.catalogue.path')}</label>
        <input id="workload-save-path" type="text" autoComplete="off" maxLength={2048} value={serverSavePath}
          disabled={busy || snapshot.activeOperation !== null} onChange={event => { setServerSavePath(event.target.value); setError(''); setNotice(''); }}
          aria-invalid={serverSavePath.length > 0 && !pathValid} />
        <p className="field-hint">{t('workload.catalogue.pathHint')}</p>
        {serverSavePath.length > 0 && !pathValid && <p className="field-error" role="alert">{t('workload.catalogue.pathInvalid')}</p>}
      </div>
      <p>{t('workload.catalogue.noDownload')}</p>
      <Button disabled={!canSubmit} onClick={() => void review()}>{busy ? t('workload.catalogue.reviewing') : t('workload.catalogue.prepare')}</Button>
    </>}
    {prepareOperation && <StatusBanner>{t('workload.catalogue.preparing')}</StatusBanner>}
    {ownedWorkload && <section aria-label={t('workload.catalogue.ownedTitle')}>
      <h3>{t('workload.catalogue.ownedTitle')}</h3>
      <p>{ownedWorkload.name}</p>
      <p>{t('workload.catalogue.bytes', { bytes: formatExactBytes(locale, ownedWorkload.totalBytesDecimal) })}</p>
      <p><code>{ownedWorkload.serverSavePath}</code></p>
      {matchingWorkloadEntry && <p><code>{matchingWorkloadEntry.metadataSource}</code></p>}
      <p role="status">{t(ownedWorkload.ownershipVerified ? 'workload.catalogue.stopped' : 'workload.catalogue.ownershipUnverified')}</p>
    </section>}
    {notice && <p role="status">{notice}</p>}
    {error && <p role="alert" className="field-error">{error}</p>}
    <Dialog open={pending !== null} title={t('workload.catalogue.confirmTitle')} onClose={() => setPending(null)}>
      {pending && <>
        <p>{translate(locale, pending.summary.description.key, pending.summary.description.parameters)}</p>
        <p>{t('workload.catalogue.noDownload')}</p>
        <Button disabled={busy || !pendingCurrent} onClick={() => void confirm()}>{t('workload.catalogue.confirm')}</Button>
        <Button disabled={busy} onClick={() => setPending(null)}>{translate(locale, 'confirmations.cancel')}</Button>
      </>}
    </Dialog>
  </Card>;
}
