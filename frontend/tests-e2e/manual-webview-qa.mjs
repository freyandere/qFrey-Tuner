import assert from 'node:assert/strict';
import { mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
const require=createRequire(new URL('../../frontend/package.json',import.meta.url)); const {chromium}=require('@playwright/test');
const root=fileURLToPath(new URL('../../',import.meta.url)); const out=`${root}.cache/e2e/webview`; mkdirSync(out,{recursive:true});
const port=Number(process.argv[2]??53047);
assert.ok(Number.isInteger(port)&&port>=1024&&port<=65535,'expected a local test port in 1024..65535');
const browser=await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
let helper;
try {
 const pages=browser.contexts().flatMap(context=>context.pages()); assert.equal(pages.length,1,'expected only the isolated qFrey-Tuner WebView page'); const page=pages[0];
 assert.match(page.url(),/^https:\/\/qfrey\.local\//); assert.equal(await page.title(),'qFrey-Tuner');
 const body=page.locator('body'); await body.getByText(/^(Not connected|Нет подключения)$/).waitFor({state:'visible'});
 const sections={ 'en-US':['Overview','Setup','Recommendations','Experiment','Results','History'], 'ru-RU':['Обзор','Условия','Рекомендации','Эксперимент','Результаты','История']}; let i=0;
 for(const locale of ['en-US','ru-RU']) { await page.getByRole('combobox').nth(0).selectOption(locale); for(const theme of ['dark','light']) { await page.getByRole('combobox').nth(1).selectOption(theme); for(const name of sections[locale]) { const nav=page.getByRole('navigation',{name:'qFrey-Tuner'}); const button=nav.getByRole('button',{name,exact:true}); await button.click(); assert.equal(await button.getAttribute('aria-current'),'page',`${locale}/${theme}/${name} did not become current`); assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth),false,`horizontal overflow ${locale}/${theme}/${name}`); await page.screenshot({path:`${out}/shell-${locale}-${theme}-${++i}.png`,fullPage:true}); } } }
 await page.getByRole('combobox').nth(0).selectOption('en-US'); await page.getByRole('combobox').nth(1).selectOption('system');
 if(process.argv[3] === '--owned-mock-connect') {
  helper=spawn(`${root}.cache/bin/QFrey.NativeHelper/Debug/net10.0/qbittorrent.exe`,['--isolated-mock'],{cwd:root,windowsHide:true,stdio:['pipe','pipe','pipe']});
  const mockPort=await new Promise((resolve,reject)=>{let text=''; const timer=setTimeout(()=>reject(new Error('mock startup timeout')),10000); helper.once('error',error=>{clearTimeout(timer);reject(error);}); helper.stdout.on('data',chunk=>{text+=String(chunk); if(text.includes('\n')){clearTimeout(timer);resolve(Number(text.trim()));}});});
  assert.ok(Number.isInteger(mockPort)&&mockPort>0&&mockPort<=65535);
  await page.getByRole('navigation').getByRole('button',{name:'Overview',exact:true}).click();
  const form=page.locator('form'); await form.locator('input[type=url]').fill(`http://127.0.0.1:${mockPort}`); await form.locator('select').selectOption('bypass'); await form.locator('button[type=submit]').click();
  await page.getByText('qBittorrent v5.2.0',{exact:false}).waitFor();
  await page.getByText('Process private memory',{exact:true}).waitFor();
  await page.getByRole('navigation').getByRole('button',{name:'Setup',exact:true}).click();
  await page.getByRole('button',{name:'Build preview',exact:true}).click();
  await page.getByText('Preview only:',{exact:false}).waitFor();
  assert.equal(await page.getByRole('button',{name:/^Apply/}).count(),0);
  for(const locale of ['en-US','ru-RU']) { await page.getByRole('combobox').nth(0).selectOption(locale); for(const theme of ['dark','light']) { await page.getByRole('combobox').nth(1).selectOption(theme); assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth),false); await page.screenshot({path:`${out}/preview-${locale}-${theme}.png`,fullPage:true}); } }
  console.log('Owned mock API connection, local telemetry and backend plan preview verified; no real qBittorrent used.');
  for(const locale of ['en-US','ru-RU']) {
   await page.getByRole('combobox').nth(0).selectOption(locale);
   for(const theme of ['dark','light']) {
    await page.getByRole('combobox').nth(1).selectOption(theme);
    await page.evaluate(()=>window.scrollTo(0,0));
    await page.screenshot({path:`${out}/preview-viewport-${locale}-${theme}.png`,fullPage:false});
    await page.getByRole('navigation').getByRole('button',{name:sections[locale][0],exact:true}).click();
    await page.getByRole('figure').filter({has:page.getByText(locale==='ru-RU'?'История текущих скоростей':'Live transfer history',{exact:false})}).waitFor();
    await page.evaluate(()=>window.scrollTo(0,0));
    await page.screenshot({path:`${out}/live-viewport-${locale}-${theme}.png`,fullPage:false});
    await page.getByRole('navigation').getByRole('button',{name:sections[locale][2],exact:true}).click();
   }
  }
 }
 console.log(JSON.stringify({browser:'Edge over WebView2 CDP',page:page.url(),connectionStatus:helper?'Validated owned mock API':'Not connected',screenshots:i+(helper?12:0),profile:'isolated .cache test profile',ordinaryQbittorrentUsed:false},null,2));
 await page.evaluate(()=>window.close()).catch(error=>{if(!/closed|disconnect/i.test(String(error)))throw error;});
} finally {
 if(helper) { const exited=new Promise(resolve=>helper.once('exit',resolve)); helper.stdin.end('quit\n'); if(helper.exitCode===null) await Promise.race([exited,new Promise((_,reject)=>setTimeout(()=>reject(new Error('owned mock did not exit')),10000))]); }
 await browser.close().catch(()=>{});
}
