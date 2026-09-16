function createInitialState() {
  return { mode: "loading", display: null, error: null, pendingTask: null, completing: false };
}

function applyDisplayLoaded(state, display) {
  return { ...state, mode: display ? "board" : "noBoardSelected", display, error: null, pendingTask: null, completing: false };
}

function beginConfirmComplete(state, task) {
  return { ...state, mode: "confirmComplete", pendingTask: task };
}

function cancelConfirmComplete(state) {
  return { ...state, mode: state.display ? "board" : "noBoardSelected", pendingTask: null, completing: false };
}

function applyError(state, error) {
  return { ...state, mode: error.code === "signed_out" ? "signedOut" : "error", error, pendingTask: null, completing: false };
}

globalThis.PlannerState = { createInitialState, applyDisplayLoaded, beginConfirmComplete, cancelConfirmComplete, applyError };
