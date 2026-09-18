export class OutlookApi {
  constructor({native=false,credential='',fetch:fetcher=(...args)=>globalThis.fetch(...args)}={}) {this.base=native?'http://localhost:8787':'';this.native=native;this.credential=credential;this.fetch=fetcher;}
  async initialize() {if(!this.native) {const data=await this.request('session',undefined,undefined,true);if(!data.token) throw new Error('Outlook session unavailable');this.token=data.token;}}
  async request(path,body,signal,bootstrap=false,retried=false) {
    const headers={Accept:'application/json'};
    if(body!==undefined) headers['Content-Type']='application/json';
    if(!bootstrap) {if(this.native) headers.Authorization=`Bearer ${this.credential}`;else headers['X-Outlook-Session']=this.token;}
    const response=await this.fetch(`${this.base}/api/outlook/${path}`,{method:body===undefined?'GET':'POST',headers,
      body:body===undefined?undefined:JSON.stringify(body),signal,cache:'no-store',credentials:'omit'});
    if(response.status===401 && !bootstrap) {
      this.onUnauthorized?.();
      if(!this.native && !retried && (body===undefined || ['view','event-details'].includes(path))) {
        await this.initialize();return this.request(path,body,signal,false,true);
      }
    }
    if(!response.ok) {let data;try{data=await response.json();}catch{}const error=new Error(data?.error?.message||data?.message||`Outlook unavailable (${response.status})`);error.status=response.status;error.code=data?.error?.code||data?.code;throw error;}
    return response.status===204?null:response.json();
  }
  get(path,signal){return this.request(path,undefined,signal);}
  post(path,body,signal){return this.request(path,body,signal);}
  pair(instanceId,requestSecret){return this.request('pairings',{instanceId,requestSecret},undefined,true);}
  poll(id,requestSecret){return this.request(`pairings/${encodeURIComponent(id)}/poll`,{requestSecret},undefined,true);}
}
export async function loadMetadata(api,signal) {
  const [calendars,preferences]=await Promise.allSettled([api.get('calendars',signal),api.get('preferences',signal)]);
  if(calendars.status==='rejected')throw calendars.reason;
  if(preferences.status==='rejected' && (preferences.reason.status===401 || preferences.reason.name==='AbortError'))throw preferences.reason;
  return {catalog:calendars.value,preferences:preferences.status==='fulfilled'?preferences.value:{workingHours:null,pcTimeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,timeZones:[]}};
}
