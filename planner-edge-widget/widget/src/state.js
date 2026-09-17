function createInitialState() {
  return { mode: "loading", display: null, error: null, pendingTask: null, completing: false, dialog: null, dialogError: null };
}

function applyDisplayLoaded(state, display) {
  return { ...state, mode: display ? "board" : "noBoardSelected", display, error: null, pendingTask: null, completing: false, dialog: null, dialogError: null };
}

function beginConfirmComplete(state, task) {
  return { ...state, mode: "confirmComplete", pendingTask: task, dialog: { type: "confirmTask", taskId: task.taskId, title: task.title }, dialogError: null };
}

function cancelConfirmComplete(state) {
  return closeDialog(state);
}

function openBoardPicker(state) {
  return { ...state, dialog: { type: "boardPicker" }, dialogError: null };
}

function openTaskDetails(state, taskId) {
  return { ...state, dialog: { type: "taskDetails", taskId, selectedBucketId: null, bucketPickerOpen: false }, dialogError: null };
}

function openTaskBucketPicker(state) {
  if (state.dialog?.type !== "taskDetails") return state;
  return { ...state, dialog: { ...state.dialog, bucketPickerOpen: !state.dialog.bucketPickerOpen }, dialogError: null };
}

function selectTaskBucket(state, bucketId) {
  if (state.dialog?.type !== "taskDetails") return state;
  return { ...state, dialog: { ...state.dialog, selectedBucketId: bucketId, bucketPickerOpen: false }, dialogError: null };
}

function beginConfirmChecklist(state, taskId, itemId, title, returnTo = null) {
  return { ...state, dialog: { type: "confirmChecklist", taskId, itemId, title, returnTo }, dialogError: null };
}

function closeDialog(state) {
  return { ...state, mode: state.display ? "board" : "noBoardSelected", pendingTask: null,
    dialog: null, dialogError: null, completing: false };
}

function applyError(state, error) {
  return { ...state, mode: error.code === "signed_out" ? "signedOut" : "error", error, pendingTask: null, completing: false };
}

globalThis.PlannerState = { createInitialState, applyDisplayLoaded, beginConfirmComplete, cancelConfirmComplete,
  openBoardPicker, openTaskDetails, openTaskBucketPicker, selectTaskBucket, beginConfirmChecklist, closeDialog, applyError };
