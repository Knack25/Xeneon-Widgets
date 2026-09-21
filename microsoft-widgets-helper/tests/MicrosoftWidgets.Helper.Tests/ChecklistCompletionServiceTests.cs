using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

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

        await CreateService(graph, cache).CompleteAsync("task", "item", CancellationToken.None);

        Assert.Equal(writes, graph.Writes.Count);
        Assert.False(cache.TryGetValue("task", out _));
        if (writes > 0) Assert.Equal(("task", "item", "etag"), graph.Writes[0]);
    }

    [Fact]
    public async Task CompleteAsync_RejectsMissingItem()
    {
        var graph = new FakeGraph(new GraphTaskDetails("etag", []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateService(graph, new MemoryCache(new MemoryCacheOptions())).CompleteAsync("task", "missing", CancellationToken.None));
        Assert.Empty(graph.Writes);
    }

    [Fact]
    public async Task CompleteAsync_RejectsTaskOutsideSelectedPlanBeforeGraphMutation()
    {
        var graph = new FakeGraph(new GraphTaskDetails("etag",
            [new GraphChecklistItem("item", "Item", false, "a")]), "other-plan");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateService(graph, new MemoryCache(new MemoryCacheOptions()))
                .CompleteAsync("task", "item", CancellationToken.None));

        Assert.Empty(graph.Writes);
    }

    private static ChecklistCompletionService CreateService(FakeGraph graph, IMemoryCache cache)
    {
        var selectedPlanTasks = new SelectedPlanTaskService(graph, new FakeSettings());
        return new ChecklistCompletionService(graph, selectedPlanTasks, cache);
    }

    private sealed class FakeSettings : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) =>
            Task.FromResult(new SettingsDto("plan", "Board", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeGraph(GraphTaskDetails details, string taskPlan = "plan") : IPlannerGraphClient
    {
        public List<(string, string, string)> Writes { get; } = [];
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => Task.FromResult(details);
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct)
        {
            Writes.Add((taskId, itemId, etag));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(
            new GraphTask(taskId, "Task", taskPlan, "bucket", null, 5, 0, "task-etag", []));
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
