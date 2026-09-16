const BASE_URL = "http://localhost:8787";

async function getDisplay() {
  const response = await fetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
}

async function completeTask(taskId) {
  const response = await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/complete`, { method: "POST" });
  return parseJsonResponse(response);
}

globalThis.PlannerApi = { getDisplay, completeTask };

async function parseJsonResponse(response) {
  const body = await response.json().catch(() => null);
  if (!response.ok) throw body ?? { code: "unknown_error", message: "The local helper returned an error." };
  return body;
}
