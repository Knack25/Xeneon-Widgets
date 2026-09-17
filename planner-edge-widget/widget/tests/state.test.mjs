import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const context = {};
runInNewContext(readFileSync(new URL("../src/state.js", import.meta.url), "utf8"), context);
const { applyDisplayLoaded, applyError, beginConfirmComplete, cancelConfirmComplete, createInitialState,
  openBoardPicker, openTaskDetails, beginConfirmChecklist, closeDialog } = context.PlannerState;

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

test("picker, details, and checklist confirmation are distinct dialogs", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Work", buckets: [] });
  assert.equal(openBoardPicker(board).dialog.type, "boardPicker");
  assert.equal(openTaskDetails(board, "task").dialog.type, "taskDetails");
  const confirming = beginConfirmChecklist(board, "task", "item", "Check item", "taskDetails");
  assert.equal(confirming.dialog.type, "confirmChecklist");
  assert.equal(closeDialog(confirming).dialog, null);
});

test("task details track a pending bucket choice", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Work", buckets: [] });
  const details = openTaskDetails(board, "task");
  const selected = context.PlannerState.selectTaskBucket(details, "target");
  assert.equal(selected.dialog.selectedBucketId, "target");
  assert.equal(closeDialog(selected).dialog, null);
});
