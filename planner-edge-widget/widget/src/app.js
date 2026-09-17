(() => {
const api = globalThis.PlannerApi;
const flow = globalThis.PlannerState;
const app = document.getElementById("app");
const details = new Map();
const failures = new Set();
const pending = new Set();
const visibleTasks = new Set();
let state = flow.createInitialState();
let plans = null;
let detailGeneration = 0;
let taskObserver = null;

async function loadDisplay(force = false) {
  if (!force && (state.dialog || state.completing)) return;
  try {
    const board = await api.getDisplay();
    detailGeneration++;
    details.clear(); failures.clear(); visibleTasks.clear();
    state = flow.applyDisplayLoaded(state, board);
  } catch (error) { state = flow.applyError(state, normalizeError(error)); }
  render();
}

function render() {
  if (state.mode === "loading") return;
  if (state.mode === "signedOut") {
    app.innerHTML = '<section class="status"><h1>Sign in to Planner</h1><p>Open the Planner Edge setup page on your computer.</p></section>';
    return;
  }
  if (state.mode === "error" && !state.display) {
    app.innerHTML = `<section class="status"><h1>Planner unavailable</h1><p>${escapeHtml(state.error.message)}</p></section>`;
    return;
  }
  if (!state.display) {
    app.innerHTML = `<section class="status"><h1>No board selected</h1><button class="primary" data-open-board-picker>Choose a board</button></section>${renderDialog()}`;
    return;
  }
  const board = state.display;
  app.innerHTML = `<header class="topbar"><div class="board-heading">
      <button class="board-title" data-open-board-picker title="Choose Planner board">${escapeHtml(board.planTitle)}</button>
      <p>${board.isStale || state.mode === "error" ? "Offline view" : `Synced ${formatTime(board.syncedAt)}`}</p></div>
      ${state.error ? `<span class="notice">${escapeHtml(state.error.message)}</span>` : ""}</header>
    <section class="board">${board.buckets.map(renderBucket).join("")}</section>${renderDialog()}`;
  observeTasks();
}

function observeTasks() {
  taskObserver?.disconnect();
  if (typeof IntersectionObserver !== "function" || !app.querySelectorAll) {
    state.display?.buckets.flatMap(bucket => bucket.tasks).forEach(task => visibleTasks.add(task.taskId));
    queueDetails();
    return;
  }
  taskObserver = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (entry.isIntersecting) visibleTasks.add(entry.target.dataset.openTask);
    }
    queueDetails();
  }, { rootMargin: "80px" });
  app.querySelectorAll(".task").forEach(task => taskObserver.observe(task));
  queueDetails();
}

function renderBucket(bucket) {
  return `<article class="bucket"><h2>${escapeHtml(bucket.name)} <span>${bucket.tasks.length}</span></h2>
    <div class="tasks">${bucket.tasks.map(renderTask).join("") || '<p class="empty">Clear</p>'}</div></article>`;
}

function renderChecklistButton(taskId, item, cssClass) {
  return `<button class="${cssClass} ${item.isChecked ? "checked" : ""}" data-checklist-item="${escapeHtml(item.itemId)}"
    data-checklist-task="${escapeHtml(taskId)}" ${item.isChecked ? "disabled" : ""}
    title="${item.isChecked ? "Completed" : "Complete checklist item"}"><span class="mini-check" aria-hidden="true"></span><span>${escapeHtml(item.title)}</span></button>`;
}

function renderChecklistPreview(item) {
  return `<span class="checklist-preview ${item.isChecked ? "checked" : ""}"><span class="mini-check" aria-hidden="true"></span><span>${escapeHtml(item.title)}</span></span>`;
}

function renderTask(task) {
  const checklist = details.get(task.taskId)?.checklist || [];
  return `<article class="task" data-open-task="${escapeHtml(task.taskId)}">
    <button class="complete-target" data-complete-task="${escapeHtml(task.taskId)}" title="Complete task"
      aria-label="Complete ${escapeHtml(task.title)}"><span class="checkbox" aria-hidden="true"></span></button>
    <div class="task-content"><button class="task-body" data-open-task="${escapeHtml(task.taskId)}" title="View task details">
      <span class="task-title">${escapeHtml(task.title)}</span>${task.dueDateTime ? `<span class="due">${formatDate(task.dueDateTime)}</span>` : ""}</button>
      ${checklist.length ? `<div class="checklist-preview-list">${checklist.slice(0, 3).map(renderChecklistPreview).join("")}</div>` : ""}
      ${checklist.length > 3 ? `<span class="more-items">+${checklist.length - 3} more</span>` : ""}
      ${failures.has(task.taskId) ? '<span class="detail-warning">Checklist unavailable</span>' : ""}</div></article>`;
}

function renderDialog() {
  const dialog = state.dialog;
  if (!dialog) return "";
  let content = "";
  if (dialog.type === "boardPicker") {
    content = `<h2>Choose board</h2>${plans === null ? '<p>Loading boards...</p>' : ""}
      ${plans?.length ? `<div class="plan-list">${plans.map(plan => `<button data-select-plan="${escapeHtml(plan.planId)}"
      ${plan.planId === state.display?.planId ? "disabled" : ""}><span>${escapeHtml(plan.title)}</span><small>${escapeHtml(plan.groupName || "")}</small></button>`).join("")}</div>` : ""}
      ${plans && !plans.length ? '<p>No boards available.</p>' : ""}`;
  } else if (dialog.type === "taskDetails") {
    const info = details.get(dialog.taskId);
    const task = findTask(dialog.taskId);
    const buckets = state.display?.buckets || [];
    const currentBucketId = info?.bucketId ?? task?.bucketId ?? "";
    const selectedBucketId = dialog.selectedBucketId ?? currentBucketId;
    const movingToDifferentBucket = selectedBucketId && selectedBucketId !== currentBucketId;
    content = `<h2>${escapeHtml(info?.title || task?.title || "Task")}</h2>${info ? `
      <dl class="task-meta"><dt>Due</dt><dd>${info.dueDateTime ? formatDate(info.dueDateTime) : "No due date"}</dd>
      <dt>Assigned to</dt><dd>${info.assignees?.length ? info.assignees.map(escapeHtml).join(", ") : "Unassigned"}</dd>
      <dt>Bucket</dt><dd><select class="bucket-select" data-task-bucket ${state.completing ? "disabled" : ""}>${buckets.map(bucket =>
        `<option value="${escapeHtml(bucket.bucketId)}" ${bucket.bucketId === selectedBucketId ? "selected" : ""}>${escapeHtml(bucket.name)}</option>`).join("")}</select></dd></dl>
      ${movingToDifferentBucket ? '<div class="confirm-actions move-action"><button class="primary" data-move-task>Move task</button></div>' : ""}
      <h3>Checklist</h3>${info.checklist?.length ? `<div class="detail-checklist">${info.checklist.map(item => renderChecklistButton(dialog.taskId, item, "detail-item")).join("")}</div>` : '<p>No checklist</p>'}`
      : `<p>${failures.has(dialog.taskId) ? "Task details unavailable." : "Loading task details..."}</p>
      ${failures.has(dialog.taskId) ? `<button data-retry-details="${escapeHtml(dialog.taskId)}">Retry</button>` : ""}`}`;
  } else if (dialog.type === "confirmTask" || dialog.type === "confirmChecklist") {
    content = `<h2>Complete ${dialog.type === "confirmTask" ? "task" : "checklist item"}?</h2><p>${escapeHtml(dialog.title)}</p>
      <div class="confirm-actions"><button data-close-dialog ${state.completing ? "disabled" : ""}>Cancel</button>
      <button class="primary" ${dialog.type === "confirmTask" ? "data-confirm-task" : "data-confirm-checklist"}
        ${state.completing ? "disabled" : ""}>Complete</button></div>`;
  }
  return `<section class="confirm" data-dialog-backdrop role="dialog" aria-modal="true"><div class="confirm-panel">${content}
    ${state.dialogError ? `<p class="dialog-error">${escapeHtml(state.dialogError)}</p>` : ""}
    ${dialog.type === "boardPicker" || dialog.type === "taskDetails" ? '<div class="confirm-actions"><button data-close-dialog>Close</button></div>' : ""}</div></section>`;
}

function findTask(taskId) {
  return state.display?.buckets.flatMap(bucket => bucket.tasks).find(task => task.taskId === taskId);
}

function queueDetails() {
  const board = state.display;
  if (!board) return;
  const planId = board.planId;
  const generation = detailGeneration;
  const ids = [state.dialog?.taskId, ...visibleTasks].filter(Boolean);
  while (pending.size < 2) {
    const taskId = ids.find(id => !details.has(id) && !failures.has(id) && !pending.has(id));
    if (!taskId) break;
    pending.add(taskId);
    api.getTaskDetails(taskId).then(info => {
      if (state.display?.planId === planId && detailGeneration === generation) details.set(taskId, info);
    }).catch(() => {
      if (state.display?.planId === planId && detailGeneration === generation) failures.add(taskId);
    }).finally(() => {
      pending.delete(taskId);
      if (state.display?.planId === planId) render();
    });
  }
}

app.addEventListener("click", async event => {
  const hit = name => event.target.closest(`[${name}]`);
  if (event.target.matches?.("[data-dialog-backdrop]")) {
    if (!state.completing) { state = flow.closeDialog(state); render(); }
    return;
  }
  if (hit("data-close-dialog")) { if (!state.completing) { state = flow.closeDialog(state); render(); } return; }
  if (hit("data-open-board-picker")) {
    state = flow.openBoardPicker(state); plans = null; render();
    try { plans = await api.getPlans(); }
    catch (error) { plans = []; state.dialogError = normalizeError(error).message; }
    render(); return;
  }
  const chosen = hit("data-select-plan");
  if (chosen) {
    if (state.completing) return;
    state.completing = true; render();
    try {
      await api.selectPlan(chosen.dataset.selectPlan);
      details.clear(); failures.clear(); state = flow.closeDialog(state);
      await loadDisplay(true);
    } catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
    return;
  }
  if (hit("data-move-task") && !state.completing && state.dialog?.type === "taskDetails") {
    const { taskId, selectedBucketId } = state.dialog;
    if (!selectedBucketId) return;
    state.completing = true; render();
    try {
      await api.moveTask(taskId, selectedBucketId);
      details.delete(taskId); failures.delete(taskId);
      await loadDisplay(true);
    } catch (error) {
      state.completing = false;
      state.dialogError = normalizeError(error).message;
      render();
    }
    return;
  }
  const itemButton = hit("data-checklist-item");
  if (itemButton && !itemButton.disabled) {
    const taskId = itemButton.dataset.checklistTask;
    const item = details.get(taskId)?.checklist.find(value => value.itemId === itemButton.dataset.checklistItem);
    if (item && !item.isChecked) {
      state = flow.beginConfirmChecklist(state, taskId, item.itemId, item.title,
        state.dialog?.type === "taskDetails" ? "taskDetails" : null);
      render();
    }
    return;
  }
  const complete = hit("data-complete-task");
  if (complete) {
    const task = findTask(complete.dataset.completeTask);
    if (task) { state = flow.beginConfirmComplete(state, task); render(); }
    return;
  }
  const retry = hit("data-retry-details");
  if (retry) { failures.delete(retry.dataset.retryDetails); render(); return; }
  const open = hit("data-open-task");
  if (open && !state.dialog) { state = flow.openTaskDetails(state, open.dataset.openTask); render(); return; }
  if (hit("data-confirm-task") && !state.completing) {
    state.completing = true; render();
    try { await api.completeTask(state.dialog.taskId); state = flow.closeDialog(state); await loadDisplay(true); }
    catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
    return;
  }
  if (hit("data-confirm-checklist") && !state.completing) {
    const { taskId, itemId, returnTo } = state.dialog;
    state.completing = true; render();
    try {
      await api.completeChecklistItem(taskId, itemId);
      details.delete(taskId); failures.delete(taskId);
      state = returnTo === "taskDetails" ? flow.openTaskDetails(flow.closeDialog(state), taskId) : flow.closeDialog(state);
      render();
    } catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
  }
});

app.addEventListener("change", event => {
  if (!event.target.matches?.("[data-task-bucket]") || state.completing) return;
  state = flow.selectTaskBucket(state, event.target.value);
  render();
});

function normalizeError(error) {
  return { code: error?.code || "network_unavailable", message: error?.message || "The local helper is unavailable." };
}
function formatTime(value) {
  return new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(value));
}
function formatDate(value) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(new Date(value));
}
function escapeHtml(value) {
  return String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#039;");
}

loadDisplay();
setInterval(loadDisplay, 60000);
})();
