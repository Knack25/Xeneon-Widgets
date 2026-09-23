using System.Net;
using System.Text;
using System.Text.Json;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Planner;
using Microsoft.Extensions.Caching.Memory;
using MicrosoftWidgets.Helper.Tests;

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
        Assert.Equal(["first", "second"], details.Checklist.Select(item => item.Id));
        Assert.False(details.Checklist[0].IsChecked);
        Assert.Equal("Line one\nLine two", details.Description);
    }

    [Fact]
    public async Task GetTaskDetailsAsync_PreservesGraphSourceOrderForEqualHints()
    {
        var handler = new StubHandler(_ => """{"checklist":{"second":{"title":"Second","orderHint":"a"},"first":{"title":"First","orderHint":"a"}}}""");

        var details = await CreateClient(handler).GetTaskDetailsAsync("task", CancellationToken.None);

        Assert.Equal(["second", "first"], details.Checklist.Select(item => item.Id));
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

    [Fact]
    public async Task PatchChecklistAsync_DeletesItemAsJsonNullWithDetailsEtag()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/planner/tasks/task/details", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"details\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("{\"checklist\":{\"item\":null}}", request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await ((IPlannerGraphClient)CreateClient(handler)).PatchChecklistAsync(
            "task", "item", null, "W/\"details\"", CancellationToken.None);
    }

    [Fact]
    public async Task PatchChecklistAsync_SerializesOnlyRequestedChecklistFields()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("{\"checklist\":{\"item\":{\"@odata.type\":\"microsoft.graph.plannerChecklistItem\",\"title\":\"Renamed\",\"orderHint\":\"previous next!\"}}}",
                request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await ((IPlannerGraphClient)CreateClient(handler)).PatchChecklistAsync(
            "task", "item", new GraphChecklistPatch("Renamed", "previous next!"),
            "W/\"details\"", CancellationToken.None);
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
            : request.RequestUri.AbsolutePath.EndsWith("/tasks") && string.IsNullOrEmpty(request.RequestUri.Query)
            ? """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0,"@odata.etag":"W/\"v1\"","conversationThreadId":"thread-1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/planner/plans/plan/tasks?$skiptoken=next"}"""
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

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/planner/plans/plan/tasks?$skiptoken=x")]
    [InlineData("https://example.com/v1.0/planner/plans/plan/tasks?$skiptoken=x")]
    [InlineData("https://graph.microsoft.com/beta/planner/plans/plan/tasks?$skiptoken=x")]
    [InlineData("https://graph.microsoft.com/v1.0/users?$skiptoken=x")]
    [InlineData("https://user:password@graph.microsoft.com/v1.0/planner/plans/plan/tasks?$skiptoken=x")]
    [InlineData("https://graph.microsoft.com/v1.0/planner/plans/plan/tasks?$skiptoken=x#fragment")]
    public void GraphNextLinkPolicy_RejectsHostileOrForeignLinks(string candidate)
    {
        var current = new Uri("https://graph.microsoft.com/v1.0/planner/plans/plan/tasks");

        Assert.Throws<InvalidDataException>(() => GraphNextLinkPolicy.RequireAllowed(
            current, candidate, new HashSet<string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void GraphNextLinkPolicy_RejectsLoopsPageOverflowAndOperationRecordOverflow()
    {
        var current = new Uri("https://graph.microsoft.com/v1.0/planner/plans/plan/tasks");
        var next = "https://graph.microsoft.com/v1.0/planner/plans/plan/tasks?$skiptoken=one";
        var visited = new HashSet<string>(StringComparer.Ordinal) { current.AbsoluteUri };
        var accepted = GraphNextLinkPolicy.RequireAllowed(current, next, visited);

        Assert.Equal(new Uri(next), accepted);
        Assert.Throws<InvalidDataException>(() => GraphNextLinkPolicy.RequireAllowed(current, next, visited));
        Assert.Throws<InvalidDataException>(() => GraphNextLinkPolicy.RequirePageCount(101));
        Assert.Throws<InvalidDataException>(() => GraphNextLinkPolicy.RequireRecordCount(
            GraphCollectionKind.Tasks, GraphNextLinkPolicy.TaskRecordLimit + 1));
    }

    [Fact]
    public async Task GetTasksAsync_RejectsForeignNextLinkBeforeBearerTokenCanLeaveGraph()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return """{"value":[],"@odata.nextLink":"https://attacker.example/steal"}""";
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(handler).GetTasksAsync("plan", CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetMyPlansAsync_StopsBeforeRequestingPage101()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            var nextPage = calls + 1;
            return $$"""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/planner/plans?$skiptoken={{nextPage}}"}""";
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(handler).GetMyPlansAsync(CancellationToken.None));

        Assert.Equal(100, calls);
    }

    [Fact]
    public async Task GetTasksAsync_RejectsOperationRecordOverflowBeforeTaskFormatRequests()
    {
        var values = string.Join(',', Enumerable.Range(0, GraphNextLinkPolicy.TaskRecordLimit + 1)
            .Select(index => $$"""{"id":"{{index}}","title":"Task","planId":"plan","percentComplete":0}"""));
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return $$"""{"value":[{{values}}]}""";
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(handler).GetTasksAsync("plan", CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void PlannerGraphTransport_DisablesAutomaticRedirects()
    {
        using var handler = PlannerGraphHttpHandlerFactory.Create();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task GetTasksAsync_MapsStartAndAppliedCategories()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
            ? """{"orderHint":"a"}"""
            : """{"value":[{"id":"task","title":"Work","planId":"plan","percentComplete":50,"priority":3,"startDateTime":"2026-09-21T12:00:00Z","appliedCategories":{"category1":true,"category2":false}}]}""");

        var task = Assert.Single(await CreateClient(handler).GetTasksAsync("plan", CancellationToken.None));

        Assert.Equal(["category1"], task.AppliedCategories);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), task.StartDateTime);
    }

    [Fact]
    public async Task GetPlanLabelsAsync_ReturnsOnlyNamedCategories()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/v1.0/planner/plans/plan/details", request.RequestUri!.AbsolutePath);
            return """{"categoryDescriptions":{"category1":"Blocked","category2":null,"category25":"Release"}}""";
        });

        var labels = await CreateClient(handler).GetPlanLabelsAsync("plan", CancellationToken.None);

        Assert.Equal(["category1", "category25"], labels.Select(label => label.Id));
        Assert.Equal(["Blocked", "Release"], labels.Select(label => label.Name));
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
    public async Task Planner_mutation_does_not_reach_graph_after_account_is_invalidated()
    {
        var identities = new MicrosoftAccountStateTests.IdentityProvider(new MicrosoftAccountIdentity(
            "home", "tenant", "client", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var lease = await state.GetAsync(default);
        var tokens = new BlockingTokenProvider();
        var sends = 0;
        var handler = new StubHandler(_ =>
        {
            Interlocked.Increment(ref sends);
            return "{}";
        });
        var client = new PlannerGraphClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            tokens,
            state);
        using var binding = state.BindRequest(lease);

        var mutation = client.CompleteTaskAsync("task-1", "W/\"latest\"", default);
        await tokens.Started;
        await state.InvalidateAsync(default);
        tokens.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => mutation);
        Assert.Equal("account_changed", error.Code);
        Assert.Equal(0, Volatile.Read(ref sends));
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
    public async Task AccountInvalidationClearsCachedBucketOrderHints()
    {
        var identities = new MicrosoftAccountStateTests.IdentityProvider(new MicrosoftAccountIdentity(
            "home", "tenant", "client", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var formatReads = 0;
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
            ? ReadFormat() : """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0}]}""");
        string ReadFormat() { formatReads++; return """{"orderHint":"a"}"""; }
        var client = new PlannerGraphClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            new StaticTokenProvider(), state);
        var firstLease = await state.GetAsync(default);
        using (state.BindRequest(firstLease))
        {
            await client.GetTasksAsync("plan", default);
            await client.GetTasksAsync("plan", default);
        }

        await state.InvalidateAsync(default);
        var secondLease = await state.GetAsync(default);
        using (state.BindRequest(secondLease))
            await client.GetTasksAsync("plan", default);

        Assert.Equal(2, formatReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Late_bucket_format_result_is_not_published_after_account_switch(bool successfulFormat)
    {
        var identities = new MicrosoftAccountStateTests.IdentityProvider(new MicrosoftAccountIdentity(
            "home-one", "tenant", "client", "one@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var handler = new DelayedFormatHandler(successfulFormat);
        var client = new PlannerGraphClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            new StaticTokenProvider(), state);
        var firstLease = await state.GetAsync(default);
        Task<IReadOnlyList<GraphTask>> stale;
        using (state.BindRequest(firstLease))
            stale = client.GetTasksAsync("plan", default);
        await handler.FirstFormatStarted.WaitAsync(TimeSpan.FromSeconds(5));

        identities.Identity = identities.Identity with { HomeAccountId = "home-two" };
        await state.InvalidateAsync(default);
        handler.ReleaseFirstFormat();
        await Assert.ThrowsAsync<OutlookException>(() => stale);

        var secondLease = await state.GetAsync(default);
        IReadOnlyList<GraphTask> current;
        using (state.BindRequest(secondLease))
            current = await client.GetTasksAsync("plan", default);

        Assert.Equal(2, handler.FormatReads);
        Assert.Equal("new-account-hint", Assert.Single(current).BucketOrderHint);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Late_bucket_format_result_is_not_published_after_planner_purge(bool successfulFormat)
    {
        var identities = new MicrosoftAccountStateTests.IdentityProvider(new MicrosoftAccountIdentity(
            "home", "tenant", "client", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var access = new PlannerDataAccessGate();
        var lifecycle = new PlannerDataLifecycle(new MemoryCache(new MemoryCacheOptions()), access);
        var handler = new DelayedFormatHandler(successfulFormat);
        var client = new PlannerGraphClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            new StaticTokenProvider(), state, access);
        var lease = await state.GetAsync(default);
        Task<IReadOnlyList<GraphTask>> stale;
        using (state.BindRequest(lease))
        using (lifecycle.BindOperation())
            stale = client.GetTasksAsync("plan", default);
        await handler.FirstFormatStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await lifecycle.PurgeAsync(default);
        handler.ReleaseFirstFormat();
        var error = await Assert.ThrowsAsync<OutlookException>(() => stale);
        Assert.Equal("planner_data_changed", error.Code);

        IReadOnlyList<GraphTask> current;
        using (state.BindRequest(lease))
        using (lifecycle.BindOperation())
            current = await client.GetTasksAsync("plan", default);

        Assert.Equal(2, handler.FormatReads);
        Assert.Equal("new-account-hint", Assert.Single(current).BucketOrderHint);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetTasksAsync_RethrowsAuthorizationFailureFromBucketFormat(HttpStatusCode status)
    {
        var handler = new ResponseHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat")
            ? new HttpResponseMessage(status) { Content = new StringContent("denied") }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0}]}""",
                    Encoding.UTF8, "application/json")
            });

        var error = await Assert.ThrowsAsync<GraphApiException>(() =>
            CreateClient(handler).GetTasksAsync("plan", CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
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

    [Fact]
    public async Task UpdateTaskAsync_PatchesOnlyRequestedPropertiesWithEtag()
    {
        var requests = new List<string>();
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/v1.0/planner/tasks/task", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            requests.Add(request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });
        var client = (IPlannerGraphClient)CreateClient(handler);

        await client.UpdateTaskAsync("task", new GraphTaskUpdate(Title: "Renamed"), "W/\"latest\"", default);
        await client.UpdateTaskAsync("task", new GraphTaskUpdate(PercentComplete: 50), "W/\"latest\"", default);
        await client.UpdateTaskAsync("task", new GraphTaskUpdate(Priority: 3), "W/\"latest\"", default);
        await client.UpdateTaskAsync("task", new GraphTaskUpdate(
            StartDateTime: new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)), "W/\"latest\"", default);
        await client.UpdateTaskAsync("task", new GraphTaskUpdate(ClearStartDate: true), "W/\"latest\"", default);
        await client.UpdateTaskAsync("task", new GraphTaskUpdate(
            AppliedCategories: new Dictionary<string, bool?> { ["category1"] = false, ["category2"] = true }),
            "W/\"latest\"", default);

        Assert.Equal([
            "{\"title\":\"Renamed\"}",
            "{\"percentComplete\":50}",
            "{\"priority\":3}",
            "{\"startDateTime\":\"2026-09-21T12:00:00+00:00\"}",
            "{\"startDateTime\":null}",
            "{\"appliedCategories\":{\"category1\":false,\"category2\":true}}"
        ], requests);
    }

    [Fact]
    public async Task CreateTaskAsync_IncludesOptionalMetadataAndOmitsProgress()
    {
        var handler = new StubHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("2026-09-21T12:00:00+00:00", body.RootElement.GetProperty("startDateTime").GetString());
            Assert.Equal(5, body.RootElement.GetProperty("priority").GetInt32());
            Assert.True(body.RootElement.GetProperty("appliedCategories").GetProperty("category2").GetBoolean());
            Assert.False(body.RootElement.TryGetProperty("percentComplete", out _));
            return "{}";
        });

        await CreateClient(handler).CreateTaskAsync("plan", "bucket", "New task", null, [],
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), 5, ["category2"], CancellationToken.None);
    }

    [Fact]
    public async Task GetConversationPostsAsync_UsesConversationTokenAndMapsPostsAndContinuation()
    {
        var next = "https://graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=older";
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1.0/groups/group/threads/thread/posts", request.RequestUri!.AbsolutePath);
            Assert.Equal("?$select=id,body,sender,from,createdDateTime", request.RequestUri.Query);
            Assert.Equal("conversation-token", request.Headers.Authorization?.Parameter);
            return """{"value":[{"id":"post-1","body":{"contentType":"html","content":"<p>Hello</p>"},"sender":{"emailAddress":{"name":"Actual sender","address":"sender@example.com"}},"from":{"emailAddress":{"name":"Fallback author","address":"fallback@example.com"}},"createdDateTime":"2026-09-21T12:00:00Z"}],"@odata.nextLink":"NEXT"}"""
                .Replace("NEXT", next, StringComparison.Ordinal);
        });

        var page = await CreateClient(handler, new DistinctTokenProvider())
            .GetConversationPostsAsync("group", "thread", null, CancellationToken.None);

        var post = Assert.Single(page.Posts);
        Assert.Equal("post-1", post.Id);
        Assert.Equal("<p>Hello</p>", post.Body);
        Assert.Equal("html", post.ContentType);
        Assert.Equal("Actual sender", post.Author);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), post.CreatedAt);
        Assert.Equal(next, page.NextLink?.AbsoluteUri);
    }

    [Fact]
    public async Task GetConversationPostsAsync_FallsBackToFromWhenSenderIsUnavailableAndRetainsTextType()
    {
        var handler = new StubHandler(_ =>
            """{"value":[{"id":"post-1","body":{"contentType":"text","content":"<script>alert(1)</script>"},"from":{"emailAddress":{"name":"Fallback author"}}}]}""");

        var post = Assert.Single((await CreateClient(handler, new DistinctTokenProvider())
            .GetConversationPostsAsync("group", "thread", null, CancellationToken.None)).Posts);

        Assert.Equal("text", post.ContentType);
        Assert.Equal("<script>alert(1)</script>", post.Body);
        Assert.Equal("Fallback author", post.Author);
    }

    [Fact]
    public async Task GetConversationPostsAsync_RejectsOperationRecordOverflow()
    {
        var values = Enumerable.Range(0, GraphNextLinkPolicy.ConversationRecordLimit + 1)
            .Select(index => new { id = index.ToString(), body = new { contentType = "text", content = "x" } });
        var payload = JsonSerializer.Serialize(new { value = values });
        var handler = new StubHandler(_ => payload);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateClient(handler, new DistinctTokenProvider())
            .GetConversationPostsAsync("group", "thread", null, CancellationToken.None));
    }

    [Fact]
    public async Task GetGroupMembersAsync_RejectsForeignNextLinkBeforeDirectoryTokenCanLeaveGraph()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return """{"value":[],"@odata.nextLink":"https://attacker.example/steal"}""";
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateClient(handler).GetGroupMembersAsync("group", CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetConversationPostsAsync_UsesValidatedContinuationWithoutAddingTop()
    {
        var continuation = new Uri("https://graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=older");
        var handler = new StubHandler(request =>
        {
            Assert.Equal(continuation, request.RequestUri);
            Assert.DoesNotContain("$top", request.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
            return """{"value":[]}""";
        });

        await CreateClient(handler, new DistinctTokenProvider())
            .GetConversationPostsAsync("group", "thread", continuation, CancellationToken.None);
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=x")]
    [InlineData("https://example.com/v1.0/groups/group/threads/thread/posts?$skiptoken=x")]
    [InlineData("https://graph.microsoft.com/v1.0/groups/group/threads/other/posts?$skiptoken=x")]
    [InlineData("https://user:password@graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=x")]
    [InlineData("https://graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=x#fragment")]
    public async Task GetConversationPostsAsync_RejectsUnsafeContinuation(string continuation)
    {
        var handler = new StubHandler(_ => throw new Xunit.Sdk.XunitException("Unsafe continuation reached the network."));

        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient(handler, new DistinctTokenProvider())
            .GetConversationPostsAsync("group", "thread", new Uri(continuation), CancellationToken.None));
    }

    [Fact]
    public async Task ReplyToConversationAsync_PostsPlainTextWithConversationToken()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/groups/group/threads/thread/reply", request.RequestUri!.AbsolutePath);
            Assert.Equal("conversation-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var postBody = body.RootElement.GetProperty("post").GetProperty("body");
            Assert.Equal("text", postBody.GetProperty("contentType").GetString());
            Assert.Equal("Status <ready>", postBody.GetProperty("content").GetString());
            return "{}";
        });

        await CreateClient(handler, new DistinctTokenProvider())
            .ReplyToConversationAsync("group", "thread", "Status <ready>", CancellationToken.None);
    }

    [Fact]
    public async Task CreateConversationThreadAsync_PostsPlainTextAndReturnsThreadId()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/groups/group/threads", request.RequestUri!.AbsolutePath);
            Assert.Equal("conversation-token", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("Task title", body.RootElement.GetProperty("topic").GetString());
            var postBody = body.RootElement.GetProperty("posts")[0].GetProperty("body");
            Assert.Equal("text", postBody.GetProperty("contentType").GetString());
            Assert.Equal("First comment", postBody.GetProperty("content").GetString());
            return """{"id":"new-thread"}""";
        });

        var threadId = await CreateClient(handler, new DistinctTokenProvider())
            .CreateConversationThreadAsync("group", "Task title", "First comment", CancellationToken.None);

        Assert.Equal("new-thread", threadId);
    }

    [Fact]
    public async Task SetConversationThreadAsync_PatchesTaskWithCurrentEtag()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/v1.0/planner/tasks/task", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"latest\"", request.Headers.IfMatch.Single().ToString());
            Assert.Equal("core-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("{\"conversationThreadId\":\"new-thread\"}", request.Content!.ReadAsStringAsync().Result);
            return "{}";
        });

        await CreateClient(handler, new DistinctTokenProvider())
            .SetConversationThreadAsync("task", "new-thread", "W/\"latest\"", CancellationToken.None);
    }

    private static PlannerGraphClient CreateClient(HttpMessageHandler handler, IGraphTokenProvider? tokenProvider = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
        tokenProvider ?? new StaticTokenProvider());

    private sealed class DelayedFormatHandler(bool successfulFirstFormat) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstFormatStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstFormat = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int formatReads;
        public Task FirstFormatStarted => firstFormatStarted.Task;
        public int FormatReads => Volatile.Read(ref formatReads);
        public void ReleaseFirstFormat() => releaseFirstFormat.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/bucketTaskBoardFormat"))
                return Json("""{"value":[{"id":"one","title":"First","planId":"plan","percentComplete":0}]}""");
            var read = Interlocked.Increment(ref formatReads);
            if (read == 1)
            {
                firstFormatStarted.TrySetResult();
                await releaseFirstFormat.Task.WaitAsync(cancellationToken);
                return successfulFirstFormat
                    ? Json("""{"orderHint":"old-account-hint"}""")
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("failed") };
            }
            return Json("""{"orderHint":"new-account-hint"}""");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StaticTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");
    }

    private sealed class DistinctTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("core-token");
        public Task<string> GetConversationTokenAsync(CancellationToken cancellationToken) => Task.FromResult("conversation-token");
    }

    private sealed class BlockingTokenProvider : IGraphTokenProvider
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return "stale-token";
        }
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
