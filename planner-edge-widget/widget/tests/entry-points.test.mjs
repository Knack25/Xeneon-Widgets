import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

test("native and hosted entries both run the task app directly", () => {
  const nativePage = readFileSync(new URL("../index.html", import.meta.url), "utf8");
  const hostedPage = readFileSync(new URL("../board.html", import.meta.url), "utf8");
  assert.match(nativePage, /src="src\/app\.js"/);
  assert.doesNotMatch(nativePage, /<iframe/);
  assert.match(hostedPage, /src="src\/app\.js"/);
  assert.doesNotMatch(hostedPage, /<iframe/);
});

test("next widget package has its own version", () => {
  const manifest = JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url), "utf8"));
  assert.equal(manifest.version, "0.2.5");
});
