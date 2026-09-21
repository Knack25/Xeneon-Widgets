(() => {
const api = globalThis.PlannerApi;
const flow = globalThis.PlannerState;
const filterEngine = globalThis.PlannerFilters;
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
let preferences = filterEngine?.createDefaultPreferences() || { myTasks: false, filters: {} };
let preferencesPlanId = null;
let activePreferencesPlanId = null;
let preferenceLoadGeneration = 0;
let searchText = "";
let searchOpen = false;
let currentUserId = null;
let filterError = null;
let members = null;
let memberPlanId = null;
let memberError = null;
let memberRequest = null;
let memberRequestGeneration = 0;
let createNotice = null;

async function loadDisplay(force = false) {
  if (!force && (state.dialog || state.completing)) return;
  try {
    const board = await api.getDisplay();
    detailGeneration++;
    details.clear(); failures.clear(); visibleTasks.clear();
    state = flow.applyDisplayLoaded(state, board);
    if (board && filterEngine && preferencesPlanId !== board.planId) await loadPreferences(board);
  } catch (error) { state = flow.applyError(state, normalizeError(error)); }
  render();
}

async function loadPreferences(board) {
  const planId = board.planId;
  const generation = ++preferenceLoadGeneration;
  const defaults = filterEngine.sanitizePreferences(filterEngine.createDefaultPreferences(), board, null);
  if (activePreferencesPlanId !== planId) {
    preferences = defaults;
    activePreferencesPlanId = planId;
  }
  preferencesPlanId = null;
  try {
    const saved = await api.getViewPreferences(planId);
    if (!isCurrentPreferenceLoad(planId, generation)) return;
    const hasAssigneeFilters = Array.isArray(saved?.filters?.assigneeIds) && saved.filters.assigneeIds.length > 0;
    const planMembers = hasAssigneeFilters ? await loadMembersForPlan(planId) : null;
    if (!isCurrentPreferenceLoad(planId, generation) || hasAssigneeFilters && !planMembers) return;
    const sanitized = filterEngine.sanitizePreferences(saved, board, planMembers);
    if (JSON.stringify(sanitized) !== JSON.stringify(saved)) {
      await api.saveViewPreferences(planId, sanitized);
      if (!isCurrentPreferenceLoad(planId, generation)) return;
    }
    if (sanitized.myTasks && !currentUserId) {
      currentUserId = (await api.getCurrentUser()).userId;
      if (!isCurrentPreferenceLoad(planId, generation)) return;
    }
    preferences = sanitized;
    activePreferencesPlanId = planId;
    preferencesPlanId = planId;
    filterError = null;
  } catch (error) {
    if (!isCurrentPreferenceLoad(planId, generation)) return;
    if (activePreferencesPlanId !== planId) {
      preferences = defaults;
      activePreferencesPlanId = planId;
    }
    preferencesPlanId = null;
    filterError = normalizeError(error).message;
  }
}

function isCurrentPreferenceLoad(planId, generation) {
  return state.display?.planId === planId && preferenceLoadGeneration === generation;
}

async function loadMembersForPlan(planId) {
  if (memberPlanId === planId && members) return members;
  if (memberRequest?.planId === planId) return memberRequest.promise;
  const generation = ++memberRequestGeneration;
  const request = { planId };
  request.promise = (async () => {
    try {
      const loaded = await api.getMembers();
      if (state.display?.planId !== planId || memberRequestGeneration !== generation) return null;
      members = loaded;
      memberPlanId = planId;
      memberError = null;
      return loaded;
    } catch (error) {
      if (state.display?.planId === planId && memberRequestGeneration === generation)
        memberError = normalizeError(error).message;
      throw error;
    } finally {
      if (memberRequest === request) memberRequest = null;
    }
  })();
  memberRequest = request;
  return request.promise;
}

async function savePreferences() {
  if (!state.display || !filterEngine) return;
  const planId = state.display.planId;
  const savedPreferences = preferences;
  const generation = ++preferenceLoadGeneration;
  activePreferencesPlanId = planId;
  try {
    await api.saveViewPreferences(planId, savedPreferences);
    if (state.display?.planId !== planId || preferenceLoadGeneration !== generation) return;
    filterError = null;
  } catch (error) {
    if (state.display?.planId !== planId || preferenceLoadGeneration !== generation) return;
    filterError = normalizeError(error).message;
  }
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
  const filteredBoard = getFilteredBoard();
  const buckets = filteredBoard.buckets;
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
      ${searchOpen ? `<input class="task-search" data-search-tasks aria-label="Search task titles" value="${escapeHtml(searchText)}" placeholder="Search tasks">` : ""}
      <button class="icon-action" data-toggle-search aria-label="Search tasks" aria-pressed="${searchOpen}" title="Search tasks">&#128269;</button>
      <button class="icon-action filter-action ${activeFilterCount() ? "active" : ""}" data-open-filters aria-label="Filter tasks" title="Filter tasks">&#9776;${activeFilterCount() ? `<span>${activeFilterCount()}</span>` : ""}</button>
      <button class="icon-action my-tasks-action ${preferences.myTasks ? "active" : ""}" data-toggle-my-tasks aria-label="My tasks" aria-pressed="${preferences.myTasks}" title="My tasks">My tasks</button>
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

function getFilteredBoard() {
  const board = state.display;
  if (!board) return null;
  if (filterEngine) return filterEngine.filterBoard(board, preferences, currentUserId, searchText, new Date());
  return preferences.myTasks && currentUserId ? { ...board, buckets: board.buckets.map(bucket => ({ ...bucket,
    tasks: bucket.tasks.filter(task => task.assignments?.includes(currentUserId)) })) } : board;
}

function renderBoardOnly() {
  const boardElement = app.querySelector?.(".board");
  const filteredBoard = getFilteredBoard();
  if (!boardElement || !filteredBoard) { render(); return; }
  const boardScrollLeft = boardElement.scrollLeft;
  const bucketScrollTops = new Map(Array.from(app.querySelectorAll?.(".bucket") || [],
    bucket => [bucket.dataset.bucketId, bucket.scrollTop]));
  boardElement.innerHTML = filteredBoard.buckets.map(renderBucket).join("");
  boardElement.scrollLeft = boardScrollLeft;
  app.querySelectorAll?.(".bucket").forEach(bucket => {
    bucket.scrollTop = bucketScrollTops.get(bucket.dataset.bucketId) ?? 0;
  });
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
    aria-label="${item.isChecked ? "Completed" : "Complete"} ${escapeHtml(item.title)}"
    title="${item.isChecked ? "Completed" : "Complete checklist item"}"><span class="mini-check" aria-hidden="true"></span></button>`;
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
      <div class="task-metadata">${renderPriority(task.priority)}${task.percentComplete === 50 ? '<span class="progress-marker">In progress</span>' : ""}${renderTaskLabels(task.labelIds)}</div>
      <div class="task-supplement">${renderTaskSupplement(task)}</div></div></article>`;
}

function renderPriority(priority) {
  if (priority === 1) return '<span class="priority-marker urgent">Urgent</span>';
  if (priority === 3) return '<span class="priority-marker important">Important</span>';
  return "";
}

function renderTaskLabels(labelIds) {
  const labels = state.display?.labels || [];
  return (labelIds || []).map(id => labels.find(label => label.labelId === id)).filter(Boolean)
    .map(label => `<span class="task-label label-${escapeHtml(label.labelId)}" title="${escapeHtml(label.name)}">${escapeHtml(label.name)}</span>`).join("");
}

function activeFilterCount() {
  const values = preferences.filters || {};
  return ["assigneeIds", "labelIds", "priorities", "bucketIds", "progressValues"]
    .reduce((count, key) => count + (values[key]?.length || 0), values.dueDateRange ? 1 : 0);
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

function renderFilterDialog() {
  const selected = preferences.filters || {};
  const labels = state.display?.labels || [];
  const buckets = state.display?.buckets || [];
  const options = (group, values) => `<div class="filter-options">${values.map(value => {
    const id = String(value.id);
    const selectedValue = selected[group];
    const active = Array.isArray(selectedValue)
      ? selectedValue.includes(value.value ?? value.id)
      : selectedValue === (value.value ?? value.id);
    return `<button type="button" data-toggle-filter data-filter-group="${group}" data-filter-value="${escapeHtml(id)}" aria-pressed="${active}"><span class="mini-check ${active ? "selected" : ""}" aria-hidden="true"></span>${escapeHtml(value.name)}</button>`;
  }).join("") || '<p class="empty">None available</p>'}</div>`;
  const dueOptions = [
    ["overdue", "Overdue"], ["today", "Today"], ["this-week", "This week"], ["later", "Later"], ["no-date", "No due date"]
  ].map(([id, name]) => ({ id, name }));
  return `<h2>Filter tasks</h2><div class="filter-panel">
    <section><h3>Assigned to</h3>${members ? options("assigneeIds", members.map(member => ({ id: member.id, name: member.displayName }))) : `<p class="empty">${escapeHtml(memberError || "Loading board members...")}</p>`}</section>
    <section><h3>Labels</h3>${options("labelIds", labels.map(label => ({ id: label.labelId, name: label.name })))}</section>
    <section><h3>Priority</h3>${options("priorities", [{ id: 1, value: 1, name: "Urgent" }, { id: 3, value: 3, name: "Important" }, { id: 5, value: 5, name: "Medium" }, { id: 9, value: 9, name: "Low" }])}</section>
    <section><h3>Bucket</h3>${options("bucketIds", buckets.map(bucket => ({ id: bucket.bucketId, name: bucket.name })))}</section>
    <section><h3>Progress</h3>${options("progressValues", [{ id: 0, value: 0, name: "Not started" }, { id: 50, value: 50, name: "In progress" }, { id: 100, value: 100, name: "Completed" }])}</section>
    <section><h3>Due</h3>${options("dueDateRange", dueOptions)}</section>
  </div>${filterError ? `<p class="dialog-error" role="alert">${escapeHtml(filterError)}</p>` : ""}<div class="confirm-actions"><button type="button" data-clear-filters>Clear filters</button><button type="button" class="primary" data-close-dialog>Done</button></div>`;
}

function renderLabelPicker(selectedIds, attribute) {
  const labels = state.display?.labels || [];
  return `<div class="label-picker">${labels.map(label => `<button type="button" ${attribute}="${escapeHtml(label.labelId)}" aria-pressed="${selectedIds.includes(label.labelId)}"><span class="label-swatch label-${escapeHtml(label.labelId)}" aria-hidden="true"></span>${escapeHtml(label.name)}</button>`).join("") || '<p class="empty">No named labels</p>'}</div>`;
}

function renderChecklistEditor(dialog, info) {
  const items = info.checklist || [];
  return `<div class="detail-checklist">${items.map((item, index) => {
    const editing = dialog.checklistEditId === item.itemId;
    return `<div class="checklist-row">${renderChecklistButton(dialog.taskId, item, "detail-item")}
      <div class="checklist-title">${editing ? `<input data-checklist-edit-draft maxlength="100" value="${escapeHtml(dialog.checklistEditDraft || "")}" aria-label="Checklist item title">
        <div class="inline-actions"><button type="button" data-cancel-checklist-edit>Cancel</button><button type="button" class="primary" data-save-checklist-edit="${escapeHtml(item.itemId)}">Save</button></div>` : `<span>${escapeHtml(item.title)}</span>`}</div>
      <div class="checklist-actions">
        <button type="button" data-move-checklist-up="${escapeHtml(item.itemId)}" aria-label="Move ${escapeHtml(item.title)} up" title="Move up" ${index === 0 ? "disabled" : ""}>&#8593;</button>
        <button type="button" data-move-checklist-down="${escapeHtml(item.itemId)}" aria-label="Move ${escapeHtml(item.title)} down" title="Move down" ${index === items.length - 1 ? "disabled" : ""}>&#8595;</button>
        <button type="button" data-edit-checklist="${escapeHtml(item.itemId)}" aria-label="Edit ${escapeHtml(item.title)}" title="Edit">&#9998;</button>
        <button type="button" data-delete-checklist="${escapeHtml(item.itemId)}" aria-label="Delete ${escapeHtml(item.title)}" title="Delete">&#128465;</button>
      </div></div>`;
  }).join("")}</div>
  <div class="checklist-add"><input data-checklist-add-draft maxlength="100" value="${escapeHtml(dialog.checklistAddDraft || "")}" placeholder="Add checklist item" aria-label="New checklist item"><button type="button" class="primary" data-add-checklist>Add</button></div>
  <p class="section-status" role="status">${escapeHtml(dialog.checklistStatus || "")}</p>`;
}

function hydrateDetailDrafts(dialog, info) {
  if (dialog.titleDraft == null) dialog.titleDraft = info.title || "";
  if (dialog.labelDraft == null) dialog.labelDraft = [...(info.labelIds || [])];
  if (dialog.startDateDraft == null) dialog.startDateDraft = info.startDateTime?.slice(0, 10) || "";
  if (dialog.progressDraft == null) dialog.progressDraft = info.percentComplete;
  if (dialog.priorityDraft == null) dialog.priorityDraft = info.priority;
  if (dialog.checklistAddDraft == null) dialog.checklistAddDraft = "";
}

function renderDialog() {
  const dialog = state.dialog;
  if (!dialog) return "";
  let content = "";
  if (dialog.type === "filters") {
    content = renderFilterDialog();
  } else if (dialog.type === "boardPicker") {
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
    if (info) hydrateDetailDrafts(dialog, info);
    content = `<h2>${escapeHtml(info?.title || task?.title || "Task")}</h2>${info ? `
      <div class="title-editor"><input data-title-draft maxlength="255" value="${escapeHtml(dialog.titleDraft)}" aria-label="Task title"><button type="button" class="primary" data-save-title>Save title</button></div>
      <p class="section-status" role="status">${escapeHtml(dialog.metadataStatus || "")}</p>
      <dl class="task-meta"><dt>Progress</dt><dd><select data-task-progress aria-label="Task progress">
        ${[[0, "Not started"], [50, "In progress"], [100, "Completed"]].map(([value, name]) => `<option value="${value}" ${dialog.progressDraft === value ? "selected" : ""}>${name}</option>`).join("")}</select></dd>
      <dt>Priority</dt><dd><select data-task-priority aria-label="Task priority">
        ${[[1, "Urgent"], [3, "Important"], [5, "Medium"], [9, "Low"]].map(([value, name]) => `<option value="${value}" ${dialog.priorityDraft === value ? "selected" : ""}>${name}</option>`).join("")}</select></dd>
      <dt>Start</dt><dd><div class="date-edit"><input type="date" data-start-date-draft value="${escapeHtml(dialog.startDateDraft)}"><button type="button" data-save-start-date>Save</button><button type="button" data-clear-start-date>Clear</button></div></dd>
      <dt>Due</dt><dd><button type="button" data-open-date-picker ${state.completing ? "disabled" : ""}>${info.dueDateTime ? formatDate(info.dueDateTime) : "No due date"}</button>
      ${dialog.datePickerOpen ? renderCalendar(dialog) : ""}</dd>
      <dt>Assigned to</dt><dd><button type="button" data-open-members ${state.completing ? "disabled" : ""}>${info.assignees?.length ? info.assignees.map(escapeHtml).join(", ") : "Unassigned"}</button>
      ${renderMemberPicker(dialog)}</dd>
      <dt>Bucket</dt><dd><button type="button" class="bucket-picker-trigger" data-open-bucket-picker ${state.completing ? "disabled" : ""}
        aria-expanded="${dialog.bucketPickerOpen ? "true" : "false"}"><span>${escapeHtml(selectedBucketName)}</span><span aria-hidden="true">v</span></button>
        ${dialog.bucketPickerOpen ? `<div class="bucket-option-list" role="listbox" aria-label="Task bucket">${buckets.map(bucket =>
          `<button type="button" data-select-task-bucket="${escapeHtml(bucket.bucketId)}" role="option"
            aria-selected="${bucket.bucketId === selectedBucketId ? "true" : "false"}">${escapeHtml(bucket.name)}</button>`).join("")}</div>` : ""}</dd></dl>
      ${movingToDifferentBucket ? '<div class="confirm-actions move-action"><button class="primary" data-move-task>Move task</button></div>' : ""}
      <h3>Labels</h3>${renderLabelPicker(dialog.labelDraft, "data-toggle-task-label")}<div class="inline-actions"><button type="button" class="primary" data-save-labels>Save labels</button></div>
      <h3>Checklist</h3>${renderChecklistEditor(dialog, info)}
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
      <div class="form-grid"><label class="field-label">Start<input type="date" data-new-task-start value="${escapeHtml(dialog.startDate || "")}"></label>
      <label class="field-label">Due<input type="date" data-new-task-due value="${escapeHtml(dialog.dateDraft || "")}"></label></div>
      <label class="field-label">Priority<select data-new-task-priority>${[[1, "Urgent"], [3, "Important"], [5, "Medium"], [9, "Low"]].map(([value, name]) => `<option value="${value}" ${dialog.priority === value ? "selected" : ""}>${name}</option>`).join("")}</select></label>
      <label class="field-label">Labels</label>${renderLabelPicker(dialog.labelIds || [], "data-toggle-new-label")}
      <label class="field-label">Assigned to</label><button type="button" data-open-members>${dialog.selectedAssignees?.length ? `${dialog.selectedAssignees.length} people` : "Unassigned"}</button>
      ${renderMemberPicker(dialog)}
      <div class="confirm-actions"><button type="button" data-close-dialog>Cancel</button><button type="button" class="primary" data-submit-new-task ${state.completing ? "disabled" : ""}>Create task</button></div>`;
  } else if (["confirmTask", "confirmChecklist", "confirmProgress", "confirmChecklistDelete"].includes(dialog.type)) {
    const deleting = dialog.type === "confirmChecklistDelete";
    const taskCompletion = dialog.type === "confirmTask" || dialog.type === "confirmProgress";
    content = `<h2>${deleting ? "Delete checklist item?" : `Complete ${taskCompletion ? "task" : "checklist item"}?`}</h2><p>${escapeHtml(dialog.title)}</p>
      <div class="confirm-actions"><button data-close-dialog ${state.completing ? "disabled" : ""}>Cancel</button>
      <button class="primary" ${dialog.type === "confirmTask" ? "data-confirm-task" : dialog.type === "confirmProgress" ? "data-confirm-progress" : deleting ? "data-confirm-checklist-delete" : "data-confirm-checklist"}
        ${state.completing ? "disabled" : ""}>${deleting ? "Delete" : "Complete"}</button></div>`;
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
    chatPage: null, chatDraft: "", chatStatus: "Loading comments...", chatLoading: true, chatPending: false,
    titleDraft: null, labelDraft: null, startDateDraft: null, progressDraft: null, priorityDraft: null,
    checklistAddDraft: "", checklistEditId: null,
    checklistEditDraft: "", checklistStatus: "", metadataStatus: "", metadataGenerations: {}, generation });
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
  if (event.target.matches?.("[data-search-tasks]")) {
    searchText = event.target.value; visibleTasks.clear(); renderBoardOnly(); return;
  }
  if (event.target.matches?.("[data-new-task-title]") && state.dialog?.type === "createTask")
    state.dialog.title = event.target.value;
  if (event.target.matches?.("[data-new-task-start]") && state.dialog?.type === "createTask")
    state.dialog.startDate = event.target.value;
  if (event.target.matches?.("[data-new-task-due]") && state.dialog?.type === "createTask")
    state.dialog.dateDraft = event.target.value;
  if (event.target.matches?.("[data-title-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.titleDraft = event.target.value; state.dialog.metadataStatus = "";
  }
  if (event.target.matches?.("[data-start-date-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.startDateDraft = event.target.value; state.dialog.metadataStatus = "";
  }
  if (event.target.matches?.("[data-checklist-add-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.checklistAddDraft = event.target.value; state.dialog.checklistStatus = "";
  }
  if (event.target.matches?.("[data-checklist-edit-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.checklistEditDraft = event.target.value; state.dialog.checklistStatus = "";
  }
  if (event.target.matches?.("[data-notes-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.notesDraft = event.target.value;
    state.dialog.notesStatus = "";
  }
  if (event.target.matches?.("[data-chat-draft]") && state.dialog?.type === "taskDetails") {
    state.dialog.chatDraft = event.target.value;
    state.dialog.chatStatus = "";
  }
});

app.addEventListener("change", async event => {
  if (event.target.matches?.("[data-new-task-priority]") && state.dialog?.type === "createTask") {
    state.dialog.priority = Number(event.target.value); return;
  }
  if (event.target.matches?.("[data-task-priority]") && state.dialog?.type === "taskDetails") {
    state.dialog.priorityDraft = Number(event.target.value);
    await saveDetailMetadata("priority", state.dialog.priorityDraft); return;
  }
  if (event.target.matches?.("[data-task-progress]") && state.dialog?.type === "taskDetails") {
    const progress = Number(event.target.value);
    if (progress === 100) {
      const info = details.get(state.dialog.taskId);
      nextMetadataGeneration(state.dialog, "progress");
      state.completing = false;
      state = flow.beginConfirmProgress(state, state.dialog.taskId, info?.title || "Task"); render();
    } else {
      state.dialog.progressDraft = progress;
      await saveDetailMetadata("progress", progress);
    }
  }
});

app.addEventListener("click", async event => {
  const hit = name => event.target.closest(`[${name}]`);
  if (hit("data-new-task") && !state.dialog) {
    state.dialog = { type: "createTask", title: "", bucketId: state.display?.buckets.find(bucket => bucket.bucketId !== "unbucketed")?.bucketId || "",
      startDate: null, dateDraft: null, priority: 5, labelIds: [], selectedAssignees: [], assigneeDraft: [] };
    state.dialogError = null; createNotice = null; render(); return;
  }
  if (hit("data-toggle-search") && !state.dialog) {
    searchOpen = !searchOpen;
    if (!searchOpen) searchText = "";
    visibleTasks.clear(); render(); return;
  }
  if (hit("data-open-filters") && !state.dialog) {
    state.dialog = { type: "filters" }; state.dialogError = null; render();
    if (memberPlanId !== state.display?.planId) { members = null; memberError = null; }
    if (!members) {
      try {
        const loadedMembers = await loadMembersForPlan(state.display.planId);
        if (!loadedMembers) return;
        const sanitized = filterEngine.sanitizePreferences(preferences, state.display, members);
        const changed = JSON.stringify(sanitized) !== JSON.stringify(preferences);
        preferences = sanitized;
        if (changed) await savePreferences();
      } catch (error) { memberError = normalizeError(error).message; }
      if (state.dialog?.type === "filters") render();
    }
    return;
  }
  if (hit("data-toggle-filter") && state.dialog?.type === "filters") {
    const button = hit("data-toggle-filter");
    const group = button.dataset.filterGroup;
    let value = button.dataset.filterValue;
    if (["priorities", "progressValues"].includes(group)) value = Number(value);
    if (group === "dueDateRange") preferences = { ...preferences, filters: { ...preferences.filters,
      dueDateRange: preferences.filters.dueDateRange === value ? null : value } };
    else {
      const current = preferences.filters[group] || [];
      preferences = { ...preferences, filters: { ...preferences.filters,
        [group]: current.includes(value) ? current.filter(item => item !== value) : [...current, value] } };
    }
    visibleTasks.clear(); render(); await savePreferences(); return;
  }
  if (hit("data-clear-filters") && state.dialog?.type === "filters") {
    preferences = { ...preferences, filters: filterEngine.createDefaultPreferences().filters };
    visibleTasks.clear(); render(); await savePreferences(); return;
  }
  if (hit("data-toggle-new-label") && state.dialog?.type === "createTask") {
    state.dialog.labelIds = toggleValue(state.dialog.labelIds, hit("data-toggle-new-label").dataset.toggleNewLabel);
    render(); return;
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
    if (memberPlanId !== state.display?.planId) { members = null; memberError = null; }
    if (!members) memberError = null;
    render();
    if (!members && !memberError) {
      try { await loadMembersForPlan(state.display.planId); }
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
    const { title, bucketId, startDate, dateDraft, priority, labelIds, selectedAssignees } = state.dialog;
    if (!title?.trim()) { state.dialogError = "Enter a task title."; render(); return; }
    if (!bucketId) { state.dialogError = "Choose a bucket."; render(); return; }
    state.completing = true; render();
    try { await api.createTask({ title: title.trim(), bucketId, date: dateDraft || null, userIds: selectedAssignees,
      startDate: startDate || null, priority, labelIds }); }
    catch (error) { state.completing = false; state.dialogError = normalizeError(error).message; render(); return; }
    state = flow.closeDialog(state);
    if (preferences.myTasks && !selectedAssignees.includes(currentUserId)) createNotice = "Task created. It is hidden by My tasks.";
    await loadDisplay(true);
    return;
  }
  if (hit("data-toggle-my-tasks")) {
    if (!preferences.myTasks && !currentUserId) {
      try { currentUserId = (await api.getCurrentUser()).userId; }
      catch (error) { filterError = normalizeError(error).message; render(); return; }
    }
    filterError = null;
    preferences = { ...preferences, myTasks: !preferences.myTasks };
    createNotice = null; visibleTasks.clear(); render(); await savePreferences(); return;
  }
  if (event.target.matches?.("[data-dialog-backdrop]")) {
    if (!state.completing) { if (!state.dialog?.returnDialog) dialogGeneration++; state = flow.closeActiveDialog(state); render(); }
    return;
  }
  if (hit("data-close-dialog")) { if (!state.completing) { if (!state.dialog?.returnDialog) dialogGeneration++; state = flow.closeActiveDialog(state); render(); } return; }
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
      memberRequest = null; memberRequestGeneration++; preferenceLoadGeneration++;
      createNotice = null; filterError = null; preferencesPlanId = null; activePreferencesPlanId = null; searchText = "";
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
  if (hit("data-save-title") && state.dialog?.type === "taskDetails" && !state.completing) {
    const title = state.dialog.titleDraft?.trim();
    if (!title) { state.dialog.metadataStatus = "Enter a task title."; render(); return; }
    await saveDetailMetadata("title", title); return;
  }
  if ((hit("data-save-start-date") || hit("data-clear-start-date")) && state.dialog?.type === "taskDetails" && !state.completing) {
    await saveDetailMetadata("startDate", hit("data-clear-start-date") ? null : state.dialog.startDateDraft || null); return;
  }
  if (hit("data-toggle-task-label") && state.dialog?.type === "taskDetails") {
    state.dialog.labelDraft = toggleValue(state.dialog.labelDraft || [], hit("data-toggle-task-label").dataset.toggleTaskLabel);
    state.dialog.metadataStatus = ""; render(); return;
  }
  if (hit("data-save-labels") && state.dialog?.type === "taskDetails" && !state.completing) {
    await saveDetailMetadata("labels", [...state.dialog.labelDraft]); return;
  }
  if (hit("data-add-checklist") && state.dialog?.type === "taskDetails" && !state.completing) {
    const dialog = state.dialog;
    const title = dialog.checklistAddDraft?.trim();
    if (!title) { dialog.checklistStatus = "Enter a checklist item."; render(); return; }
    await runChecklistMutation(dialog, () => api.addChecklistItem(dialog.taskId, title), () => { dialog.checklistAddDraft = ""; });
    return;
  }
  if (hit("data-edit-checklist") && state.dialog?.type === "taskDetails") {
    const itemId = hit("data-edit-checklist").dataset.editChecklist;
    const item = details.get(state.dialog.taskId)?.checklist.find(value => value.itemId === itemId);
    state.dialog.checklistEditId = itemId; state.dialog.checklistEditDraft = item?.title || "";
    state.dialog.checklistStatus = ""; render(); return;
  }
  if (hit("data-cancel-checklist-edit") && state.dialog?.type === "taskDetails") {
    state.dialog.checklistEditId = null; state.dialog.checklistEditDraft = ""; render(); return;
  }
  if (hit("data-save-checklist-edit") && state.dialog?.type === "taskDetails" && !state.completing) {
    const dialog = state.dialog;
    const itemId = hit("data-save-checklist-edit").dataset.saveChecklistEdit;
    const title = dialog.checklistEditDraft?.trim();
    if (!title) { dialog.checklistStatus = "Enter a checklist item title."; render(); return; }
    await runChecklistMutation(dialog, () => api.renameChecklistItem(dialog.taskId, itemId, title), () => {
      dialog.checklistEditId = null; dialog.checklistEditDraft = "";
    });
    return;
  }
  if (hit("data-delete-checklist") && state.dialog?.type === "taskDetails" && !state.completing) {
    const itemId = hit("data-delete-checklist").dataset.deleteChecklist;
    const item = details.get(state.dialog.taskId)?.checklist.find(value => value.itemId === itemId);
    if (item) state = flow.beginConfirmChecklistDelete(state, state.dialog.taskId, itemId, item.title);
    render(); return;
  }
  if ((hit("data-move-checklist-up") || hit("data-move-checklist-down")) && state.dialog?.type === "taskDetails" && !state.completing) {
    const dialog = state.dialog;
    const down = hit("data-move-checklist-down");
    const itemId = (down || hit("data-move-checklist-up")).dataset[down ? "moveChecklistDown" : "moveChecklistUp"];
    await runChecklistMutation(dialog, () => api.moveChecklistItem(dialog.taskId, itemId, down ? "down" : "up"));
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
  if (hit("data-confirm-progress") && !state.completing && state.dialog?.type === "confirmProgress") {
    const confirmation = state.dialog;
    const taskId = confirmation.taskId;
    state.completing = true; render();
    try {
      await api.setProgress(taskId, 100);
      updateDetailValue(taskId, "progress", 100);
      state = flow.closeActiveDialog(state);
      if (state.dialog?.type === "taskDetails") {
        state.dialog.progressDraft = null;
        state.dialog.metadataStatus = "Progress saved.";
      }
      render();
    } catch (error) {
      state.completing = false; state.dialogError = normalizeError(error).message; render();
    }
    return;
  }
  if (hit("data-confirm-checklist-delete") && !state.completing && state.dialog?.type === "confirmChecklistDelete") {
    const confirmation = state.dialog;
    state.completing = true; render();
    try {
      await api.deleteChecklistItem(confirmation.taskId, confirmation.itemId);
      state = flow.closeActiveDialog(state);
      const dialog = state.dialog;
      if (dialog?.type === "taskDetails") {
        dialog.checklistStatus = "Checklist item deleted.";
        await reloadTaskDetails(confirmation.taskId, dialog);
      } else render();
    } catch (error) {
      state.completing = false; state.dialogError = normalizeError(error).message; render();
    }
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

function toggleValue(values, value) {
  return values.includes(value) ? values.filter(item => item !== value) : [...values, value];
}

async function saveDetailMetadata(field, value) {
  const dialog = state.dialog;
  if (dialog?.type !== "taskDetails") return;
  const taskId = dialog.taskId;
  const generation = nextMetadataGeneration(dialog, field);
  const operations = {
    title: () => api.setTitle(taskId, value),
    progress: () => api.setProgress(taskId, value),
    priority: () => api.setPriority(taskId, value),
    startDate: () => api.setStartDate(taskId, value),
    labels: () => api.setLabels(taskId, value)
  };
  state.completing = true; dialog.metadataStatus = "Saving..."; render();
  try {
    await operations[field]();
    if (!isCurrentMetadataRequest(dialog, field, generation)) return;
    updateDetailValue(taskId, field, value);
    if (field === "priority") dialog.priorityDraft = null;
    if (field === "progress") dialog.progressDraft = null;
    if (state.dialog === dialog) state.completing = false;
    dialog.metadataStatus = "Changes saved."; render();
  } catch (error) {
    if (!isCurrentMetadataRequest(dialog, field, generation)) return;
    if (state.dialog === dialog) state.completing = false;
    dialog.metadataStatus = normalizeError(error).message; render();
  }
}

function nextMetadataGeneration(dialog, field) {
  dialog.metadataGenerations ||= {};
  dialog.metadataGenerations[field] = (dialog.metadataGenerations[field] || 0) + 1;
  return dialog.metadataGenerations[field];
}

function isCurrentMetadataRequest(dialog, field, generation) {
  const activeDialog = state.dialog === dialog || state.dialog?.returnDialog === dialog;
  return activeDialog && dialog.metadataGenerations?.[field] === generation;
}

function updateDetailValue(taskId, field, value) {
  const info = details.get(taskId);
  if (!info) return;
  const property = { title: "title", progress: "percentComplete", priority: "priority", startDate: "startDateTime", labels: "labelIds" }[field];
  const stored = field === "startDate" && value ? `${value}T12:00:00Z` : value;
  details.set(taskId, { ...info, [property]: stored });
  if (state.display) state = { ...state, display: { ...state.display, buckets: state.display.buckets.map(bucket => ({
    ...bucket, tasks: bucket.tasks.map(task => task.taskId === taskId ? { ...task, [property]: stored } : task)
  })) } };
}

async function runChecklistMutation(dialog, operation, onSuccess = () => {}) {
  state.completing = true; dialog.checklistStatus = "Saving checklist..."; render();
  try {
    await operation();
    if (state.dialog !== dialog) return;
    onSuccess();
    dialog.checklistStatus = "Checklist saved.";
    await reloadTaskDetails(dialog.taskId, dialog);
  } catch (error) {
    if (state.dialog !== dialog) return;
    state.completing = false; dialog.checklistStatus = normalizeError(error).message; render();
  }
}

async function reloadTaskDetails(taskId, dialog) {
  try {
    const info = await api.getTaskDetails(taskId);
    details.set(taskId, info); failures.delete(taskId);
  } catch (error) {
    dialog.checklistStatus = normalizeError(error).message;
  }
  state.completing = false;
  if (state.dialog === dialog) render();
}

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
