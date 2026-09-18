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
const api = pathname => {
  if (pathname === '/configuration') return { clientId: '11111111-1111-1111-1111-111111111111', tenant: 'organizations' };
  if (pathname === '/auth/status') return { isSignedIn: false };
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
  calls.push({ pathname, method: request.method });
  const data = api(pathname);
  if (data !== null) { response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify(data)); return; }
  const file = path.join(root, pathname === '/' ? 'index.html' : pathname.slice(1));
  if (!file.startsWith(root)) { response.writeHead(403); response.end(); return; }
  try { response.writeHead(200, { 'Content-Type': pathname.endsWith('.js') ? 'text/javascript' : 'text/html' }); response.end(await readFile(file)); }
  catch { response.end(''); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  await mkdir(output, { recursive: true });
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  await page.getByRole('button', { name: 'Connect Outlook', exact: true }).waitFor();
  assert.equal(calls.some(c => c.method === 'POST'), false);
  await page.getByRole('button', { name: 'Connect Outlook', exact: true }).click();
  await page.getByText('Outlook is connected.', { exact: true }).waitFor();
  await page.getByText('Available calendars', { exact: true }).click();
  assert.match(await page.locator('#outlook-calendars').textContent(), /<script>Calendar label<\/script>/);
  assert.equal(await page.locator('#outlook-calendars script').count(), 0);
  for (const [width, height] of [[1280, 900], [390, 844]]) {
    await page.setViewportSize({ width, height });
    await page.locator('#outlook').scrollIntoViewIfNeeded();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.screenshot({ path: path.join(output, `${width}.png`), fullPage: true });
  }
  assert.equal(calls.filter(c => c.pathname.endsWith('/connect')).length, 1);
  assert.deepEqual(errors, []);
  console.log('Setup browser checks passed: one explicit consent request, literal calendar text, desktop/mobile layout, no runtime errors.');
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
