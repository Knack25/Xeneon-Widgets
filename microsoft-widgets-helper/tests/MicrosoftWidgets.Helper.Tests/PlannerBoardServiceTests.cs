using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerBoardServiceTests
{
    [Fact]
    public async Task GetPlansAsync_ReturnsSharedPlansSortedByGroupAndTitleAndCachesThem()
    {
        var graph = new FakePlannerGraphClient();
        var service = new PlannerBoardService(graph, new MemoryCache(new MemoryCacheOptions()));

        var plans = await service.GetPlansAsync(CancellationToken.None);
        await service.GetPlansAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Roadmap"], plans.Select(plan => plan.Title).ToArray());
        Assert.Equal("Engineering", plans[0].GroupName);
        Assert.Equal("Marketing", plans[1].GroupName);
        Assert.Equal(1, graph.PlanReads);
    }

    private sealed class FakePlannerGraphClient : IPlannerGraphClient
    {
        public int PlanReads { get; private set; }

        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken)
        {
            PlanReads++;
            return Task.FromResult<IReadOnlyList<GraphPlan>>([
                new("plan-b", "Roadmap", "group-b", null),
                new("plan-a", "Alpha", "group-a", null)
            ]);
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<GraphGroup> groups =
            [
                new("group-b", "Marketing"),
                new("group-a", "Engineering")
            ];
            return Task.FromResult(groups);
        }

        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Do not query each group for plans.");

        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
