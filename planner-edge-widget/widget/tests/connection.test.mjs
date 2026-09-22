import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

test("Planner sends only its scoped credential in the shared header", async () => {
  const calls = [];
  const context = { location: { protocol: 'file:' }, fetch: async (url, options) => {
    calls.push({ url, options }); return { ok: true, status: 204 };
  } };
  runInNewContext(readFileSync(new URL('../src/api.js', import.meta.url), 'utf8'), context);
  context.PlannerApi.credential = 'planner-secret';
  await context.PlannerApi.getDisplay();
  await context.PlannerApi.deleteChecklistItem('task', 'item');
  for (const { url, options } of calls) {
    assert.equal(options?.headers?.['X-Microsoft-Widgets-Credential'], 'planner-secret');
    assert.equal(options.headers.Authorization, undefined);
    assert.equal(url.includes('planner-secret'), false);
  }
});

test("Planner preview uses helperApi and cannot mint a widget credential", async () => {
  const calls = [];
  const context = { location: { protocol: 'http:', hostname: 'localhost', port: '8787' },
    helperApi: { ready: Promise.resolve(true), fetch: async (url, options) => {
      calls.push({ url, options }); return { ok: true, status: 204 };
    } }, fetch: async () => { throw new Error('Preview bypassed owner API'); } };
  runInNewContext(readFileSync(new URL('../src/api.js', import.meta.url), 'utf8'), context);
  await context.PlannerApi.getDisplay();
  assert.equal(calls[0].url, '/display');
  assert.equal(calls[0].options?.headers?.['X-Microsoft-Widgets-Credential'], undefined);
  await assert.rejects(() => context.PlannerApi.pair('preview', 'secret'));
});

test("notes and chat API wrappers send the exact helper contracts", async () => {
  const calls = [];
  const context = { URLSearchParams, fetch: async (path, options = {}) => {
    calls.push([path, options]);
    return { ok: true, status: path.includes("/chat") && options.method !== "POST" ? 200 : 204,
      json: async () => ({ state: "available", messages: [] }) };
  } };
  runInNewContext(readFileSync(new URL("../src/api.js", import.meta.url), "utf8"), context);

  await context.PlannerApi.updateNotes("task", "Line one\nLine two");
  await context.PlannerApi.getTaskChat("task", "opaque cursor");
  await context.PlannerApi.postTaskChat("task", "Status update");

  assert.equal(calls[0][0], "http://localhost:8787/tasks/task/notes");
  assert.equal(calls[0][1].method, "PUT");
  assert.equal(calls[0][1].headers["Content-Type"], "application/json");
  assert.equal(calls[0][1].body, JSON.stringify({ description: "Line one\nLine two" }));
  assert.equal(calls[1][0], "http://localhost:8787/tasks/task/chat?cursor=opaque+cursor");
  assert.equal(calls[1][1].method, undefined);
  assert.equal(calls[2][0], "http://localhost:8787/tasks/task/chat");
  assert.equal(calls[2][1].method, "POST");
  assert.equal(calls[2][1].headers["Content-Type"], "application/json");
  assert.equal(calls[2][1].body, JSON.stringify({ message: "Status update" }));
});

test("connection test reports CORS and opaque requests separately", async () => {
  const elements = Object.fromEntries(["cors-result", "opaque-result", "retry"].map(id => [id, { textContent: "", disabled: false, addEventListener: () => {} }]));
  const modes = [];
  const context = {
    document: { getElementById: id => elements[id] },
    fetch: async (_url, options) => {
      modes.push(options.mode);
      if (options.mode === "cors") throw new TypeError("Failed to fetch");
      return { type: "opaque", status: 0 };
    },
    AbortController: class { signal = {}; abort() {} },
    setTimeout: () => 1,
    clearTimeout: () => {},
  };
  runInNewContext(readFileSync(new URL("../../connection-test/app.js", import.meta.url), "utf8"), context);
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(modes, ["cors", "no-cors"]);
  assert.match(elements["cors-result"].textContent, /Failed to fetch/);
  assert.match(elements["opaque-result"].textContent, /Reached helper/);
});
