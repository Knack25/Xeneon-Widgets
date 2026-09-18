import {chromium} from '@playwright/test';
import {readFile,mkdir} from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve('..'),output=path.join(root,'docs/images');
process.argv.push('--port','0');
const {server}=await import('../scripts/preview.mjs');
await new Promise(resolve=>server.listening?resolve():server.once('listening',resolve));
const browser=await chromium.launch({channel:'msedge',headless:true});
try {
  await mkdir(output,{recursive:true});
  const page=await browser.newPage({viewport:{width:1280,height:720}});
  const base=`http://127.0.0.1:${server.address().port}`;
  // Screenshots use fixtures only; never allow this context to contact the live helper.
  await page.route('**/*',route=>new URL(route.request().url()).origin===base?route.continue():route.abort());
  await page.goto(`${base}/outlook/?demo=1&instance=readme`);
  await page.locator('[data-event-id]').first().waitFor();
  await page.getByRole('button',{name:'Calendars',exact:true}).click();
  await page.getByRole('checkbox',{name:'Select all calendars'}).check();
  await page.keyboard.press('Escape');
  await page.screenshot({path:path.join(output,'outlook-week.png')});
  await page.locator('[data-event-id]').filter({hasText:'Weekly planning'}).first().click();
  await page.getByRole('heading',{name:'Description',exact:true}).waitFor();
  await page.screenshot({path:path.join(output,'outlook-details.png')});
  await page.keyboard.press('Escape');
  await page.getByRole('button',{name:'Calendar settings',exact:true}).click();
  await page.getByRole('radio',{name:'Dark',exact:true}).check();
  assert.equal(await page.locator('html').getAttribute('data-theme'),'dark');
  await page.keyboard.press('Escape');
  await page.screenshot({path:path.join(output,'outlook-dark.png')});
  await page.reload();
  await page.locator('[data-event-id]').first().waitFor();
  assert.equal(await page.locator('html').getAttribute('data-theme'),'dark');
  await page.getByRole('button',{name:'Calendar settings',exact:true}).click();
  await page.getByRole('radio',{name:'System',exact:true}).check();
  await page.emulateMedia({colorScheme:'light'});
  assert.equal(await page.locator('html').getAttribute('data-theme'),'light');
  await page.emulateMedia({colorScheme:'dark'});
  await page.waitForFunction(()=>document.documentElement.dataset.theme==='dark');
  await page.getByRole('radio',{name:'Light',exact:true}).check();
  assert.equal(await page.locator('html').getAttribute('data-theme'),'light');
  await page.keyboard.press('Escape');
  const titles=['Plan release scope','Review calendar experience','Prepare installation guide','Test touch interactions','Publish release notes','Collect feedback'];
  const buckets=['Backlog','In progress','Review','Complete'].map((name,i)=>({bucketId:`b${i}`,name,tasks:titles.slice(i,i+2).map((title,j)=>({taskId:`t${i}${j}`,bucketId:`b${i}`,title,assignments:['demo'],dueDateTime:'2026-09-24T17:00:00Z'}))}));
  await page.unroute('**/*');
  await page.route('**/*',async route=>{
    const url=new URL(route.request().url());
    if(url.origin==='http://localhost:8787') {
      let json={};
      if(url.pathname==='/display')json={planId:'demo',planTitle:'Product launch',syncedAt:'2026-09-18T14:00:00Z',buckets};
      else if(url.pathname==='/auth/me')json={id:'demo'};
      else if(url.pathname.endsWith('/details')){const task=buckets.flatMap(b=>b.tasks).find(t=>url.pathname.includes(t.taskId));json={...task,assignees:['Alex Morgan'],description:'Coordinate the next release with the team. Demo content only.',checklist:['Review requirements','Confirm acceptance criteria','Share with the team'].map((title,i)=>({id:`c${i}`,title,isChecked:i===0}))};}
      return route.fulfill({json});
    }
    if(url.origin!==base)return route.abort();
    const file=path.join(root,'planner-edge-widget/widget',url.pathname==='/'?'board.html':url.pathname);
    return route.fulfill({body:await readFile(file),contentType:file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/html'});
  });
  await page.goto(base);
  await page.getByRole('button',{name:'Product launch',exact:true}).waitFor();
  await page.screenshot({path:path.join(output,'planner-board.png')});
  await page.locator('.task-body').first().click();
  await page.getByRole('heading',{name:'Notes',exact:true}).waitFor();
  await page.screenshot({path:path.join(output,'planner-details.png')});
  console.log('Demo screenshots and theme persistence/system checks passed.');
} finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
