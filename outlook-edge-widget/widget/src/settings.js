export const defaultSettings = () => ({ theme:'light', view:'timeGridWeek', firstDay:0, weekends:true, agendaDays:7,
  selectionMode:'primary', calendarKeys:[], timeZone:'local', hoursMode:'outlook', hoursStart:'08:00', hoursEnd:'18:00', credential:'' });

export function normalizeSettings(value) {
  const s = defaultSettings(), v = value && typeof value === 'object' ? value : {};
  if(['light','dark','system'].includes(v.theme))s.theme=v.theme;
  if (['timeGridWeek','agenda','dayGridMonth'].includes(v.view)) s.view=v.view;
  if (Number.isInteger(v.firstDay) && v.firstDay>=0 && v.firstDay<=6) s.firstDay=v.firstDay;
  if (typeof v.weekends==='boolean') s.weekends=v.weekends;
  if ([7,14,30].includes(v.agendaDays)) s.agendaDays=v.agendaDays;
  if (['primary','all','selected'].includes(v.selectionMode)) s.selectionMode=v.selectionMode;
  if (Array.isArray(v.calendarKeys)) s.calendarKeys=[...new Set(v.calendarKeys.filter(k=>typeof k==='string'))];
  if (typeof v.timeZone==='string') {
    try { if(v.timeZone!=='local') new Intl.DateTimeFormat('en',{timeZone:v.timeZone}); s.timeZone=v.timeZone; } catch {}
  }
  if(v.hoursMode==='manual') s.hoursMode='manual';
  for(const key of ['hoursStart','hoursEnd']) if(/^([01]\d|2[0-3]):[0-5]\d$/.test(v[key])) s[key]=v[key];
  if(typeof v.credential==='string') s.credential=v.credential;
  return s;
}
export function settingsStore(storage,id) {
  const read=()=>{try {const v=JSON.parse(storage.getItem(id)||'{}');return v && typeof v==='object' && !Array.isArray(v)?v:{};}catch{return {};}};
  return {load:()=>normalizeSettings(read().outlook), save(value){storage.setItem(id,JSON.stringify({...read(),outlook:normalizeSettings(value)}));}};
}
export function instanceIdentity({uniqueId,search=''}) {
  if(typeof uniqueId==='string' && uniqueId) return {id:uniqueId,native:true,demo:false};
  const p=new URLSearchParams(search), demo=p.get('demo')==='1', instance=p.get('instance') || (demo?'demo':null);
  if(!instance) throw new Error('An explicit preview instance is required.');
  return {id:`outlook-preview:${demo?'demo:':''}${instance}`.replace('demo:demo','demo'),native:false,demo};
}
export async function waitForIdentity({protocol,search,readId,wait=ms=>new Promise(resolve=>setTimeout(resolve,ms)),attempts=150}) {
  if(protocol!=='file:')return instanceIdentity({uniqueId:readId(),search});
  for(let i=0;i<attempts;i++) {
    const id=readId();if(typeof id==='string' && id)return instanceIdentity({uniqueId:id,search});
    await wait(100);
  }
  throw new Error('Waiting for iCUE instance identity. Reload this widget to retry.');
}
export function selectedKeys(s,catalog) {
  if(s.selectionMode==='all') return catalog.map(c=>c.key);
  if(s.selectionMode==='primary') return [catalog.find(c=>c.kind==='personal')?.key].filter(Boolean);
  return s.calendarKeys.filter(key=>catalog.some(c=>c.key===key));
}
