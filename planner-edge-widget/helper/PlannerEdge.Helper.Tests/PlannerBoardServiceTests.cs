using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerBoardServiceTests
{
    [Fact]
    public async Task GetPlansAsync_ReturnsPlansAcrossMemberGroupsSortedByGroupAndTitle()
    {
        var graph = new FakePlannerGraphClient();
        var service = new PlannerBoardService(graph);

        var plans = await service.GetPlansAsync(CancellationToken.None);

        Assert.Equal(["Alpha", "Roadmap"], plans.Select(plan => plan.Title).ToArray());
        Assert.Equal("Engineering", plans[0].GroupName);
        Assert.Equal("Marketing", plans[1].GroupName);
    }

    private sealed class FakePlannerGraphClient : IPlannerGraphClient
    {
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
        {
            IReadOnlyList<GraphPlan> plans = groupId switch
            {
                "group-a" => [new GraphPlan("plan-a", "Alpha", "group-a", "Engineering")],
                "group-b" => [new GraphPlan("plan-b", "Roadmap", "group-b", "Marketing")],
                _ => []
            };
            return Task.FromResult(plans);
        }

        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
