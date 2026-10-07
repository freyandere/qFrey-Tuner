import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { join, relative } from 'node:path';

const require = createRequire(new URL('../package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const root = fileURLToPath(new URL('../../', import.meta.url));
const initial = JSON.parse(readFileSync(new URL('../../tests-contract/fixtures/protocol/initialize-success.json', import.meta.url), 'utf8'));
const origin = new URL(process.argv[2] ?? 'http://127.0.0.1:4173/');
assert.ok(['http:', 'https:'].includes(origin.protocol) && ['127.0.0.1', 'localhost'].includes(origin.hostname)
  && !origin.username && !origin.password, 'Use a local production/preview URL without credentials.');
const channel = process.env.QFREY_QA_BROWSER ?? 'msedge';
assert.ok(['msedge', 'chrome'].includes(channel), 'Use msedge or chrome for QFREY_QA_BROWSER.');
const out = join(root, '.cache/e2e/restore-browser');
mkdirSync(out, { recursive: true });
const labels = {
  'en-US': { history: 'History', choose: 'Choose and review a backup', restore: 'Restore original settings…',
    dialog: 'Confirm backup restoration', confirm: 'Confirm and restore', cancel: 'Cancel', conflict: 'Conflict',
    completed: 'Original settings restored and verified by readback. Previous experiment results remain in history.' },
  'ru-RU': { history: 'История', choose: 'Выбрать и проверить копию', restore: 'Восстановить исходные настройки…',
    dialog: 'Подтверждение восстановления копии', confirm: 'Подтвердить и восстановить', cancel: 'Отмена', conflict: 'Конфликт',
    completed: 'Исходные настройки восстановлены и проверены повторным чтением. Результаты прежних экспериментов сохранены в истории.' },
};
const report = { status: 'running', origin: origin.href, channel, mockOnly: true, cases: [], screenshots: [], commands: [] };
let browser;
let page;
try {
  browser = await chromium.launch({ channel, headless: true });
  page = await browser.newPage({ viewport: { width: 1200, height: 840 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', route => {
    const url = new URL(route.request().url());
    if (url.origin === origin.origin && ['GET', 'HEAD'].includes(route.request().method())) return route.continue();
    errors.push(`Unexpected network request: ${route.request().method()} ${url.origin}${url.pathname}`);
    return route.abort();
  });
  await page.addInitScript(initial => {
    const handlers = [];
    const sessionId = '11111111-1111-4111-8111-111111111111';
    const selectionToken = 'A'.repeat(64);
    const confirmationToken = 'B'.repeat(64);
    let sequence = 0;
    const data = structuredClone(initial.data);
    data.connection = 'validated';
    data.target = { sessionId, endpoint: 'http://mock.invalid', qbittorrentVersion: '5.1.0', apiVersion: '2.11.3', libtorrentVersion: '2.0.11', isLocal: false };
    data.confirmations = [];
    const commands = [];
    const emit = value => handlers.forEach(handler => handler({ data: structuredClone(value) }));
    const future = () => new Date(Date.now() + 600000).toISOString();
    const review = scenario => {
      data.revision++;
      data.activeOperation = null;
      data.availableActions = [];
      data.restore = { selectionToken, displayName: 'mock-legacy-backup.json',
        sourceCycleId: '22222222-2222-4222-8222-222222222222', review: {
          isLegacy: true, targetMatches: true, canRestore: true, alreadyOriginal: false, fingerprint: 'c'.repeat(64),
          blockReasonCodes: [], differences: [{ key: 'max_connec', original: 500, intended: 1000, current: 1000, disposition: 'restoreRequired' }],
        } };
      const confirmation = { token: confirmationToken, targetSessionId: sessionId, revision: data.revision,
        expiresUtc: future(), actionId: 'RestoreLegacyBackup', description: { key: 'restore.confirmTitle', parameters: {} } };
      data.confirmations = [confirmation];
      if (scenario === 'missing') data.confirmations = [];
      if (scenario === 'expired') confirmation.expiresUtc = new Date(Date.now() - 60000).toISOString();
      if (scenario === 'wrongRevision') confirmation.revision--;
      if (scenario === 'wrongTarget') confirmation.targetSessionId = '33333333-3333-4333-8333-333333333333';
      if (scenario === 'wrongAction') confirmation.actionId = 'Rollback';
      if (scenario === 'duplicate') data.confirmations.push({ ...confirmation, token: 'D'.repeat(64) });
      if (scenario === 'conflict') {
        data.restore.review.canRestore = false;
        data.restore.review.blockReasonCodes = ['EXTERNAL_DRIFT'];
        data.restore.review.differences[0].current = 750;
        data.restore.review.differences[0].disposition = 'conflict';
      }
    };
    window.restoreQa = { commands, scenario: scenario => {
      review(scenario);
      emit({ sessionId, operationId: null, sequence: ++sequence, revision: data.revision, snapshot: data });
    } };
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type, handler) => handlers.push(handler),
      postMessage: request => {
        commands.push(structuredClone(request));
        let replyData;
        if (request.command === 'Initialize') replyData = data;
        else if (request.command === 'SetUiPreferences') {
          data.preferences = { ...request.payload }; data.revision++; replyData = data;
        } else if (request.command === 'ListHistory') replyData = { items: [], nextCursor: null };
        else if (request.command === 'SelectNativeFile' && request.payload.purpose === 'restore') {
          replyData = { token: selectionToken, purpose: 'restore', displayName: 'mock-legacy-backup.json', expiresUtc: future() };
        } else if (request.command === 'RequestConfirmation' && request.payload.actionId === 'RestoreLegacyBackup'
          && request.payload.selectionToken === selectionToken) {
          review('eligible'); replyData = data;
        } else if (request.command === 'RestoreLegacyBackup') {
          const confirmation = data.confirmations[0];
          if (data.confirmations.length !== 1 || !data.restore.review.canRestore
            || data.restore.review.differences.some(item => item.disposition === 'conflict')
            || request.payload.selectionToken !== selectionToken || request.payload.confirmationToken !== confirmation.token
            || confirmation.actionId !== 'RestoreLegacyBackup' || confirmation.targetSessionId !== sessionId
            || confirmation.revision !== data.revision || Date.parse(confirmation.expiresUtc) <= Date.now()
            || request.targetSessionId !== sessionId || request.expectedRevision !== data.revision)
            throw new Error('Mock refused an inexact restore request');
          data.revision++; data.restore = null; data.confirmations = [];
          data.availableActions = [{ id: 'restore.completed', enabled: false, reasonCode: null, messageKey: 'restore.completed' }];
          replyData = { operationId: '44444444-4444-4444-8444-444444444444', revision: data.revision };
        } else throw new Error(`Unexpected mock command: ${request.command}`);
        const reply = { requestId: request.requestId, ok: true, revision: data.revision, data: structuredClone(replyData), error: null };
        queueMicrotask(() => emit(reply));
      },
    } } });
    document.addEventListener('DOMContentLoaded', () => {
      const marker = document.createElement('div');
      marker.dataset.testid = 'mock-bridge-marker';
      marker.textContent = 'PLAYWRIGHT MOCK BRIDGE · NO qBittorrent';
      Object.assign(marker.style, { position: 'fixed', bottom: '8px', right: '8px', zIndex: '9999', padding: '6px 8px', background: '#7c2d12', color: 'white', font: '12px sans-serif' });
      document.body.append(marker);
    }, { once: true });
  }, initial);
  await page.goto(origin.href);
  await page.getByRole('heading', { level: 1 }).waitFor();
  assert.equal(await page.locator('[data-testid="mock-bridge-marker"]').count(), 1);
  const restores = () => page.evaluate(() => window.restoreQa.commands.filter(command => command.command === 'RestoreLegacyBackup'));
  const capture = async (locale, theme, state) => {
    for (const [width, height] of [[1200, 840], [960, 680]]) {
      await page.setViewportSize({ width, height });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false,
        `Overflow ${locale}/${theme}/${state}/${width}`);
      const layout = await page.evaluate(() => {
        const dialog = document.querySelector('dialog[open]');
        const bounds = dialog?.getBoundingClientRect();
        return {
          dialog: bounds ? { centerX: bounds.left + bounds.width / 2, centerY: bounds.top + bounds.height / 2,
            viewportWidth: innerWidth, viewportHeight: innerHeight } : null,
          cells: [...document.querySelectorAll('.table-scroll th, .table-scroll td')].map(cell => {
            const style = getComputedStyle(cell);
            return { left: parseFloat(style.paddingLeft), right: parseFloat(style.paddingRight) };
          }),
        };
      });
      assert.ok(layout.cells.every(cell => cell.left >= 8 && cell.right >= 8),
        `Readable table cell spacing ${locale}/${theme}/${state}/${width}`);
      if (state === 'confirmation') {
        assert.ok(layout.dialog, 'Confirmation dialog must remain open');
        assert.ok(Math.abs(layout.dialog.centerX - layout.dialog.viewportWidth / 2) <= 2
          && Math.abs(layout.dialog.centerY - layout.dialog.viewportHeight / 2) <= 2,
        `Centered dialog ${locale}/${theme}/${width}`);
      }
      const path = join(out, `${locale}-${theme}-${state}-${width}.png`);
      await page.screenshot({ path, fullPage: true });
      report.screenshots.push(relative(root, path));
    }
    assert.doesNotMatch(await page.locator('main').innerText(), /\b(?:restore|errors|recommendations)\.[a-zA-Z]+/);
    assert.equal(await page.getByRole('alert').count(), 0, 'Unexpected product error');
  };
  for (const locale of ['en-US', 'ru-RU']) {
    await page.locator('header .preferences select').nth(0).selectOption(locale);
    const text = labels[locale];
    await page.getByRole('navigation', { name: 'qFrey-Tuner' }).getByRole('button', { name: text.history, exact: true }).click();
    for (const theme of ['dark', 'light']) {
      await page.locator('header .preferences select').nth(1).selectOption(theme);
      const button = page.getByRole('button', { name: text.restore, exact: true });
      const before = (await restores()).length;
      await page.getByRole('button', { name: text.choose, exact: true }).click();
      await button.waitFor();
      assert.equal(await button.isEnabled(), true);
      assert.equal((await restores()).length, before, 'Review must not restore');
      await capture(locale, theme, 'review');
      for (const scenario of ['missing', 'expired', 'wrongRevision', 'wrongTarget', 'wrongAction', 'duplicate', 'conflict']) {
        await page.evaluate(async scenario => {
          window.restoreQa.scenario(scenario);
          await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        }, scenario);
        await page.waitForFunction(() => [...document.querySelectorAll('main button')]
          .some(button => /^(Restore original settings|Восстановить исходные настройки)/.test(button.textContent) && button.disabled));
        assert.equal(await button.isDisabled(), true, scenario);
        assert.equal((await restores()).length, before, `${scenario} must not restore`);
        if (scenario === 'conflict') {
          await page.getByRole('cell', { name: text.conflict, exact: true }).waitFor();
          await capture(locale, theme, scenario);
        }
      }
      await page.evaluate(() => window.restoreQa.scenario('eligible'));
      await button.click();
      const dialog = page.getByRole('dialog', { name: text.dialog, exact: true });
      await dialog.waitFor({ state: 'visible' });
      await capture(locale, theme, 'confirmation');
      await dialog.getByRole('button', { name: text.cancel, exact: true }).click();
      assert.equal((await restores()).length, before, 'Cancel must not restore');
      await button.click();
      await dialog.waitFor({ state: 'visible' });
      await page.evaluate(() => window.restoreQa.scenario('wrongRevision'));
      await dialog.waitFor({ state: 'hidden' });
      assert.equal((await restores()).length, before, 'Revision change must dismiss confirmation without restoring');
      await page.getByRole('button', { name: text.choose, exact: true }).click();
      await button.click();
      await dialog.getByRole('button', { name: text.confirm, exact: true }).click();
      await page.getByText(text.completed, { exact: true }).waitFor();
      const sent = await restores();
      assert.equal(sent.length, before + 1, 'Exactly one restore per confirmation');
      assert.deepEqual(sent.at(-1).payload, { selectionToken: 'A'.repeat(64), confirmationToken: 'B'.repeat(64) });
      assert.equal(await button.count(), 0, 'Completed snapshot must clear restore review');
      await capture(locale, theme, 'accepted');
      report.cases.push({ locale, theme, denied: ['missing', 'expired', 'wrongRevision', 'wrongTarget', 'wrongAction', 'duplicate', 'conflict'],
        reviewDoesNotRestore: true, cancelDoesNotRestore: true, revisionDismissesDialog: true, exactRestoreAccepted: true,
        dialogCentered: true, tableCellSpacing: true });
    }
  }
  assert.deepEqual(errors, [], 'No browser or unexpected network errors');
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = String(error.stack ?? error);
  if (page) await page.screenshot({ path: join(out, 'failure.png'), fullPage: true }).catch(() => {});
  throw error;
} finally {
  if (page) report.commands = await page.evaluate(() => window.restoreQa?.commands ?? []).catch(() => []);
  writeFileSync(join(out, 'report.json'), JSON.stringify(report, null, 2));
  if (browser) await browser.close();
  console.log(JSON.stringify({ status: report.status, cases: report.cases.length, screenshots: report.screenshots.length,
    report: relative(root, join(out, 'report.json')) }));
}
