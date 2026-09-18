using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskMoveServiceTests
{
    [Fact]
    public async Task MoveAsync_UsesFreshEtagForBucketOnSelectedBoard()
    {
        var graph = new FakeGraph();
        var service = new TaskMoveService(graph, new FakeSettings(), new MemoryCache(new MemoryCacheOptions()));

        await service.MoveAsync("task", "target", CancellationToken.None);

        Assert.Equal(("task", "target", "latest-etag"), graph.Move);
    }

    [Theory]
    [InlineData("other-plan", "target")]
    [InlineData("plan", "missing")]
    public async Task MoveAsync_RejectsTasksOrBucketsOutsideSelectedBoard(string taskPlan, string target)
    {
        var graph = new FakeGraph { TaskPlan = taskPlan };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new TaskMoveService(graph, new FakeSettings(), new MemoryCache(new MemoryCacheOptions()))
                .MoveAsync("task", target, CancellationToken.None));

        Assert.Null(graph.Move);
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
        public string TaskPlan { get; init; } = "plan";
        public (string TaskId, string BucketId, string ETag)? Move { get; private set; }
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) =>
            Task.FromResult<GraphTask?>(new GraphTask(taskId, "Task", TaskPlan, "current", null, null, 0, "latest-etag", []));
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphBucket>>([new GraphBucket("current", "Current", planId), new GraphBucket("target", "Target", planId)]);
        public Task MoveTaskAsync(string taskId, string bucketId, string etag, CancellationToken ct)
        {
            Move = (taskId, bucketId, etag);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
