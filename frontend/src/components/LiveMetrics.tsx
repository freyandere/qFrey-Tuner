import { useEffect, useState } from 'react';
import type { AppSnapshot } from '../contracts/protocol';
import { translate } from '../i18n/messages';
import { presentMetric } from '../metrics/presentation';
import { Card } from './base';
import { LiveChart } from './LiveChart';

const titles: Record<string, string> = { 'transfer.download': 'metrics.download', 'transfer.upload': 'metrics.upload',
  'session.dhtNodes': 'metrics.dhtNodes', 'diagnostics.apiDuration': 'metrics.apiDuration',
  'local.cpu': 'metrics.cpu', 'local.privateMemory': 'metrics.privateMemory', 'local.ioRead': 'metrics.ioRead', 'local.ioWrite': 'metrics.ioWrite' };
export function LiveMetrics({ snapshot }: { snapshot: AppSnapshot }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer); }, []);
  const t = (key: string) => translate(snapshot.preferences.locale, key);
  return <Card title={t('metrics.title')}>
    <LiveChart snapshot={snapshot} />
    {!snapshot.target && <p>{t('metrics.connectFirst')}</p>}
    {snapshot.target && snapshot.metrics.length === 0 && <p>{t('metrics.unknown')}</p>}
    {snapshot.metrics.length > 0 && <div className="metric-table" role="region" tabIndex={0} aria-label={t('metrics.title')}><table><caption>{t('metrics.scopeNote')}</caption><thead><tr><th>{t('metrics.metric')}</th><th>{t('metrics.value')}</th><th>{t('metrics.status')}</th><th>{t('metrics.source')}</th></tr></thead>
      <tbody>{snapshot.metrics.map(metric => {
        const shown = presentMetric(metric, snapshot.preferences.locale, now);
        return <tr key={metric.id}><th scope="row">{t(titles[metric.id] ?? metric.id)}</th><td>{shown.value}</td><td>{shown.status}</td><td>{metric.id === 'diagnostics.apiDuration' ? t('metrics.roundTrip') : metric.source} · {t('metrics.' + metric.scope)}</td></tr>;
      })}</tbody></table></div>}
  </Card>;
}
