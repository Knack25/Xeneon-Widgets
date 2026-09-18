import { el, swatch } from './dom.js';
import { selectedKeys } from './settings.js';
export function renderPicker(container,catalog,settings,onChange) {
  const focused=container.contains(document.activeElement)?document.activeElement.getAttribute('aria-label'):null;
  const keys=selectedKeys(settings,catalog), all=el('input',{type:'checkbox','aria-label':'Select all calendars'});
  all.checked=settings.selectionMode==='all';all.indeterminate=keys.length>0 && settings.selectionMode!=='all';
  all.onchange=()=>onChange({selectionMode:all.checked?'all':'selected',calendarKeys:[]});
  container.replaceChildren(el('label',{},all,'Select all'),el('hr'));
  for(const calendar of catalog) {
    const check=el('input',{type:'checkbox','aria-label':calendar.name});check.checked=keys.includes(calendar.key);
    check.onchange=()=>onChange({selectionMode:'selected',calendarKeys:check.checked?[...keys,calendar.key]:keys.filter(k=>k!==calendar.key)});
    container.append(el('label',{},check,swatch(calendar),el('span',{},calendar.name,el('small',{},calendar.owner||calendar.kind))));
  }
  if(!catalog.length)container.append(el('p',{class:'muted'},'No calendars available'));
  if(focused)[...container.querySelectorAll('input')].find(n=>n.getAttribute('aria-label')===focused)?.focus({preventScroll:true});
}
