import 'fullcalendar/skeleton.css';
import 'fullcalendar/themes/classic/theme.css';
import 'fullcalendar/themes/classic/palette.css';
import '../styles.css';
import { OutlookApi, loadMetadata } from './api.js';
import { FixtureApi } from './fixtures.js';
import { settingsStore, waitForIdentity, selectedKeys, normalizeSettings } from './settings.js';
import { RefreshState, rangeFor, moveDate, zoneFor, today, eventId, ageLimit } from './state.js';
import { CalendarAdapter } from './calendar.js';
import { Dialogs } from './dialogs.js';
import { renderPicker } from './picker.js';
import { el, button, icon, calendarLabel } from './dom.js';

export async function start(root,environment={}) {
  let identity;
  try {identity=await waitForIdentity({protocol:location.protocol,search:location.search,readId:()=>environment.uniqueId??(typeof uniqueId==='undefined'?undefined:uniqueId)});}
  catch(error){root.replaceChildren(el('section',{class:'startup'},el('h1',{},'Outlook'),el('p',{},error.message)));return;}
  const store=settingsStore(localStorage,identity.id);let settings=store.load();
  const themeMedia=matchMedia('(prefers-color-scheme: dark)');
  const applyTheme=()=>{document.documentElement.dataset.theme=settings.theme==='system'?(themeMedia.matches?'dark':'light'):settings.theme;};
  themeMedia.addEventListener('change',applyTheme);applyTheme();
  window.addEventListener('pagehide',()=>themeMedia.removeEventListener('change',applyTheme),{once:true});
  const api=identity.demo?new FixtureApi():new OutlookApi({native:identity.native,credential:settings.credential});
  let catalog=[], preferences=null, anchor=today(zoneFor(settings)), adapter, controller, metadataAt=0, ready=false, lastZone=zoneFor(settings), recovering=false;
  const state=new RefreshState();
  const shell=el('div',{class:'shell'}), toolbar=el('header',{class:'toolbar'}), subbar=el('div',{class:'subbar'});
  const picker=el('div',{class:'picker',hidden:'',id:'calendar-picker','aria-label':'Select calendars'});
  const pickerButton=button('Calendars',()=>{picker.hidden=!picker.hidden;pickerButton.setAttribute('aria-expanded',String(!picker.hidden));if(!picker.hidden)picker.querySelector('input')?.focus();});
  pickerButton.setAttribute('aria-controls','calendar-picker');pickerButton.setAttribute('aria-expanded','false');
  const pickerWrap=el('div',{class:'picker-wrap'},pickerButton,picker);
  const views=el('div',{class:'view-switch',role:'group','aria-label':'Calendar view'});
  for(const [view,label]of [['agenda','Agenda'],['timeGridWeek','Week'],['dayGridMonth','Month']]) {
    const b=button(label,()=>change({view}));b.dataset.view=view;views.append(b);
  }
  const refreshButton=button('Refresh',()=>refresh(true),'RefreshCw');
  toolbar.append(el('div',{class:'brand'},icon('CalendarDays'),'Outlook'),pickerWrap,el('div',{class:'spacer'}),views,button('Calendar settings',openSettings,'Settings2'),refreshButton);
  const rangeTitle=el('h1',{class:'range-title'}),legend=el('div',{class:'legend'});
  const agendaSpan=el('div',{class:'view-switch agenda-span',role:'group','aria-label':'Agenda days'});
  for(const n of [7,14,30]){const b=button(`${n} days`,()=>change({agendaDays:n}));b.dataset.days=n;agendaSpan.append(b);}
  const previous=button('Previous week',()=>navigate(-1),'ChevronLeft'),next=button('Next week',()=>navigate(1),'ChevronRight');
  subbar.append(el('div',{class:'navigation'},button('Today',()=>{anchor=today(zoneFor(settings));reconfigure();refresh();}),previous,next),rangeTitle,agendaSpan,el('div',{class:'spacer'}),legend);
  const status=el('div',{class:'status',role:'status',hidden:''});
  const board=el('section',{class:'board','aria-label':'Calendar'}),calendarRoot=el('div',{id:'calendar'}),empty=el('div',{class:'empty',hidden:''});board.append(calendarRoot,empty);
  const sync=el('span',{},'Connecting...'),zone=el('span'),weekends=el('input',{type:'checkbox','aria-label':'Show weekends'});weekends.onchange=()=>change({weekends:weekends.checked});
  const footer=el('footer',{},sync,zone,el('label',{},weekends,'Show weekends'));
  shell.append(toolbar,subbar,status,board,footer);root.replaceChildren(shell);
  const dialogs=new Dialogs(root,pickerButton);
  api.onUnauthorized=()=>{state.clear();dialogs.unavailable();showEvents();if(identity.native){settings.credential='';api.credential='';save();ready=false;notice('Pairing expired or revoked.');status.append(' ',button('Pair again',pair));}};
  const pickerClose=()=>{picker.hidden=true;pickerButton.setAttribute('aria-expanded','false');};
  document.addEventListener('pointerdown',e=>{if(!pickerWrap.contains(e.target))pickerClose();});
  pickerWrap.addEventListener('keydown',e=>{if(e.key==='Escape'){pickerClose();pickerButton.focus();}});
  function save(){try{store.save(settings);}catch{notice('Settings could not be saved on this device.');}}
  function notice(text){status.textContent=text;status.hidden=!text;}
  function controls() {
    const keys=selectedKeys(settings,catalog);
    pickerButton.replaceChildren('Calendars ',el('span',{},String(keys.length)),icon('ChevronDown'));
    renderPicker(picker,catalog,settings,change);
    legend.replaceChildren(...catalog.filter(c=>keys.includes(c.key)).map(calendarLabel));
    for(const b of views.children)b.setAttribute('aria-pressed',String(b.dataset.view===settings.view));
    weekends.checked=settings.weekends;agendaSpan.hidden=settings.view!=='agenda';for(const b of agendaSpan.children)b.setAttribute('aria-pressed',String(Number(b.dataset.days)===settings.agendaDays));
    zone.textContent=`${zoneFor(settings)}${settings.timeZone==='local'?' | PC':''}`;
    const unit=settings.view==='dayGridMonth'?'month':settings.view==='agenda'?`${settings.agendaDays} days`:'week';
    for(const [b,label] of [[previous,`Previous ${unit}`],[next,`Next ${unit}`]]){b.title=label;b.setAttribute('aria-label',label);}
  }
  function change(patch){settings=normalizeSettings({...settings,...patch});save();applyTheme();if(Object.keys(patch).every(key=>key==='theme'))return;controls();reconfigure();refresh();}
  function navigate(n){anchor=moveDate(anchor,n,settings);reconfigure();refresh();}
  function reconfigure(){adapter?.configure(settings,anchor,preferences);controls();}
  function showEvents(){adapter?.update(state.events,catalog);const noSelection=!selectedKeys(settings,catalog).length;empty.hidden=!noSelection && (!state.offline || state.events.length>0);empty.textContent=noSelection?'No calendars selected':'Calendar unavailable for this range';}
  function openEvent(event,origin){pickerClose();dialogs.details(event,catalog.find(c=>c.key===event.calendarKey),api,zoneFor(settings),()=>ready && !state.offline,origin);}
  function makeCalendar(){adapter=new CalendarAdapter(calendarRoot,{settings,anchor,preferences,onTitle:title=>rangeTitle.textContent=title,onEvent:openEvent,onMore:(events,origin)=>{
    const unique=[...new Map(events.map(e=>[eventId(e),e])).values()];dialogs.list('Events',unique,catalog,openEvent,origin);
  }});}
  async function metadata(signal) {
    const {catalog:newCatalog,preferences:newPreferences}=await loadMetadata(api,signal);
    if(signal?.aborted)return;
    const removed=catalog.filter(c=>!newCatalog.some(n=>n.key===c.key));for(const c of removed)state.removeSource(c.key);
    if(removed.length)dialogs.unavailable();
    const catalogChanged=JSON.stringify(catalog)!==JSON.stringify(newCatalog);
    catalog=newCatalog;preferences=newPreferences;metadataAt=Date.now();if(catalogChanged)controls();
    adapter?.configure(settings,anchor,preferences);
  }
  async function refresh(forceMetadata=false) {
    if(!ready)return;
    controller?.abort();controller=new AbortController();const signal=controller.signal;
    const range=rangeFor(anchor,settings), keys=selectedKeys(settings,catalog);
    const ticket=state.begin(JSON.stringify([range.start,range.end,[...keys].sort()]));showEvents();
    if(!keys.length){state.accept(ticket,{events:[],sources:[]});showEvents();notice('');sync.textContent=identity.demo?'Demo':'Ready';return;}
    refreshButton.disabled=true;
    try {
      // Revalidate helper authorization before replacing a retained offline snapshot.
      if(state.offline || recovering) {
        const status=await api.get('status',signal);
        if(!status.ready){const e=new Error('Reconnect Outlook in helper setup.');e.status=401;throw e;}
        recovering=false;
      }
      if(forceMetadata || Date.now()-metadataAt>=900000) {
        await metadata(signal);if(signal.aborted)return;
        const changed=JSON.stringify(keys)!==JSON.stringify(selectedKeys(settings,catalog));
        if(changed){refresh();return;}
      }
      const result=await api.post('view',{calendarKeys:keys,start:range.start,end:range.end},signal);
      if(!state.accept(ticket,result))return;
      const issues=state.sources.filter(s=>s.stale || s.error);
      notice(issues.map(s=>`${catalog.find(c=>c.key===s.calendarKey)?.name||'Calendar'}: ${s.error?.message||'Stale'}${s.fetchedAt?` | Last updated ${new Date(s.fetchedAt).toLocaleString()}`:''}`).join(' | '));
      const times=state.sources.map(s=>Date.parse(s.fetchedAt)).filter(Number.isFinite);
      sync.textContent=`${identity.demo?'Demo | ':''}${times.length?`Updated ${new Date(Math.min(...times)).toLocaleTimeString()}`:'Ready'}`;
      if(issues.some(s=>/access|denied|not_found|forbidden|unauthorized/i.test(s.error?.code||'')))dialogs.unavailable();
      showEvents();
    }catch(error){
      if(signal.aborted || !state.current(ticket))return;
      state.fail(ticket,error);dialogs.unavailable();recovering=true;
      const cached=state.cache.get(state.key);
      notice([401,403].includes(error.status)?'Access changed. Reconnect Outlook in helper setup.':`Offline | ${cached && state.events.length?`Last updated ${new Date(cached.at).toLocaleString()}`:'No saved events for this range'}`);
      if(error.status===401 && identity.native){settings.credential='';api.credential='';save();ready=false;const reconnect=button('Pair again',pair);status.append(' ',reconnect);}
      if(error.status===401 && !identity.native){ready=false;status.append(' ',button('Reconnect',initialize));}
      sync.textContent='Unavailable';showEvents();
    }finally{if(state.current(ticket))refreshButton.disabled=false;}
  }
  function openSettings() {
    pickerClose();dialogs.open('Calendar settings');
    const choices=(legend,name,values,current,onChange)=>{
      const field=el('fieldset',{class:'choices'},el('legend',{},legend));
      for(const [value,label]of values){const input=el('input',{type:'radio',name,value,'aria-label':label});input.checked=value===current;input.onchange=()=>{if(input.checked)onChange(value);};field.append(el('label',{},input,label));}
      return field;
    };
    const first=choices('Week starts on','week-start',['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'].map((name,i)=>[i,name]),settings.firstDay,firstDay=>change({firstDay}));
    dialogs.panel.append(choices('Appearance','theme',[['light','Light'],['dark','Dark'],['system','System']],settings.theme,theme=>change({theme})));
    const start=el('input',{type:'text',inputmode:'numeric',maxlength:5,'aria-label':'Workday starts',value:settings.hoursStart}),end=el('input',{type:'text',inputmode:'numeric',maxlength:5,'aria-label':'Workday ends',value:settings.hoursEnd});
    const hoursToggle=()=>{start.disabled=end.disabled=settings.hoursMode!=='manual';};hoursToggle();
    const hours=choices('Working hours','hours-mode',[['outlook','Use Outlook hours'],['manual','Custom hours']],settings.hoursMode,hoursMode=>{change({hoursMode});hoursToggle();});
    const validation=el('p',{role:'status',class:'muted'});
    const setTime=(input,key)=>{if(/^([01]\d|2[0-3]):[0-5]\d$/.test(input.value)){change({[key]:input.value});validation.textContent='';input.removeAttribute('aria-invalid');}else{input.setAttribute('aria-invalid','true');validation.textContent='Enter a time from 00:00 to 23:59.';}};
    start.onchange=()=>setTime(start,'hoursStart');end.onchange=()=>setTime(end,'hoursEnd');
    const zoneInput=el('input',{type:'search','aria-label':'Search time zones',autocomplete:'off'});
    const zones=el('fieldset',{class:'zone-options'},el('legend',{},'Time zone'));
    const supported=Intl.supportedValuesOf?.('timeZone')||[];
    const names=[...new Set([settings.timeZone,...(preferences?.timeZones||[]),...supported,'UTC'])].filter(name=>{try{new Intl.DateTimeFormat('en',{timeZone:name});return true;}catch{return false;}});
    function zoneOptions(){
      zones.replaceChildren(el('legend',{},'Time zone'));
      const filtered=[['local',`PC (${Intl.DateTimeFormat().resolvedOptions().timeZone})`],...names.map(n=>[n,n])].filter(([,label])=>label.toLowerCase().includes(zoneInput.value.toLowerCase()));
      for(const [value,label]of filtered){const input=el('input',{type:'radio',name:'time-zone',value,'aria-label':label});input.checked=settings.timeZone===value;input.onchange=()=>change({timeZone:value});zones.append(el('label',{},input,label));}
      if(!filtered.length)zones.append(el('p',{class:'muted'},'No matching time zones'));
    }
    zoneInput.oninput=zoneOptions;zoneOptions();
    dialogs.panel.append(first,hours,el('div',{class:'hours'},el('label',{},'Start',start),el('label',{},'End',end)),validation,el('label',{class:'setting'},'Search time zones',zoneInput),zones);
  }
  let pairTimer;
  dialogs.onClose=()=>clearTimeout(pairTimer);
  async function pair() {
    clearTimeout(pairTimer);const generation=dialogs.open('Pair Outlook');const text=el('p',{role:'status'},'Requesting pairing code...');dialogs.panel.append(text);
    const secret=Array.from(crypto.getRandomValues(new Uint8Array(32)),b=>b.toString(16).padStart(2,'0')).join('');
    try {
      const request=await api.pair(identity.id,secret);if(generation!==dialogs.generation)return;text.textContent='Approve this code in Microsoft Widgets Helper setup.';dialogs.panel.append(el('strong',{class:'pair-code'},request.code));
      const poll=async()=>{
        if(generation!==dialogs.generation)return;
        if(Date.now()>=Date.parse(request.expiresAt)){text.textContent='Pairing expired.';dialogs.panel.append(button('New code',pair));return;}
        try{const result=await api.poll(request.id,secret);if(generation!==dialogs.generation)return;if(result.status==='approved' && result.credential){settings.credential=result.credential;api.credential=result.credential;save();dialogs.close();await initialize();return;}pairTimer=setTimeout(poll,2000);}
        catch(error){text.textContent=error.message;dialogs.panel.append(button('Retry pairing',pair));}
      };pairTimer=setTimeout(poll,1500);
    }catch(error){text.textContent=error.message;dialogs.panel.append(button('Retry pairing',pair));}
  }
  async function initialize() {
    try {
      if(identity.native && !settings.credential){notice('Pair this widget with Microsoft Widgets Helper.');status.append(' ',button('Pair widget',pair));sync.textContent='Not paired';return;}
      await api.initialize();const result=await api.get('status');
      if(!result.ready){state.clear();dialogs.unavailable();showEvents();notice(result.error?.message||'Connect Outlook in Microsoft Widgets Helper setup.');sync.textContent='Not connected';status.append(' ',button('Retry',initialize));return;}
      await metadata();ready=true;if(!adapter)makeCalendar();await refresh();
    }catch(error){state.clear();dialogs.unavailable();showEvents();notice(error.message);sync.textContent='Unavailable';
      if(error.status===401 && identity.native){settings.credential='';api.credential='';save();ready=false;status.append(' ',button('Pair again',pair));}
      else status.append(' ',button('Retry',initialize));
    }
  }
  controls();await initialize();
  const tick=setInterval(()=>{
    const current=zoneFor(settings);if(lastZone!==current){lastZone=current;reconfigure();}
    if(!document.hidden)refresh();
    if(state.offline && !state.cached().length && state.events.length){state.events=[];showEvents();}
  },60000);
  document.addEventListener('visibilitychange',()=>{if(!document.hidden){reconfigure();refresh();}});
  window.addEventListener('online',()=>refresh(true));
  window.addEventListener('pagehide',()=>{clearInterval(tick);clearTimeout(pairTimer);controller?.abort();dialogs.close();state.clear();},{once:true});
  // Fixture controls are available only behind the explicit demo query flag.
  if(identity.demo)window.outlookDemo={refresh,api,state,settings:()=>({...settings}),navigate,change,dialogs};
  return {refresh};
}
if(typeof document!=='undefined')start(document.getElementById('app'));
