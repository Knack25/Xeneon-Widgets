import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/MicrosoftWidgets.Helper/wwwroot/updates.js', import.meta.url), 'utf8');
const settle = () => new Promise(resolve => setImmediate(resolve));
function setup(approval = true) {
  const elements = new Map();
  const calls = [];
  const state = { state: 'available', currentVersion: '0.3.1', availableVersion: '0.4.0', canInstall: true, notes: '<script>not executable</script>' };
  const helperApi = {
    fetch: async (path, options) => { calls.push({ path, options }); return { ok: true, json: async () => path.endsWith('/result') ? {} : state }; }
  };
  const context = vm.createContext({
    document: { querySelector(id) {
      if (!elements.has(id)) elements.set(id, { addEventListener(event, action) { this[event] = action; } });
      return elements.get(id);
    } },
    window: { helperApi },
    fetch: () => { throw new Error('setup UI must use helperApi.fetch'); },
    confirm: () => approval, setInterval() {}, location: { reload() {} }, Date
  });
  vm.runInContext(source, context);
  return { elements, calls, state };
}

test('manual check makes a fresh request, while page load never installs', async () => {
  const app = setup();
  await settle();
  assert.equal(app.calls.some(c => c.path === '/updates/install'), false);
  await app.elements.get('#check-updates').click();
  assert.equal(app.calls.at(-1).path, '/updates/check');
  assert.equal(app.calls.at(-1).options.method, 'POST');
  assert.equal(app.elements.get('#update-notes-text').textContent, app.state.notes);
});

test('update requests use the owner helper API instead of the legacy header', async () => {
  const app = setup();
  await settle();
  assert.equal(app.calls.every(call => call.options.headers['X-Microsoft-Widgets-Update'] === undefined), true);
});

test('cancelling approval never sends install; approval includes the offered version', async () => {
  const cancelled = setup(false);
  await settle();
  cancelled.elements.get('#install-update').click();
  assert.equal(cancelled.calls.some(c => c.path === '/updates/install'), false);
  const approved = setup();
  await settle();
  approved.elements.get('#install-update').click();
  await settle();
  const call = approved.calls.find(c => c.path === '/updates/install');
  assert.deepEqual(JSON.parse(call.options.body), { version: '0.4.0' });
});
