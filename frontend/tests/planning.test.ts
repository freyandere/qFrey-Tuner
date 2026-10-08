import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { enPlanningMessages, ruPlanningMessages } from '../src/i18n/planningMessages';
import { enRecommendationReviewMessages, ruRecommendationReviewMessages } from '../src/i18n/recommendationReviewMessages';
import { createPlanSelections, RecommendationList } from '../src/components/RecommendationList';
import type { Plan, Recommendation } from '../src/contracts/domain';

const mappingSource = readFileSync(new URL('../../src/QFrey.Core/Tuning/PreferenceMapping.cs', import.meta.url), 'utf8');
const preferenceKeys = [...new Set([...mappingSource.matchAll(/(?:Add|Bool|Unsupported)\("([a-z0-9_]+)"/g)].map(match => match[1]))];
const placeholders = (value: string) => [...value.matchAll(/\{([\w.]+)\}/g)].map(match => match[1]).sort();

test('planning messages have matching placeholders in English and Russian', () => {
  expect(Object.keys(ruPlanningMessages).sort()).toEqual(Object.keys(enPlanningMessages).sort());
  for (const key of Object.keys(enPlanningMessages) as (keyof typeof enPlanningMessages)[])
    expect(placeholders(ruPlanningMessages[key])).toEqual(placeholders(enPlanningMessages[key]));
});

test('recommendation review labels are complete and keep matching placeholders in both locales', () => {
  expect(Object.keys(ruRecommendationReviewMessages).sort()).toEqual(Object.keys(enRecommendationReviewMessages).sort());
  for (const key of Object.keys(enRecommendationReviewMessages) as (keyof typeof enRecommendationReviewMessages)[])
    expect(placeholders(ruRecommendationReviewMessages[key])).toEqual(placeholders(enRecommendationReviewMessages[key]));
});

test('every PlanBuilder preference candidate has a localized recommendation title', () => {
  for (const key of preferenceKeys) expect(enPlanningMessages).toHaveProperty(`recommendations.${key}`);
  expect(preferenceKeys).toContain('limit_utp_rate');
});

test('API enum labels match the audited values', () => {
  expect(enPlanningMessages['settings.values.encryption.2']).toMatch(/disable encryption/i);
  expect(enPlanningMessages['settings.values.disk_io_read_mode.0']).toMatch(/disable OS cache/i);
  expect(enPlanningMessages['settings.values.disk_io_read_mode.1']).toMatch(/enable OS cache/i);
  expect(enPlanningMessages['settings.values.disk_io_write_mode.2']).toMatch(/write-through/i);
});

test('port selection stays grouped and a manual override survives the next plan build', () => {
  const recommendation = (apiKey: string, groupId = apiKey, manual = false): Recommendation => ({
    id: apiKey, groupId, apiKey, category: 'stability', titleKey: `recommendations.${apiKey}`,
    reason: { key: manual ? 'recommendations.reason.manualOverride' : 'recommendations.reason.heuristic', parameters: {} },
    evidence: 'heuristic', currentValue: 100, proposedValue: apiKey === 'listen_port' ? 55000 : 200,
    unit: 'count', valueType: 'number', editable: true, allowedRange: null, allowedValues: [],
    supportStatus: 'supported', selected: true, cautionCodes: [],
  });
  const plan: Plan = {
    id: 'p', targetSessionId: 's', revision: 1, createdUtc: '', inputsFingerprint: '', baselineFingerprint: '',
    previewOnly: true, applicable: false, approved: false, blockReasonCodes: [],
    recommendations: [recommendation('listen_port', 'port'), recommendation('random_port', 'port'), recommendation('max_connec', 'max_connec', true)],
    omissions: [], original: {}, proposed: {},
  };
  const selections = createPlanSelections(plan, 'port', false);
  expect(selections.find(item => item.groupId === 'port')).toEqual({ groupId: 'port', selected: false, overrides: {} });
  expect(selections.find(item => item.groupId === 'max_connec')?.overrides).toEqual({ max_connec: 200 });
  expect(createPlanSelections(plan, undefined, undefined, 'max_connec', 300).find(item => item.groupId === 'max_connec')?.overrides)
    .toEqual({ max_connec: 300 });
  expect(createPlanSelections(plan, undefined, undefined, 'max_connec', 200).find(item => item.groupId === 'max_connec')?.overrides)
    .toEqual({});
});

test('basic recommendation review defaults to changes and keeps API identifiers in advanced details', () => {
  const make = (apiKey: string, currentValue: number, proposedValue: number): Recommendation => ({
    id: apiKey, groupId: apiKey, apiKey, category: 'stability', titleKey: `recommendations.${apiKey}`,
    reason: { key: 'recommendations.reason.heuristic', parameters: {} }, evidence: 'heuristic', currentValue, proposedValue,
    unit: 'count', valueType: 'number', editable: true, allowedRange: { minimum: 0, maximum: 1000, step: 1 },
    allowedValues: [], supportStatus: 'supported', selected: true, cautionCodes: [],
  });
  const plan: Plan = {
    id: 'review', targetSessionId: 'session', revision: 1, createdUtc: '', inputsFingerprint: '', baselineFingerprint: '',
    previewOnly: false, applicable: true, approved: false, blockReasonCodes: [],
    recommendations: [make('max_connec', 500, 800), make('max_uploads', 50, 50)], omissions: [], original: {}, proposed: {},
  };
  const html = renderToStaticMarkup(createElement(RecommendationList, {
    plan, onSelections: () => undefined, busy: false, locale: 'en-US',
  }));

  expect(html).toContain('Maximum connections');
  expect(html).not.toContain('Maximum upload slots');
  expect(html).not.toContain('<code>max_connec</code>');
  expect(html).toContain('Search by setting name or API key');
  expect(html).toContain('aria-pressed="true">Basic</button>');
  expect(html).toContain('>Table</button>');
  expect(html).toContain('value="changed" selected=""');
  expect(html).toContain('>Settings to review</h2>');
  expect(html).not.toContain('>Recommendations</h2>');
  expect(html).toContain('role="group" aria-label="Recommendation detail level"');
  expect(html).toContain('role="group" aria-label="Recommendation layout"');
  expect(html).toContain('class="recommendation-filters"');
  expect(html).toContain('<dl class="recommendation-values">');
});

test('enum recommendation values have no count suffix and retain their labels in both locales', () => {
  const recommendation: Recommendation = {
    id: 'protocol', groupId: 'protocol', apiKey: 'bittorrent_protocol', category: 'connectivity',
    titleKey: 'recommendations.bittorrent_protocol',
    reason: { key: 'recommendations.reason.heuristic', parameters: {} }, evidence: 'heuristic',
    currentValue: 1, proposedValue: 0, unit: 'count', valueType: 'enum', editable: true,
    allowedRange: null, allowedValues: [
      { value: 0, labelKey: 'settings.values.bittorrent_protocol.0' },
      { value: 1, labelKey: 'settings.values.bittorrent_protocol.1' },
    ], supportStatus: 'supported', selected: true, cautionCodes: [],
  };
  const plan: Plan = {
    id: 'review', targetSessionId: 'session', revision: 1, createdUtc: '', inputsFingerprint: '', baselineFingerprint: '',
    previewOnly: false, applicable: true, approved: false, blockReasonCodes: [], recommendations: [recommendation],
    omissions: [], original: {}, proposed: {},
  };
  for (const locale of ['en-US', 'ru-RU'] as const) {
    const html = renderToStaticMarkup(createElement(RecommendationList, {
      plan, onSelections: () => undefined, busy: false, locale,
    }));
    expect(html).toContain('TCP');
    expect(html).toContain('TCP ');
    expect(html).not.toMatch(/TCP (?:count|шт\.)/);
    expect(html).toContain(locale === 'en-US' ? '>Settings to review</h2>' : '>Настройки для проверки</h2>');
  }
});
