const BASE_URL = globalThis.location?.protocol === "http:" &&
  globalThis.location.hostname === "localhost" && globalThis.location.port === "8787"
  ? "" : "http://localhost:8787";

async function getDisplay() {
  const response = await fetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
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

globalThis.PlannerApi = { getDisplay, completeTask, getPlans, selectPlan, getTaskDetails, completeChecklistItem, moveTask };

async function parseJsonResponse(response) {
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) throw body ?? { code: "unknown_error", message: "The local helper returned an error." };
  return body;
}
