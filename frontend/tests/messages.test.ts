import { expect, test } from 'vitest';
import { translate } from '../src/i18n/messages';
test('parameters format locally, preserve remote text as plain text and handle missing values', () => {
  expect(translate('ru-RU', 'metrics.stale', { seconds: 12.5 })).toBe('Данные устарели · 12,5 с назад');
  expect(translate('en-US', 'metrics.stale', { seconds: 12.5 })).toBe('Data is stale · 12.5 s ago');
  expect(translate('en-US', 'metrics.unavailable', { reason: '<script>bad()</script>' })).toBe('Unavailable: <script>bad()</script>');
  expect(translate('en-US', 'metrics.stale')).not.toContain('{seconds}');
  expect(translate('en-US', 'metrics.stale', { seconds: Infinity })).not.toContain('Infinity');
});
