import assert from 'node:assert/strict';
import { FixtureApi } from '../widget/src/fixtures.js';

export async function runContractChecks(browser,base) {
  const context=await browser.newContext(), page=await context.newPage(), fixture=new FixtureApi(), calls=[];
  page.on('pageerror',error=>console.error('Contract preview:',error.message));
  let sessions=0, firstView=true,releaseLive;
  const liveGate=new Promise(resolve=>releaseLive=resolve);
  await page.route('**/api/outlook/**',async route=>{
    const req=route.request(),path=new URL(req.url()).pathname.split('/api/outlook/')[1];
    calls.push({path,headers:req.headers()});
    if(path==='session'){await route.fulfill({json:{token:`session-${++sessions}`}});return;}
    assert.ok(req.headers()['x-outlook-session']);assert.ok(req.url().startsWith(base));
    if(path==='preferences'){await route.fulfill({status:503,json:{error:{code:'offline',message:'Preferences unavailable'}}});return;}
    if(path==='view/cached') {
      const result=await fixture.post('view',req.postDataJSON());
      result.events=result.events.map((event,index)=>({...event,title:index?'Cached calendar event':'Cached resume event'}));
      result.sources=result.sources.map(source=>({...source,stale:true}));
      await route.fulfill({json:result});return;
    }
    if(path==='view' && firstView){firstView=false;await route.fulfill({status:401,json:{error:{code:'unauthorized'}}});return;}
    if(path==='view') await liveGate;
    const result=req.method()==='GET'?await fixture.get(path):await fixture.post(path,req.postDataJSON());
    await route.fulfill({json:result});
  });
  await page.goto(`${base}/outlook/?instance=contract-preview`);
  await page.getByText('Cached resume event',{exact:true}).waitFor({timeout:5000});
  releaseLive();
  try{await page.locator('[data-event-id]').first().waitFor({timeout:5000});}
  catch(error){console.error('Contract routes',calls.map(c=>c.path));console.error('Contract body',await page.locator('body').innerText());throw error;}
  assert.equal(sessions,2,'view 401 renews preview session exactly once');
  assert.equal(calls.filter(c=>c.path==='view').length,2);
  assert.equal(calls.filter(c=>c.path==='view/cached').length,1);
  await page.getByText('Cached resume event',{exact:true}).waitFor({state:'detached'});
  assert.equal(await page.getByText('Pair again',{exact:true}).count(),0);
  await context.close();

  // A helper snapshot may finish after the network request fails. Keep the
  // snapshot visible, but do not imply that a live update is still running.
  const offlineContext=await browser.newContext(),offline=await offlineContext.newPage();
  const offlineFixture=new FixtureApi();
  await offline.route('**/api/outlook/**',async route=>{
    const req=route.request(),path=new URL(req.url()).pathname.split('/api/outlook/')[1];
    if(path==='session'){await route.fulfill({json:{token:'offline-session'}});return;}
    if(path==='preferences'){await route.fulfill({status:503,json:{error:{code:'offline',message:'Preferences unavailable'}}});return;}
    if(path==='view'){await route.fulfill({status:503,json:{error:{code:'offline',message:'Network unavailable'}}});return;}
    if(path==='view/cached') {
      await new Promise(resolve=>setTimeout(resolve,100));
      const result=await offlineFixture.post('view',req.postDataJSON());
      result.events=result.events.map((event,index)=>({...event,title:index?'Cached calendar event':'Late cached event'}));
      result.sources=result.sources.map(source=>({...source,stale:true}));
      await route.fulfill({json:result});return;
    }
    const result=req.method()==='GET'?await offlineFixture.get(path):await offlineFixture.post(path,req.postDataJSON());
    await route.fulfill({json:result});
  });
  await offline.goto(`${base}/outlook/?instance=offline-cache-preview`);
  await offline.getByText('Late cached event',{exact:true}).waitFor({timeout:5000});
  assert.equal(await offline.locator('footer').getByText('Unavailable',{exact:true}).count(),1);
  assert.match(await offline.locator('[role="status"]').textContent(),/Offline \| Last updated/);
  await offlineContext.close();

  // Native boot uses actual file transport, delayed host identity, revoked storage, and fake HTTP only.
  const nativeContext=await browser.newContext(),native=await nativeContext.newPage();
  const nativeFixture=new FixtureApi();let requestSecret,paired=false,polls=0,sourceAuthFailure=false;
  await native.addInitScript(()=>{
    localStorage.setItem('native-test',JSON.stringify({hostProperty:27,outlook:{credential:'revoked'}}));
    setTimeout(()=>{window.uniqueId='native-test';window.icueEvents?.onICUEInitialized?.();},200);
  });
  await native.route('**/api/outlook/**',async route=>{
    const req=route.request(),path=new URL(req.url()).pathname.split('/api/outlook/')[1];
    assert.ok(req.url().startsWith('http://localhost:8787/api/outlook/'));
    if(path==='pairings') {
      const body=req.postDataJSON();assert.equal(body.instanceId,'native-test');assert.match(body.requestSecret,/^[a-f0-9]{64}$/);requestSecret=body.requestSecret;
      await route.fulfill({json:{id:'request-id',code:'ABCD12',expiresAt:new Date(Date.now()+300000).toISOString()}});return;
    }
    if(path==='pairings/request-id/poll') {
      assert.equal(req.postDataJSON().requestSecret,requestSecret);polls++;paired=true;
      await route.fulfill({json:{status:'approved',credential:'approved-credential'}});return;
    }
    if(!paired){await route.fulfill({status:401,json:{error:{code:'unauthorized',message:'Revoked pairing'}}});return;}
    assert.equal(req.headers().authorization,'Bearer approved-credential');
    const result=req.method()==='GET'?await nativeFixture.get(path):await nativeFixture.post(path,req.postDataJSON());
    if(path==='view' && sourceAuthFailure)result.sources=[{calendarKey:'work',fetchedAt:null,stale:false,error:{code:'sign_in_required',message:'Account changed'}}];
    await route.fulfill({json:result});
  });
  await native.goto(new URL('../dist/index.html',import.meta.url).href);
  await native.getByRole('button',{name:'Pair again',exact:true}).waitFor();
  assert.equal(await native.evaluate(()=>JSON.parse(localStorage.getItem('native-test')).outlook.credential),'');
  await native.getByRole('button',{name:'Pair again',exact:true}).click();
  await native.getByText('ABCD12',{exact:true}).waitFor();
  await native.locator('[data-event-id]').first().waitFor();
  assert.equal(polls,1);
  const saved=await native.evaluate(()=>JSON.parse(localStorage.getItem('native-test')));
  assert.equal(saved.hostProperty,27);assert.equal(saved.outlook.credential,'approved-credential');assert.equal(saved.outlook.events,undefined);
  await native.locator('[data-event-id]').first().click();
  await native.getByRole('heading',{name:'Description',exact:true}).waitFor();
  sourceAuthFailure=true;
  await native.evaluate(()=>document.querySelector('button[aria-label="Refresh"]').click());
  await native.getByRole('button',{name:'Pair again',exact:true,includeHidden:true}).waitFor({state:'attached'});
  assert.equal(await native.locator('[data-event-id]').count(),0);
  assert.equal(await native.locator('#dialog-title').textContent(),'Event details');
  assert.equal(await native.getByRole('button',{name:'Join meeting',exact:true}).count(),0);
  assert.equal(await native.evaluate(()=>JSON.parse(localStorage.getItem('native-test')).outlook.credential),'');
  await native.keyboard.press('Escape');
  assert.equal(await native.getByRole('button',{name:'Refresh',exact:true}).isDisabled(),false);
  sourceAuthFailure=false;paired=false;
  await native.getByRole('button',{name:'Pair again',exact:true}).click();
  await native.locator('[data-event-id]').first().waitFor();
  assert.equal(polls,2,'embedded auth failure can recover through re-pairing');
  await nativeContext.close();
}
