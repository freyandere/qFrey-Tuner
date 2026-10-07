import type { Locale } from '../contracts/protocol';

export const enHistoryMessages = {
  'history.title': 'Saved cycles',
  'history.loading': 'Loading saved cycles…',
  'history.empty': 'No saved cycles yet.',
  'history.date': 'Date (UTC)',
  'history.endpoint': 'Endpoint',
  'history.version': 'qBittorrent version',
  'history.phase': 'Phase',
  'history.status': 'Outcome',
  'history.changes': 'Changes',
  'history.next': 'Next page',
  'history.open': 'Open read-only results',
  'history.dateUnavailable': 'Date unavailable',
  'history.phase.draft': 'Draft',
  'history.phase.baselineReady': 'Baseline ready',
  'history.phase.planReady': 'Plan ready',
  'history.phase.applying': 'Applying',
  'history.phase.appliedVerified': 'Applied and verified',
  'history.phase.afterReady': 'After measurement ready',
  'history.phase.completed': 'Completed',
  'history.phase.recoveryRequired': 'Recovery required',
  'history.phase.rolledBack': 'Rolled back',
  'history.phase.unknown': 'Unknown phase',
  'history.outcome.notApplied': 'Not applied',
  'history.outcome.pending': 'Pending',
  'history.outcome.verified': 'Verified',
  'history.outcome.unverified': 'Unknown',
  'history.outcome.reverted': 'Reverted',
  'history.outcome.retained': 'Retained',
  'history.outcome.unknown': 'Unknown',
  'history.changes.one': '{count} change',
  'history.changes.few': '{count} changes',
  'history.changes.many': '{count} changes',
  'history.changes.other': '{count} changes'
} as const;

export const ruHistoryMessages = {
  'history.title': 'Сохранённые циклы',
  'history.loading': 'Загрузка сохранённых циклов…',
  'history.empty': 'Сохранённых циклов пока нет.',
  'history.date': 'Дата (UTC)',
  'history.endpoint': 'Адрес',
  'history.version': 'Версия qBittorrent',
  'history.phase': 'Этап',
  'history.status': 'Итог',
  'history.changes': 'Изменения',
  'history.next': 'Следующая страница',
  'history.open': 'Открыть результаты только для чтения',
  'history.dateUnavailable': 'Дата недоступна',
  'history.phase.draft': 'Черновик',
  'history.phase.baselineReady': 'Исходный замер готов',
  'history.phase.planReady': 'План готов',
  'history.phase.applying': 'Применение',
  'history.phase.appliedVerified': 'Применено и проверено',
  'history.phase.afterReady': 'Итоговый замер готов',
  'history.phase.completed': 'Завершён',
  'history.phase.recoveryRequired': 'Требуется восстановление',
  'history.phase.rolledBack': 'Откат выполнен',
  'history.phase.unknown': 'Неизвестный этап',
  'history.outcome.notApplied': 'Не применялось',
  'history.outcome.pending': 'Ожидание',
  'history.outcome.verified': 'Проверено',
  'history.outcome.unverified': 'Неизвестно',
  'history.outcome.reverted': 'Отменено',
  'history.outcome.retained': 'Оставлено',
  'history.outcome.unknown': 'Неизвестно',
  'history.changes.one': '{count} изменение',
  'history.changes.few': '{count} изменения',
  'history.changes.many': '{count} изменений',
  'history.changes.other': '{count} изменения'
} as const;

export type HistoryMessageKey = keyof typeof enHistoryMessages;

export function historyMessage(locale: Locale, key: HistoryMessageKey, parameters: Readonly<Record<string, string | number>> = {}): string {
  const dictionary = locale === 'ru-RU' ? ruHistoryMessages : enHistoryMessages;
  const template = dictionary[key] ?? enHistoryMessages[key];
  return template.replace(/\{(\w+)\}/g, (_match, name: string) => {
    const value = parameters[name];
    return typeof value === 'number' && Number.isFinite(value) ? new Intl.NumberFormat(locale).format(value)
      : typeof value === 'string' ? value : '—';
  });
}

export function formatHistoryDate(locale: Locale, createdUtc: string): string {
  const instant = new Date(createdUtc);
  if (!Number.isFinite(instant.getTime())) return historyMessage(locale, 'history.dateUnavailable');
  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(instant);
}

export function historyPhaseKey(phase: string): HistoryMessageKey {
  const key = `history.phase.${phase}` as HistoryMessageKey;
  return Object.hasOwn(enHistoryMessages, key) ? key : 'history.phase.unknown';
}

export function historyOutcomeKey(phase: string, applyStatus: string): HistoryMessageKey {
  if (phase === 'rolledBack' || applyStatus === 'reverted') return 'history.outcome.reverted';
  if (phase === 'completed' && applyStatus === 'verified') return 'history.outcome.retained';
  if (phase === 'recoveryRequired' || phase === 'applying' || applyStatus === 'pending' || applyStatus === 'unverified')
    return 'history.outcome.unknown';
  if (applyStatus === 'notApplied') return 'history.outcome.notApplied';
  return applyStatus === 'verified' ? 'history.outcome.verified' : 'history.outcome.unknown';
}

export function formatHistoryChangeCount(locale: Locale, count: number): string {
  const category = new Intl.PluralRules(locale).select(count);
  const key = `history.changes.${category}` as HistoryMessageKey;
  return historyMessage(locale, Object.hasOwn(enHistoryMessages, key) ? key : 'history.changes.other', { count });
}
