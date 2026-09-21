import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const context = { Date };
runInNewContext(readFileSync(new URL("../src/filters.js", import.meta.url), "utf8"), context);
const filters = context.PlannerFilters;

const emptyFilters = () => ({
  assigneeIds: [], labelIds: [], priorities: [], bucketIds: [], progressValues: [], dueDateRange: null
});

const task = (taskId, overrides = {}) => ({
  taskId, title: taskId, bucketId: "one", dueDateTime: null, priority: 5,
  percentComplete: 0, assignments: [], labelIds: [], ...overrides
});

const board = {
  planId: "plan",
  labels: [{ labelId: "category1", name: "Release" }, { labelId: "category2", name: "Support" }],
  buckets: [
    { bucketId: "one", name: "One", tasks: [
      task("wrong-label", { assignments: ["alex"], labelIds: ["category2"], priority: 1 }),
      task("match", { assignments: ["sam"], labelIds: ["category1"], priority: 3 }),
      task("wrong-priority", { assignments: ["alex"], labelIds: ["category1"], priority: 5 })
    ] },
    { bucketId: "two", name: "Two", tasks: [] }
  ]
};

function ids(result) {
  return Array.from(result.buckets.flatMap(bucket => bucket.tasks), item => item.taskId);
}

function localIso(year, month, day, hour = 12) {
  return new Date(year, month, day, hour).toISOString();
}

test("default preferences contain no persisted search state", () => {
  assert.deepEqual(JSON.parse(JSON.stringify(filters.createDefaultPreferences())), {
    myTasks: false,
    filters: emptyFilters()
  });
  assert.equal("searchText" in filters.createDefaultPreferences(), false);
});

test("groups combine with AND and values within a group use OR", () => {
  const result = filters.filterBoard(board, {
    myTasks: false,
    filters: { assigneeIds: ["alex", "sam"], labelIds: ["category1"], priorities: [1, 3],
      bucketIds: [], progressValues: [], dueDateRange: null }
  }, "me", "", new Date(2026, 8, 21, 12));

  assert.deepEqual(ids(result), ["match"]);
});

test("My tasks and explicit assignees are separate AND conditions", () => {
  const source = { buckets: [{ bucketId: "one", tasks: [
    task("mine-and-alex", { assignments: ["me", "alex"] }),
    task("mine-only", { assignments: ["me"] }),
    task("alex-only", { assignments: ["alex"] })
  ] }] };
  const preferences = { myTasks: true, filters: { ...emptyFilters(), assigneeIds: ["alex"] } };

  assert.deepEqual(ids(filters.filterBoard(source, preferences, "me", "", new Date())), ["mine-and-alex"]);
});

test("title search is case-insensitive and combines with structured filters", () => {
  const source = { buckets: [{ bucketId: "one", tasks: [
    task("release", { title: "Ship RELEASE", priority: 1 }),
    task("other", { title: "Ship release notes", priority: 5 })
  ] }] };
  const preferences = { myTasks: false, filters: { ...emptyFilters(), priorities: [1] } };

  assert.deepEqual(ids(filters.filterBoard(source, preferences, null, "ReLeAsE", new Date())), ["release"]);
});

test("due presets use local calendar boundaries through the upcoming Sunday", () => {
  const now = new Date(2026, 8, 21, 12);
  const source = { buckets: [{ bucketId: "one", tasks: [
    task("overdue", { dueDateTime: localIso(2026, 8, 20, 23) }),
    task("today-start", { dueDateTime: localIso(2026, 8, 21, 0) }),
    task("today-end", { dueDateTime: localIso(2026, 8, 21, 23) }),
    task("sunday", { dueDateTime: localIso(2026, 8, 27, 23) }),
    task("later", { dueDateTime: localIso(2026, 8, 28, 0) }),
    task("no-date")
  ] }] };
  const matching = dueDateRange => ids(filters.filterBoard(source,
    { myTasks: false, filters: { ...emptyFilters(), dueDateRange } }, null, "", now));

  assert.deepEqual(matching("overdue"), ["overdue"]);
  assert.deepEqual(matching("today"), ["today-start", "today-end"]);
  assert.deepEqual(matching("this-week"), ["today-start", "today-end", "sunday"]);
  assert.deepEqual(matching("later"), ["later"]);
  assert.deepEqual(matching("no-date"), ["no-date"]);
});

test("filtering preserves Planner ordering, input data, and empty buckets", () => {
  const source = {
    planId: "plan", buckets: [
      { bucketId: "first", tasks: [task("first", { bucketId: "first", priority: 5 }), task("second", { bucketId: "first", priority: 1 })] },
      { bucketId: "empty", tasks: [] },
      { bucketId: "last", tasks: [task("third", { bucketId: "last", priority: 5 })] }
    ]
  };
  const before = JSON.stringify(source);
  const result = filters.filterBoard(source,
    { myTasks: false, filters: { ...emptyFilters(), priorities: [5] } }, null, "", new Date());

  assert.deepEqual(Array.from(result.buckets, bucket => bucket.bucketId), ["first", "empty", "last"]);
  assert.deepEqual(ids(result), ["first", "third"]);
  assert.equal(result.buckets[1].tasks.length, 0);
  assert.equal(JSON.stringify(source), before);
});

test("deleted IDs are removed while valid structured selections survive", () => {
  const preferences = {
    myTasks: true,
    filters: {
      assigneeIds: ["alex", "deleted-person"], labelIds: ["category1", "deleted-label"],
      priorities: [1, 2, 9], bucketIds: ["one", "deleted-bucket"], progressValues: [0, 25, 100],
      dueDateRange: "today"
    },
    searchText: "do not persist"
  };
  const sanitized = filters.sanitizePreferences(preferences, board,
    [{ id: "alex", displayName: "Alex" }, { id: "sam", displayName: "Sam" }]);

  assert.deepEqual(JSON.parse(JSON.stringify(sanitized)), {
    myTasks: true,
    filters: {
      assigneeIds: ["alex"], labelIds: ["category1"], priorities: [1, 9], bucketIds: ["one"],
      progressValues: [0, 100], dueDateRange: "today"
    }
  });
});
