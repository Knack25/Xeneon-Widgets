import test from 'node:test';
import assert from 'node:assert/strict';
import { OutlookApi } from '../widget/src/api.js';
import { settingsStore } from '../widget/src/settings.js';

test('native Outlook uses the shared credential and scoped pairing contracts', async () => {
  const calls = [];
  const api = new OutlookApi({ native: true, credential: 'outlook-secret', fetch: async (url, options) => {
    calls.push({ url, options }); return new Response('{}');
  } });
  await api.get('calendars');
  await api.pair('instance', 'request-secret');
  await api.poll('pair-id', 'request-secret');
  assert.equal(calls[0].options.headers['X-Microsoft-Widgets-Credential'], 'outlook-secret');
  assert.equal(calls[0].options.headers.Authorization, undefined);
  assert.equal(calls[1].url, 'http://localhost:8787/api/local-access/pairings');
  assert.deepEqual(JSON.parse(calls[1].options.body), { scope: 'outlook', instanceId: 'instance', requestSecret: 'request-secret' });
  assert.equal(calls[2].url, 'http://localhost:8787/api/local-access/pairings/pair-id/poll');
  assert.equal(calls[1].options.headers['X-Microsoft-Widgets-Credential'], undefined);
  for (const call of calls) assert.doesNotMatch(call.url, /outlook-secret|request-secret/);
});

test('Outlook preview uses only the owner helperApi and refuses pairing', async () => {
  const calls = [];
  const api = new OutlookApi({ credential: 'ignored', helperApi: { ready: Promise.resolve(true), fetch: async (url, options) => {
    calls.push({ url, options }); return new Response('[]');
  } }, fetch: async () => { throw new Error('Owner API bypassed'); } });
  await api.initialize(); await api.get('calendars');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, '/api/outlook/calendars');
  assert.equal(calls[0].options.headers['X-Outlook-Session'], undefined);
  assert.equal(calls[0].options.headers['X-Microsoft-Widgets-Credential'], undefined);
  await assert.rejects(() => api.pair('preview', 'secret'));
});

test('credentials remain isolated by instance and widget namespace', () => {
  const values = new Map([['one', JSON.stringify({ planner: { credential: 'planner-secret' } })]]);
  const storage = { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value) };
  settingsStore(storage, 'one').save({ credential: 'outlook-secret' });
  assert.equal(settingsStore(storage, 'one').load().credential, 'outlook-secret');
  assert.equal(settingsStore(storage, 'two').load().credential, '');
  assert.equal(JSON.parse(values.get('one')).planner.credential, 'planner-secret');
});
