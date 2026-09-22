export class OutlookApi {
  constructor({native=false,credential='',fetch:fetcher=(...args)=>globalThis.fetch(...args),helperApi}={}) {this.base=native?'http://localhost:8787':'';this.native=native;this.credential=native?credential:'';this.fetch=fetcher;this.helperApi=helperApi;}
  async initialize() {
    if(this.native)return;
    if(!this.helperApi && !globalThis.helperApi) {
      await new Promise((resolve,reject)=>{const script=document.createElement('script');script.src='/helper-api.js';script.onload=resolve;script.onerror=()=>reject(new Error('Open Microsoft Widgets Setup to preview Outlook.'));document.head.append(script);});
    }
    this.helperApi ??= globalThis.helperApi;
    if(!await this.helperApi?.ready)throw new Error('Open Microsoft Widgets Setup to preview Outlook.');
  }
  async request(path,body,signal,bootstrap=false) {
    if(bootstrap && !this.native)throw new Error('Preview uses the setup owner session.');
    const headers={Accept:'application/json'};
    if(body!==undefined) headers['Content-Type']='application/json';
    if(!bootstrap && this.native && this.credential) headers['X-Microsoft-Widgets-Credential']=this.credential;
    if(!this.native)await this.initialize();
    const fetcher=this.native?this.fetch:this.helperApi.fetch.bind(this.helperApi);
    const response=await fetcher(`${this.base}${bootstrap?'/api/local-access/':'/api/outlook/'}${path}`,{method:body===undefined?'GET':'POST',headers,
      body:body===undefined?undefined:JSON.stringify(body),signal,cache:'no-store',credentials:'omit'});
    if(response.status===401 && !bootstrap) {
      this.onUnauthorized?.();
    }
    if(!response.ok) {let data;try{data=await response.json();}catch{}const error=new Error(data?.error?.message||data?.message||`Outlook unavailable (${response.status})`);error.status=response.status;error.code=data?.error?.code||data?.code;throw error;}
    return response.status===204?null:response.json();
  }
  get(path,signal){return this.request(path,undefined,signal);}
  post(path,body,signal){return this.request(path,body,signal);}
  pair(instanceId,requestSecret){return this.request('pairings',{scope:'outlook',instanceId,requestSecret},undefined,true);}
  poll(id,requestSecret){return this.request(`pairings/${encodeURIComponent(id)}/poll`,{requestSecret},undefined,true);}
}
export async function loadMetadata(api,signal) {
  const [calendars,preferences]=await Promise.allSettled([api.get('calendars',signal),api.get('preferences',signal)]);
  if(calendars.status==='rejected')throw calendars.reason;
  if(preferences.status==='rejected' && (preferences.reason.status===401 || preferences.reason.name==='AbortError'))throw preferences.reason;
  return {catalog:calendars.value,preferences:preferences.status==='fulfilled'?preferences.value:{workingHours:null,pcTimeZone:Intl.DateTimeFormat().resolvedOptions().timeZone,timeZones:[]}};
}
