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
  assert.equal((app.innerHTML.match(/data-checklist-item=/g) || []).length, 3);
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
  assert.deepEqual(calls.map(([path, method]) => [path.endsWith("/selected-plan"), method]), [[true, "PUT"], [false, "POST"]]);
  assert.match(calls[1][0], /tasks\/task\/checklist\/item\/complete$/);
});

test("task body and checklist taps do not open task completion", async () => {
  let click;
  const app = { innerHTML: "", addEventListener: (_, listener) => { click = listener; } };
  const board = { planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [
    { bucketId: "b", name: "Doing", tasks: [{ taskId: "task", title: "Build" }] }
  ] };
  const detail = { taskId: "task", title: "Build", checklist: [{ itemId: "item", title: "Check", isChecked: false }], assignees: [] };
  const context = { document: { getElementById: () => app }, setInterval() {}, Intl, Date,
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display") ? board : detail }) };
  for (const file of ["state.js", "api.js", "app.js"])
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context);
  await new Promise(resolve => setTimeout(resolve, 20));

  const tap = async (attribute, dataset) => click({ target: {
    closest: selector => selector === `[${attribute}]` ? { dataset } : null
  } });
  await tap("data-open-task", { openTask: "task" });
  assert.match(app.innerHTML, /No due date/);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
  await tap("data-checklist-item", { checklistTask: "task", checklistItem: "item" });
  assert.match(app.innerHTML, /Complete checklist item\?/);
  assert.doesNotMatch(app.innerHTML, /Complete task\?/);
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
