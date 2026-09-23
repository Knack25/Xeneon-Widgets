import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const context = {};
runInNewContext(readFileSync(new URL("../src/state.js", import.meta.url), "utf8"), context);
const { applyDisplayLoaded, applyDisplayRefresh, applyError, beginConfirmComplete, cancelConfirmComplete, createInitialState,
  openBoardPicker, openTaskDetails, beginConfirmChecklist, closeDialog, openTaskBucketPicker,
  createAuthorizationLifecycle } = context.PlannerState;

test("authorization lifecycle aborts requests and invalidates every prior ticket", () => {
  const lifecycle = createAuthorizationLifecycle();
  const board = lifecycle.beginRequest();
  const details = lifecycle.beginRequest();

  assert.equal(lifecycle.isCurrent(board), true);
  lifecycle.clearAuthorization();

  assert.equal(board.signal.aborted, true);
  assert.equal(details.signal.aborted, true);
  assert.equal(lifecycle.isCurrent(board), false);
  assert.equal(lifecycle.isCurrent(lifecycle.beginRequest()), true);
});

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
  const picker = openTaskBucketPicker(details);
  assert.equal(picker.dialog.bucketPickerOpen, true);
  const selected = context.PlannerState.selectTaskBucket(picker, "target");
  assert.equal(selected.dialog.selectedBucketId, "target");
  assert.equal(selected.dialog.bucketPickerOpen, false);
  assert.equal(closeDialog(selected).dialog, null);
});

test("display refresh replaces board data without discarding interaction state", () => {
  const dialog = { type: "taskDetails", taskId: "task", titleDraft: "Draft title", notesDraft: "Draft notes" };
  const current = { ...applyDisplayLoaded(createInitialState(), { planId: "plan", planTitle: "Cached", buckets: [] }),
    dialog, dialogError: "Keep this context", pendingTask: { taskId: "task" }, completing: true };

  const refreshed = applyDisplayRefresh(current, { planId: "plan", planTitle: "Live", buckets: [] });

  assert.equal(refreshed.mode, "board");
  assert.equal(refreshed.display.planTitle, "Live");
  assert.equal(refreshed.dialog, dialog);
  assert.equal(refreshed.dialog.titleDraft, "Draft title");
  assert.equal(refreshed.dialog.notesDraft, "Draft notes");
  assert.equal(refreshed.error, null);
  assert.equal(refreshed.pendingTask, null);
  assert.equal(refreshed.completing, false);
});

test("checklist deletion confirmation preserves the task details dialog", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Work", buckets: [] });
  const details = openTaskDetails(board, "task");
  details.dialog.checklistEditId = "item";
  details.dialog.checklistEditDraft = "Draft title";
  const confirming = context.PlannerState.beginConfirmChecklistDelete(details, "task", "item", "Remove me");

  assert.equal(confirming.dialog.type, "confirmChecklistDelete");
  assert.equal(confirming.dialog.returnDialog.checklistEditDraft, "Draft title");
  assert.equal(context.PlannerState.closeActiveDialog(confirming).dialog.type, "taskDetails");
});
