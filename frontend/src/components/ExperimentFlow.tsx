import { useState } from 'react';
import type { WorkloadReference } from '../contracts/domain';
import type { AppSnapshot, ExperimentPhase } from '../contracts/protocol';
import { parseExistingHashes, translateExperiment, translateExperimentStage } from './experimentMessages';

type Props = {
  snapshot: AppSnapshot;
  onStart: (workload: WorkloadReference) => Promise<void>;
  onCancel: (operationId: string) => Promise<void>;
};

const steps = ['preparation', 'baseline', 'plan', 'apply', 'after', 'comparison'] as const;
const phaseStep: Record<ExperimentPhase, typeof steps[number]> = {
  draft: 'baseline', baselineReady: 'plan', planReady: 'plan', applying: 'apply', appliedVerified: 'after',
  afterReady: 'comparison', completed: 'comparison', recoveryRequired: 'apply', rolledBack: 'comparison'
};

export function measurementWorkloadReference(afterMode: boolean, frozenReference: WorkloadReference | null, hashes: string[], newId: () => string = () => globalThis.crypto.randomUUID()): WorkloadReference | null {
  if (afterMode) return frozenReference?.kind === 'existing' ? { ...frozenReference, hashes: [...frozenReference.hashes] } : null;
  return { id: newId(), kind: 'existing', hashes: [...hashes] };
}

export function ExperimentFlow({ snapshot, onStart, onCancel }: Props) {
  const locale = snapshot.preferences.locale;
  const t = (key: string, parameters?: Record<string, string | number>) => translateExperiment(locale, key, parameters);
  const [hashText, setHashText] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const experiment = snapshot.experiment;
  const frozenReference = experiment?.workload?.reference ?? null;
  const frozenHashesText = frozenReference?.hashes.join(' ') ?? '';
  const parsedFrozenHashes = parseExistingHashes(frozenHashesText);
  const baselineValid = experiment?.baseline?.status === 'valid';
  const afterValid = experiment?.after?.status === 'valid';
  const afterMode = snapshot.applyStatus === 'verified';
  const hasComparableExistingWorkload = frozenReference?.kind === 'existing' && parsedFrozenHashes.valid;
  const hashes = afterMode ? parsedFrozenHashes : parseExistingHashes(hashText);
  const targetReady = snapshot.connection === 'validated' && snapshot.target !== null;
  const inputsReady = experiment?.runInputs !== null && experiment?.runInputs !== undefined;
  const operation = snapshot.activeOperation;
  const operationBusy = operation !== null;
  const ready = targetReady && inputsReady && !operationBusy && !pending && hashes.valid;
  const startDisabled = !ready || (afterMode ? !hasComparableExistingWorkload || !baselineValid || afterValid : baselineValid);
  const stageIndex = steps.indexOf(phaseStep[snapshot.phase]);

  const start = async () => {
    setSubmitted(true);
    setError('');
    setNotice('');
    const selected = afterMode ? parsedFrozenHashes : parseExistingHashes(hashText);
    if (!selected.valid || !targetReady || !inputsReady || operationBusy || pending
      || (afterMode && (!hasComparableExistingWorkload || !baselineValid || afterValid)) || (!afterMode && baselineValid)) return;
    setPending(true);
    try {
      const workload = measurementWorkloadReference(afterMode, frozenReference, selected.hashes);
      if (workload) await onStart(workload);
    } catch {
      setError(t('experiment.startFailed'));
    } finally {
      setPending(false);
    }
  };

  const cancel = async () => {
    if (!operation || operation.kind !== 'measurement' || !operation.cancellable) return;
    setNotice('');
    setError('');
    try {
      await onCancel(operation.id);
      setNotice(t('experiment.operation.cancelRequested'));
    } catch {
      setError(t('experiment.operation.cancelFailed'));
    }
  };

  const progress = operation?.kind === 'measurement' && operation.progress !== null
    && Number.isFinite(operation.progress) && operation.progress >= 0 && operation.progress <= 100 ? operation.progress : null;

  return <section className="card experiment-flow" aria-labelledby="experiment-flow-title">
    <h2 id="experiment-flow-title">{t('experiment.title')}</h2>
    <p>{t('experiment.description')}</p>
    <ol aria-label={t('experiment.title')} className="experiment-steps">
      {steps.map((step, index) => <li key={step} aria-current={index === stageIndex ? 'step' : undefined}>
        {t(`experiment.steps.${step}`)}
      </li>)}
    </ol>

    <section aria-labelledby="experiment-status-title">
      <h3 id="experiment-status-title">{t('experiment.status.title')}</h3>
      <p>{t(`experiment.phase.${snapshot.phase}`)}</p>
      <dl>
        <dt>{t('experiment.status.baseline')}</dt>
        <dd>{t(experiment?.baseline ? `experiment.measurement.${experiment.baseline.status}` : 'experiment.status.notMeasured')}</dd>
        <dt>{t('experiment.status.after')}</dt>
        <dd>{t(experiment?.after ? `experiment.measurement.${experiment.after.status}` : 'experiment.status.notMeasured')}</dd>
        <dt>{t('experiment.apply.title')}</dt>
        <dd>{t(`experiment.apply.${snapshot.applyStatus}`)}</dd>
      </dl>
      {targetReady && snapshot.target && <p>{snapshot.target.endpoint} · {snapshot.target.qbittorrentVersion}</p>}
      {!targetReady && <p role="status">{t('experiment.targetRequired')}</p>}
      {!inputsReady && <p role="status">{t('experiment.inputsRequired')}</p>}
    </section>

    {operationBusy && <section aria-labelledby="experiment-operation-title" role="status">
      <h3 id="experiment-operation-title">{t('experiment.operation.title')}</h3>
      <p><strong>{t('experiment.operation.stage')}:</strong> {translateExperimentStage(locale, operation.stage)}</p>
      {operation.kind === 'measurement' && <>
        {progress === null
          ? <><progress aria-label={t('experiment.operation.progress')} /><p>{t('experiment.operation.indeterminate')}</p></>
          : <><progress aria-label={t('experiment.operation.progress')} max={100} value={progress} /><p>{t('experiment.operation.progress')}: {progress}%</p></>}
        {operation.cancellable && <button type="button" onClick={cancel}>{t('experiment.operation.cancel')}</button>}
        {!operation.cancellable && <p>{t('experiment.operation.notCancellable')}</p>}
      </>}
    </section>}

    <div className="field">
      <label htmlFor="experiment-hashes">{t(afterMode ? 'experiment.afterHashes.label' : 'experiment.hashes.label')}</label>
      <textarea id="experiment-hashes" rows={4} value={afterMode ? frozenHashesText : hashText} onChange={event => setHashText(event.target.value)}
        readOnly={afterMode} aria-describedby="experiment-hashes-hint" aria-invalid={submitted && !hashes.valid} disabled={operationBusy || pending} />
      <p id="experiment-hashes-hint" className="field-hint">{t('experiment.hashes.hint')}</p>
      {(afterMode ? frozenHashesText : hashText).trim() && hashes.valid && <p>{t('experiment.hashes.count', { count: hashes.hashes.length })}</p>}
      {submitted && !hashes.valid && <p className="field-error" role="alert">{t('experiment.hashes.invalid')}</p>}
    </div>

    {afterMode && !baselineValid && <p role="status">{t('experiment.afterNeedsBaseline')}</p>}
    {afterMode && afterValid && <p role="status">{t('experiment.afterExists')}</p>}
    {afterMode && !hasComparableExistingWorkload && <p role="status">{t('experiment.afterWorkloadMissing')}</p>}
    {snapshot.phase === 'rolledBack' && <p role="status">{t('experiment.rolledBackNewCycle')}</p>}
    {!afterMode && baselineValid && snapshot.phase !== 'rolledBack' && <p role="status">{t('experiment.baselineExists')}</p>}
    {error && <p className="field-error" role="alert">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    <button type="button" disabled={startDisabled} onClick={start}>
      {pending ? t('experiment.starting') : t(afterMode ? 'experiment.start.after' : 'experiment.start.baseline')}
    </button>

  </section>;
}
