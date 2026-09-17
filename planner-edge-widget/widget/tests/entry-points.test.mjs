import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

test("native entry embeds the helper while hosted entry runs the task app", () => {
  const nativePage = readFileSync(new URL("../index.html", import.meta.url), "utf8");
  const hostedPage = readFileSync(new URL("../board.html", import.meta.url), "utf8");
  assert.match(nativePage, /<iframe[^>]+src="http:\/\/localhost:8787\/board\/index\.html"/);
  assert.doesNotMatch(nativePage, /src="src\/app\.js"/);
  assert.match(hostedPage, /src="src\/app\.js"/);
  assert.doesNotMatch(hostedPage, /<iframe/);
});
