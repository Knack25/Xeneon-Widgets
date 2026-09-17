import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

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
