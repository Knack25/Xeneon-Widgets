# Planner Edge Interactions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add in-widget board switching, faithful bucket order, separate task and checklist tap targets, and a task detail view.

**Architecture:** Keep Microsoft Graph, token handling, writes, and cache in the local .NET helper. Extend its display contract and focused endpoints; keep the iCUE widget a small HTML/JavaScript client using the existing API and confirmation patterns.

**Tech Stack:** .NET 10 minimal API, xUnit, Microsoft Graph REST, MSAL, browser JavaScript, Node tests, icuewidget CLI.

**Spec:** `planner-edge-widget/docs/superpowers/specs/2026-09-17-planner-edge-interactions-design.md`

## Global Constraints

- Preserve the working `v0.1.4` tag and make the next package `0.2.0`.
- The helper binds only to `http://localhost:8787`; tokens never enter the widget.
- Keep the existing `User.Read` and `Tasks.ReadWrite` scopes, adding only `User.ReadBasic.All` for assignee names.
- Graph checklist writes require the current task-details ETag and must update only the chosen item.
- Do not create or edit Planner tasks beyond existing task completion and new checklist completion.

## File Map

- `helper/PlannerEdge.Helper/Graph/{GraphModels,IPlannerGraphClient,PlannerGraphClient}.cs`: Graph parsing, details, checklist updates, user names.
- `helper/PlannerEdge.Helper/Planner/{PlannerDisplayService,ChecklistCompletionService}.cs`: display assembly and safe checklist writes.
- `helper/PlannerEdge.Helper/Contracts/ApiModels.cs` and `Program.cs`: widget-facing data and endpoints.
- `helper/PlannerEdge.Helper/Auth/MicrosoftAuthService.cs`: one extra delegated scope.
- `widget/src/{api,state,app}.js`, `widget/styles.css`: picker, task detail, tap targets, confirmation.
- Matching xUnit and Node test files cover behavior before production edits.

---

### Task 1: Preserve Bucket Order

**Files:** Modify `helper/PlannerEdge.Helper/Graph/GraphModels.cs`, `Graph/PlannerGraphClient.cs`, `Planner/PlannerDisplayService.cs`; test `helper/PlannerEdge.Helper.Tests/{GraphClientTests,PlannerDisplayServiceTests}.cs`.

**Interfaces:** Extend `GraphBucket` with `string? OrderHint`; keep `GetBucketsAsync` and `GetDisplayAsync` signatures unchanged.

- [ ] **Step 1: Write failing tests.** Parse a Graph bucket with `orderHint: "b"`. Give the display service shuffled buckets with hints `"z"`, `"a"`, `"m"`; assert output `a,m,z`, followed by synthetic `No bucket` when relevant.

```csharp
Assert.Equal(["First", "Middle", "Last"], board.Buckets.Select(b => b.Name));
```
- [ ] **Step 2: Run targeted tests.** `dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter 'FullyQualifiedName~GraphClientTests|FullyQualifiedName~PlannerDisplayServiceTests' --no-restore -m:1`; expect the new assertions to fail.
- [ ] **Step 3: Implement.** Parse `orderHint` with `TryGetProperty`, and order buckets using `OrderBy(b => b.OrderHint, StringComparer.Ordinal)` before mapping them. Keep a deterministic ID tie-breaker and put missing hints after present hints.

```csharp
var orderedBuckets = buckets.OrderBy(b => b.OrderHint is null)
    .ThenBy(b => b.OrderHint, StringComparer.Ordinal)
    .ThenBy(b => b.Id, StringComparer.Ordinal);
```
- [ ] **Step 4: Re-run targeted tests and commit.** `git commit -m "fix: display Planner buckets in order-hint order"`.

### Task 2: Read Checklists And Complete One Item

**Files:** Modify `Graph/{GraphModels,IPlannerGraphClient,PlannerGraphClient}.cs`, `Contracts/ApiModels.cs`, `Planner/PlannerDisplayService.cs`, `Program.cs`; create `Planner/ChecklistCompletionService.cs`; test `helper/PlannerEdge.Helper.Tests/{GraphClientTests,PlannerDisplayServiceTests,ChecklistCompletionServiceTests}.cs`.

**Interfaces:** Add `GraphTaskDetails(string ETag, IReadOnlyList<GraphChecklistItem> Checklist)` and `GraphChecklistItem(string Id, string Title, bool IsChecked, string? OrderHint)`. Add `GetTaskDetailsAsync(taskId, ct)` and `CompleteChecklistItemAsync(taskId, itemId, etag, ct)` to `IPlannerGraphClient`. Add `TaskDetailsResponse` for the focused `GET /tasks/{taskId}/details`; leave `TaskDisplay` unchanged so old cached boards remain readable. Write endpoint: `POST /tasks/{taskId}/checklist/{itemId}/complete`.

- [ ] **Step 1: Write failing tests.** Parse the keyed `checklist` object from `GET planner/tasks/{id}/details`, preserving item IDs and ordinal `orderHint` order. Verify an unchecked item produces a PATCH to the details URL with `If-Match` and a body containing only `checklist: { itemId: { isChecked: true } }`. Checked items return success without PATCH; missing items and ETag conflicts return errors.

```csharp
Assert.Equal("item-1", details.Checklist[0].Id);
Assert.True(request.Headers.IfMatch.Any());
Assert.Equal("{\"checklist\":{\"item-1\":{\"isChecked\":true}}", requestBody);
```
- [ ] **Step 2: Run targeted xUnit tests** and observe failures for the absent methods and contract.
- [ ] **Step 3: Implement Graph read/write and service.** Use `JsonDocument` for the open checklist object and `JsonSerializer` for the PATCH body. Fetch the latest details and ETag immediately before writing. Do not retry a 409/412 blindly; reuse the existing conflict response. Register the service and endpoint in `Program.cs`.

```csharp
var details = await graph.GetTaskDetailsAsync(taskId, ct);
var item = details.Checklist.SingleOrDefault(value => value.Id == itemId)
    ?? throw new InvalidOperationException("Checklist item was not found.");
if (!item.IsChecked)
    await graph.CompleteChecklistItemAsync(taskId, itemId, details.ETag, ct);
```
- [ ] **Step 4: Add checklist previews without blocking the board.** Return the current task list immediately; expose a focused `GET /tasks/{taskId}/details` for on-demand details, and use a short-lived helper cache for repeated reads. The widget bounds concurrent detail requests to four. A failed detail read returns an error for that task only. Add tests for cache reuse and partial failure.
- [ ] **Step 5: Run all helper tests and commit.** `dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --no-restore -m:1`; `git commit -m "feat: read and complete Planner checklist items"`.

### Task 3: Resolve Assignees And Select Boards

**Files:** Modify `Auth/MicrosoftAuthService.cs`, `Graph/{IPlannerGraphClient,PlannerGraphClient}.cs`, `Planner/PlannerDisplayService.cs`, `Contracts/ApiModels.cs`, `Program.cs`; test `helper/PlannerEdge.Helper.Tests/{GraphClientTests,PlannerDisplayServiceTests,StorageTests}.cs`.

**Interfaces:** Add `GetUserDisplayNameAsync(userId, ct)` to the Graph client. Add `PUT /selected-plan` accepting `SelectedPlanRequest(string PlanId)`; validate the ID against `/plans`, preserve `HideCompletedTasks`, save ID/title through `IPlannerSettingsStore`, and return saved settings.

- [ ] **Step 1: Write failing tests.** Verify a user lookup requests `/users/{id}?$select=displayName`; a 403 or missing user yields "Assigned person unavailable" without hiding the task. Verify selecting a valid plan saves its current title and preserves the completed-task setting, while an unknown plan leaves settings untouched.

```csharp
Assert.Equal("New board", saved.SelectedPlanTitle);
Assert.True(saved.HideCompletedTasks);
Assert.Equal("Old board", settingsAfterUnknownPlan.SelectedPlanTitle);
```
- [ ] **Step 2: Run targeted xUnit tests** and observe failures for the absent methods and endpoint behavior.
- [ ] **Step 3: Implement.** Request `User.ReadBasic.All` in MSAL, cache names by ID, and resolve distinct assignee IDs with bounded concurrency. Add a selection service or minimal endpoint validation using `PlannerBoardService`; no raw ID should be the primary visible label. The existing `PlannerCoordinator` already rejects a cached display whose plan ID differs, so switching cannot show the old board as the new one.

```csharp
var plan = (await boards.GetPlansAsync(ct)).SingleOrDefault(p => p.PlanId == request.PlanId)
    ?? throw new ArgumentException("That board is no longer available.");
var current = await settings.LoadSettingsAsync(ct);
await settings.SaveSettingsAsync(current with { SelectedPlanId = plan.PlanId, SelectedPlanTitle = plan.Title }, ct);
```
- [ ] **Step 4: Run helper tests and commit.** `git commit -m "feat: select boards and show assignee names"`.

### Task 4: Widget Picker, Task Details, And Tap Targets

**Files:** Modify `widget/src/{api,state,app}.js`, `widget/styles.css`; add or extend `widget/tests/{state,startup,interactions}.test.mjs` and import the new test in `all.test.mjs`.

**Interfaces:** API functions `getPlans()`, `selectPlan(planId)`, `getTaskDetails(taskId)`, `completeChecklistItem(taskId,itemId)` call the helper endpoints above. State keeps `activeDialog` as `boardPicker`, `taskDetails`, `confirmTask`, or `confirmChecklist`, plus pending IDs and error.

- [ ] **Step 1: Write failing Node tests.** Assert the title opens the picker; a failed selection retains the board; a task checkbox opens task confirmation; the task body opens details; the first three ordered checklist items appear on the card; both preview and detail checklist taps open checklist confirmation; cancellation causes no POST; a second tap during a write is ignored.

```javascript
assert.match(app.innerHTML, /data-open-board-picker/);
assert.match(app.innerHTML, /data-complete-task/);
assert.match(app.innerHTML, /data-open-task/);
assert.equal((app.innerHTML.match(/data-checklist-item/g) || []).length, 3);
```
- [ ] **Step 2: Run `npm test` in `widget/`** and observe the new assertions fail.
- [ ] **Step 3: Implement API and state transitions.** Fetch available plans on picker open, save on selection, then refresh display. Fetch details lazily for visible cards with no more than four in flight and cache by task ID briefly; update an individual card when its details arrive. Do not let a failed detail fetch blank the board.

```javascript
async function selectPlan(planId) {
  return parseJsonResponse(await fetch(`${BASE_URL}/selected-plan`, {
    method: "PUT", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ planId })
  }));
}
```
- [ ] **Step 4: Implement semantic markup and CSS.** Use separate buttons for the task checkbox, task body, and checklist items; never nest buttons. Add close/cancel controls, focus treatment, `No checklist`, `Unassigned`, and `No due date` empty states, and responsive layout within the XENEON frame.
- [ ] **Step 5: Run `npm test` and commit.** `git commit -m "feat: switch boards and inspect Planner tasks"`.

### Task 5: Package And Verify The Next Version

**Files:** Modify `widget/manifest.json`, `docs/setup.md`; output ignored `dist/PlannerEdgeWidget-0.2.0.icuewidget`.

**Interfaces:** No new runtime interface.

- [ ] **Step 1: Add a package-version assertion** to `widget/tests/entry-points.test.mjs` and observe failure at the old version.

```javascript
assert.equal(JSON.parse(readFileSync(new URL("../manifest.json", import.meta.url))).version, "0.2.0");
```
- [ ] **Step 2: Set manifest version `0.2.0` and update setup text** for additional sign-in consent and board switching.
- [ ] **Step 3: Run `scripts/verify.ps1`, `scripts/publish-helper.ps1`, and `scripts/package.ps1`** from the project root. Confirm iCUE CLI validation and package creation.
- [ ] **Step 4: Check the native widget** on XENEON EDGE: board order against Teams, picker persistence, detail view, and task/checklist confirmation. Ask the user for account-dependent confirmation if the device cannot be controlled locally.
- [ ] **Step 5: Review diff, commit, and report.** `git diff --check`; `git commit -m "release: Planner Edge widget 0.2.0"`. Do not tag or push a release until device verification succeeds.
