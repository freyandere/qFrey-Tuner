import { readFileSync, mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(new URL('../../frontend/package.json', import.meta.url));
const { chromium } = require('@playwright/test');
const root = fileURLToPath(new URL('../../', import.meta.url));
const fixture = JSON.parse(readFileSync(`${root}tests-contract/fixtures/protocol/initialize-success.json`, 'utf8'));
const screens = { 'en-US': ['Overview','Setup','Recommendations','Experiment','Results','History'], 'ru-RU': ['Обзор','Условия','Рекомендации','Эксперимент','Результаты','История'] };
const out = `${root}.cache/e2e/screenshots`;
mkdirSync(out, { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true, timeout: 10000 });
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 840 } });
  await page.addInitScript((initial) => {
    const handlers = []; let revision = initial.revision; let data = structuredClone(initial.data);
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_type, handler) => handlers.push(handler),
      postMessage: (message) => { if (message.command === 'SetUiPreferences' && message.payload) { data.preferences = { ...data.preferences, ...message.payload }; data.revision = ++revision; } const reply = { ...initial, requestId: message.requestId, revision, data: { ...data, revision }, error: null }; queueMicrotask(() => handlers.forEach(handler => handler({ data: reply }))); }
    } } });
    const marker = document.createElement('div'); marker.dataset.testid = 'mock-bridge-marker'; marker.textContent = 'PLAYWRIGHT MOCK BRIDGE · NO qBittorrent'; Object.assign(marker.style,{position:'fixed',bottom:'8px',right:'8px',zIndex:'9999',padding:'6px 8px',background:'#7c2d12',color:'white',font:'12px/1.2 sans-serif',borderRadius:'4px'}); document.addEventListener('DOMContentLoaded',()=>document.body.append(marker),{once:true});
  }, fixture);
  await page.goto('http://127.0.0.1:4173/');
  await page.getByRole('heading', {level:1}).waitFor({state:'visible',timeout:10000});
  let i=0;
  for (const locale of ['en-US','ru-RU']) {
    await page.getByRole('combobox').nth(0).selectOption(locale);
    for (const theme of ['dark','light']) {
      await page.getByRole('combobox').nth(1).selectOption(theme);
      for (const screen of screens[locale]) {
        const nav = page.getByRole('navigation',{name:'qFrey-Tuner'});
        const button = nav.getByRole('button',{name:screen,exact:true});
        await button.click();
        if (await button.getAttribute('aria-current') !== 'page') throw new Error(`nav failed ${locale}/${theme}/${screen}`);
        await page.setViewportSize({width:1200,height:840});
        const overflow = await page.evaluate(()=>document.documentElement.scrollWidth > document.documentElement.clientWidth);
        if (overflow) throw new Error(`overflow 1200 ${locale}/${theme}/${screen}`);
        await page.screenshot({path:`${out}/shell-${locale}-${theme}-${++i}.png`,fullPage:true});
        await page.setViewportSize({width:960,height:680});
        const narrow = await page.evaluate(()=>document.documentElement.scrollWidth > document.documentElement.clientWidth);
        if (narrow) throw new Error(`overflow 960 ${locale}/${theme}/${screen}`);
      }
    }
  }
  const themeResults = {};
  for (const theme of ['dark','light']) {
    await page.getByRole('combobox').nth(1).selectOption(theme);
    themeResults[theme] = await page.evaluate(() => {
      const s=getComputedStyle(document.documentElement), names=['--text','--secondary','--accent','--warning','--error'], bgNames=['--canvas','--surface','--raised'];
      const rgb=x=>{if(!x)return [0,0,0];if(!x.startsWith('#'))return x.match(/[\d.]+/g)?.slice(0,3).map(Number)??[0,0,0];let h=x.slice(1);if(h.length===3)h=[...h].map(v=>v+v).join('');return h.match(/.{2}/g).map(v=>parseInt(v,16));}; const lum=x=>{const c=rgb(x).map(v=>v/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return .2126*c[0]+.7152*c[1]+.0722*c[2]};
      const colors=Object.fromEntries([...names,...bgNames].map(n=>[n,s.getPropertyValue(n).trim()])); const ratios={};
      for(const fg of names) for(const bg of bgNames){const a=lum(colors[fg]),b=lum(colors[bg]);ratios[`${fg} on ${bg}`]=((Math.max(a,b)+.05)/(Math.min(a,b)+.05)).toFixed(2)}
      return {colors,ratios};
    });
  }
  await page.emulateMedia({colorScheme:'dark'}); await page.getByRole('combobox').nth(1).selectOption('system');
  const systemDark=await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim());
  await page.emulateMedia({colorScheme:'light'}); const systemLight=await page.evaluate(()=>getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim());
  await page.getByRole('combobox').nth(0).selectOption('en-US');
  const nav=page.getByRole('navigation',{name:'qFrey-Tuner'}); await nav.getByRole('button',{name:'Overview'}).focus(); await page.keyboard.press('Tab'); const focused=nav.getByRole('button',{name:'Setup'}); const focusVisible=await focused.evaluate(e=>({active:document.activeElement===e,outline:getComputedStyle(e).outline,width:getComputedStyle(e).outlineWidth,color:getComputedStyle(e).outlineColor}));
  console.log(JSON.stringify({screenshots:i,systemDark,systemLight,focusVisible,themeResults},null,2));
} finally { await browser.close(); }
