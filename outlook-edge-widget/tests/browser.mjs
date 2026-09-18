import { chromium } from '@playwright/test';
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { runContractChecks } from './browser-contract.mjs';
process.argv.push('--port','0');
const {server}=await import('../scripts/preview.mjs');
await new Promise(resolve=>server.listening?resolve():server.once('listening',resolve));
const port=server.address().port;
const browser=await chromium.launch({channel:'msedge',headless:true});
const context=await browser.newContext();const page=await context.newPage();
const errors=[],requests=[];page.on('pageerror',e=>errors.push(e.stack));page.on('request',r=>requests.push(r.url()));
await mkdir('test-results',{recursive:true});
try {
  await page.goto(`http://127.0.0.1:${port}/outlook/?demo=1&instance=browser`);
  await page.waitForFunction(()=>window.outlookDemo);
  await page.getByRole('button',{name:'Calendars',exact:true}).click();
  await page.getByRole('checkbox',{name:'Select all calendars'}).check();
  await page.keyboard.press('Escape');
  for(const [name,width,height]of [['xl',2560,720],['l',1280,720],['m',850,720],['mobile',390,844]]) {
    await page.setViewportSize({width,height});
    for(const view of ['Week','Agenda','Month']) {
      await page.getByRole('button',{name:view,exact:true}).click();
      await page.waitForTimeout(200);
      assert.ok(await page.locator('#calendar [role="grid"],#calendar [role="list"]').count(),`${name} ${view} calendar`);
      assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false,`${name} page overflow`);
      await page.screenshot({path:`test-results/${name}-${view.toLowerCase()}.png`});
    }
  }
  await page.setViewportSize({width:1280,height:720});
  await page.getByRole('button',{name:'Week',exact:true}).click();
  assert.equal(await page.getByText('Medical appointment',{exact:true}).count(),0);
  const event=page.locator('[data-event-id]').first();await event.click();
  await page.getByRole('dialog').getByRole('heading',{name:'Description'}).waitFor();
  const title=await page.locator('#dialog-title').textContent();
  await page.evaluate(()=>window.outlookDemo.refresh());
  assert.equal(await page.locator('#dialog-title').textContent(),title);
  if(await page.getByRole('button',{name:'Join meeting',exact:true}).count()) {
    await page.getByRole('button',{name:'Join meeting',exact:true}).click();
    assert.equal(await page.evaluate(()=>window.outlookDemo.api.launched.length),1);
  }
  await page.keyboard.press('Escape');
  assert.equal(await page.getByRole('dialog').count(),0);
  // Closing on a backdrop click requires the pointer to have started on that backdrop.
  await event.click();await page.getByRole('heading',{name:'Description',exact:true}).waitFor();
  await page.locator('.dialog').dispatchEvent('pointerdown');await page.locator('.overlay').dispatchEvent('click');
  assert.equal(await page.getByRole('dialog').count(),1);
  await page.locator('.overlay').dispatchEvent('pointerdown');await page.locator('.overlay').dispatchEvent('click');
  assert.equal(await page.getByRole('dialog').count(),0);
  await page.evaluate(()=>{window.outlookDemo.api.offline=true;return window.outlookDemo.refresh();});
  assert.match(await page.locator('.status').textContent(),/Offline/);
  assert.ok(await page.locator('[data-event-id]').count());
  await page.evaluate(()=>window.outlookDemo.navigate(1));
  await page.getByText('Calendar unavailable for this range',{exact:true}).waitFor();
  await page.evaluate(()=>{window.outlookDemo.api.offline=false;return window.outlookDemo.refresh();});
  await page.evaluate(()=>window.outlookDemo.navigate(-1));
  await page.locator('[data-event-id]').first().waitFor();
  // Private details are authorized only in the dialog, never promoted into summaries.
  const privateEvent=page.locator('[data-event-id][aria-label^="Private event"]').first();
  await privateEvent.click();await page.getByRole('heading',{name:'Medical appointment',exact:true}).waitFor();
  assert.equal(await page.locator('.shell').getByText('Medical appointment',{exact:true}).count(),0);
  await page.keyboard.press('Tab');assert.equal(await page.evaluate(()=>document.querySelector('.dialog').contains(document.activeElement)),true);
  await page.keyboard.press('Escape');assert.equal(await page.getByText('Medical appointment',{exact:true}).count(),0);
  // Same-range polling and metadata refresh preserve the actual time scroller.
  const beforeScroll=await page.evaluate(()=>{const n=[...document.querySelectorAll('#calendar *')].find(n=>n.scrollHeight>n.clientHeight+200 && getComputedStyle(n).overflowY==='auto');n.scrollTop=620;n.dataset.testScroller='true';return n.scrollTop;});
  await page.evaluate(()=>window.outlookDemo.refresh(true));
  assert.equal(await page.locator('[data-test-scroller]').evaluate(n=>n.scrollTop),beforeScroll);
  // An older detail request cannot replace the details opened by a newer tap.
  await page.evaluate(async()=>{
    const demo=window.outlookDemo,events=demo.state.events.filter(e=>!e.isPrivate),first=events[0],second=events[1];
    let release;const firstResponse=new Promise(resolve=>release=resolve);
    const api={post:async(path,body)=>body.reference===first.reference?firstResponse:{...second,title:'Second selected event',attendees:[]}};
    const canUse=()=>true;
    demo.dialogs.details(first,{name:'Fixture'},api,'UTC',canUse);
    await demo.dialogs.details(second,{name:'Fixture'},api,'UTC',canUse);
    release({...first,title:'Obsolete private details',attendees:[]});await Promise.resolve();
  });
  assert.equal(await page.locator('#dialog-title').textContent(),'Second selected event');await page.keyboard.press('Escape');
  // Opening overflow must leave the selected view unchanged.
  await page.getByRole('button',{name:'Month',exact:true}).click();
  await page.locator('.outlook-more').first().click();
  await page.getByRole('dialog').waitFor();assert.ok(await page.locator('.overflow-event').count());
  await page.locator('.overflow-event').first().click();await page.getByRole('heading',{name:'Description',exact:true}).waitFor();await page.keyboard.press('Escape');
  assert.equal(await page.getByRole('button',{name:'Month',exact:true}).getAttribute('aria-pressed'),'true');
  // Six-row month and full agenda spans are engine-rendered.
  await page.evaluate(()=>window.outlookDemo.navigate(-1));
  assert.equal(await page.locator('#calendar [role="gridcell"][data-date]').count(),42);
  await page.getByRole('button',{name:'Agenda',exact:true}).click();await page.getByRole('button',{name:'30 days',exact:true}).click();
  await page.getByText('No events',{exact:true}).first().waitFor();
  // In-document settings work without Qt native popup controls.
  await page.getByRole('button',{name:'Calendar settings',exact:true}).click();
  assert.equal(await page.locator('select,datalist').count(),0);
  await page.getByRole('radio',{name:'Friday',exact:true}).check();
  await page.getByRole('radio',{name:'Custom hours',exact:true}).check();
  await page.getByRole('textbox',{name:'Workday starts',exact:true}).fill('22:00');await page.getByRole('textbox',{name:'Workday starts',exact:true}).press('Tab');
  await page.getByRole('searchbox',{name:'Search time zones',exact:true}).fill('Kathmandu');
  await page.getByRole('radio',{name:'Asia/Kathmandu',exact:true}).check();
  await page.keyboard.press('Escape');
  await page.reload();await page.waitForFunction(()=>window.outlookDemo);
  assert.equal(await page.evaluate(()=>window.outlookDemo.settings().firstDay),5);
  assert.equal(await page.evaluate(()=>window.outlookDemo.settings().timeZone),'Asia/Kathmandu');
  const other=await context.newPage();await other.goto(`http://127.0.0.1:${port}/outlook/?demo=1&instance=other`);await other.waitForFunction(()=>window.outlookDemo);
  assert.equal(await other.evaluate(()=>window.outlookDemo.settings().firstDay),0);await other.close();
  assert.equal(errors.length,0,errors.join('\n'));
  assert.equal(requests.some(url=>url.includes('/api/')),false,'demo must not call helper');
  await runContractChecks(browser,`http://127.0.0.1:${port}`);
  console.log('Browser checks passed: four widths, three views, privacy/races, dialogs/focus, fake Join, offline/recovery, six-week month, settings, two instances, preview renewal, native file pairing. No live API reads.');
}catch(error){console.error('Page errors:',errors);console.error((await page.locator('body').innerText()).slice(0,2000));console.error((await page.locator('#calendar').innerHTML()).slice(0,1000));await page.screenshot({path:'test-results/failure.png'});throw error;}
finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
