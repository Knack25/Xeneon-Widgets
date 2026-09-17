import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

test("widget scripts start together and show the selected board", async () => {
  const app = { innerHTML: '<section class="status">Loading Planner...</section>', querySelectorAll: () => [], querySelector: () => null };
  const context = {
    document: { getElementById: () => app },
    fetch: async () => ({ ok: true, status: 200, json: async () => ({ planTitle: "Work", syncedAt: new Date().toISOString(), buckets: [] }) }),
    setInterval: () => {},
    Intl,
    Date,
  };
  for (const file of ["state.js", "api.js", "app.js"]) {
    runInNewContext(readFileSync(new URL(`../src/${file}`, import.meta.url), "utf8"), context, { filename: file });
  }
  await new Promise(resolve => setImmediate(resolve));
  assert.match(app.innerHTML, /Work/);
  assert.doesNotMatch(app.innerHTML, /Loading Planner/);
});
