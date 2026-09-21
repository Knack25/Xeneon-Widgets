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

    [Fact]
    public void TaskChatContracts_ExposeStableFields()
    {
        var createdAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var message = new TaskChatMessage("post-1", "Alex", createdAt, "Ready to ship");
        var response = new TaskChatResponse("available", [message], "opaque", null);
        var request = new PostChatRequest("Status update");

        Assert.Equal("post-1", Assert.Single(response.Messages).Id);
        Assert.Equal("Alex", response.Messages[0].Author);
        Assert.Equal(createdAt, response.Messages[0].CreatedAt);
        Assert.Equal("Ready to ship", response.Messages[0].Body);
        Assert.Equal("opaque", response.NextCursor);
        Assert.Equal("Status update", request.Message);
    }
}
