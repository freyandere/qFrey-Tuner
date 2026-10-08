import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { expect, test } from 'vitest';
import type { ExperimentSummary, MeasurementSummary, ResultCard } from '../src/contracts/domain';
import { ResultCards } from '../src/components/ResultCards';
import { enResultMessages, resultMessage, ruResultMessages } from '../src/i18n/resultMessages';

const cycleId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const at = '2026-01-02T12:00:00Z';
const valid = (value: number) => ({ quality: 'valid' as const, value, measuredAtUtc: at });
const measurement = (id: string, kind: 'baseline' | 'after'): MeasurementSummary => ({
  id, kind, status: 'valid', startedUtc: at, analysisVersion: 'measurement-1', scope: 'workload', reasonCodes: [],
  sampleCount: 60, durationMs: 60_000, meanDownload: valid(1024), medianDownload: valid(1000),
  standardDeviation: valid(2), zeroSamplePercent: valid(0)
});
const summary = (results: ResultCard[]): ExperimentSummary => ({
  cycleId, runInputs: null, workload: null, baseline: measurement('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', 'baseline'),
  after: measurement('cccccccc-cccc-4ccc-8ccc-cccccccccccc', 'after'), plan: null, results
});
const comparison = (content: ResultCard['content'] = { type: 'comparison', comparison: {
  metricId: 'transfer.download', category: 'throughput', unit: 'bytesPerSecond', before: valid(100), after: valid(120),
  absoluteDelta: 20, relativeDeltaPercent: 20, verdict: 'observedImprovement', reasonCodes: ['CORRELATED_NOISE_NOT_CAUSAL'],
  evidence: 'derived', scope: 'workload'
} }): ResultCard => ({
  id: 'download', category: 'throughput', kind: 'metricComparison', titleKey: 'metrics.download',
  explanation: { key: 'results.comparisonCaveat', parameters: {} }, evidence: 'derived', severity: 'info', context: 'historical',
  cycleId, measuredAtUtc: at,
  content
});
const render = (data: ExperimentSummary | null, locale: 'en-US' | 'ru-RU', historical: boolean) => renderToStaticMarkup(
  createElement(ResultCards, { summary: data, locale, historical })
);

test('renders comparison values in exact units with verdict, scope, evidence, and caveat', () => {
  const html = render(summary([comparison()]), 'en-US', false);

  expect(html).toContain('Experiment results');
  expect(html).toContain('Baseline');
  expect(html).toContain('After');
  expect(html).toContain(cycleId);
  expect(html).toContain('100 B/s');
  expect(html).toContain('120 B/s');
  expect(html).toContain('+20 B/s');
  expect(html).toContain('+20%');
  expect(html).toContain('Observed improvement');
  expect(html).toContain('Selected workload');
  expect(html).toContain('Derived');
  expect(html).toContain('claim of causation');
  expect(html).toContain('Samples can be correlated; this result does not establish causation.');
});

test('renders missing and invalid values explicitly and never invents a relative percentage', () => {
  const item = comparison({ type: 'comparison', comparison: {
    metricId: 'transfer.download', category: 'throughput', unit: 'bytesPerSecond',
    before: { quality: 'invalid', reasonCodes: ['UNKNOWN_MEASUREMENT_FIELD'] },
    after: { quality: 'notMeasured', reasonCodes: ['INSUFFICIENT_VALID_SAMPLES'] },
    absoluteDelta: null, relativeDeltaPercent: null, verdict: 'notComparable', reasonCodes: ['SCOPE_MISMATCH'],
    evidence: 'derived', scope: 'session'
  } });
  const html = render(summary([item]), 'ru-RU', true);

  expect(html).toContain('Исторический результат');
  expect(html).toContain('Замер некорректен');
  expect(html).toContain('Не измерено');
  expect(html).toContain('Обязательное поле замера отсутствует или некорректно.');
  expect(html).toContain('Версия анализа');
  expect(html).toContain('Эти замеры нельзя корректно сравнить');
  expect(html).not.toContain('%');
});

test('renders applied change and recovery details as text without fake action buttons', () => {
  const applied: ResultCard = {
    id: 'change-1', category: 'configuration', kind: 'appliedChange', titleKey: 'recommendations.max_connec',
    explanation: { key: 'results.comparisonCaveat', parameters: {} }, evidence: 'observed', severity: 'warning',
    context: 'historical', cycleId, measuredAtUtc: at,
    content: { type: 'change', apiKey: 'max_connec', before: 500, intended: 600, observed: null, status: 'unverified' }
  };
  const recovery: ResultCard = {
    id: 'recovery-1', category: 'limitations', kind: 'recovery', titleKey: 'results.recovery',
    explanation: { key: 'missing.explanation.key', parameters: { detail: 'safe text' } }, evidence: 'observed', severity: 'error',
    context: 'historical', cycleId, measuredAtUtc: null,
    content: { type: 'recovery', conflictKeys: ['max_connec'], actionIds: ['checkState', 'rollback'], backupAvailable: true }
  };
  const html = render(summary([applied, recovery]), 'en-US', true);

  expect(html).toContain('Maximum connections');
  expect(html).toContain('Unknown');
  expect(html).toContain('Read back');
  expect(html).toContain('Value unavailable');
  expect(html).toContain('Backup available');
  expect(html).toContain('<code>checkState</code>');
  expect(html).toContain('Details unavailable.');
  expect(html).not.toContain('<button');
});

test('supports observations and limitation reasons with neutral localized labels', () => {
  const observation: ResultCard = {
    id: 'peers', category: 'stability', kind: 'observation', titleKey: 'metrics.peers',
    explanation: { key: 'results.comparisonCaveat', parameters: {} }, evidence: 'observed', severity: 'neutral',
    context: 'currentExperiment', cycleId, measuredAtUtc: at,
    content: { type: 'observation', metricId: 'peers.total', value: valid(8), unit: 'count', scope: 'workload' }
  };
  const limitation: ResultCard = {
    id: 'limit', category: 'limitations', kind: 'limitation', titleKey: 'results.limitation',
    explanation: { key: 'results.comparisonCaveat', parameters: {} }, evidence: 'derived', severity: 'warning',
    context: 'currentExperiment', cycleId, measuredAtUtc: null,
    content: { type: 'limitation', reasonCodes: ['SAMPLE_GAP', 'FUTURE_UNMAPPED_REASON'] }
  };
  const html = render(summary([observation, limitation]), 'ru-RU', false);

  expect(html).toContain('Наблюдение');
  expect(html).toContain('8 шт.');
  expect(html).toContain('Выбранные торренты');
  expect(html).toContain('Часть отсчётов отсутствует.');
  expect(html).toContain('Подробности недоступны.');
  expect(html).not.toContain('FUTURE_UNMAPPED_REASON');
});

test('empty state and result dictionary are localized in both languages', () => {
  expect(render(null, 'ru-RU', true)).toContain('Сводка результатов недоступна.');
  expect(render(summary([]), 'en-US', true)).toContain('There are no result cards');
  expect(resultMessage('ru-RU', 'missing.key')).toBe('Подробности недоступны.');
  expect(resultMessage('en-US', 'missing.key')).toBe('Details unavailable.');
  expect(resultMessage('ru-RU', 'results.currentExperiment')).toBe('Результаты текущего эксперимента');
  expect(Object.keys(ruResultMessages).sort()).toEqual(Object.keys(enResultMessages).sort());
});
