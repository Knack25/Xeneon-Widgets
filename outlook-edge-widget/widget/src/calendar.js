import { Calendar } from 'fullcalendar';
import theme from 'fullcalendar/themes/classic';
import dayGrid from 'fullcalendar/daygrid';
import timeGrid from 'fullcalendar/timegrid';
import list from 'fullcalendar/list';
import { rangeFor, zoneFor, eventId, agendaEmptyEvents, workingIntervals, initialScrollTime } from './state.js';
import { colorFor, el } from './dom.js';

export class CalendarAdapter {
  constructor(container,{settings,anchor,preferences,onEvent,onMore,onTitle}) {
    this.container=container;this.settings=settings;this.anchor=anchor;this.preferences=preferences;
    container.dataset.view=settings.view;
    this.calendar=new Calendar(container,{
      plugins:[theme,dayGrid,timeGrid,list],initialView:settings.view==='agenda'?'outlookAgenda':settings.view,
      initialDate:anchor,headerToolbar:false,footerToolbar:false,height:'100%',
      timeZone:zoneFor(settings),firstDay:settings.firstDay,weekends:settings.weekends,
      editable:false,selectable:false,eventStartEditable:false,eventDurationEditable:false,
      eventInteractive:true,nowIndicator:true,allDaySlot:true,fixedWeekCount:false,moreLinkClass:'outlook-more',
      scrollTime:this.hoursStart(),scrollTimeReset:false,slotMinTime:'00:00:00',slotMaxTime:'24:00:00',
      slotDuration:'00:30:00',slotMinHeight:28,dayMaxEvents:2,eventMaxStack:this.stackLimit(),eventMinHeight:38,
      slotEventOverlap:false,
      views:{outlookAgenda:{type:'list',visibleRange:()=>this.agendaRange(),titleFormat:{year:'numeric',month:'short',day:'numeric'}},timeGridWeek:{titleFormat:{year:'numeric',month:'short',day:'numeric'}}},
      eventContent:info=>{
        const event=info.event, summary=event.extendedProps.summary;
        const node=el('div',{class:'event-content'},el('strong',{},event.title));
        if(info.timeText)node.append(el('small',{},info.timeText));
        if(summary && !summary.isPrivate) node.append(el('small',{class:'event-calendar'},event.extendedProps.calendarName||''));
        return {domNodes:[node]};
      },
      eventDidMount:info=>{
        const summary=info.event.extendedProps.summary;
        if(!summary){info.el.removeAttribute('tabindex');return;}
        info.el.dataset.eventId=info.event.id;
        info.el.setAttribute('aria-label',`${summary.isPrivate?'Private event':summary.title}${info.timeText?`, ${info.timeText}`:''}`);
        info.el.style.setProperty('--source-color',info.event.extendedProps.sourceColor);
      },
      eventClick:info=>{const summary=info.event.extendedProps.summary;if(summary)onEvent(summary,info.el);},
      moreLinkClick:info=>{
        onMore(info.allSegs.map(seg=>seg.event.extendedProps.summary).filter(Boolean),info.jsEvent?.target);
        // Pinned v7 treats a truthy non-string as a handled action; strings navigate.
        return true;
      },
      datesSet:info=>onTitle(info.view.title)
    });
    this.calendar.render();
    this.signature=this.configSignature();
    this.resize=new ResizeObserver(()=>{this.calendar.setOption('eventMaxStack',this.stackLimit());});this.resize.observe(container);
  }
  stackLimit(){return Math.max(1,Math.min(4,Math.floor((this.container.clientWidth-60)/(this.settings.weekends?7:5)/90)));}
  hoursStart(){return initialScrollTime(this.settings,this.preferences?.workingHours,this.anchor);}
  agendaRange(){const r=rangeFor(this.anchor,{...this.settings,view:'agenda'});return {start:r.startDate,end:r.endDate};}
  configSignature(){const {view,firstDay,weekends,agendaDays,hoursMode,hoursStart,hoursEnd}=this.settings;return JSON.stringify([this.anchor,view,firstDay,weekends,agendaDays,hoursMode,hoursStart,hoursEnd,zoneFor(this.settings),this.preferences?.workingHours]);}
  configure(settings,anchor,preferences) {
    const oldHours=this.hoursStart(), changedView=this.settings.view!==settings.view || this.anchor!==anchor;
    this.settings=settings;this.anchor=anchor;this.preferences=preferences;
    const signature=this.configSignature();if(this.signature===signature)return;this.signature=signature;
    const scroll=[...this.container.querySelectorAll('*')].filter(n=>n.scrollHeight>n.clientHeight+1).map(n=>[n,n.scrollTop]);
    this.container.dataset.view=settings.view;
    this.calendar.batchRendering(()=>{
      for(const [key,value] of Object.entries({firstDay:settings.firstDay,weekends:settings.weekends,timeZone:zoneFor(settings),eventMaxStack:this.stackLimit()}))this.calendar.setOption(key,value);
      const view=settings.view==='agenda'?'outlookAgenda':settings.view;
      if(view==='outlookAgenda')this.calendar.changeView(view,this.agendaRange());else this.calendar.changeView(view,anchor);
    });
    if(changedView){for(const n of this.container.querySelectorAll('*'))n.scrollTop=0;if(settings.view==='timeGridWeek')this.calendar.scrollToTime(this.hoursStart());}
    else if(oldHours!==this.hoursStart())this.calendar.scrollToTime(this.hoursStart());
    else for(const [n,top]of scroll)if(n.isConnected)n.scrollTop=top;
  }
  update(events,catalog) {
    const scroll=[...this.container.querySelectorAll('*')].filter(n=>n.scrollHeight>n.clientHeight+1 || n.scrollWidth>n.clientWidth+1).map(n=>[n,n.scrollTop,n.scrollLeft]);
    const focusId=document.activeElement?.dataset?.eventId;
    const input=events.map(e=>{const c=catalog.find(c=>c.key===e.calendarKey),color=colorFor(c);return {id:eventId(e),title:e.title,
      start:e.isAllDay?e.start.slice(0,10):e.start,end:e.isAllDay?e.end.slice(0,10):e.end,allDay:e.isAllDay,
      color,contrastColor:'#18262c',extendedProps:{summary:e,calendarName:c?.name,sourceColor:color}};});
    if(this.settings.view==='agenda')input.push(...agendaEmptyEvents(events,rangeFor(this.anchor,this.settings),this.settings));
    if(this.settings.view==='timeGridWeek')input.push(...workingIntervals(this.settings,this.preferences?.workingHours,rangeFor(this.anchor,this.settings)));
    const next=new Map(input.map(e=>[e.id,e]));
    this.calendar.batchRendering(()=>{
      for(const current of this.calendar.getEvents()) {
        const value=next.get(current.id);
        if(!value){current.remove();continue;}
        if(this.previous?.get(current.id)!==JSON.stringify(value)){current.remove();this.calendar.addEvent(value);}
        next.delete(current.id);
      }
      for(const value of next.values())this.calendar.addEvent(value);
    });
    this.previous=new Map(input.map(e=>[e.id,JSON.stringify(e)]));
    for(const [n,top,left]of scroll){if(n.isConnected){n.scrollTop=top;n.scrollLeft=left;}}
    if(focusId && !document.activeElement?.dataset?.eventId) [...this.container.querySelectorAll('[data-event-id]')].find(n=>n.dataset.eventId===focusId)?.focus({preventScroll:true});
  }
  destroy(){this.resize.disconnect();this.calendar.destroy();}
}
