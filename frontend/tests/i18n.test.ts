import { expect, test } from 'vitest';
import { en, ru, formatDate, formatNumber, formatPlural, parseDecimal, pluralCategory } from '../src/i18n/messages';
test('Both locales cover the same shell strings and placeholders', () => {
  expect(Object.keys(ru).sort()).toEqual(Object.keys(en).sort());
  const placeholders = (message: string) => [...message.matchAll(/\{([\w.]+)\}/g)].map((match) => match[1]).sort();
  for (const key of Object.keys(en) as (keyof typeof en)[]) expect(placeholders(ru[key])).toEqual(placeholders(en[key]));
});
test('Decimal input keeps precision and rejects ambiguous separators', () => {
  expect(parseDecimal('12,345')).toBe(12.345);
  expect(parseDecimal('12.345')).toBe(12.345);
  expect(parseDecimal('  -0,25  ')).toBe(-0.25);
  for (const input of ['1,234.5','1.234,5','1 234','1,234 567','1.',' .5','1e3','NaN','Infinity','']) expect(parseDecimal(input)).toBeNull();
});
test('Intl formatters use the selected locale', () => {
  expect(formatNumber('ru-RU', 12345.5)).toBe(new Intl.NumberFormat('ru-RU').format(12345.5));
  expect(formatNumber('en-US', 12345.5)).toBe(new Intl.NumberFormat('en-US').format(12345.5));
  const date = new Date(Date.UTC(2026, 0, 2));
  expect(formatDate('ru-RU', date, { dateStyle: 'medium', timeZone: 'UTC' })).toBe(new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeZone: 'UTC' }).format(date));
  expect(formatDate('en-US', date, { dateStyle: 'medium', timeZone: 'UTC' })).toBe(new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeZone: 'UTC' }).format(date));
});
test('Russian plural rules distinguish one, few, many, and other', () => {
  expect([1, 2, 5, 11, 21, 22].map((value) => pluralCategory('ru-RU', value))).toEqual(['one', 'few', 'many', 'many', 'one', 'few']);
  const forms = { one: '1 файл', few: '2 файла', many: '5 файлов', other: 'файлов' };
  expect([1, 2, 5, 11, 21, 22].map((value) => formatPlural('ru-RU', value, forms))).toEqual(['1 файл', '2 файла', '5 файлов', '5 файлов', '1 файл', '2 файла']);
});
