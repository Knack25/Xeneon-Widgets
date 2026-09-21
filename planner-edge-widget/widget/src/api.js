const BASE_URL = globalThis.location?.protocol === "http:" &&
  globalThis.location.hostname === "localhost" && globalThis.location.port === "8787"
  ? "" : "http://localhost:8787";

async function getDisplay() {
  const response = await fetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
}

async function getViewPreferences(planId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/view-preferences/${encodeURIComponent(planId)}`));
}

async function saveViewPreferences(planId, preferences) {
  return parseJsonResponse(await fetch(`${BASE_URL}/view-preferences/${encodeURIComponent(planId)}`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(preferences)
  }));
}

async function getCurrentUser() {
  return parseJsonResponse(await fetch(`${BASE_URL}/auth/me`));
}

async function completeTask(taskId) {
  const response = await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/complete`, { method: "POST" });
  return parseJsonResponse(response);
}

async function getPlans() {
  return parseJsonResponse(await fetch(`${BASE_URL}/plans`));
}

async function selectPlan(planId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/selected-plan`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ planId })
  }));
}

async function getTaskDetails(taskId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/details`));
}

async function completeChecklistItem(taskId, itemId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/checklist/${encodeURIComponent(itemId)}/complete`,
    { method: "POST" }));
}

async function moveTask(taskId, bucketId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/bucket`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ bucketId })
  }));
}

async function setDueDate(taskId, date) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/due-date`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ date })
  }));
}

async function setTitle(taskId, title) {
  return putTaskValue(taskId, "title", { title });
}

async function setProgress(taskId, progress) {
  return putTaskValue(taskId, "progress", { progress });
}

async function setPriority(taskId, priority) {
  return putTaskValue(taskId, "priority", { priority });
}

async function setStartDate(taskId, date) {
  return putTaskValue(taskId, "start-date", { date });
}

async function setLabels(taskId, labelIds) {
  return putTaskValue(taskId, "labels", { labelIds });
}

async function putTaskValue(taskId, route, value) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/${route}`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
  }));
}

async function addChecklistItem(taskId, title) {
  return checklistRequest(taskId, "", "POST", { title });
}

async function renameChecklistItem(taskId, itemId, title) {
  return checklistRequest(taskId, `/${encodeURIComponent(itemId)}`, "PUT", { title });
}

async function deleteChecklistItem(taskId, itemId) {
  return checklistRequest(taskId, `/${encodeURIComponent(itemId)}`, "DELETE");
}

async function moveChecklistItem(taskId, itemId, direction) {
  return checklistRequest(taskId, `/${encodeURIComponent(itemId)}/position`, "PUT", { direction });
}

async function checklistRequest(taskId, suffix, method, value) {
  const options = { method };
  if (value) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(value);
  }
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/checklist${suffix}`, options));
}

async function getMembers() {
  return parseJsonResponse(await fetch(`${BASE_URL}/members`));
}

async function setAssignments(taskId, userIds) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/assignments`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ userIds })
  }));
}

async function createTask(task) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(task)
  }));
}

async function updateNotes(taskId, description) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/notes`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ description })
  }));
}

async function getTaskChat(taskId, cursor) {
  const query = cursor ? `?${new URLSearchParams({ cursor })}` : "";
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/chat${query}`));
}

async function postTaskChat(taskId, message) {
  return parseJsonResponse(await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/chat`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ message })
  }));
}

globalThis.PlannerApi = { getDisplay, getViewPreferences, saveViewPreferences, getCurrentUser, completeTask,
  getPlans, selectPlan, getTaskDetails, completeChecklistItem, moveTask, setDueDate, setTitle, setProgress,
  setPriority, setStartDate, setLabels, addChecklistItem, renameChecklistItem, deleteChecklistItem,
  moveChecklistItem, getMembers, setAssignments, createTask, updateNotes, getTaskChat, postTaskChat };

async function parseJsonResponse(response) {
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) throw body ?? { code: "unknown_error", message: "The local helper returned an error." };
  return body;
}
