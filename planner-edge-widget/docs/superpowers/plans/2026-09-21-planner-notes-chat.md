# Planner Notes and Task Chat Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let Planner Edge users explicitly save task notes and read or post the task's Microsoft Planner conversation without making chat permission a prerequisite for ordinary task actions.

**Architecture:** Keep the existing Planner token path limited to core task scopes, but request conversation consent during the bundled Planner connection and expose it as a separate capability. Add focused notes and chat services behind local helper endpoints; load chat lazily in the existing task-detail dialog and render Graph conversation HTML as server-normalized plain text.

**Tech Stack:** .NET 8 minimal API, MSAL, Microsoft Graph REST v1.0, xUnit, HtmlAgilityPack, vanilla JavaScript/CSS, Node test runner.

**Spec:** `planner-edge-widget/docs/superpowers/specs/2026-09-21-planner-notes-chat-design.md`

## Global Constraints

- Notes and comments are plain text in the widget; raw Microsoft conversation HTML is never inserted into the page.
- Missing `Group-Conversation.ReadWrite.All` permission disables only task chat.
- Existing comments cannot be edited or deleted.
- Notes and comment drafts survive failed requests.
- Existing backdrop dismissal, dialog scrolling, and board scroll restoration must continue working on the XENEON EDGE touchscreen.
- Use Conventional Commits for every commit.

## Review Focus

- A task from a different plan must not be allowed to read or write a conversation using the selected plan's group ID; pin this in `TaskChatServiceTests`.
- A malicious or stale continuation URL must be rejected unless it is HTTPS on `graph.microsoft.com` and matches the expected group/thread posts path; pin this in `TaskChatServiceTests`.
- A first comment whose thread is created but cannot be attached must not be automatically retried into duplicate conversations; pin the single-attempt behavior in `TaskChatServiceTests`.
- Closing task details while chat is loading must not reopen the dialog or show a cancellation error; pin this in `interactions.test.mjs`.
- Re-rendering after notes/chat state changes must preserve the detail panel's vertical scroll position as well as the board's scroll positions; pin this in `interactions.test.mjs`.

---

### Task 1: Bundled Conversation Consent With Core Planner Fallback

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/IGraphTokenProvider.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/MicrosoftAuthService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/MicrosoftAuthCapabilityService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/index.html`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthCapabilityTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthAcquisitionTests.cs`

**Interfaces:**
- Produces: `IGraphTokenProvider.GetConversationTokenAsync(CancellationToken)`.
- Produces: `MicrosoftAuthService.ConversationScopes` and `PlannerConnectScopes`.
- Produces: `IMicrosoftAuthService.EnableTaskChatAsync(CancellationToken)` and `POST /auth/enable-task-chat`.
- Produces: `MicrosoftAuthCapabilities.TaskChat` with `available`, `interaction_required`, `signed_out`, or `unavailable` state.

- [ ] **Step 1: Write failing authorization tests**

Add assertions that core Planner API token acquisition still requests only `User.Read` and `Tasks.ReadWrite`, Planner interactive sign-in requests those scopes plus `Group-Conversation.ReadWrite.All`, conversation token acquisition and the existing-user enable action request only the conversation scope, and capabilities report task chat independently.

```csharp
Assert.Equal(new[] { "User.Read", "Tasks.ReadWrite" }, MicrosoftAuthService.PlannerScopes);
Assert.Contains("Group-Conversation.ReadWrite.All", MicrosoftAuthService.PlannerConnectScopes);
Assert.Equal(new[] { "Group-Conversation.ReadWrite.All" }, MicrosoftAuthService.ConversationScopes);
Assert.Equal("interaction_required", capabilities.TaskChat.State);
Assert.Equal("available", capabilities.Planner.State);
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "MicrosoftAuthCapabilityTests|MicrosoftAuthAcquisitionTests"`

Expected: FAIL because the conversation scopes, token method, and capability do not exist.

- [ ] **Step 3: Implement split core/connect scopes**

Keep `GetAccessTokenAsync` on `PlannerScopes`, change `SignInAsync` to use `PlannerConnectScopes`, and add:

```csharp
internal static IReadOnlyList<string> ConversationScopes { get; } =
    Array.AsReadOnly(new[] { "Group-Conversation.ReadWrite.All" });
internal static IReadOnlyList<string> PlannerConnectScopes { get; } =
    Array.AsReadOnly(new[] { "User.Read", "Tasks.ReadWrite", "Group-Conversation.ReadWrite.All" });

public Task<string> GetConversationTokenAsync(CancellationToken cancellationToken) =>
    GetTokenForScopesAsync(ConversationScopes, cancellationToken);
```

Extend `MicrosoftAuthCapabilities` with `TaskChat`, probe it using `ConversationScopes`, and update the helper method that returns the same capability for every field.

Add `EnableTaskChatAsync` using `ConnectAsync(ConversationScopes, requireExistingAccount: true, cancellationToken)` and map `POST /auth/enable-task-chat` through the existing account transition guard. Add an **Enable task chat** button to the Planner connection section. Include `taskChat` in `renderPermissions` and failed capability defaults; the button calls the new endpoint and remains hidden while signed out. Update the permission disclosure to name `Group-Conversation.ReadWrite.All` and explain that it reads and posts Planner task comments.

- [ ] **Step 4: Run all helper tests**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/index.html microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthCapabilityTests.cs microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthAcquisitionTests.cs
git commit -m "feat(planner): request task chat permission"
```

### Task 2: Preserve Planner Conversation Identity

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/GraphModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`

**Interfaces:**
- Produces: nullable `GraphTask.ConversationThreadId` as the final positional record property.
- Consumes: existing `plannerTask.conversationThreadId` Graph property.

- [ ] **Step 1: Write the failing mapping test**

Update `GetTasksAsync_MapsTaskAndFollowsNextPage` so the first task JSON contains `"conversationThreadId":"thread-1"` and assert:

```csharp
Assert.Equal("thread-1", tasks[0].ConversationThreadId);
Assert.Null(tasks[1].ConversationThreadId);
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter GetTasksAsync_MapsTaskAndFollowsNextPage`

Expected: FAIL because `GraphTask` does not expose the property.

- [ ] **Step 3: Add and map the nullable property**

Append the property to preserve existing test constructors:

```csharp
public sealed record GraphTask(
    string Id,
    string Title,
    string PlanId,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments,
    string? BucketOrderHint = null,
    DateTimeOffset? StartDateTime = null,
    string? ConversationThreadId = null);
```

Map `conversationThreadId` in `PlannerGraphClient.ToTask`.

- [ ] **Step 4: Run all helper tests**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs
git commit -m "feat(planner): retain task conversation identity"
```

### Task 3: Explicit Notes Updates

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/IPlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskNotesService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`
- Create: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskNotesServiceTests.cs`

**Interfaces:**
- Produces: `UpdateNotesRequest(string Description)`.
- Produces: `IPlannerGraphClient.UpdateTaskDescriptionAsync(string taskId, string description, string etag, CancellationToken)`.
- Produces: `TaskNotesService.UpdateAsync(string taskId, string description, CancellationToken)`.
- Produces: `PUT /api/planner/tasks/{taskId}/notes` and legacy-compatible `/tasks/{taskId}/notes`.

- [ ] **Step 1: Write failing Graph and service tests**

Assert that the Graph client PATCHes only the description with the details ETag and that the service reloads details, rejects notes longer than 4000 characters, updates Graph once, and invalidates `TaskDetailsService` only after success.

```csharp
Assert.Equal(HttpMethod.Patch, request.Method);
Assert.Equal("W/\"details\"", request.Headers.IfMatch.Single().ToString());
Assert.Equal("{\"description\":\"Updated notes\"}", await request.Content!.ReadAsStringAsync());
await service.UpdateAsync("task", "Updated notes", CancellationToken.None);
Assert.Equal("Updated notes", graph.UpdatedDescription);
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "GraphClientTests|TaskNotesServiceTests"`

Expected: FAIL because notes update interfaces do not exist.

- [ ] **Step 3: Implement the notes Graph call and service**

Implement the PATCH using `If-Match`. `TaskNotesService` trims neither internal whitespace nor line breaks, permits an empty description to clear notes, enforces the 4000-character limit, and calls `TaskDetailsService.Invalidate(taskId)` after success.

Map the endpoint:

```csharp
app.MapPut("/tasks/{taskId}/notes", async (string taskId, UpdateNotesRequest request,
    TaskNotesService notes, CancellationToken ct) =>
{
    await notes.UpdateAsync(taskId, request.Description, ct);
    return Results.NoContent();
});
```

- [ ] **Step 4: Run helper tests**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj`

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): add explicit notes updates"
```

### Task 4: Task Conversation Graph Client and Service

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/MicrosoftWidgets.Helper.csproj`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Contracts/ApiModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/GraphModels.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/IPlannerGraphClient.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/ConversationText.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskChatService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`
- Create: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskChatServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/ContractTests.cs`

**Interfaces:**
- Produces: `GraphConversationPost`, `GraphConversationPage`, and Graph client methods `GetConversationPostsAsync`, `ReplyToConversationAsync`, `CreateConversationThreadAsync`, and `SetConversationThreadAsync`.
- Produces: `TaskChatMessage`, `TaskChatResponse`, and `PostChatRequest` API contracts.
- Produces: `TaskChatService.GetAsync(string taskId, string? cursor, CancellationToken)` and `PostAsync(string taskId, string message, CancellationToken)`.
- Produces: `GET` and `POST /api/planner/tasks/{taskId}/chat` plus legacy-compatible routes.

- [ ] **Step 1: Add HtmlAgilityPack 1.13.0 and write failing text-conversion tests**

Add `<PackageReference Include="HtmlAgilityPack" Version="1.13.0" />`. Test paragraph, `<br>`, entity decoding, scripts/styles, and HTML-like user text:

```csharp
Assert.Equal("First line\nSecond & final", ConversationText.ToPlainText(
    "<p>First line<br>Second &amp; final</p><script>alert(1)</script>"));
```

- [ ] **Step 2: Write failing Graph client tests**

Cover:

```text
GET  /groups/group/threads/thread/posts?$select=id,body,from,createdDateTime
POST /groups/group/threads/thread/reply
POST /groups/group/threads
PATCH /planner/tasks/task with conversationThreadId and current task ETag
```

Assert conversation requests use the conversation token, response posts map ID/body/author/timestamp, and `@odata.nextLink` is retained internally.

- [ ] **Step 3: Write failing service tests**

Cover existing history, no-thread empty state, chronological output, opaque cursor round-trip, permission fallback, wrong-plan rejection, malicious/stale cursor rejection, existing-thread reply, first-thread creation/attachment, 4000-character input validation, ETag conflict, and exactly one create call when attachment fails.

Use these public contracts:

```csharp
public sealed record TaskChatMessage(string Id, string Author, DateTimeOffset? CreatedAt, string Body);
public sealed record TaskChatResponse(string State, IReadOnlyList<TaskChatMessage> Messages,
    string? NextCursor = null, string? Message = null);
public sealed record PostChatRequest(string Message);
```

- [ ] **Step 4: Run focused tests and verify failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "GraphClientTests|TaskChatServiceTests|ContractTests"`

Expected: FAIL because conversation types and services do not exist.

- [ ] **Step 5: Implement safe Graph conversation operations**

Use `IGraphTokenProvider.GetConversationTokenAsync` for group conversation requests. Send new message bodies as `contentType: "text"`. Validate any decoded continuation URI with all of these conditions before use:

```csharp
uri.Scheme == Uri.UriSchemeHttps
&& uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
&& uri.AbsolutePath.Equals($"/v1.0/groups/{escapedGroup}/threads/{escapedThread}/posts",
    StringComparison.Ordinal)
```

Normalize returned HTML with `ConversationText` before creating API contracts.

- [ ] **Step 6: Implement `TaskChatService` and routes**

Resolve the selected plan, require `task.PlanId == selectedPlanId`, use the plan's `GroupId`, and represent missing consent as `State = "interaction_required"` on GET. POST returns a structured 403 through the existing error pipeline when conversation permission is missing. Never retry first-thread creation automatically after a failed attachment.

Register the service and map:

```csharp
app.MapGet("/tasks/{taskId}/chat", async (string taskId, string? cursor,
    TaskChatService chat, CancellationToken ct) => Results.Ok(await chat.GetAsync(taskId, cursor, ct)));
app.MapPost("/tasks/{taskId}/chat", async (string taskId, PostChatRequest request,
    TaskChatService chat, CancellationToken ct) => Results.Ok(await chat.PostAsync(taskId, request.Message, ct)));
```

- [ ] **Step 7: Run all helper tests**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "feat(planner): add task conversation API"
```

### Task 5: Editable Notes and Chat in the Widget

**Files:**
- Modify: `planner-edge-widget/widget/src/api.js`
- Modify: `planner-edge-widget/widget/src/app.js`
- Modify: `planner-edge-widget/widget/styles.css`
- Test: `planner-edge-widget/widget/tests/interactions.test.mjs`
- Test: `planner-edge-widget/widget/tests/connection.test.mjs`

**Interfaces:**
- Consumes: `PUT /tasks/{taskId}/notes`, `GET /tasks/{taskId}/chat?cursor=...`, and `POST /tasks/{taskId}/chat`.
- Produces: `PlannerApi.updateNotes`, `getTaskChat`, and `postTaskChat`.
- Produces: dialog-local notes draft, notes status, chat page, chat draft, chat status, and chat request generation state.

- [ ] **Step 1: Write failing API wrapper tests**

Assert exact URL, method, JSON header, and body for notes and chat calls:

```javascript
await PlannerApi.updateNotes("task", "Line one\nLine two");
await PlannerApi.getTaskChat("task", "opaque cursor");
await PlannerApi.postTaskChat("task", "Status update");
```

- [ ] **Step 2: Write failing task-detail interaction tests**

Cover explicit notes save, empty-note clearing, disabled pending actions, success state, retained notes after failure, lazy chat loading only after the details dialog opens, chronological rendering, author/time display, empty state, permission state, load-earlier prepending, retained failed comment draft, and posting exactly once.

Add security and regression assertions:

```javascript
assert.doesNotMatch(app.innerHTML, /<script>alert/);
assert.match(app.innerHTML, /&lt;script&gt;/);
assert.equal(detailPanel.scrollTop, 240);
assert.doesNotMatch(app.innerHTML, /Task chat/); // after closing during an in-flight request
```

- [ ] **Step 3: Run widget tests and verify failure**

Run: `npm test --prefix planner-edge-widget/widget`

Expected: FAIL because the API wrappers and UI controls do not exist.

- [ ] **Step 4: Implement API wrappers and dialog-local state**

Add:

```javascript
async function updateNotes(taskId, description) { /* PUT JSON body */ }
async function getTaskChat(taskId, cursor) { /* GET with URLSearchParams */ }
async function postTaskChat(taskId, message) { /* POST JSON body */ }
```

Initialize notes and chat state when opening task details. Use a dialog generation token so a resolved request cannot mutate a closed or different task dialog. Preserve `.confirm-panel.scrollTop` across render calls in addition to existing board and bucket positions.

- [ ] **Step 5: Implement notes and chat markup/actions**

Render the notes textarea even when the description is empty, with explicit Save behavior and nearby status text. Render chat messages with `escapeHtml`, a **Load earlier comments** button, and a composer. Do not clear either draft before its request succeeds.

Use `Intl.DateTimeFormat` for local timestamps and keep messages oldest-to-newest after prepending earlier pages.

- [ ] **Step 6: Add touchscreen-sized styling**

Add focused `.notes-editor`, `.notes-actions`, `.chat-history`, `.chat-message`, `.chat-meta`, `.chat-composer`, and `.section-status` rules. Keep controls at least 40px high, make the chat history part of the detail panel's normal vertical flow, and do not introduce nested scroll capture inside the chat list.

- [ ] **Step 7: Run widget tests**

Run: `npm test --prefix planner-edge-widget/widget`

Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add planner-edge-widget/widget/src planner-edge-widget/widget/styles.css planner-edge-widget/widget/tests
git commit -m "feat(planner): add notes and task chat UI"
```

### Task 6: Setup Documentation and Full Verification

**Files:**
- Modify: `planner-edge-widget/docs/setup.md`
- Modify: `planner-edge-widget/README.md`
- Modify: `microsoft-widgets-helper/README.md`
- Modify: helper setup-page permission copy in `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/index.html` if that copy is present there.

**Interfaces:**
- Consumes: completed notes/chat implementation.
- Produces: administrator setup instructions for `Group-Conversation.ReadWrite.All` and user-facing fallback behavior.

- [ ] **Step 1: Update setup and feature documentation**

Document the complete Planner delegated permission set, explain that existing users may need to reconnect once, identify task chat as the only feature disabled when conversation consent is absent, and state that notes use the existing task permission.

- [ ] **Step 2: Run complete automated verification**

Run:

```powershell
dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj
npm test --prefix planner-edge-widget/widget
git diff --check
```

Expected: all tests PASS and `git diff --check` prints nothing.

- [ ] **Step 3: Build the helper in Release mode**

Run: `dotnet build microsoft-widgets-helper/src/MicrosoftWidgets.Helper/MicrosoftWidgets.Helper.csproj -c Release`

Expected: build succeeds with zero errors.

- [ ] **Step 4: Perform XENEON EDGE manual verification**

Verify a task with existing comments and a task without a conversation. Confirm notes save, first comment creation, normal replies, load-earlier behavior, unavailable permission copy, tap-outside dismissal, vertical detail scrolling, horizontal board scrolling, and restored touch scrolling after closing details.

- [ ] **Step 5: Commit documentation**

```bash
git add planner-edge-widget/README.md planner-edge-widget/docs/setup.md microsoft-widgets-helper/README.md microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/index.html
git commit -m "docs(planner): document notes and task chat"
```

- [ ] **Step 6: Review branch history and status**

Run: `git status --short` and `git log --oneline -7`.

Expected: clean status and each feature unit represented by a Conventional Commit.
