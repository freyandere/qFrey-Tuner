import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { AppSnapshot } from '../src/contracts/protocol';
import { ExperimentFlow, measurementWorkloadReference } from '../src/components/ExperimentFlow';
import { enExperimentMessages, parseExistingHashes, ruExperimentMessages, translateExperiment, translateExperimentStage } from '../src/components/experimentMessages';

const inputs = {
  network: { downloadMbps: 500, uploadMbps: 100, connectionType: 'fiber' as const, useVpn: false, vpnInterface: '',
    ispThrottling: false, downloadSource: 'manual' as const, uploadSource: 'manual' as const },
  hardware: { storageType: 'nvme' as const, ramGiB: 16, cpuCores: 8, isHybridCpu: false, performanceCores: 8, source: 'manual' as const },
  usage: { trackerType: 'public' as const, userRole: 'leecher' as const, environment: 'system' as const }, proposedPort: null
};
const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 1,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session', endpoint: 'http://localhost:8080', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: true },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [], availableActions: [],
  experiment: { cycleId: null, runInputs: inputs, workload: null, baseline: null, after: null, plan: null, results: [] },
  ...overrides
});
const render = (value: AppSnapshot) => renderToStaticMarkup(createElement(ExperimentFlow, {
  snapshot: value, onStart: async () => {}, onCancel: async () => {}
}));
const hashA = 'a'.repeat(40);
const hashB = 'b'.repeat(40);

describe('ExperimentFlow', () => {
  it('validates unique 40-character v1 hashes and enforces the count bounds', () => {
    expect(parseExistingHashes(` ${hashA.toUpperCase()}\n${hashB} `)).toEqual({ hashes: [hashA, hashB], valid: true });
    expect(parseExistingHashes(`${hashA} ${hashA.toUpperCase()}`).valid).toBe(false);
    expect(parseExistingHashes('not-a-hash').valid).toBe(false);
    expect(parseExistingHashes('').valid).toBe(false);
    const maximum = Array.from({ length: 5000 }, (_, index) => index.toString(16).padStart(40, '0')).join(' ');
    expect(parseExistingHashes(maximum).valid).toBe(true);
    expect(parseExistingHashes(`${maximum} ${'f'.repeat(40)}`).valid).toBe(false);
  });

  it('requires a validated target and draft inputs and explains unavailable actions in Russian', () => {
    const value = snapshot({
      preferences: { locale: 'ru-RU', theme: 'system' }, connection: 'disconnected', target: null,
      experiment: { cycleId: null, runInputs: null, workload: null, baseline: null, after: null, plan: null, results: [] }
    });
    const html = render(value);
    expect(html).toContain('Перед замером подключитесь');
    expect(html).toContain('Заполните и проверьте черновые условия');
    expect(html).not.toContain('Managed test download');
    expect(html).toContain('Не применено');
    expect(html).not.toContain('experiment-apply-disabled');
    expect(html).toContain('disabled=""');
  });

  it('offers after measurement only when application is verified and baseline is valid', () => {
    const verified = snapshot({
      applyStatus: 'verified',
      experiment: { cycleId: null, runInputs: inputs, workload: null,
        baseline: { id: 'baseline', kind: 'baseline', status: 'valid', startedUtc: '2026-10-07T00:00:00Z', analysisVersion: '1', scope: 'workload', reasonCodes: [], sampleCount: 5, durationMs: 5000,
          meanDownload: { quality: 'notMeasured', reasonCodes: [] }, medianDownload: { quality: 'notMeasured', reasonCodes: [] }, standardDeviation: { quality: 'notMeasured', reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured', reasonCodes: [] } },
        after: null, plan: null, results: [] }
    });
    const html = render(verified);
    expect(html).toContain('Start after measurement');
    expect(html).not.toContain('Start baseline measurement');
    expect(html).toContain('Verified');

    const noBaseline = snapshot({ applyStatus: 'verified' });
    expect(render(noBaseline)).toContain('A valid baseline is required');
    expect(render(noBaseline)).toContain('disabled=""');
  });

  it('uses the frozen existing workload hashes for an after measurement', () => {
    expect(measurementWorkloadReference(true, { id: 'frozen-id', kind: 'existing', hashes: [hashA] }, [hashB]))
      .toEqual({ id: 'frozen-id', kind: 'existing', hashes: [hashA] });
    expect(measurementWorkloadReference(false, null, [hashB], () => 'new-id'))
      .toEqual({ id: 'new-id', kind: 'existing', hashes: [hashB] });
    const frozen = snapshot({
      applyStatus: 'verified',
      experiment: { cycleId: null, runInputs: inputs,
        workload: { reference: { id: 'frozen-id', kind: 'existing', hashes: [hashA] }, name: 'Existing', totalBytesDecimal: '1',
          serverSavePath: '/', catalogueId: null, ownershipVerified: false, reasonCodes: [] },
        baseline: { id: 'baseline', kind: 'baseline', status: 'valid', startedUtc: '2026-10-07T00:00:00Z', analysisVersion: '1', scope: 'workload', reasonCodes: [], sampleCount: 5, durationMs: 5000,
          meanDownload: { quality: 'notMeasured', reasonCodes: [] }, medianDownload: { quality: 'notMeasured', reasonCodes: [] }, standardDeviation: { quality: 'notMeasured', reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured', reasonCodes: [] } },
        after: null, plan: null, results: [] }
    });
    const html = render(frozen);
    expect(html).toContain('Frozen baseline workload hashes');
    expect(html).toContain(`>${hashA}</textarea>`);
    expect(html).toContain('readOnly=""');
  });

  it('measures an authoritative owned reference without manually entered hashes or implicit download start', () => {
    const reference = { id: 'owned-id', kind: 'owned' as const, hashes: [hashA] };
    const owned = snapshot({ experiment: { ...snapshot().experiment!, workload: {
      reference, name: 'Official test image', totalBytesDecimal: '1234', serverSavePath: '/test',
      catalogueId: 'test', ownershipVerified: true, reasonCodes: [],
    } } });
    const html = render(owned);
    expect(html).toContain('Official test image');
    expect(html).toContain('Measurement does not start, stop or delete');
    expect(html).not.toContain('<textarea');
    expect(html).not.toContain('disabled=""');
    expect(measurementWorkloadReference(false, reference, [hashB], () => 'unused', reference)).toEqual(reference);
    expect(measurementWorkloadReference(true, reference, [hashB], () => 'unused', reference)).toEqual(reference);
    expect(measurementWorkloadReference(true, { ...reference, id: 'other' }, [], () => 'unused', reference)).toBeNull();
    expect(measurementWorkloadReference(true, reference, [], () => 'unused', { ...reference, hashes: [hashB] })).toBeNull();
    expect(render({ ...owned, experiment: { ...owned.experiment!, workload: {
      ...owned.experiment!.workload!, ownershipVerified: false,
    } } })).toContain('disabled=""');
    for (const blocked of [{ ...owned, phase: 'recoveryRequired' as const }, { ...owned, applyStatus: 'unverified' as const }]) {
      expect(render(blocked)).not.toContain('Start baseline measurement');
      expect(render(blocked)).toContain('Measurements are blocked');
    }
  });

  it('treats a rolled-back cycle as historical and directs the user to a new plan and cycle', () => {
    const rolledBack = snapshot({
      preferences: { locale: 'ru-RU', theme: 'light' }, phase: 'rolledBack', applyStatus: 'reverted',
      experiment: { ...snapshot().experiment!, baseline: {
        id: 'baseline', kind: 'baseline', status: 'valid', startedUtc: '2026-10-07T00:00:00Z', analysisVersion: '1', scope: 'workload',
        reasonCodes: [], sampleCount: 70, durationMs: 70000,
        meanDownload: { quality: 'notMeasured', reasonCodes: [] }, medianDownload: { quality: 'notMeasured', reasonCodes: [] },
        standardDeviation: { quality: 'notMeasured', reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured', reasonCodes: [] }
      } }
    });
    const html = render(rolledBack);
    expect(html).toContain('Этот цикл завершён откатом. Замеры сохранены в истории. Для нового исходного замера нужен новый план и цикл; не применяйте повторно план этого цикла.');
    expect(html).toContain('<li aria-current="step">Сравнение</li>');
    expect(html).not.toContain('Перед повторным замером проверьте план и подтвердите применение.');
    expect(html).not.toContain('<li aria-current="step">4. Применение</li>');
  });

  it('shows only reported stage/progress and exposes cancellation only when allowed', () => {
    const operation = { id: 'op', kind: 'measurement' as const, stage: 'sampling', progress: 37, cancellable: true };
    const html = render(snapshot({ activeOperation: operation }));
    expect(html).toContain('Stage:</strong> Sampling');
    expect(html).toContain('value="37"');
    expect(html).toContain('Cancel measurement');
    expect(html).not.toContain('Start baseline measurement');
    expect(html).not.toContain('<textarea');

    const indeterminate = render(snapshot({ activeOperation: { ...operation, progress: null, cancellable: false } }));
    expect(indeterminate).toContain('Progress is not reported.');
    expect(indeterminate).not.toContain('value="37"');
    expect(indeterminate).not.toContain('Cancel measurement');
    expect(indeterminate).toContain('This operation cannot be cancelled here.');
  });

  it('replaces completed measurement inputs and start actions with the next-step status in both locales', () => {
    const baseline = {
      id: 'baseline', kind: 'baseline' as const, status: 'valid' as const, startedUtc: '2026-10-07T00:00:00Z',
      analysisVersion: '1', scope: 'workload' as const, reasonCodes: [], sampleCount: 70, durationMs: 70000,
      meanDownload: { quality: 'notMeasured' as const, reasonCodes: [] }, medianDownload: { quality: 'notMeasured' as const, reasonCodes: [] },
      standardDeviation: { quality: 'notMeasured' as const, reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured' as const, reasonCodes: [] },
    };
    for (const locale of ['en-US', 'ru-RU'] as const) {
      const value = snapshot({ preferences: { locale, theme: 'light' }, phase: 'baselineReady',
        experiment: { ...snapshot().experiment!, baseline } });
      const html = render(value);
      expect(html).toContain(translateExperiment(locale, 'experiment.cycleTitle'));
      expect(html).toContain(translateExperiment(locale, 'experiment.baselineExists'));
      expect(html).not.toContain(translateExperiment(locale, 'experiment.start.baseline'));
      expect(html).not.toContain('<textarea');
      expect(html).not.toContain(translateExperiment(locale, 'experiment.description'));
      expect(html).not.toContain(`<h2 id="experiment-flow-title">${translateExperiment(locale, 'experiment.title')}</h2>`);

      const completed = render({ ...value, phase: 'completed', applyStatus: 'verified', experiment: {
        ...value.experiment!, after: { ...baseline, id: 'after', kind: 'after' },
      } });
      expect(completed).toContain(translateExperiment(locale, 'experiment.afterExists'));
      expect(completed).not.toContain(translateExperiment(locale, 'experiment.start.after'));
      expect(completed).not.toContain('<textarea');
    }
  });

  it('localizes known operation stages and hides unknown raw stage labels', () => {
    expect(translateExperimentStage('ru-RU', 'warmingUp')).toBe('Прогрев');
    expect(translateExperimentStage('en-US', 'sampling')).toBe('Sampling');
    expect(translateExperimentStage('en-US', 'futureStage')).toBe('Working');
    expect(translateExperimentStage('en-US', 'futureStage')).not.toContain('futureStage');
    expect(enExperimentMessages['experiment.operation.cancelRequested']).toContain('Cancellation requested');
    expect(ruExperimentMessages['experiment.operation.cancelRequested']).toContain('Запрошена отмена');
  });

  it('keeps RU and EN keys and named placeholders aligned', () => {
    expect(Object.keys(ruExperimentMessages).sort()).toEqual(Object.keys(enExperimentMessages).sort());
    const placeholders = (text: string) => [...text.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enExperimentMessages) as (keyof typeof enExperimentMessages)[]) {
      expect(placeholders(ruExperimentMessages[key])).toEqual(placeholders(enExperimentMessages[key]));
    }
    expect(translateExperiment('ru-RU', 'experiment.hashes.count', { count: 3 })).toContain('3');
  });
});
