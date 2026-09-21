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
let dialogGeneration = 0;
let taskObserver = null;
let myTasksOnly = false;
let currentUserId = null;
let filterError = null;
let members = null;
let memberPlanId = null;
let memberError = null;
let createNotice = null;

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
  const buckets = myTasksOnly && currentUserId ? board.buckets.map(bucket => ({ ...bucket,
    tasks: bucket.tasks.filter(task => task.assignments?.includes(currentUserId)) })) : board.buckets;
  const previousBoard = app.querySelector?.(".board");
  const sameBoard = previousBoard && previousBoard.dataset.planId === board.planId;
  const boardScrollLeft = sameBoard ? previousBoard.scrollLeft : 0;
  const bucketScrollTops = new Map(Array.from(sameBoard ? app.querySelectorAll?.(".bucket") || [] : [],
    bucket => [bucket.dataset.bucketId, bucket.scrollTop]));
  const detailScrollTop = state.dialog?.type === "taskDetails"
    ? app.querySelector?.(".confirm-panel")?.scrollTop ?? 0 : 0;
  app.innerHTML = `<header class="topbar"><div class="board-heading">
      <button class="board-title" data-open-board-picker title="Choose Planner board">${escapeHtml(board.planTitle)}</button>
      <p>${board.isStale || state.mode === "error" ? "Offline view" : `Synced ${formatTime(board.syncedAt)}`}</p></div>
      <div class="topbar-actions">${state.error || filterError || createNotice ? `<span class="notice">${escapeHtml(state.error?.message || filterError || createNotice)}</span>` : ""}
      <button class="icon-action my-tasks-action ${myTasksOnly ? "active" : ""}" data-toggle-my-tasks aria-label="My tasks" aria-pressed="${myTasksOnly}" title="My tasks">My tasks</button>
      <button class="icon-action" data-new-task aria-label="New task" title="New task">+</button></div></header>
    <section class="board" data-plan-id="${escapeHtml(board.planId)}">${buckets.map(renderBucket).join("")}</section>${renderDialog()}`;
  const renderedBoard = app.querySelector?.(".board");
  if (renderedBoard) renderedBoard.scrollLeft = boardScrollLeft;
  app.querySelectorAll?.(".bucket").forEach(bucket => {
    bucket.scrollTop = bucketScrollTops.get(bucket.dataset.bucketId) ?? 0;
  });
  const detailPanel = state.dialog?.type === "taskDetails" ? app.querySelector?.(".confirm-panel") : null;
  if (detailPanel) detailPanel.scrollTop = detailScrollTop;
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
  return `<article class="bucket" data-bucket-id="${escapeHtml(bucket.bucketId)}"><h2>${escapeHtml(bucket.name)} <span>${bucket.tasks.length}</span></h2>
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
  return `<article class="task" data-open-task="${escapeHtml(task.taskId)}">
    <button class="complete-target" data-complete-task="${escapeHtml(task.taskId)}" title="Complete task"
      aria-label="Complete ${escapeHtml(task.title)}"><span class="checkbox" aria-hidden="true"></span></button>
    <div class="task-content"><button class="task-body" data-open-task="${escapeHtml(task.taskId)}" title="View task details">
      <span class="task-title">${escapeHtml(task.title)}</span>${task.dueDateTime ? `<span class="due">${formatDate(task.dueDateTime)}</span>` : ""}</button>
      <div class="task-supplement">${renderTaskSupplement(task)}</div></div></article>`;
}

function renderTaskSupplement(task) {
  const checklist = details.get(task.taskId)?.checklist || [];
  return `${checklist.length ? `<div class="checklist-preview-list">${checklist.slice(0, 3).map(renderChecklistPreview).join("")}</div>` : ""}
    ${checklist.length > 3 ? `<span class="more-items">+${checklist.length - 3} more</span>` : ""}
    ${failures.has(task.taskId) ? '<span class="detail-warning">Checklist unavailable</span>' : ""}`;
}

function renderCalendar(dialog) {
  const [year, month] = dialog.calendarMonth.split("-").map(Number);
  const firstWeekday = new Date(Date.UTC(year, month - 1, 1)).getUTCDay();
  const days = new Date(Date.UTC(year, month, 0)).getUTCDate();
  const cells = Array.from({ length: firstWeekday }, () => '<span></span>');
  for (let day = 1; day <= days; day++) {
    const date = `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
    cells.push(`<button type="button" data-select-date="${date}" class="${dialog.dateDraft === date ? "selected" : ""}" aria-pressed="${dialog.dateDraft === date}">${day}</button>`);
  }
  return `<div class="calendar"><div class="calendar-nav"><button type="button" data-calendar-month="-1" aria-label="Previous month">&#8249;</button>
    <strong>${new Intl.DateTimeFormat(undefined, { month: "long", year: "numeric", timeZone: "UTC" }).format(new Date(Date.UTC(year, month - 1, 1)))}</strong>
    <button type="button" data-calendar-month="1" aria-label="Next month">&#8250;</button></div>
    <div class="calendar-grid">${["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"].map(day => `<span>${day}</span>`).join("")}${cells.join("")}</div>
    <div class="confirm-actions"><button type="button" data-cancel-date>Cancel</button><button type="button" data-clear-date>Clear date</button><button type="button" class="primary" data-save-date>Save</button></div></div>`;
}

function renderMemberPicker(dialog) {
  if (!dialog.memberPickerOpen) return "";
  if (memberError) return `<p class="dialog-error">${escapeHtml(memberError)}</p><div class="confirm-actions"><button type="button" data-cancel-members>Close picker</button></div>`;
  if (!members) return "<p>Loading board members...</p>";
  const chosen = dialog.assigneeDraft || [];
  return `<div class="member-picker"><div class="member-list">${members.map(member =>
    `<button type="button" data-toggle-member="${escapeHtml(member.id)}" aria-pressed="${chosen.includes(member.id)}"><span class="mini-check ${chosen.includes(member.id) ? "selected" : ""}" aria-hidden="true"></span>${escapeHtml(member.displayName)}</button>`).join("") || '<p>No board members found.</p>'}</div>
    <div class="confirm-actions"><button type="button" data-cancel-members>Cancel</button><button type="button" class="primary" data-save-members>Save</button></div></div>`;
}

function sortChatMessages(messages) {
  const unique = new Map(messages.map(message => [message.id, message]));
  return [...unique.values()].sort((left, right) => {
    const byTime = new Date(left.createdAt || 0) - new Date(right.createdAt || 0);
    return byTime || String(left.id).localeCompare(String(right.id));
  });
}

function renderChat(dialog) {
  const page = dialog.chatPage;
  if (!page) return `<h3>Task chat</h3><p class="section-status" role="status">${escapeHtml(dialog.chatStatus || "Loading comments...")}</p>`;
  if (page.state === "attachment_pending")
    return `<h3>Task chat</h3><p class="section-status" role="status">${escapeHtml(page.message || "Your comment was created. Refresh task details before posting again.")}</p>
      <div class="chat-composer"><textarea data-chat-draft maxlength="4000" disabled aria-label="Submitted comment">${escapeHtml(dialog.chatDraft || "")}</textarea></div>`;
  if (page.state !== "available")
    return `<h3>Task chat</h3><p class="section-status" role="status">${escapeHtml(page.message || "Task chat is unavailable.")}</p>`;
  const messages = sortChatMessages(page.messages || []);
  return `<h3>Task chat</h3>
    <div class="chat-history">${page.nextCursor ? `<button type="button" data-load-earlier-chat ${dialog.chatLoading ? "disabled" : ""}>Load earlier comments</button>` : ""}
      ${messages.length ? messages.map(message => `<article class="chat-message"><p class="chat-meta"><strong>${escapeHtml(message.author || "Unknown")}</strong>${message.createdAt ? ` <time>${escapeHtml(formatChatTime(message.createdAt))}</time>` : ""}</p><p>${escapeHtml(message.body || "")}</p></article>`).join("") : '<p class="empty">No comments yet.</p>'}</div>
    <div class="chat-composer"><textarea data-chat-draft maxlength="4000" aria-label="Add a comment" placeholder="Add a comment">${escapeHtml(dialog.chatDraft || "")}</textarea><button type="button" class="primary" data-post-chat ${dialog.chatPending ? "disabled" : ""}>Post</button></div>
    <p class="section-status" role="status">${escapeHtml(dialog.chatStatus || "")}</p>`;
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
    const selectedBucketName = buckets.find(bucket => bucket.bucketId === selectedBucketId)?.name || "No bucket";
    const movingToDifferentBucket = selectedBucketId && selectedBucketId !== currentBucketId;
    content = `<h2>${escapeHtml(info?.title || task?.title || "Task")}</h2>${info ? `
      <dl class="task-meta"><dt>Due</dt><dd><button type="button" data-open-date-picker ${state.completing ? "disabled" : ""}>${info.dueDateTime ? formatDate(info.dueDateTime) : "No due date"}</button>
      ${dialog.datePickerOpen ? renderCalendar(dialog) : ""}</dd>
      <dt>Assigned to</dt><dd><button type="button" data-open-members ${state.completing ? "disabled" : ""}>${info.assignees?.length ? info.assignees.map(escapeHtml).join(", ") : "Unassigned"}</button>
      ${renderMemberPicker(dialog)}</dd>
      <dt>Bucket</dt><dd><button type="button" class="bucket-picker-trigger" data-open-bucket-picker ${state.completing ? "disabled" : ""}
        aria-expanded="${dialog.bucketPickerOpen ? "true" : "false"}"><span>${escapeHtml(selectedBucketName)}</span><span aria-hidden="true">v</span></button>
        ${dialog.bucketPickerOpen ? `<div class="bucket-option-list" role="listbox" aria-label="Task bucket">${buckets.map(bucket =>
          `<button type="button" data-select-task-bucket="${escapeHtml(bucket.bucketId)}" role="option"
            aria-selected="${bucket.bucketId === selectedBucketId ? "true" : "false"}">${escapeHtml(bucket.name)}</button>`).join("")}</div>` : ""}</dd></dl>
      ${movingToDifferentBucket ? '<div class="confirm-actions move-action"><button class="primary" data-move-task>Move task</button></div>' : ""}
      <h3>Checklist</h3>${info.checklist?.length ? `<div class="detail-checklist">${info.checklist.map(item => renderChecklistButton(dialog.taskId, item, "detail-item")).join("")}</div>` : '<p>No checklist</p>'}
      <h3>Notes</h3><textarea class="notes-editor" data-notes-draft aria-label="Task notes">${escapeHtml(dialog.notesDraft ?? info.description ?? "")}</textarea>
      <div class="notes-actions"><span class="section-status" role="status">${escapeHtml(dialog.notesStatus || "")}</span><button type="button" class="primary" data-save-notes ${dialog.notesPending ? "disabled" : ""}>Save notes</button></div>
      ${renderChat(dialog)}`
      : `<p>${failures.has(dialog.taskId) ? "Task details unavailable." : "Loading task details..."}</p>
      ${failures.has(dialog.taskId) ? `<button data-retry-details="${escapeHtml(dialog.taskId)}">Retry</button>` : ""}`}`;
  } else if (dialog.type === "createTask") {
    const buckets = state.display?.buckets.filter(bucket => bucket.bucketId !== "unbucketed") || [];
    content = `<h2>New task</h2><label class="field-label" for="new-task-title">Title</label>
      <input id="new-task-title" data-new-task-title maxlength="255" value="${escapeHtml(dialog.title || "")}" placeholder="Task title">
      <label class="field-label">Bucket</label><button type="button" class="bucket-picker-trigger" data-new-task-bucket-picker>${escapeHtml(buckets.find(bucket => bucket.bucketId === dialog.bucketId)?.name || "Choose a bucket")}</button>
      ${dialog.bucketPickerOpen ? `<div class="bucket-option-list">${buckets.map(bucket => `<button type="button" data-new-task-bucket="${escapeHtml(bucket.bucketId)}">${escapeHtml(bucket.name)}</button>`).join("")}</div>` : ""}
      <label class="field-label">Due</label><button type="button" data-open-date-picker>${dialog.dateDraft ? escapeHtml(dialog.dateDraft) : "No due date"}</button>
      ${dialog.datePickerOpen ? renderCalendar(dialog) : ""}
      <label class="field-label">Assigned to</label><button type="button" data-open-members>${dialog.selectedAssignees?.length ? `${dialog.selectedAssignees.length} people` : "Unassigned"}</button>
      ${renderMemberPicker(dialog)}
      <div class="confirm-actions"><button type="button" data-close-dialog>Cancel</button><button type="button" class="primary" data-submit-new-task ${state.completing ? "disabled" : ""}>Create task</button></div>`;
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

function showTaskDetails(taskId) {
  state = flow.openTaskDetails(state, taskId);
  const generation = ++dialogGeneration;
  Object.assign(state.dialog, { notesDraft: details.get(taskId)?.description || null, notesStatus: "", notesPending: false,
    chatPage: null, chatDraft: "", chatStatus: "Loading comments...", chatLoading: true, chatPending: false, generation });
  render();
  loadTaskChat(taskId, generation);
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
      if (state.display?.planId === planId && detailGeneration === generation) {
        details.set(taskId, info);
        if (state.dialog?.type === "taskDetails" && state.dialog.taskId === taskId && state.dialog.notesDraft === null)
          state.dialog.notesDraft = info.description || "";
      }
    }).catch(() => {
      if (state.display?.planId === planId && detailGeneration === generation) failures.add(taskId);
    }).finally(() => {
      pending.delete(taskId);
      if (state.display?.planId === planId) {
        const card = Array.from(app.querySelectorAll?.(".task") || [])
          .find(element => element.dataset.openTask === taskId);
        const supplement = card?.querySelector?.(".task-supplement");
        const task = findTask(taskId);
        if (supplement && task) supplement.innerHTML = renderTaskSupplement(task);
        if (state.dialog?.type === "taskDetails" && state.dialog.taskId === taskId) render();
        else if (!supplement) render();
        else queueDetails();
      }
    });
  }
}

app.addEventListener("input", event => {
  if (event.target.matches?.("[data-new-task-title]") && state.dialog?.type === "createTask")
    state.dialog.title = event.target.value;
  if (event.target.matches?.("[data-notes-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.notesDraft = event.target.value;
    state.dialog.notesStatus = "";
  }
  if (event.target.matches?.("[data-chat-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.chatDraft = event.target.value;
    state.dialog.chatStatus = "";
  }
});

app.addEventListener("click", async event => {
  const hit = name => event.target.closest(`[${name}]`);
  if (hit("data-new-task") && !state.dialog) {
    state.dialog = { type: "createTask", title: "", bucketId: state.display?.buckets.find(bucket => bucket.bucketId !== "unbucketed")?.bucketId || "",
      dateDraft: null, selectedAssignees: [], assigneeDraft: [] };
    state.dialogError = null; createNotice = null; render(); return;
  }
  if (hit("data-new-task-bucket-picker") && state.dialog?.type === "createTask") {
    state.dialog.bucketPickerOpen = !state.dialog.bucketPickerOpen; render(); return;
  }
  if (hit("data-new-task-bucket") && state.dialog?.type === "createTask") {
    state.dialog.bucketId = hit("data-new-task-bucket").dataset.newTaskBucket;
    state.dialog.bucketPickerOpen = false; render(); return;
  }
  if (hit("data-open-members") && !state.completing && state.dialog) {
    const dialog = state.dialog;
    dialog.memberPickerOpen = true;
    dialog.assigneeDraft = [...(dialog.type === "createTask" ? dialog.selectedAssignees || [] : details.get(dialog.taskId)?.assigneeIds || [])];
    state.dialogError = null;
    if (memberPlanId !== state.display?.planId) { members = null; memberError = null; memberPlanId = state.display?.planId; }
    if (!members) memberError = null;
    render();
    if (!members && !memberError) {
      try { members = await api.getMembers(); }
      catch (error) { memberError = normalizeError(error).message; }
      if (state.dialog === dialog) render();
    }
    return;
  }
  if (hit("data-toggle-member") && state.dialog?.memberPickerOpen) {
    const id = hit("data-toggle-member").dataset.toggleMember;
    const draft = state.dialog.assigneeDraft;
    state.dialog.assigneeDraft = draft.includes(id) ? draft.filter(value => value !== id) : [...draft, id];
    render(); return;
  }
  if (hit("data-cancel-members") && state.dialog?.memberPickerOpen) {
    state.dialog.memberPickerOpen = false;
    if (state.dialog.type === "taskDetails") state.dialog.assigneeDraft = null;
    render(); return;
  }
  if (hit("data-save-members") && !state.completing && state.dialog?.memberPickerOpen) {
    if (state.dialog.type === "createTask") {
      state.dialog.selectedAssignees = [...state.dialog.assigneeDraft];
      state.dialog.memberPickerOpen = false; render(); return;
    }
    const taskId = state.dialog.taskId;
    state.completing = true; render();
    try { await api.setAssignments(taskId, state.dialog.assigneeDraft); details.delete(taskId); failures.delete(taskId); await loadDisplay(true); }
    catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
    return;
  }
  if (hit("data-submit-new-task") && !state.completing && state.dialog?.type === "createTask") {
    const { title, bucketId, dateDraft, selectedAssignees } = state.dialog;
    if (!title?.trim()) { state.dialogError = "Enter a task title."; render(); return; }
    if (!bucketId) { state.dialogError = "Choose a bucket."; render(); return; }
    state.completing = true; render();
    try { await api.createTask({ title: title.trim(), bucketId, date: dateDraft, userIds: selectedAssignees }); }
    catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); return; }
    state = flow.closeDialog(state);
    if (myTasksOnly && !selectedAssignees.includes(currentUserId)) createNotice = "Task created. It is hidden by My tasks.";
    await loadDisplay(true);
    return;
  }
  if (hit("data-toggle-my-tasks")) {
    if (!myTasksOnly && !currentUserId) {
      try { currentUserId = (await api.getCurrentUser()).userId; }
      catch (error) { filterError = normalizeError(error).message; render(); return; }
    }
    filterError = null;
    myTasksOnly = !myTasksOnly; createNotice = null; visibleTasks.clear(); render(); return;
  }
  if (event.target.matches?.("[data-dialog-backdrop]")) {
    if (!state.completing) { dialogGeneration++; state = flow.closeDialog(state); render(); }
    return;
  }
  if (hit("data-close-dialog")) { if (!state.completing) { dialogGeneration++; state = flow.closeDialog(state); render(); } return; }
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
      details.clear(); failures.clear(); members = null; memberError = null; memberPlanId = null;
      createNotice = null; filterError = null;
      state = flow.closeDialog(state);
      await loadDisplay(true);
    } catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
    return;
  }
  if (hit("data-open-bucket-picker") && !state.completing && state.dialog?.type === "taskDetails") {
    state = flow.openTaskBucketPicker(state); render(); return;
  }
  if (hit("data-open-date-picker") && !state.completing && ["taskDetails", "createTask"].includes(state.dialog?.type)) {
    const due = state.dialog.type === "taskDetails" ? details.get(state.dialog.taskId)?.dueDateTime : null;
    const dateDraft = state.dialog.type === "createTask" ? state.dialog.dateDraft : due ? due.slice(0, 10) : null;
    state.dialog = { ...state.dialog, datePickerOpen: true, dateDraft,
      originalDate: dateDraft,
      calendarMonth: (dateDraft || new Date().toISOString().slice(0, 10)).slice(0, 7) };
    render(); return;
  }
  if (hit("data-calendar-month") && state.dialog?.datePickerOpen) {
    const [year, month] = state.dialog.calendarMonth.split("-").map(Number);
    const next = new Date(Date.UTC(year, month - 1 + Number(hit("data-calendar-month").dataset.calendarMonth), 1));
    state.dialog.calendarMonth = next.toISOString().slice(0, 7); render(); return;
  }
  if (hit("data-select-date") && state.dialog?.datePickerOpen) {
    state.dialog.dateDraft = hit("data-select-date").dataset.selectDate; render(); return;
  }
  if (hit("data-cancel-date") && state.dialog?.datePickerOpen) {
    if (state.dialog.type === "createTask") state.dialog.dateDraft = state.dialog.originalDate;
    state.dialog.datePickerOpen = false; render(); return;
  }
  if ((hit("data-save-date") || hit("data-clear-date")) && !state.completing && state.dialog?.datePickerOpen) {
    const taskId = state.dialog.taskId;
    const date = hit("data-clear-date") ? null : state.dialog.dateDraft;
    if (!date && !hit("data-clear-date")) return;
    if (state.dialog.type === "createTask") {
      state.dialog.dateDraft = date; state.dialog.datePickerOpen = false; render(); return;
    }
    state.completing = true; render();
    try { await api.setDueDate(taskId, date); details.delete(taskId); failures.delete(taskId); await loadDisplay(true); }
    catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
    return;
  }
  const selectedBucket = hit("data-select-task-bucket");
  if (selectedBucket && !state.completing && state.dialog?.type === "taskDetails") {
    state = flow.selectTaskBucket(state, selectedBucket.dataset.selectTaskBucket); render(); return;
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
  if (open && !state.dialog) {
    showTaskDetails(open.dataset.openTask);
    return;
  }
  if (hit("data-save-notes") && state.dialog?.type === "taskDetails" && !state.dialog.notesPending) {
    const dialog = state.dialog;
    const { taskId, generation } = dialog;
    const description = dialog.notesDraft ?? "";
    dialog.notesPending = true; dialog.notesStatus = "Saving notes..."; render();
    try {
      await api.updateNotes(taskId, description);
      if (!isCurrentTaskDialog(taskId, generation)) return;
      const info = details.get(taskId);
      if (info) details.set(taskId, { ...info, description });
      const hasNewerDraft = dialog.notesDraft !== description;
      if (!hasNewerDraft) dialog.notesDraft = null;
      dialog.notesPending = false;
      dialog.notesStatus = hasNewerDraft ? "Earlier notes saved. Save current changes." : "Notes saved.";
      render();
    } catch (error) {
      if (!isCurrentTaskDialog(taskId, generation)) return;
      dialog.notesPending = false; dialog.notesStatus = normalizeError(error).message; render();
    }
    return;
  }
  if (hit("data-load-earlier-chat") && state.dialog?.type === "taskDetails" && !state.dialog.chatLoading) {
    const dialog = state.dialog;
    if (dialog.chatPage?.nextCursor) loadTaskChat(dialog.taskId, dialog.generation, dialog.chatPage.nextCursor, true);
    return;
  }
  if (hit("data-post-chat") && state.dialog?.type === "taskDetails" && !state.dialog.chatPending) {
    const dialog = state.dialog;
    const { taskId, generation } = dialog;
    const message = dialog.chatDraft;
    if (!message.trim()) { dialog.chatStatus = "Enter a comment."; render(); return; }
    dialog.chatPending = true; dialog.chatStatus = "Posting comment..."; render();
    try {
      const page = await api.postTaskChat(taskId, message);
      if (!isCurrentTaskDialog(taskId, generation)) return;
      dialog.chatPage = { ...page, messages: sortChatMessages(page.messages || []) };
      if (page.state === "available" && dialog.chatDraft === message) dialog.chatDraft = "";
      dialog.chatPending = false; dialog.chatStatus = "Comment posted."; render();
    } catch (error) {
      if (!isCurrentTaskDialog(taskId, generation)) return;
      dialog.chatPending = false; dialog.chatStatus = normalizeError(error).message; render();
    }
    return;
  }
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
      state = flow.closeDialog(state);
      if (returnTo === "taskDetails") showTaskDetails(taskId);
      else render();
    } catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); }
  }
});

function normalizeError(error) {
  return { code: error?.code || "network_unavailable", message: error?.message || "The local helper is unavailable." };
}
function isCurrentTaskDialog(taskId, generation) {
  return state.dialog?.type === "taskDetails" && state.dialog.taskId === taskId &&
    state.dialog.generation === generation && dialogGeneration === generation;
}
async function loadTaskChat(taskId, generation, cursor = null, prepend = false) {
  const dialog = state.dialog;
  if (!isCurrentTaskDialog(taskId, generation)) return;
  dialog.chatLoading = true;
  if (prepend) { dialog.chatStatus = "Loading earlier comments..."; render(); }
  try {
    const page = await api.getTaskChat(taskId, cursor);
    if (!isCurrentTaskDialog(taskId, generation)) return;
    const current = prepend ? dialog.chatPage?.messages || [] : [];
    dialog.chatPage = { ...page, messages: sortChatMessages([...(page.messages || []), ...current]) };
    dialog.chatLoading = false; dialog.chatStatus = ""; render();
  } catch (error) {
    if (!isCurrentTaskDialog(taskId, generation)) return;
    dialog.chatLoading = false; dialog.chatStatus = normalizeError(error).message; render();
  }
}
function formatTime(value) {
  return new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(value));
}
function formatChatTime(value) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));
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
