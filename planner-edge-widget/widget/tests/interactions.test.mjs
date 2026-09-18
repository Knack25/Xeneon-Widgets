import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

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
  assert.doesNotMatch(app.innerHTML, /<select/);
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
