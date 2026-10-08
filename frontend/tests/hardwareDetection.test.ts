import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { DraftInputs } from '../src/contracts/domain';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canAdoptHardware, HardwareDetectionPanel } from '../src/components/HardwareDetectionPanel';
import { enHardwareMessages, ruHardwareMessages, translateHardware } from '../src/components/hardwareMessages';

const inputs: DraftInputs['hardware'] = { storageType: 'nvme', ramGiB: 16, cpuCores: 8, isHybridCpu: true, performanceCores: 4, source: 'localDetection' };
const fresh = { status: 'fresh' as const, value: 16 * 1024 ** 3, sampledAtUtc: '2026-10-07T12:00:00Z' };
const hardware: NonNullable<AppSnapshot['hardware']> = { inputs, reasonCodes: [], facts: {
  ramBytes: fresh, logicalCpuCount: { ...fresh, value: 8 }, performanceCpuCount: { ...fresh, value: 4 },
  storageType: 'nvme', storageReasonCode: null, volumeToken: null
} };
const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 1, preferences: { locale: 'en-US', theme: 'system' },
  connection: 'validated', target: { sessionId: 's', endpoint: 'http://localhost:8080', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: true },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [], availableActions: [], hardware,
  ...overrides
});
const render = (value: AppSnapshot) => renderToStaticMarkup(createElement(HardwareDetectionPanel, {
  snapshot: value, onDetect: async () => {}, onSelectVolume: async () => null, onAdopt: () => {}
}));

describe('HardwareDetectionPanel', () => {
  it('displays the exact raw memory bytes and offers explicit adoption only for fresh local readings', () => {
    const value = snapshot();
    expect(canAdoptHardware(value)).toBe(true);
    const html = render(value);
    expect(html).toContain('17,179,869,184 bytes');
    expect(html).toContain('16 GiB');
    expect(html).toContain('Adopt detected values');
    expect(html).not.toContain('Detected values adopted');
  });

  it('never adopts stale, incomplete, active-operation, or remote hardware', () => {
    const stale = { ...hardware, facts: { ...hardware.facts,
      ramBytes: { status: 'stale' as const, lastValue: fresh.value, sampledAtUtc: fresh.sampledAtUtc, reasonCode: 'hardwareReadingStale' } } };
    expect(canAdoptHardware(snapshot({ hardware: stale }))).toBe(false);
    expect(canAdoptHardware(snapshot({ hardware: { ...hardware, inputs: null } }))).toBe(false);
    expect(canAdoptHardware(snapshot({ activeOperation: { id: 'op', kind: 'hardwareDetection', stage: 'sampling', progress: null, cancellable: true } }))).toBe(false);
    const remote = snapshot({ target: { ...snapshot().target!, isLocal: false } });
    expect(canAdoptHardware(remote)).toBe(false);
    expect(render({ ...remote, hardware: null })).toContain('The connected qBittorrent target is remote.');
  });

  it('shows stale values as stale and missing values as unavailable, never as zero', () => {
    const stale = { ...hardware, inputs: null, facts: { ...hardware.facts,
      ramBytes: { status: 'stale' as const, lastValue: fresh.value, sampledAtUtc: fresh.sampledAtUtc, reasonCode: 'hardwareReadingStale' },
      logicalCpuCount: { status: 'unavailable' as const, reasonCode: 'cpuCountUnavailable' } } };
    const html = render(snapshot({ hardware: stale, preferences: { locale: 'ru-RU', theme: 'dark' } }));
    expect(html).toContain('Устаревшее наблюдение');
    expect(html).toContain('Недоступно');
    expect(html).toContain('Не удалось прочитать число логических процессоров.');
    expect(html).not.toContain('0 GiB');
    expect(html).toContain('Выбрать папку для определения диска');
  });

  it('keeps RU and EN message keys and placeholders aligned and localizes unknown reasons', () => {
    expect(Object.keys(ruHardwareMessages).sort()).toEqual(Object.keys(enHardwareMessages).sort());
    const placeholders = (text: string) => [...text.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enHardwareMessages) as (keyof typeof enHardwareMessages)[])
      expect(placeholders(ruHardwareMessages[key])).toEqual(placeholders(enHardwareMessages[key]));
    expect(translateHardware('ru-RU', 'hardware.reason.futureSourceCode')).toBe('Не удалось прочитать некоторые сведения об оборудовании.');
  });
});
