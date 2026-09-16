using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Errors;

namespace PlannerEdge.Helper.Tests;

public sealed class ContractTests
{
    [Fact]
    public void BoardDisplay_ContainsStableWidgetFields()
    {
        var board = new BoardDisplay(
            PlanId: "plan-1",
            PlanTitle: "Launch Board",
            SyncedAt: new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero),
            IsStale: false,
            Buckets:
            [
                new BucketDisplay(
                    BucketId: "bucket-1",
                    Name: "Doing",
                    Tasks:
                    [
                        new TaskDisplay(
                            TaskId: "task-1",
                            Title: "Ship widget",
                            BucketId: "bucket-1",
                            DueDateTime: null,
                            Priority: 5,
                            PercentComplete: 0,
                            ETag: "W/\"etag\"",
                            Assignments: ["Nathan"])
                    ])
            ]);

        Assert.Equal("plan-1", board.PlanId);
        Assert.Single(board.Buckets);
        Assert.Single(board.Buckets[0].Tasks);
        Assert.Equal("W/\"etag\"", board.Buckets[0].Tasks[0].ETag);
    }

    [Fact]
    public void AppError_UsesStableCodes()
    {
        var error = AppError.Forbidden("Planner denied access.");

        Assert.Equal("forbidden", error.Code);
        Assert.Equal("Planner denied access.", error.Message);
    }
}
