import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/MicrosoftWidgets.Helper/wwwroot/outlook-setup.js', import.meta.url), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
class Element {
  constructor() { this.children = []; this.value = ''; }
  addEventListener(name, handler) { this[name] = handler; }
  replaceChildren(...children) { this.children = children; }
  append(...children) { this.children.push(...children); }
  setAttribute(name, value) { this[name] = value; }
}
function setup({ approval = true, error = null, discoveryErrors = [], statusError = null } = {}) {
  const elements = new Map();
  const calls = [];
  const doc = { querySelector(id) { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); },
    createElement() { return new Element(); }, addEventListener() {}, dispatchEvent() {} };
  vm.runInNewContext(source, {
    document: doc, AbortController, CustomEvent: class {}, Date, setInterval() {},
    confirm: () => approval,
    fetch: async (path, options = {}) => {
      calls.push({ path, options });
      if (statusError && path.endsWith('/status')) return { ok: false, status: 401, json: async () => ({ error: { message: statusError } }) };
      if (error && path.endsWith('/connect')) return { ok: false, status: 403, json: async () => ({ message: error }) };
      const value = path.endsWith('/session') ? { token: 'local-session' }
        : path.endsWith('/status') ? { configured: true, signedIn: true, ready: true, discoveryErrors }
        : path.endsWith('/pairings') ? [{ id: 'pair-id', code: '123456', instanceId: 'widget-a', expiresAt: new Date().toISOString() }]
        : path.endsWith('/paired') ? [{ credentialId: 'credential-id', instanceId: 'widget-a' }]
        : path.endsWith('/calendars') ? [{ key: 'calendar', name: '<script>example</script>', owner: 'Example', kind: 'personal' }]
        : path === '/installation' ? { outlookWidgetAvailable: true } : {};
      return { ok: true, status: 200, json: async () => value };
    }
  });
  return { elements, calls };
}
test('setup loads status without interactive consent or meeting launch', async () => {
  const app = setup(); await settle();
  assert.equal(app.calls.some(c => c.options.method === 'POST'), false);
  assert.equal(app.calls.some(c => c.path.endsWith('/connect') || c.path.endsWith('/join')), false);
  const status = app.calls.find(c => c.path.endsWith('/status'));
  assert.equal(status.options.headers['X-Outlook-Session'], 'local-session');
});
test('a failed status check leaves explicit reconnect available without auto consent', async () => {
  const app = setup({ statusError: 'Reconnect Outlook.' }); await settle();
  assert.equal(app.elements.get('#outlook-connect').disabled, false);
  assert.equal(app.elements.get('#outlook-status').textContent, 'Reconnect Outlook.');
  assert.equal(app.calls.some(c => c.options.method === 'POST'), false);
});
test('Connect Outlook sends one bundled connection request only after clicking', async () => {
  const app = setup(); await settle();
  await app.elements.get('#outlook-connect').click();
  const consent = app.calls.filter(c => c.path.endsWith('/connect'));
  assert.equal(consent.length, 1);
  assert.equal(consent[0].options.method, 'POST');
  assert.equal(consent[0].options.headers['X-Outlook-Session'], 'local-session');
});
test('admin consent failure remains visible and does not trigger further prompts', async () => {
  const app = setup({ error: 'Administrator approval is required.' }); await settle();
  await app.elements.get('#outlook-connect').click();
  assert.equal(app.elements.get('#outlook-message').textContent, 'Administrator approval is required.');
  assert.equal(app.calls.filter(c => c.path.endsWith('/connect')).length, 1);
});
test('pair approval requires a confirmation and never uses a Graph write', async () => {
  const app = setup({ approval: false }); await settle();
  const row = app.elements.get('#outlook-pairings').children[0];
  await row.children.find(c => c.textContent === 'Approve').click();
  assert.equal(app.calls.some(c => c.path.endsWith('/approve')), false);
});
test('calendar text is not inserted as HTML', async () => {
  const app = setup(); await settle();
  const row = app.elements.get('#outlook-calendars').children[0];
  assert.ok(row.children.some(c => c.textContent.includes('<script>example</script>')));
  assert.equal(row.innerHTML, undefined);
});
test('partial discovery warnings remain visible while available calendars load', async () => {
  const app = setup({ discoveryErrors: [{ code: 'source_access_denied', message: 'A group calendar is not accessible.' }] }); await settle();
  assert.match(app.elements.get('#outlook-status').textContent, /A group calendar is not accessible/);
  assert.equal(app.elements.get('#outlook-calendars').children.length, 1);
});
