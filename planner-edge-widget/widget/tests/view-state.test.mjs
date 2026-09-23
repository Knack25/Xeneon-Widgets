import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

function loadModule(initial = {}) {
  const values = new Map(Object.entries(initial));
  const localStorage = {
    getItem(key) { return values.has(key) ? values.get(key) : null; },
    setItem(key, value) { values.set(key, value); },
    removeItem(key) { values.delete(key); },
    key(index) { return [...values.keys()][index] ?? null; },
    get length() { return values.size; }
  };
  const context = { localStorage };
  runInNewContext(readFileSync(new URL("../src/view-state.js", import.meta.url), "utf8"), context);
  return { viewState: context.PlannerViewState, values };
}

test("malformed or missing scroll state falls back to zero", () => {
  const { viewState } = loadModule({
    "planner-edge:view:bad-json": "{broken",
    "planner-edge:view:bad-shape": JSON.stringify({ boardScrollLeft: -20, bucketScrollTops: { one: -5, two: "12" } })
  });

  assert.deepEqual(JSON.parse(JSON.stringify(viewState.load("missing"))), {
    boardScrollLeft: 0, bucketScrollTops: {}
  });
  assert.deepEqual(JSON.parse(JSON.stringify(viewState.load("bad-json"))), {
    boardScrollLeft: 0, bucketScrollTops: {}
  });
  assert.deepEqual(JSON.parse(JSON.stringify(viewState.load("bad-shape"))), {
    boardScrollLeft: 0, bucketScrollTops: { one: 0, two: 0 }
  });
});

test("save stores only finite nonnegative scroll values under the plan key", () => {
  const { viewState, values } = loadModule();

  viewState.save("plan-a", {
    boardScrollLeft: 240,
    bucketScrollTops: { one: 80, two: -1, three: Number.POSITIVE_INFINITY },
    searchText: "never store this",
    task: { title: "never store Planner content" }
  });

  assert.equal(values.size, 1);
  assert.deepEqual(JSON.parse(values.get("planner-edge:view:plan-a")), {
    boardScrollLeft: 240,
    bucketScrollTops: { one: 80, two: 0, three: 0 }
  });
});

test("scroll state is isolated by plan", () => {
  const { viewState } = loadModule();
  viewState.save("plan-a", { boardScrollLeft: 10, bucketScrollTops: { one: 20 } });
  viewState.save("plan-b", { boardScrollLeft: 30, bucketScrollTops: { two: 40 } });

  assert.deepEqual(JSON.parse(JSON.stringify(viewState.load("plan-a"))), {
    boardScrollLeft: 10, bucketScrollTops: { one: 20 }
  });
  assert.deepEqual(JSON.parse(JSON.stringify(viewState.load("plan-b"))), {
    boardScrollLeft: 30, bucketScrollTops: { two: 40 }
  });
});

test("authorization reset removes every Planner scroll key and preserves unrelated storage", () => {
  const { viewState, values } = loadModule({ unrelated: "keep" });
  viewState.save("plan-a", { boardScrollLeft: 10, bucketScrollTops: { one: 20 } });
  viewState.save("plan-b", { boardScrollLeft: 30, bucketScrollTops: { two: 40 } });

  viewState.clear();

  assert.deepEqual([...values.entries()], [["unrelated", "keep"]]);
});
