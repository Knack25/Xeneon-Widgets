import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const settle = (milliseconds = 15) => new Promise(resolve => setTimeout(resolve, milliseconds));

function createDetailFixture(fetch, appOverrides = {}, contextOverrides = {}) {
  const handlers = {};
  const app = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  Object.defineProperties(app, Object.getOwnPropertyDescriptors(appOverrides));
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date, URLSearchParams,
    IntersectionObserver: class { observe() {} disconnect() {} }, fetch,
    localStorage: { getItem: () => null, setItem() {} }, ...contextOverrides };
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

test("scheduled board refresh reuses task details until the task ETag changes", async () => {
  let refresh;
  let detailReads = 0;
  const app = { innerHTML: "", addEventListener() {} };
  let taskETag = "v1";
  const context = { document: { getElementById: () => app }, setInterval: callback => { refresh = callback; }, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => {
      if (path.endsWith("/display/cached")) return null;
      if (path.endsWith("/display")) return { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
        { bucketId: "bucket", name: "Doing", tasks: [{ taskId: "task", title: "Build", eTag: taskETag }] }
      ] };
      detailReads++;
      return { taskId: "task", checklist: [], assignees: [] };
    } }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(detailReads, 1);
  await refresh();
  await new Promise(resolve => setTimeout(resolve, 15));
  assert.equal(detailReads, 1);
  taskETag = "v2";
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
      if (path.endsWith("/display/cached")) return null;
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

test("returning from checklist completion preserves the notes and chat session", async () => {
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
  assert.equal(chatReads, 1);
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

test("switching plans isolates preferences and retries a failed preference load", async () => {
  let refresh;
  let selectedPlan = "plan-a";
  let planBPreferenceReads = 0;
  const boards = {
    "plan-a": preferenceBoard("plan-a", "Alpha"),
    "plan-b": preferenceBoard("plan-b", "Beta")
  };
  const handlers = {};
  const appNode = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const context = { document: { getElementById: () => appNode }, Intl, Date, URLSearchParams,
    localStorage: { getItem: () => null, setItem() {} },
    IntersectionObserver: class { observe() {} disconnect() {} },
    setInterval: callback => { refresh = callback; },
    fetch: async (path, options = {}) => {
      if (path.endsWith("/display")) return response(boards[selectedPlan]);
      if (path.endsWith("/plans")) return response([{ planId: "plan-a", title: "Alpha" }, { planId: "plan-b", title: "Beta" }]);
      if (path.endsWith("/selected-plan") && options.method === "PUT") {
        selectedPlan = JSON.parse(options.body).planId; return response({ planId: selectedPlan });
      }
      if (path.endsWith("/view-preferences/plan-a")) return response(preferencesWith({ priorities: [1] }));
      if (path.endsWith("/view-preferences/plan-b")) {
        planBPreferenceReads++;
        if (planBPreferenceReads === 1) return response({ code: "offline", message: "Preferences unavailable." }, 503);
        return response(preferencesWith({ priorities: [5] }));
      }
      return response({ checklist: [], assignees: [] });
    } };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  const click = (attribute, dataset = {}) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null,
    matches: selector => selector === `[${attribute}]`
  } });
  await settle();
  assert.match(appNode.innerHTML, /Urgent plan-a/);
  assert.doesNotMatch(appNode.innerHTML, /Normal plan-a/);
  await click("data-open-board-picker");
  await click("data-select-plan", { selectPlan: "plan-b" });
  assert.match(appNode.innerHTML, /Urgent plan-b/);
  assert.match(appNode.innerHTML, /Normal plan-b/);
  assert.equal(planBPreferenceReads, 1);
  await refresh();
  assert.doesNotMatch(appNode.innerHTML, /Urgent plan-b/);
  assert.match(appNode.innerHTML, /Normal plan-b/);
  assert.equal(planBPreferenceReads, 2);
});

test("same-plan preference retry preserves settings saved after an initial read failure", async () => {
  let refresh;
  let preferenceReads = 0;
  const writes = [];
  const board = preferenceBoard("plan", "Work");
  const handlers = {};
  const appNode = { innerHTML: "", addEventListener: (type, listener) => { handlers[type] = listener; } };
  const context = { document: { getElementById: () => appNode }, Intl, Date, URLSearchParams,
    localStorage: { getItem: () => null, setItem() {} },
    IntersectionObserver: class { observe() {} disconnect() {} },
    setInterval: callback => { refresh = callback; },
    fetch: async (path, options = {}) => {
      if (path.endsWith("/display")) return response(board);
      if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
        const saved = JSON.parse(options.body); writes.push(saved); return response(saved);
      }
      if (path.endsWith("/view-preferences/plan")) {
        preferenceReads++;
        return response({ code: "offline", message: "Preferences unavailable." }, 503);
      }
      if (path.endsWith("/members")) return response([]);
      return response({ checklist: [], assignees: [] });
    } };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  const click = (attribute, dataset = {}) => handlers.click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null,
    matches: selector => selector === `[${attribute}]`
  } });
  await settle();
  assert.equal(preferenceReads, 1);
  assert.match(appNode.innerHTML, /Urgent plan/);
  assert.match(appNode.innerHTML, /Normal plan/);
  await click("data-open-filters");
  await click("data-toggle-filter", { filterGroup: "priorities", filterValue: "1" });
  await click("data-close-dialog");
  assert.deepEqual(writes.at(-1).filters.priorities, [1]);
  assert.match(appNode.innerHTML, /Urgent plan/);
  assert.doesNotMatch(appNode.innerHTML, /Normal plan/);
  await refresh();
  assert.equal(preferenceReads, 2);
  assert.match(appNode.innerHTML, /Urgent plan/);
  assert.doesNotMatch(appNode.innerHTML, /Normal plan/);
});

test("restored assignee filters remove stale members and persist the repair", async () => {
  const writes = [];
  let memberReads = 0;
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: "b", name: "Doing", tasks: [
      { taskId: "valid-task", title: "Valid", assignments: ["valid"], priority: 5, percentComplete: 0, labelIds: [] },
      { taskId: "other-task", title: "Other", assignments: ["other"], priority: 5, percentComplete: 0, labelIds: [] }
    ] }
  ] };
  const { app } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
      writes.push(JSON.parse(options.body)); return response(JSON.parse(options.body));
    }
    if (path.endsWith("/view-preferences/plan")) return response(preferencesWith({ assigneeIds: ["valid", "deleted"] }));
    if (path.endsWith("/members")) { memberReads++; return response([{ id: "valid", displayName: "Valid Person" }]); }
    return response({ checklist: [], assignees: [] });
  });
  await settle();
  assert.equal(memberReads, 1);
  assert.deepEqual(writes.at(-1).filters.assigneeIds, ["valid"]);
  assert.match(app.innerHTML, />Valid</);
  assert.doesNotMatch(app.innerHTML, />Other</);
});

test("overlapping member restoration ignores the prior plan response", async () => {
  let refresh;
  let selectedPlan = "plan-a";
  const memberReads = { "plan-a": 0, "plan-b": 0 };
  const memberResolvers = {};
  const boards = {
    "plan-a": { ...preferenceBoard("plan-a", "Alpha"), buckets: [{ bucketId: "plan-a-bucket", name: "Doing", tasks: [
      { taskId: "a", title: "Alpha task", assignments: ["a-user"], priority: 5, percentComplete: 0, labelIds: [] }
    ] }] },
    "plan-b": { ...preferenceBoard("plan-b", "Beta"), buckets: [{ bucketId: "plan-b-bucket", name: "Doing", tasks: [
      { taskId: "b", title: "Beta task", assignments: ["b-user"], priority: 5, percentComplete: 0, labelIds: [] },
      { taskId: "other", title: "Other task", assignments: ["other"], priority: 5, percentComplete: 0, labelIds: [] }
    ] }] }
  };
  const appNode = { innerHTML: "", addEventListener() {} };
  const context = { document: { getElementById: () => appNode }, Intl, Date, URLSearchParams,
    localStorage: { getItem: () => null, setItem() {} },
    IntersectionObserver: class { observe() {} disconnect() {} },
    setInterval: callback => { refresh = callback; },
    fetch: async path => {
      if (path.endsWith("/display")) return response(boards[selectedPlan]);
      if (path.includes("/view-preferences/"))
        return response(preferencesWith({ assigneeIds: [selectedPlan === "plan-a" ? "a-user" : "b-user"] }));
      if (path.endsWith("/members")) {
        const requestPlan = selectedPlan;
        memberReads[requestPlan]++;
        return new Promise(resolve => { memberResolvers[requestPlan] = members => resolve(response(members)); });
      }
      return response({ checklist: [], assignees: [] });
    } };
  for (const file of ["state.js", "api.js", "filters.js", "view-state.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await settle();
  assert.equal(memberReads["plan-a"], 1);
  selectedPlan = "plan-b";
  refresh();
  await settle();
  assert.equal(memberReads["plan-b"], 1);
  memberResolvers["plan-b"]([{ id: "b-user", displayName: "Beta User" }]);
  await settle();
  assert.match(appNode.innerHTML, />Beta task</);
  assert.doesNotMatch(appNode.innerHTML, />Other task</);
  memberResolvers["plan-a"]([{ id: "a-user", displayName: "Alpha User" }]);
  await settle();
  assert.match(appNode.innerHTML, />Beta task</);
  assert.doesNotMatch(appNode.innerHTML, />Alpha task</);
  assert.deepEqual(memberReads, { "plan-a": 1, "plan-b": 1 });
});

test("successful structured filter changes persist the selected value", async () => {
  const writes = [];
  const board = organizationBoard();
  const { tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.endsWith("/view-preferences/plan") && options.method === "PUT") {
      writes.push(JSON.parse(options.body)); return response(JSON.parse(options.body));
    }
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/members")) return response([]);
    return response({ checklist: [], assignees: [] });
  });
  await settle();
  await tap("data-open-filters");
  await tap("data-toggle-filter", { filterGroup: "priorities", filterValue: "1" });
  assert.deepEqual(writes.at(-1).filters.priorities, [1]);
  assert.equal("searchText" in writes.at(-1), false);
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
  assert.match(app.innerHTML, /data-task-progress[\s\S]*option value="100" selected/);
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

test("failed priority and non-complete progress writes retain attempted selections", async () => {
  const board = organizationBoard();
  const detail = { ...organizationDetail(), priority: 1, percentComplete: 0 };
  const { app, tap, change } = createDetailFixture(async (path, options = {}) => {
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
  await change("data-task-priority", "3");
  assert.match(app.innerHTML, /data-task-priority[\s\S]*option value="3" selected/);
  assert.match(app.innerHTML, /Refresh and try again/);
  await change("data-task-progress", "50");
  assert.match(app.innerHTML, /data-task-progress[\s\S]*option value="50" selected/);
  assert.match(app.innerHTML, /Refresh and try again/);
});

test("older priority success cannot clear a newer failed draft", async () => {
  const requests = [];
  const board = organizationBoard();
  const detail = { ...organizationDetail(), priority: 1 };
  const { app, tap, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (path.endsWith("/priority") && options.method === "PUT")
      return new Promise(resolve => requests.push({ value: JSON.parse(options.body).priority, resolve }));
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  const older = change("data-task-priority", "3");
  const newer = change("data-task-priority", "5");
  assert.deepEqual(requests.map(request => request.value), [3, 5]);
  requests[1].resolve(response({ code: "conflict", message: "Keep medium." }, 409));
  await newer;
  assert.match(app.innerHTML, /data-task-priority[\s\S]*option value="5" selected/);
  assert.match(app.innerHTML, /Keep medium/);
  requests[0].resolve(response(null, 204));
  await older;
  assert.match(app.innerHTML, /data-task-priority[\s\S]*option value="5" selected/);
  assert.match(app.innerHTML, /Keep medium/);
});

test("older progress success cannot clear a newer failed non-complete draft", async () => {
  const requests = [];
  const board = organizationBoard();
  const detail = { ...organizationDetail(), percentComplete: 0 };
  const { app, tap, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (path.endsWith("/progress") && options.method === "PUT")
      return new Promise(resolve => requests.push({ value: JSON.parse(options.body).progress, resolve }));
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  const older = change("data-task-progress", "50");
  const newer = change("data-task-progress", "0");
  assert.deepEqual(requests.map(request => request.value), [50, 0]);
  requests[1].resolve(response({ code: "conflict", message: "Keep not started." }, 409));
  await newer;
  assert.match(app.innerHTML, /data-task-progress[\s\S]*option value="0" selected/);
  assert.match(app.innerHTML, /Keep not started/);
  requests[0].resolve(response(null, 204));
  await older;
  assert.match(app.innerHTML, /data-task-progress[\s\S]*option value="0" selected/);
  assert.match(app.innerHTML, /Keep not started/);
});

test("metadata completion cannot release an active progress confirmation request", async () => {
  const priorityRequests = [];
  const progressRequests = [];
  const board = organizationBoard();
  const detail = { ...organizationDetail(), priority: 1, percentComplete: 0 };
  const { app, tap, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (path.endsWith("/priority") && options.method === "PUT")
      return new Promise(resolve => priorityRequests.push(resolve));
    if (path.endsWith("/progress") && options.method === "PUT")
      return new Promise(resolve => progressRequests.push(resolve));
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  const priorityWrite = change("data-task-priority", "3");
  await change("data-task-progress", "100");
  assert.match(app.innerHTML, /Complete task\?/);
  const completionWrite = tap("data-confirm-progress");
  assert.equal(progressRequests.length, 1);
  assert.match(app.innerHTML, /data-confirm-progress\s+disabled/);

  priorityRequests[0](response(null, 204));
  await priorityWrite;
  const confirmStayedDisabled = /data-confirm-progress\s+disabled/.test(app.innerHTML);
  const duplicateAttempt = tap("data-confirm-progress");
  const progressWriteCount = progressRequests.length;

  progressRequests[0](response(null, 204));
  await completionWrite;
  if (progressRequests[1]) {
    progressRequests[1](response(null, 204));
    await duplicateAttempt;
  }

  assert.equal(confirmStayedDisabled, true);
  assert.equal(progressWriteCount, 1);
  assert.match(app.innerHTML, /data-task-progress[\s\S]*option value="100" selected/);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
});

test("background display completion cannot release an active progress confirmation request", async () => {
  let refresh;
  let displayReads = 0;
  let resolveRefresh;
  const progressRequests = [];
  const board = organizationBoard();
  const detail = { ...organizationDetail(), percentComplete: 0 };
  const { app, tap, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display/cached")) return response(null, 204);
    if (path.endsWith("/display")) {
      displayReads++;
      if (displayReads === 1) return response(board);
      return new Promise(resolve => { resolveRefresh = resolve; });
    }
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (path.endsWith("/progress") && options.method === "PUT")
      return new Promise(resolve => progressRequests.push(resolve));
    return response(null, 204);
  }, {}, { setInterval: callback => { refresh = callback; } });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();

  const refreshing = refresh();
  await settle();
  await change("data-task-progress", "100");
  const completion = tap("data-confirm-progress");
  assert.equal(progressRequests.length, 1);
  assert.match(app.innerHTML, /data-confirm-progress\s+disabled/);

  resolveRefresh(response(board));
  await refreshing;
  await settle();
  const stayedDisabled = /data-confirm-progress\s+disabled/.test(app.innerHTML);
  const duplicate = tap("data-confirm-progress");
  const progressWriteCount = progressRequests.length;

  progressRequests[0](response(null, 204));
  await completion;
  if (progressRequests[1]) {
    progressRequests[1](response(null, 204));
    await duplicate;
  }

  assert.equal(stayedDisabled, true);
  assert.equal(progressWriteCount, 1);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
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
  assert.match(app.innerHTML, /confirm-panel[\s\S]*dialog-error[^>]*role="alert"[^>]*>Could not save filters/);
  await tap("data-close-dialog");
  assert.match(app.innerHTML, /Urgent task/);
  assert.doesNotMatch(app.innerHTML, /Normal task/);
});

test("failed checklist add and rename writes retain their drafts", async () => {
  const board = organizationBoard();
  const detail = organizationDetail();
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(detail);
    if (path.includes("/checklist") && ["POST", "PUT"].includes(options.method))
      return response({ code: "conflict", message: "Checklist changed in Planner." }, 409);
    return response(null, 204);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-checklist-add-draft", "Keep new item");
  await tap("data-add-checklist");
  assert.match(app.innerHTML, /data-checklist-add-draft[^>]*value="Keep new item"/);
  assert.match(app.innerHTML, /Checklist changed in Planner/);
  await tap("data-edit-checklist", { editChecklist: "first" });
  input("data-checklist-edit-draft", "Keep renamed item");
  await tap("data-save-checklist-edit", { saveChecklistEdit: "first" });
  assert.match(app.innerHTML, /data-checklist-edit-draft[^>]*value="Keep renamed item"/);
  assert.match(app.innerHTML, /Checklist changed in Planner/);
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

test("live refresh preserves open dialog drafts filters search and scroll", async () => {
  let refresh;
  let resolveRefresh;
  let displayReads = 0;
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
  const makeBoard = title => ({ planId: "plan", planTitle: title, syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build", priority: 1,
      percentComplete: 0, assignments: ["me"], labelIds: [], eTag: "v1" }] }
  ] });
  const detail = { taskId: "task", title: "Build", checklist: [], assignees: ["Me"], assigneeIds: ["me"],
    description: "Original notes", priority: 1, percentComplete: 0, startDateTime: null, labelIds: [] };
  const { app, tap, input } = createDetailFixture(async path => {
    if (path.endsWith("/display/cached")) return response(null, 204);
    if (path.endsWith("/display")) {
      displayReads++;
      if (displayReads === 1) return response(makeBoard("Cached Board"));
      return new Promise(resolve => { resolveRefresh = resolve; });
    }
    if (path.includes("view-preferences")) return response({ myTasks: true, filters: { assigneeIds: [], labelIds: [],
      priorities: [1], bucketIds: [], progressValues: [], dueDateRange: null } });
    if (path.endsWith("/auth/me")) return response({ userId: "me" });
    if (path.endsWith("/details")) return response(detail);
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    throw new Error(`Unexpected request: ${path}`);
  }, appOverrides, { setInterval: callback => { refresh = callback; } });

  await settle(30);
  await tap("data-toggle-search");
  input("data-search-tasks", "Build");
  await tap("data-open-task", { openTask: "task" });
  await settle(30);
  input("data-title-draft", "Draft title");
  input("data-notes-draft", "Draft notes");
  input("data-checklist-add-draft", "Draft checklist item");
  input("data-start-date-draft", "2026-09-30");
  boardElement.scrollLeft = 420;
  bucketElement.scrollTop = 180;

  const refreshing = refresh();
  await settle();
  assert.equal(typeof resolveRefresh, "function");
  resolveRefresh(response(makeBoard("Live Board")));
  await refreshing;
  await settle();

  assert.match(app.innerHTML, /Live Board/);
  assert.match(app.innerHTML, /data-title-draft[^>]*value="Draft title"/);
  assert.match(app.innerHTML, /data-notes-draft[^>]*>Draft notes<\/textarea>/);
  assert.match(app.innerHTML, /data-checklist-add-draft[^>]*value="Draft checklist item"/);
  assert.match(app.innerHTML, /data-start-date-draft[^>]*value="2026-09-30"/);
  assert.match(app.innerHTML, /filter-action active/);
  assert.match(app.innerHTML, /my-tasks-action active/);
  assert.match(app.innerHTML, /data-search-tasks[^>]*value="Build"/);
  assert.match(app.innerHTML, /data-checklist-task="task"|Task chat/);
  assert.equal(boardElement.scrollLeft, 420);
  assert.equal(bucketElement.scrollTop, 180);
});

test("late live response from an old plan generation is ignored", async () => {
  let selectedPlan = "plan-a";
  let liveReads = 0;
  let resolveOldLive;
  const boards = {
    "plan-a": { planId: "plan-a", planTitle: "Alpha cached", syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [] },
    "plan-b": { planId: "plan-b", planTitle: "Beta live", syncedAt: "2026-09-21T12:01:00Z", labels: [], buckets: [] }
  };
  const { app, tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display/cached")) return response(boards[selectedPlan]);
    if (path.endsWith("/display")) {
      liveReads++;
      if (liveReads === 1) return new Promise(resolve => { resolveOldLive = resolve; });
      return response(boards[selectedPlan]);
    }
    if (path.endsWith("/plans")) return response([{ planId: "plan-b", title: "Beta", groupName: "Team" }]);
    if (path.endsWith("/selected-plan")) {
      selectedPlan = JSON.parse(options.body).planId;
      return response(null, 204);
    }
    if (path.includes("view-preferences")) return response(defaultPreferences());
    throw new Error(`Unexpected request: ${path}`);
  });

  await settle();
  assert.match(app.innerHTML, /Alpha cached/);
  await tap("data-open-board-picker");
  await tap("data-select-plan", { selectPlan: "plan-b" });
  await settle();
  assert.match(app.innerHTML, /Beta live/);

  resolveOldLive(response({ ...boards["plan-a"], planTitle: "Alpha late" }));
  await settle();
  assert.match(app.innerHTML, /Beta live/);
  assert.doesNotMatch(app.innerHTML, /Alpha late/);
});

test("saved board and bucket offsets restore after reload and scroll events persist updates", async () => {
  let markup = "";
  let boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
  let bucketElement = { dataset: { bucketId: "b" }, scrollTop: 0 };
  const writes = [];
  const appOverrides = {
    get innerHTML() { return markup; },
    set innerHTML(value) {
      markup = value;
      boardElement = { dataset: { planId: "plan" }, scrollLeft: 0 };
      bucketElement = { dataset: { bucketId: "b" }, scrollTop: 0 };
    },
    querySelector: selector => selector === ".board" && markup.includes('class="board"') ? boardElement : null,
    querySelectorAll: selector => selector === ".bucket" && markup.includes('class="board"') ? [bucketElement] : []
  };
  const stored = JSON.stringify({ boardScrollLeft: 315, bucketScrollTops: { b: 125 } });
  const localStorage = {
    getItem: key => key === "planner-edge:view:plan" ? stored : null,
    setItem: (key, value) => writes.push([key, value])
  };
  const board = { planId: "plan", planTitle: "Work", syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: "b", name: "Doing", tasks: [] }
  ] };
  const { handlers } = createDetailFixture(async path => {
    if (path.endsWith("/display/cached")) return response(null, 204);
    if (path.endsWith("/display")) return response(board);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    throw new Error(`Unexpected request: ${path}`);
  }, appOverrides, { localStorage });

  await settle(30);
  assert.equal(boardElement.scrollLeft, 315);
  assert.equal(bucketElement.scrollTop, 125);

  boardElement.scrollLeft = 460;
  bucketElement.scrollTop = 190;
  handlers.scroll({ target: bucketElement });
  await settle();
  const [key, value] = writes.at(-1);
  assert.equal(key, "planner-edge:view:plan");
  assert.deepEqual(JSON.parse(value), { boardScrollLeft: 460, bucketScrollTops: { b: 190 } });
});

test("rapid preference writes are serialized and the newest failed snapshot stays active", async () => {
  const writes = [];
  const { app, tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(organizationBoard());
    if (path.endsWith("/auth/me")) return response({ userId: "me" });
    if (path.includes("view-preferences") && options.method === "PUT")
      return new Promise(resolve => writes.push({ body: JSON.parse(options.body), resolve }));
    if (path.includes("view-preferences")) return response(defaultPreferences());
    throw new Error(`Unexpected request: ${path}`);
  });
  await settle();

  const first = tap("data-toggle-my-tasks");
  await settle();
  const second = tap("data-toggle-my-tasks");
  await settle();

  assert.equal(writes.length, 1);
  assert.equal(writes[0].body.myTasks, true);
  writes[0].resolve(response(null, 204));
  await settle();
  assert.equal(writes.length, 2);
  assert.equal(writes[1].body.myTasks, false);
  writes[1].resolve(response({ code: "disk_error", message: "Preferences were not saved." }, 500));
  await Promise.all([first, second]);
  await settle();

  assert.match(app.innerHTML, /aria-label="My tasks" aria-pressed="false"/);
  assert.match(app.innerHTML, /Preferences were not saved/);
});

test("checklist completion refreshes server detail without discarding distinct drafts", async () => {
  let detailReads = 0;
  let completed = false;
  const initial = { ...organizationDetail(), priority: 1, percentComplete: 0 };
  const refreshed = { ...initial, title: "Server refreshed", description: "Server notes",
    checklist: initial.checklist.map(item => item.itemId === "first" ? { ...item, isChecked: true } : item) };
  const { app, tap, input, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(organizationBoard());
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) { detailReads++; return response(completed ? refreshed : initial); }
    if (path.endsWith("/priority") || path.endsWith("/progress"))
      return response({ code: "save_failed", message: "Keep the draft." }, 503);
    if (path.endsWith("/checklist/first/complete") && options.method === "POST") {
      completed = true;
      return response(null, 204);
    }
    throw new Error(`Unexpected request: ${path}`);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();

  input("data-title-draft", "Draft title");
  input("data-notes-draft", "Draft notes");
  input("data-start-date-draft", "2026-10-03");
  input("data-checklist-add-draft", "Draft addition");
  await tap("data-toggle-task-label", { toggleTaskLabel: "category2" });
  await tap("data-edit-checklist", { editChecklist: "second" });
  input("data-checklist-edit-draft", "Draft rename");
  await change("data-task-priority", "9");
  await change("data-task-progress", "50");
  const readsBeforeCompletion = detailReads;
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "first" });
  await tap("data-confirm-checklist");
  await settle();

  assert.equal(detailReads, readsBeforeCompletion + 1);
  assert.match(app.innerHTML, /<h2>Server refreshed<\/h2>/);
  assert.match(app.innerHTML, /data-title-draft[^>]*value="Draft title"/);
  assert.match(app.innerHTML, /data-notes-draft[^>]*>Draft notes<\/textarea>/);
  assert.match(app.innerHTML, /data-start-date-draft[^>]*value="2026-10-03"/);
  assert.match(app.innerHTML, /data-checklist-add-draft[^>]*value="Draft addition"/);
  assert.match(app.innerHTML, /data-checklist-edit-draft[^>]*value="Draft rename"/);
  assert.match(app.innerHTML, /value="9" selected>Low/);
  assert.match(app.innerHTML, /value="50" selected>In progress/);
  assert.match(app.innerHTML, /data-toggle-task-label="category2"[^>]*aria-pressed="true"/);
});

test("metadata and checklist conflicts refresh server detail while retaining retryable drafts", async () => {
  let detailReads = 0;
  let titleWrites = 0;
  let checklistWrites = 0;
  let serverTitle = "Urgent task";
  const { app, tap, input } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(organizationBoard());
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) {
      detailReads++;
      return response({ ...organizationDetail(), title: serverTitle });
    }
    if (path.endsWith("/title") && options.method === "PUT") {
      titleWrites++;
      serverTitle = "Server title after conflict";
      return response({ code: "task_conflict", message: "Task changed. Refreshed details." }, 409);
    }
    if (path.endsWith("/checklist") && options.method === "POST") {
      checklistWrites++;
      serverTitle = "Server title after checklist conflict";
      return response({ code: "task_conflict", message: "Checklist changed. Refreshed details." }, 409);
    }
    throw new Error(`Unexpected request: ${path}`);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  input("data-notes-draft", "Keep notes");
  input("data-title-draft", "Retry title");
  const readsBeforeTitle = detailReads;
  await tap("data-save-title");
  assert.equal(titleWrites, 1);
  assert.equal(detailReads, readsBeforeTitle + 1);
  assert.match(app.innerHTML, /<h2>Server title after conflict<\/h2>/);
  assert.match(app.innerHTML, /data-title-draft[^>]*value="Retry title"/);
  assert.match(app.innerHTML, /data-notes-draft[^>]*>Keep notes<\/textarea>/);
  assert.match(app.innerHTML, /Task changed\. Refreshed details\./);

  input("data-checklist-add-draft", "Retry checklist item");
  const readsBeforeChecklist = detailReads;
  await tap("data-add-checklist");
  assert.equal(checklistWrites, 1);
  assert.equal(detailReads, readsBeforeChecklist + 1);
  assert.match(app.innerHTML, /<h2>Server title after checklist conflict<\/h2>/);
  assert.match(app.innerHTML, /data-checklist-add-draft[^>]*value="Retry checklist item"/);
  assert.match(app.innerHTML, /Checklist changed\. Refreshed details\./);
});

test("not found checklist write refreshes the board and removes the deleted task", async () => {
  let displayReads = 0;
  const present = organizationBoard();
  const removed = { ...present, buckets: present.buckets.map(bucket => ({ ...bucket,
    tasks: bucket.tasks.filter(task => task.taskId !== "task") })) };
  const { app, tap } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(displayReads++ ? removed : present);
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(organizationDetail());
    if (path.endsWith("/checklist/first") && options.method === "DELETE")
      return response({ code: "not_found", message: "Task was deleted. Board refreshed." }, 404);
    throw new Error(`Unexpected request: ${path}`);
  });
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();
  await tap("data-delete-checklist", { deleteChecklist: "first" });
  const readsBeforeDelete = displayReads;
  await tap("data-confirm-checklist-delete");
  await settle();

  assert.equal(displayReads, readsBeforeDelete + 1);
  assert.doesNotMatch(app.innerHTML, /data-open-task="task"/);
  assert.match(app.innerHTML, /Task was deleted\. Board refreshed\./);
});

test("nested detail confirmations preserve owner scroll on cancel and successful writes", async () => {
  let markup = "";
  let detailPanel = { scrollTop: 0 };
  const appOverrides = {
    get innerHTML() { return markup; },
    set innerHTML(value) { markup = value; detailPanel = { scrollTop: 0 }; },
    querySelector(selector) {
      if (selector === ".confirm-panel") return markup.includes("confirm-panel") ? detailPanel : null;
      return null;
    },
    querySelectorAll() { return []; }
  };
  const { tap, change } = createDetailFixture(async (path, options = {}) => {
    if (path.endsWith("/display")) return response(organizationBoard());
    if (path.includes("view-preferences")) return response(defaultPreferences());
    if (path.endsWith("/chat")) return response({ state: "available", messages: [] });
    if (path.endsWith("/details")) return response(organizationDetail());
    if (path.includes("/checklist") || path.endsWith("/progress")) return response(null, 204);
    throw new Error(`Unexpected request: ${path} ${options.method || "GET"}`);
  }, appOverrides);
  await settle();
  await tap("data-open-task", { openTask: "task" });
  await settle();

  detailPanel.scrollTop = 210;
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "first" });
  await tap("data-close-dialog");
  assert.equal(detailPanel.scrollTop, 210);

  detailPanel.scrollTop = 220;
  await tap("data-delete-checklist", { deleteChecklist: "first" });
  await tap("data-close-dialog");
  assert.equal(detailPanel.scrollTop, 220);

  detailPanel.scrollTop = 230;
  await change("data-task-progress", "100");
  await tap("data-close-dialog");
  assert.equal(detailPanel.scrollTop, 230);

  detailPanel.scrollTop = 310;
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "first" });
  await tap("data-confirm-checklist");
  assert.equal(detailPanel.scrollTop, 310);

  detailPanel.scrollTop = 320;
  await tap("data-delete-checklist", { deleteChecklist: "first" });
  await tap("data-confirm-checklist-delete");
  assert.equal(detailPanel.scrollTop, 320);

  detailPanel.scrollTop = 330;
  await change("data-task-progress", "100");
  await tap("data-confirm-progress");
  assert.equal(detailPanel.scrollTop, 330);
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

function preferenceBoard(planId, title) {
  return { planId, planTitle: title, syncedAt: "2026-09-21T12:00:00Z", labels: [], buckets: [
    { bucketId: `${planId}-bucket`, name: "Doing", tasks: [
      { taskId: `${planId}-urgent`, title: `Urgent ${planId}`, assignments: [], priority: 1, percentComplete: 0, labelIds: [] },
      { taskId: `${planId}-normal`, title: `Normal ${planId}`, assignments: [], priority: 5, percentComplete: 0, labelIds: [] }
    ] }
  ] };
}

function preferencesWith(filters) {
  return { ...defaultPreferences(), filters: { ...defaultPreferences().filters, ...filters } };
}

function assertRequest(calls, suffix, method, expectedBody) {
  const call = calls.find(([path, options]) => path.endsWith(suffix) && options?.method === method);
  assert.ok(call, `${method} ${suffix} was not requested`);
  assert.deepEqual(JSON.parse(call[1].body), expectedBody);
}
