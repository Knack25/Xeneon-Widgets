import http from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const requireWidget = createRequire(new URL('../../outlook-edge-widget/package.json', import.meta.url));
const { chromium } = requireWidget('@playwright/test');
const root = fileURLToPath(new URL('../src/MicrosoftWidgets.Helper/wwwroot/', import.meta.url));
const output = fileURLToPath(new URL('../dist/setup-browser/', import.meta.url));
const calls = [];
let connected = false;
let permissionState = 'available';
let configured = true;
const bootstrapToken = 'fixture-bootstrap-token';
const ownerSession = 'fixture-owner-session';
const api = pathname => {
  if (pathname === '/configuration') return { clientId: configured ? '11111111-1111-1111-1111-111111111111' : '', tenant: 'organizations' };
  if (pathname === '/auth/status') return { isSignedIn: true, displayName: 'Example account' };
  if (pathname === '/auth/capabilities') return Object.fromEntries(['planner', 'assigneeNames', 'boardMembers'].map(key => [key, { state: permissionState }]));
  if (pathname === '/auth/enable-assignee-names') { permissionState = 'available'; return {isSignedIn:true}; }
  if (pathname === '/plans') return [{planId:'fixture',title:'Example board'}];
  if (pathname === '/settings') return {selectedPlanId:'fixture',hideCompletedTasks:true};
  if (pathname === '/installation') return { version: 'development', plannerWidgetAvailable: true, outlookWidgetAvailable: true };
  if (pathname === '/updates') return { state: 'idle', currentVersion: 'development' };
  if (pathname === '/updates/result') return {};
  if (pathname === '/api/outlook/session') return { token: 'fixture-session' };
  if (pathname === '/api/outlook/status') return { configured: true, signedIn: connected, ready: connected };
  if (pathname === '/api/outlook/connect') { connected = true; return { isSignedIn: true }; }
  if (pathname === '/api/outlook/calendars') return [{ key: 'fixture', name: '<script>Calendar label</script>', kind: 'personal', owner: 'Example account' }];
  if (pathname === '/api/outlook/pairings') return [{ id: 'fixture-pair', instanceId: 'Fixture display', code: '123456', expiresAt: new Date(Date.now() + 60000).toISOString() }];
  if (pathname === '/api/outlook/paired') return [];
  return null;
};
const server = http.createServer(async (request, response) => {
  const pathname = new URL(request.url, 'http://localhost').pathname;
  calls.push({ pathname, method: request.method, url: request.url, headers: request.headers });
  if (pathname === '/api/local-access/session') {
    if (request.method !== 'POST' || request.headers['x-microsoft-widgets-bootstrap'] !== bootstrapToken) {
      response.writeHead(401, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ message: 'Owner session required.' })); return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ token: ownerSession })); return;
  }
  const data = api(pathname);
  if (data !== null) {
    if (request.headers['x-microsoft-widgets-owner'] !== ownerSession) {
      response.writeHead(401, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ message: 'Owner session required.' })); return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify(data)); return;
  }
  const file = path.join(root, pathname === '/' ? 'index.html' : pathname.slice(1));
  if (!file.startsWith(root)) { response.writeHead(403); response.end(); return; }
  const contentType = pathname.endsWith('.js') ? 'text/javascript' : pathname.endsWith('.css') ? 'text/css' : 'text/html';
  try { response.writeHead(200, {
    'Content-Type': contentType,
    'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
  }); response.end(await readFile(file)); }
  catch { response.end(''); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  await mkdir(output, { recursive: true });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    const replaceState = history.replaceState.bind(history);
    window.__replaceStateCalls = 0;
    history.replaceState = (...args) => { window.__replaceStateCalls++; return replaceState(...args); };
  });
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.locator('#tray-instructions').getByText('Open Microsoft Widgets Setup from the notification area or Start menu to continue.').waitFor();
  assert.equal(calls.some(call => call.pathname === '/configuration' || call.pathname === '/installation'), false);
  await page.goto(`http://127.0.0.1:${server.address().port}/#access=${bootstrapToken}&section=updates`);
  await page.reload();
  await page.getByRole('navigation', {name:'Helper sections'}).waitFor({timeout:3000});
  await page.waitForFunction(() => location.hash === '#updates');
  assert.equal(await page.evaluate(() => window.__replaceStateCalls > 0), true);
  assert.equal(await page.evaluate(() => location.search.includes('access=')), false);
  assert.equal(calls.some(call => call.url.includes('access=')), false);
  assert.equal(calls.some(call => call.pathname === '/api/local-access/session' && call.headers['x-microsoft-widgets-bootstrap'] === bootstrapToken), true);
  await page.getByRole('navigation', {name:'Helper sections'}).waitFor({timeout:3000});
  assert.equal(await page.locator('#updates').isVisible(), true);
  await page.goto(`http://127.0.0.1:${server.address().port}/#updates`);
  assert.equal(await page.locator('#updates').isVisible(), true,'Tray update link opens Settings');
  await page.getByRole('link', {name:'Planner',exact:true}).click();
  await page.getByRole('button', { name: 'Assignee names enabled', exact: true }).waitFor();
  assert.equal(await page.locator('#enable-names').isDisabled(), true);
  assert.equal(await page.locator('#enable-members').isDisabled(), true);
  assert.equal(await page.locator('#sign-in').isDisabled(), true);
  assert.equal(await page.locator('#board').inputValue(), 'fixture');
  assert.equal(calls.some(c => c.method === 'POST' && c.pathname !== '/api/local-access/session'), false);
  await page.getByRole('link', {name:'Outlook',exact:true}).click();
  await page.getByRole('button', { name: 'Connect Outlook', exact: true }).click();
  await page.locator('#outlook-status').filter({hasText:'Outlook is connected.'}).waitFor();
  assert.equal(await page.locator('#outlook-connect').isDisabled(), true);
  await page.getByText('Available calendars', { exact: true }).click();
  assert.match(await page.locator('#outlook-calendars').textContent(), /<script>Calendar label<\/script>/);
  assert.equal(await page.locator('#outlook-calendars script').count(), 0);
  for (const [width, height] of [[1280, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    await page.locator('#outlook').scrollIntoViewIfNeeded();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `${width}.png`), fullPage: true });
    await page.getByRole('link', {name:'Settings',exact:true}).click();
    assert.equal(await page.locator('#updates').isVisible(), true);
    assert.equal(await page.locator('#client-id').isVisible(), false);
    await page.getByText('Application configuration', {exact:true}).click();
    assert.equal(await page.locator('#client-id').isVisible(), true);
    await page.getByText('Application configuration', {exact:true}).click();
    await page.getByRole('link', {name:'Overview',exact:true}).click();
    assert.match(await page.locator('#overview-approvals').textContent(), /awaiting approval/);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `overview-${width}.png`), fullPage: true });
    await page.getByRole('link', {name:'Outlook',exact:true}).click();
  }
  assert.equal(calls.filter(c => c.pathname.endsWith('/connect')).length, 1);
  permissionState = 'unavailable';
  await page.getByRole('link', {name:'Planner',exact:true}).click();
  await page.reload();
  await page.getByRole('button', {name:'Retry assignee names',exact:true}).waitFor();
  const postsBefore = calls.filter(c => c.method === 'POST').length;
  permissionState = 'available';
  await page.getByRole('button', {name:'Retry assignee names',exact:true}).click();
  await page.getByRole('button', {name:'Planner connected',exact:true}).waitFor();
  await page.waitForFunction(() => !document.querySelector('#board').disabled, null, {timeout:3000});
  assert.equal(await page.locator('#board').inputValue(), 'fixture');
  assert.equal(calls.filter(c => c.method === 'POST').length, postsBefore);
  permissionState = 'interaction_required';
  await page.reload();
  await page.getByRole('button', {name:'Show assignee names',exact:true}).click();
  await page.getByRole('button', {name:'Assignee names enabled',exact:true}).waitFor();
  assert.equal(await page.locator('#enable-names').isDisabled(), true);
  assert.equal(calls.filter(c => c.pathname === '/auth/enable-assignee-names').length, 1);
  configured = false;
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.locator('#client-id').waitFor();
  assert.equal(await page.locator('#view-settings').isVisible(), true);
  assert.equal(await page.locator('#client-id').inputValue(), '');
  assert.deepEqual(errors, []);
  console.log('Setup browser checks passed: automatic permission states/data load, disabled available actions, retry without consent, explicit missing-permission flow, literal calendar text, desktop/mobile layout, no runtime errors.');
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
