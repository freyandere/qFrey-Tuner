import type { LiveMetric, Locale } from '../contracts/protocol';
import { formatNumber, translate } from '../i18n/messages';

export function presentMetric(metric: LiveMetric, locale: Locale, now: number) {
  const reading = metric.reading;
  if (reading.status !== 'fresh' && reading.status !== 'stale')
    return { value: '—', status: translate(locale, reading.status === 'error' ? 'metrics.error' : 'metrics.unknown'), fresh: false };
  const age = Math.max(0, (now - Date.parse(reading.sampledAtUtc)) / 1000);
  const fresh = reading.status === 'fresh' && age <= (metric.scope === 'localProcess' ? 6 : 3);
  const raw = reading.status === 'fresh' ? reading.value : reading.lastValue;
  const divisor = metric.unit === 'bytesPerSecond' || metric.unit === 'bytes' ? raw >= 1048576 ? 1048576 : raw >= 1024 ? 1024 : 1 : 1;
  const unit = metric.unit === 'bytesPerSecond' ? translate(locale, divisor === 1048576 ? 'metrics.bytesPerSecond' : divisor === 1024 ? 'units.kibPerSecond' : 'units.bytesPerSecond')
    : metric.unit === 'bytes' ? translate(locale, divisor === 1048576 ? 'metrics.bytes' : divisor === 1024 ? 'units.kib' : 'units.bytes')
      : translate(locale, 'metrics.' + metric.unit);
  return { value: `${formatNumber(locale, raw / divisor, { maximumFractionDigits: divisor === 1 && metric.unit !== 'milliseconds' ? 0 : 2 })} ${unit}`,
    status: fresh ? translate(locale, 'metrics.fresh') : translate(locale, 'metrics.stale', { seconds: Math.floor(age) }), fresh };
}
