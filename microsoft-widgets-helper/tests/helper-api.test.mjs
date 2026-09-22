import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/MicrosoftWidgets.Helper/wwwroot/helper-api.js', import.meta.url), 'utf8');

test('configuration response replacement session is adopted before the next request', async () => {
  const stored = new Map([['microsoft-widgets-owner-session', 'old-session']]);
  const requests = [];
  let call = 0;
  const context = {
    window: {},
    location: { hash: '', pathname: '/', search: '' },
    history: { replaceState() {} },
    URLSearchParams,
    Headers,
    Error,
    sessionStorage: {
      getItem: key => stored.get(key) ?? null,
      setItem: (key, value) => stored.set(key, value)
    },
    fetch: async (_path, options = {}) => {
      requests.push(options.headers.get('X-Microsoft-Widgets-Owner'));
      call++;
      return {
        ok: true,
        headers: new Headers(call === 1
          ? { 'X-Microsoft-Widgets-Owner-Replacement': 'new-session' }
          : {}),
        json: async () => ({})
      };
    }
  };
  context.window.fetch = context.fetch;
  vm.runInNewContext(source, context);

  await context.window.helperApi.fetch('/configuration', { method: 'PUT' });
  await context.window.helperApi.fetch('/auth/status');

  assert.deepEqual(requests, ['old-session', 'new-session']);
  assert.equal(stored.get('microsoft-widgets-owner-session'), 'new-session');
});
