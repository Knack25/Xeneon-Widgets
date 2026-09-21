import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

test("widget scripts start together and show the selected board", async () => {
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', addEventListener() {} };
  const context = {
    document: { getElementById: () => app },
    fetch: async path => ({ ok: true, status: 200, json: async () => path.endsWith("/display")
      ? ({ planId: "plan", planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [], labels: [] })
      : ({ myTasks: false, filters: {} }) }),
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
  await context.PlannerApi.getDisplay();
  assert.deepEqual(paths, ["/display"]);
});
