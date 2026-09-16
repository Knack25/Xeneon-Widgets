import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const context = {};
runInNewContext(readFileSync(new URL("../src/state.js", import.meta.url), "utf8"), context);
const { applyDisplayLoaded, applyError, beginConfirmComplete, cancelConfirmComplete, createInitialState } = context.PlannerState;

test("display load selects board or no-board state", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Launch", buckets: [] });
  assert.equal(board.mode, "board");
  assert.equal(applyDisplayLoaded(board, null).mode, "noBoardSelected");
});

test("completion confirmation can be cancelled", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Launch", buckets: [] });
  const confirming = beginConfirmComplete(board, { taskId: "one", title: "Finish" });
  assert.equal(confirming.pendingTask.taskId, "one");
  assert.equal(cancelConfirmComplete(confirming).mode, "board");
});

test("signed out has distinct state and other errors keep display", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Launch", buckets: [] });
  assert.equal(applyError(board, { code: "signed_out" }).mode, "signedOut");
  const failed = applyError(board, { code: "network_unavailable" });
  assert.equal(failed.mode, "error");
  assert.equal(failed.display.planTitle, "Launch");
});
