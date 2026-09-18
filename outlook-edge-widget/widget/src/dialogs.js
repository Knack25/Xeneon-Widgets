import { el, button, icon, calendarLabel, formatWhen } from './dom.js';

export class Dialogs {
  constructor(root,fallback) {
    this.root=root;this.fallback=fallback;this.generation=0;
    this.overlay=el('div',{class:'overlay',hidden:''});
    this.panel=el('section',{class:'dialog',role:'dialog','aria-modal':'true','aria-labelledby':'dialog-title',tabindex:'-1'});
    this.overlay.append(this.panel);root.append(this.overlay);
    this.overlay.addEventListener('pointerdown',e=>{this.backdropStart=e.target===this.overlay;});
    this.overlay.addEventListener('click',e=>{if(this.backdropStart && e.target===this.overlay)this.close();this.backdropStart=false;});
    this.overlay.addEventListener('keydown',e=>{
      if(e.key==='Escape'){e.preventDefault();this.close();}
      if(e.key==='Tab') {
        const all=[...this.panel.querySelectorAll('button:not(:disabled),input:not(:disabled),[tabindex="0"]')].filter(n=>!n.hidden && n.getClientRects().length);
        const list=all.filter(n=>n.type!=='radio' || n===(all.find(r=>r.type==='radio' && r.name===n.name && r.checked)||all.find(r=>r.type==='radio' && r.name===n.name)));
        const first=list[0],last=list.at(-1);
        if(!first){e.preventDefault();this.panel.focus();}
        else if(e.shiftKey && (document.activeElement===first || document.activeElement===this.panel)){e.preventDefault();last.focus();}
        else if(!e.shiftKey && document.activeElement===last){e.preventDefault();first.focus();}
      }
    });
  }
  open(title,origin=document.activeElement) {
    if(this.overlay.hidden) this.origin=origin;
    this.abort?.abort();this.generation++;this.kind='other';this.detail=null;
    this.panel.replaceChildren(el('div',{class:'dialog-head'},el('h2',{id:'dialog-title'},title),button('Close',()=>this.close(),'X')));
    this.overlay.hidden=false;this.root.querySelector('.shell').inert=true;
    this.panel.querySelector('button').focus();return this.generation;
  }
  close(){this.abort?.abort();this.generation++;this.detail=null;this.kind=null;this.overlay.hidden=true;this.panel.replaceChildren();this.root.querySelector('.shell').inert=false;(this.origin?.isConnected?this.origin:this.fallback)?.focus();this.onClose?.();}
  async details(summary,calendar,api,zone,canUse,origin) {
    const generation=this.open(summary.isPrivate?'Private event':summary.title,origin);this.kind='details';
    this.abort=new AbortController();const signal=this.abort.signal;
    const loading=el('p',{role:'status'},'Loading details...');this.panel.append(calendarLabel(calendar),loading);
    try {
      if(!canUse()) throw new Error('Details unavailable while offline.');
      const data=await api.post('event-details',{calendarKey:summary.calendarKey,reference:summary.reference},signal);
      if(generation!==this.generation || signal.aborted) return;
      if(data.reference!==summary.reference || data.calendarKey!==summary.calendarKey) throw new Error('Event details did not match the selected event.');
      this.detail=data;loading.remove();this.panel.querySelector('h2').textContent=data.title||'Private event';
      const dl=el('dl');
      for(const [name,value] of [['When',formatWhen(data,zone)],['Location',data.location],['Organizer',data.organizer],['Attendees',(data.attendees||[]).join(', ')]])
        dl.append(el('dt',{},name),el('dd',{},value||'Not specified'));
      this.panel.append(dl,el('h3',{},'Description'),el('p',{class:'description'},data.description||'No description'));
      let url;try{url=new URL(data.joinUrl);}catch{}
      if(url?.protocol==='https:') {
        const status=el('p',{role:'status',class:'muted'});
        const join=button('Join meeting',async()=>{
          if(!canUse()){status.textContent='Meeting launch unavailable while offline.';return;}
          join.disabled=true;status.textContent='Opening meeting...';
          try {await api.post('join',{calendarKey:summary.calendarKey,reference:summary.reference},signal);if(generation===this.generation) status.textContent=api.demo?'Demo meeting launch recorded.':'Meeting opened on your PC.';}
          catch(error){if(generation===this.generation) status.textContent=error.message;}
          finally{setTimeout(()=>{join.disabled=false;},1500);}
        });join.className='join';join.prepend(icon('Video'));
        this.panel.append(el('p',{class:'muted'},url.hostname),join,status);
      }
    } catch(error) {if(generation===this.generation && !signal.aborted) loading.textContent=error.message;}
  }
  unavailable(){if(this.kind==='overflow'){this.close();return;}if(this.kind==='details'){this.abort?.abort();this.generation++;this.detail=null;const title=this.panel.querySelector('.dialog-head');this.panel.replaceChildren(title,el('p',{role:'status'},'Details unavailable. Reconnect and reopen this event.'));this.panel.querySelector('h2').textContent='Event details';this.panel.querySelector('button').focus();}}
  list(title,events,catalog,openDetails,origin) {
    this.open(title,origin);this.kind='overflow';
    for(const event of events) {
      const calendar=catalog.find(c=>c.key===event.calendarKey);
      const row=button(event.isPrivate?'Private event':event.title,()=>openDetails(event,row));row.className='overflow-event';
      row.replaceChildren(calendarLabel(calendar),el('strong',{},event.isPrivate?'Private event':event.title),el('small',{},event.isAllDay?'All day':event.start));
      this.panel.append(row);
    }
  }
}
