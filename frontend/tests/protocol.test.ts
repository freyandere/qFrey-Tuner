import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from 'vitest';
import { parseRequest } from '../src/contracts/requestValidation';
import { acceptedOperation, experiment, measurement, plan, resultCard } from '../src/contracts/validation';
import { isSnapshot } from '../src/bridge/client';
const directory = resolve(import.meta.dirname, '../../tests-contract/fixtures/protocol');
const read = (file: string): unknown => JSON.parse(readFileSync(resolve(directory, file), 'utf8'));

test.each(readdirSync(directory).filter(f => f.startsWith('product-command-')))('accepts shared typed request %s', file => {
  expect(parseRequest(readFileSync(resolve(directory, file), 'utf8')).protocolVersion).toBe(1);
});
test.each(readdirSync(directory).filter(f => f.startsWith('product-invalid-')))('rejects shared invalid request %s', file => {
  expect(() => parseRequest(readFileSync(resolve(directory, file), 'utf8'))).toThrow();
});
test.each(['product-applied-verified.json', 'product-unverified.json'])('accepts shared snapshot %s', file => {
  const v = read(file) as { revision: number };
  expect(isSnapshot(v, v.revision)).toBe(true);
});
test('accepts historical values and preview plan without converting them to live', () => {
  expect(plan(read('product-plan.json'))).toBe(true);
  expect(resultCard(read('product-historical-result.json'))).toBe(true);
  expect(acceptedOperation(read('product-accepted-operation.json'))).toBe(true);
  expect(measurement(read('product-cancelled-measurement.json'))).toBe(true);
  expect(experiment({ cycleId: null, runInputs: null, workload: null, baseline: null, after: null, plan: read('product-plan.json'), results: [read('product-historical-result.json')] })).toBe(true);
});
test.each(readdirSync(directory).filter(f => f.startsWith('product-snapshot-invalid-')))('rejects shared invalid snapshot %s', file => {
  const v = read(file) as { revision: number };
  expect(isSnapshot(v, v.revision)).toBe(false);
});
test('accepts compatible snapshot additions and shared normal/stale telemetry', () => {
  const v = read('product-snapshot-unknown-addition.json') as { revision: number };
  expect(isSnapshot(v, v.revision)).toBe(true);
  const base = read('product-applied-verified.json') as Record<string, unknown>;
  for (const file of ['product-telemetry-normal.json', 'product-telemetry-stale.json']) {
    expect(isSnapshot({ ...base, metrics: [read(file)] }, base.revision as number)).toBe(true);
  }
});
test('rejects invalid product fields inside a full snapshot', () => {
  const valid = read('product-applied-verified.json') as Record<string, unknown>;
  expect(isSnapshot({ ...valid, experiment: { results: [] } }, valid.revision as number)).toBe(false);
  expect(isSnapshot({ ...valid, confirmations: [{ token: 'token' }] }, valid.revision as number)).toBe(false);
  expect(isSnapshot({ ...valid, hardware: { inputs: null } }, valid.revision as number)).toBe(false);
  expect(isSnapshot({ ...valid, networkTest: { downloadBytesPerSecond: Infinity } }, valid.revision as number)).toBe(false);
  expect(isSnapshot({ ...valid, interfaces: [null] }, valid.revision as number)).toBe(false);
  const workload = { id: 'ubuntu', name: 'Ubuntu', totalBytesDecimal: '4762707968', metadataSource: 'https://example.test/image.torrent' };
  expect(isSnapshot({ ...valid, workloadCatalogue: [workload] }, valid.revision as number)).toBe(true);
  for (const size of ['01', '0', '18446744073709551616', '-1'])
    expect(isSnapshot({ ...valid, workloadCatalogue: [{ ...workload, totalBytesDecimal: size }] }, valid.revision as number)).toBe(false);
  expect(isSnapshot({ ...valid, workloadCatalogue: [{ ...workload, metadataSource: 'https://user:secret@example.test/' }] }, valid.revision as number)).toBe(false);
});
test('rejects int32 overflow, bool-as-int, unsafe numbers and unbounded commands', () => {
  const command = read('product-command-build-plan.json') as { payload: { selections: unknown[] } };
  const withOverride = (v: unknown) => JSON.stringify({ ...command, payload: { ...command.payload, selections: [{ groupId: 'port', selected: true, overrides: { listen_port: v } }] } });
  expect(() => parseRequest(withOverride(2147483648))).toThrow();
  expect(() => parseRequest(withOverride(Number.MAX_SAFE_INTEGER + 1))).toThrow();
  expect(() => parseRequest(readFileSync(resolve(directory, 'product-command-cancel.json'), 'utf8') + ' '.repeat(65536))).toThrow();
});

test('restore review accepts bounded readonly differences and rejects malformed authority', () => {
  const base = read('product-applied-verified.json') as Record<string, unknown>;
  const restore = { selectionToken: 'native-selection', displayName: 'backup.json',
    sourceCycleId: '00000000-0000-0000-0000-000000000601', review: {
      isLegacy: true, targetMatches: true, canRestore: true, alreadyOriginal: false,
      fingerprint: 'a'.repeat(64), blockReasonCodes: [],
      differences: [{ key: 'listen_port', original: 55000, intended: 55001, current: 55001, disposition: 'restoreRequired' }],
    } };
  const valid = (value: unknown) => isSnapshot({ ...base, restore: value }, base.revision as number);
  expect(valid(restore)).toBe(true);
  expect(valid({ ...restore, sourceCycleId: 'invalid' })).toBe(false);
  expect(valid({ ...restore, review: { ...restore.review, canRestore: 1 } })).toBe(false);
  expect(valid({ ...restore, review: { ...restore.review, fingerprint: 'short' } })).toBe(false);
  expect(valid({ ...restore, review: { ...restore.review, differences: [{ ...restore.review.differences[0], current: 2147483648 }] } })).toBe(false);
  expect(valid({ ...restore, review: { ...restore.review, differences: [{ ...restore.review.differences[0], disposition: 'forceRestore' }] } })).toBe(false);
});
