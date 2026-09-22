import test from 'node:test';
import assert from 'node:assert/strict';
import { defaultSettings, normalizeSettings, settingsStore, selectedKeys, instanceIdentity, waitForIdentity } from '../widget/src/settings.js';
import { rangeFor, moveDate, maskEvent, RefreshState, workingPeriods, workingIntervals, initialScrollTime } from '../widget/src/state.js';
import { OutlookApi, loadMetadata } from '../widget/src/api.js';

test('theme defaults to light and validates saved preferences', () => {
  assert.equal(defaultSettings().theme,'light');
  for(const theme of ['light','dark','system'])assert.equal(normalizeSettings({theme}).theme,theme);
  assert.equal(normalizeSettings({theme:'invalid'}).theme,'light');
});

test('Week, Sunday, seven days, weekends and PC zone are defaults', () => {
  const s = defaultSettings();
  assert.equal(s.view, 'timeGridWeek'); assert.equal(s.firstDay, 0);
  assert.equal(s.agendaDays, 7); assert.equal(s.weekends, true); assert.equal(s.timeZone, 'local');
});
test('settings isolate uniqueId and preserve host properties without event data', () => {
  const data = new Map([['one', JSON.stringify({ host: 12 })]]);
  const storage = { getItem: k => data.get(k), setItem: (k,v) => data.set(k,v) };
  const a = settingsStore(storage, 'one'), b = settingsStore(storage, 'two');
  a.save({ ...a.load(), firstDay: 5, credential: 'paired', events: ['secret'] });
  assert.equal(a.load().firstDay, 5); assert.equal(b.load().firstDay, 0);
  assert.equal(JSON.parse(data.get('one')).host, 12);
  assert.equal(data.get('one').includes('secret'), false);
});
test('malformed settings and identities cannot become shared native storage', () => {
  assert.deepEqual(normalizeSettings({ view:'bad', firstDay:9, agendaDays:8, weekends:'no', hoursStart:'29:00' }), defaultSettings());
  assert.equal(instanceIdentity({ uniqueId:'native', search:'?instance=other' }).id, 'native');
  assert.equal(instanceIdentity({ search:'?demo=1' }).id, 'outlook-preview:demo');
  assert.throws(() => instanceIdentity({ search:'' }), /instance/i);
});
test('all mode discovers new calendars while explicit empty selection stays empty', () => {
  const catalog = [{key:'a',kind:'personal'},{key:'b',kind:'shared'}];
  assert.deepEqual(selectedKeys(defaultSettings(),catalog), ['a']);
  assert.deepEqual(selectedKeys({...defaultSettings(), selectionMode:'all'},catalog), ['a','b']);
  assert.deepEqual(selectedKeys({...defaultSettings(), selectionMode:'selected', calendarKeys:[]},catalog), []);
});
test('ranges include hidden weekends, all intersecting month weeks, and DST offsets', () => {
  const s = {...defaultSettings(), timeZone:'America/New_York', weekends:false};
  const r = rangeFor('2026-03-10',s);
  assert.equal(r.start,'2026-03-08T00:00:00-05:00');
  assert.equal(r.end,'2026-03-15T00:00:00-04:00');
  const month = rangeFor('2026-08-15',{...s,view:'dayGridMonth'});
  assert.equal(month.start.slice(0,10),'2026-07-26'); assert.equal(month.end.slice(0,10),'2026-09-06');
  assert.equal(moveDate('2026-01-31',1,{...s,view:'dayGridMonth'}),'2026-02-01');
  assert.equal(rangeFor('2026-09-18',{...s,view:'agenda',agendaDays:30}).end.slice(0,10),'2026-10-18');
});
test('private masking discards unexpected sensitive fields', () => {
  const event = maskEvent({reference:'r',calendarKey:'c',title:'Secret',isPrivate:true,start:'s',end:'e',description:'hidden',joinUrl:'https://secret'});
  assert.equal(event.title,'Private event'); assert.equal(JSON.stringify(event).includes('hidden'),false);
  assert.equal(event.joinUrl,undefined);
});
test('superseded responses cannot replace the current range; offline cache expires and auth purges', () => {
  const state = new RefreshState(); const old = state.begin('old'); const current = state.begin('new');
  assert.equal(state.accept(old,{events:[],sources:[]},100),false);
  state.accept(current,{events:[{reference:'r',calendarKey:'a',title:'Public'}],sources:[]},100);
  assert.equal(state.fail(current,{status:0},200).length,1);
  assert.equal(state.fail(current,{status:0},86400101).length,0);
  state.accept(current,{events:[{reference:'r',calendarKey:'a'}],sources:[]},200);
  assert.equal(state.fail(current,{status:401},201).length,0);
});
test('helper cache seeds an empty reload before live data replaces it', () => {
  const state=new RefreshState(), ticket=state.begin('range');
  assert.equal(state.seed(ticket,{events:[{reference:'cached',calendarKey:'a',title:'Cached'}],sources:[{calendarKey:'a',fetchedAt:new Date(100).toISOString(),stale:true,error:null}]},200),true);
  assert.equal(state.events[0].title,'Cached');
  assert.equal(state.offline,false);
  assert.equal(state.accept(ticket,{events:[{reference:'live',calendarKey:'a',title:'Live'}],sources:[{calendarKey:'a',fetchedAt:new Date(300).toISOString(),stale:false,error:null}]},300),true);
  assert.equal(state.events[0].title,'Live');
  assert.equal(state.seed(ticket,{events:[{reference:'late',calendarKey:'a',title:'Late cache'}],sources:[]},400),false);
  assert.equal(state.events[0].title,'Live');
});
test('overnight working hours wrap and preserve days', () => {
  assert.deepEqual(workingPeriods({...defaultSettings(),hoursMode:'manual',hoursStart:'22:00',hoursEnd:'06:00'},null), [
    {daysOfWeek:[1,2,3,4,5],startTime:'22:00',endTime:'24:00'},
    {daysOfWeek:[2,3,4,5,6],startTime:'00:00',endTime:'06:00'}
  ]);
});
test('preview session token is used on every API call at relative origin', async () => {
  const requests=[];
  const api = new OutlookApi({native:false, fetch: async (url,init) => {
    requests.push({url,init}); return new Response(JSON.stringify(url.endsWith('/session')?{token:'csrf'}:[]));
  }});
  await api.initialize(); await api.get('calendars'); await api.post('view',{calendarKeys:[],start:'a',end:'b'});
  assert.equal(requests[0].url,'/api/outlook/session');
  assert.equal(requests[1].init.headers['X-Outlook-Session'],'csrf');
  assert.equal(requests[2].init.headers['X-Outlook-Session'],'csrf');
});
test('native bearer and pairing secret follow the wire contract', async () => {
  const requests=[];
  const api = new OutlookApi({native:true,credential:'paired',fetch:async(url,init)=>{
    requests.push({url,init}); return new Response('{}');
  }});
  await api.get('status'); await api.pair('instance','unguessable'); await api.poll('id','unguessable');
  assert.equal(requests[0].url,'http://localhost:8787/api/outlook/status');
  assert.equal(requests[0].init.headers.Authorization,'Bearer paired');
  assert.deepEqual(JSON.parse(requests[1].init.body),{instanceId:'instance',requestSecret:'unguessable'});
  assert.deepEqual(JSON.parse(requests[2].init.body),{requestSecret:'unguessable'});
});
test('backend snapshot age is not reset by receiving stale data', () => {
  const state=new RefreshState(), ticket=state.begin('range'), now=Date.now();
  state.accept(ticket,{events:[{reference:'r',calendarKey:'a'}],sources:[{calendarKey:'a',fetchedAt:new Date(now-23*3600000).toISOString(),stale:true}]},now);
  assert.equal(state.fail(ticket,{status:0},now+2*3600000).length,0);
});
test('embedded account auth errors purge every cached range', () => {
  for(const code of ['sign_in_required','consent_required','account_changed']) {
    const state=new RefreshState();state.accept(state.begin('old'),{events:[{reference:'r',calendarKey:'a'}],sources:[]});
    const ticket=state.begin('new');
    assert.throws(()=>state.accept(ticket,{events:[{reference:'s',calendarKey:'b'}],sources:[{calendarKey:'a',error:{code}}]}),e=>e.status===401);
    assert.equal(state.cache.size,0);assert.equal(state.events.length,0);
  }
});
test('working hours convert source-zone dates across DST and midnight', () => {
  const s={...defaultSettings(),timeZone:'Asia/Kolkata'};
  const h={daysOfWeek:['monday'],startTime:'22:00:00',endTime:'06:00:00',timeZone:'America/New_York'};
  const r=rangeFor('2026-03-10',s), intervals=workingIntervals(s,h,r);
  const monday=intervals.find(i=>i.id==='hours:2026-03-09');
  assert.equal(monday.start,'2026-03-10T07:30:00+05:30');assert.equal(monday.end,'2026-03-10T15:30:00+05:30');
  assert.equal(initialScrollTime(s,h,'2026-03-09'),'07:30:00');
});
test('preview renews session once for safe reads but never replays Join', async () => {
  for(const path of ['calendars','view','event-details','join']) {
    let sessions=0,calls=0,purges=0;
    const api=new OutlookApi({fetch:async url=>{
      if(url.endsWith('/session'))return new Response(JSON.stringify({token:`token-${++sessions}`}));
      calls++;return calls===1?new Response('{}',{status:401}):new Response('{}');
    }});api.onUnauthorized=()=>purges++;
    await api.initialize();
    if(path==='join')await assert.rejects(api.post(path,{reference:'r',calendarKey:'c'}));
    else if(path==='calendars')await api.get(path);else await api.post(path,{});
    assert.equal(purges,1);assert.equal(calls,path==='join'?1:2);assert.equal(sessions,path==='join'?1:2);
  }
});
test('native startup waits for delayed uniqueId without assigning preview storage', async () => {
  let attempts=0;
  const identity=await waitForIdentity({protocol:'file:',search:'',readId:()=>++attempts>2?'late-native':undefined,wait:async()=>{}});
  assert.equal(identity.id,'late-native');assert.equal(identity.native,true);
});
test('preferences failure permits calendar reads with fallback, but auth failure propagates', async () => {
  const catalog=[{key:'a'}];
  const api={get:async path=>{if(path==='calendars')return catalog;throw new Error('Unavailable');}};
  const result=await loadMetadata(api);assert.deepEqual(result.catalog,catalog);assert.equal(result.preferences.workingHours,null);
  api.get=async path=>{if(path==='calendars')return catalog;throw Object.assign(new Error('Mailbox settings unavailable'),{status:403});};
  assert.equal((await loadMetadata(api)).preferences.workingHours,null);
  api.get=async path=>{if(path==='calendars')return catalog;throw Object.assign(new Error('Expired'),{status:401});};
  await assert.rejects(loadMetadata(api),e=>e.status===401);
});
