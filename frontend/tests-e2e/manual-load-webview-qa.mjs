import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { join, relative, win32 } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(new URL('../package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const [cdpArg, pidArg, durationArg = '1800'] = process.argv.slice(2);
const cdpPort = Number(cdpArg), ownedPid = Number(pidArg), durationSeconds = Number(durationArg);
assert.ok(Number.isInteger(cdpPort) && cdpPort >= 1024 && cdpPort <= 65535, 'Usage: node manual-load-webview-qa.mjs <127.0.0.1 CDP port> <owned EXE PID> [durationSeconds=1800].');
assert.ok(Number.isSafeInteger(ownedPid) && ownedPid > 0, 'Pass the PID of the root-owned qFrey-Tuner.exe process.');
assert.ok(Number.isInteger(durationSeconds) && durationSeconds >= 1 && durationSeconds <= 86400, 'durationSeconds must be 1..86400.');

const runId = `${new Date().toISOString().replaceAll(':', '-')}-${process.pid}`;
const runDir = join(root, '.cache', 'e2e', 'load-webview', runId);
mkdirSync(runDir, { recursive: true });
const preferencesFixture = JSON.parse(readFileSync(join(root, 'tests-contract/fixtures/legacy/settings-payloads.json'), 'utf8'))['2.0.11'].payload;
const preferences = { ...preferencesFixture, scheduler_enabled: false };
const syntheticTorrents = Array.from({ length: 1000 }, (_, index) => ({
  hash: index.toString(16).padStart(40, '0'),
  name: `Synthetic UI load torrent ${String(index + 1).padStart(4, '0')}`,
  state: index === 0 ? 'downloading' : 'pausedDL',
  progress: (index % 1000) / 1000,
  dlspeed: 16_384 + (index % 64) * 1_024,
  upspeed: 1_024 + (index % 16) * 256,
  num_seeds: index % 23,
  num_leechs: index % 31,
  total_size: 2_000_000_000 + index,
  downloaded: 500_000_000 + index * 100,
  uploaded: index * 10_000,
  amount_left: 1_500_000_000 - index * 100,
  ratio: (index % 10) / 10,
  save_path: '/synthetic/qfrey-load',
  category: '',
  tags: '',
  priority: 0,
  num_complete: index % 40,
  num_incomplete: index % 30,
  eta: 10_000 + index,
  added_on: 1_700_000_000 + index,
  completion_on: -1,
  seeding_time: 0,
  time_active: 3_600 + index,
  tracker: 'https://tracker.example.invalid/announce',
}));
const torrentJson = JSON.stringify(syntheticTorrents);
const apiCounts = new Map();
const apiDurations = [];
const apiRequestSamples = [];
let apiPostCount = 0;
let apiErrors = 0;
let apiServer;
let browser;
let page;
let rootProcessIdentity;
let previousProcessSample;
let navigationCount = 0;
const rawSamples = [];
const uiLatenciesMs = [];
const clientApiDurationSamples = [];
const screenshots = [];
const unknownMetricCounts = { privateWorkingSet: 0, cpu: 0 };

const routes = {
  'app/version': ['v5.2.0', 'text/plain; charset=utf-8'],
  'app/webapiVersion': ['2.15.0', 'text/plain; charset=utf-8'],
  'app/buildInfo': [JSON.stringify({ libtorrent: '2.0.11' }), 'application/json; charset=utf-8'],
  'app/preferences': [JSON.stringify(preferences), 'application/json; charset=utf-8'],
  'app/networkInterfaceList': [JSON.stringify([{ value: 'synthetic0', name: 'Synthetic adapter' }]), 'application/json; charset=utf-8'],
  'transfer/info': [JSON.stringify({ dl_info_speed: 4_194_304, up_info_speed: 262_144, dht_nodes: 17, use_alt_speed_limits: false }), 'application/json; charset=utf-8'],
  'torrents/info': [torrentJson, 'application/json; charset=utf-8'],
};

const listen = server => new Promise((resolve, reject) => {
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => resolve(server.address().port));
});

const percentile = (values, p) => {
  if (!values.length) return null;
  const ordered = [...values].sort((a, b) => a - b);
  return ordered[Math.min(ordered.length - 1, Math.ceil(p * ordered.length) - 1)];
};
const summary = values => ({ count: values.length, median: percentile(values, 0.5), p95: percentile(values, 0.95), max: values.length ? Math.max(...values) : null });
const utcNow = () => new Date().toISOString();
const powerScheme = (() => {
  try { return execFileSync('powercfg.exe', ['/getactivescheme'], { encoding: 'utf8', timeout: 3_000, windowsHide: true }).trim(); }
  catch { return null; }
})();
const recordApiSample = (method, route, status, responseBytes, started) => {
  const durationMs = performance.now() - started;
  apiDurations.push(durationMs);
  apiRequestSamples.push({ completedUtc: utcNow(), method, route, status, responseBytes, durationMs });
};

const localeData = {
  'en-US': { sections: ['Overview', 'Setup', 'Recommendations', 'Experiment', 'Results', 'History'], disconnected: /Not connected/ },
  'ru-RU': { sections: ['Обзор', 'Условия', 'Рекомендации', 'Эксперимент', 'Результаты', 'История'], disconnected: /Нет подключения|Не подключено/ },
};

const powershell = String.raw`
$ErrorActionPreference = 'Stop'
try {
  $all = @(Get-CimInstance -ClassName Win32_Process | ForEach-Object {
    $created = [datetime]$_.CreationDate
    [pscustomobject]@{
      pid = [int]$_.ProcessId
      ppid = [int]$_.ParentProcessId
      createdTicks = [string]$created.ToUniversalTime().Ticks
      kernelTicks = [string]$_.KernelModeTime
      userTicks = [string]$_.UserModeTime
      image = [string]$_.ExecutablePath
    }
  })
  $private = @(Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process | ForEach-Object {
    [pscustomobject]@{ pid = [int]$_.IDProcess; bytes = [string]$_.WorkingSetPrivate }
  })
  $machine = Get-CimInstance -ClassName Win32_ComputerSystem
  [pscustomobject]@{ ok = $true; processes = $all; private = $private; logicalProcessors = [int]$machine.NumberOfLogicalProcessors } | ConvertTo-Json -Depth 4 -Compress
} catch {
  [pscustomobject]@{ ok = $false; reason = 'CIM_QUERY_UNAVAILABLE' } | ConvertTo-Json -Compress
}`;

const verifyOwnedRootFallback = () => {
  const script = `$ErrorActionPreference='Stop'; $p=Get-Process -Id ${ownedPid}; [pscustomobject]@{pid=$p.Id;createdTicks=[string]$p.StartTime.ToUniversalTime().Ticks;image=[string]$p.Path} | ConvertTo-Json -Compress`;
  const encoded = Buffer.from(script, 'utf16le').toString('base64');
  let item;
  try {
    item = JSON.parse(execFileSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
      { encoding: 'utf8', timeout: 5_000, maxBuffer: 64 * 1024, windowsHide: true }).replace(/^\uFEFF/, '').trim());
  } catch { throw new Error(`Could not verify owned PID ${ownedPid} and its start time.`); }
  if (item.pid !== ownedPid || win32.basename(item.image ?? '').toLowerCase() !== 'qfrey-tuner.exe')
    throw new Error(`PID ${ownedPid} is not the expected qFrey-Tuner.exe process.`);
  const identity = { pid: ownedPid, createdTicks: item.createdTicks, image: item.image };
  if (rootProcessIdentity && rootProcessIdentity.createdTicks !== identity.createdTicks)
    throw new Error('The owned root process exited or its PID was reused during sampling.');
  rootProcessIdentity ??= identity;
};

const readProcessTree = async () => {
  let response;
  try {
    const encoded = Buffer.from(powershell, 'utf16le').toString('base64');
    const text = execFileSync('powershell.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
      { encoding: 'utf8', timeout: 12_000, maxBuffer: 8 * 1024 * 1024, windowsHide: true });
    response = JSON.parse(text.replace(/^\uFEFF/, '').trim());
  } catch {
    verifyOwnedRootFallback();
    previousProcessSample = undefined;
    return { privateWorkingSetBytes: null, privateWorkingSetReason: 'CIM_QUERY_UNAVAILABLE', cpuPercent: null, cpuReason: 'CIM_QUERY_UNAVAILABLE', processCount: null, logicalProcessors: null };
  }
  if (!response.ok || !Array.isArray(response.processes) || !Array.isArray(response.private))
  {
    verifyOwnedRootFallback();
    previousProcessSample = undefined;
    return { privateWorkingSetBytes: null, privateWorkingSetReason: response.reason ?? 'CIM_QUERY_UNAVAILABLE', cpuPercent: null,
      cpuReason: response.reason ?? 'CIM_QUERY_UNAVAILABLE', processCount: null, logicalProcessors: response.logicalProcessors ?? null };
  }

  const processes = response.processes;
  const ownedRoot = processes.find(item => item.pid === ownedPid);
  if (!ownedRoot) throw new Error(`Owned process PID ${ownedPid} is absent; refusing to follow a missing or reused PID.`);
  if (win32.basename(ownedRoot.image ?? '').toLowerCase() !== 'qfrey-tuner.exe')
    throw new Error(`PID ${ownedPid} is not qFrey-Tuner.exe; refusing to inspect another process.`);
  const identity = { pid: ownedPid, createdTicks: ownedRoot.createdTicks, image: ownedRoot.image };
  if (rootProcessIdentity && (rootProcessIdentity.pid !== identity.pid || rootProcessIdentity.createdTicks !== identity.createdTicks))
    throw new Error('The owned root process exited or its PID was reused during sampling.');
  rootProcessIdentity ??= identity;

  const byPid = new Map(processes.map(item => [item.pid, item]));
  const members = new Map([[ownedPid, ownedRoot]]);
  let changed = true;
  while (changed) {
    changed = false;
    for (const candidate of processes) {
      if (members.has(candidate.pid)) continue;
      const parent = members.get(candidate.ppid);
      if (parent && BigInt(candidate.createdTicks) >= BigInt(parent.createdTicks)) {
        members.set(candidate.pid, candidate);
        changed = true;
      }
    }
  }
  const privateByPid = new Map(response.private.filter(item => Number.isSafeInteger(item.pid))
    .map(item => [item.pid, item.bytes === null || item.bytes === '' ? null : Number(item.bytes)]));
  const privateValues = [...members.keys()].map(pid => privateByPid.get(pid));
  const privateWorkingSetBytes = privateValues.every(value => Number.isSafeInteger(value) && value >= 0)
    ? privateValues.reduce((sum, value) => sum + value, 0) : null;
  const processCounters = [...members.values()].map(item => ({
    key: `${item.pid}:${item.createdTicks}`,
    cpuSeconds: item.kernelTicks === null || item.userTicks === null || item.kernelTicks === '' || item.userTicks === ''
      ? null : (Number(item.kernelTicks) + Number(item.userTicks)) / 10_000_000,
  }));
  const cpuCountersValid = processCounters.every(item => typeof item.cpuSeconds === 'number' && Number.isFinite(item.cpuSeconds));
  const currentKeys = processCounters.map(item => item.key).sort();
  const priorKeys = previousProcessSample?.processCounters.map(item => item.key).sort();
  let cpuPercent = null;
  let cpuReason = previousProcessSample ? 'PROCESS_TREE_CHANGED' : 'FIRST_CPU_DELTA';
  if (previousProcessSample && cpuCountersValid && JSON.stringify(currentKeys) === JSON.stringify(priorKeys)
    && previousProcessSample.processCounters.every(item => typeof item.cpuSeconds === 'number' && Number.isFinite(item.cpuSeconds))) {
    const previous = new Map(previousProcessSample.processCounters.map(item => [item.key, item.cpuSeconds]));
    const cpuSeconds = processCounters.reduce((sum, item) => sum + Math.max(0, item.cpuSeconds - previous.get(item.key)), 0);
    const elapsedSeconds = (Date.now() - previousProcessSample.wallTimeMs) / 1000;
    const logical = response.logicalProcessors;
    if (elapsedSeconds > 0 && Number.isInteger(logical) && logical > 0) {
      cpuPercent = Math.min(100, cpuSeconds / (elapsedSeconds * logical) * 100);
      cpuReason = null;
    } else cpuReason = 'CPU_DENOMINATOR_UNAVAILABLE';
  }
  if (!cpuCountersValid) cpuReason = 'CPU_COUNTER_UNAVAILABLE';
  const value = { privateWorkingSetBytes, privateWorkingSetReason: privateWorkingSetBytes === null ? 'PRIVATE_WORKING_SET_UNAVAILABLE' : null,
    cpuPercent, cpuReason, processCount: members.size, logicalProcessors: response.logicalProcessors,
    processCounters, wallTimeMs: Date.now() };
  previousProcessSample = value;
  return value;
};

const captureUiState = async (requireLiveMetrics = true) => {
  const body = await page.locator('body').innerText();
  assert.doesNotMatch(body, /\b(?:errors|experiment|mutation|setup|recommendations|results|history|metrics|connection)\.[A-Za-z][A-Za-z0-9_.-]*\b/,
    'Raw translation key visible in UI.');
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
  assert.equal(overflow, false, 'UI has horizontal overflow.');
  const liveTable = page.getByRole('region').filter({ has: page.getByRole('table') });
  const metricRows = await liveTable.getByRole('row').count().catch(() => 0);
  if (requireLiveMetrics) assert.ok(metricRows >= 2, 'Expected rendered live metric rows from the synthetic API.');
  return { bodyTextLength: body.length, metricRows, horizontalOverflow: overflow };
};

const installBridgeEventEvidence = async () => page.evaluate(() => {
  const webview = window.chrome?.webview;
  if (!webview) throw new Error('WebView bridge is unavailable.');
  const state = window.__loadQaBridge = { count: 0, sequenceGaps: 0, outOfOrder: 0, lastSequence: null,
    lastAt: null, lastInterarrivalMs: null, maximumInterarrivalMs: 0, apiDurationSamples: [] };
  webview.addEventListener('message', event => {
    const value = event.data;
    if (!value || typeof value !== 'object' || typeof value.requestId === 'string' || !Number.isSafeInteger(value.sequence)) return;
    const at = performance.now();
    if (state.lastSequence !== null) {
      if (value.sequence > state.lastSequence + 1) state.sequenceGaps += value.sequence - state.lastSequence - 1;
      else if (value.sequence <= state.lastSequence) state.outOfOrder++;
      if (state.lastAt !== null) {
        state.lastInterarrivalMs = at - state.lastAt;
        state.maximumInterarrivalMs = Math.max(state.maximumInterarrivalMs, state.lastInterarrivalMs);
      }
    }
    state.lastSequence = value.sequence;
    state.lastAt = at;
    state.count++;
    const metrics = value.snapshot?.metrics;
    if (Array.isArray(metrics)) {
      for (const metric of metrics) {
        const reading = metric?.id === 'diagnostics.apiDuration' ? metric.reading : null;
        if (reading?.status === 'fresh' && Number.isFinite(reading.value) && typeof reading.sampledAtUtc === 'string')
          state.apiDurationSamples.push({ sampledAtUtc: reading.sampledAtUtc, milliseconds: reading.value });
      }
    }
  });
});

const readBridgeEventEvidence = async () => page.evaluate(() => {
  const state = window.__loadQaBridge;
  return state ? { count: state.count, sequenceGaps: state.sequenceGaps, outOfOrder: state.outOfOrder,
    lastInterarrivalMs: state.lastInterarrivalMs, maximumInterarrivalMs: state.maximumInterarrivalMs,
    apiDurationSamples: state.apiDurationSamples.splice(0) } : null;
});

const navigateAndMeasure = async (locale, theme, sectionIndex) => {
  const language = page.locator('header .preferences select').nth(0);
  const colorTheme = page.locator('header .preferences select').nth(1);
  await language.selectOption(locale);
  await colorTheme.selectOption(theme);
  await page.locator(`html[lang="${locale}"][data-theme="${theme}"]`).waitFor({ state: 'attached', timeout: 5000 });
  const nav = page.getByRole('navigation', { name: 'qFrey-Tuner' });
  const button = nav.getByRole('button', { name: localeData[locale].sections[sectionIndex], exact: true });
  const started = performance.now();
  await button.click();
  const elapsedMs = performance.now() - started;
  assert.equal(await button.getAttribute('aria-current'), 'page');
  uiLatenciesMs.push(elapsedMs);
  const ui = await captureUiState(sectionIndex === 0);
  return { locale, theme, section: localeData[locale].sections[sectionIndex], latencyMs: elapsedMs, ...ui };
};

const writeEvidence = (status, extra = {}) => {
  writeFileSync(join(runDir, 'samples.jsonl'), rawSamples.map(sample => JSON.stringify(sample)).join('\n') + '\n');
  writeFileSync(join(runDir, 'api-requests.jsonl'), apiRequestSamples.map(sample => JSON.stringify(sample)).join('\n') + '\n');
  const apiSamples = apiDurations;
  const privateSetSamples = rawSamples.map(sample => sample.process?.privateWorkingSetBytes)
    .filter(value => Number.isSafeInteger(value) && value >= 0);
  const cpuSamples = rawSamples.map(sample => sample.process?.cpuPercent)
    .filter(value => typeof value === 'number' && Number.isFinite(value));
  const firstPrivate = privateSetSamples[0] ?? null;
  const lastPrivate = privateSetSamples.at(-1) ?? null;
  const report = {
    schemaVersion: 1,
    status,
    mockOnly: true,
    startedUtc: rawSamples[0]?.sampledAtUtc ?? null,
    endedUtc: rawSamples.at(-1)?.sampledAtUtc ?? utcNow(),
    requestedDurationSeconds: durationSeconds,
    observedDurationSeconds: rawSamples.length > 1 ? (Date.parse(rawSamples.at(-1).sampledAtUtc) - Date.parse(rawSamples[0].sampledAtUtc)) / 1000 : 0,
    candidate: rootProcessIdentity ? { pid: ownedPid, startTimeTicks: rootProcessIdentity.createdTicks,
      executable: rootProcessIdentity.image, sha256: extra.candidateSha256 ?? null } : null,
    browser: extra.browser ?? null,
    host: { platform: os.platform(), release: os.release(), osVersion: os.version(), architecture: os.arch(), logicalProcessors: os.cpus().length,
      processorModel: os.cpus()[0]?.model ?? null, totalMemoryBytes: os.totalmem(), activePowerScheme: powerScheme },
    runtime: { status: 'partial', webViewChromiumVersion: extra.browser?.chromiumVersion ?? null,
      dotnetRuntime: 'notExposedByTheProductUi' },
    syntheticFixture: { torrents: syntheticTorrents.length, uniqueHashes: new Set(syntheticTorrents.map(item => item.hash)).size,
      totalResponseBytes: Buffer.byteLength(torrentJson), realTorrentsOrDownloads: false },
    api: { counts: Object.fromEntries(apiCounts), postCount: apiPostCount, errorCount: apiErrors,
      responseDurationMs: summary(apiSamples),
      candidateEndpointDurationMs: summary(clientApiDurationSamples.map(item => item.milliseconds)),
      candidateEndpointDurationSource: 'diagnostics.apiDuration fresh readings in bridge snapshots; local queue excluded',
      timingScope: 'local mock server receive-to-response callback, not client queue time' },
    ui: { navigationActionLatencyMs: summary(uiLatenciesMs), rawTranslationKeys: 0, horizontalOverflowSamples: 0,
      screenshots: [...screenshots] },
    processTree: { samples: rawSamples.length, privateWorkingSetUnknown: unknownMetricCounts.privateWorkingSet,
      cpuUnknown: unknownMetricCounts.cpu, privateWorkingSetSource: 'Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate',
      cpuSource: 'delta(Win32_Process.KernelModeTime + UserModeTime) / elapsed / logical processor count',
      privateWorkingSetBytes: summary(privateSetSamples), cpuPercentOfAllLogicalCores: summary(cpuSamples),
      memoryEndpoints: { firstBytes: firstPrivate, lastBytes: lastPrivate,
        deltaBytes: firstPrivate === null || lastPrivate === null ? null : lastPrivate - firstPrivate,
        deltaPercent: firstPrivate === null || firstPrivate === 0 || lastPrivate === null ? null : (lastPrivate - firstPrivate) / firstPrivate * 100 },
      noPrivateMemorySize64Substitution: true },
    bridgeEvents: rawSamples.filter(sample => sample.bridgeEvents !== null).at(-1)?.bridgeEvents ?? null,
    queueEvidence: { status: 'notExposed', reason: 'The candidate exposes endpoint request duration with local queue excluded, but no request-admission queue duration.' },
    eventLagEvidence: { status: 'interarrivalOnly', reason: 'Bridge snapshots have a sequence but no source timestamp; transit lag cannot be derived.',
      maximumSnapshotInterarrivalMs: rawSamples.reduce((max, sample) => Math.max(max, sample.bridgeEvents?.maximumInterarrivalMs ?? 0), 0) || null },
    artifacts: { report: 'report.json', rawSamples: 'samples.jsonl', apiRequests: 'api-requests.jsonl', screenshots: [...screenshots] },
    ...extra,
  };
  writeFileSync(join(runDir, 'report.json'), JSON.stringify(report, null, 2));
};

const serveApi = createServer((request, response) => {
  const started = performance.now();
  const key = `${request.method} ${request.url ?? ''}`;
  apiCounts.set(key, (apiCounts.get(key) ?? 0) + 1);
  const route = (request.url ?? '').replace(/^\/api\/v2\//, '').split(/[?#]/, 1)[0];
  const remote = request.socket.remoteAddress;
  if (remote !== '127.0.0.1' && remote !== '::ffff:127.0.0.1') {
    apiErrors++;
    response.writeHead(403).end(() => recordApiSample(request.method, route, 403, 0, started));
    return;
  }
  if (request.method !== 'GET') {
    apiPostCount++;
    apiErrors++;
    const body = 'Mock is read-only.';
    response.writeHead(405, { 'cache-control': 'no-store' }).end(body, () => recordApiSample(request.method, route, 405, Buffer.byteLength(body), started));
    return;
  }
  const fixture = routes[route];
  if (!fixture) {
    apiErrors++;
    const body = 'Unknown mock route.';
    response.writeHead(404, { 'cache-control': 'no-store' }).end(body, () => recordApiSample(request.method, route, 404, Buffer.byteLength(body), started));
    return;
  }
  const finish = () => recordApiSample(request.method, route, 200, Buffer.byteLength(fixture[0]), started);
  response.writeHead(200, { 'content-type': fixture[1], 'content-length': Buffer.byteLength(fixture[0]), 'cache-control': 'no-store' });
  response.end(fixture[0], finish);
});

try {
  const apiPort = await listen(serveApi);
  apiServer = serveApi;
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${cdpPort}`);
  const pages = browser.contexts().flatMap(context => context.pages());
  assert.equal(pages.length, 1, 'Expected exactly one root-owned isolated WebView page.');
  page = pages[0];
  assert.match(page.url(), /^https:\/\/qfrey\.local\//);
  await page.locator('header .preferences select').nth(0).selectOption('en-US');
  await page.locator('header .preferences select').nth(1).selectOption('dark');
  const initialText = await page.locator('body').innerText();
  assert.match(initialText, localeData['en-US'].disconnected,
    'Refusing to connect a candidate that is already connected; start from the disconnected QA profile.');
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Experiment', exact: true }).click();
  assert.ok(await page.getByText('Not measured', { exact: true }).count() >= 2, 'Refusing to reuse an existing experiment profile.');
  await page.getByText('Not applied', { exact: true }).waitFor();
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Overview', exact: true }).click();
  await installBridgeEventEvidence();

  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Overview', exact: true }).click();
  const form = page.locator('main form').first();
  await form.locator('input[type="url"]').fill(`http://127.0.0.1:${apiPort}`);
  await form.locator('select').first().selectOption('bypass');
  await form.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByText('qBittorrent v5.2.0', { exact: false }).waitFor({ state: 'visible', timeout: 15_000 });
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Overview', exact: true }).click();
  await page.locator('section.card').filter({ has: page.getByRole('heading', { name: 'Live metrics', exact: true }) })
    .waitFor({ state: 'visible', timeout: 10_000 });
  await page.locator('.metric-table').getByRole('table').waitFor({ state: 'visible', timeout: 10_000 });

  // One explicit read-only baseline forces the actual telemetry path to consume all 1,000 rows.
  // It sends only GET requests; the mock rejects every write method.
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Setup', exact: true }).click();
  await page.locator('#setup-download').fill('900');
  await page.locator('#setup-upload').fill('60');
  await page.getByRole('button', { name: 'Build preview', exact: true }).click();
  await page.getByRole('heading', { level: 1, name: 'Recommendations', exact: true })
    .waitFor({ state: 'visible', timeout: 15_000 });
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Experiment', exact: true }).click();
  await page.getByLabel('Existing torrent info hashes', { exact: true }).fill(syntheticTorrents[0].hash);
  const torrentsBeforeBaseline = apiCounts.get('GET /api/v2/torrents/info') ?? 0;
  const measurementStartedUtc = utcNow();
  await page.getByRole('button', { name: 'Start baseline measurement', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel measurement', exact: true }).waitFor({ state: 'visible', timeout: 15_000 });
  await delay(1_000);
  await navigateAndMeasure('ru-RU', 'light', 5);
  await navigateAndMeasure('en-US', 'dark', 0);
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Experiment', exact: true }).click();
  const validDeadline = Date.now() + 150_000;
  while (Date.now() < validDeadline && await page.getByText('Valid', { exact: true }).count() === 0)
    await delay(500);
  await page.getByText('Valid', { exact: true }).first().waitFor({ state: 'visible', timeout: 1_000 });
  const readOnlyBaseline = {
    startedUtc: measurementStartedUtc,
    completedUtc: utcNow(),
    status: 'valid',
    torrentInfoReads: (apiCounts.get('GET /api/v2/torrents/info') ?? 0) - torrentsBeforeBaseline,
  };
  assert.ok(readOnlyBaseline.torrentInfoReads >= 72,
    `Expected the selected synthetic baseline to scan 1,000-row telemetry at least 72 times; saw ${readOnlyBaseline.torrentInfoReads}.`);
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Overview', exact: true }).click();
  await captureUiState();

  const processSeed = await readProcessTree();
  assert.ok(rootProcessIdentity, 'Could not bind the explicitly owned qFrey PID to its executable and start time.');
  const exeHash = createHash('sha256').update(readFileSync(rootProcessIdentity.image)).digest('hex');
  const candidateSha256 = exeHash;
  const browserVersion = browser.version();
  const capture = async name => {
    const file = join(runDir, `${name}.png`);
    await page.screenshot({ path: file, fullPage: true });
    screenshots.push(relative(root, file));
  };
  const startupUi = [];
  for (const [locale, theme] of [['en-US', 'dark'], ['en-US', 'light'], ['ru-RU', 'dark'], ['ru-RU', 'light']])
    startupUi.push(await navigateAndMeasure(locale, theme, 0));
  await capture('connected-start');

  const settleSeconds = Math.min(60, durationSeconds);
  const settleStartedUtc = utcNow();
  if (settleSeconds > 0) await delay(settleSeconds * 1000);
  await captureUiState();
  const settleCompletedUtc = utcNow();
  previousProcessSample = undefined;
  const runStarted = Date.now();
  const endAt = runStarted + durationSeconds * 1000;
  let lastNavigation = 0;
  let firstLoopDelay = null;
  let maxLoopDelayMs = 0;
  let nextSampleAt = Date.now();
  while (Date.now() < endAt) {
    const schedulerLagMs = Math.max(0, Date.now() - nextSampleAt);
    const sampledAtUtc = utcNow();
    const proc = await readProcessTree();
    if (proc.privateWorkingSetBytes === null) unknownMetricCounts.privateWorkingSet++;
    if (proc.cpuPercent === null) unknownMetricCounts.cpu++;
    const runtimeSeconds = (Date.now() - runStarted) / 1000;
    if (firstLoopDelay === null) firstLoopDelay = schedulerLagMs;
    maxLoopDelayMs = Math.max(maxLoopDelayMs, schedulerLagMs);
    if (Date.now() - lastNavigation >= 60_000 || rawSamples.length === 0) {
      const navigationStep = navigationCount++;
      const locale = navigationStep % 2 === 0 ? 'en-US' : 'ru-RU';
      const theme = Math.floor(navigationStep / 2) % 2 === 0 ? 'dark' : 'light';
      const sectionIndex = navigationStep % localeData[locale].sections.length;
      const ui = await navigateAndMeasure(locale, theme, sectionIndex);
      const bridgeEvents = await readBridgeEventEvidence();
      if (bridgeEvents) clientApiDurationSamples.push(...bridgeEvents.apiDurationSamples);
      rawSamples.push({ sampledAtUtc, runtimeSeconds, apiRequestCount: apiDurations.length,
        process: { ...proc, processCounters: undefined, wallTimeMs: undefined }, ui, bridgeEvents, schedulerLagMs });
      lastNavigation = Date.now();
    } else {
      const bridgeEvents = await readBridgeEventEvidence();
      if (bridgeEvents) clientApiDurationSamples.push(...bridgeEvents.apiDurationSamples);
      rawSamples.push({ sampledAtUtc, runtimeSeconds, apiRequestCount: apiDurations.length,
        process: { ...proc, processCounters: undefined, wallTimeMs: undefined }, bridgeEvents, schedulerLagMs });
    }
    if (rawSamples.length % 12 === 0) writeEvidence('running', { candidateSha256, browser: { engine: 'WebView2 over CDP', chromiumVersion: browserVersion,
      cdpPort, mockApiEndpoint: `http://127.0.0.1:${apiPort}` }, startupUi, scheduler: { firstSampleLagMs: firstLoopDelay,
      maximumObservedDelayMs: maxLoopDelayMs }, settleSeconds, settleStartedUtc, settleCompletedUtc, readOnlyBaseline,
      settleNote: 'Connected mock telemetry remains active; this is not an idle-process measurement.' });
    nextSampleAt += 5_000;
    if (Date.now() < endAt) await delay(Math.max(0, Math.min(nextSampleAt - Date.now(), endAt - Date.now())));
  }

  const finalUi = [];
  for (const [locale, theme] of [['en-US', 'dark'], ['ru-RU', 'light']])
    finalUi.push(await navigateAndMeasure(locale, theme, 0));
  await capture('connected-end');
  assert.equal(apiPostCount, 0, 'No qBittorrent write request is allowed in this read-only load run.');
  assert.equal(apiErrors, 0, 'Mock API reported an unexpected route or remote caller.');
  assert.ok(apiCounts.get('GET /api/v2/torrents/info') > 0, 'The client never read the synthetic 1000-torrent inventory.');
  writeEvidence('passed', { candidateSha256, browser: { engine: 'WebView2 over CDP', chromiumVersion: browserVersion,
    cdpPort, mockApiEndpoint: `http://127.0.0.1:${apiPort}` }, startupUi, finalUi,
    scheduler: { firstSampleLagMs: firstLoopDelay, maximumObservedDelayMs: maxLoopDelayMs }, settleSeconds,
    settleStartedUtc, settleCompletedUtc, readOnlyBaseline,
    idleMemory: { status: 'notMeasured', reason: 'Connected telemetry remained active throughout the run.' },
    startupColdWarm: { status: 'notMeasured', reason: 'This harness receives an already running candidate; it does not launch the EXE.' },
    pythonBaseline: { status: 'notMeasured', reason: 'No matching Python application baseline was run by this harness.' } });
  console.log(JSON.stringify({ status: 'passed', mockOnly: true, runDir, samples: rawSamples.length,
    durationSeconds, apiPostCount, syntheticTorrents: syntheticTorrents.length, candidateSha256 }, null, 2));
} catch (error) {
  let failureScreenshot = null;
  if (page) {
    failureScreenshot = join(runDir, 'failure.png');
    await page.screenshot({ path: failureScreenshot, fullPage: true }).catch(() => {});
    screenshots.push(relative(root, failureScreenshot));
  }
  writeEvidence('failed', { error: String(error?.message ?? error), candidateSha256: null,
    browser: { engine: 'WebView2 over CDP', cdpPort }, failureScreenshot: failureScreenshot ? relative(root, failureScreenshot) : null });
  throw error;
} finally {
  if (browser) await browser.close().catch(() => {});
  if (apiServer) {
    apiServer.closeAllConnections();
    await new Promise(resolve => apiServer.close(resolve));
  }
}
