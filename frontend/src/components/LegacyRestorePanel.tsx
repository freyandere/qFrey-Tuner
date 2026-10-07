import { useEffect, useState } from 'react';
import type { ConfirmationSummary } from '../contracts/domain';
import type { AppSnapshot } from '../contracts/protocol';
import { translate } from '../i18n/messages';
import { Badge, Button, Card, Dialog, StatusBanner } from './base';
import { translateRestore } from './restoreMessages';

type RestoreReview = NonNullable<AppSnapshot['restore']>['review'];
type RestoreSnapshot = NonNullable<AppSnapshot['restore']>;

export interface LegacyRestorePanelProps {
  snapshot: AppSnapshot;
  onReview: () => Promise<void>;
  onRestore: (selectionToken: string, confirmationToken: string) => Promise<void>;
}

export function getRestoreConfirmation(snapshot: AppSnapshot, now = Date.now()): ConfirmationSummary | null {
  const sessionId = snapshot.target?.sessionId;
  if (snapshot.connection !== 'validated' || !sessionId) return null;
  const candidates = (snapshot.confirmations ?? []).filter(item => item.actionId === 'RestoreLegacyBackup'
    && item.targetSessionId === sessionId && item.revision === snapshot.revision
    && item.token.length > 0 && Number.isFinite(Date.parse(item.expiresUtc)) && Date.parse(item.expiresUtc) > now);
  return candidates.length === 1 ? candidates[0]! : null;
}

export function canRestoreLegacyBackup(snapshot: AppSnapshot): boolean {
  const restore = snapshot.restore;
  return Boolean(snapshot.connection === 'validated' && snapshot.target && restore?.selectionToken
    && restore.review.targetMatches && restore.review.canRestore && !restore.review.alreadyOriginal
    && restore.review.blockReasonCodes.length === 0
    && !restore.review.differences.some(item => item.disposition === 'conflict')
    && !snapshot.activeOperation && getRestoreConfirmation(snapshot));
}

function valueText(value: number | boolean | string | null, locale: AppSnapshot['preferences']['locale']): string {
  if (value === null) return translateRestore(locale, 'restore.unknown');
  if (typeof value === 'boolean') return translateRestore(locale, value ? 'restore.yes' : 'restore.no');
  if (typeof value === 'number') return new Intl.NumberFormat(locale).format(value);
  return value.length === 0 ? translateRestore(locale, 'restore.valueEmpty') : value;
}

function settingLabel(key: string, index: number, locale: AppSnapshot['preferences']['locale']): string {
  const label = translate(locale, `recommendations.${key}`);
  return label === translate(locale, 'errors.unknown')
    ? translateRestore(locale, 'restore.settingNumber', { number: index + 1 }) : label;
}

function dispositionText(disposition: RestoreReview['differences'][number]['disposition'], locale: AppSnapshot['preferences']['locale']): string {
  const key = disposition === 'alreadyOriginal' ? 'restore.alreadyOriginalItem'
    : disposition === 'restoreRequired' ? 'restore.restoreRequired' : `restore.${disposition}`;
  return translateRestore(locale, key);
}

function reviewIsBlocked(restore: RestoreSnapshot): boolean {
  return !restore.review.targetMatches || !restore.review.canRestore || restore.review.alreadyOriginal
    || restore.review.blockReasonCodes.length > 0 || restore.review.differences.some(item => item.disposition === 'conflict');
}

export function LegacyRestorePanel({ snapshot, onReview, onRestore }: LegacyRestorePanelProps) {
  const [dialogOpen, setDialogOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const locale = snapshot.preferences.locale;
  const text = (key: string) => translateRestore(locale, key);
  const restore = snapshot.restore;
  useEffect(() => setDialogOpen(false), [restore?.selectionToken, restore?.review.fingerprint, snapshot.target?.sessionId, snapshot.revision]);
  const confirmation = getRestoreConfirmation(snapshot);
  const blocked = restore ? reviewIsBlocked(restore) : true;
  const cannotStart = !canRestoreLegacyBackup(snapshot) || busy;

  const review = async () => {
    if (busy || snapshot.activeOperation) return;
    setBusy(true);
    setFailed(false);
    try { await onReview(); }
    catch { setFailed(true); }
    finally { setBusy(false); }
  };

  const confirm = async () => {
    const currentConfirmation = getRestoreConfirmation(snapshot);
    if (!restore || !currentConfirmation || !canRestoreLegacyBackup(snapshot) || busy) {
      setDialogOpen(false);
      return;
    }
    setBusy(true);
    setDialogOpen(false);
    setFailed(false);
    try { await onRestore(restore.selectionToken, currentConfirmation.token); }
    catch { setFailed(true); }
    finally { setBusy(false); }
  };

  return <Card title={text('restore.title')}>
    <p>{text('restore.empty')}</p>
    {snapshot.activeOperation && <StatusBanner severity="warning">{text('restore.busy')}</StatusBanner>}
    <Button disabled={busy || Boolean(snapshot.activeOperation) || snapshot.connection !== 'validated'} onClick={() => void review()}>
      {busy ? text('restore.selecting') : text('restore.select')}
    </Button>
    {failed && <StatusBanner severity="error">{text('restore.failed')}</StatusBanner>}

    {restore && <section aria-labelledby="restore-review-title">
      <h3 id="restore-review-title">{text('restore.reviewTitle')}</h3>
      <dl>
        <dt>{text('restore.source')}</dt><dd>{restore.displayName}</dd>
        <dt>{text('restore.sourceCycle')}</dt><dd>{restore.sourceCycleId}</dd>
        <dt>{text('restore.legacy')}</dt><dd><Badge tone="info">{restore.review.isLegacy ? text('restore.yes') : text('restore.no')}</Badge></dd>
        <dt>{text('restore.targetMatch')}</dt><dd>{restore.review.targetMatches ? text('restore.yes') : text('restore.no')}</dd>
        <dt>{text('restore.canRestore')}</dt><dd>{restore.review.canRestore ? text('restore.yes') : text('restore.no')}</dd>
      </dl>
      {restore.review.alreadyOriginal && <StatusBanner>{text('restore.alreadyOriginal')}</StatusBanner>}
      {restore.review.blockReasonCodes.length > 0 && <div aria-label={text('restore.blocked')}>
        <ul>{restore.review.blockReasonCodes.map((code, index) => <li key={`${code}:${index}`}>{text(`restore.reason.${code}`)}</li>)}</ul>
      </div>}
      {(blocked && !restore.review.alreadyOriginal || !confirmation) && <StatusBanner severity="warning">
        {snapshot.activeOperation ? text('restore.busy') : blocked && !restore.review.alreadyOriginal ? text('restore.blocked')
          : text('restore.noConfirmation')}
      </StatusBanner>}
      {restore.review.differences.length > 0 && <div className="table-scroll">
        <table>
          <caption>{text('restore.differences')}</caption>
          <thead><tr><th>{text('restore.setting')}</th><th>{text('restore.current')}</th><th>{text('restore.original')}</th><th>{text('restore.intended')}</th><th>{text('restore.disposition')}</th></tr></thead>
          <tbody>{restore.review.differences.map((item, index) => <tr key={`${item.key}:${index}`}>
            <th scope="row">{settingLabel(item.key, index, locale)} <details><summary>{text('restore.apiKey')}</summary><code>{item.key}</code></details></th>
            <td>{valueText(item.current, locale)}</td><td>{valueText(item.original, locale)}</td><td>{valueText(item.intended, locale)}</td>
            <td>{dispositionText(item.disposition, locale)}</td>
          </tr>)}</tbody>
        </table>
      </div>}
      <Button disabled={cannotStart} onClick={() => setDialogOpen(true)}>{text('restore.restoreAction')}</Button>
    </section>}

    <Dialog open={dialogOpen} title={text('restore.confirmTitle')} onClose={() => setDialogOpen(false)}>
      <p>{text('restore.confirmBody')}</p>
      {!confirmation && <StatusBanner severity="warning">{text('restore.expired')}</StatusBanner>}
      <Button disabled={!restore || !confirmation || !canRestoreLegacyBackup(snapshot) || busy} onClick={() => void confirm()}>
        {busy ? text('restore.working') : text('restore.confirmAction')}
      </Button>
      <Button disabled={busy} onClick={() => setDialogOpen(false)}>{text('restore.cancel')}</Button>
    </Dialog>
  </Card>;
}
