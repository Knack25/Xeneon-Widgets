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
        Assert.Equal("Checklist", Assert.Single(first.Checklist).Title);
        Assert.Same(first, second);
        Assert.Equal(1, graph.DetailReads);
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public int DetailReads { get; private set; }
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) =>
            Task.FromResult<GraphTask?>(new GraphTask(taskId, "Task", "plan", "bucket", null, null, 0, "etag", []));
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
