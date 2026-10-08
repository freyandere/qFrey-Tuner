import { expect, test } from 'vitest';
import type { LiveMetric } from '../src/contracts/protocol';
import { presentMetric } from '../src/metrics/presentation';
const metric: LiveMetric = { id: 'transfer.download', unit: 'bytesPerSecond', scope: 'session', source: 'transfer/info:dl_info_speed',
  reading: { status: 'fresh', value: 1048576, sampledAtUtc: '2026-10-06T10:00:00Z' } };
test('byte rates use exact binary conversion and client freshness expires without new events', () => {
  expect(presentMetric(metric, 'en-US', Date.parse('2026-10-06T10:00:03Z'))).toMatchObject({ value: '1 MiB/s', fresh: true });
  expect(presentMetric(metric, 'ru-RU', Date.parse('2026-10-06T10:00:04Z'))).toMatchObject({ fresh: false });
  expect(presentMetric({ ...metric, reading: { ...metric.reading, value: 1 } as LiveMetric['reading'] }, 'en-US', Date.parse('2026-10-06T10:00:00Z')).value).toBe('1 B/s');
});
test('missing and malformed samples never display zero', () => {
  expect(presentMetric({ ...metric, reading: { status: 'unavailable', reasonCode: 'FIELD_MISSING' } }, 'en-US', 0).value).toBe('—');
  expect(presentMetric({ ...metric, reading: { status: 'error', reasonCode: 'INVALID_METRIC_TYPE' } }, 'en-US', 0).value).toBe('—');
});
