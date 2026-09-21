using System.Text.Json;
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
                            Assignments: ["Nathan"],
                            StartDateTime: new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
                            LabelIds: ["category1"])
                    ])
            ],
            Labels: [new LabelDisplay("category1", "Blocked")]);

        Assert.Equal("plan-1", board.PlanId);
        Assert.Single(board.Buckets);
        Assert.Single(board.Buckets[0].Tasks);
        Assert.Equal("W/\"etag\"", board.Buckets[0].Tasks[0].ETag);
        Assert.Equal("Blocked", Assert.Single(board.Labels!).Name);
        Assert.Equal(["category1"], board.Buckets[0].Tasks[0].LabelIds);
    }

    [Fact]
    public void BoardDisplay_DeserializesCachedJsonWithoutOrganizationFields()
    {
        const string json = """{"planId":"plan-1","planTitle":"Launch Board","syncedAt":"2026-09-16T12:30:00Z","isStale":false,"buckets":[{"bucketId":"bucket-1","name":"Doing","tasks":[{"taskId":"task-1","title":"Ship widget","bucketId":"bucket-1","dueDateTime":null,"priority":5,"percentComplete":0,"eTag":"W/\"etag\"","assignments":[]}]}]}""";

        var board = JsonSerializer.Deserialize<BoardDisplay>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(board);
        Assert.Null(board.Labels);
        var task = Assert.Single(Assert.Single(board.Buckets).Tasks);
        Assert.Null(task.StartDateTime);
        Assert.Null(task.LabelIds);
    }

    [Fact]
    public void TaskDetailsResponse_ExposesOrganizationFields()
    {
        var start = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var details = new TaskDetailsResponse("task", "Work", "bucket", null, [], [],
            StartDateTime: start, Priority: 3, PercentComplete: 50, LabelIds: ["category1"]);

        Assert.Equal(start, details.StartDateTime);
        Assert.Equal(3, details.Priority);
        Assert.Equal(50, details.PercentComplete);
        Assert.Equal(["category1"], details.LabelIds);
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
