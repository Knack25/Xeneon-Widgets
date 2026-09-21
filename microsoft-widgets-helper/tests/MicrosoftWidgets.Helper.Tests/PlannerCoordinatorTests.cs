using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerCoordinatorTests
{
    [Fact]
    public async Task CachedDisplay_ReturnsSelectedPlanWithoutGraphCall()
    {
        var graph = new CountingGraph();
        var coordinator = CreateCoordinator("plan-a", "plan-a", graph);

        var display = await coordinator.GetCachedDisplayAsync(CancellationToken.None);

        Assert.NotNull(display);
        Assert.Equal("plan-a", display.PlanId);
        Assert.Equal(0, graph.CallCount);
    }

    [Fact]
    public async Task CachedDisplay_RejectsAnotherPlanWithoutGraphCall()
    {
        var graph = new CountingGraph();
        var coordinator = CreateCoordinator("new", "old", graph);

        var display = await coordinator.GetCachedDisplayAsync(CancellationToken.None);

        Assert.Null(display);
        Assert.Equal(0, graph.CallCount);
    }

    private static PlannerCoordinator CreateCoordinator(string selectedPlan, string cachedPlan, CountingGraph graph)
    {
        var cached = new BoardDisplay(cachedPlan, "Board", DateTimeOffset.UtcNow, true, []);
        return new PlannerCoordinator(new FakeStore(
            new SettingsDto(selectedPlan, "Board", true), cached), new PlannerDisplayService(graph));
    }

    private sealed class FakeStore(SettingsDto settings, BoardDisplay cached) : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(settings);
        public Task SaveSettingsAsync(SettingsDto value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BoardDisplay?>(cached);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingGraph : IPlannerGraphClient
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) => Called<IReadOnlyList<GraphGroup>>();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => Called<IReadOnlyList<GraphPlan>>();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => Called<IReadOnlyList<GraphBucket>>();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => Called<IReadOnlyList<GraphTask>>();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => Called<GraphTask?>();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) => Called<string?>();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken) => Called<GraphTaskDetails>();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken) => Called<object>();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => Called<object>();

        private Task<T> Called<T>()
        {
            CallCount++;
            throw new InvalidOperationException("Graph must not be called for cached display.");
        }
    }
}
