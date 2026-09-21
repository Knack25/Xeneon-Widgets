using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskDetailsServiceTests
{
    [Fact]
    public async Task GetAsync_CachesDetailsAndKeepsBoardIndependent()
    {
        var graph = new FakeGraph();
        var service = new TaskDetailsService(graph, new MemoryCache(new MemoryCacheOptions()));

        var first = await service.GetAsync("task", CancellationToken.None);
        var second = await service.GetAsync("task", CancellationToken.None);

        Assert.Equal("task", first.TaskId);
        Assert.Equal("bucket", first.BucketId);
        Assert.Equal("Checklist", Assert.Single(first.Checklist).Title);
        Assert.Equal("Alex Smith", Assert.Single(first.Assignees));
        Assert.Same(first, second);
        Assert.Equal(1, graph.DetailReads);
    }

    [Fact]
    public async Task GetAsync_FallsBackWhenDirectoryNameIsUnavailable()
    {
        var graph = new FakeGraph { FailNameLookup = true };

        var details = await new TaskDetailsService(graph, new MemoryCache(new MemoryCacheOptions()))
            .GetAsync("task", CancellationToken.None);

        Assert.Equal("Assigned person unavailable", Assert.Single(details.Assignees));
    }

    [Fact]
    public async Task GetAsync_MapsTaskOrganizationFields()
    {
        var start = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var graph = new FakeGraph { StartDateTime = start };

        var details = await new TaskDetailsService(graph, new MemoryCache(new MemoryCacheOptions()))
            .GetAsync("task", CancellationToken.None);

        Assert.Equal(start, details.StartDateTime);
        Assert.Equal(3, details.Priority);
        Assert.Equal(50, details.PercentComplete);
        Assert.Equal(["category1"], details.LabelIds);
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public int DetailReads { get; private set; }
        public bool FailNameLookup { get; init; }
        public DateTimeOffset? StartDateTime { get; init; }
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) =>
            Task.FromResult<GraphTask?>(new GraphTask(taskId, "Task", "plan", "bucket", null, 3, 50, "etag", ["person"],
                StartDateTime: StartDateTime, AppliedCategories: ["category1"]));
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => FailNameLookup
            ? throw new HttpRequestException("Directory unavailable")
            : Task.FromResult<string?>("Alex Smith");
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct)
        {
            DetailReads++;
            return Task.FromResult(new GraphTaskDetails("etag", [new GraphChecklistItem("item", "Checklist", false, "a")]));
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
