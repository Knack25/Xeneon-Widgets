import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const settle = () => new Promise(resolve => setImmediate(resolve));

function board(planTitle, planId = "plan") {
  return { planId, planTitle, syncedAt: "2026-09-21T12:00:00Z", isStale: false, buckets: [], labels: [] };
}

function response(body, status = 200) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

function startWidget(fetch, localStorage = { getItem: () => null, setItem() {} }) {
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', addEventListener() {} };
  const context = { document: { getElementById: () => app }, fetch, setInterval: () => {}, localStorage, Intl, Date };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context, { filename: file });
  return { app, context };
}

test("widget scripts start together and show the selected board", async () => {
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', addEventListener() {} };
  const context = {
    document: { getElementById: () => app },
    fetch: async path => path.endsWith("/display/cached") ? response(null, 204) : response(path.endsWith("/display")
      ? ({ planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [], labels: [] })
      : ({ myTasks: false, filters: {} })),
    setInterval: () => {},
    localStorage: { getItem: () => null, setItem() {} },
    Intl,
    Date,
  };
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

test("signed-out live refresh also retains the cached board", async () => {
  const { app } = startWidget(async path => {
    if (path.endsWith("/display/cached")) return response(board("Cached Board"));
    if (path.endsWith("/display")) throw { code: "signed_out", message: "Sign in again." };
    if (path.includes("view-preferences")) return response({ myTasks: false, filters: {} });
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  await settle();
  assert.match(app.innerHTML, /Cached Board/);
  assert.match(app.innerHTML, /Offline view/);
  assert.match(app.innerHTML, /Sign in again\./);
});
