import { createElement, CalendarDays, ChevronDown, ChevronLeft, ChevronRight, Settings2, X, Video, RefreshCw } from 'lucide';
const icons={CalendarDays,ChevronDown,ChevronLeft,ChevronRight,Settings2,X,Video,RefreshCw};
export function el(tag,attrs={},...children) {
  const node=document.createElement(tag);
  for(const [key,value] of Object.entries(attrs)) {
    if(key==='class') node.className=value;
    else if(key.startsWith('on')) node.addEventListener(key.slice(2).toLowerCase(),value);
    else if(value!==undefined && value!==null) node.setAttribute(key,String(value));
  }
  node.append(...children.filter(c=>c!==undefined && c!==null));return node;
}
export function icon(name) {return createElement(icons[name],{width:18,height:18,'aria-hidden':'true'});}
export function button(label,handler,name) {return el('button',{type:'button',title:label,'aria-label':label,class:name?'icon-button':'',onclick:handler},...(name?[icon(name)]:[label]));}
export function colorFor(calendar) {
  const raw=calendar?.color;
  if(/^#[0-9a-f]{6}$/i.test(raw)) return raw;
  const colors={lightBlue:'#569ee8',lightGreen:'#47b8a3',lightOrange:'#e8ae51',lightGray:'#83919b',lightYellow:'#c0a133',lightTeal:'#41a6ad',lightPink:'#d779a5',lightBrown:'#ac8065',lightRed:'#d77272'};
  if(colors[raw]) return colors[raw];
  let hash=0;for(const ch of calendar?.key||'') hash=(hash*31+ch.charCodeAt(0))>>>0;
  return ['#569ee8','#47b8a3','#e8ae51','#d779a5','#41a6ad','#d77272'][hash%6];
}
export function swatch(calendar) {const dot=el('span',{class:'swatch','aria-hidden':'true'});dot.style.backgroundColor=colorFor(calendar);return dot;}
export function calendarLabel(calendar) {return el('span',{class:'calendar-label'},swatch(calendar),calendar?.name||'Calendar');}
export function formatWhen(event,zone) {
  if(event.isAllDay) return `${event.start.slice(0,10)}${event.end.slice(0,10)!==event.start.slice(0,10)?` - ${event.end.slice(0,10)} (end exclusive)`:''} | All day`;
  const fmt=new Intl.DateTimeFormat(undefined,{timeZone:zone,month:'short',day:'numeric',hour:'numeric',minute:'2-digit'});
  try{return `${fmt.format(new Date(event.start))} - ${fmt.format(new Date(event.end))} | ${zone}`;}catch{return 'Time unavailable';}
}
