import type { HistoryPage } from '../contracts/domain';
import type { Locale } from '../contracts/protocol';
import { formatHistoryChangeCount, formatHistoryDate, historyMessage, historyOutcomeKey, historyPhaseKey } from '../i18n/historyMessages';
import { Button, Card } from './base';

type Props = {
  page: HistoryPage | null;
  locale: Locale;
  busy: boolean;
  onNext: () => void;
  onOpen: (cycleId: string) => void;
};

export function HistoryList({ page, locale, busy, onNext, onOpen }: Props) {
  const t = (key: Parameters<typeof historyMessage>[1]) => historyMessage(locale, key);
  const empty = !busy && (!page || page.items.length === 0);

  return <Card title={t('history.title')}>
    {busy && <p role="status" aria-live="polite">{t('history.loading')}</p>}
    {empty && <p>{t('history.empty')}</p>}
    {page && page.items.length > 0 && <>
      <div className="metric-table" role="region" tabIndex={0} aria-label={t('history.title')}>
        <table>
          <caption className="sr-only">{t('history.title')}</caption>
          <thead><tr>
            <th scope="col">{t('history.date')}</th>
            <th scope="col">{t('history.endpoint')}</th>
            <th scope="col">{t('history.version')}</th>
            <th scope="col">{t('history.phase')}</th>
            <th scope="col">{t('history.status')}</th>
            <th scope="col">{t('history.changes')}</th>
          </tr></thead>
          <tbody>{page.items.map(cycle => {
            const date = formatHistoryDate(locale, cycle.createdUtc);
            return <tr key={cycle.cycleId}>
              <td><Button disabled={busy} aria-label={`${t('history.open')}: ${date}`} onClick={() => onOpen(cycle.cycleId)}>{date}</Button></td>
              <td>{cycle.endpoint}</td>
              <td>{cycle.qbittorrentVersion}</td>
              <td>{t(historyPhaseKey(cycle.phase))}</td>
              <td>{t(historyOutcomeKey(cycle.phase, cycle.applyStatus))}</td>
              <td>{formatHistoryChangeCount(locale, cycle.changeCount)}</td>
            </tr>;
          })}</tbody>
        </table>
      </div>
      {page.nextCursor !== null && <Button disabled={busy} onClick={onNext}>{t('history.next')}</Button>}
    </>}
  </Card>;
}
