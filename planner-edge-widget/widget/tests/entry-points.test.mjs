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
  assert.equal(manifest.version, "0.3.1");
});

test("native and helper-hosted entries load pure widget modules before the app", () => {
  for (const page of ["index.html", "board.html"]) {
    const html = readFileSync(new URL(`../${page}`, import.meta.url), "utf8");
    const filters = html.indexOf('src="src/filters.js"');
    const viewState = html.indexOf('src="src/view-state.js"');
    const app = html.indexOf('src="src/app.js"');

    assert.ok(filters >= 0, `${page} loads filters.js`);
    assert.ok(viewState >= 0, `${page} loads view-state.js`);
    assert.ok(filters < app, `${page} loads filters.js before app.js`);
    assert.ok(viewState < app, `${page} loads view-state.js before app.js`);
  }
});

test("widget package includes every script loaded by the native entry", () => {
  const nativePage = readFileSync(new URL("../index.html", import.meta.url), "utf8");
  const packageScript = readFileSync(new URL("../../scripts/package.ps1", import.meta.url), "utf8");
  const releaseManifest = readFileSync(new URL("../../../scripts/release-manifests.ps1", import.meta.url), "utf8");
  const scripts = [...nativePage.matchAll(/<script src="src\/(.+?\.js)"><\/script>/g)].map(match => match[1]);

  assert.deepEqual(scripts, ["state.js", "api.js", "filters.js", "view-state.js", "app.js"]);
  assert.match(packageScript, /PlannerWidgetManifest/);
  for (const script of scripts)
    assert.match(releaseManifest, new RegExp(`'src/${script.replace(".", "\\.")}'`));
});
