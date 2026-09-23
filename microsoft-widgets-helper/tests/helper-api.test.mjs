import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/MicrosoftWidgets.Helper/wwwroot/helper-api.js', import.meta.url), 'utf8');

test('configuration and first sign-in replacement sessions are adopted before refresh', async () => {
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
    fetch: async (path, options = {}) => {
      requests.push(options.headers.get('X-Microsoft-Widgets-Owner'));
      call++;
      return {
        ok: true,
        headers: new Headers(path === '/configuration'
          ? { 'X-Microsoft-Widgets-Owner-Replacement': 'configured-session' }
          : path === '/auth/sign-in'
            ? { 'X-Microsoft-Widgets-Owner-Replacement': 'signed-in-session' }
            : {}),
        json: async () => ({})
      };
    }
  };
  context.window.fetch = context.fetch;
  vm.runInNewContext(source, context);

  await context.window.helperApi.fetch('/configuration', { method: 'PUT' });
  await context.window.helperApi.fetch('/auth/sign-in', { method: 'POST' });
  await context.window.helperApi.fetch('/auth/status');

  assert.deepEqual(requests, ['old-session', 'configured-session', 'signed-in-session']);
  assert.equal(stored.get('microsoft-widgets-owner-session'), 'signed-in-session');
});
