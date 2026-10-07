import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { expect, test } from 'vitest';
import type { CycleSummary, HistoryPage } from '../src/contracts/domain';
import { HistoryList } from '../src/components/HistoryList';
import { enHistoryMessages, formatHistoryChangeCount, formatHistoryDate, historyMessage, historyOutcomeKey, historyPhaseKey } from '../src/i18n/historyMessages';

const first: CycleSummary = {
  cycleId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', createdUtc: '2026-01-01T00:30:00Z',
  endpoint: 'http://127.0.0.1:8080', qbittorrentVersion: '5.1.0', phase: 'completed', applyStatus: 'verified', changeCount: 1
};
const second: CycleSummary = {
  cycleId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', createdUtc: '2026-01-02T12:00:00Z',
  endpoint: 'http://127.0.0.1:8080', qbittorrentVersion: '5.1.0', phase: 'rolledBack', applyStatus: 'reverted', changeCount: 5
};
const page: HistoryPage = { items: [first, second], nextCursor: 'next-page' };
const noop = () => {};
const render = (value: HistoryPage | null, locale: 'en-US' | 'ru-RU', busy: boolean) => renderToStaticMarkup(
  createElement(HistoryList, { page: value, locale, busy, onNext: noop, onOpen: noop })
);

test('history renders typed saved summaries with UTC dates and truthful outcomes', () => {
  const html = render(page, 'en-US', false);

  expect(html).toContain(formatHistoryDate('en-US', first.createdUtc));
  expect(html).toContain(first.endpoint);
  expect(html).toContain(first.qbittorrentVersion);
  expect(html).toContain('Completed');
  expect(html).toContain('Retained');
  expect(html).toContain('Rolled back');
  expect(html).toContain('Reverted');
  expect(html).toContain('1 change');
  expect(html).toContain('5 changes');
  expect(html).toContain('Open read-only results');
  expect(html).toContain('Next page');
});

test('date formatting uses UTC and localizes without shifting the instant', () => {
  const instant = '2026-01-01T00:30:00Z';
  for (const locale of ['en-US', 'ru-RU'] as const) {
    const expected = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(new Date(instant));
    expect(formatHistoryDate(locale, instant)).toBe(expected);
  }
  expect(formatHistoryDate('en-US', instant)).not.toBe(formatHistoryDate('ru-RU', instant));
  expect(formatHistoryDate('en-US', 'not-a-date')).toBe('Date unavailable');
});

test('phase and outcome labels include retained, reverted, and uncertain recovery states', () => {
  expect(historyMessage('ru-RU', historyPhaseKey('recoveryRequired'))).toBe('Требуется восстановление');
  expect(historyMessage('en-US', historyOutcomeKey('completed', 'verified'))).toBe('Retained');
  expect(historyMessage('ru-RU', historyOutcomeKey('rolledBack', 'reverted'))).toBe('Отменено');
  expect(historyMessage('en-US', historyOutcomeKey('recoveryRequired', 'unverified'))).toBe('Unknown');
  expect(historyMessage('ru-RU', historyPhaseKey('futurePhase'))).toBe('Неизвестный этап');
});

test('change counts use English and Russian plural categories', () => {
  expect([1, 2, 5, 11, 21].map(value => formatHistoryChangeCount('ru-RU', value)))
    .toEqual(['1 изменение', '2 изменения', '5 изменений', '11 изменений', '21 изменение']);
  expect(formatHistoryChangeCount('en-US', 2)).toBe('2 changes');
});

test('history loading and empty states never invent cycles; busy disables pagination', () => {
  const loading = render(null, 'ru-RU', true);
  const empty = render({ items: [], nextCursor: null }, 'en-US', false);
  const busyPage = render(page, 'en-US', true);

  expect(loading).toContain('Загрузка сохранённых циклов');
  expect(loading).not.toContain('No saved cycles yet');
  expect(empty).toContain('No saved cycles yet.');
  expect(empty).not.toContain(first.cycleId);
  expect(busyPage).toContain('Loading saved cycles');
  expect(busyPage).toContain('<button type="button" disabled="">Next page</button>');
});

test('history dictionary exports matching shared-key subset for root catalog integration', () => {
  const expectedSubset = [
    'history.title', 'history.loading', 'history.empty', 'history.date', 'history.endpoint', 'history.version',
    'history.phase', 'history.status', 'history.changes', 'history.next', 'history.open', 'history.dateUnavailable',
    'history.phase.draft', 'history.phase.baselineReady', 'history.phase.planReady', 'history.phase.applying',
    'history.phase.appliedVerified', 'history.phase.afterReady', 'history.phase.completed', 'history.phase.recoveryRequired',
    'history.phase.rolledBack', 'history.phase.unknown', 'history.outcome.notApplied', 'history.outcome.pending',
    'history.outcome.verified', 'history.outcome.unverified', 'history.outcome.reverted', 'history.outcome.retained',
    'history.outcome.unknown', 'history.changes.one', 'history.changes.few', 'history.changes.many', 'history.changes.other'
  ].sort();
  expect(Object.keys(enHistoryMessages).sort()).toEqual(expectedSubset);
});
