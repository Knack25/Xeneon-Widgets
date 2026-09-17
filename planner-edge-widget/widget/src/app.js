(() => {
const { completeTask, getDisplay } = globalThis.PlannerApi;
const { applyDisplayLoaded, applyError, beginConfirmComplete, cancelConfirmComplete, createInitialState } = globalThis.PlannerState;

const app = document.getElementById("app");
let state = createInitialState();

async function loadDisplay() {
  if (state.mode === "confirmComplete" || state.completing) return;
  try { state = applyDisplayLoaded(state, await getDisplay()); }
  catch (error) { state = applyError(state, normalizeError(error)); }
  render();
}

function render() {
  if (state.mode === "loading") return;
  if (state.mode === "signedOut") {
    app.innerHTML = '<section class="status"><h1>Sign in to Planner</h1><p>Open the Planner Edge setup page on your computer.</p></section>';
    return;
  }
  if (state.mode === "noBoardSelected") {
    app.innerHTML = '<section class="status"><h1>Select a board</h1><p>Open the Planner Edge setup page on your computer.</p></section>';
    return;
  }
  if (state.mode === "error" && !state.display) {
    app.innerHTML = `<section class="status"><h1>Planner unavailable</h1><p>${escapeHtml(state.error.message)}</p></section>`;
    return;
  }
  renderBoard();
}

function renderBoard() {
  const display = state.display;
  const stale = display.isStale || state.mode === "error";
  app.innerHTML = `
    <header class="topbar">
      <div><h1>${escapeHtml(display.planTitle)}</h1><p>${stale ? "Offline view" : `Synced ${formatTime(display.syncedAt)}`}</p></div>
      ${state.error ? `<span class="notice">${escapeHtml(state.error.message)}</span>` : ""}
    </header>
    <section class="board">${display.buckets.map(renderBucket).join("")}</section>
    ${state.mode === "confirmComplete" ? renderConfirm() : ""}`;

  app.querySelectorAll("[data-task-id]").forEach(button => button.addEventListener("click", () => {
    const task = display.buckets.flatMap(bucket => bucket.tasks).find(item => item.taskId === button.dataset.taskId);
    if (!task) return;
    state = beginConfirmComplete(state, task);
    render();
  }));
  app.querySelector("[data-cancel]")?.addEventListener("click", () => {
    state = cancelConfirmComplete(state);
    render();
  });
  app.querySelector("[data-confirm]")?.addEventListener("click", async event => {
    if (state.completing) return;
    state.completing = true;
    event.currentTarget.disabled = true;
    try {
      await completeTask(state.pendingTask.taskId);
      state = createInitialState();
      await loadDisplay();
    } catch (error) {
      state = applyError(state, normalizeError(error));
      render();
    }
  });
}

function renderBucket(bucket) {
  return `<article class="bucket"><h2>${escapeHtml(bucket.name)} <span>${bucket.tasks.length}</span></h2>
    <div class="tasks">${bucket.tasks.map(renderTask).join("") || '<p class="empty">Clear</p>'}</div></article>`;
}

function renderTask(task) {
  return `<button class="task" data-task-id="${escapeHtml(task.taskId)}" aria-label="Complete ${escapeHtml(task.title)}">
    <span class="checkbox"></span><span class="task-title">${escapeHtml(task.title)}</span>
    ${task.dueDateTime ? `<span class="due">${formatDate(task.dueDateTime)}</span>` : ""}</button>`;
}

function renderConfirm() {
  return `<section class="confirm" role="dialog" aria-modal="true" aria-label="Complete task">
    <div class="confirm-panel"><h2>Complete task?</h2><p>${escapeHtml(state.pendingTask.title)}</p>
    <div class="confirm-actions"><button data-cancel>Cancel</button><button data-confirm>Complete</button></div></div></section>`;
}

function normalizeError(error) {
  return { code: error.code || "network_unavailable", message: error.message || "The local helper is unavailable." };
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
