import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const root=new URL('../',import.meta.url);
test('native HEAD keeps title readable by the iCUE XML parser',async()=>{
  const html=await readFile(new URL('widget/index.html',root),'utf8');
  const head=html.match(/<head>([\s\S]*?)<\/head>/)[1];
  assert.match(head,/<title>Outlook Edge Widget<\/title>/);
  for(const tag of head.matchAll(/<(?:meta|link)\b[^>]*>/g))assert.match(tag[0],/\/>$/);
});
test('dependency versions are exact, lockfile matches, and Outlook version is independent',async()=>{
  const pkg=JSON.parse(await readFile(new URL('package.json',root),'utf8'));
  const lock=JSON.parse(await readFile(new URL('package-lock.json',root),'utf8'));
  const manifest=JSON.parse(await readFile(new URL('widget/manifest.json',root),'utf8'));
  assert.equal(pkg.version,'0.2.0');assert.equal(manifest.version,pkg.version);assert.equal(lock.packages[''].version,pkg.version);
  for(const [name,version] of Object.entries({...pkg.dependencies,...pkg.devDependencies})) {
    assert.match(version,/^\d+\.\d+\.\d+$/);assert.equal(lock.packages[`node_modules/${name}`].version,version);
  }
});
