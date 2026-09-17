using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Tests;

public sealed class ChecklistCompletionServiceTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task CompleteAsync_UsesLatestDetailsAndSkipsCheckedItem(bool checkedAlready, int writes)
    {
        var graph = new FakeGraph(new GraphTaskDetails("etag", [new GraphChecklistItem("item", "Item", checkedAlready, "a")]));
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("task", "stale");

        await new ChecklistCompletionService(graph, cache).CompleteAsync("task", "item", CancellationToken.None);

        Assert.Equal(writes, graph.Writes.Count);
        Assert.False(cache.TryGetValue("task", out _));
        if (writes > 0) Assert.Equal(("task", "item", "etag"), graph.Writes[0]);
    }

    [Fact]
    public async Task CompleteAsync_RejectsMissingItem()
    {
        var graph = new FakeGraph(new GraphTaskDetails("etag", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ChecklistCompletionService(graph, new MemoryCache(new MemoryCacheOptions())).CompleteAsync("task", "missing", CancellationToken.None));
        Assert.Empty(graph.Writes);
    }

    private sealed class FakeGraph(GraphTaskDetails details) : IPlannerGraphClient
    {
        public List<(string, string, string)> Writes { get; } = [];
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => Task.FromResult(details);
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct)
        {
            Writes.Add((taskId, itemId, etag));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
