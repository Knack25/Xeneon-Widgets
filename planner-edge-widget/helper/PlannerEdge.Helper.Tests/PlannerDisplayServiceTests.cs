using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerDisplayServiceTests
{
    [Fact]
    public async Task GetDisplayAsync_OrdersBucketsByPlannerHint()
    {
        var graph = new FakePlannerGraphClient
        {
            Buckets =
            [
                new GraphBucket("last", "Last", "plan", "z"),
                new GraphBucket("first", "First", "plan", "a"),
                new GraphBucket("middle", "Middle", "plan", "m")
            ],
            Tasks = [new GraphTask("loose", "Loose", "plan", null, null, null, 0, "etag", [])]
        };

        var board = await new PlannerDisplayService(graph).GetDisplayAsync("plan", "Board", true, CancellationToken.None);

        Assert.Equal(["First", "Middle", "Last", "No bucket"], board.Buckets.Select(bucket => bucket.Name));
    }

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
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
