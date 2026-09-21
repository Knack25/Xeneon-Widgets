import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const settle = (milliseconds = 15) => new Promise(resolve => setTimeout(resolve, milliseconds));

function createDetailFixture(fetch, appOverrides = {}) {
  const handlers = {};
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  Object.defineProperties(app, Object.getOwnPropertyDescriptors(appOverrides));
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date, URLSearchParams,
    IntersectionObserver: class { observe() {} disconnect() {} }, fetch,
    localStorage: { getItem: () => null, setItem() {} } };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  const tap = (attribute, dataset = {}) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null,
    matches: selector => selector === `[${attribute}]`
  } });
  const input = (attribute, value) => handlers.input({ target: {
    matches: selector => selector === `[${attribute}]`, value
  } });
  const change = (attribute, value) => handlers.change({ target: {
    matches: selector => selector === `[${attribute}]`, value
  } });
  return { app, handlers, tap, input, change };
}

test("task card has separate tap targets and previews only three checklist items", async () => {
  const app = { innerHTML: "", addEventListener() {} };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build", dueDateTime: null }] }
  ] };
  const details = { taskId: "task", title: "Build", checklist: [1, 2, 3, 4].map(i =>
    ({ itemId: `i${i}`, title: `Item ${i}`, isChecked: false })), assignees: [] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board : details }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 20));

  assert.match(app.innerHTML, /data-open-board-picker/);
  assert.match(app.innerHTML, /data-complete-task="task"/);
  assert.match(app.innerHTML, /data-open-task="task"/);
  assert.equal((app.innerHTML.match(/<span class="checklist-preview /g) || []).length, 3);
  assert.doesNotMatch(app.innerHTML, /data-checklist-item=/);
  assert.match(app.innerHTML, /\+1 more/);
});

test("API uses focused helper endpoints for selection and checklist completion", async () => {
  const calls = [];
  const context = { fetch: async (path, options) => {
    calls.push([path, options?.method]);
    return { ok: true, status: 204, json: async () => null };
  } };
  runInNewContext(readFileSync(new URL("../src/api.js", import.meta.url), "utf8"), context);
  await context.PlannerApi.selectPlan("plan");
  await context.PlannerApi.completeChecklistItem("task", "item");
  await context.PlannerApi.moveTask("task", "target");
  assert.deepEqual(calls.map(([path, method]) => [path.endsWith("/selected-plan"), method]), [[true, "PUT"], [false, "POST"], [false, "PUT"]]);
  assert.match(calls[1][0], /tasks\/task\/checklist\/item\/complete$/);
  assert.match(calls[2][0], /tasks\/task\/bucket$/);
  assert.equal(calls[2][1], "PUT");
});

test("task details use a tap-friendly bucket picker and move only after selection", async () => {
  const handlers = {};
  const calls = [];
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const task = { taskId: "task", title: "Build", bucketId: "current" };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "current", name: "Current", tasks: [task] },
    { bucketId: "target", name: "Target", tasks: [] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "current", checklist: [], assignees: [] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async (path, options) => {
      calls.push([path, options?.method]);
      return { ok: true, status: options?.method === "PUT" ? 204 : 200,
        json: async () => path.endsWith("/display") ? board : detail };
    } };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  await handlers.click({ target: { closest: selector => selector === "[data-open-task]" ? { dataset: { openTask: "task" } } : null } });
  assert.match(app.innerHTML, /data-open-bucket-picker/);
  assert.doesNotMatch(app.innerHTML, /<select[^>]*data-task-bucket/);
  assert.doesNotMatch(app.innerHTML, /data-move-task/);

  await handlers.click({ target: { closest: selector => selector === "[data-open-bucket-picker]" ? { dataset: {} } : null } });
  assert.match(app.innerHTML, /data-select-task-bucket="target"/);
  await handlers.click({ target: { closest: selector => selector === "[data-select-task-bucket]" ? { dataset: { selectTaskBucket: "target" } } : null } });
  assert.match(app.innerHTML, /data-move-task/);
  await handlers.click({ target: { closest: selector => selector === "[data-move-task]" ? { dataset: {} } : null } });
  assert.ok(calls.some(([path, method]) => path.endsWith("/tasks/task/bucket") && method === "PUT"));
});

test("task body and checklist taps do not open task completion", async () => {
  const handlers = {};
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [{ itemId: "item", title: "Check", isChecked: false }], assignees: [] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board : detail }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 20));

  const tap = async (attribute, dataset) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null
  } });
  await tap("data-open-task", { openTask: "task" });
  assert.match(app.innerHTML, /No due date/);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
  assert.match(app.innerHTML, /data-checklist-item="item"/);
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "item" });
  assert.match(app.innerHTML, /Complete checklist item\?/);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
});

test("outside tap closes the board picker", async () => {
  const handlers = {};
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async () => ({ ok: true, status: 200, json: async () => board }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 10));
  const tap = async attribute => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset: {} } : null,
    matches: selector => selector === `[${attribute}]`
  } });
  await tap("data-open-board-picker");
  assert.match(app.innerHTML, /Choose board/);
  await tap("data-dialog-backdrop");
  assert.doesNotMatch(app.innerHTML, /Choose board/);
});

test("scheduled board refresh reloads checklist previews", async () => {
  let refresh;
  let detailReads = 0;
  const app = { innerHTML: "", addEventListener() {} };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "bucket", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const context = { document: { getElementById: () => app }, setInterval: callback => { refresh = callback; }, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => {
      if (path.endsWith("/display")) return board;
      detailReads++;
      return { taskId: "task", checklist: [], assignees: [] };
    } }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(detailReads, 1);
  await refresh();
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(detailReads, 2);
});

test("task detail updates preserve board and bucket scroll positions", async () => {
  let resolveDetails;
  let markup = "";
  let boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
  let bucketElement = { dataset: { bucketId: "bucket" }, scrollTop: 0 };
  const app = {
    addEventListener() {},
    get innerHTML() { return markup; },
    set innerHTML(value) {
      markup = value;
      boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
      bucketElement = { dataset: { bucketId: "bucket" }, scrollTop: 0 };
    },
    querySelector: selector => selector === ".board" ? boardElement : null,
    querySelectorAll: selector => selector === ".bucket" ? [bucketElement] : []
  };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "bucket", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board :
      new Promise(resolve => { resolveDetails = resolve; }) }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 10));
  boardElement.scrollLeft = 420;
  bucketElement.scrollTop = 180;
  resolveDetails({ taskId: "task", title: "Build", checklist: [], assignees: [] });
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(boardElement.scrollLeft, 420);
  assert.equal(bucketElement.scrollTop, 180);
});

test("checklist loading updates its card without rebuilding the scrolling board", async () => {
  let resolveDetails;
  let boardRenders = 0;
  let markup = "";
  const supplement = { innerHTML: "" };
  const card = { dataset: { openTask: "task" }, querySelector: () => supplement };
  const app = {
    addEventListener() {},
    get innerHTML() { return markup; },
    set innerHTML(value) { markup = value; boardRenders++; },
    querySelectorAll: selector => selector === ".task" ? [card] : []
  };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "bucket", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board :
      new Promise(resolve => { resolveDetails = resolve; }) }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(boardRenders, 1);
  resolveDetails({ taskId: "task", title: "Build", checklist: [{ itemId: "one", title: "First", isChecked: false }], assignees: [] });
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(boardRenders, 1);
  assert.match(supplement.innerHTML, /First/);
});

test("only visible task cards prefetch details when observation is available", async () => {
  let observer;
  const reads = [];
  const app = { innerHTML: "", addEventListener() {}, querySelectorAll: () => [
    { dataset: { openTask: "visible" } }, { dataset: { openTask: "hidden" } }
  ] };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "bucket", name: "Doing", tasks: [
      { taskId: "visible", title: "Visible" }, { taskId: "hidden", title: "Hidden" }
    ] }
  ] };
  class Observer {
    constructor(callback) { this.callback = callback; observer = this; }
    observe() {}
    disconnect() {}
  }
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    IntersectionObserver: Observer,
    fetch: async path => ({ ok: true, status: 200, json: async () => {
      if (path.endsWith("/display")) return board;
      reads.push(path);
      return { checklist: [], assignees: [] };
    } }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(reads.length, 0);
  observer.callback([{ target: { dataset: { openTask: "visible" } }, isIntersecting: true }]);
  await new Promise(resolve => setTimeout(resolve, 10));
  assert.equal(reads.length, 1);
  assert.match(reads[0], /visible/);
});

test("My tasks filters assignments on the selected board", async () => {
  const handlers = {};
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [
      { taskId: "mine", title: "My work", assignments: ["me"] },
      { taskId: "other", title: "Other work", assignments: ["other"] }
    ] }
  ] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board :
      path.endsWith("/auth/me") ? { userId: "me" } : { checklist: [] } }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  await handlers.click({ target: { closest: selector => selector === "[data-toggle-my-tasks]" ? { dataset: {} } : null } });
  assert.match(app.innerHTML, /My work/);
  assert.doesNotMatch(app.innerHTML, /Other work/);
  assert.match(app.innerHTML, /aria-pressed="true"/);
});

test("new task form submits once and keeps the selected board", async () => {
  const handlers = {};
  const calls = [];
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "first", name: "First", tasks: [] }
  ] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async (path, options) => {
      calls.push([path, options]);
      return { ok: true, status: options?.method === "POST" ? 204 : 200,
        json: async () => path.endsWith("/display") ? board : null };
    } };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 10));
  const tap = async attribute => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset: {} } : null
  } });
  await tap("data-new-task");
  assert.match(app.innerHTML, /New task/);
  assert.match(app.innerHTML, /First/);
  handlers.input({ target: { matches: selector => selector === "[data-new-task-title]", value: "Test task" } });
  await tap("data-submit-new-task");
  const create = calls.find(([path, options]) => path.endsWith("/tasks") && options?.method === "POST");
  assert.ok(create);
  assert.equal(JSON.parse(create[1].body).title, "Test task");
  assert.equal(JSON.parse(create[1].body).bucketId, "first");
  assert.match(app.innerHTML, /Work/);
});

test("details escape notes and calendar saves a chosen due date", async () => {
  const handlers = {};
  const calls = [];
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [], assignees: [],
    description: "Line one\n<script>alert(1)</script>" };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async (path, options) => {
      calls.push([path, options]);
      return { ok: true, status: options?.method === "PUT" ? 204 : 200,
        json: async () => path.endsWith("/display") ? board : detail };
    } };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  const tap = async (attribute, dataset = {}) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null
  } });
  await tap("data-open-task", { openTask: "task" });
  assert.match(app.innerHTML, /Line one/);
  assert.match(app.innerHTML, /&lt;script&gt;/);
  assert.doesNotMatch(app.innerHTML, /<script>alert/);
  await tap("data-open-date-picker");
  assert.match(app.innerHTML, /data-select-date=/);
  await tap("data-select-date", { selectDate: "2026-09-19" });
  await tap("data-save-date");
  const update = calls.find(([path, options]) => path.endsWith("/tasks/task/due-date") && options?.method === "PUT");
  assert.equal(JSON.parse(update[1].body).date, "2026-09-19");
});

test("assignee picker saves multiple selected board members", async () => {
  const handlers = {};
  const calls = [];
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [],
    assignees: ["Alex"], assigneeIds: ["person-a"] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async (path, options) => {
      calls.push([path, options]);
      return { ok: true, status: options?.method === "PUT" ? 204 : 200,
        json: async () => path.endsWith("/display") ? board : path.endsWith("/members") ?
          [{ id: "person-a", displayName: "Alex" }, { id: "person-b", displayName: "Blair" }] : detail };
    } };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  const tap = async (attribute, dataset = {}) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null
  } });
  await tap("data-open-task", { openTask: "task" });
  await tap("data-open-members");
  assert.match(app.innerHTML, /Blair/);
  await tap("data-toggle-member", { toggleMember: "person-b" });
  await tap("data-save-members");
  const update = calls.find(([path, options]) => path.endsWith("/tasks/task/assignments") && options?.method === "PUT");
  assert.deepEqual(JSON.parse(update[1].body).userIds, ["person-a", "person-b"]);
});

test("notes save explicitly, support clearing, disable while pending, and report success", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [], assignees: [], description: "Original" };
  const updates = [];
  let releaseUpdate;
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (path.includes("/chat")) return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
    updates.push(JSON.parse(options.body).description);
    return new Promise(resolve => { releaseUpdate = () => resolve({ ok: true, status: 204, json: async () => null }); });
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  assert.match(app.innerHTML, /data-notes-draft/);

  input("data-notes-draft", "Line one\nLine two");
  const saving = tap("data-save-notes");
  assert.match(app.innerHTML, /data-save-notes[^>]*disabled/);
  assert.deepEqual(updates, ["Line one\nLine two"]);
  releaseUpdate();
  await saving;
  assert.match(app.innerHTML, /Notes saved/);

  input("data-notes-draft", "");
  const clearing = tap("data-save-notes");
  assert.deepEqual(updates, ["Line one\nLine two", ""]);
  releaseUpdate();
  await clearing;
  assert.match(app.innerHTML, /Notes saved/);
});

test("notes edits made while saving remain unsaved and do not replace the submitted cache value", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [], assignees: [], description: "Original" };
  let releaseUpdate;
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (path.includes("/chat")) return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
    assert.equal(JSON.parse(options.body).description, "Submitted notes");
    return new Promise(resolve => { releaseUpdate = () => resolve({ ok: true, status: 204, json: async () => null }); });
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-notes-draft", "Submitted notes");
  const saving = tap("data-save-notes");
  input("data-notes-draft", "Next unsaved notes");
  releaseUpdate();
  await saving;
  assert.match(app.innerHTML, /Next unsaved notes/);
  assert.match(app.innerHTML, /Save current changes/);

  await tap("data-close-dialog");
  await tap("data-open-task", { openTask: "task" });
  await settle();
  assert.match(app.innerHTML, /Submitted notes/);
  assert.doesNotMatch(app.innerHTML, /Next unsaved notes/);
});

test("failed notes saves retain an escaped draft", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [], assignees: [], description: "" };
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (path.includes("/chat")) return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
    return { ok: false, status: 503, json: async () => ({ message: "Save failed" }) };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-notes-draft", '<script>alert("notes")</script>');
  await tap("data-save-notes");
  assert.match(app.innerHTML, /Save failed/);
  assert.match(app.innerHTML, /&lt;script&gt;alert\(&quot;notes&quot;\)&lt;\/script&gt;/);
  assert.doesNotMatch(app.innerHTML, /<script>alert/);
});

test("chat loads only in task details and renders chronological escaped author, time, and body", async () => {
  const calls = [];
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", bucketId: "b", checklist: [], assignees: [], description: "" };
  const page = { state: "available", messages: [
    { id: "later", author: "Blair", createdAt: "2026-09-21T15:30:00Z", body: "Later" },
    { id: "earlier", author: "<script>alert(1)</script>", createdAt: "2026-09-21T14:00:00Z", body: "<script>alert(2)</script>" }
  ] };
  const { app, tap } = createDetailFixture(async path => {
    calls.push(path);
    return { ok: true, status: 200, json: async () => path.endsWith("/display") ? board : path.endsWith("/details") ? detail : page };
  });
  await settle();
  assert.equal(calls.filter(path => path.includes("/chat")).length, 0);
  await tap("data-open-task", { openTask: "task" });
  await settle();
  assert.equal(calls.filter(path => path.includes("/chat")).length, 1);
  assert.ok(app.innerHTML.indexOf("alert(2)") < app.innerHTML.indexOf("Later"));
  assert.match(app.innerHTML, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.match(app.innerHTML, /&lt;script&gt;alert\(2\)&lt;\/script&gt;/);
  assert.doesNotMatch(app.innerHTML, /<script>alert/);
  assert.match(app.innerHTML, /chat-meta/);
  assert.match(app.innerHTML, /\d{1,2}:\d{2}/);
});

test("chat shows empty and permission states", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "empty", title: "Empty" }, { taskId: "locked", title: "Locked" }] }
  ] };
  const { app, tap } = createDetailFixture(async path => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => ({ taskId: path.includes("locked") ? "locked" : "empty", checklist: [], assignees: [], description: "" }) };
    const response = path.includes("locked")
      ? { state: "interaction_required", messages: [], message: "Enable task chat to read and post Planner comments." }
      : { state: "available", messages: [] };
    return { ok: true, status: 200, json: async () => response };
  });
  await settle();
  await tap("data-open-task", { openTask: "empty" });
  await settle();
  assert.match(app.innerHTML, /No comments yet/);
  await tap("data-close-dialog");
  await tap("data-open-task", { openTask: "locked" });
  await settle();
  assert.match(app.innerHTML, /Enable task chat to read and post Planner comments/);
  assert.doesNotMatch(app.innerHTML, /data-post-chat/);
});

test("chat composer is multiline with its existing length and touch constraints", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap } = createDetailFixture(async path => ({ ok: true, status: 200, json: async () =>
    path.endsWith("/display") ? board : path.endsWith("/details") ? detail : { state: "available", messages: [] }
  }));
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();

  assert.match(app.innerHTML, /<textarea[^>]*data-chat-draft[^>]*maxlength="4000"/);
  assert.doesNotMatch(app.innerHTML, /<input[^>]*data-chat-draft/);
});

test("loading earlier chat prepends messages and preserves chronological order", async () => {
  const calls = [];
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap } = createDetailFixture(async path => {
    calls.push(path);
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    const older = path.includes("cursor=older");
    return { ok: true, status: 200, json: async () => older
      ? { state: "available", messages: [{ id: "first", author: "Alex", createdAt: "2026-09-20T12:00:00Z", body: "First" }] }
      : { state: "available", messages: [{ id: "second", author: "Blair", createdAt: "2026-09-21T12:00:00Z", body: "Second" }], nextCursor: "older" } };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  assert.match(app.innerHTML, /Load earlier comments/);
  await tap("data-load-earlier-chat");
  assert.ok(calls.some(path => path.endsWith("/tasks/task/chat?cursor=older")));
  assert.ok(app.innerHTML.indexOf("First") < app.innerHTML.indexOf("Second"));
});

test("failed chat posts retain the escaped draft and rapid taps post exactly once", async () => {
  let rejectPost;
  let postCount = 0;
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (options.method === "POST") {
      postCount++;
      return new Promise(resolve => { rejectPost = () => resolve({ ok: false, status: 503, json: async () => ({ message: "Post failed" }) }); });
    }
    return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-chat-draft", "Update <script>alert(3)</script>");
  const first = tap("data-post-chat");
  const second = tap("data-post-chat");
  assert.equal(postCount, 1);
  assert.match(app.innerHTML, /data-post-chat[^>]*disabled/);
  rejectPost();
  await Promise.all([first, second]);
  assert.match(app.innerHTML, /Post failed/);
  assert.match(app.innerHTML, /Update &lt;script&gt;alert\(3\)&lt;\/script&gt;/);
  assert.doesNotMatch(app.innerHTML, /<script>alert/);
});

test("successful chat posts clear the draft and render the returned page", async () => {
  const posts = [];
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (options.method === "POST") {
      posts.push(JSON.parse(options.body).message);
      return { ok: true, status: 200, json: async () => ({ state: "available", messages: [
        { id: "posted", author: "Alex", createdAt: "2026-09-21T12:00:00Z", body: "Status update" }
      ] }) };
    }
    return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-chat-draft", "Status update");
  await tap("data-post-chat");
  assert.deepEqual(posts, ["Status update"]);
  assert.match(app.innerHTML, /Status update/);
  assert.match(app.innerHTML, /<textarea[^>]*data-chat-draft[^>]*><\/textarea>/);
  assert.match(app.innerHTML, /Comment posted/);
});

test("chat edits made while posting remain as the next comment draft", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  let releasePost;
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (options.method === "POST") {
      assert.equal(JSON.parse(options.body).message, "Submitted comment");
      return new Promise(resolve => { releasePost = () => resolve({ ok: true, status: 200, json: async () => ({
        state: "available", messages: [{ id: "posted", author: "Alex", createdAt: "2026-09-21T12:00:00Z", body: "Submitted comment" }]
      }) }); });
    }
    return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-chat-draft", "Submitted comment");
  const posting = tap("data-post-chat");
  input("data-chat-draft", "Next comment");
  releasePost();
  await posting;
  assert.match(app.innerHTML, /<textarea[^>]*data-chat-draft[^>]*>Next comment<\/textarea>/);
  assert.match(app.innerHTML, /Submitted comment/);
});

test("closed dialogs reject stale chat completions", async () => {
  let resolveChat;
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap } = createDetailFixture(async path => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    return new Promise(resolve => { resolveChat = resolve; });
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  await tap("data-close-dialog");
  resolveChat({ ok: true, status: 200, json: async () => ({ state: "available", messages: [
    { id: "late", author: "Alex", createdAt: "2026-09-21T12:00:00Z", body: "Late" }
  ] }) });
  await settle();
  assert.doesNotMatch(app.innerHTML, /Task chat/);
  assert.doesNotMatch(app.innerHTML, /Late/);
});

test("closed dialogs ignore late chat rejection and cancellation errors", async () => {
  for (const lateError of [new Error("Late failure"), Object.assign(new Error("Cancelled"), { name: "AbortError" })]) {
    let rejectChat;
    const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
      { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
    ] };
    const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
    const { app, tap } = createDetailFixture(async path => {
      if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
      if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
      return new Promise((_, reject) => { rejectChat = reject; });
    });
    await settle();
    await tap("data-open-task", { openTask: "task" });
    await settle();
    await tap("data-close-dialog");
    rejectChat(lateError);
    await settle();
    assert.doesNotMatch(app.innerHTML, /Task chat|Late failure|Cancelled/);
  }
});

test("unattached first comment keeps a disabled draft and prevents another submission", async () => {
  let postCount = 0;
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (options.method === "POST") {
      postCount++;
      return { ok: true, status: 200, json: async () => ({ state: "attachment_pending", messages: [],
        message: "Your comment was created, but Planner could not attach the conversation. Refresh task details before posting again." }) };
    }
    return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-chat-draft", "First <comment>");
  await tap("data-post-chat");

  assert.equal(postCount, 1);
  assert.match(app.innerHTML, /comment was created/);
  assert.match(app.innerHTML, /<textarea[^>]*data-chat-draft[^>]*disabled[^>]*>First &lt;comment&gt;<\/textarea>/);
  assert.doesNotMatch(app.innerHTML, /data-post-chat/);
});

test("chat updates preserve detail panel, board, and bucket scroll positions", async () => {
  let resolveChat;
  let markup = "";
  let boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
  let bucketElement = { dataset: { bucketId: "b" }, scrollTop: 0 };
  let detailPanel = { scrollTop: 0 };
  const appOverrides = {
    get innerHTML() { return markup; },
    set innerHTML(value) {
      markup = value;
      boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
      bucketElement = { dataset: { bucketId: "b" }, scrollTop: 0 };
      detailPanel = { scrollTop: 0 };
    },
    querySelector(selector) {
      if (selector === ".board") return boardElement;
      if (selector === ".confirm-panel") return markup.includes("confirm-panel") ? detailPanel : null;
      return null;
    },
    querySelectorAll(selector) { return selector === ".bucket" ? [bucketElement] : []; }
  };
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: [], description: "" };
  const { tap } = createDetailFixture(async path => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    return new Promise(resolve => { resolveChat = resolve; });
  }, appOverrides);
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  boardElement.scrollLeft = 420;
  bucketElement.scrollTop = 180;
  detailPanel.scrollTop = 240;
  resolveChat({ ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) });
  await settle();
  assert.equal(boardElement.scrollLeft, 420);
  assert.equal(bucketElement.scrollTop, 180);
  assert.equal(detailPanel.scrollTop, 240);
});

test("returning from checklist completion reinitializes notes and chat", async () => {
  let chatReads = 0;
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [
    { itemId: "item", title: "Check", isChecked: false }
  ], assignees: [], description: "Keep this" };
  const { app, tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return { ok: true, status: 200, json: async () => board };
    if (path.endsWith("/details")) return { ok: true, status: 200, json: async () => detail };
    if (path.includes("/chat")) {
      chatReads++;
      return { ok: true, status: 200, json: async () => ({ state: "available", messages: [] }) };
    }
    if (options.method === "POST") return { ok: true, status: 204, json: async () => null };
    throw new Error(`Unexpected request: ${path}`);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "item" });
  await tap("data-confirm-checklist");
  await settle();
  assert.equal(chatReads, 2);
  assert.match(app.innerHTML, /data-notes-draft/);
  assert.match(app.innerHTML, /data-chat-draft/);
});

test("My tasks and filters persist per board while search does not", async () => {
  const writes = [];
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: "b", name: "Doing", tasks: [
      { taskId: "mine", title: "Mine", assignments: ["me"], priority: 5, percentComplete: 0, labelIds: [] },
      { taskId: "other", title: "Other", assignments: ["other"], priority: 5, percentComplete: 0, labelIds: [] }
    ] }
  ] };
  const preferences = { myTasks: true, filters: { assigneeIds: [], labelIds: [], priorities: [], bucketIds: [], progressValues: [], dueDateRange: null } };
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
      writes.push(JSON.parse(options.body)); return response(JSON.parse(options.body));
    }
    if (path.endsWith("/view-preferences/plan")) return response(preferences);
    if (path.endsWith("/auth/me")) return response({ userId: "me" });
    return response({ checklist: [], assignees: [] });
  });
  await settle();
  assert.match(app.innerHTML, /Mine/);
  assert.doesNotMatch(app.innerHTML, />Other</);

  await tap("data-toggle-my-tasks");
  assert.equal(writes.at(-1).myTasks, false);
  assert.equal("searchText" in writes.at(-1), false);
  await tap("data-toggle-search");
  input("data-search-tasks", "Other");
  assert.match(app.innerHTML, />Other</);
  assert.doesNotMatch(app.innerHTML, />Mine</);
  assert.equal(writes.some(write => JSON.stringify(write).includes("Other")), false);
});

test("typing a search updates the board without rebuilding the focused toolbar", async () => {
  let appRenders = 0;
  let markup = "";
  const boardElement = { dataset: { planId: "plan" }, scrollLeft: 0, innerHTML: "" };
  const appOverrides = {
    get innerHTML() { return markup; },
    set innerHTML(value) { markup = value; appRenders++; },
    querySelector: selector => selector === ".board" ? boardElement : null,
    querySelectorAll: () => []
  };
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: "b", name: "Doing", tasks: [
      { taskId: "mine", title: "Mine", assignments: [], priority: 5, percentComplete: 0, labelIds: [] },
      { taskId: "other", title: "Other", assignments: [], priority: 5, percentComplete: 0, labelIds: [] }
    ] }
  ] };
  const { tap, input } = createDetailFixture(async path => response(path.endsWith("/display") ? board :
    path.includes("view-preferences") ? defaultPreferences() : { checklist: [], assignees: [] }), appOverrides);
  await settle();
  await tap("data-toggle-search");
  const beforeTyping = appRenders;
  input("data-search-tasks", "Other");
  assert.equal(appRenders, beforeTyping);
  assert.match(boardElement.innerHTML, />Other</);
  assert.doesNotMatch(boardElement.innerHTML, />Mine</);
});

test("cards render priority labels progress due date and checklist preview", async () => {
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z",
    labels: [{ labelId: "category1", name: "Blocked" }], buckets: [{ bucketId: "b", name: "Doing", tasks: [{
      taskId: "task", title: "Build", priority: 1, percentComplete: 50, labelIds: ["category1"],
      dueDateTime: "2026-09-25T12:00:00Z", assignments: []
    }] }] };
  const { app } = createDetailFixture(async path => response(path.endsWith("/display") ? board :
    path.includes("view-preferences") ? defaultPreferences() : { taskId: "task", checklist: [{ itemId: "one", title: "First", isChecked: false }], assignees: [] }));
  await settle();
  assert.match(app.innerHTML, /Urgent/);
  assert.match(app.innerHTML, /Blocked/);
  assert.match(app.innerHTML, /In progress/);
  assert.match(app.innerHTML, /Sep 25/);
  assert.match(app.innerHTML, /First/);
});

test("task metadata controls use focused routes and completion confirmation", async () => {
  const calls = [];
  const board = organizationBoard();
  const detail = organizationDetail();
  const { app, tap, input, change } = createDetailFixture(async (path, options = {}) => {
    calls.push([path, options]);
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();

  input("data-title-draft", "Renamed task");
  await tap("data-save-title");
  assertRequest(calls, "/tasks/task/title", "PUT", { title: "Renamed task" });

  change("data-task-priority", "3");
  await settle();
  assertRequest(calls, "/tasks/task/priority", "PUT", { priority: 3 });

  input("data-start-date-draft", "2026-09-21");
  await tap("data-save-start-date");
  assertRequest(calls, "/tasks/task/start-date", "PUT", { date: "2026-09-21" });

  await tap("data-toggle-task-label", { toggleTaskLabel: "category2" });
  await tap("data-save-labels");
  assertRequest(calls, "/tasks/task/labels", "PUT", { labelIds: ["category1", "category2"] });

  const progressWritesBefore = calls.filter(([path]) => path.endsWith("/progress")).length;
  change("data-task-progress", "100");
  assert.match(app.innerHTML, /Complete task\?/);
  assert.equal(calls.filter(([path]) => path.endsWith("/progress")).length, progressWritesBefore);
  await tap("data-confirm-progress");
  assertRequest(calls, "/tasks/task/progress", "PUT", { progress: 100 });
});

test("failed title and label writes retain their drafts", async () => {
  const board = organizationBoard();
  const detail = organizationDetail();
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (options.method === "PUT") return response({ code: "conflict", message: "Refresh and try again." }, 409);
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-title-draft", "Keep this title");
  await tap("data-toggle-task-label", { toggleTaskLabel: "category2" });
  await tap("data-save-title");
  assert.match(app.innerHTML, /value="Keep this title"/);
  assert.match(app.innerHTML, /Refresh and try again/);
  await tap("data-save-labels");
  assert.match(app.innerHTML, /data-toggle-task-label="category2"[^>]*aria-pressed="true"/);
});

test("new task sends start date priority and labels without progress", async () => {
  const calls = [];
  const board = organizationBoard();
  const { tap, input, change } = createDetailFixture(async (path, options = {}) => {
    calls.push([path, options]);
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    return response(null, options.method === "POST" ? 204 : 200);
  });
  await settle();
  await tap("data-new-task");
  input("data-new-task-title", "New work");
  input("data-new-task-start", "2026-09-21");
  input("data-new-task-due", "2026-09-25");
  change("data-new-task-priority", "3");
  await tap("data-toggle-new-label", { toggleNewLabel: "category1" });
  await tap("data-submit-new-task");
  const create = calls.find(([path, options]) => path.endsWith("/tasks") && options?.method === "POST");
  const body = JSON.parse(create[1].body);
  assert.deepEqual(body, { title: "New work", bucketId: "b", date: "2026-09-25", userIds: [],
    startDate: "2026-09-21", priority: 3, labelIds: ["category1"] });
  assert.equal("percentComplete" in body, false);
});

test("saved filter errors remain visible without discarding the active filter", async () => {
  const board = organizationBoard();
  const { app, tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences") && options.method === "PUT")
      return response({ code: "offline", message: "Could not save filters." }, 503);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/members")) return response([]);
    return response({ checklist: [], assignees: [] });
  });
  await settle();
  await tap("data-open-filters");
  await tap("data-toggle-filter", { filterGroup: "priorities", filterValue: "1" });
  assert.match(app.innerHTML, /Could not save filters/);
  await tap("data-close-dialog");
  assert.match(app.innerHTML, /Urgent task/);
  assert.doesNotMatch(app.innerHTML, /Normal task/);
});

test("checklist supports add rename delete confirmation and button reordering", async () => {
  const calls = [];
  const board = organizationBoard();
  const detail = organizationDetail();
  const { app, handlers, tap, input } = createDetailFixture(async (path, options = {}) => {
    calls.push([path, options]);
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  assert.match(app.innerHTML, /data-move-checklist-up="first"[^>]*disabled/);
  assert.match(app.innerHTML, /data-move-checklist-down="second"[^>]*disabled/);
  assert.equal(handlers.dragstart, undefined);
  assert.equal(handlers.touchmove, undefined);

  input("data-checklist-add-draft", "New item");
  await tap("data-add-checklist");
  assertRequest(calls, "/tasks/task/checklist", "POST", { title: "New item" });

  await tap("data-edit-checklist", { editChecklist: "first" });
  input("data-checklist-edit-draft", "Renamed");
  await tap("data-save-checklist-edit", { saveChecklistEdit: "first" });
  assertRequest(calls, "/tasks/task/checklist/first", "PUT", { title: "Renamed" });

  await tap("data-delete-checklist", { deleteChecklist: "first" });
  assert.match(app.innerHTML, /Delete checklist item\?/);
  assert.equal(calls.some(([path, options]) => path.endsWith("/checklist/first") && options?.method === "DELETE"), false);
  await tap("data-dialog-backdrop");
  assert.match(app.innerHTML, /Task chat/);
  await tap("data-delete-checklist", { deleteChecklist: "first" });
  await tap("data-confirm-checklist-delete");
  assert.ok(calls.some(([path, options]) => path.endsWith("/checklist/first") && options?.method === "DELETE"));

  await tap("data-move-checklist-down", { moveChecklistDown: "first" });
  assertRequest(calls, "/tasks/task/checklist/first/position", "PUT", { direction: "down" });

  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "first" });
  assert.match(app.innerHTML, /Complete checklist item\?/);
});

function response(body, status = 200) {
  return { ok: status >= 200 && status < 300, status, json: async () => body };
}

function defaultPreferences() {
  return { myTasks: false, filters: { assigneeIds: [], labelIds: [], priorities: [], bucketIds: [], progressValues: [], dueDateRange: null } };
}

function organizationBoard() {
  return { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z",
    labels: [{ labelId: "category1", name: "Blocked" }, { labelId: "category2", name: "Release" }],
    buckets: [{ bucketId: "b", name: "Doing", tasks: [
      { taskId: "task", title: "Urgent task", bucketId: "b", priority: 1, percentComplete: 50,
        labelIds: ["category1"], assignments: ["person-a"], dueDateTime: "2026-09-25T12:00:00Z" },
      { taskId: "normal", title: "Normal task", bucketId: "b", priority: 5, percentComplete: 0,
        labelIds: [], assignments: [] }
    ] }]
  };
}

function organizationDetail() {
  return { taskId: "task", title: "Urgent task", bucketId: "b", dueDateTime: "2026-09-25T12:00:00Z",
    startDateTime: null, priority: 1, percentComplete: 50, labelIds: ["category1"],
    assignees: ["Alex"], assigneeIds: ["person-a"], description: "Notes", checklist: [
      { itemId: "first", title: "First", isChecked: false },
      { itemId: "second", title: "Second", isChecked: false }
    ] };
}

function assertRequest(calls, suffix, method, expectedBody) {
  const call = calls.find(([path, options]) => path.endsWith(suffix) && options?.method === method);
  assert.ok(call, `${method} ${suffix} was not requested`);
  assert.deepEqual(JSON.parse(call[1].body), expectedBody);
}
