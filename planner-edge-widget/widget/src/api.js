const nativePlanner = !["http:", "https:"].includes(globalThis.location?.protocol);
const BASE_URL = nativePlanner ? "http://localhost:8787" : "";

async function plannerOwnerApi() {
  if (!globalThis.helperApi) {
    await new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "/helper-api.js";
      script.onload = resolve;
      script.onerror = () => reject(new Error("Open Microsoft Widgets Setup to preview Planner."));
      document.head.append(script);
    });
  }
  if (!await globalThis.helperApi?.ready) throw new Error("Open Microsoft Widgets Setup to preview Planner.");
  return globalThis.helperApi;
}

async function plannerFetch(url, options = {}, bootstrap = false) {
  const headers = { ...options.headers };
  if (nativePlanner && !bootstrap && globalThis.PlannerApi.credential)
    headers["X-Microsoft-Widgets-Credential"] = globalThis.PlannerApi.credential;
  const request = { ...options, headers, cache: "no-store", credentials: "omit" };
  const response = nativePlanner ? await fetch(url, request) : await (globalThis.helperApi || await plannerOwnerApi()).fetch(url, request);
  if (response.status === 401 && !bootstrap) globalThis.PlannerApi.onUnauthorized?.();
  return response;
}

function readPlannerInstance() {
  try {
    const value = JSON.parse(localStorage.getItem(globalThis.PlannerApi.instanceId) || "{}");
    return value && typeof value === "object" && !Array.isArray(value) ? value : {};
  } catch { return {}; }
}

async function initializePlanner() {
  const api = globalThis.PlannerApi;
  if (!nativePlanner) { await plannerOwnerApi(); return true; }
  for (let attempt = 0; attempt < 150; attempt++) {
    const id = typeof uniqueId === "undefined" ? undefined : uniqueId;
    if (typeof id === "string" && id) {
      api.instanceId = id;
      const saved = readPlannerInstance().planner?.credential;
      api.credential = typeof saved === "string" ? saved : "";
      return Boolean(api.credential);
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("Waiting for iCUE instance identity. Reload this widget to retry.");
}

function savePlannerCredential(credential) {
  const api = globalThis.PlannerApi;
  if (!nativePlanner || !api.instanceId) throw new Error("Native Planner instance required.");
  const value = readPlannerInstance();
  localStorage.setItem(api.instanceId, JSON.stringify({ ...value, planner: { ...value.planner, credential } }));
  api.credential = credential;
}

async function plannerPairing(path, body) {
  if (!nativePlanner) throw new Error("Preview uses the setup owner session.");
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/api/local-access/pairings${path}`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body)
  }, true));
}

async function getDisplay() {
  const response = await plannerFetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
}

async function getCachedDisplay() {
  const response = await plannerFetch(`${BASE_URL}/display/cached`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
}

async function getViewPreferences(planId) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/view-preferences/${encodeURIComponent(planId)}`));
}

async function saveViewPreferences(planId, preferences) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/view-preferences/${encodeURIComponent(planId)}`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(preferences)
  }));
}

async function getCurrentUser() {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/api/planner/me`));
}

async function completeTask(taskId) {
  const response = await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/complete`, { method: "POST" });
  return parseJsonResponse(response);
}

async function getPlans() {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/plans`));
}

async function selectPlan(planId) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/selected-plan`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ planId })
  }));
}

async function getTaskDetails(taskId) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/details`));
}

async function completeChecklistItem(taskId, itemId) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/checklist/${encodeURIComponent(itemId)}/complete`,
    { method: "POST" }));
}

async function moveTask(taskId, bucketId) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/bucket`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ bucketId })
  }));
}

async function setDueDate(taskId, date) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/due-date`, {
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
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/${route}`, {
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
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/checklist${suffix}`, options));
}

async function getMembers() {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/members`));
}

async function setAssignments(taskId, userIds) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/assignments`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ userIds })
  }));
}

async function createTask(task) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(task)
  }));
}

async function updateNotes(taskId, description) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/notes`, {
    method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ description })
  }));
}

async function getTaskChat(taskId, cursor) {
  const query = cursor ? `?${new URLSearchParams({ cursor })}` : "";
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/chat${query}`));
}

async function postTaskChat(taskId, message) {
  return parseJsonResponse(await plannerFetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/chat`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ message })
  }));
}

globalThis.PlannerApi = { native: nativePlanner, credential: "", initialize: initializePlanner, setCredential: savePlannerCredential,
  pair: (instanceId, requestSecret) => plannerPairing("", { scope: "planner", instanceId, requestSecret }),
  poll: (id, requestSecret) => plannerPairing(`/${encodeURIComponent(id)}/poll`, { requestSecret }), getDisplay, getCachedDisplay, getViewPreferences, saveViewPreferences, getCurrentUser, completeTask,
  getPlans, selectPlan, getTaskDetails, completeChecklistItem, moveTask, setDueDate, setTitle, setProgress,
  setPriority, setStartDate, setLabels, addChecklistItem, renameChecklistItem, deleteChecklistItem,
  moveChecklistItem, getMembers, setAssignments, createTask, updateNotes, getTaskChat, postTaskChat };

async function parseJsonResponse(response) {
  if (response.status === 204) return null;
  const body = await response.json().catch(() => null);
  if (!response.ok) throw { ...(body?.error || body || {}), status: response.status,
    code: body?.error?.code || body?.code || (response.status === 401 ? "pairing_required" : "unknown_error"),
    message: body?.error?.message || body?.message || "The local helper returned an error." };
  return body;
}
