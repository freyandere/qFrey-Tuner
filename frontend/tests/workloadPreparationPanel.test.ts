import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { ConfirmationSummary } from '../src/contracts/domain';
import type { AppSnapshot } from '../src/contracts/protocol';
import { canReviewWorkloadPreparation, isAbsoluteServerSavePath, isCurrentWorkloadConfirmation, WorkloadPreparationPanel } from '../src/components/WorkloadPreparationPanel';
import { enWorkloadMessages, ruWorkloadMessages, translateWorkload } from '../src/components/workloadMessages';

const catalogue = [{ id: 'ubuntu', name: 'Ubuntu test image', totalBytesDecimal: '4762707968',
  metadataSource: 'https://releases.example.test/ubuntu.torrent' }];
const snapshot = (overrides: Partial<AppSnapshot> = {}): AppSnapshot => ({
  protocolVersion: 1, schemaVersion: 2, appVersion: 'test', revision: 12,
  preferences: { locale: 'en-US', theme: 'system' }, connection: 'validated',
  target: { sessionId: 'session-1', endpoint: 'http://127.0.0.1:8080/', qbittorrentVersion: '5.2.0', apiVersion: '2.11', libtorrentVersion: '2.0', isLocal: false },
  phase: 'draft', applyStatus: 'notApplied', activeOperation: null, metrics: [], availableActions: [],
  workloadCatalogue: catalogue,
  experiment: { cycleId: null, runInputs: null, workload: null, baseline: null, after: null, plan: null, results: [] },
  ...overrides
});
const confirmation = (overrides: Partial<ConfirmationSummary> = {}): ConfirmationSummary => ({
  token: 'one-use-token', targetSessionId: 'session-1', revision: 12,
  expiresUtc: '2026-10-07T12:01:00Z', actionId: 'PrepareWorkload',
  description: { key: 'confirmation.workload.prepare', parameters: { name: 'Ubuntu.iso', sizeBytes: 4762707968,
    serverSavePath: '/srv/qb/downloads', endpoint: 'http://127.0.0.1:8080/' } }, ...overrides
});
const render = (value: AppSnapshot) => renderToStaticMarkup(createElement(WorkloadPreparationPanel, {
  snapshot: value, onReview: async () => confirmation(), onPrepare: async () => {}
}));

describe('WorkloadPreparationPanel', () => {
  it('shows the exact catalogue size, source, and honest stopped-state limit without starting anything', () => {
    let reviews = 0;
    let preparations = 0;
    const html = renderToStaticMarkup(createElement(WorkloadPreparationPanel, {
      snapshot: snapshot(), onReview: async () => { reviews++; return confirmation(); },
      onPrepare: async () => { preparations++; }
    }));
    expect(html).toContain('Ubuntu test image');
    expect(html).toContain('4,762,707,968 bytes');
    expect(html).toContain('https://releases.example.test/ubuntu.torrent');
    expect(html).toContain('The application cannot prove free space');
    expect(html).toContain('does not authorize starting the download');
    expect(reviews).toBe(0);
    expect(preparations).toBe(0);
  });

  it('accepts absolute target paths and rejects relative, traversal, whitespace, controls, and oversized paths', () => {
    expect(isAbsoluteServerSavePath('/srv/qb/downloads')).toBe(true);
    expect(isAbsoluteServerSavePath('C:\\qBittorrent\\downloads')).toBe(true);
    expect(isAbsoluteServerSavePath('\\\\nas\\share\\downloads')).toBe(true);
    expect(isAbsoluteServerSavePath('downloads')).toBe(false);
    expect(isAbsoluteServerSavePath('/srv/../private')).toBe(false);
    expect(isAbsoluteServerSavePath(' /srv/qb ')).toBe(false);
    expect(isAbsoluteServerSavePath('/srv/\nqb')).toBe(false);
    expect(isAbsoluteServerSavePath(`/${'a'.repeat(2048)}`)).toBe(false);
  });

  it('fails closed unless a validated idle target has a catalogue and no captured workload measurement', () => {
    expect(canReviewWorkloadPreparation(snapshot())).toBe(true);
    expect(canReviewWorkloadPreparation(snapshot({ connection: 'degraded' }))).toBe(false);
    expect(canReviewWorkloadPreparation(snapshot({ target: null }))).toBe(false);
    expect(canReviewWorkloadPreparation(snapshot({ activeOperation: { id: 'op', kind: 'prepareWorkload', stage: 'metadataDownload', progress: null, cancellable: true } }))).toBe(false);
    expect(canReviewWorkloadPreparation(snapshot({ workloadCatalogue: [] }))).toBe(false);
    expect(canReviewWorkloadPreparation(snapshot({ experiment: { ...snapshot().experiment!, baseline: {
      id: 'baseline', kind: 'baseline', status: 'pending', startedUtc: '2026-10-07T12:00:00Z', analysisVersion: '1',
      scope: 'workload', reasonCodes: [], sampleCount: 0, durationMs: 0,
      meanDownload: { quality: 'notMeasured', reasonCodes: [] }, medianDownload: { quality: 'notMeasured', reasonCodes: [] },
      standardDeviation: { quality: 'notMeasured', reasonCodes: [] }, zeroSamplePercent: { quality: 'notMeasured', reasonCodes: [] }
    } } }))).toBe(false);
    expect(canReviewWorkloadPreparation(snapshot({ applyStatus: 'verified' }))).toBe(false);
  });

  it('binds confirmation to action, target session, current revision, and a fresh expiry', () => {
    const value = snapshot();
    const review = confirmation();
    const now = Date.parse('2026-10-07T12:00:30Z');
    expect(isCurrentWorkloadConfirmation(review, value, now)).toBe(true);
    expect(isCurrentWorkloadConfirmation(confirmation({ actionId: 'ApplyPlan' }), value, now)).toBe(false);
    expect(isCurrentWorkloadConfirmation(confirmation({ targetSessionId: 'other' }), value, now)).toBe(false);
    expect(isCurrentWorkloadConfirmation(confirmation({ revision: 11 }), value, now)).toBe(false);
    expect(isCurrentWorkloadConfirmation(confirmation({ expiresUtc: '2026-10-07T12:00:30Z' }), value, now)).toBe(false);
  });

  it('reports ownership without claiming stopped-state proof, and keeps RU/EN catalogs aligned', () => {
    const owned = { reference: { id: 'owned-1', kind: 'owned' as const, hashes: ['a'.repeat(40)] }, name: 'Ubuntu test image',
      totalBytesDecimal: '4762707968', serverSavePath: '/srv/qb/downloads', catalogueId: 'ubuntu', ownershipVerified: true, reasonCodes: [] };
    const html = render(snapshot({ experiment: { ...snapshot().experiment!, workload: owned } }));
    expect(html).toContain('Ownership is verified. The torrent was requested to be added stopped; its stopped state has not yet been verified.');
    expect(html).not.toContain('Starting the download requires separate consent.');
    expect(Object.keys(ruWorkloadMessages).sort()).toEqual(Object.keys(enWorkloadMessages).sort());
    const placeholders = (text: string) => [...text.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map(match => match[1]).sort();
    for (const key of Object.keys(enWorkloadMessages) as (keyof typeof enWorkloadMessages)[])
      expect(placeholders(ruWorkloadMessages[key])).toEqual(placeholders(enWorkloadMessages[key]));
    expect(translateWorkload('ru-RU', 'workload.catalogue.ownershipUnverified')).toContain('не проверена');
  });
});
