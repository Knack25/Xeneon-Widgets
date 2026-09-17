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
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken)
        {
            CompletedCalls.Add((taskId, etag));
            return Task.CompletedTask;
        }

        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
