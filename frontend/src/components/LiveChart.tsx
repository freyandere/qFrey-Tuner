import { useEffect, useState } from 'react';
import type { AppSnapshot } from '../contracts/protocol';
import { formatNumber, translate } from '../i18n/messages';
type Point = { at: number; download: number | null; upload: number | null };

export function ratePath(points: readonly Point[], field: 'download' | 'upload', maximum: number): string {
  let start = true;
  const first = points[0]?.at ?? 0;
  const duration = Math.max(1, (points.at(-1)?.at ?? first) - first);
  return points.map(point => {
    const value = point[field];
    if (value === null) { start = true; return ''; }
    const command = start ? 'M' : 'L'; start = false;
    return `${command}${(point.at - first) * 600 / duration},${120 - value / Math.max(1, maximum) * 110}`;
  }).join(' ');
}

export function LiveChart({ snapshot }: { snapshot: AppSnapshot }) {
  const [points, setPoints] = useState<Point[]>([]);
  useEffect(() => { setPoints([]); }, [snapshot.target?.sessionId]);
  useEffect(() => {
    const sample = () => {
      const dl = snapshot.metrics.find(metric => metric.id === 'transfer.download')?.reading;
      const ul = snapshot.metrics.find(metric => metric.id === 'transfer.upload')?.reading;
      const now = Date.now();
      const value = (reading: typeof dl) => reading?.status === 'fresh' && now - Date.parse(reading.sampledAtUtc) <= 3000 ? reading.value : null;
      const download = value(dl); const upload = value(ul);
      const at = download !== null && dl?.status === 'fresh' ? Date.parse(dl.sampledAtUtc)
        : upload !== null && ul?.status === 'fresh' ? Date.parse(ul.sampledAtUtc) : now;
      setPoints(old => {
        const last = old.at(-1);
        if (last && (at < last.at || last.at === at && last.download === download && last.upload === upload
          || download === null && upload === null && last.download === null && last.upload === null)) return old;
        return [...old, { at, download, upload }].slice(-600);
      });
    };
    if (!snapshot.target) return;
    sample(); const timer = setInterval(sample, 1000); return () => clearInterval(timer);
  }, [snapshot.target, snapshot.metrics]);
  if (!snapshot.target) return null;
  const t = (key: string) => translate(snapshot.preferences.locale, key);
  const maximum = Math.max(1, ...points.flatMap(point => [point.download ?? 0, point.upload ?? 0]));
  return <figure className="live-chart"><figcaption>{t('metrics.chart')} · {t('metrics.session')}</figcaption>
    <p>{t('metrics.chartNote')}</p>
    <svg viewBox="0 0 600 130" role="img" aria-label={t('metrics.chart')}><line x1="0" x2="600" y1="120" y2="120" stroke="var(--border)" />
      <path d={ratePath(points, 'download', maximum)} fill="none" stroke="var(--graph-download)" strokeWidth="2" />
      <path d={ratePath(points, 'upload', maximum)} fill="none" stroke="var(--graph-upload)" strokeWidth="2" strokeDasharray="4 3" /></svg>
    <p><span style={{ color: 'var(--graph-download)' }}>{t('metrics.download')} ━</span> / <span style={{ color: 'var(--graph-upload)' }}>{t('metrics.upload')} ┄</span> · {t('metrics.chartMaximum')}: {formatNumber(snapshot.preferences.locale, maximum / 1048576, { maximumFractionDigits: 2 })} {t('metrics.bytesPerSecond')}</p>
  </figure>;
}
