import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { webcrypto } from "node:crypto";

const settle = () => new Promise(resolve => setImmediate(resolve));

function board(planTitle, planId = "plan") {
  return { planId, planTitle, syncedAt: "2026-09-21T12:00:00Z", isStale: false, buckets: [], labels: [] };
}

function response(body, status = 200) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

function startWidget(fetch, localStorage = { getItem: () => null, setItem() {} }, overrides = {}) {
  const handlers = {};
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', addEventListener(type, handler) { handlers[type] = handler; } };
  const context = { document: { getElementById: () => app }, fetch, setInterval: () => {}, localStorage, Intl, Date,
    location: { protocol: 'http:', hostname: 'localhost', port: '8787' }, helperApi: { ready: Promise.resolve(true), fetch }, ...overrides };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context, { filename: file });
  return { app, context, tapPair: () => handlers.click({ target: { closest: selector => selector === '[data-pair-widget]' ? {} : null, matches: () => false } }) };
}

test('native Planner waits for its instance and stores only the Planner credential', async () => {
  const values = new Map([['native-one', JSON.stringify({ outlook: { credential: 'outlook-secret' } })]]);
  const timers = [], calls = [], logs = [];
  const storage = { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value) };
  const widget = startWidget(async (url, options = {}) => {
    calls.push({ url, options });
    if (url.endsWith('/pairings')) return response({ id: 'pair-id', code: '123456', expiresAt: new Date(Date.now() + 60000).toISOString() });
    if (url.endsWith('/poll')) return response({ status: 'approved', credential: 'planner-secret' });
    if (url.endsWith('/display')) return response(board('Paired Board'));
    return response(null, 204);
  }, storage, { location: { protocol: 'file:' }, helperApi: undefined, crypto: webcrypto,
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {}, console: { log: value => logs.push(value) } });
  await settle();
  assert.equal(calls.length, 0);
  widget.context.uniqueId = 'native-one';
  await timers.shift()(); await settle();
  assert.match(widget.app.innerHTML, /Pair.*Planner|Pair widget/);
  await widget.tapPair(); await settle();
  assert.match(widget.app.innerHTML, /123456/);
  assert.deepEqual(JSON.parse(calls[0].options.body).scope, 'planner');
  await timers.shift()(); await settle();
  const saved = JSON.parse(values.get('native-one'));
  assert.equal(saved.planner.credential, 'planner-secret');
  assert.equal(saved.outlook.credential, 'outlook-secret');
  assert.equal(calls.find(c => c.url.endsWith('/display')).options.headers['X-Microsoft-Widgets-Credential'], 'planner-secret');
  assert.doesNotMatch(widget.app.innerHTML + JSON.stringify(logs) + calls.map(c => c.url).join(), /planner-secret|outlook-secret/);
  assert.equal(values.has('native-two'), false);
});

test('Planner pairing expires and retries without exposing returned errors or secrets', async () => {
  const timers = [], calls = [];
  let attempt = 0;
  const widget = startWidget(async (url, options) => {
    calls.push({ url, options });
    if (++attempt === 1) return response({ id: 'old', code: '123456', expiresAt: '2000-01-01T00:00:00Z' });
    return response({ error: { message: 'do-not-render-secret' } }, 503);
  }, undefined, { location: { protocol: 'file:' }, uniqueId: 'native-two', helperApi: undefined, crypto: webcrypto,
    setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout() {} });
  await settle(); await widget.tapPair(); await timers.shift()();
  assert.match(widget.app.innerHTML, /expired/i);
  assert.equal(calls.length, 1);
  await widget.tapPair();
  assert.match(widget.app.innerHTML, /Retry pairing/);
  assert.doesNotMatch(widget.app.innerHTML, /do-not-render-secret/);
});

test('Planner preview never reads or writes durable credentials', async () => {
  let reads = 0, writes = 0;
  const widget = startWidget(async () => response(null, 204), { getItem() { reads++; return null; }, setItem() { writes++; } });
  await settle();
  assert.equal(reads, 0); assert.equal(writes, 0);
  assert.doesNotMatch(widget.app.innerHTML, /Pair widget/);
});

test('native Planner offers re-pairing when CORS hides a revoked response', async () => {
  const widget = startWidget(async () => { throw new TypeError('Failed to fetch'); },
    { getItem: () => JSON.stringify({ planner: { credential: 'revoked' } }), setItem() {} },
    { location: { protocol: 'file:' }, uniqueId: 'native', helperApi: undefined });
  await settle();
  assert.match(widget.app.innerHTML, /Pair again/);
});

test("widget scripts start together and show the selected board", async () => {
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', addEventListener() {} };
  const context = {
    location: { protocol: 'http:', hostname: 'localhost', port: '8787' },
    document: { getElementById: () => app },
    fetch: async path => path.endsWith("/display/cached") ? response(null, 204) : response(path.endsWith("/display")
      ? ({ planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [], labels: [] })
      : ({ myTasks: false, filters: {} })),
    setInterval: () => {},
    localStorage: { getItem: () => null, setItem() {} },
    Intl,
    Date,
  };
  context.helperApi = { ready: Promise.resolve(true), fetch: context.fetch };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"]) {
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context, { filename: file });
  }
  await new Promise(resolve => setImmediate(resolve));
  assert.match(app.innerHTML, /Work/);
  assert.doesNotMatch(app.innerHTML, /Loading Planner/);
});

test("helper-hosted view requests its own origin", async () => {
  const paths = [];
  const context = {
    location: { protocol: "http:", hostname: "localhost", port: "8787" },
    fetch: async path => { paths.push(path); return { ok: true, status: 204 }; },
  };
  context.helperApi = { ready: Promise.resolve(true), fetch: context.fetch };
  runInNewContext(readFileSync(new URL("../src/api.js", import.meta.url), "utf8"), context);
  await context.PlannerApi.getCachedDisplay();
  await context.PlannerApi.getDisplay();
  assert.deepEqual(paths, ["/display/cached", "/display"]);
});

test("startup renders cache before the unresolved live request", async () => {
  let resolveLive;
  const liveResponse = new Promise(resolve => { resolveLive = resolve; });
  const { app } = startWidget(async path => {
    if (path.endsWith("/display/cached")) return response(board("Cached Board"));
    if (path.endsWith("/display")) return liveResponse;
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  assert.match(app.innerHTML, /Cached Board/);
  assert.doesNotMatch(app.innerHTML, /Live Board/);

  resolveLive(response(board("Live Board")));
  await settle();
  await settle();
  assert.match(app.innerHTML, /Live Board/);
  assert.doesNotMatch(app.innerHTML, /Cached Board/);
});

test("live board preserves saved filters that are absent from the cached board", async () => {
  let resolveLive;
  let resolvePreferences;
  const writes = [];
  const liveResponse = new Promise(resolve => { resolveLive = resolve; });
  const preferencesResponse = new Promise(resolve => { resolvePreferences = resolve; });
  const cached = { ...board("Cached Board"), buckets: [], labels: [] };
  const live = { ...board("Live Board"),
    labels: [{ labelId: "release", name: "Release" }],
    buckets: [
      { bucketId: "selected", name: "Selected", tasks: [
        { taskId: "matching", title: "Matching task", labelIds: ["release"] }
      ] },
      { bucketId: "other", name: "Other", tasks: [
        { taskId: "other", title: "Other task", labelIds: [] }
      ] }
    ]
  };
  const saved = { myTasks: false, filters: { assigneeIds: [], labelIds: ["release"], priorities: [],
    bucketIds: ["selected"], progressValues: [], dueDateRange: null } };
  const { app } = startWidget(async (path, options = {}) => {
    if (path.endsWith("/display/cached")) return response(cached);
    if (path.endsWith("/display")) return liveResponse;
    if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
      writes.push(JSON.parse(options.body));
      return response(JSON.parse(options.body));
    }
    if (path.endsWith("/view-preferences/plan")) return preferencesResponse;
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  assert.match(app.innerHTML, /Cached Board/);
  resolveLive(response(live));
  await settle();
  resolvePreferences(response(saved));
  await settle();
  await settle();

  assert.match(app.innerHTML, /Matching task/);
  assert.doesNotMatch(app.innerHTML, /Other task/);
  assert.deepEqual(writes, []);
});

test("live board repairs saved filters removed since the cached board exactly once", async () => {
  let resolveLive;
  let resolvePreferences;
  const writes = [];
  const liveResponse = new Promise(resolve => { resolveLive = resolve; });
  const preferencesResponse = new Promise(resolve => { resolvePreferences = resolve; });
  const cached = { ...board("Cached Board"),
    labels: [{ labelId: "removed-label", name: "Removed" }],
    buckets: [{ bucketId: "removed-bucket", name: "Removed", tasks: [] }]
  };
  const live = { ...board("Live Board"),
    labels: [{ labelId: "current-label", name: "Current" }],
    buckets: [{ bucketId: "current-bucket", name: "Current", tasks: [
      { taskId: "current", title: "Current task", labelIds: ["current-label"] }
    ] }]
  };
  const saved = { myTasks: false, filters: { assigneeIds: [], labelIds: ["removed-label"], priorities: [],
    bucketIds: ["removed-bucket"], progressValues: [], dueDateRange: null } };
  const { app } = startWidget(async (path, options = {}) => {
    if (path.endsWith("/display/cached")) return response(cached);
    if (path.endsWith("/display")) return liveResponse;
    if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
      writes.push(JSON.parse(options.body));
      return response(JSON.parse(options.body));
    }
    if (path.endsWith("/view-preferences/plan")) return preferencesResponse;
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  assert.match(app.innerHTML, /Cached Board/);
  resolveLive(response(live));
  await settle();
  resolvePreferences(response(saved));
  await settle();
  await settle();

  assert.match(app.innerHTML, /Current task/);
  assert.equal(writes.length, 1);
  assert.deepEqual(writes[0].filters.labelIds, []);
  assert.deepEqual(writes[0].filters.bucketIds, []);
});

test("startup continues with live data when no cache exists", async () => {
  const paths = [];
  const { app } = startWidget(async path => {
    paths.push(path);
    if (path.endsWith("/display/cached")) return response(null, 204);
    if (path.endsWith("/display")) return response(board("Live Only"));
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  await settle();
  assert.match(app.innerHTML, /Live Only/);
  assert.ok(paths.some(path => path.endsWith("/display/cached")));
  assert.ok(paths.some(path => path.endsWith("/display")));
});

test("failed live refresh retains the cached board as an offline view", async () => {
  const { app } = startWidget(async path => {
    if (path.endsWith("/display/cached")) return response(board("Cached Board"));
    if (path.endsWith("/display")) throw { code: "network_unavailable", message: "No network." };
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  await settle();
  assert.match(app.innerHTML, /Cached Board/);
  assert.match(app.innerHTML, /Offline view/);
  assert.match(app.innerHTML, /No network\./);
});

test("signed-out live refresh clears cached work data", async () => {
  const { app } = startWidget(async path => {
    if (path.endsWith("/display/cached")) return response(board("Cached Board"));
    if (path.endsWith("/display")) throw { code: "signed_out", message: "Sign in again." };
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  await settle();
  assert.doesNotMatch(app.innerHTML, /Cached Board/);
  assert.match(app.innerHTML, /Sign in|Pair again|restore preview/i);
  assert.match(app.innerHTML, /Sign in again\./);
});

test("late cached and live boards cannot repopulate after authorization loss", async () => {
  let resolveCached, resolveLive;
  const cached = new Promise(resolve => { resolveCached = resolve; });
  const live = new Promise(resolve => { resolveLive = resolve; });
  const { app, context } = startWidget(async path => {
    if (path.endsWith("/display/cached")) return cached;
    if (path.endsWith("/display")) return live;
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  context.PlannerApi.onUnauthorized();
  resolveCached(response(board("Leaked Cache")));
  resolveLive(response(board("Leaked Live")));
  await settle(); await settle();

  assert.doesNotMatch(app.innerHTML, /Leaked Cache|Leaked Live/);
});

test("authorization loss aborts and rejects late details chat and action responses", async () => {
  const releases = [];
  const signals = [];
  const context = {
    location: { protocol: "http:", hostname: "localhost", port: "8787" },
    fetch: (_path, options) => {
      signals.push(options.signal);
      return new Promise(resolve => releases.push(() => resolve(response({ state: "available", messages: [] }))));
    }
  };
  context.helperApi = { ready: Promise.resolve(true), fetch: context.fetch };
  runInNewContext(readFileSync(new URL("../src/state.js", import.meta.url), "utf8"), context);
  runInNewContext(readFileSync(new URL("../src/api.js", import.meta.url), "utf8"), context);
  const lifecycle = context.PlannerState.createAuthorizationLifecycle();
  context.PlannerApi.authorizationLifecycle = lifecycle;

  const pending = [context.PlannerApi.getTaskDetails("task"), context.PlannerApi.getTaskChat("task"),
    context.PlannerApi.postTaskChat("task", "comment")];
  lifecycle.clearAuthorization();
  releases.forEach(release => release());
  const results = await Promise.allSettled(pending);

  assert.equal(signals.every(signal => signal.aborted), true);
  assert.equal(results.every(result => result.status === "rejected" && result.reason.name === "AbortError"), true);
});

test("forbidden Planner response clears authorization before content can be read", async () => {
  const context = {
    location: { protocol: "http:", hostname: "localhost", port: "8787" },
    fetch: async () => response({ error: { code: "permission_denied", message: "Denied" } }, 403)
  };
  context.helperApi = { ready: Promise.resolve(true), fetch: context.fetch };
  runInNewContext(readFileSync(new URL("../src/api.js", import.meta.url), "utf8"), context);
  let cleared = 0;
  context.PlannerApi.onUnauthorized = () => { cleared++; };

  await assert.rejects(() => context.PlannerApi.getDisplay());

  assert.equal(cleared, 1);
});
