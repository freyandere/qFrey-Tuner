import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { join, relative } from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const require = createRequire(new URL('../package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const debugPort = Number(process.argv[2]);
assert.ok(Number.isInteger(debugPort) && debugPort >= 1024 && debugPort <= 65535, 'Pass the candidate WebView2 CDP port (1024–65535).');

const settingsFixture = JSON.parse(readFileSync(join(root, 'tests-contract/fixtures/legacy/settings-payloads.json'), 'utf8'))['2.0.11'].payload;
const preferences = { ...settingsFixture, scheduler_enabled: false };
const originalPreferences = structuredClone(preferences);
const infoHash = 'c'.repeat(40);
const torrentSize = 128 * 1024 * 1024;
const apiCounts = new Map();
const preferenceReadbacksAfterPosts = [];
let postCount = 0;
let setPreferencesPosts = 0;
let lastPreferenceReadbackPost = 0;
let applied = false;

const api = createServer(async (request, response) => {
  const route = (request.url ?? '').split('?')[0].replace(/^\/api\/v2\//, '');
  const key = `${request.method} ${route}`;
  apiCounts.set(key, (apiCounts.get(key) ?? 0) + 1);
  const send = (status, body, contentType = 'application/json; charset=utf-8') => {
    response.writeHead(status, { 'content-type': contentType, 'cache-control': 'no-store' });
    response.end(body);
  };

  if (request.method === 'GET') {
    if (route === 'app/preferences' && setPreferencesPosts > lastPreferenceReadbackPost) {
      preferenceReadbacksAfterPosts.push(setPreferencesPosts);
      lastPreferenceReadbackPost = setPreferencesPosts;
    }
    const body = {
      'app/version': ['v5.1.0', 'text/plain; charset=utf-8'],
      'app/webapiVersion': ['2.11.0', 'text/plain; charset=utf-8'],
      'app/buildInfo': [JSON.stringify({ libtorrent: '2.0.11' }), 'application/json; charset=utf-8'],
      'app/preferences': [JSON.stringify(preferences), 'application/json; charset=utf-8'],
      'app/networkInterfaceList': [JSON.stringify([{ value: 'eth0', name: 'Ethernet' }]), 'application/json; charset=utf-8'],
      'transfer/info': [JSON.stringify({ dl_info_speed: (applied ? 6 : 5) * 1024 * 1024, up_info_speed: 256 * 1024,
        dht_nodes: 12, use_alt_speed_limits: false }), 'application/json; charset=utf-8'],
      'torrents/info': [JSON.stringify([{ hash: infoHash, state: 'downloading', progress: 0.25,
        dlspeed: (applied ? 6 : 5) * 1024 * 1024, upspeed: 256 * 1024, num_seeds: 8, num_leechs: 12,
        name: 'Owned synthetic QA payload', total_size: torrentSize, save_path: '/mock/downloads' }]), 'application/json; charset=utf-8'],
    }[route];
    if (!body) return send(404, 'Not found', 'text/plain; charset=utf-8');
    return send(200, body[0], body[1]);
  }

  postCount++;
  if (request.method !== 'POST' || route !== 'app/setPreferences')
    return send(404, 'Unexpected mock API write', 'text/plain; charset=utf-8');
  setPreferencesPosts++;
  let encoded = '';
  for await (const chunk of request) {
    encoded += chunk;
    if (encoded.length > 65536) return send(413, 'Too large', 'text/plain; charset=utf-8');
  }
  try {
    const values = JSON.parse(new URLSearchParams(encoded).get('json') ?? '');
    assert.ok(values && typeof values === 'object' && !Array.isArray(values));
    for (const [name, value] of Object.entries(values)) {
      assert.ok(Object.hasOwn(preferences, name), 'Mock rejected an unexpected preference key.');
      const current = preferences[name];
      assert.equal(typeof value, typeof current, 'Mock rejected a preference type change.');
      if (typeof value === 'number') assert.ok(Number.isSafeInteger(value), 'Mock accepts only safe integer settings.');
    }
    Object.assign(preferences, values);
    applied = setPreferencesPosts % 2 === 1;
    send(200, '', 'text/plain; charset=utf-8');
  } catch {
    send(400, 'Invalid mock preferences', 'text/plain; charset=utf-8');
  }
});

const listen = server => new Promise((resolve, reject) => {
  server.once('error', reject);
  server.listen(0, '127.0.0.1', () => resolve(server.address().port));
});
const locales = { 'en-US': { nav: ['Overview', 'Setup', 'Recommendations', 'Experiment', 'Results', 'History'], hashes: 'Existing torrent info hashes' },
  'ru-RU': { nav: ['Обзор', 'Условия', 'Рекомендации', 'Эксперимент', 'Результаты', 'История'], hashes: 'Info-хеши существующих торрентов' } };
const confirmations = { 'en-US': { apply: 'Review apply', rollback: 'Review rollback', confirm: 'Confirm and continue', cancel: 'Cancel' },
  'ru-RU': { apply: 'Просмотреть применение', rollback: 'Просмотреть откат', confirm: 'Подтвердить и продолжить', cancel: 'Отмена' } };
const runDir = join(root, '.cache/e2e/experiment-webview', `${Date.now()}-${Math.random().toString(16).slice(2)}`);
mkdirSync(runDir, { recursive: true });

let browser;
let page;
let completed = false;
let approvalEvidence = null;
const screenshots = [];
const assertCleanAndCapture = async (page, name, locale, theme) => {
  const languageSelect = page.locator('header .preferences select').nth(0);
  const themeSelect = page.locator('header .preferences select').nth(1);
  await languageSelect.selectOption(locale);
  await themeSelect.selectOption(theme);
  await page.locator(`html[lang="${locale}"][data-theme="${theme}"]`).waitFor({ state: 'attached', timeout: 5000 });
  const text = await page.locator('body').innerText();
  assert.doesNotMatch(text, /\b(?:errors|experiment|mutation|setup|recommendations|results|history|metrics)\.[A-Za-z][A-Za-z0-9_.-]*\b/, `Raw translation key in ${name}/${locale}/${theme}.`);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false,
    `Horizontal overflow in ${name}/${locale}/${theme}.`);
  const path = join(runDir, `${name}-${locale}-${theme}.png`);
  await page.screenshot({ path, fullPage: true });
  screenshots.push(relative(root, path));
};
const setLocaleTheme = async (page, locale, theme) => {
  await page.locator('header .preferences select').nth(0).selectOption(locale);
  await page.locator('header .preferences select').nth(1).selectOption(theme);
  await page.locator(`html[lang="${locale}"][data-theme="${theme}"]`).waitFor({ state: 'attached', timeout: 5000 });
};
const captureMatrix = async (page, name) => {
  for (const locale of ['en-US', 'ru-RU'])
    for (const theme of ['dark', 'light']) await assertCleanAndCapture(page, name, locale, theme);
  await setLocaleTheme(page, 'en-US', 'dark');
};
const navigate = async (page, locale, name) => page.getByRole('navigation', { name: 'qFrey-Tuner' })
  .getByRole('button', { name: locales[locale].nav[name], exact: true }).click();
const activeCancelDuringMeasurement = async (page, locale) => {
  await navigate(page, locale, 5);
  await page.getByRole('button', { name: locale === 'ru-RU' ? 'Отменить операцию' : 'Cancel operation', exact: true }).waitFor({ state: 'visible' });
  await navigate(page, locale, 3);
  await page.getByRole('button', { name: locale === 'ru-RU' ? 'Отменить замер' : 'Cancel measurement', exact: true }).waitFor({ state: 'visible' });
};
const measure = async (page, kind) => {
  const beforeTorrentReads = apiCounts.get('GET torrents/info') ?? 0;
  const startName = kind === 'baseline' ? 'Start baseline measurement' : 'Start after measurement';
  await page.getByRole('button', { name: startName, exact: true }).click();
  await page.getByRole('button', { name: 'Cancel measurement', exact: true }).waitFor({ state: 'visible', timeout: 15000 });
  const started = Date.now();
  await activeCancelDuringMeasurement(page, 'en-US');
  while (Date.now() - started < 120000) {
    if (await page.getByRole('button', { name: 'Cancel measurement', exact: true }).count() === 0) break;
    await page.waitForTimeout(500);
  }
  await page.getByRole('button', { name: 'Cancel measurement', exact: true }).waitFor({ state: 'detached', timeout: 1000 }).catch(() => {});
  const expectedValidStatuses = kind === 'after' ? 2 : 1;
  const validDeadline = Date.now() + 10000;
  while (Date.now() < validDeadline && await page.getByText('Valid', { exact: true }).count() < expectedValidStatuses)
    await page.waitForTimeout(250);
  assert.ok(await page.getByText('Valid', { exact: true }).count() >= expectedValidStatuses,
    `Expected ${expectedValidStatuses} valid measurement status(es) after ${kind}.`);
  const torrentReads = (apiCounts.get('GET torrents/info') ?? 0) - beforeTorrentReads;
  assert.ok(torrentReads >= 72, `Expected initial/final context plus 70 real collector reads; observed ${torrentReads}.`);
  await captureMatrix(page, `${kind}-valid`);
  return torrentReads;
};

try {
  const apiPort = await listen(api);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`);
  const pages = browser.contexts().flatMap(context => context.pages());
  assert.equal(pages.length, 1, 'Expected one isolated qFrey-Tuner WebView page.');
  page = pages[0];
  await page.evaluate(() => {
    const webview = window.chrome?.webview;
    if (!webview) return;
    const commands = new Map();
    const postMessage = webview.postMessage.bind(webview);
    webview.postMessage = message => {
      if (message.command === 'AcceptPlan') commands.set(message.requestId, true);
      return postMessage(message);
    };
    window.__qaAcceptPlanReplies = [];
    webview.addEventListener('message', event => {
      const reply = event.data;
      if (!reply || typeof reply !== 'object' || !commands.has(reply.requestId)) return;
      commands.delete(reply.requestId);
      const data = reply.ok ? reply.data : null;
      const plan = data?.experiment?.plan;
      window.__qaAcceptPlanReplies.push({
        requestId: reply.requestId, ok: reply.ok, errorCode: reply.error?.code ?? null,
        revision: reply.revision, snapshotRevision: data?.revision ?? null,
        planId: plan?.id ?? null, planRevision: plan?.revision ?? null,
        approved: plan?.approved ?? null, previewOnly: plan?.previewOnly ?? null,
        applicable: plan?.applicable ?? null, phase: data?.phase ?? null,
        availableActions: (data?.availableActions ?? []).filter(action => action.id === 'AcceptPlan')
          .map(action => ({ id: action.id, enabled: action.enabled, reasonCode: action.reasonCode }))
      });
    });
  });
  assert.match(page.url(), /^https:\/\/qfrey\.local\//);
  await page.locator('header .preferences select').nth(0).selectOption('en-US');
  await page.locator('header .preferences select').nth(1).selectOption('dark');
  assert.match(await page.locator('body').innerText(), /Not connected/,
    'The candidate must start disconnected; refusing to touch an existing target or experiment.');
  await navigate(page, 'en-US', 3);
  assert.ok(await page.getByText('Not measured', { exact: true }).count() >= 2, 'Refusing a candidate with an existing experiment.');
  await page.getByText('Not applied', { exact: true }).waitFor();
  await navigate(page, 'en-US', 0);
  await captureMatrix(page, 'disconnected');

  await navigate(page, 'en-US', 1);
  const connection = page.locator('main form').first();
  await connection.locator('input[type="url"]').fill(`http://127.0.0.1:${apiPort}`);
  await connection.locator('select').first().selectOption('bypass');
  await connection.getByRole('button', { name: 'Connect', exact: true }).click();
  await page.getByText('qBittorrent v5.1.0', { exact: false }).waitFor({ state: 'visible', timeout: 15000 });
  await captureMatrix(page, 'connected');

  await navigate(page, 'en-US', 1);
  await page.locator('#setup-download').fill('900');
  await page.locator('#setup-upload').fill('60');
  await page.getByRole('button', { name: 'Build preview', exact: true }).click();
  await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: 'Recommendations', exact: true }).click();
  await captureMatrix(page, 'prebaseline-preview');

  await navigate(page, 'en-US', 3);
  await page.getByLabel(locales['en-US'].hashes, { exact: true }).fill(infoHash);
  const baselineTorrentReads = await measure(page, 'baseline');
  await navigate(page, 'en-US', 5);
  await page.getByText('Plan ready', { exact: true }).waitFor({ state: 'visible', timeout: 15000 });
  await page.getByRole('button', { name: /^Open read-only results:/ }).first().click();
  await page.getByText('Historical result · this does not describe current settings.', { exact: true }).waitFor({ state: 'visible' });
  await page.getByText('Valid', { exact: true }).waitFor({ state: 'visible' });
  await page.getByRole('button', { name: 'Current experiment results', exact: true }).click();
  await navigate(page, 'en-US', 1);
  await page.getByRole('button', { name: 'Build preview', exact: true }).click();
  await navigate(page, 'en-US', 2);
  await captureMatrix(page, 'plan-review');
  await navigate(page, 'en-US', 3);
  await page.getByRole('button', { name: 'Approve plan', exact: true }).click();
  await page.getByText('Approval applies to this plan revision only. A changed plan needs a new review.', { exact: true }).waitFor({ state: 'visible', timeout: 15000 });
  approvalEvidence = await page.evaluate(() => window.__qaAcceptPlanReplies ?? []);
  assert.equal(approvalEvidence.length, 1, 'Expected exactly one observed AcceptPlan reply.');
  assert.equal(approvalEvidence[0].ok, true, 'AcceptPlan should succeed.');
  assert.equal(approvalEvidence[0].approved, true, 'AcceptPlan reply should approve this plan revision.');
  assert.equal(approvalEvidence[0].applicable, true, 'Only an applicable plan may be approved.');
  await captureMatrix(page, 'approved-plan');

  await page.getByRole('button', { name: confirmations['en-US'].apply, exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.waitFor({ state: 'visible' });
  await assertCleanAndCapture(page, 'apply-confirmation', 'en-US', 'dark');
  await dialog.getByRole('button', { name: confirmations['en-US'].cancel, exact: true }).click();
  await setLocaleTheme(page, 'ru-RU', 'light');
  await page.getByRole('button', { name: confirmations['ru-RU'].apply, exact: true }).click();
  await dialog.waitFor({ state: 'visible' });
  await assertCleanAndCapture(page, 'apply-confirmation', 'ru-RU', 'light');
  await dialog.getByRole('button', { name: confirmations['ru-RU'].confirm, exact: true }).click();
  await page.getByText('Проверено', { exact: true }).waitFor({ state: 'visible', timeout: 30000 });
  assert.equal(setPreferencesPosts, 1, 'Apply should send exactly one mock setPreferences POST.');
  assert.ok(preferenceReadbacksAfterPosts.includes(1), 'Apply must be followed by a qBittorrent preferences readback.');
  assert.notDeepEqual(preferences, originalPreferences, 'Apply must change mock settings before rollback.');
  await captureMatrix(page, 'applied-verified');

  await navigate(page, 'en-US', 3);
  const afterTorrentReads = await measure(page, 'after');
  await navigate(page, 'en-US', 4);
  await page.getByText('Result from the current experiment.', { exact: true }).first().waitFor({ state: 'visible', timeout: 15000 });
  assert.ok(await page.getByText('Valid', { exact: true }).count() >= 2, 'Both measurement summaries should be valid.');
  await captureMatrix(page, 'comparison-results');

  await navigate(page, 'en-US', 3);
  await page.getByRole('button', { name: confirmations['en-US'].rollback, exact: true }).click();
  await dialog.waitFor({ state: 'visible' });
  await assertCleanAndCapture(page, 'rollback-confirmation', 'en-US', 'dark');
  await dialog.getByRole('button', { name: confirmations['en-US'].cancel, exact: true }).click();
  await setLocaleTheme(page, 'ru-RU', 'light');
  await page.getByRole('button', { name: confirmations['ru-RU'].rollback, exact: true }).click();
  await dialog.waitFor({ state: 'visible' });
  await assertCleanAndCapture(page, 'rollback-confirmation', 'ru-RU', 'light');
  await dialog.getByRole('button', { name: confirmations['ru-RU'].confirm, exact: true }).click();
  await page.getByText('Откачено', { exact: true }).waitFor({ state: 'visible', timeout: 30000 });
  assert.equal(setPreferencesPosts, 2, 'Rollback should add exactly one mock setPreferences POST.');
  assert.ok(preferenceReadbacksAfterPosts.includes(2), 'Rollback must be followed by a qBittorrent preferences readback.');
  assert.deepEqual(preferences, originalPreferences, 'Rollback must restore every mock preference to its original value.');
  assert.equal(postCount, setPreferencesPosts, 'No other mock API write is allowed.');
  await captureMatrix(page, 'rolled-back');
  await navigate(page, 'en-US', 5);
  await page.getByText('Rolled back', { exact: true }).waitFor({ state: 'visible', timeout: 15000 });
  await captureMatrix(page, 'persisted-rolled-back-history');
  completed = true;
  writeFileSync(join(runDir, 'report.json'), JSON.stringify({ status: 'passed', postCount,
    setPreferencesPostCount: setPreferencesPosts, acceptPlanReply: approvalEvidence,
    baselineValid: true, afterValid: true, rollbackVerified: true,
    preferenceReadbacksAfterPosts, mockPreferencesRestored: true,
    realTorrentReadsPerMeasurement: [baselineTorrentReads, afterTorrentReads], screenshots }, null, 2));
} catch (error) {
  const failureEvidence = page ? await page.evaluate(() => ({
    alerts: [...document.querySelectorAll('[role="alert"], .error')].map(element => element.innerText).slice(0, 8),
    approvePlanButtons: [...document.querySelectorAll('main button')].filter(button => /approve plan|подтвердить план/i.test(button.innerText))
      .map(button => ({ disabled: button.disabled, label: button.innerText.trim() })).slice(0, 4),
    screen: document.querySelector('main h1')?.innerText ?? null,
    approvalReplies: window.__qaAcceptPlanReplies ?? []
  })).catch(() => null) : null;
  if (page) {
    const path = join(runDir, 'failure.png');
    await page.screenshot({ path, fullPage: true }).catch(() => {});
    screenshots.push(relative(root, path));
  }
  writeFileSync(join(runDir, 'report.json'), JSON.stringify({ status: 'failed', postCount,
    setPreferencesPostCount: setPreferencesPosts, screenshots, failureEvidence }, null, 2));
  throw error;
} finally {
  if (browser) await browser.close().catch(() => {});
  api.closeAllConnections();
  await new Promise(resolve => api.close(resolve));
  if (!completed) process.exitCode = 1;
}
