using System.Net;
using System.Text;
using System.Text.Json;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Tests;

public sealed class GraphClientTests
{
    [Fact]
    public async Task GetUserDisplayNameAsync_ReadsBasicProfile()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/v1.0/users/person", request.RequestUri!.AbsolutePath);
            Assert.Equal("?$select=displayName", request.RequestUri.Query);
            return """{"displayName":"Alex Smith"}""";
        });

        Assert.Equal("Alex Smith", await CreateClient(handler).GetUserDisplayNameAsync("person", CancellationToken.None));
    }

    [Fact]
    public async Task GetGroupMembersAsync_UsesAdvancedQueryHeadersAndUserCast()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Contains("/members/microsoft.graph.user", request.RequestUri!.AbsolutePath);
            Assert.Contains("$count=true", request.RequestUri.Query);
            Assert.Equal("eventual", request.Headers.GetValues("ConsistencyLevel").Single());
            return """{"value":[{"id":"person","displayName":"Alex"}]}""";
        });

        var member = Assert.Single(await CreateClient(handler).GetGroupMembersAsync("group", CancellationToken.None));

        Assert.Equal("person", member.Id);
        Assert.Equal("Alex", member.DisplayName);
    }

    [Fact]
    public async Task GetTaskDetailsAsync_MapsOrderedChecklist()
    {
        var handler = new StubHandler(_ => """{"@odata.etag":"W/\"details\"","description":"Line one\nLine two","checklist":{"second":{"title":"Second","isChecked":true,"orderHint":"z"},"first":{"title":"First","isChecked":false,"orderHint":"a"}}}""");

        var details = await CreateClient(handler).GetTaskDetailsAsync("task", CancellationToken.None);

        Assert.Equal("W/\"details\"", details.ETag);
        Assert.Equal(["second", "first"], details.Checklist.Select(item => item.Id));
        Assert.True(details.Checklist[0].IsChecked);
        Assert.Equal("Line one\nLine two", details.Description);
    }

    [Fact]
    public async Task CompleteChecklistItemAsync_PatchesOnlySelectedItemWithEtag()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/planner/tasks/task/details", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("{\"checklist\":{\"item\":{\"@odata.type\":\"microsoft.graph.plannerChecklistItem\",\"isChecked\":true}}}",
                request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await CreateClient(handler).CompleteChecklistItemAsync("task", "item", "W/\"latest\"", CancellationToken.None);
    }

    [Theory]
    [InlineData("Updated notes", "{\"description\":\"Updated notes\"}")]
    [InlineData("Line one\nLine two", "{\"description\":\"Line one\\nLine two\"}")]
    [InlineData("", "{\"description\":\"\"}")]
    public async Task UpdateTaskDescriptionAsync_PatchesOnlyDescriptionWithDetailsEtag(string description, string expectedBody)
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/planner/tasks/task/details", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"details\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal(expectedBody, request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await CreateClient(handler).UpdateTaskDescriptionAsync("task", description, "W/\"details\"", CancellationToken.None);
    }

    [Fact]
    public async Task GetBucketsAsync_MapsOrderHint()
    {
        var handler = new StubHandler(_ => """{"value":[{"id":"bucket","name":"Doing","planId":"plan","orderHint":"abc"}]}""");

        var buckets = await CreateClient(handler).GetBucketsAsync("plan", CancellationToken.None);

        Assert.Equal("abc", Assert.Single(buckets).OrderHint);
    }

    [Fact]
    public async Task GetMyPlansAsync_RetriesThrottlingAndReadsOwner()
    {
        var calls = 0;
        var handler = new ResponseHandler(request =>
        {
            Assert.Equal("/v1.0/me/planner/plans", request.RequestUri!.AbsolutePath);
            if (++calls == 1)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return throttled;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[{"id":"plan","title":"Team","owner":"group"}]}""")
            };
        });

        var plan = Assert.Single(await CreateClient(handler).GetMyPlansAsync(CancellationToken.None));

        Assert.Equal("group", plan.GroupId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetMemberGroupsAsync_UsesNeutralLabelWhenNameIsUnavailable()
    {
        var handler = new StubHandler(_ => """{"value":[{"id":"group","displayName":null}]}""");

        var group = Assert.Single(await CreateClient(handler).GetMemberGroupsAsync(CancellationToken.None));

        Assert.Equal("Planner", group.DisplayName);
    }

    [Fact]
    public async Task GetTasksAsync_MapsTaskAndFollowsNextPage()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
            ? """{"orderHint":"a"}"""
            : request.RequestUri.AbsolutePath.EndsWith("/tasks")
            ? """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0,"@odata.etag":"W/\"v1\"","conversationThreadId":"thread-1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/next"}"""
            : """{"value":[{"id":"two","title":"Second","planId":"plan","percentComplete":0,"@odata.etag":"W/\"v2\""}]}""");
        var client = CreateClient(handler);

        var tasks = await client.GetTasksAsync("plan", CancellationToken.None);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("W/\"v1\"", tasks[0].ETag);
        Assert.Equal("two", tasks[1].Id);
        Assert.Equal("a", tasks[0].BucketOrderHint);
        Assert.Equal("thread-1", tasks[0].ConversationThreadId);
        Assert.Null(tasks[1].ConversationThreadId);
    }

    [Fact]
    public async Task CompleteTaskAsync_SendsLatestEtagAndCompletionBody()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("{\"percentComplete\":100}", request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });
        await CreateClient(handler).CompleteTaskAsync("task-1", "W/\"latest\"", CancellationToken.None);
    }

    [Fact]
    public async Task MoveTaskAsync_PatchesOnlyBucketWithLatestEtag()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/planner/tasks/task", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("{\"bucketId\":\"target\"}", request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await CreateClient(handler).MoveTaskAsync("task", "target", "W/\"latest\"", CancellationToken.None);
    }

    [Fact]
    public async Task GetTasksAsync_CachesBucketOrderHints()
    {
        var formatReads = 0;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
            ? ReadFormat() : """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0}]}""");
        string ReadFormat() { formatReads++; return """{"orderHint":"a"}"""; }
        var client = CreateClient(handler);

        await client.GetTasksAsync("plan", CancellationToken.None);
        await client.GetTasksAsync("plan", CancellationToken.None);

        Assert.Equal(1, formatReads);
    }

    [Fact]
    public async Task SetAssignmentsAsync_PatchesOnlyChangedUsers()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var assignments = body.RootElement.GetProperty("assignments");
            Assert.Equal(JsonValueKind.Null, assignments.GetProperty("removed").ValueKind);
            Assert.Equal(" !", assignments.GetProperty("added").GetProperty("orderHint").GetString());
            return "{}";
        });

        await CreateClient(handler).SetAssignmentsAsync("task", ["added"], ["removed"], "W/\"latest\"", CancellationToken.None);
    }

    [Fact]
    public async Task CreateTaskAsync_UsesPlanBucketDateAndAssignments()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("plan", body.RootElement.GetProperty("planId").GetString());
            Assert.Equal("bucket", body.RootElement.GetProperty("bucketId").GetString());
            Assert.Equal("New task", body.RootElement.GetProperty("title").GetString());
            Assert.Equal(" !", body.RootElement.GetProperty("assignments").GetProperty("person").GetProperty("orderHint").GetString());
            return "{}";
        });

        await CreateClient(handler).CreateTaskAsync("plan", "bucket", "New task", null, ["person"], CancellationToken.None);
    }

    private static PlannerGraphClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
        new StaticTokenProvider());

    private sealed class StaticTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody(request), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
