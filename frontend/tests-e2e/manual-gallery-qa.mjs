import { mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require=createRequire(new URL('../../frontend/package.json',import.meta.url)); const {chromium,expect}=require('@playwright/test');
const root=fileURLToPath(new URL('../../',import.meta.url)); mkdirSync(`${root}.cache/e2e/gallery`,{recursive:true});
const browser=await chromium.launch({channel:'msedge',headless:true,timeout:10000});
try {
 const page=await browser.newPage({viewport:{width:960,height:680}}); await page.goto('http://127.0.0.1:4174/gallery.html'); await page.getByRole('heading',{level:1}).waitFor({state:'visible'});
 const reports={};
 for(const locale of ['en-US','ru-RU']) for(const theme of ['dark','light']) {
   await page.locator('#gallery-locale').selectOption(locale);
   await page.locator('#gallery-theme').selectOption(theme);
   const state=await page.evaluate(()=>{const s=getComputedStyle(document.documentElement),names=['--text','--secondary','--accent','--warning','--error','--graph-download','--graph-upload'],bgs=['--canvas','--surface','--raised']; const colors=Object.fromEntries([...names,...bgs].map(k=>[k,s.getPropertyValue(k).trim()])); const rgb=x=>{if(!x.startsWith('#'))return x.match(/[\d.]+/g).slice(0,3).map(Number);let h=x.slice(1);if(h.length===3)h=[...h].map(v=>v+v).join('');return h.match(/.{2}/g).map(v=>parseInt(v,16));}; const lum=x=>{const c=rgb(x).map(v=>v/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4);return .2126*c[0]+.7152*c[1]+.0722*c[2]}; const ratios={}; for(const fg of names) for(const bg of bgs){const a=lum(colors[fg]),b=lum(colors[bg]);ratios[`${fg} on ${bg}`]=Number(((Math.max(a,b)+.05)/(Math.min(a,b)+.05)).toFixed(2));} return {colors,ratios};});
   const overflow=await page.evaluate(()=>document.documentElement.scrollWidth>document.documentElement.clientWidth); if(overflow) throw new Error(`overflow ${locale}/${theme}`);
   await page.screenshot({path:`${root}.cache/e2e/gallery/${locale}-${theme}.png`,fullPage:true}); reports[`${locale}/${theme}`]=state;
 }
 await page.locator('#gallery-locale').selectOption('en-US'); const tablist=page.getByRole('tablist'); await tablist.getByRole('tab',{name:'Results'}).focus(); await page.keyboard.press('ArrowRight'); await expect(tablist.getByRole('tab',{name:'History'})).toHaveAttribute('aria-selected','true');
 const focus=await tablist.getByRole('tab',{name:'History'}).evaluate(el=>({active:document.activeElement===el,outline:getComputedStyle(el).outline,width:getComputedStyle(el).outlineWidth,color:getComputedStyle(el).outlineColor}));
 await page.getByRole('button',{name:'Confirm'}).first().click(); const dialog=page.getByRole('dialog'); await expect(dialog).toBeVisible(); const modal=await dialog.evaluate(el=>({modal:el.matches(':modal'),focusTag:document.activeElement.tagName,focusText:document.activeElement.textContent})); await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible();
 await page.emulateMedia({forcedColors:'active'}); const forcedColors=await page.evaluate(()=>Object.fromEntries(['.badge','.status-warning','.tabs [role=tab][aria-selected=true]'].map(sel=>{const s=getComputedStyle(document.querySelector(sel));return [sel,{borderColor:s.borderTopColor,color:s.color}]})));
 console.log(JSON.stringify({themes:reports,tabFocus:focus,dialogOpen:modal,dialogClosedByEscape:true,forcedColors},null,2));
} finally {await browser.close();}
