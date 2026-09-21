const BASE_URL = globalThis.location?.protocol === "http:" &&
  globalThis.location.hostname === "localhost" && globalThis.location.port === "8787"
  ? "" : "http://localhost:8787";

async function getDisplay() {
  const response = await fetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
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

globalThis.PlannerApi = { getDisplay, getCurrentUser, completeTask, getPlans, selectPlan, getTaskDetails,
  completeChecklistItem, moveTask, setDueDate, getMembers, setAssignments, createTask, updateNotes,
  getTaskChat, postTaskChat };

async function parseJsonResponse(response) {
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) throw body ?? { code: "unknown_error", message: "The local helper returned an error." };
  return body;
}
