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
