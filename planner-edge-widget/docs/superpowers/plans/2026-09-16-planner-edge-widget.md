# Planner Edge Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a XENEON EDGE iCUE widget plus local Windows helper that displays a selected Microsoft Planner board and completes tasks with tap plus confirmation.

**Architecture:** A .NET helper binds to loopback on port `8787`, owns Microsoft authentication, Graph access, caching, and Planner writes, and exposes a small JSON API. The iCUE widget is static HTML/CSS/JavaScript that talks only to `http://localhost:8787`, never stores Microsoft tokens, and renders the selected board in glanceable XENEON EDGE states.

**Tech Stack:** .NET 10 minimal ASP.NET Core helper, xUnit tests, MSAL for delegated Microsoft work-account auth, Microsoft Graph REST through `HttpClient`, static iCUE widget HTML/CSS/JavaScript, Node built-in test runner for widget state tests.

**Spec:** `planner-edge-widget/docs/superpowers/specs/2026-09-16-planner-edge-widget-design.md`

## Global Constraints

- Target project root: `planner-edge-widget`.
- Helper binds only to loopback at `http://localhost:8787`.
- First helper shipping shape is a console app, not a tray app or Windows service.
- Microsoft auth uses delegated work or school account scopes: `User.Read` and `Tasks.ReadWrite`.
- Microsoft client ID is configured outside source through `PlannerEdge__AzureAd__ClientId`, user secrets, or appsettings for local development.
- Widget declares only the needed iCUE URL permission for `localhost` port `8787`.
- Widget never stores Microsoft tokens.
- First version supports only read/display and marking tasks complete.
- First version does not create, edit, delete, or reassign Planner tasks.
- Completed tasks are hidden by default.
- Error states use stable codes: `signed_out`, `consent_required`, `forbidden`, `network_unavailable`, `graph_unavailable`, `task_conflict`, `unknown_error`.

---

## File Structure

- `planner-edge-widget/PlannerEdgeWidget.sln`
  Solution containing helper and tests.

- `planner-edge-widget/helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj`
  Minimal ASP.NET Core helper app.

- `planner-edge-widget/helper/PlannerEdge.Helper/Program.cs`
  Host wiring, loopback URL binding, dependency registration, endpoint mapping.

- `planner-edge-widget/helper/PlannerEdge.Helper/Contracts/*.cs`
  API request and response DTOs shared inside the helper.

- `planner-edge-widget/helper/PlannerEdge.Helper/Graph/*.cs`
  Microsoft Graph client abstraction, REST implementation, and Graph DTO mapping.

- `planner-edge-widget/helper/PlannerEdge.Helper/Planner/*.cs`
  Planner board discovery, display model creation, and task completion logic.

- `planner-edge-widget/helper/PlannerEdge.Helper/Auth/*.cs`
  MSAL account sign-in/status/sign-out service.

- `planner-edge-widget/helper/PlannerEdge.Helper/Storage/*.cs`
  Local JSON cache and settings persistence.

- `planner-edge-widget/helper/PlannerEdge.Helper/Errors/*.cs`
  Stable app error codes and API error mapping.

- `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj`
  xUnit helper tests.

- `planner-edge-widget/widget/manifest.json`
  iCUE widget metadata and localhost permission.

- `planner-edge-widget/widget/index.html`
  Widget markup and script/style includes.

- `planner-edge-widget/widget/src/api.js`
  Local helper API client.

- `planner-edge-widget/widget/src/state.js`
  Widget state transitions and display helpers.

- `planner-edge-widget/widget/src/app.js`
  DOM rendering and interaction wiring.

- `planner-edge-widget/widget/styles.css`
  XENEON EDGE layout and states.

- `planner-edge-widget/widget/resources/icon.svg`
  Widget preview icon.

- `planner-edge-widget/widget/tests/state.test.mjs`
  Node tests for widget state behavior.

- `planner-edge-widget/docs/setup.md`
  Microsoft Entra app registration, helper run, widget import, and manual test instructions.

---

### Task 1: Scaffold Helper Solution And Core Contracts

**Files:**
- Create: `planner-edge-widget/PlannerEdgeWidget.sln`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Contracts/ApiModels.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Errors/AppError.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/ContractTests.cs`

**Interfaces:**
- Produces: `AppError`, `ApiErrorResponse`, `PlanSummary`, `BucketDisplay`, `TaskDisplay`, `BoardDisplay`, `AuthStatusResponse`, `SettingsDto`, `CompleteTaskResponse`.
- Consumes: no earlier task output.

- [ ] **Step 1: Create solution and projects**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget
dotnet new sln -n PlannerEdgeWidget
dotnet new web -n PlannerEdge.Helper -o helper/PlannerEdge.Helper --framework net10.0
dotnet new xunit -n PlannerEdge.Helper.Tests -o helper/PlannerEdge.Helper.Tests --framework net10.0
dotnet sln add helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj
dotnet sln add helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj
dotnet add helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj reference helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj
```

- [ ] **Step 2: Write failing contract test**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/ContractTests.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Errors;

namespace PlannerEdge.Helper.Tests;

public sealed class ContractTests
{
    [Fact]
    public void BoardDisplay_ContainsStableWidgetFields()
    {
        var board = new BoardDisplay(
            PlanId: "plan-1",
            PlanTitle: "Launch Board",
            SyncedAt: new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero),
            IsStale: false,
            Buckets:
            [
                new BucketDisplay(
                    BucketId: "bucket-1",
                    Name: "Doing",
                    Tasks:
                    [
                        new TaskDisplay(
                            TaskId: "task-1",
                            Title: "Ship widget",
                            BucketId: "bucket-1",
                            DueDateTime: null,
                            Priority: 5,
                            PercentComplete: 0,
                            ETag: "W/\"etag\"",
                            Assignments: ["Nathan"])
                    ])
            ]);

        Assert.Equal("plan-1", board.PlanId);
        Assert.Single(board.Buckets);
        Assert.Single(board.Buckets[0].Tasks);
        Assert.Equal("W/\"etag\"", board.Buckets[0].Tasks[0].ETag);
    }

    [Fact]
    public void AppError_UsesStableCodes()
    {
        var error = AppError.Forbidden("Planner denied access.");

        Assert.Equal("forbidden", error.Code);
        Assert.Equal("Planner denied access.", error.Message);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter ContractTests
```

Expected: fail because `PlannerEdge.Helper.Contracts` and `PlannerEdge.Helper.Errors` do not exist.

- [ ] **Step 4: Implement contracts and error model**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Contracts/ApiModels.cs`:

```csharp
namespace PlannerEdge.Helper.Contracts;

public sealed record ApiErrorResponse(string Code, string Message);

public sealed record AuthStatusResponse(
    bool IsSignedIn,
    string? DisplayName,
    string? AccountHint,
    ApiErrorResponse? Error = null);

public sealed record PlanSummary(
    string PlanId,
    string Title,
    string? GroupId,
    string? GroupName);

public sealed record SettingsDto(
    string? SelectedPlanId,
    string? SelectedPlanTitle,
    bool HideCompletedTasks);

public sealed record BucketDisplay(
    string BucketId,
    string Name,
    IReadOnlyList<TaskDisplay> Tasks);

public sealed record TaskDisplay(
    string TaskId,
    string Title,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments);

public sealed record BoardDisplay(
    string PlanId,
    string PlanTitle,
    DateTimeOffset SyncedAt,
    bool IsStale,
    IReadOnlyList<BucketDisplay> Buckets);

public sealed record CompleteTaskResponse(
    string TaskId,
    bool Completed,
    BoardDisplay? Board);
```

Create `planner-edge-widget/helper/PlannerEdge.Helper/Errors/AppError.cs`:

```csharp
using PlannerEdge.Helper.Contracts;

namespace PlannerEdge.Helper.Errors;

public sealed record AppError(string Code, string Message)
{
    public ApiErrorResponse ToResponse() => new(Code, Message);

    public static AppError SignedOut(string message = "Sign in to Microsoft Planner.") => new("signed_out", message);
    public static AppError ConsentRequired(string message = "Microsoft consent is required.") => new("consent_required", message);
    public static AppError Forbidden(string message = "Planner denied access.") => new("forbidden", message);
    public static AppError NetworkUnavailable(string message = "Network unavailable.") => new("network_unavailable", message);
    public static AppError GraphUnavailable(string message = "Microsoft Graph unavailable.") => new("graph_unavailable", message);
    public static AppError TaskConflict(string message = "The task changed in Planner.") => new("task_conflict", message);
    public static AppError Unknown(string message = "Something went wrong.") => new("unknown_error", message);
}
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter ContractTests
```

Expected: pass.

- [ ] **Step 6: Commit**

Run:

```powershell
git add planner-edge-widget
git commit -m "feat: scaffold helper contracts"
```

---

### Task 2: Add Settings And Cache Persistence

**Files:**
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Storage/LocalPaths.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Storage/LocalJsonStore.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Storage/PlannerSettingsStore.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/StorageTests.cs`

**Interfaces:**
- Consumes: `SettingsDto`, `PlanSummary`, `BoardDisplay`.
- Produces: `ILocalJsonStore`, `IPlannerSettingsStore`, settings read/write methods, cached display read/write methods.

- [ ] **Step 1: Write failing storage tests**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/StorageTests.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SettingsStore_PersistsSelectedPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        await store.SaveSettingsAsync(new SettingsDto("plan-1", "Launch Board", HideCompletedTasks: true), CancellationToken.None);

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);
        Assert.Equal("plan-1", loaded.SelectedPlanId);
        Assert.Equal("Launch Board", loaded.SelectedPlanTitle);
        Assert.True(loaded.HideCompletedTasks);
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsWhenNoSettingsExist()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);

        Assert.Null(loaded.SelectedPlanId);
        Assert.Null(loaded.SelectedPlanTitle);
        Assert.True(loaded.HideCompletedTasks);
    }

    [Fact]
    public async Task SettingsStore_PersistsCachedDisplayAsStaleOnRead()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));
        var display = new BoardDisplay(
            "plan-1",
            "Launch Board",
            new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero),
            IsStale: false,
            []);

        await store.SaveCachedDisplayAsync(display, CancellationToken.None);

        var loaded = await store.LoadCachedDisplayAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("plan-1", loaded.PlanId);
        Assert.True(loaded.IsStale);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter StorageTests
```

Expected: fail because storage classes do not exist.

- [ ] **Step 3: Implement local JSON store**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Storage/LocalJsonStore.cs`:

```csharp
using System.Text.Json;

namespace PlannerEdge.Helper.Storage;

public interface ILocalJsonStore
{
    Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken);
    Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken);
}

public sealed class LocalJsonStore(string rootDirectory) : ILocalJsonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
    {
        var path = GetPath(name);
        if (!File.Exists(path)) return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    public async Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = GetPath(name);
        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPath(string name) => Path.Combine(rootDirectory, name + ".json");
}
```

- [ ] **Step 4: Implement settings store**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Storage/PlannerSettingsStore.cs`:

```csharp
using PlannerEdge.Helper.Contracts;

namespace PlannerEdge.Helper.Storage;

public interface IPlannerSettingsStore
{
    Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken);
    Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken);
    Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken);
}

public sealed class PlannerSettingsStore(ILocalJsonStore jsonStore) : IPlannerSettingsStore
{
    private const string SettingsFileName = "settings";
    private const string CachedDisplayFileName = "cached-display";

    public async Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        return await jsonStore.ReadAsync<SettingsDto>(SettingsFileName, cancellationToken)
            ?? new SettingsDto(null, null, HideCompletedTasks: true);
    }

    public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        return jsonStore.WriteAsync(SettingsFileName, settings, cancellationToken);
    }

    public async Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken)
    {
        var display = await jsonStore.ReadAsync<BoardDisplay>(CachedDisplayFileName, cancellationToken);
        return display is null ? null : display with { IsStale = true };
    }

    public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken)
    {
        return jsonStore.WriteAsync(CachedDisplayFileName, display with { IsStale = false }, cancellationToken);
    }
}
```

Create `planner-edge-widget/helper/PlannerEdge.Helper/Storage/LocalPaths.cs`:

```csharp
namespace PlannerEdge.Helper.Storage;

public static class LocalPaths
{
    public static string AppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PlannerEdgeWidget");
    }
}
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter "ContractTests|StorageTests"
```

Expected: pass.

- [ ] **Step 6: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: persist helper settings"
```

---

### Task 3: Build Planner Display Model From Graph Data

**Files:**
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Graph/GraphModels.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Graph/IPlannerGraphClient.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerDisplayService.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerDisplayServiceTests.cs`

**Interfaces:**
- Consumes: `BoardDisplay`, `BucketDisplay`, `TaskDisplay`, `SettingsDto`.
- Produces: `IPlannerGraphClient`, `PlannerDisplayService.GetDisplayAsync(string planId, string planTitle, bool hideCompletedTasks, CancellationToken)`.

- [ ] **Step 1: Write failing display model tests**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerDisplayServiceTests.cs`:

```csharp
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerDisplayServiceTests
{
    [Fact]
    public async Task GetDisplayAsync_GroupsActiveTasksByBucketAndHidesCompleted()
    {
        var graph = new FakePlannerGraphClient
        {
            Buckets =
            [
                new GraphBucket("bucket-1", "Doing", "plan-1"),
                new GraphBucket("bucket-2", "Later", "plan-1")
            ],
            Tasks =
            [
                new GraphTask("task-1", "Build helper", "plan-1", "bucket-1", null, 5, 0, "etag-1", []),
                new GraphTask("task-2", "Done task", "plan-1", "bucket-1", null, 5, 100, "etag-2", []),
                new GraphTask("task-3", "Style widget", "plan-1", "bucket-2", null, 3, 50, "etag-3", ["Nathan"])
            ]
        };
        var service = new PlannerDisplayService(graph);

        var board = await service.GetDisplayAsync("plan-1", "Launch Board", hideCompletedTasks: true, CancellationToken.None);

        Assert.Equal("Launch Board", board.PlanTitle);
        Assert.Equal(2, board.Buckets.Count);
        Assert.Single(board.Buckets[0].Tasks);
        Assert.Equal("Build helper", board.Buckets[0].Tasks[0].Title);
        Assert.Single(board.Buckets[1].Tasks);
        Assert.Equal("Nathan", board.Buckets[1].Tasks[0].Assignments[0]);
    }

    private sealed class FakePlannerGraphClient : IPlannerGraphClient
    {
        public IReadOnlyList<GraphBucket> Buckets { get; init; } = [];
        public IReadOnlyList<GraphTask> Tasks { get; init; } = [];

        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => Task.FromResult(Buckets);
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => Task.FromResult(Tasks);
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter PlannerDisplayServiceTests
```

Expected: fail because graph models and display service do not exist.

- [ ] **Step 3: Implement Graph models and abstraction**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Graph/GraphModels.cs`:

```csharp
namespace PlannerEdge.Helper.Graph;

public sealed record GraphGroup(string Id, string DisplayName);
public sealed record GraphPlan(string Id, string Title, string GroupId, string? GroupName);
public sealed record GraphBucket(string Id, string Name, string PlanId);
public sealed record GraphTask(
    string Id,
    string Title,
    string PlanId,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments);
```

Create `planner-edge-widget/helper/PlannerEdge.Helper/Graph/IPlannerGraphClient.cs`:

```csharp
namespace PlannerEdge.Helper.Graph;

public interface IPlannerGraphClient
{
    Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken);
    Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken);
    Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement display service**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerDisplayService.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerDisplayService(IPlannerGraphClient graphClient)
{
    public async Task<BoardDisplay> GetDisplayAsync(
        string planId,
        string planTitle,
        bool hideCompletedTasks,
        CancellationToken cancellationToken)
    {
        var buckets = await graphClient.GetBucketsAsync(planId, cancellationToken);
        var tasks = await graphClient.GetTasksAsync(planId, cancellationToken);

        var visibleTasks = hideCompletedTasks
            ? tasks.Where(task => task.PercentComplete < 100)
            : tasks;

        var tasksByBucket = visibleTasks
            .GroupBy(task => task.BucketId ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(ToDisplay).ToList());

        var bucketDisplays = buckets
            .Select(bucket => new BucketDisplay(
                bucket.Id,
                bucket.Name,
                tasksByBucket.TryGetValue(bucket.Id, out var bucketTasks) ? bucketTasks : []))
            .ToList();

        if (tasksByBucket.TryGetValue(string.Empty, out var unbucketedTasks) && unbucketedTasks.Count > 0)
        {
            bucketDisplays.Add(new BucketDisplay("unbucketed", "No bucket", unbucketedTasks));
        }

        return new BoardDisplay(
            planId,
            planTitle,
            DateTimeOffset.UtcNow,
            IsStale: false,
            bucketDisplays);
    }

    private static TaskDisplay ToDisplay(GraphTask task)
    {
        return new TaskDisplay(
            task.Id,
            task.Title,
            task.BucketId,
            task.DueDateTime,
            task.Priority,
            task.PercentComplete,
            task.ETag,
            task.Assignments);
    }
}
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter "ContractTests|StorageTests|PlannerDisplayServiceTests"
```

Expected: pass.

- [ ] **Step 6: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: map planner display data"
```

---

### Task 4: Add Board Discovery Service

**Files:**
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerBoardService.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerBoardServiceTests.cs`

**Interfaces:**
- Consumes: `IPlannerGraphClient`, `PlanSummary`, `GraphGroup`, `GraphPlan`.
- Produces: `PlannerBoardService.GetPlansAsync(CancellationToken)`.

- [ ] **Step 1: Write failing board discovery test**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerBoardServiceTests.cs`:

```csharp
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerBoardServiceTests
{
    [Fact]
    public async Task GetPlansAsync_ReturnsPlansAcrossMemberGroupsSortedByGroupAndTitle()
    {
        var graph = new FakePlannerGraphClient();
        var service = new PlannerBoardService(graph);

        var plans = await service.GetPlansAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Roadmap"], plans.Select(plan => plan.Title).ToArray());
        Assert.Equal("Engineering", plans[0].GroupName);
        Assert.Equal("Marketing", plans[1].GroupName);
    }

    private sealed class FakePlannerGraphClient : IPlannerGraphClient
    {
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<GraphGroup> groups =
            [
                new("group-b", "Marketing"),
                new("group-a", "Engineering")
            ];
            return Task.FromResult(groups);
        }

        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken)
        {
            IReadOnlyList<GraphPlan> plans = groupId switch
            {
                "group-a" => [new GraphPlan("plan-a", "Alpha", "group-a", "Engineering")],
                "group-b" => [new GraphPlan("plan-b", "Roadmap", "group-b", "Marketing")],
                _ => []
            };
            return Task.FromResult(plans);
        }

        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter PlannerBoardServiceTests
```

Expected: fail because `PlannerBoardService` does not exist.

- [ ] **Step 3: Implement board service**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerBoardService.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerBoardService(IPlannerGraphClient graphClient)
{
    public async Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var groups = await graphClient.GetMemberGroupsAsync(cancellationToken);
        var plans = new List<PlanSummary>();

        foreach (var group in groups.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var groupPlans = await graphClient.GetPlansForGroupAsync(group.Id, cancellationToken);
            plans.AddRange(groupPlans.Select(plan => new PlanSummary(plan.Id, plan.Title, plan.GroupId, plan.GroupName ?? group.DisplayName)));
        }

        return plans
            .OrderBy(plan => plan.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter "PlannerBoardServiceTests|PlannerDisplayServiceTests"
```

Expected: pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: discover planner boards"
```

---

### Task 5: Add Task Completion Service With ETag Handling

**Files:**
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Planner/TaskCompletionService.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/TaskCompletionServiceTests.cs`

**Interfaces:**
- Consumes: `IPlannerGraphClient`, `GraphTask`, `CompleteTaskResponse`.
- Produces: `TaskCompletionService.CompleteAsync(string taskId, CancellationToken)`.

- [ ] **Step 1: Write failing completion tests**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/TaskCompletionServiceTests.cs`:

```csharp
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskCompletionServiceTests
{
    [Fact]
    public async Task CompleteAsync_UsesLatestEtagAndMarksIncompleteTaskComplete()
    {
        var graph = new FakePlannerGraphClient(new GraphTask("task-1", "Finish", "plan-1", "bucket-1", null, 5, 0, "etag-latest", []));
        var service = new TaskCompletionService(graph);

        var response = await service.CompleteAsync("task-1", CancellationToken.None);

        Assert.True(response.Completed);
        Assert.Equal("task-1", response.TaskId);
        Assert.Equal(("task-1", "etag-latest"), graph.CompletedCalls.Single());
    }

    [Fact]
    public async Task CompleteAsync_TreatsAlreadyCompleteTaskAsSuccessWithoutPatch()
    {
        var graph = new FakePlannerGraphClient(new GraphTask("task-1", "Finish", "plan-1", "bucket-1", null, 5, 100, "etag-latest", []));
        var service = new TaskCompletionService(graph);

        var response = await service.CompleteAsync("task-1", CancellationToken.None);

        Assert.True(response.Completed);
        Assert.Empty(graph.CompletedCalls);
    }

    private sealed class FakePlannerGraphClient(GraphTask? task) : IPlannerGraphClient
    {
        public List<(string TaskId, string ETag)> CompletedCalls { get; } = [];

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => Task.FromResult(task);
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken)
        {
            CompletedCalls.Add((taskId, etag));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter TaskCompletionServiceTests
```

Expected: fail because `TaskCompletionService` does not exist.

- [ ] **Step 3: Implement completion service**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Planner/TaskCompletionService.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskCompletionService(IPlannerGraphClient graphClient)
{
    public async Task<CompleteTaskResponse> CompleteAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException($"Planner task '{taskId}' was not found.");

        if (task.PercentComplete >= 100)
        {
            return new CompleteTaskResponse(taskId, Completed: true, Board: null);
        }

        await graphClient.CompleteTaskAsync(taskId, task.ETag, cancellationToken);
        return new CompleteTaskResponse(taskId, Completed: true, Board: null);
    }
}
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter TaskCompletionServiceTests
```

Expected: pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: complete planner tasks"
```

---

### Task 6: Implement Microsoft Auth And Graph REST Client

**Files:**
- Modify: `planner-edge-widget/helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Auth/MicrosoftAuthService.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Graph/PlannerGraphClient.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Graph/GraphApiException.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/GraphClientTests.cs`

**Interfaces:**
- Consumes: `IPlannerGraphClient`, `GraphGroup`, `GraphPlan`, `GraphBucket`, `GraphTask`.
- Produces: `MicrosoftAuthService`, `PlannerGraphClient`.

- [ ] **Step 1: Add packages**

Run:

```powershell
dotnet add helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj package Microsoft.Identity.Client
dotnet add helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj package Microsoft.Extensions.Http
```

- [ ] **Step 2: Write failing Graph client response mapping test**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/GraphClientTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Tests;

public sealed class GraphClientTests
{
    [Fact]
    public async Task GetTasksAsync_MapsGraphTaskFieldsAndEtag()
    {
        var handler = new StubHandler("""
        {
          "value": [
            {
              "id": "task-1",
              "title": "Build helper",
              "planId": "plan-1",
              "bucketId": "bucket-1",
              "dueDateTime": "2026-09-17T13:00:00Z",
              "priority": 5,
              "percentComplete": 0,
              "@odata.etag": "W/\"etag-1\"",
              "assignments": {
                "user-1": {
                  "assignedBy": { "user": { "displayName": "Nathan" } }
                }
              }
            }
          ]
        }
        """);
        var client = new PlannerGraphClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        }, new StaticTokenProvider("token"));

        var tasks = await client.GetTasksAsync("plan-1", CancellationToken.None);

        Assert.Single(tasks);
        Assert.Equal("task-1", tasks[0].Id);
        Assert.Equal("W/\"etag-1\"", tasks[0].ETag);
        Assert.Equal(0, tasks[0].PercentComplete);
    }

    private sealed class StaticTokenProvider(string token) : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Headers.ETag = EntityTagHeaderValue.Parse("\"response-etag\"");
            return Task.FromResult(response);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter GraphClientTests
```

Expected: fail because `PlannerGraphClient` and `IGraphTokenProvider` do not exist.

- [ ] **Step 4: Implement auth token provider interface and MSAL service**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Auth/MicrosoftAuthService.cs`:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Auth;

public sealed record AzureAdOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string Tenant { get; init; } = "organizations";
}

public interface IMicrosoftAuthService : IGraphTokenProvider
{
    Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public sealed class MicrosoftAuthService(IOptions<AzureAdOptions> options) : IMicrosoftAuthService
{
    private static readonly string[] Scopes = ["User.Read", "Tasks.ReadWrite"];
    private readonly IPublicClientApplication? app = CreateApp(options.Value);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (app is null) throw new InvalidOperationException("Microsoft client ID is not configured.");

        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is null) throw new MsalUiRequiredException("no_account", "No Microsoft account is signed in.");

        var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
        return result.AccessToken;
    }

    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (app is null) return new AuthStatusResponse(false, null, null, new ApiErrorResponse("unknown_error", "Microsoft client ID is not configured."));

        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        return account is null
            ? new AuthStatusResponse(false, null, null)
            : new AuthStatusResponse(true, account.Username, account.Username);
    }

    public async Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken)
    {
        if (app is null) return new AuthStatusResponse(false, null, null, new ApiErrorResponse("unknown_error", "Microsoft client ID is not configured."));

        var result = await app.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);

        return new AuthStatusResponse(true, result.Account.Username, result.Account.Username);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        if (app is null) return;

        foreach (var account in await app.GetAccountsAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await app.RemoveAsync(account);
        }
    }

    private static IPublicClientApplication? CreateApp(AzureAdOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId)) return null;

        return PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.Tenant)
            .WithDefaultRedirectUri()
            .Build();
    }
}
```

- [ ] **Step 5: Implement Graph REST client**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Graph/GraphApiException.cs`:

```csharp
namespace PlannerEdge.Helper.Graph;

public sealed class GraphApiException(System.Net.HttpStatusCode statusCode, string message) : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
```

Create `planner-edge-widget/helper/PlannerEdge.Helper/Graph/PlannerGraphClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlannerEdge.Helper.Graph;

public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider) : IPlannerGraphClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("me/memberOf/microsoft.graph.group?$select=id,displayName", cancellationToken);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(item => new GraphGroup(item.GetProperty("id").GetString()!, item.GetProperty("displayName").GetString() ?? "Unnamed group"))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"groups/{Uri.EscapeDataString(groupId)}/planner/plans", cancellationToken);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(item => new GraphPlan(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString() ?? "Untitled plan", groupId, null))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"planner/plans/{Uri.EscapeDataString(planId)}/buckets", cancellationToken);
        return document.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(item => new GraphBucket(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString() ?? "Unnamed bucket", item.GetProperty("planId").GetString() ?? planId))
            .ToList();
    }

    public async Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"planner/plans/{Uri.EscapeDataString(planId)}/tasks", cancellationToken);
        return document.RootElement.GetProperty("value").EnumerateArray().Select(ToTask).ToList();
    }

    public async Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"planner/tasks/{Uri.EscapeDataString(taskId)}", cancellationToken);
        return ToTask(document.RootElement);
    }

    public async Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent("{\"percentComplete\":100}", Encoding.UTF8, "application/json");
        await SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var response = await SendAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new GraphApiException(response.StatusCode, body);
    }

    private static GraphTask ToTask(JsonElement item)
    {
        var assignments = item.TryGetProperty("assignments", out var assignmentsProperty)
            ? assignmentsProperty.EnumerateObject().Select(property => property.Name).ToList()
            : [];

        return new GraphTask(
            item.GetProperty("id").GetString()!,
            item.GetProperty("title").GetString() ?? "Untitled task",
            item.GetProperty("planId").GetString() ?? string.Empty,
            item.TryGetProperty("bucketId", out var bucketId) ? bucketId.GetString() : null,
            item.TryGetProperty("dueDateTime", out var dueDate) && dueDate.ValueKind != JsonValueKind.Null ? dueDate.GetDateTimeOffset() : null,
            item.TryGetProperty("priority", out var priority) && priority.ValueKind != JsonValueKind.Null ? priority.GetInt32() : null,
            item.TryGetProperty("percentComplete", out var percentComplete) ? percentComplete.GetInt32() : 0,
            item.TryGetProperty("@odata.etag", out var etag) ? etag.GetString() ?? string.Empty : string.Empty,
            assignments);
    }
}
```

- [ ] **Step 6: Run tests**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter GraphClientTests
```

Expected: pass.

- [ ] **Step 7: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: add microsoft graph client"
```

---

### Task 7: Expose Helper HTTP API

**Files:**
- Modify: `planner-edge-widget/helper/PlannerEdge.Helper/Program.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerCoordinator.cs`
- Create: `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerCoordinatorTests.cs`

**Interfaces:**
- Consumes: all helper services from Tasks 2-6.
- Produces: HTTP endpoints from the spec: `/health`, `/auth/status`, `/auth/sign-in`, `/auth/sign-out`, `/plans`, `/settings`, `/display`, `/tasks/{taskId}/complete`.

- [ ] **Step 1: Write failing coordinator test for no selected board**

Create `planner-edge-widget/helper/PlannerEdge.Helper.Tests/PlannerCoordinatorTests.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerCoordinatorTests
{
    [Fact]
    public async Task GetDisplayAsync_ReturnsNullWhenNoPlanIsSelected()
    {
        var settings = new InMemorySettingsStore(new SettingsDto(null, null, true));
        var coordinator = new PlannerCoordinator(settings, displayService: null!);

        var display = await coordinator.GetDisplayAsync(CancellationToken.None);

        Assert.Null(display);
    }

    private sealed class InMemorySettingsStore(SettingsDto current) : IPlannerSettingsStore
    {
        private BoardDisplay? cachedDisplay;

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(current);
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
        {
            current = settings;
            return Task.CompletedTask;
        }

        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) => Task.FromResult(cachedDisplay);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken)
        {
            cachedDisplay = display;
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj --filter PlannerCoordinatorTests
```

Expected: fail because `PlannerCoordinator` does not exist.

- [ ] **Step 3: Implement coordinator**

Create `planner-edge-widget/helper/PlannerEdge.Helper/Planner/PlannerCoordinator.cs`:

```csharp
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerCoordinator(
    IPlannerSettingsStore settingsStore,
    PlannerDisplayService displayService)
{
    public async Task<BoardDisplay?> GetDisplayAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId)) return null;

        try
        {
            var display = await displayService.GetDisplayAsync(
                settings.SelectedPlanId,
                settings.SelectedPlanTitle ?? "Selected board",
                settings.HideCompletedTasks,
                cancellationToken);

            await settingsStore.SaveCachedDisplayAsync(display, cancellationToken);
            return display;
        }
        catch
        {
            var cachedDisplay = await settingsStore.LoadCachedDisplayAsync(cancellationToken);
            return cachedDisplay?.PlanId == settings.SelectedPlanId ? cachedDisplay : throw;
        }
    }
}
```

- [ ] **Step 4: Wire services and endpoints**

Replace `planner-edge-widget/helper/PlannerEdge.Helper/Program.cs` with:

```csharp
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:8787");
builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddSingleton<ILocalJsonStore>(_ => new LocalJsonStore(LocalPaths.AppDataRoot()));
builder.Services.AddSingleton<IPlannerSettingsStore, PlannerSettingsStore>();
builder.Services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
builder.Services.AddSingleton<IGraphTokenProvider>(provider => provider.GetRequiredService<IMicrosoftAuthService>());
builder.Services.AddHttpClient<IPlannerGraphClient, PlannerGraphClient>(client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
});
builder.Services.AddSingleton<PlannerBoardService>();
builder.Services.AddSingleton<PlannerDisplayService>();
builder.Services.AddSingleton<TaskCompletionService>();
builder.Services.AddSingleton<PlannerCoordinator>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.1.0" }));

app.MapGet("/auth/status", async (IMicrosoftAuthService auth, CancellationToken cancellationToken) =>
    Results.Ok(await auth.GetStatusAsync(cancellationToken)));

app.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, CancellationToken cancellationToken) =>
    Results.Ok(await auth.SignInAsync(cancellationToken)));

app.MapPost("/auth/sign-out", async (IMicrosoftAuthService auth, CancellationToken cancellationToken) =>
{
    await auth.SignOutAsync(cancellationToken);
    return Results.NoContent();
});

app.MapGet("/plans", async (PlannerBoardService boards, CancellationToken cancellationToken) =>
    Results.Ok(await boards.GetPlansAsync(cancellationToken)));

app.MapGet("/settings", async (IPlannerSettingsStore settings, CancellationToken cancellationToken) =>
    Results.Ok(await settings.LoadSettingsAsync(cancellationToken)));

app.MapPut("/settings", async (SettingsDto dto, IPlannerSettingsStore settings, CancellationToken cancellationToken) =>
{
    await settings.SaveSettingsAsync(dto, cancellationToken);
    return Results.Ok(dto);
});

app.MapGet("/display", async (PlannerCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var display = await coordinator.GetDisplayAsync(cancellationToken);
    return display is null ? Results.NoContent() : Results.Ok(display);
});

app.MapPost("/tasks/{taskId}/complete", async (string taskId, TaskCompletionService completion, CancellationToken cancellationToken) =>
    Results.Ok(await completion.CompleteAsync(taskId, cancellationToken)));

app.Run();
```

- [ ] **Step 5: Run tests and build**

Run:

```powershell
dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj
dotnet build PlannerEdgeWidget.sln
```

Expected: pass and build succeeds.

- [ ] **Step 6: Commit**

Run:

```powershell
git add planner-edge-widget/helper
git commit -m "feat: expose helper api"
```

---

### Task 8: Scaffold Widget And State Tests

**Files:**
- Create: `planner-edge-widget/widget/package.json`
- Create: `planner-edge-widget/widget/manifest.json`
- Create: `planner-edge-widget/widget/index.html`
- Create: `planner-edge-widget/widget/src/state.js`
- Create: `planner-edge-widget/widget/tests/state.test.mjs`
- Create: `planner-edge-widget/widget/resources/icon.svg`

**Interfaces:**
- Consumes: helper JSON contracts from Task 1.
- Produces: `createInitialState()`, `applyDisplayLoaded(state, display)`, `beginConfirmComplete(state, task)`, `cancelConfirmComplete(state)`.

- [ ] **Step 1: Write widget state tests**

Create `planner-edge-widget/widget/package.json`:

```json
{
  "scripts": {
    "test": "node --test"
  },
  "type": "module"
}
```

Create `planner-edge-widget/widget/tests/state.test.mjs`:

```javascript
import test from "node:test";
import assert from "node:assert/strict";
import {
  applyDisplayLoaded,
  beginConfirmComplete,
  cancelConfirmComplete,
  createInitialState
} from "../src/state.js";

test("applyDisplayLoaded moves widget into board state", () => {
  const state = createInitialState();
  const next = applyDisplayLoaded(state, {
    planTitle: "Launch Board",
    isStale: false,
    buckets: []
  });

  assert.equal(next.mode, "board");
  assert.equal(next.display.planTitle, "Launch Board");
});

test("confirmation state stores selected task and cancel returns to board", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Launch Board", isStale: false, buckets: [] });
  const confirming = beginConfirmComplete(board, { taskId: "task-1", title: "Build helper" });

  assert.equal(confirming.mode, "confirmComplete");
  assert.equal(confirming.pendingTask.taskId, "task-1");

  const canceled = cancelConfirmComplete(confirming);
  assert.equal(canceled.mode, "board");
  assert.equal(canceled.pendingTask, null);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
npm test
```

Expected: fail because `src/state.js` does not exist.

- [ ] **Step 3: Implement widget state module**

Create `planner-edge-widget/widget/src/state.js`:

```javascript
export function createInitialState() {
  return {
    mode: "loading",
    display: null,
    error: null,
    pendingTask: null
  };
}

export function applyDisplayLoaded(state, display) {
  return {
    ...state,
    mode: display ? "board" : "noBoardSelected",
    display,
    error: null,
    pendingTask: null
  };
}

export function beginConfirmComplete(state, task) {
  return {
    ...state,
    mode: "confirmComplete",
    pendingTask: task
  };
}

export function cancelConfirmComplete(state) {
  return {
    ...state,
    mode: state.display ? "board" : "noBoardSelected",
    pendingTask: null
  };
}
```

- [ ] **Step 4: Add iCUE widget shell**

Create `planner-edge-widget/widget/manifest.json`:

```json
{
  "author": "Nathan Schneider",
  "id": "com.knack25.planneredgewidget",
  "name": "Planner Edge Widget",
  "description": "Display and complete Microsoft Planner tasks from a selected board.",
  "version": "0.1.0",
  "preview_icon": "resources/icon.svg",
  "min_framework_version": "1.0.0",
  "os": [
    {
      "platform": "windows"
    }
  ],
  "supported_devices": [
    {
      "type": "dashboard_lcd"
    }
  ],
  "permissions": [
    {
      "type": "url",
      "domain": "localhost",
      "port": 8787
    }
  ]
}
```

Create `planner-edge-widget/widget/index.html`:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Planner Edge Widget</title>
    <link rel="stylesheet" href="styles.css" />
  </head>
  <body>
    <main id="app" aria-live="polite">
      <section class="status">Loading Planner...</section>
    </main>
    <script type="module" src="src/app.js"></script>
  </body>
</html>
```

Create `planner-edge-widget/widget/resources/icon.svg`:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="10" fill="#111827"/>
  <path d="M18 18h28v28H18z" fill="#1f8a70"/>
  <path d="M24 31l6 6 12-14" fill="none" stroke="#ffffff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
```

- [ ] **Step 5: Run widget tests**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
npm test
```

Expected: pass.

- [ ] **Step 6: Commit**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon
git add planner-edge-widget/widget
git commit -m "feat: scaffold icue widget"
```

---

### Task 9: Implement Widget API Client And Rendering

**Files:**
- Create: `planner-edge-widget/widget/src/api.js`
- Create: `planner-edge-widget/widget/src/app.js`
- Create: `planner-edge-widget/widget/styles.css`
- Modify: `planner-edge-widget/widget/tests/state.test.mjs`

**Interfaces:**
- Consumes: `BoardDisplay`, `CompleteTaskResponse`, widget state functions.
- Produces: rendered states and complete-task confirmation behavior.

- [ ] **Step 1: Extend state tests for error state**

Modify `planner-edge-widget/widget/tests/state.test.mjs` to add:

```javascript
import { applyError } from "../src/state.js";

test("applyError keeps stale display visible when present", () => {
  const board = applyDisplayLoaded(createInitialState(), { planTitle: "Launch Board", isStale: false, buckets: [] });
  const errored = applyError(board, { code: "network_unavailable", message: "Network unavailable." });

  assert.equal(errored.mode, "error");
  assert.equal(errored.display.planTitle, "Launch Board");
  assert.equal(errored.error.code, "network_unavailable");
});
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
npm test
```

Expected: fail because `applyError` does not exist.

- [ ] **Step 3: Add error state function**

Add this export to `planner-edge-widget/widget/src/state.js`:

```javascript
export function applyError(state, error) {
  return {
    ...state,
    mode: "error",
    error,
    pendingTask: null
  };
}
```

- [ ] **Step 4: Implement helper API client**

Create `planner-edge-widget/widget/src/api.js`:

```javascript
const BASE_URL = "http://localhost:8787";

export async function getDisplay() {
  const response = await fetch(`${BASE_URL}/display`);
  if (response.status === 204) return null;
  return parseJsonResponse(response);
}

export async function completeTask(taskId) {
  const response = await fetch(`${BASE_URL}/tasks/${encodeURIComponent(taskId)}/complete`, {
    method: "POST"
  });
  return parseJsonResponse(response);
}

async function parseJsonResponse(response) {
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    throw body ?? { code: "unknown_error", message: "The local helper returned an error." };
  }
  return body;
}
```

- [ ] **Step 5: Implement DOM app**

Create `planner-edge-widget/widget/src/app.js`:

```javascript
import { completeTask, getDisplay } from "./api.js";
import {
  applyDisplayLoaded,
  applyError,
  beginConfirmComplete,
  cancelConfirmComplete,
  createInitialState
} from "./state.js";

const app = document.getElementById("app");
let state = createInitialState();

async function loadDisplay() {
  try {
    state = applyDisplayLoaded(state, await getDisplay());
  } catch (error) {
    state = applyError(state, normalizeError(error));
  }
  render();
}

function render() {
  if (state.mode === "loading") {
    app.innerHTML = `<section class="status">Loading Planner...</section>`;
    return;
  }

  if (state.mode === "noBoardSelected") {
    app.innerHTML = `<section class="status">Select a Planner board in the helper.</section>`;
    return;
  }

  if (state.mode === "error" && !state.display) {
    app.innerHTML = `<section class="status status-error">${escapeHtml(state.error.message)}</section>`;
    return;
  }

  renderBoard();
}

function renderBoard() {
  const display = state.display;
  const stale = display.isStale || state.mode === "error";
  app.innerHTML = `
    <header class="topbar">
      <div>
        <h1>${escapeHtml(display.planTitle)}</h1>
        <p>${stale ? "Offline view" : `Synced ${formatTime(display.syncedAt)}`}</p>
      </div>
      ${state.error ? `<span class="pill">${escapeHtml(state.error.code)}</span>` : ""}
    </header>
    <section class="board">
      ${display.buckets.map(renderBucket).join("")}
    </section>
    ${state.mode === "confirmComplete" ? renderConfirm() : ""}
  `;

  app.querySelectorAll("[data-complete-task]").forEach((button) => {
    button.addEventListener("click", () => {
      const task = findTask(button.dataset.completeTask);
      state = beginConfirmComplete(state, task);
      render();
    });
  });

  app.querySelector("[data-cancel-complete]")?.addEventListener("click", () => {
    state = cancelConfirmComplete(state);
    render();
  });

  app.querySelector("[data-confirm-complete]")?.addEventListener("click", async () => {
    const taskId = state.pendingTask.taskId;
    await completeTask(taskId);
    state = createInitialState();
    await loadDisplay();
  });
}

function renderBucket(bucket) {
  return `
    <article class="bucket">
      <h2>${escapeHtml(bucket.name)} <span>${bucket.tasks.length}</span></h2>
      <div class="tasks">
        ${bucket.tasks.map(renderTask).join("") || `<p class="empty">Clear</p>`}
      </div>
    </article>
  `;
}

function renderTask(task) {
  return `
    <button class="task" data-complete-task="${escapeHtml(task.taskId)}">
      <span class="checkbox"></span>
      <span class="task-title">${escapeHtml(task.title)}</span>
      ${task.dueDateTime ? `<span class="due">${formatDate(task.dueDateTime)}</span>` : ""}
    </button>
  `;
}

function renderConfirm() {
  return `
    <section class="confirm" role="dialog" aria-modal="true">
      <div class="confirm-panel">
        <h2>Complete task?</h2>
        <p>${escapeHtml(state.pendingTask.title)}</p>
        <div class="confirm-actions">
          <button data-cancel-complete>Cancel</button>
          <button data-confirm-complete>Complete</button>
        </div>
      </div>
    </section>
  `;
}

function findTask(taskId) {
  return state.display.buckets.flatMap((bucket) => bucket.tasks).find((task) => task.taskId === taskId);
}

function normalizeError(error) {
  return {
    code: error.code || "unknown_error",
    message: error.message || "The local helper is unavailable."
  };
}

function formatTime(value) {
  return new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(new Date(value));
}

function formatDate(value) {
  return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric" }).format(new Date(value));
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

loadDisplay();
setInterval(loadDisplay, 60000);
```

- [ ] **Step 6: Add widget styles**

Create `planner-edge-widget/widget/styles.css`:

```css
:root {
  color: #f8fafc;
  background: #111827;
  font-family: "Segoe UI", system-ui, sans-serif;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  overflow: hidden;
}

#app {
  width: 100vw;
  height: 100vh;
  padding: 18px 22px;
  background: linear-gradient(135deg, #111827 0%, #172033 55%, #16352f 100%);
}

.status {
  display: grid;
  height: 100%;
  place-items: center;
  color: #d1d5db;
  font-size: 34px;
}

.status-error {
  color: #fecaca;
}

.topbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  height: 86px;
}

h1,
h2,
p {
  margin: 0;
}

h1 {
  font-size: 34px;
  font-weight: 700;
}

.topbar p {
  margin-top: 6px;
  color: #9ca3af;
  font-size: 18px;
}

.pill {
  border: 1px solid #f59e0b;
  border-radius: 999px;
  padding: 8px 12px;
  color: #fde68a;
  font-size: 15px;
}

.board {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: minmax(300px, 1fr);
  gap: 14px;
  height: calc(100vh - 126px);
  overflow-x: auto;
  overflow-y: hidden;
}

.bucket {
  min-width: 0;
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 8px;
  background: rgba(15, 23, 42, 0.76);
  padding: 14px;
}

.bucket h2 {
  display: flex;
  justify-content: space-between;
  align-items: center;
  height: 34px;
  color: #e5e7eb;
  font-size: 20px;
}

.bucket h2 span {
  color: #5eead4;
  font-size: 18px;
}

.tasks {
  display: grid;
  gap: 10px;
  margin-top: 12px;
}

.task {
  display: grid;
  grid-template-columns: 30px 1fr auto;
  gap: 10px;
  align-items: center;
  min-height: 58px;
  width: 100%;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.06);
  color: #f8fafc;
  text-align: left;
}

.checkbox {
  width: 22px;
  height: 22px;
  margin-left: 4px;
  border: 2px solid #5eead4;
  border-radius: 5px;
}

.task-title {
  overflow: hidden;
  font-size: 18px;
  line-height: 1.2;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.due {
  color: #fcd34d;
  font-size: 14px;
}

.empty {
  color: #6b7280;
  font-size: 18px;
}

.confirm {
  position: fixed;
  inset: 0;
  display: grid;
  place-items: center;
  background: rgba(0, 0, 0, 0.58);
}

.confirm-panel {
  width: min(620px, 80vw);
  border: 1px solid rgba(255, 255, 255, 0.16);
  border-radius: 8px;
  background: #111827;
  padding: 24px;
}

.confirm-panel h2 {
  font-size: 28px;
}

.confirm-panel p {
  margin-top: 10px;
  color: #d1d5db;
  font-size: 21px;
}

.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 12px;
  margin-top: 22px;
}

.confirm-actions button {
  min-width: 140px;
  border: 1px solid #5eead4;
  border-radius: 8px;
  background: #0f766e;
  color: #ffffff;
  font-size: 18px;
  padding: 12px 16px;
}
```

- [ ] **Step 7: Run widget tests**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
npm test
```

Expected: pass.

- [ ] **Step 8: Commit**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon
git add planner-edge-widget/widget
git commit -m "feat: render planner widget"
```

---

### Task 10: Add Setup Documentation And Verification Script

**Files:**
- Create: `planner-edge-widget/docs/setup.md`
- Create: `planner-edge-widget/scripts/verify.ps1`
- Modify: `planner-edge-widget/README.md`

**Interfaces:**
- Consumes: helper and widget commands from earlier tasks.
- Produces: user-facing setup instructions and a single verification command.

- [ ] **Step 1: Create verification script**

Create `planner-edge-widget/scripts/verify.ps1`:

```powershell
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot\..
try {
    dotnet test helper/PlannerEdge.Helper.Tests/PlannerEdge.Helper.Tests.csproj
    Push-Location widget
    try {
        npm test
    }
    finally {
        Pop-Location
    }
}
finally {
    Pop-Location
}
```

- [ ] **Step 2: Create setup documentation**

Create `planner-edge-widget/docs/setup.md`:

```markdown
# Planner Edge Widget Setup

## Microsoft Entra App Registration

Create a public-client app registration in Microsoft Entra for your work tenant.

Configure delegated Microsoft Graph permissions:

- `User.Read`
- `Tasks.ReadWrite`

Enable public client/native client flows and use MSAL's default redirect URI.

Configure the helper client ID locally:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\helper\PlannerEdge.Helper
dotnet user-secrets init
$clientId = $env:PLANNER_EDGE_CLIENT_ID
dotnet user-secrets set "AzureAd:ClientId" $clientId
dotnet user-secrets set "AzureAd:Tenant" "organizations"
```

## Run The Helper

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget
dotnet run --project helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj
```

The helper listens on:

```text
http://localhost:8787
```

## Sign In

Open:

```text
http://localhost:8787/auth/sign-in
```

The first sign-in opens Microsoft authentication in the system browser.

## Select A Board

Read available boards:

```text
http://localhost:8787/plans
```

Save a selected board by sending:

```json
{
  "selectedPlanId": "plan-id-from-plans",
  "selectedPlanTitle": "Plan title from plans",
  "hideCompletedTasks": true
}
```

to:

```text
PUT http://localhost:8787/settings
```

## Import Widget

Validate and package the widget with the iCUE Widget CLI:

```powershell
icuewidget validate C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
icuewidget package C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\widget
```

Import the generated `.icuewidget` file in iCUE.

## Verify

```powershell
C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget\scripts\verify.ps1
```
```

- [ ] **Step 3: Update README**

Replace `planner-edge-widget/README.md` with:

```markdown
# Planner Edge Widget

Planner Edge Widget is a XENEON EDGE display project for showing and completing Microsoft Planner tasks from a work account.

The project has two runtime pieces:

- `helper/PlannerEdge.Helper`: local Windows helper that handles Microsoft sign-in, Microsoft Graph calls, caching, and task completion.
- `widget`: packaged iCUE widget that runs on the XENEON EDGE and talks only to the local helper over `http://localhost:8787`.

## Status

Initial implementation targets:

- select a Planner board
- display active tasks grouped by bucket
- complete a task with tap plus confirmation

## Docs

- Design: `docs/superpowers/specs/2026-09-16-planner-edge-widget-design.md`
- Implementation plan: `docs/superpowers/plans/2026-09-16-planner-edge-widget.md`
- Setup: `docs/setup.md`
```

- [ ] **Step 4: Run verification**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon\planner-edge-widget
.\scripts\verify.ps1
```

Expected: helper tests and widget tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
Set-Location C:\Users\NathanSchneider\Git\Xeneon
git add planner-edge-widget
git commit -m "docs: add setup and verification"
```

---

## Self-Review

Spec coverage:

- Microsoft work-account sign-in is covered in Task 6 and exposed in Task 7.
- Board selection and plan discovery are covered in Tasks 4, 7, and 10.
- Display of active tasks grouped by bucket is covered in Tasks 3 and 9.
- Tap plus confirmation is covered in Tasks 8 and 9.
- Completing tasks with `percentComplete: 100` and ETag is covered in Tasks 5 and 6.
- Loading, signed-out, no-board, error, stale/offline-style display states are covered in Tasks 7, 8, and 9.
- Local caching is covered by Task 2 cached display persistence and Task 7 stale display fallback.
- Security constraints are covered by the loopback binding in Task 7, token isolation in Task 6, widget permission in Task 8, and confirmation flow in Task 9.

Term scan:

- The plan contains exact file paths, command lines, method names, and test snippets.
- No task relies on unnamed future code.
- The only user-specific unknown is the Entra client ID, and the plan routes it through configuration instead of source.

Type consistency:

- `IPlannerGraphClient` is introduced in Task 3 and consumed consistently by Tasks 4, 5, and 6.
- `SettingsDto` is introduced in Task 1 and consumed consistently by Tasks 2, 7, and 10.
- Widget state functions introduced in Task 8 are consumed consistently by Task 9.
