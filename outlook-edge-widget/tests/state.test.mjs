import test from 'node:test';
import assert from 'node:assert/strict';
import { RefreshState } from '../widget/src/state.js';

const response = reference => ({
  events: [{ reference, calendarKey: 'calendar', title: reference }],
  sources: []
});

test('authorization failure invalidates delayed cache and live responses', () => {
  for (const status of [401, 403]) {
    const state = new RefreshState();
    const ticket = state.begin('range');

    assert.deepEqual(state.fail(ticket, { status }), []);
    assert.equal(state.seed(ticket, response('late-cache')), false);
    assert.equal(state.accept(ticket, response('late-live')), false);
    assert.deepEqual(state.events, []);
    assert.deepEqual(state.sources, []);
    assert.equal(state.cache.size, 0);
  }
});

test('embedded account change invalidates every request ticket', () => {
  const state = new RefreshState();
  const ticket = state.begin('range');

  assert.throws(() => state.accept(ticket, {
    events: response('unauthorized').events,
    sources: [{ calendarKey: 'calendar', error: { code: 'account_changed', message: 'Changed' } }]
  }), error => error.status === 401);

  assert.equal(state.seed(ticket, response('late-cache')), false);
  assert.equal(state.accept(ticket, response('late-live')), false);
  assert.deepEqual(state.events, []);
  assert.equal(state.authorized, false);
  assert.equal(state.offline, false);
});

test('successful authorization explicitly restores usable state', () => {
  const state = new RefreshState();
  state.clearAuthorization();

  assert.equal(state.authorized, false);
  state.authorize();

  assert.equal(state.authorized, true);
});

test('late cached authorization loss wins over resolved and superseded tickets', () => {
  for(const superseded of [false,true]) {
    const state=new RefreshState(), old=state.begin('range');
    state.accept(old,response('live'));
    if(superseded)state.begin('new-range');

    assert.throws(()=>state.seed(old,{
      events:[],sources:[{calendarKey:'calendar',error:{code:'sign_in_required',message:'Sign in'}}]
    }),error=>error.status===401);
    assert.equal(state.authorized,false);
    assert.deepEqual(state.events,[]);
  }
});

test('clearAuthorization aborts active work before invalidating tickets', () => {
  const state = new RefreshState();
  const ticket = state.begin('range');
  const first = new AbortController();
  const second = new AbortController();
  state.track(first);
  state.track(second);

  state.clearAuthorization();

  assert.equal(first.signal.aborted, true);
  assert.equal(second.signal.aborted, true);
  assert.equal(state.accept(ticket, response('late-live')), false);
  assert.deepEqual(state.events, []);
});
