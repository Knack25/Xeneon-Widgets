# Planner Task Organization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Planner task metadata editing, checklist management, persistent per-board filtering, persistent My tasks state, and immediate cached resume to the Planner Edge widget.

**Architecture:** Extend the helper with typed Graph models and focused HTTP routes backed by narrow services. Keep filter evaluation in a pure widget module, Planner data caching in the helper, and bootstrap the widget from cached data before reconciling a live refresh without resetting dialogs, drafts, or scroll positions.

**Tech Stack:** .NET 10 minimal APIs, Microsoft Graph v1.0 REST, `System.Text.Json`, xUnit, browser JavaScript and CSS, Node's built-in test runner.

**Spec:** `planner-edge-widget/docs/superpowers/specs/2026-09-21-planner-task-organization-design.md`

## Global Constraints

- Continue using delegated `User.Read` and `Tasks.ReadWrite`; add no Graph permission.
- Keep legacy Planner routes and `/api/planner` aliases behaviorally identical.
- Use focused public routes, never an arbitrary Graph PATCH endpoint.
- Preserve Planner bucket and task ordering while filters are active.
- Label names are read-only; users may assign only existing named labels.
- New tasks start at `percentComplete: 0` and default to priority `5`.
- Search is session-only; structured filters and My tasks persist per plan.
- Planner data remains helper-owned; browser storage holds scroll state only.
- Touch interactions must not depend on hover or precision dragging.
- All commits follow Conventional Commits 1.0.0.

## Review Focus

- Never show a cached snapshot belonging to a previously selected plan; Task 4 pins the plan-ID guard.
- A live refresh must not close details or replace drafts; Task 7 pins the refresh race.
- Deleted label, bucket, or member IDs must be removed from saved filters while valid IDs survive; Task 5 pins sanitization.
- Reject a start date after a due date before contacting Graph; Task 2 pins date consistency.
- Surface ETag conflicts while leaving user drafts retryable; Tasks 2, 3, and 6 pin this behavior.

---

### Task 1: Planner Read Models And Label Catalog

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/GraphModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/IPlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerDisplayService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskDetailsService.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerDisplayServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskDetailsServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/ContractTests.cs`

**Interfaces:**
- Consumes: Existing `GraphTask`, `TaskDisplay`, `TaskDetailsResponse`, and `BoardDisplay`.
- Produces: `GraphPlanLabel`, `GetPlanLabelsAsync`, applied-category IDs on tasks, and label/start/progress/priority fields in board and detail responses.

- [ ] **Step 1: Write failing mapping and contract tests**

```csharp
[Fact]
public async Task GetTasksAsync_MapsStartAndAppliedCategories()
{
    var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
        ? """{"orderHint":"a"}"""
        : """{"value":[{"id":"task","title":"Work","planId":"plan","percentComplete":50,"priority":3,"startDateTime":"2026-09-21T12:00:00Z","appliedCategories":{"category1":true,"category2":false}}]}""");
    var task = Assert.Single(await CreateClient(handler).GetTasksAsync("plan", CancellationToken.None));
    Assert.Equal(["category1"], task.AppliedCategories);
    Assert.NotNull(task.StartDateTime);
}

[Fact]
public async Task GetPlanLabelsAsync_ReturnsOnlyNamedCategories()
{
    var handler = new StubHandler(_ => """{"categoryDescriptions":{"category1":"Blocked","category2":null,"category25":"Release"}}""");
    Assert.Equal(["category1", "category25"],
        (await CreateClient(handler).GetPlanLabelsAsync("plan", CancellationToken.None)).Select(x => x.Id));
}
```

Update contract tests to construct old cached JSON without new fields and new responses with labels, start date, progress, priority, and applied labels.

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~GraphClientTests|FullyQualifiedName~PlannerDisplayServiceTests|FullyQualifiedName~TaskDetailsServiceTests|FullyQualifiedName~ContractTests"
```

Expected: compile failure for missing label and task fields.

- [ ] **Step 3: Implement typed parsing and response mapping**

```csharp
public sealed record GraphPlanLabel(string Id, string Name);
public sealed record LabelDisplay(string LabelId, string Name);
```

Append `IReadOnlyList<string>? AppliedCategories = null` to `GraphTask`. Add `GetPlanLabelsAsync(string planId, CancellationToken)` using `GET planner/plans/{planId}/details`; enumerate `category1` through `category25` and omit blank names. Parse only `appliedCategories` properties whose value is `true`.

Append nullable/defaulted new fields to API records so older cached JSON still deserializes. `BoardDisplay` gains labels; `TaskDisplay` and `TaskDetailsResponse` gain start date and label IDs, with details also returning priority and progress. Map all fields in display/detail services.

- [ ] **Step 4: Run the focused tests and verify pass**

Run Step 2's command. Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): expose task organization metadata"
```

### Task 2: Focused Task Metadata Updates

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskMetadataService.cs`
- Create: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskMetadataServiceTests.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/GraphModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/IPlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerIntegrationTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthConfigurationTests.cs`

**Interfaces:**
- Consumes: Task 1's task/label models, `IPlannerSettingsStore`, and `IMemoryCache`.
- Produces: `GraphTaskUpdate`, `UpdateTaskAsync`, and service methods `SetTitleAsync`, `SetProgressAsync`, `SetPriorityAsync`, `SetStartDateAsync`, `SetLabelsAsync`.

- [ ] **Step 1: Write failing service and Graph-body tests**

```csharp
[Theory]
[InlineData(0)]
[InlineData(50)]
[InlineData(100)]
public async Task SetProgressAsync_AcceptsPlannerValues(int value)
{
    var fixture = CreateFixture();
    await fixture.Service.SetProgressAsync("task", value, CancellationToken.None);
    Assert.Equal(value, Assert.Single(fixture.Graph.Updates).Update.PercentComplete);
}

[Fact]
public async Task SetStartDateAsync_RejectsDateAfterDue()
{
    var fixture = CreateFixture(due: new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    await Assert.ThrowsAsync<ArgumentException>(() =>
        fixture.Service.SetStartDateAsync("task", "2026-09-21", CancellationToken.None));
    Assert.Empty(fixture.Graph.Updates);
}
```

Cover title trimming/255 limit, values `0,50,100`, priorities `1,3,5,9`, nullable dates, selected-plan ownership, named-label validation, preservation of unnamed categories, `If-Match`, and an auth test asserting Planner scopes remain exactly `User.Read` and `Tasks.ReadWrite`.

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~TaskMetadataServiceTests|FullyQualifiedName~GraphClientTests|FullyQualifiedName~PlannerIntegrationTests|FullyQualifiedName~MicrosoftAuthConfigurationTests"
```

Expected: compile failure for the new service and routes.

- [ ] **Step 3: Implement typed internal updates and focused routes**

```csharp
public sealed record GraphTaskUpdate(string? Title = null, int? PercentComplete = null,
    int? Priority = null, DateTimeOffset? StartDateTime = null, bool ClearStartDate = false,
    IReadOnlyDictionary<string, bool?>? AppliedCategories = null);
```

Build PATCH JSON from requested properties only; `ClearStartDate` writes `startDateTime: null`. Every service method loads the current task, verifies the selected plan, uses its ETag, and invalidates detail cache. Parse dates as `yyyy-MM-dd` at noon UTC. For labels, send true/false only for named categories changed by the user and leave unnamed applied categories untouched.

Add request records `TitleRequest`, `ProgressRequest`, `PriorityRequest`, `StartDateRequest`, and `LabelsRequest`. Map focused PUT routes `/title`, `/progress`, `/priority`, `/start-date`, and `/labels`, each returning 204.

Extend `CreateTaskRequest` and `CreateTaskAsync` with optional `StartDate`, `Priority`, and `LabelIds`; validate dates/labels and PATCH labels only if Graph creation cannot include them. New tasks omit progress and therefore start at zero.

- [ ] **Step 4: Run focused tests and verify pass**

Run Step 2's command. Expected: PASS with unchanged permissions.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): add task metadata updates"
```

### Task 3: Complete Checklist Management

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/ChecklistService.cs`
- Create: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/ChecklistServiceTests.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/IPlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerIntegrationTests.cs`

**Interfaces:**
- Consumes: Ordered `GraphTaskDetails.Checklist` and detail ETags.
- Produces: `ChecklistService.AddAsync`, `RenameAsync`, `DeleteAsync`, `MoveAsync`, plus `PatchChecklistAsync`.

- [ ] **Step 1: Write failing mutation and order-hint tests**

```csharp
[Fact]
public async Task MoveAsync_UsesPlannerRelativeHint()
{
    var fixture = CreateFixture(
        new GraphChecklistItem("first", "First", false, "first"),
        new GraphChecklistItem("second", "Second", false, "second"));
    await fixture.Service.MoveAsync("task", "second", "up", CancellationToken.None);
    Assert.Equal(" first!", fixture.Graph.LastOrderHint);
}
```

Test add/rename title limits, GUID item IDs, null deletion JSON, missing items, top/bottom no-ops, current detail ETag, and Graph conflict propagation.

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~ChecklistServiceTests|FullyQualifiedName~GraphClientTests|FullyQualifiedName~PlannerIntegrationTests"
```

Expected: compile failure for checklist management.

- [ ] **Step 3: Implement service and routes**

Use one details PATCH method internally. Add-to-bottom uses `<lastHint> !`, or ` !` when empty. For move, remove the item from the ordered working list, insert one position up/down, then calculate:

```csharp
var previous = target > 0 ? reordered[target - 1].OrderHint ?? "" : "";
var next = target + 1 < reordered.Count ? reordered[target + 1].OrderHint ?? "" : "";
var hint = $"{previous} {next}!";
```

Add `ChecklistTitleRequest` and `ChecklistPositionRequest`. Map POST `/tasks/{taskId}/checklist`, PUT and DELETE `/tasks/{taskId}/checklist/{itemId}`, and PUT `/tasks/{taskId}/checklist/{itemId}/position`. Retain the existing completion route. Invalidate task details after success.

- [ ] **Step 4: Run focused tests and verify pass**

Run Step 2's command. Expected: PASS, including no write at move boundaries.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): add checklist management"
```

### Task 4: Per-Plan Preferences And Cache-Only Display

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerViewPreferenceService.cs`
- Create: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerViewPreferenceServiceTests.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Storage/PlannerSettingsStore.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerCoordinator.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/StorageTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerIntegrationTests.cs`

**Interfaces:**
- Consumes: Existing settings JSON and cached `BoardDisplay`.
- Produces: `PlannerFilterSettings`, `PlanViewPreferences`, GET/PUT `/view-preferences/{planId}`, and GET `/display/cached`.

- [ ] **Step 1: Write failing compatibility, isolation, and plan-guard tests**

```csharp
[Fact]
public async Task Preferences_AreIndependentPerPlan()
{
    var service = CreateService();
    await service.SaveAsync("plan-a", Preferences(myTasks: true), CancellationToken.None);
    Assert.True((await service.GetAsync("plan-a", CancellationToken.None)).MyTasks);
    Assert.False((await service.GetAsync("plan-b", CancellationToken.None)).MyTasks);
}

[Fact]
public async Task CachedDisplay_RejectsAnotherPlan()
{
    var coordinator = CreateCoordinator(selectedPlan: "new", cachedPlan: "old");
    Assert.Null(await coordinator.GetCachedDisplayAsync(CancellationToken.None));
}
```

Deserialize a pre-feature settings file containing only the original three fields and assert default preferences.

- [ ] **Step 2: Run tests to verify failure**

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~StorageTests|FullyQualifiedName~PlannerViewPreferenceServiceTests|FullyQualifiedName~PlannerIntegrationTests"
```

Expected: compile failure for preferences and cache-only coordination.

- [ ] **Step 3: Implement compatible settings and focused routes**

```csharp
public sealed record PlannerFilterSettings(IReadOnlyList<string> AssigneeIds,
    IReadOnlyList<string> LabelIds, IReadOnlyList<int> Priorities,
    IReadOnlyList<string> BucketIds, IReadOnlyList<int> ProgressValues, string? DueDateRange);
public sealed record PlanViewPreferences(bool MyTasks, PlannerFilterSettings Filters);
```

Append `IReadOnlyDictionary<string, PlanViewPreferences>? PlanViews = null` to `SettingsDto`. Normalize null arrays, deduplicate IDs, allow priorities `1,3,5,9`, progress `0,50,100`, and due ranges `overdue,today,this-week,later,no-date`. Save one selected-plan entry without dropping others.

Add `PlannerCoordinator.GetCachedDisplayAsync` that reads settings/cache only and returns null unless plan IDs match. Map GET `/display/cached` and GET/PUT `/view-preferences/{planId}`; preference service rejects a plan ID different from the selected plan.

- [ ] **Step 4: Run focused tests and verify pass**

Run Step 2's command. Expected: PASS, including old settings upgrade.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): persist board view preferences"
```

### Task 5: Pure Widget Filters And Scroll State

**Files:**
- Create: `planner-edge-widget/widget/src/filters.js`
- Create: `planner-edge-widget/widget/src/view-state.js`
- Create: `planner-edge-widget/widget/tests/filters.test.mjs`
- Create: `planner-edge-widget/widget/tests/view-state.test.mjs`
- Modify: `planner-edge-widget/widget/index.html`
- Modify: `planner-edge-widget/widget/tests/all.test.mjs`
- Modify: `planner-edge-widget/widget/tests/entry-points.test.mjs`

**Interfaces:**
- Consumes: Board JSON, preferences, current user ID, search text, and current date.
- Produces: `PlannerFilters.createDefaultPreferences`, `sanitizePreferences`, `filterBoard`; `PlannerViewState.load` and `save`.

- [ ] **Step 1: Write failing pure-module tests**

```javascript
test("groups combine with AND and values within a group use OR", () => {
  const result = filters.filterBoard(board, {
    myTasks: false,
    filters: { assigneeIds: ["alex", "sam"], labelIds: ["category1"], priorities: [1, 3],
      bucketIds: [], progressValues: [], dueDateRange: null }
  }, "me", "", new Date("2026-09-21T12:00:00Z"));
  assert.deepEqual(result.buckets.flatMap(x => x.tasks).map(x => x.taskId), ["match"]);
});
```

Test every due preset at local-day boundaries: overdue is before today, today is
the local current date, this week is today through the upcoming Sunday, later is
after that Sunday, and no-date is null. Also test My tasks plus assignee
filtering, case-insensitive search, unchanged ordering, visible empty buckets,
deleted-ID sanitization, malformed storage JSON, and nonnegative scroll values.

- [ ] **Step 2: Run tests to verify failure**

```powershell
node --test planner-edge-widget/widget/tests/filters.test.mjs planner-edge-widget/widget/tests/view-state.test.mjs
```

Expected: module-not-found failure.

- [ ] **Step 3: Implement classic-script modules**

```javascript
globalThis.PlannerFilters = { createDefaultPreferences, sanitizePreferences, filterBoard };
globalThis.PlannerViewState = { load, save };
```

Never sort board arrays. Multiple values inside a category are OR; categories, My tasks, and search are AND. Store only `{boardScrollLeft,bucketScrollTops}` at `planner-edge:view:${planId}` and clamp malformed values to zero. Load both scripts before `app.js` and add tests to `all.test.mjs`.

- [ ] **Step 4: Run focused and full widget tests**

```powershell
node --test planner-edge-widget/widget/tests/filters.test.mjs planner-edge-widget/widget/tests/view-state.test.mjs
npm test --prefix planner-edge-widget/widget
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add planner-edge-widget/widget
git commit -m "feat(planner): add persistent board filtering state"
```

### Task 6: Widget Organization And Checklist Controls

**Files:**
- Modify: `planner-edge-widget/widget/src/api.js`
- Modify: `planner-edge-widget/widget/src/state.js`
- Modify: `planner-edge-widget/widget/src/app.js`
- Modify: `planner-edge-widget/widget/styles.css`
- Test: `planner-edge-widget/widget/tests/state.test.mjs`
- Test: `planner-edge-widget/widget/tests/interactions.test.mjs`
- Test: `planner-edge-widget/widget/tests/startup.test.mjs`

**Interfaces:**
- Consumes: Tasks 2-5 helper routes and pure modules.
- Produces: Filter/search panels, persistent My tasks, editable task metadata, enhanced New task, and checklist editing controls.

- [ ] **Step 1: Write failing interactions for persistence and task metadata**

Add `My tasks and filters persist per board while search does not`: return
`{myTasks:true,filters:{...}}` from `/view-preferences/plan`, toggle My tasks,
and assert the PUT body contains `myTasks:false` and no `searchText` property.
Enter a title search, assert the rendered task set changes, and again assert no
preference PUT contains the search string.

Add `new task sends start date priority and labels without progress`: fill the
new-task controls, submit, parse the `/tasks` POST body, and assert
`startDate:"2026-09-21"`, `date:"2026-09-25"`, `priority:3`,
`labelIds:["category1"]`, and no `percentComplete` property.

Add tests for card priority/labels/progress, title edits, progress confirmation
at 100, start date, label assignment, saved-filter errors, and failed writes
retaining drafts. Each mutation test must assert the exact focused route,
method, and JSON body.

- [ ] **Step 2: Write failing checklist interactions**

Add `checklist supports add rename delete confirmation and button reordering`.
Assert add sends POST `{title:"New item"}`, rename sends PUT
`{title:"Renamed"}`, delete sends no request until its confirmation is accepted
and then uses DELETE, and moving down sends PUT `{direction:"down"}` to the
item's `/position` route.

Assert first Up/last Down disabled, deletion has distinct confirmation copy,
completed-item confirmation remains, outside-tap closes only the active dialog,
and no drag listener captures touch scrolling.

- [ ] **Step 3: Run tests to verify failure**

```powershell
node --test planner-edge-widget/widget/tests/state.test.mjs planner-edge-widget/widget/tests/interactions.test.mjs planner-edge-widget/widget/tests/startup.test.mjs
```

Expected: failures for missing controls and API calls.

- [ ] **Step 4: Implement focused API and state methods**

Add API wrappers matching Tasks 2-4 and checklist add/rename/delete/position. Load and sanitize preferences per plan; save each structured-filter or My tasks change. Keep search only in memory. Add separate state for title/label/checklist drafts and checklist-delete confirmation so failed calls retain input.

- [ ] **Step 5: Implement touch-first UI**

Cards show prominent Urgent/Important markers, label colors, In progress, existing due date, and checklist preview. Add stable search/filter icon controls and a scrolling multi-select filter dialog with active count and Clear filters.

Details edit title, progress, priority, start date, due date, assignees, bucket, and labels. Progress 100 reuses explicit completion confirmation. Checklist rows use edit, delete, move-up, and move-down icon buttons; add and rename are inline with Save/Cancel. Details retain notes and chat below.

New task sends title, bucket, assignees, start/due dates, priority 5 by default, and labels. Keep all controls touch-sized, preserve outside-tap closing, and prevent panel scroll from locking the board after closure.

- [ ] **Step 6: Run focused and full tests**

```powershell
node --test planner-edge-widget/widget/tests/state.test.mjs planner-edge-widget/widget/tests/interactions.test.mjs planner-edge-widget/widget/tests/startup.test.mjs
npm test --prefix planner-edge-widget/widget
```

Expected: PASS with no notes, chat, assignment, bucket, due-date, completion, or outside-tap regression.

- [ ] **Step 7: Commit**

```powershell
git add planner-edge-widget/widget
git commit -m "feat(planner): add task organization controls"
```

### Task 7: Cached Resume, Documentation, And Verification

**Files:**
- Modify: `planner-edge-widget/widget/src/api.js`
- Modify: `planner-edge-widget/widget/src/state.js`
- Modify: `planner-edge-widget/widget/src/app.js`
- Modify: `planner-edge-widget/widget/tests/state.test.mjs`
- Modify: `planner-edge-widget/widget/tests/startup.test.mjs`
- Modify: `planner-edge-widget/widget/tests/interactions.test.mjs`
- Modify: `planner-edge-widget/README.md`
- Modify: `planner-edge-widget/docs/setup.md`
- Modify: `microsoft-widgets-helper/README.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: GET `/display/cached` and `PlannerViewState`.
- Produces: `getCachedDisplay`, non-destructive `applyDisplayRefresh`, cache-first startup, persisted scroll restoration, and updated user documentation.

- [ ] **Step 1: Write failing cache-first and race tests**

Add `startup renders cache before the unresolved live request`: make
`/display/cached` return a board titled `Cached Board`, keep `/display` pending
behind a manually controlled promise, settle the cache request, and assert the
HTML contains `Cached Board` before resolving the live promise. Resolve a board
titled `Live Board` and assert it replaces the cached title.

Add `live refresh preserves open dialog drafts filters and scroll`: open task
details, enter distinct title and notes drafts, set board and bucket offsets,
resolve the live display, and assert both draft strings, the active filter, the
dialog task ID, and each offset are unchanged.

Also test absent cache, failed live refresh retaining Offline view, late
old-plan responses being ignored, and restored offsets after reload.

- [ ] **Step 2: Run tests to verify failure**

```powershell
node --test planner-edge-widget/widget/tests/startup.test.mjs planner-edge-widget/widget/tests/state.test.mjs planner-edge-widget/widget/tests/interactions.test.mjs
```

Expected: failure because startup blocks on live display.

- [ ] **Step 3: Implement non-destructive cache-first startup**

```javascript
function applyDisplayRefresh(state, display) {
  return { ...state, mode: "board", display, error: null, pendingTask: null, completing: false };
}
```

Request cache and live display concurrently; render cache first when present, then apply live data only for the active plan generation. Do not replace `dialog`, drafts, filters, or scroll. Invalidate detail cache only for tasks whose ETags changed or disappeared. A live failure leaves cached data visible and stale.

Save scroll offsets through `PlannerViewState` before rerenders and from throttled scroll listeners. Restore saved values only on the first render for a plan; use current DOM values on subsequent renders.

- [ ] **Step 4: Add package assertions and update documentation**

Extend entry-point/package tests to ensure `filters.js` and `view-state.js` load before `app.js` in packaged and helper-hosted views. Document every new control, per-board persistence, cache-first refresh, unchanged permissions, and that Outlook fast resume is separate future work.

- [ ] **Step 5: Run complete automated verification**

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj
npm test --prefix planner-edge-widget/widget
dotnet build microsoft-widgets-helper/src/MicrosoftWidgets.Helper/MicrosoftWidgets.Helper.csproj -c Release
git diff --check
git status --short
```

Expected: all tests pass, Release build succeeds, `git diff --check` is empty, and status contains only intended changes.

- [ ] **Step 6: Perform manual XENEON-sized verification**

At supported S, M, L, and XL sizes, and on the physical EDGE when available, verify cached-first rendering; restored filters, My tasks, and scroll; filter/detail scrolling after outside-tap close; metadata/checklist writes matching Planner web; stable Planner ordering; and containment of long names.

- [ ] **Step 7: Commit documentation and performance work**

```powershell
git add README.md planner-edge-widget microsoft-widgets-helper/README.md
git commit -m "perf(planner): restore cached board immediately"
```

- [ ] **Step 8: Request final whole-branch review**

Review the branch against the linked specification, focusing on permission stability, stale-response races, order-hint math, ETag conflicts, saved-filter sanitization, and touchscreen scrolling. Address each finding with a focused Conventional Commit, rerun Step 5, and report the physical EDGE smoke test separately if hardware is unavailable.
