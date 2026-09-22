import { Temporal } from 'temporal-polyfill';
import { maskEvent } from './state.js';
export const fixtureCalendars=[
  {key:'work',name:'My calendar',owner:'Demo account',kind:'personal',color:'#569ee8',canViewPrivateItems:true},
  {key:'team',name:'IT team',owner:'Alex Morgan',kind:'shared',color:'#47b8a3',canViewPrivateItems:true},
  {key:'ops',name:'Operations',owner:'Operations group',kind:'group',color:'#e8ae51',canViewPrivateItems:false}
];
export class FixtureApi {
  demo=true; launched=[]; events=new Map();
  async initialize(){}
  async get(path) {
    if(path==='status')return {configured:true,signedIn:true,ready:true};
    if(path==='calendars')return fixtureCalendars;
    if(path==='preferences')return {workingHours:{daysOfWeek:['monday','tuesday','wednesday','thursday','friday'],startTime:'08:00:00',endTime:'18:00:00',timeZone:'America/New_York'},pcTimeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,timeZones:['America/New_York','America/Chicago','America/Los_Angeles','Europe/London','Europe/Paris','Asia/Kolkata','Asia/Kathmandu','Australia/Sydney','UTC']};
    throw new Error('Unknown fixture route');
  }
  async post(path,body,signal) {
    if(signal?.aborted)throw new DOMException('Aborted','AbortError');
    if(path==='view' || path==='view/cached') {
      if(this.offline)throw new Error('Demo helper offline');
      const result=[], zone=Intl.DateTimeFormat().resolvedOptions().timeZone;
      for(let d=Temporal.PlainDate.from(body.start.slice(0,10));Temporal.PlainDate.compare(d,body.end.slice(0,10))<0;d=d.add({days:1})) {
        if(d.dayOfWeek===7)continue;
        const count=d.dayOfWeek===2?7:d.dayOfWeek===6?1:3;
        for(let i=0;i<count;i++) {
          const calendarKey=['work','team','ops'][i%3],reference=`${d}:${i}`,hour=i<3?9+i*2:10;
          const isPrivate=d.dayOfWeek===3 && i===0;
          const start=d.toZonedDateTime({timeZone:zone,plainTime:`${String(hour).padStart(2,'0')}:00`});
          const event={reference,calendarKey,title:isPrivate?'Medical appointment':['Weekly planning','Service desk review','Release readiness','Vendor check-in','Infrastructure review','Project handoff','Budget review'][i],
            start:start.toString({timeZoneName:'never'}),end:start.add({minutes:90}).toString({timeZoneName:'never'}),isPrivate,isAllDay:false,isCancelled:false,
            location:isPrivate?'Downtown clinic':'Microsoft Teams',organizer:'Alex Morgan',attendees:['Jamie Lee','Casey Taylor'],description:'Review progress, discuss open questions, and confirm next steps.',joinUrl:isPrivate?null:'https://teams.microsoft.com/l/meetup-join/demo'};
          this.events.set(reference,event);if(body.calendarKeys.includes(calendarKey))result.push(maskEvent(event));
        }
        if(d.dayOfWeek===4) {
          const event={reference:`${d}:all`,calendarKey:'team',title:'Release window',start:d.toString(),end:d.add({days:2}).toString(),isAllDay:true,isPrivate:false,isCancelled:false};
          this.events.set(event.reference,event);if(body.calendarKeys.includes('team'))result.push(event);
        }
      }
      return {events:result,sources:body.calendarKeys.map(calendarKey=>({calendarKey,fetchedAt:new Date().toISOString(),stale:path==='view/cached',error:null}))};
    }
    if(path==='event-details') {if(this.offline)throw new Error('Demo helper offline');return {...this.events.get(body.reference)};}
    if(path==='join'){this.launched.push({...body});return null;}
    throw new Error('Unknown fixture route');
  }
}
