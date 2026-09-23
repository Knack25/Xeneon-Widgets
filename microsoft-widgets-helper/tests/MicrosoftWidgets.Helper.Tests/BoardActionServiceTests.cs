using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class BoardActionServiceTests
{
    private const string MemberId = "11111111-1111-1111-1111-111111111111";
    private const string OtherId = "22222222-2222-2222-2222-222222222222";
    private const string NonmemberId = "44444444-4444-4444-4444-444444444444";
    private const string GroupId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task SetDueDate_UsesLatestEtagAndNoonUtc()
    {
        var graph = new FakeGraph();
        var settings = new FakeSettings();
        await new DueDateService(graph, new SelectedPlanTaskService(graph, settings),
            new MemoryCache(new MemoryCacheOptions()))
            .SetAsync("task", "2026-09-19", CancellationToken.None);

        Assert.Equal("latest", graph.UpdatedEtag);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero), graph.UpdatedDue);
    }

    [Fact]
    public async Task SetDueDate_RejectsBeforeStartDate()
    {
        var graph = new FakeGraph { Start = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero) };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DueDateService(graph, new SelectedPlanTaskService(graph, new FakeSettings()),
                new MemoryCache(new MemoryCacheOptions()))
                .SetAsync("task", "2026-09-19", CancellationToken.None));
        Assert.Null(graph.UpdatedEtag);
    }

    [Fact]
    public async Task SetAssignments_UsesOnlyAddedAndRemovedMembers()
    {
        var graph = new FakeGraph { Assigned = [MemberId] };
        var settings = new FakeSettings();
        var members = new BoardMemberService(graph, settings);
        await new TaskAssignmentService(graph, new SelectedPlanTaskService(graph, settings), members,
            new MemoryCache(new MemoryCacheOptions()))
            .SetAsync("task", [OtherId], CancellationToken.None);

        Assert.Equal([OtherId], graph.Added);
        Assert.Equal([MemberId], graph.Removed);
        Assert.Equal("latest", graph.UpdatedEtag);
    }

    [Fact]
    public async Task DueDateAndAssignmentMutationsRejectTasksOutsideSelectedPlan()
    {
        var graph = new FakeGraph { TaskPlan = "other-plan", Assigned = [MemberId] };
        var settings = new FakeSettings();
        var selected = new SelectedPlanTaskService(graph, settings);

        await Assert.ThrowsAsync<ArgumentException>(() => new DueDateService(graph, selected,
            new MemoryCache(new MemoryCacheOptions())).SetAsync("task", "2026-09-19", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new TaskAssignmentService(graph, selected,
            new BoardMemberService(graph, settings), new MemoryCache(new MemoryCacheOptions()))
            .SetAsync("task", [OtherId], CancellationToken.None));

        Assert.Null(graph.UpdatedDue);
        Assert.Null(graph.Added);
    }

    [Fact]
    public async Task CreateTask_RejectsNonmemberBeforeGraphWrite()
    {
        var graph = new FakeGraph();
        var settings = new FakeSettings();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new TaskCreationService(graph, settings, new BoardMemberService(graph, settings))
                .CreateAsync("New", "bucket", null, [NonmemberId], CancellationToken.None));
        Assert.Null(graph.CreatedTitle);
    }

    [Fact]
    public async Task CreateTask_UsesSelectedPlanAndValidatedBucket()
    {
        var graph = new FakeGraph();
        var settings = new FakeSettings();
        await new TaskCreationService(graph, settings, new BoardMemberService(graph, settings))
            .CreateAsync(" New ", "bucket", null, [MemberId], CancellationToken.None);
        Assert.Equal("New", graph.CreatedTitle);
        Assert.Equal("plan", graph.CreatedPlan);
    }

    private sealed class FakeSettings : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) => Task.FromResult(new SettingsDto("plan", "Board", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public DateTimeOffset? Start { get; init; }
        public IReadOnlyList<string> Assigned { get; init; } = [];
        public string TaskPlan { get; init; } = "plan";
        public DateTimeOffset? UpdatedDue { get; private set; }
        public string? UpdatedEtag { get; private set; }
        public IReadOnlyList<string>? Added { get; private set; }
        public IReadOnlyList<string>? Removed { get; private set; }
        public string? CreatedTitle { get; private set; }
        public string? CreatedPlan { get; private set; }
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(
            new GraphTask(taskId, "Task", TaskPlan, "bucket", null, null, 0, "latest", Assigned, null, Start));
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphPlan>>(
            [new GraphPlan("plan", "Board", GroupId, null)]);
        public Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphMember>>([new GraphMember(MemberId, "Member"), new GraphMember(OtherId, "Other")]);
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphBucket>>([new GraphBucket("bucket", "Bucket", planId)]);
        public Task SetDueDateAsync(string taskId, DateTimeOffset? dueDate, string etag, CancellationToken ct)
        { UpdatedDue = dueDate; UpdatedEtag = etag; return Task.CompletedTask; }
        public Task SetAssignmentsAsync(string taskId, IReadOnlyList<string> add, IReadOnlyList<string> remove, string etag, CancellationToken ct)
        { Added = add; Removed = remove; UpdatedEtag = etag; return Task.CompletedTask; }
        public Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate, IReadOnlyList<string> assigneeIds, CancellationToken ct)
        { CreatedPlan = planId; CreatedTitle = title; return Task.CompletedTask; }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
