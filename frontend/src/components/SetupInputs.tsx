import { useEffect, useState } from 'react';
import type { DraftInputs, InputSource } from '../contracts/domain';
import type { Locale } from '../contracts/protocol';
import { parseDecimal, translate } from '../i18n/messages';
import { Card } from './base';

type Props = { valueDraftInputs: DraftInputs; onChange: (value: DraftInputs) => void; onBuild: () => void; busy: boolean; canBuild: boolean; locale: Locale };

function NumericField({ id, label, value, locale, min, max, integer = false, optional = false, hint, disabled = false, onValidChange, onValidityChange }:
  { id: string; label: string; value: number | null; locale: Locale; min: number; max: number; integer?: boolean; optional?: boolean; hint?: string;
    disabled?: boolean; onValidChange: (value: number | null) => void; onValidityChange: (valid: boolean) => void }) {
  const [text, setText] = useState(value === null ? '' : String(value));
  const parsed = optional && text.trim() === '' ? null : parseDecimal(text);
  const valid = parsed === null ? optional && text.trim() === '' : parsed >= min && parsed <= max && (!integer || Number.isInteger(parsed));
  useEffect(() => { setText(value === null ? '' : String(value)); }, [value]);
  useEffect(() => { onValidityChange(valid); }, [valid, onValidityChange]);
  const commit = () => { if (valid) onValidChange(parsed); };
  return <label className="field" htmlFor={id}>{label}<input id={id} type="text" inputMode={integer ? 'numeric' : 'decimal'} value={text} disabled={disabled}
    aria-invalid={!valid} aria-describedby={hint ? `${id}-hint` : undefined}
    onChange={event => {
      const next = event.target.value; setText(next);
      const number = optional && next.trim() === '' ? null : parseDecimal(next);
      const nextValid = number === null ? optional && next.trim() === '' : number >= min && number <= max && (!integer || Number.isInteger(number));
      onValidityChange(nextValid);
    }} onBlur={commit} onKeyDown={event => { if (event.key === 'Enter') { event.preventDefault(); commit(); } }} />{hint && <small id={`${id}-hint`}>{hint}</small>}</label>;
}

export function SetupInputs({ valueDraftInputs, onChange, onBuild, busy, canBuild, locale }: Props) {
  const t = (key: string) => translate(locale, key);
  const [validity, setValidity] = useState<Record<string, boolean>>({});
  const setValid = (key: string) => (valid: boolean) => setValidity(old => old[key] === valid ? old : { ...old, [key]: valid });
  const input = valueDraftInputs;
  const setNetwork = (patch: Partial<DraftInputs['network']>) => onChange({ ...input, network: { ...input.network, ...patch,
    ...(Object.hasOwn(patch, 'downloadMbps') ? { downloadSource: 'manual' as const } : {}),
    ...(Object.hasOwn(patch, 'uploadMbps') ? { uploadSource: 'manual' as const } : {}) } });
  const setHardware = (patch: Partial<DraftInputs['hardware']>) => onChange({ ...input, hardware: { ...input.hardware, ...patch, source: 'manual' } });
  const setUsage = (patch: Partial<DraftInputs['usage']>) => onChange({ ...input, usage: { ...input.usage, ...patch } });
  const source = (value: InputSource) => <small>{t(`setup.source.${value}`)}</small>;
  const invalid = Object.values(validity).some(valid => !valid);
  return <Card title={t('setup.title')}>
    <form className="setup-form" onSubmit={event => { event.preventDefault(); if (!busy && canBuild && !invalid) onBuild(); }}>
      <p>{t('setup.defaultsHint')}</p>
      <fieldset disabled={busy} className="setup-group"><legend>{t('setup.network')}</legend>
        <NumericField id="setup-download" label={t('setup.downloadMbps')} value={input.network.downloadMbps} locale={locale} min={Number.MIN_VALUE} max={100000}
          onValidChange={downloadMbps => downloadMbps !== null && setNetwork({ downloadMbps })} onValidityChange={setValid('download')} />
        <label className="field">{t('setup.downloadSource')}{source(input.network.downloadSource)}</label>
        <NumericField id="setup-upload" label={t('setup.uploadMbps')} value={input.network.uploadMbps} locale={locale} min={Number.MIN_VALUE} max={100000}
          onValidChange={uploadMbps => uploadMbps !== null && setNetwork({ uploadMbps })} onValidityChange={setValid('upload')} />
        <label className="field">{t('setup.uploadSource')}{source(input.network.uploadSource)}</label>
        <label className="field">{t('setup.connectionType')}<select value={input.network.connectionType} onChange={event => setNetwork({ connectionType: event.target.value as DraftInputs['network']['connectionType'] })}>
          {(['fiber', 'cableDsl', 'mobile4G'] as const).map(value => <option key={value} value={value}>{t(`setup.${value}`)}</option>)}</select></label>
        <label><input type="checkbox" checked={input.network.useVpn} onChange={event => setNetwork({ useVpn: event.target.checked })} />{t('setup.useVpn')}</label>
        {input.network.useVpn && <label className="field">{t('setup.vpnInterface')}<input value={input.network.vpnInterface} maxLength={256} onChange={event => setNetwork({ vpnInterface: event.target.value })} />
          <small>{t('setup.vpnInterfaceHint')}</small></label>}
        <label><input type="checkbox" checked={input.network.ispThrottling} onChange={event => setNetwork({ ispThrottling: event.target.checked })} />{t('setup.ispThrottling')}</label>
      </fieldset>
      <fieldset disabled={busy} className="setup-group"><legend>{t('setup.hardware')}</legend>
        <label className="field">{t('setup.storageType')}<select value={input.hardware.storageType} onChange={event => setHardware({ storageType: event.target.value as DraftInputs['hardware']['storageType'] })}>
          {(['hdd', 'ssdSata', 'nvme'] as const).map(value => <option key={value} value={value}>{t(`setup.storage.${value}`)}</option>)}</select></label>
        <NumericField id="setup-ram" label={t('setup.ramGiB')} value={input.hardware.ramGiB} locale={locale} min={1} max={1048576} integer
          onValidChange={ramGiB => ramGiB !== null && setHardware({ ramGiB })} onValidityChange={setValid('ram')} />
        <NumericField id="setup-cpu" label={t('setup.cpuCores')} value={input.hardware.cpuCores} locale={locale} min={1} max={4096} integer
          onValidChange={cpuCores => cpuCores !== null && setHardware({ cpuCores, performanceCores: Math.min(input.hardware.performanceCores, cpuCores) })} onValidityChange={setValid('cpu')} />
        <label><input type="checkbox" checked={input.hardware.isHybridCpu} onChange={event => setHardware({ isHybridCpu: event.target.checked })} />{t('setup.isHybridCpu')}</label>
        {input.hardware.isHybridCpu && <NumericField id="setup-performance-cores" label={t('setup.performanceCores')} value={input.hardware.performanceCores} locale={locale} min={0} max={input.hardware.cpuCores} integer
          onValidChange={performanceCores => performanceCores !== null && setHardware({ performanceCores })} onValidityChange={setValid('performanceCores')} />}
        <label className="field">{t('setup.hardwareSource')}{source(input.hardware.source)}</label>
      </fieldset>
      <fieldset disabled={busy} className="setup-group"><legend>{t('setup.usage')}</legend>
        <label className="field">{t('setup.trackerType')}<select value={input.usage.trackerType} onChange={event => setUsage({ trackerType: event.target.value as DraftInputs['usage']['trackerType'] })}>
          {(['public', 'private'] as const).map(value => <option key={value} value={value}>{t(`setup.tracker.${value}`)}</option>)}</select></label>
        <label className="field">{t('setup.userRole')}<select value={input.usage.userRole} onChange={event => setUsage({ userRole: event.target.value as DraftInputs['usage']['userRole'] })}>
          {(['leecher', 'seeder', 'uploader'] as const).map(value => <option key={value} value={value}>{t(`setup.role.${value}`)}</option>)}</select></label>
        <label className="field">{t('setup.environment')}<select value={input.usage.environment} onChange={event => setUsage({ environment: event.target.value as DraftInputs['usage']['environment'] })}>
          {(['system', 'portable', 'truenas', 'nas', 'docker', 'seedbox'] as const).map(value => <option key={value} value={value}>{t(`setup.environment.${value}`)}</option>)}</select></label>
      </fieldset>
      <NumericField id="setup-port" label={t('setup.proposedPort')} value={input.proposedPort} locale={locale} min={49152} max={65535} integer optional disabled={busy}
        hint={t('setup.proposedPortHint')} onValidChange={proposedPort => onChange({ ...input, proposedPort })} onValidityChange={setValid('port')} />
      {invalid && <p className="field-error" role="alert">{t('setup.invalid')}</p>}
      <div className="form-actions"><button type="submit" disabled={busy || !canBuild || invalid}>{t(busy ? 'setup.building' : 'setup.build')}</button></div>
    </form>
  </Card>;
}
