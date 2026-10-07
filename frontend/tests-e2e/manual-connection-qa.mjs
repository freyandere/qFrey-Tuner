import { readFileSync, mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
const require = createRequire(new URL('../package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const initial = JSON.parse(readFileSync(new URL('../../tests-contract/fixtures/protocol/initialize-success.json', import.meta.url), 'utf8'));
const out = new URL('../../.cache/e2e/connection/', import.meta.url);
mkdirSync(out, { recursive: true });
const channel = process.env.QFREY_QA_BROWSER ?? 'msedge';
if (!['msedge', 'chrome'].includes(channel)) throw new Error('Use msedge or chrome for QFREY_QA_BROWSER');
const browser = await chromium.launch({ channel, headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 960, height: 680 } });
  await page.addInitScript(initial => {
    const handlers = []; let data = structuredClone(initial.data); let fail = false; let sequence = 0;
    window.mockConnection = { failNext: () => { fail = true; }, publish: () => {
      data.metrics = [
        { id: 'transfer.download', unit: 'bytesPerSecond', scope: 'session', source: 'transfer/info:dl_info_speed', reading: { status: 'fresh', value: 1048576, sampledAtUtc: new Date().toISOString() } },
        { id: 'transfer.upload', unit: 'bytesPerSecond', scope: 'session', source: 'transfer/info:up_info_speed', reading: { status: 'unavailable', reasonCode: 'FIELD_MISSING' } },
        { id: 'session.dhtNodes', unit: 'count', scope: 'session', source: 'transfer/info:dht_nodes', reading: { status: 'error', reasonCode: 'INVALID_METRIC_TYPE' } } ];
      const event = { sessionId: data.target.sessionId, operationId: null, sequence: ++sequence, revision: data.revision, snapshot: structuredClone(data) };
      handlers.forEach(h => h({ data: event }));
    } };
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_, h) => handlers.push(h),
      postMessage: request => {
        let error = null;
        if (request.command === 'SetUiPreferences') { data.preferences = request.payload; data.revision++; }
        if (request.command === 'Connect') {
          data.revision++; data.target = null; data.connection = 'disconnected'; data.metrics = [];
          if (fail) { fail = false; error = { code: 'AUTHENTICATION_FAILED', messageKey: 'errors.authenticationFailed', severity: 'error', retry: 'none', recoveryActionIds: [], correlationId: crypto.randomUUID() }; }
          else { data.target = { sessionId: crypto.randomUUID(), endpoint: request.payload.endpoint, qbittorrentVersion: 'v5.2.0', apiVersion: '2.15.0', libtorrentVersion: '2.0.11', isLocal: true }; data.connection = 'validated'; sequence = 0; }
        }
        if (request.command === 'Disconnect') { data.target = null; data.connection = 'disconnected'; data.metrics = []; data.revision++; }
        if (request.command === 'BuildPlan') {
          data.revision++;
          const selection=request.payload.selections.find(s=>s.groupId==='max_connec');
          const proposed=selection?.overrides.max_connec ?? 500;
          data.experiment={cycleId:null,runInputs:request.payload.inputs,workload:null,baseline:null,after:null,results:[],plan:{
            id:crypto.randomUUID(),targetSessionId:data.target.sessionId,revision:data.revision,createdUtc:new Date().toISOString(),inputsFingerprint:'mock-inputs',baselineFingerprint:'',previewOnly:true,applicable:false,approved:false,
            blockReasonCodes:['BASELINE_REQUIRED'],original:{max_connec:200},proposed:{max_connec:proposed},omissions:[],recommendations:[{
              id:'recommendation-max_connec',groupId:'max_connec',apiKey:'max_connec',category:'stability',titleKey:'recommendations.max_connec',reason:{key:selection?.overrides.max_connec===undefined?'recommendations.reason.heuristic':'recommendations.reason.manualOverride',parameters:{}},evidence:'heuristic',currentValue:200,proposedValue:proposed,
              unit:'count',valueType:'number',editable:true,allowedRange:{minimum:1,maximum:2147483647,step:1},allowedValues:[{value:-1,labelKey:'settings.values.unlimited'}],supportStatus:'supported',selected:selection?.selected??true,cautionCodes:[]}]}};
        }
        const reply = { requestId: request.requestId, ok: !error, revision: data.revision, data: error ? null : structuredClone(data), error };
        queueMicrotask(() => handlers.forEach(h => h({ data: reply })));
      }
    } } });
  }, initial);
  await page.goto('http://127.0.0.1:4173/');
  await page.waitForFunction(() => getComputedStyle(document.body).fontFamily.includes('Segoe'), null, { timeout: 10000 });
  for (const locale of ['en-US', 'ru-RU']) {
    await page.getByRole('combobox').nth(0).selectOption(locale);
    for (const theme of ['dark', 'light']) {
      await page.getByRole('combobox').nth(1).selectOption(theme);
      const form = page.locator('form');
      await form.locator('input[type=url]').fill('http://127.0.0.1:54321');
      await form.locator('select').selectOption('bypass');
      await form.locator('button[type=submit]').click();
      await page.getByText('qBittorrent v5.2.0', { exact: false }).waitFor();
      await page.evaluate(() => window.mockConnection.publish());
      await page.getByRole('cell', { name: locale === 'en-US' ? '1 MiB/s' : '1 МиБ/с', exact: true }).waitFor();
      if (await page.getByRole('cell', { name: '—', exact: true }).count() !== 2) throw new Error('Missing/error became numeric values');
      if (await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth)) throw new Error('Horizontal overflow');
      await page.screenshot({ path: new URL(`${locale}-${theme}.png`, out).pathname.replace(/^\/(\w:)/, '$1'), fullPage: true });
      await form.locator('button[type=button]').click();
    }
  }
  await page.getByRole('combobox').nth(0).selectOption('en-US');
  await page.getByRole('navigation').getByRole('button',{name:'Overview',exact:true}).click();
  await page.locator('form select').selectOption('bypass'); await page.locator('form button[type=submit]').click();
  await page.getByRole('navigation').getByRole('button',{name:'Setup',exact:true}).click();
  await page.getByLabel('Download speed (Mbps)',{exact:true}).fill('123,456');
  await page.getByRole('combobox').nth(0).selectOption('ru-RU');
  if(Number((await page.locator('#setup-download').inputValue()).replace(',','.'))!==123.456) throw new Error('Locale switch lost precise draft');
  await page.getByRole('combobox').nth(0).selectOption('en-US');
  await page.getByRole('button',{name:'Build preview',exact:true}).click();
  await page.getByText('Preview only:',{exact:false}).waitFor();
  const manual=page.getByRole('textbox',{name:'Proposed Maximum connections',exact:true});
  await manual.fill('321'); await manual.press('Enter');
  await page.getByText(/^(This value was manually changed for the plan\.|Manual value selected for this plan\.)/).waitFor();
  if(await manual.inputValue()!=='321') throw new Error('Manual override did not survive rebuild');
  for(const locale of ['en-US','ru-RU']) { await page.getByRole('combobox').nth(0).selectOption(locale); for(const theme of ['dark','light']) { await page.getByRole('combobox').nth(1).selectOption(theme); await page.screenshot({path:new URL(`preview-${locale}-${theme}.png`,out).pathname.replace(/^\/(\w:)/,'$1'),fullPage:true}); } }
  await page.getByRole('combobox').nth(0).selectOption('en-US');
  await page.getByRole('navigation').getByRole('button',{name:'Overview',exact:true}).click();
  const form = page.locator('form');
  await form.locator('input[type=url]').fill('http://127.0.0.1:54321');
  await form.locator('select').selectOption('password');
  await form.locator('input:not([type])').fill('mock-user');
  await form.locator('input[type=password]').fill('mock-secret');
  await page.evaluate(() => window.mockConnection.failNext());
  await form.locator('button[type=submit]').click();
  await page.getByRole('alert').getByText('Authentication failed.', { exact: false }).waitFor();
  if (await form.locator('input[type=password]').inputValue() !== '' || await form.locator('input:not([type])').inputValue() !== '') throw new Error('Credentials retained in form');
  if (await page.getByText('Connected · compatibility verified', { exact: true }).count()) throw new Error('Failed reconnect retained validated target');
  console.log('PASS: 4 locale/theme connection+telemetry captures; missing/error, disconnect and failed authentication clear state/credentials. Mock bridge only.');
} finally { await browser.close(); }
