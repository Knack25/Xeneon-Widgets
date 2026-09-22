(() => {
function load(planId) {
  try {
    const stored = JSON.parse(globalThis.localStorage?.getItem(storageKey(planId)) ?? "null");
    return sanitize(stored);
  } catch {
    return sanitize(null);
  }
}

function save(planId, state) {
  const sanitized = sanitize(state);
  try {
    globalThis.localStorage?.setItem(storageKey(planId), JSON.stringify(sanitized));
  } catch {
    // Scroll persistence must never prevent the board from rendering.
  }
  return sanitized;
}

function clear() {
  try {
    const storage = globalThis.localStorage;
    if (!storage) return;
    const keys = [];
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index);
      if (key?.startsWith("planner-edge:view:")) keys.push(key);
    }
    for (const key of keys) storage.removeItem(key);
  } catch {
    // Authorization cleanup must not prevent the signed-out view from rendering.
  }
}

function storageKey(planId) {
  return `planner-edge:view:${planId}`;
}

function sanitize(state) {
  const bucketScrollTops = {};
  if (state?.bucketScrollTops && typeof state.bucketScrollTops === "object" && !Array.isArray(state.bucketScrollTops)) {
    for (const [bucketId, value] of Object.entries(state.bucketScrollTops)) {
      bucketScrollTops[bucketId] = nonnegative(value);
    }
  }
  return { boardScrollLeft: nonnegative(state?.boardScrollLeft), bucketScrollTops };
}

function nonnegative(value) {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 ? value : 0;
}

globalThis.PlannerViewState = { load, save, clear };
})();
