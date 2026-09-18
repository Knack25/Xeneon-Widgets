using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Tests;

public sealed class BoardSelectionServiceTests
{
    [Fact]
    public async Task SelectAsync_SavesCurrentTitleAndPreservesHideCompleted()
    {
        var store = new FakeStore();
        var service = new BoardSelectionService(new PlannerBoardService(new FakeGraph(), new MemoryCache(new MemoryCacheOptions())), store);

        var selected = await service.SelectAsync("plan", CancellationToken.None);

        Assert.Equal("Current name", selected.SelectedPlanTitle);
        Assert.True(selected.HideCompletedTasks);
    }

    [Fact]
    public async Task SelectAsync_DoesNotReplaceSettingsForUnknownPlan()
    {
        var store = new FakeStore();
        var service = new BoardSelectionService(new PlannerBoardService(new FakeGraph(), new MemoryCache(new MemoryCacheOptions())), store);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SelectAsync("missing", CancellationToken.None));

        Assert.Equal("old", (await store.LoadSettingsAsync(CancellationToken.None)).SelectedPlanId);
    }

    private sealed class FakeStore : IPlannerSettingsStore
    {
        private SettingsDto current = new("old", "Old name", true);
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) => Task.FromResult(current);
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) { current = settings; return Task.CompletedTask; }
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => Task.FromResult<BoardDisplay?>(null);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphPlan>>([new GraphPlan("plan", "Current name", "group", null)]);
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphGroup>>([new GraphGroup("group", "Team")]);
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphPlan>>([new GraphPlan("plan", "Current name", groupId, null)]);
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
