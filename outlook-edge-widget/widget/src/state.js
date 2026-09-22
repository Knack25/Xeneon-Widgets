import { Temporal } from 'temporal-polyfill';

export const zoneFor = s => s.timeZone==='local' ? Intl.DateTimeFormat().resolvedOptions().timeZone : s.timeZone;
export const today = zone => Temporal.Now.plainDateISO(zone).toString();
export function rangeFor(anchor,s) {
  let start=Temporal.PlainDate.from(anchor), end;
  const align=d=>d.subtract({days:(d.dayOfWeek%7-s.firstDay+7)%7});
  if(s.view==='timeGridWeek') {start=align(start);end=start.add({days:7});}
  else if(s.view==='dayGridMonth') {
    const first=start.with({day:1}), next=first.add({months:1}); start=align(first); end=align(next);
    if(!end.equals(next)) end=end.add({days:7});
  } else end=start.add({days:s.agendaDays});
  const iso=d=>d.toZonedDateTime({timeZone:zoneFor(s),plainTime:'00:00'}).toString({timeZoneName:'never',calendarName:'never'});
  return {start:iso(start),end:iso(end),startDate:start.toString(),endDate:end.toString()};
}
export function moveDate(anchor,n,s) {
  const d=Temporal.PlainDate.from(anchor);
  return (s.view==='dayGridMonth'?d.with({day:1}).add({months:n}):d.add({days:n*(s.view==='agenda'?s.agendaDays:7)})).toString();
}
export function maskEvent(e) {
  return {reference:e.reference,calendarKey:e.calendarKey,title:e.isPrivate?'Private event':String(e.title||'Untitled event'),
    start:e.start,end:e.end,isAllDay:!!e.isAllDay,isPrivate:!!e.isPrivate,isCancelled:!!e.isCancelled};
}
export const eventId = e => JSON.stringify([e.calendarKey,e.reference]);
export const ageLimit=86400000;
export class RefreshState {
  generation=0; resolvedGeneration=0; key=''; cache=new Map(); events=[]; sources=[]; offline=false;
  begin(key) {this.key=key; this.generation++; this.events=this.cached(); return {generation:this.generation,key};}
  current(t){return t.generation===this.generation && t.key===this.key;}
  cached(now=Date.now()) {const item=this.cache.get(this.key);return item?item.events.filter(e=>now-(item.sourceTimes?.get(e.calendarKey)??item.at)<ageLimit):[];}
  seed(t,result,now=Date.now()) {
    if(!this.current(t) || this.resolvedGeneration===t.generation)return false;
    return this.apply(t,result,now,false);
  }
  accept(t,result,now=Date.now()) {return this.apply(t,result,now,true);}
  apply(t,result,now,resolve) {
    if(!this.current(t)) return false;
    this.sources=result.sources||[];
    const auth=this.sources.find(s=>['sign_in_required','consent_required','account_changed','unauthorized'].includes(s.error?.code));
    if(auth){this.clear();const error=new Error(auth.error.message||'Reconnect Outlook in helper setup.');error.status=401;error.code=auth.error.code;throw error;}
    const denied=new Set(this.sources.filter(s=>['source_access_denied','source_not_found','source_removed','forbidden','not_found'].includes(s.error?.code)).map(s=>s.calendarKey));
    const expired=new Set(this.sources.filter(s=>s.stale && (!s.fetchedAt || now-Date.parse(s.fetchedAt)>=ageLimit)).map(s=>s.calendarKey));
    this.events=(result.events||[]).filter(e=>!e.isCancelled && !denied.has(e.calendarKey) && !expired.has(e.calendarKey)).map(maskEvent);
    const sourceTimes=new Map(this.sources.map(s=>[s.calendarKey,Number.isFinite(Date.parse(s.fetchedAt))?Date.parse(s.fetchedAt):(s.stale?0:now)]));
    if(resolve){this.resolvedGeneration=t.generation;this.offline=false;}
    this.cache.delete(this.key);this.cache.set(this.key,{events:this.events,at:sourceTimes.size?Math.min(...sourceTimes.values()):now,sourceTimes});
    while(this.cache.size>32) this.cache.delete(this.cache.keys().next().value);
    for(const key of denied) this.removeSource(key);
    return true;
  }
  removeSource(key) {for(const item of this.cache.values()) item.events=item.events.filter(e=>e.calendarKey!==key);this.events=this.events.filter(e=>e.calendarKey!==key);}
  clear(){this.cache.clear();this.events=[];this.sources=[];this.resolvedGeneration=0;}
  fail(t,error,now=Date.now()) {
    if(!this.current(t)) return this.events;
    if([401,403,404].includes(error.status)) this.clear();
    this.offline=true; this.events=this.cached(now); return this.events;
  }
}
const dayNames=['sunday','monday','tuesday','wednesday','thursday','friday','saturday'];
export function workingPeriods(s,hours) {
  const h=s.hoursMode==='outlook' && hours?hours:{daysOfWeek:[1,2,3,4,5],startTime:s.hoursStart,endTime:s.hoursEnd};
  const days=(h.daysOfWeek||[]).map(d=>typeof d==='number'?d:dayNames.indexOf(String(d).toLowerCase())).filter(d=>d>=0 && d<=6);
  const start=h.startTime.slice(0,5),end=h.endTime.slice(0,5);
  if(start<end) return [{daysOfWeek:days,startTime:start,endTime:end}];
  return [{daysOfWeek:days,startTime:start,endTime:'24:00'},{daysOfWeek:days.map(d=>(d+1)%7),startTime:'00:00',endTime:end}];
}
function hoursConfig(s,hours) {
  const zone=zoneFor(s);
  const h=s.hoursMode==='outlook' && hours?hours:{daysOfWeek:[1,2,3,4,5],startTime:s.hoursStart,endTime:s.hoursEnd,timeZone:zone};
  let sourceZone=h.timeZone||zone;
  try{new Intl.DateTimeFormat('en',{timeZone:sourceZone});}catch{sourceZone=zone;}
  return {...h,timeZone:sourceZone,daysOfWeek:h.daysOfWeek.map(d=>typeof d==='number'?d:dayNames.indexOf(String(d).toLowerCase()))};
}
export function workingIntervals(s,hours,range) {
  const h=hoursConfig(s,hours),result=[], zone=zoneFor(s);
  // Include neighboring source dates whose overnight work period crosses into this display range.
  for(let d=Temporal.PlainDate.from(range.startDate).subtract({days:2});Temporal.PlainDate.compare(d,Temporal.PlainDate.from(range.endDate).add({days:2}))<0;d=d.add({days:1})) {
    if(!h.daysOfWeek.includes(d.dayOfWeek%7))continue;
    const start=d.toZonedDateTime({timeZone:h.timeZone,plainTime:h.startTime});
    const endDate=h.endTime<=h.startTime?d.add({days:1}):d;
    const end=endDate.toZonedDateTime({timeZone:h.timeZone,plainTime:h.endTime});
    result.push({id:`hours:${d}`,start:start.withTimeZone(zone).toString({timeZoneName:'never'}),end:end.withTimeZone(zone).toString({timeZoneName:'never'}),display:'inverse-background',groupId:'working-hours',className:'nonworking-hours'});
  }
  return result;
}
export function initialScrollTime(s,hours,anchor) {
  const h=hoursConfig(s,hours);
  return Temporal.PlainDate.from(anchor).toZonedDateTime({timeZone:h.timeZone,plainTime:h.startTime}).withTimeZone(zoneFor(s)).toPlainTime().toString();
}
export function dateInZone(value,zone) {return value.length===10?value:Temporal.Instant.from(value).toZonedDateTimeISO(zone).toPlainDate().toString();}
export function agendaEmptyEvents(events,range,s) {
  const result=[], zone=zoneFor(s);
  for(let d=Temporal.PlainDate.from(range.startDate);Temporal.PlainDate.compare(d,range.endDate)<0;d=d.add({days:1})) {
    const key=d.toString(); if(!s.weekends && [6,7].includes(d.dayOfWeek)) continue;
    const start=d.toZonedDateTime({timeZone:zone,plainTime:'00:00'}).epochMilliseconds;
    const end=d.add({days:1}).toZonedDateTime({timeZone:zone,plainTime:'00:00'}).epochMilliseconds;
    if(!events.some(e=>e.isAllDay ? e.start.slice(0,10)<d.add({days:1}).toString() && e.end.slice(0,10)>key : Date.parse(e.start)<end && Date.parse(e.end)>start))
      result.push({id:`empty:${key}`,title:'No events',start:key,allDay:true,extendedProps:{empty:true},className:'empty-event'});
  }
  return result;
}
