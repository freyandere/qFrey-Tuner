import { expect, test } from 'vitest';
import { readFileSync } from 'node:fs';
import {
  enCalculatorMessages,
  localizeCalculatorParameters,
  ruCalculatorMessages,
} from '../src/i18n/calculatorMessages';

const coreSource = readFileSync(new URL('../../src/QFrey.Core/Tuning/Calculator.cs', import.meta.url), 'utf8');
const emittedCodes = [...new Set([...coreSource.matchAll(/"(calculator\.(?:explanation|warning)\.[A-Za-z0-9]+)"/g)].map((match) => match[1]))].sort();

const placeholders = (message: string) => [...message.matchAll(/\{([\w.]+)\}/g)].map((match) => match[1]).sort();

test('Every calculator code emitted by Core has both translations with matching placeholders', () => {
  expect(Object.keys(enCalculatorMessages).sort()).toEqual(emittedCodes);
  expect(Object.keys(ruCalculatorMessages).sort()).toEqual(emittedCodes);
  for (const code of emittedCodes) expect(placeholders(ruCalculatorMessages[code as keyof typeof ruCalculatorMessages])).toEqual(placeholders(enCalculatorMessages[code as keyof typeof enCalculatorMessages]));
});

test('Known role and profile values are localized; interface and unknown values remain literal', () => {
  expect(localizeCalculatorParameters('ru-RU', {
    role: 'leecher', profile: 'trueNas', interface: 'wg0', other: 'unchanged',
  })).toEqual({
    role: 'личер', profile: 'TrueNAS с ZFS', interface: 'wg0', other: 'unchanged',
  });
  expect(localizeCalculatorParameters('ru-RU', { role: 'futureRole', profile: 'futureProfile' })).toEqual({
    role: 'futureRole', profile: 'futureProfile',
  });
  for (const role of ['leecher', 'seeder', 'uploader'])
    expect(localizeCalculatorParameters('ru-RU', { role }).role).not.toBe(role);
  for (const profile of ['trueNas', 'nas', 'docker', 'seedbox', 'hdd', 'ssdSata', 'nvme'])
    expect(localizeCalculatorParameters('ru-RU', { profile }).profile).not.toBe(profile);
});
