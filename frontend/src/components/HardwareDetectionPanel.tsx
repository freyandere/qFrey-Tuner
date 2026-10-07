import { useState } from 'react';
import type { DraftInputs, NativeSelection } from '../contracts/domain';
import type { AppSnapshot, MetricReading } from '../contracts/protocol';
import { formatDate, formatNumber } from '../i18n/messages';
import { translateHardware } from './hardwareMessages';
import { Card } from './base';

type Props = {
  snapshot: AppSnapshot;
  onDetect: (volumeToken: string | null) => Promise<void>;
  onSelectVolume: () => Promise<NativeSelection | null>;
  onAdopt: (inputs: DraftInputs['hardware']) => void;
};

export function canAdoptHardware(snapshot: AppSnapshot): boolean {
  const hardware = snapshot.hardware;
  const facts = hardware?.facts;
  return snapshot.connection === 'validated' && snapshot.target?.isLocal === true && snapshot.activeOperation === null
    && hardware?.inputs !== null && hardware?.inputs !== undefined && facts !== undefined
    && facts.ramBytes.status === 'fresh' && facts.logicalCpuCount.status === 'fresh'
    && facts.performanceCpuCount.status === 'fresh' && facts.storageType !== null && facts.storageReasonCode === null
    && hardware.inputs.source === 'localDetection';
}

export function HardwareDetectionPanel({ snapshot, onDetect, onSelectVolume, onAdopt }: Props) {
  const locale = snapshot.preferences.locale;
  const t = (key: string, parameters?: Readonly<Record<string, string | number>>) => translateHardware(locale, key, parameters);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState(false);
  const [selectedFolder, setSelectedFolder] = useState('');
  const hardware = snapshot.hardware;
  const facts = hardware?.facts;
  const targetReady = snapshot.connection === 'validated' && snapshot.target !== null;
  const busy = pending || snapshot.activeOperation !== null;
  const adoptable = canAdoptHardware(snapshot) && !pending;

  const detect = async (volumeToken: string | null) => {
    if (!targetReady || busy) return;
    setPending(true); setError(false);
    try { await onDetect(volumeToken); }
    catch { setError(true); }
    finally { setPending(false); }
  };
  const chooseVolume = async () => {
    if (!snapshot.target?.isLocal || !targetReady || busy) return;
    setPending(true); setError(false);
    try {
      const selection = await onSelectVolume();
      if (!selection || selection.purpose !== 'volume') return;
      setSelectedFolder(selection.displayName);
      await onDetect(selection.token);
    } catch { setError(true); }
    finally { setPending(false); }
  };

  const reason = (code: string | null | undefined) => code ? t(`hardware.reason.${code}`) : '';
  const reading = (value: MetricReading | undefined, kind: 'ram' | 'count') => {
    if (!value) return <span>{t('hardware.notDetected')}</span>;
    let number: number;
    let sampledAtUtc: string;
    let stale = false;
    let staleReason: string | null = null;
    switch (value.status) {
      case 'fresh': number = value.value; sampledAtUtc = value.sampledAtUtc; break;
      case 'stale': number = value.lastValue; sampledAtUtc = value.sampledAtUtc; stale = true; staleReason = value.reasonCode; break;
      case 'unavailable': return <><span>{t('hardware.unavailable')}</span> <small>{reason(value.reasonCode)}</small></>;
      case 'error': return <><span>{t('hardware.error')}</span> <small>{reason(value.reasonCode)}</small></>;
    }
    const stamp = formatDate(locale, Date.parse(sampledAtUtc), { dateStyle: 'medium', timeStyle: 'short' });
    if (kind === 'ram') {
      const gib = number / (1024 ** 3);
      return <>{formatNumber(locale, gib, { maximumFractionDigits: 2 })} GiB · {formatNumber(locale, number, { maximumFractionDigits: 0 })} bytes
        <small>{t(stale ? 'hardware.stale' : 'hardware.fresh')} · {t('hardware.sampledAt', { time: stamp })}
          {stale ? ` · ${reason(staleReason)}` : ''}</small></>;
    }
    return <>{formatNumber(locale, number, { maximumFractionDigits: 0 })}
      <small>{t(stale ? 'hardware.stale' : 'hardware.fresh')} · {t('hardware.sampledAt', { time: stamp })}
        {stale ? ` · ${reason(staleReason)}` : ''}</small></>;
  };

  return <Card title={t('hardware.title')}>
    <p>{t('hardware.description')}</p>
    {!targetReady && <p role="status">{t('hardware.noTarget')}</p>}
    {targetReady && snapshot.target && !snapshot.target.isLocal && <p role="status">{t('hardware.remoteUnavailable')}</p>}
    {selectedFolder && <p>{t('hardware.volumeSelected', { name: selectedFolder })}</p>}
    {hardware && facts ? <>
      <div className="metric-table" role="region" tabIndex={0} aria-label={t('hardware.title')}>
        <table><tbody>
          <tr><th scope="row">{t('hardware.ram')}</th><td>{reading(facts.ramBytes, 'ram')}</td></tr>
          <tr><th scope="row">{t('hardware.logicalCpu')}</th><td>{reading(facts.logicalCpuCount, 'count')}</td></tr>
          <tr><th scope="row">{t('hardware.performanceCpu')}</th><td>{reading(facts.performanceCpuCount, 'count')}</td></tr>
          <tr><th scope="row">{t('hardware.storage')}</th><td>{facts.storageType ? t(`hardware.storage.${facts.storageType}`) : <>{t('hardware.unavailable')} <small>{reason(facts.storageReasonCode)}</small></>}</td></tr>
        </tbody></table>
      </div>
      {hardware.reasonCodes.map(code => <p key={code} role="status">{reason(code)}</p>)}
    </> : <p role="status">{t('hardware.noResult')}</p>}
    {error && <p className="field-error" role="alert">{t('hardware.actionFailed')}</p>}
    <div className="form-actions">
      <button type="button" disabled={!targetReady || busy} onClick={() => void detect(null)}>{pending ? t('hardware.detecting') : t('hardware.detect')}</button>
      <button type="button" disabled={!targetReady || !snapshot.target?.isLocal || busy} onClick={() => void chooseVolume()}>{t('hardware.chooseVolume')}</button>
      <button type="button" disabled={!adoptable} onClick={() => hardware?.inputs && onAdopt(hardware.inputs)}>{t('hardware.adopt')}</button>
    </div>
    <p>{t(adoptable ? 'hardware.adoptHint' : 'hardware.adoptUnavailable')}</p>
  </Card>;
}
