import { build } from 'esbuild';
import { readFile, writeFile, mkdir, cp, readdir, lstat, realpath, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { CalendarDays } from 'lucide';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const dist=path.join(root,'dist');
async function assertNoLinks(directory){
  let stat;
  try{stat=await lstat(directory);}catch(error){if(error.code==='ENOENT')return;throw error;}
  if(stat.isSymbolicLink())throw new Error(`Refusing to clean linked build path: ${directory}`);
  if(!stat.isDirectory())throw new Error(`Build output is not a directory: ${directory}`);
  for(const entry of await readdir(directory,{withFileTypes:true})){
    const entryPath=path.join(directory,entry.name);
    const entryStat=await lstat(entryPath);
    if(entryStat.isSymbolicLink())throw new Error(`Refusing to clean linked build entry: ${entryPath}`);
    if(entryStat.isDirectory())await assertNoLinks(entryPath);
  }
}
await assertNoLinks(dist);
try{
  const [realRoot,realDist]=await Promise.all([realpath(root),realpath(dist)]);
  if(!realDist.startsWith(`${realRoot}${path.sep}`))throw new Error(`Build output escapes widget root: ${realDist}`);
}catch(error){if(error.code!=='ENOENT')throw error;}
await rm(dist, { recursive: true, force: true });
await mkdir(path.join(dist,'assets'),{recursive:true});
await build({entryPoints:[path.join(root,'widget/src/app.js')],bundle:true,outfile:path.join(dist,'assets/app.js'),format:'iife',target:['chrome110'],minify:true,legalComments:'eof'});
for(const file of ['index.html','manifest.json','translation.json','translations'])await cp(path.join(root,'widget',file),path.join(dist,file),{recursive:true});
await mkdir(path.join(dist,'resources'),{recursive:true});
const attrs=object=>Object.entries(object).map(([k,v])=>`${k}="${v}"`).join(' ');
const svg=`<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="#47b8a3" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${CalendarDays.map(([tag,a])=>`<${tag} ${attrs(a)}/>`).join('')}</svg>`;
await writeFile(path.join(dist,'resources/icon.svg'),svg);
const lock=JSON.parse(await readFile(path.join(root,'package-lock.json'),'utf8'));let notices='Locally bundled dependencies. FullCalendar Standard (no premium plugins).\n';
for(const [location,info]of Object.entries(lock.packages)){
  if(!location || info.dev || info.optional)continue;
  const dir=path.join(root,location);let files;try{files=await readdir(dir);}catch{continue;}
  const license=files.find(f=>/^licen[sc]e(?:\.|$)/i.test(f));
  if(!license)throw new Error(`Missing license for ${location}`);
  notices+=`\n===== ${location.replace('node_modules/','')} ${info.version} =====\n${await readFile(path.join(dir,license),'utf8')}\n`;
}
await writeFile(path.join(dist,'THIRD-PARTY-NOTICES.txt'),notices);
console.log('Built outlook-edge-widget/dist: index.html, assets/app.js, assets/app.css, manifest.json, resources/icon.svg, translations/en.json, THIRD-PARTY-NOTICES.txt');
