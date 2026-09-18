import { chromium } from '@playwright/test';
import assert from 'node:assert/strict';
process.argv.push('--port','0');
const {server}=await import('../scripts/preview.mjs');
await new Promise(resolve=>server.listening?resolve():server.once('listening',resolve));
const browser=await chromium.launch({channel:'msedge',headless:true});
try {
  const page=await browser.newPage({hasTouch:true,viewport:{width:1280,height:720}});
  await page.goto(`http://127.0.0.1:${server.address().port}/outlook/?demo=1`);
  const event=page.locator('[data-event-id]').first();
  await event.waitFor();
  await event.tap();
  await page.getByRole('heading',{name:'Description',exact:true}).waitFor();
  assert.equal(await page.locator('.shell').evaluate(n=>n.inert),false,'Modal must not toggle embedded calendar inert state');
  assert.equal(await page.locator('.shell').getAttribute('aria-hidden'),'true');
  await page.locator('.shell button').first().evaluate(n=>n.focus());
  assert.equal(await page.locator('.dialog').evaluate(n=>n.contains(document.activeElement)),true,'Focus must remain inside the modal');
  await page.touchscreen.tap(5,350);
  assert.equal(await page.getByRole('dialog').count(),0);
  assert.equal(await page.locator('.shell').getAttribute('aria-hidden'),null);
  assert.equal(await page.locator('[data-event-id]').evaluateAll(nodes=>nodes.includes(document.activeElement)),false,'Touch dismissal must not force focus back into the calendar');
  const client=await page.context().newCDPSession(page);
  const target=await page.evaluate(()=>{
    const n=[...document.querySelectorAll('#calendar *')].find(n=>n.scrollHeight>n.clientHeight+100 && ['auto','scroll'].includes(getComputedStyle(n).overflowY));
    if(!n)throw new Error('Missing calendar scroller');
    n.dataset.touchTest='true';const r=n.getBoundingClientRect();
    return {x:Math.round(r.x+140),y:Math.round(r.y+r.height-60),before:n.scrollTop};
  });
  await client.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:target.x,y:target.y}]});
  for(let i=1;i<=12;i++) {
    await client.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:target.x,y:target.y-i*15}]});
    await page.waitForTimeout(20);
  }
  await client.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});
  const after=await page.locator('[data-touch-test]').evaluate(n=>n.scrollTop);
  assert.ok(after>target.before+30,`Touch scroll after dismiss: ${target.before} -> ${after}`);
  console.log('Touch scroll after backdrop dismissal passed.');
} finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
