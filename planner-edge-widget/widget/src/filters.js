(() => {
const VALID_PRIORITIES = new Set([1, 3, 5, 9]);
const VALID_PROGRESS = new Set([0, 50, 100]);
const VALID_DUE_RANGES = new Set(["overdue", "today", "this-week", "later", "no-date"]);

function createDefaultPreferences() {
  return {
    myTasks: false,
    filters: {
      assigneeIds: [], labelIds: [], priorities: [], bucketIds: [], progressValues: [], dueDateRange: null
    }
  };
}

function sanitizePreferences(preferences, board, members) {
  const normalized = normalizePreferences(preferences);
  const validAssignees = Array.isArray(members)
    ? new Set(members.map(member => member?.id).filter(Boolean))
    : null;
  const validLabels = new Set((board?.labels ?? [])
    .filter(label => label?.name?.trim())
    .map(label => label.labelId));
  const validBuckets = new Set((board?.buckets ?? []).map(bucket => bucket?.bucketId).filter(Boolean));

  return {
    myTasks: normalized.myTasks,
    filters: {
      ...normalized.filters,
      assigneeIds: validAssignees
        ? normalized.filters.assigneeIds.filter(id => validAssignees.has(id))
        : normalized.filters.assigneeIds,
      labelIds: normalized.filters.labelIds.filter(id => validLabels.has(id)),
      bucketIds: normalized.filters.bucketIds.filter(id => validBuckets.has(id))
    }
  };
}

function filterBoard(board, preferences, currentUserId, searchText, currentDate) {
  const normalized = normalizePreferences(preferences);
  const filters = normalized.filters;
  const search = typeof searchText === "string" ? searchText.trim().toLocaleLowerCase() : "";
  const dueBoundaries = getDueBoundaries(currentDate);

  return {
    ...board,
    buckets: (board?.buckets ?? []).map(bucket => ({
      ...bucket,
      tasks: (bucket.tasks ?? []).filter(task =>
        (!normalized.myTasks || Boolean(currentUserId) && (task.assignments ?? []).includes(currentUserId)) &&
        matchesAny(task.assignments, filters.assigneeIds) &&
        matchesAny(task.labelIds, filters.labelIds) &&
        matchesValue(task.priority, filters.priorities) &&
        matchesValue(bucket.bucketId, filters.bucketIds) &&
        matchesValue(task.percentComplete, filters.progressValues) &&
        matchesDueDate(task.dueDateTime, filters.dueDateRange, dueBoundaries) &&
        (!search || String(task.title ?? "").toLocaleLowerCase().includes(search))
      )
    }))
  };
}

function normalizePreferences(preferences) {
  const defaults = createDefaultPreferences();
  const filters = preferences?.filters;
  return {
    myTasks: preferences?.myTasks === true,
    filters: {
      assigneeIds: normalizeIds(filters?.assigneeIds),
      labelIds: normalizeIds(filters?.labelIds),
      priorities: normalizeValues(filters?.priorities, VALID_PRIORITIES),
      bucketIds: normalizeIds(filters?.bucketIds),
      progressValues: normalizeValues(filters?.progressValues, VALID_PROGRESS),
      dueDateRange: VALID_DUE_RANGES.has(filters?.dueDateRange) ? filters.dueDateRange : defaults.filters.dueDateRange
    }
  };
}

function normalizeIds(values) {
  if (!Array.isArray(values)) return [];
  return [...new Set(values.filter(value => typeof value === "string" && value.trim()).map(value => value.trim()))];
}

function normalizeValues(values, allowed) {
  if (!Array.isArray(values)) return [];
  return [...new Set(values.filter(value => allowed.has(value)))];
}

function matchesAny(actual, selected) {
  return selected.length === 0 || selected.some(value => (actual ?? []).includes(value));
}

function matchesValue(actual, selected) {
  return selected.length === 0 || selected.includes(actual);
}

function getDueBoundaries(currentDate) {
  const now = currentDate instanceof Date && Number.isFinite(currentDate.getTime())
    ? currentDate
    : new Date();
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const tomorrow = new Date(today);
  tomorrow.setDate(today.getDate() + 1);
  const afterSunday = new Date(today);
  afterSunday.setDate(today.getDate() + ((7 - today.getDay()) % 7) + 1);
  return { today, tomorrow, afterSunday };
}

function matchesDueDate(value, range, boundaries) {
  if (!range) return true;
  if (range === "no-date") return value == null;
  if (value == null) return false;
  const due = new Date(value);
  if (!Number.isFinite(due.getTime())) return false;
  if (range === "overdue") return due < boundaries.today;
  if (range === "today") return due >= boundaries.today && due < boundaries.tomorrow;
  if (range === "this-week") return due >= boundaries.today && due < boundaries.afterSunday;
  return due >= boundaries.afterSunday;
}

globalThis.PlannerFilters = { createDefaultPreferences, sanitizePreferences, filterBoard };
})();
