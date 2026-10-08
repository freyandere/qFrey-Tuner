import { useEffect, useRef, useState } from 'react';
import type { AppSnapshot, Locale, ThemePreference } from './contracts/protocol';
import { sendCommand, sendOperation, selectNativeFile, subscribeSnapshots, listHistory, readCycle } from './bridge/client';
import { translate } from './i18n/messages';
import { ConnectionForm } from './components/ConnectionForm';
import { LiveMetrics } from './components/LiveMetrics';
import { SetupInputs } from './components/SetupInputs';
import { RecommendationList } from './components/RecommendationList';
import { HistoryList } from './components/HistoryList';
import { ResultCards } from './components/ResultCards';
import { ExperimentFlow } from './components/ExperimentFlow';
import { MutationControls, type MutationAction } from './components/MutationControls';
import { HardwareDetectionPanel } from './components/HardwareDetectionPanel';
import { WorkloadPreparationPanel } from './components/WorkloadPreparationPanel';
import { LegacyRestorePanel } from './components/LegacyRestorePanel';
import { OwnedWorkloadControls, type OwnedWorkloadAction } from './components/OwnedWorkloadControls';
import { NetworkTestPanel, isMeasuredNetworkSpeed } from './components/NetworkTestPanel';
import { TargetLifecyclePanel, type TargetLifecycleAction } from './components/TargetLifecyclePanel';
import type { DraftInputs, PlanSelection, HistoryPage, ExperimentSummary, WorkloadReference, ConfirmationSummary } from './contracts/domain';
const screens = ['overview', 'setup', 'recommendations', 'experiment', 'results', 'history'] as const;
export function App() {
  const [snapshot, setSnapshot] = useState<AppSnapshot | null>(null);
  const snapshotRef = useRef<AppSnapshot | null>(null);
  snapshotRef.current = snapshot;
  const [screen, setScreen] = useState<typeof screens[number]>('overview');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [endpointDirty, setEndpointDirty] = useState(false);
  const [historyPage, setHistoryPage] = useState<HistoryPage | null>(null);
  const [historyBusy, setHistoryBusy] = useState(false);
  const historyRequest = useRef(0);
  function navigate(next: typeof screens[number]) {
    if (next !== screen) {
      historyRequest.current++;
      setHistoryBusy(false);
      setScreen(next);
    }
    requestAnimationFrame(() => heading.current?.focus());
  }
  const [historical, setHistorical] = useState<ExperimentSummary | null>(null);
  const [exportNotice, setExportNotice] = useState<{ cycleId: string; name: string } | null>(null);
  const [draft, setDraft] = useState<DraftInputs>({
    network: { downloadMbps: 100, uploadMbps: 20, connectionType: 'cableDsl', useVpn: false, vpnInterface: '', ispThrottling: false, downloadSource: 'manual', uploadSource: 'manual' },
    hardware: { ramGiB: 16, cpuCores: 8, isHybridCpu: false, performanceCores: 0, storageType: 'hdd', source: 'manual' },
    usage: { trackerType: 'public', userRole: 'leecher', environment: 'system' }, proposedPort: null,
  });
  const heading = useRef<HTMLHeadingElement>(null);
  const locale: Locale = snapshot?.preferences.locale ?? (navigator.language.startsWith('ru') ? 'ru-RU' : 'en-US');
  const theme = snapshot?.preferences.theme ?? 'system';
  const t = (key: string) => translate(locale, key);
  async function initialize() {
    setError(null);
    try { const next = await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }); setSnapshot(next); if (next.experiment?.runInputs) setDraft(next.experiment.runInputs); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
  }
  useEffect(() => { void initialize(); }, []);
  useEffect(() => subscribeSnapshots(setSnapshot), []);
  useEffect(() => { if (screen === 'history' && snapshot) void loadHistory(null); }, [screen, Boolean(snapshot)]);
  async function loadHistory(cursor: string | null) {
    const request = ++historyRequest.current;
    setHistoryBusy(true); setError(null);
    try { const page = await listHistory(cursor); if (request === historyRequest.current) setHistoryPage(page); }
    catch (reason) { if (request === historyRequest.current) setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
    finally { if (request === historyRequest.current) setHistoryBusy(false); }
  }
  async function openCycle(cycleId: string) {
    const request = ++historyRequest.current;
    setHistoryBusy(true); setError(null);
    try { const cycle = await readCycle(cycleId); if (request === historyRequest.current) { setHistorical(cycle); navigate('results'); } }
    catch (reason) { if (request === historyRequest.current) setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
    finally { if (request === historyRequest.current) setHistoryBusy(false); }
  }
  useEffect(() => {
    document.documentElement.lang = locale;
    document.documentElement.dataset.theme = theme;
  }, [locale, theme]);
  async function preferences(nextLocale: Locale, nextTheme: ThemePreference) {
    setBusy(true); setError(null);
    try { setSnapshot(await sendCommand({ command: 'SetUiPreferences', payload: { locale: nextLocale, theme: nextTheme } })); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
    finally { setBusy(false); }
  }
  async function buildPlan(selections: PlanSelection[] = []) {
    if (endpointDirty || snapshot?.connection !== 'validated') { setError('errors.targetNotValidated'); return; }
    setBusy(true); setError(null);
    try { setSnapshot(await sendCommand({ command: 'BuildPlan', payload: { inputs: draft, selections } })); navigate('recommendations'); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
    finally { setBusy(false); }
  }
  async function startMeasurement(workload: WorkloadReference) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null); setBusy(true);
    try {
      await sendOperation({ command: 'StartMeasurement', payload: { kind: snapshot?.applyStatus === 'verified' ? 'after' : 'baseline', workload } });
      setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
    finally { setBusy(false); }
  }
  async function cancelOperation(operationId: string) {
    setError(null);
    try { setSnapshot(await sendCommand({ command: 'CancelOperation', payload: { operationId } })); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function approvePlan(planId: string, revision: number) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null);
    try { setSnapshot(await sendCommand({ command: 'AcceptPlan', payload: { planId, revision } })); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function requestMutationConfirmation(action: MutationAction, cycleId: string, planId: string): Promise<ConfirmationSummary> {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    const before = snapshotRef.current;
    const plan = before?.experiment?.plan;
    const targetSessionId = before?.target?.sessionId;
    if (!before || !plan || !targetSessionId || plan.id !== planId || before.experiment?.cycleId !== cycleId) throw new Error('errors.confirmationExpired');
    const existingTokens = new Set((before.confirmations ?? []).map(item => item.token));
    setError(null);
    try {
      const returned = await sendCommand({ command: 'RequestConfirmation', payload: { actionId: action, planId, cycleId, workloadId: null } });
      setSnapshot(returned);
      const returnedPlan = returned.experiment?.plan;
      if (returned.target?.sessionId !== targetSessionId || returnedPlan?.id !== planId || returnedPlan.revision !== plan.revision
        || returned.experiment?.cycleId !== cycleId) throw new Error('errors.confirmationExpired');
      const matches = (returned.confirmations ?? []).filter(item => !existingTokens.has(item.token)
        && item.actionId === action && item.targetSessionId === targetSessionId && item.revision === plan.revision
        && Number.isFinite(Date.parse(item.expiresUtc)) && Date.parse(item.expiresUtc) > Date.now());
      if (matches.length !== 1) throw new Error('errors.confirmationExpired');
      return matches[0]!;
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function reviewWorkload(catalogueId: string, serverSavePath: string): Promise<ConfirmationSummary> {
    const before = snapshotRef.current;
    if (endpointDirty || !before?.target || before.connection !== 'validated') throw new Error('errors.targetNotValidated');
    const existing = new Set((before.confirmations ?? []).map(item => item.token));
    const returned = await sendCommand({ command: 'RequestConfirmation', payload: {
      actionId: 'PrepareWorkload', planId: null, cycleId: null, workloadId: null, catalogueId, serverSavePath,
    } });
    setSnapshot(returned);
    const matches = (returned.confirmations ?? []).filter(item => !existing.has(item.token)
      && item.actionId === 'PrepareWorkload' && item.targetSessionId === before.target!.sessionId
      && item.revision === returned.revision && Date.parse(item.expiresUtc) > Date.now());
    if (returned.target?.sessionId !== before.target.sessionId || matches.length !== 1) throw new Error('errors.confirmationExpired');
    return matches[0]!;
  }
  async function prepareWorkload(catalogueId: string, serverSavePath: string, confirmationToken: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    await sendOperation({ command: 'PrepareWorkload', payload: { catalogueId, serverSavePath, approvalToken: confirmationToken } });
    setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
  }
  async function reviewTargetAction(actionId: OwnedWorkloadAction | TargetLifecycleAction | 'RunNetworkTest', workloadId: string | null = null): Promise<ConfirmationSummary> {
    const before = snapshotRef.current;
    if (endpointDirty || !before?.target || before.connection !== 'validated') throw new Error('errors.targetNotValidated');
    const tokens = new Set((before.confirmations ?? []).map(item => item.token));
    const returned = await sendCommand({ command: 'RequestConfirmation', payload: { actionId, planId: null, cycleId: null, workloadId } });
    setSnapshot(returned);
    const matches = (returned.confirmations ?? []).filter(item => !tokens.has(item.token)
      && item.actionId === actionId && item.targetSessionId === before.target!.sessionId
      && item.revision === before.revision && Date.parse(item.expiresUtc) > Date.now());
    if (returned.target?.sessionId !== before.target.sessionId || returned.revision !== before.revision || matches.length !== 1)
      throw new Error('errors.confirmationExpired');
    return matches[0]!;
  }
  async function operateOwnedWorkload(action: OwnedWorkloadAction, workloadId: string, confirmationToken: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    await sendOperation({ command: action, payload: { workloadId, confirmationToken } });
  }
  async function selectOwnedWorkload(workloadId: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setSnapshot(await sendCommand({ command: 'SelectOwnedWorkload', payload: { workloadId } }));
  }
  async function runNetworkTest(confirmationToken: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    await sendOperation({ command: 'RunNetworkTest', payload: { confirmationToken } });
  }
  async function operateTarget(action: TargetLifecycleAction, confirmationToken: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    await sendOperation({ command: action, payload: { confirmationToken } });
  }
  async function applyPlan(planId: string, planRevision: number, token: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null);
    try {
      await sendOperation({ command: 'ApplyPlan', payload: { planId, expectedRevision: planRevision, confirmationToken: token } });
      setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function rollback(cycleId: string, planRevision: number, token: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null);
    try {
      await sendOperation({ command: 'Rollback', payload: { cycleId, expectedRevision: planRevision, confirmationToken: token } });
      setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function keepChanges(cycleId: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null);
    try { setSnapshot(await sendCommand({ command: 'KeepChanges', payload: { cycleId, applyStatus: 'verified' } })); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); throw reason; }
  }
  async function detectHardware(volumeToken: string | null) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    setError(null);
    await sendOperation({ command: 'DetectLocalHardware', payload: { volumeToken } });
    setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
  }
  async function reviewLegacyBackup() {
    if (endpointDirty || snapshotRef.current?.connection !== 'validated') throw new Error('errors.targetNotValidated');
    const selected = await selectNativeFile('restore');
    if (!selected) return;
    setSnapshot(await sendCommand({ command: 'RequestConfirmation', payload: {
      actionId: 'RestoreLegacyBackup', planId: null, cycleId: null, workloadId: null, selectionToken: selected.token,
    } }));
  }
  async function restoreLegacyBackup(selectionToken: string, confirmationToken: string) {
    if (endpointDirty) throw new Error('errors.targetNotValidated');
    await sendOperation({ command: 'RestoreLegacyBackup', payload: { selectionToken, confirmationToken } });
    setSnapshot(await sendCommand({ command: 'Initialize', payload: { protocolVersion: 1 } }));
  }
  async function exportReport(format: 'json' | 'html') {
    const cycleId = (historical ?? snapshot?.experiment)?.cycleId;
    if (!cycleId) return;
    setBusy(true); setError(null); setExportNotice(null);
    try {
      const selected = await selectNativeFile(format === 'json' ? 'exportJson' : 'exportHtml');
      if (!selected) return;
      setSnapshot(await sendCommand({ command: 'ExportReport', payload: { cycleId, format, destinationToken: selected.token, locale } }));
      setExportNotice({ cycleId, name: selected.displayName });
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'errors.unknown'); }
    finally { setBusy(false); }
  }
  return <div className="app-shell">
    <header><div className="brand">qFrey-Tuner <small>{snapshot?.appVersion}</small></div><span className="connection">{t(snapshot?.connection === 'validated' ? 'connection.validated' : 'disconnected')}</span>
      <div className="preferences"><label>{t('language')}<select aria-label={t('language')} value={locale} disabled={!snapshot || busy} onChange={e => void preferences(e.target.value as Locale, theme)}><option value="ru-RU">Русский</option><option value="en-US">English</option></select></label>
      <label>{t('theme')}<select aria-label={t('theme')} value={theme} disabled={!snapshot || busy} onChange={e => void preferences(locale, e.target.value as ThemePreference)}>{(['system', 'dark', 'light'] as const).map(value => <option key={value} value={value}>{t(value)}</option>)}</select></label></div>
    </header>
    <div className="workspace"><nav aria-label="qFrey-Tuner">{screens.map(id => <button key={id} aria-current={screen === id ? 'page' : undefined} onClick={() => navigate(id)}>{t(id)}</button>)}</nav>
      <main><p className="eyebrow">{t(screen)}</p><h1 ref={heading} tabIndex={-1}>{screen === 'overview' ? t('title') : t(screen)}</h1>
        <p className="intro">{t('introduction')}</p>
        {snapshot?.activeOperation && <section className="notice" role="status" aria-label={t('experiment.operation.title')}>
          <strong>{t('experiment.operation.title')}</strong>
          {snapshot.activeOperation.progress === null ? <progress aria-label={t('experiment.operation.progress')} />
            : <progress aria-label={t('experiment.operation.progress')} max={100} value={snapshot.activeOperation.progress} />}
          {snapshot.activeOperation.cancellable && !(screen === 'experiment' && snapshot.activeOperation.kind === 'measurement')
            && <button onClick={() => void cancelOperation(snapshot.activeOperation!.id).catch(() => {})}>{t('experiment.operation.cancelAny')}</button>}
        </section>}
        {error && <div role="alert" className="error"><p>{t(error)}</p><button onClick={() => void initialize()}>{t('retry')}</button></div>}
        {snapshot?.availableActions.filter(action => action.id === 'operation.error').map(action =>
          <p className="error" role="alert" key={action.id}>{t(action.messageKey)}</p>)}
        {snapshot?.availableActions.filter(action => action.id === 'restore.completed').map(action =>
          <p className="notice" role="status" key={action.id}>{t(action.messageKey)}</p>)}
        {!snapshot && !error && <p role="status">{t('loading')}</p>}
        {snapshot && (screen === 'overview' || screen === 'setup') && <ConnectionForm snapshot={snapshot} onSnapshot={setSnapshot} onError={setError} onEndpointEdited={() => setEndpointDirty(true)} onConnected={() => setEndpointDirty(false)} />}
        {snapshot && screen === 'overview' && <LiveMetrics snapshot={snapshot} />}
        {snapshot && screen === 'overview' && !endpointDirty && <TargetLifecyclePanel snapshot={snapshot}
          onReview={reviewTargetAction} onRun={operateTarget} />}
        {snapshot && <div className="setup-inputs" hidden={screen !== 'setup'}><SetupInputs valueDraftInputs={draft} onChange={setDraft} onBuild={() => void buildPlan()} busy={busy || snapshot.activeOperation !== null} canBuild={snapshot.connection === 'validated' && !endpointDirty} locale={locale} /></div>}
        {snapshot && screen === 'setup' && !endpointDirty && <HardwareDetectionPanel snapshot={snapshot} onDetect={detectHardware}
          onSelectVolume={() => selectNativeFile('volume')} onAdopt={hardware => setDraft(current => ({ ...current, hardware }))} />}
        {snapshot && screen === 'setup' && !endpointDirty && <NetworkTestPanel snapshot={snapshot}
          onReview={() => reviewTargetAction('RunNetworkTest')} onRun={runNetworkTest}
          onUse={result => setDraft(current => ({ ...current, network: { ...current.network,
            ...(isMeasuredNetworkSpeed(result.downloadBytesPerSecond) ? { downloadMbps: result.downloadBytesPerSecond / 1_000_000 * 8, downloadSource: 'measured' as const } : {}),
            ...(isMeasuredNetworkSpeed(result.uploadBytesPerSecond) ? { uploadMbps: result.uploadBytesPerSecond / 1_000_000 * 8, uploadSource: 'measured' as const } : {}),
          } }))} />}
        {snapshot && screen === 'recommendations' && !snapshot.experiment?.plan && <div className="form-actions"><button onClick={() => navigate('setup')}>{t('navigation.setup')}</button></div>}
        {snapshot && screen === 'recommendations' && <RecommendationList plan={snapshot.experiment?.plan ?? null} onSelections={selections => void buildPlan(selections)} busy={busy || snapshot.activeOperation !== null || endpointDirty || snapshot.connection !== 'validated'} locale={locale} />}
        {snapshot && screen === 'experiment' && <>
          <ExperimentFlow snapshot={snapshot} onStart={startMeasurement} onCancel={cancelOperation} />
          {!endpointDirty && <WorkloadPreparationPanel snapshot={snapshot} onReview={reviewWorkload} onPrepare={prepareWorkload} />}
          {!endpointDirty && <OwnedWorkloadControls snapshot={snapshot} onReview={reviewTargetAction}
            onAction={operateOwnedWorkload} onSelect={selectOwnedWorkload} />}
          <MutationControls snapshot={snapshot} onApprove={approvePlan} onRequest={requestMutationConfirmation}
            onApply={applyPlan} onRollback={rollback} onKeep={keepChanges} />
        </>}
        {snapshot && screen === 'history' && <HistoryList page={historyPage} locale={locale} busy={historyBusy} onNext={() => void loadHistory(historyPage?.nextCursor ?? null)} onOpen={id => void openCycle(id)} />}
        {snapshot && screen === 'history' && !endpointDirty && <LegacyRestorePanel snapshot={snapshot} onReview={reviewLegacyBackup} onRestore={restoreLegacyBackup} />}
        {snapshot && screen === 'results' && <>{!(historical ?? snapshot.experiment)?.results.length && <div className="form-actions"><button onClick={() => navigate('experiment')}>{t('navigation.experiment')}</button><button onClick={() => navigate('history')}>{t('navigation.history')}</button></div>}{historical && <button onClick={() => setHistorical(null)}>{t('results.currentExperiment')}</button>}<ResultCards summary={historical ?? snapshot.experiment ?? null} locale={locale} historical={historical !== null} />
          {(historical ?? snapshot.experiment)?.cycleId && <div className="form-actions">
            <button disabled={busy} onClick={() => void exportReport('json')}>{t('reports.exportJson')}</button>
            <button disabled={busy} onClick={() => void exportReport('html')}>{t('reports.exportHtml')}</button>
          </div>}
          {exportNotice && exportNotice.cycleId === (historical ?? snapshot.experiment)?.cycleId
            && <p role="status">{translate(locale, 'reports.saved', { name: exportNotice.name })}</p>}
        </>}
      </main>
    </div>
  </div>;
}
