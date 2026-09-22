using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskChatServiceTests
{
    [Theory]
    [InlineData("<p>First line<br>Second &amp; final</p><script>alert(1)</script>", "First line\nSecond & final")]
    [InlineData("<style>p{color:red}</style><p>One</p><p>Two</p>", "One\nTwo")]
    [InlineData("Use &lt;script&gt;alert(1)&lt;/script&gt; as text", "Use <script>alert(1)</script> as text")]
    [InlineData("2 < 3 and 5 > 4", "2 < 3 and 5 > 4")]
    public void ToPlainText_NormalizesConversationHtml(string html, string expected)
    {
        Assert.Equal(expected, ConversationText.ToPlainText(html));
    }

    [Fact]
    public async Task GetAsync_ReturnsChronologicalPlainTextHistoryFromSelectedPlansGroup()
    {
        var fixture = CreateFixture();
        fixture.Graph.Page = new GraphConversationPage([
            new("new", "<p>Second &amp; safe</p><script>bad()</script>", "Blair", DateTimeOffset.Parse("2026-09-21T12:05:00Z")),
            new("old", "<p>First<br>line</p>", "Alex", DateTimeOffset.Parse("2026-09-21T12:00:00Z"))
        ], null);

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("available", response.State);
        Assert.Equal(["old", "new"], response.Messages.Select(message => message.Id));
        Assert.Equal("First\nline", response.Messages[0].Body);
        Assert.Equal("Second & safe", response.Messages[1].Body);
        Assert.Equal(("group", "thread", (Uri?)null), Assert.Single(fixture.Graph.ConversationReads));
    }

    [Fact]
    public async Task GetAsync_PreservesTextBodiesAndConvertsOnlyHtmlBodies()
    {
        var fixture = CreateFixture();
        fixture.Graph.Page = new GraphConversationPage([
            new("text", "<script>alert(1)</script>", "Alex", DateTimeOffset.Parse("2026-09-21T12:00:00Z"), "text"),
            new("html", "<p>Safe &amp; sound</p><script>alert(2)</script>", "Blair", DateTimeOffset.Parse("2026-09-21T12:01:00Z"), "html")
        ], null);

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("<script>alert(1)</script>", response.Messages[0].Body);
        Assert.Equal("Safe & sound", response.Messages[1].Body);
    }

    [Fact]
    public async Task GetAsync_ReturnsAvailableEmptyStateWithoutReadingConversationWhenTaskHasNoThread()
    {
        var fixture = CreateFixture(threadId: null);

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("available", response.State);
        Assert.Empty(response.Messages);
        Assert.Null(response.NextCursor);
        Assert.Empty(fixture.Graph.ConversationReads);
        Assert.Equal(1, fixture.Graph.ConversationAccessChecks);
    }

    [Fact]
    public async Task GetAsync_ReturnsInteractionRequiredForTaskWithoutThreadWhenConsentIsMissing()
    {
        var fixture = CreateFixture(threadId: null);
        fixture.Graph.ConversationException = new MsalUiRequiredException("consent_required", "Sensitive detail");

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("interaction_required", response.State);
        Assert.Empty(response.Messages);
        Assert.DoesNotContain("Sensitive", response.Message);
        Assert.Equal(1, fixture.Graph.ConversationAccessChecks);
    }

    [Fact]
    public async Task GetAsync_DoesNotRequireOptionalGroupMemberDirectoryPermission()
    {
        var fixture = CreateFixture();
        fixture.Graph.GroupMembersException = new MsalUiRequiredException("consent_required", "Group members unavailable");

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("available", response.State);
        Assert.Single(fixture.Graph.ConversationReads);
    }

    [Fact]
    public async Task GetAsync_RoundTripsOpaqueContinuationCursor()
    {
        var fixture = CreateFixture();
        var next = new Uri("https://graph.microsoft.com/v1.0/groups/group/threads/thread/posts?$skiptoken=older");
        fixture.Graph.Page = new GraphConversationPage([], next);

        var first = await fixture.Service.GetAsync("task", null, CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("graph.microsoft.com", first.NextCursor);

        fixture.Graph.Page = new GraphConversationPage([], null);
        await fixture.Service.GetAsync("task", first.NextCursor, CancellationToken.None);

        Assert.Equal(next, fixture.Graph.ConversationReads[1].Cursor);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("aHR0cHM6Ly9leGFtcGxlLmNvbS92MS4wL2dyb3Vwcy9ncm91cC90aHJlYWRzL3RocmVhZC9wb3N0cz8kc2tpcHRva2VuPXg")]
    [InlineData("aHR0cHM6Ly9ncmFwaC5taWNyb3NvZnQuY29tL3YxLjAvZ3JvdXBzL2dyb3VwL3RocmVhZHMvb3RoZXIvcG9zdHM_JHNraXB0b2tlbj14")]
    public async Task GetAsync_RejectsMalformedMaliciousOrStaleCursor(string cursor)
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.GetAsync("task", cursor, CancellationToken.None));

        Assert.Empty(fixture.Graph.ConversationReads);
    }

    [Fact]
    public async Task GetAsync_ReturnsInteractionRequiredWhenConversationConsentIsMissing()
    {
        var fixture = CreateFixture();
        fixture.Graph.ConversationException = new MsalUiRequiredException("consent_required", "Sensitive detail");

        var response = await fixture.Service.GetAsync("task", null, CancellationToken.None);

        Assert.Equal("interaction_required", response.State);
        Assert.Empty(response.Messages);
        Assert.DoesNotContain("Sensitive", response.Message);
    }

    [Fact]
    public async Task GetAsync_RejectsTaskFromDifferentPlanBeforeReadingConversation()
    {
        var fixture = CreateFixture(taskPlanId: "other-plan");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.GetAsync("task", null, CancellationToken.None));

        Assert.Empty(fixture.Graph.ConversationReads);
    }

    [Fact]
    public async Task PostAsync_RepliesOnceAndReturnsRefreshedConversation()
    {
        var fixture = CreateFixture();
        fixture.Graph.Page = new GraphConversationPage([
            new("posted", "Accepted", "Alex", DateTimeOffset.Parse("2026-09-21T12:00:00Z"))
        ], null);

        var response = await fixture.Service.PostAsync("task", "Status update", CancellationToken.None);

        Assert.Equal(("group", "thread", "Status update"), Assert.Single(fixture.Graph.Replies));
        Assert.Equal("posted", Assert.Single(response.Messages).Id);
    }

    [Fact]
    public async Task PostAsync_CreatesAndAttachesFirstThreadThenInvalidatesDetailsAndReloads()
    {
        var fixture = CreateFixture(threadId: null);
        await fixture.Details.GetAsync("task", CancellationToken.None);
        fixture.Graph.Page = new GraphConversationPage([
            new("first", "First comment", "Alex", DateTimeOffset.Parse("2026-09-21T12:00:00Z"))
        ], null);

        var response = await fixture.Service.PostAsync("task", "First comment", CancellationToken.None);
        await fixture.Details.GetAsync("task", CancellationToken.None);

        Assert.Equal(("group", "Task title", "First comment"), Assert.Single(fixture.Graph.Creates));
        Assert.Equal(("task", "new-thread", "W/\"task\""), Assert.Single(fixture.Graph.Attachments));
        Assert.Equal("new-thread", Assert.Single(fixture.Graph.ConversationReads).ThreadId);
        Assert.Equal("first", Assert.Single(response.Messages).Id);
        Assert.Equal(2, fixture.Graph.DetailReads);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4000, false)]
    [InlineData(4001, true)]
    public async Task PostAsync_RequiresNonEmptyMessageWithinLimit(int length, bool shouldReject)
    {
        var fixture = CreateFixture();
        var message = new string('x', length);

        if (shouldReject)
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.PostAsync("task", message, CancellationToken.None));
        else
            await fixture.Service.PostAsync("task", message, CancellationToken.None);

        Assert.Equal(shouldReject ? 0 : 1, fixture.Graph.Replies.Count);
    }

    [Fact]
    public async Task PostAsync_ReconcilesMatchingThreadAfterUncertainAttachmentWithoutCreatingAgain()
    {
        var fixture = CreateFixture(threadId: null);
        fixture.Graph.AttachmentResults.Enqueue(new HttpRequestException("connection dropped"));
        fixture.Graph.TaskResult = read => read == 1
            ? fixture.Graph.CurrentTask
            : fixture.Graph.CurrentTask with { ConversationThreadId = "new-thread", ETag = "W/\"latest\"" };

        var response = await fixture.Service.PostAsync("task", "First comment", CancellationToken.None);

        Assert.Equal("available", response.State);
        Assert.Single(fixture.Graph.Creates);
        Assert.Single(fixture.Graph.Attachments);
        Assert.Equal("new-thread", Assert.Single(fixture.Graph.ConversationReads).ThreadId);
    }

    [Fact]
    public async Task PostAsync_RetriesSameThreadWithRefreshedEtagAfterConflict()
    {
        var fixture = CreateFixture(threadId: null);
        fixture.Graph.AttachmentResults.Enqueue(new GraphApiException(HttpStatusCode.PreconditionFailed, "stale"));
        fixture.Graph.AttachmentResults.Enqueue(null);
        fixture.Graph.TaskResult = read => read == 1
            ? fixture.Graph.CurrentTask
            : fixture.Graph.CurrentTask with { ETag = "W/\"latest\"" };

        var response = await fixture.Service.PostAsync("task", "First comment", CancellationToken.None);

        Assert.Equal("available", response.State);
        Assert.Single(fixture.Graph.Creates);
        Assert.Equal([
            ("task", "new-thread", "W/\"task\""),
            ("task", "new-thread", "W/\"latest\"")
        ], fixture.Graph.Attachments);
    }

    [Fact]
    public async Task PostAsync_ReturnsAttachmentPendingAfterPersistentFailureWithoutCreatingAgain()
    {
        var fixture = CreateFixture(threadId: null);
        fixture.Graph.AttachmentResults.Enqueue(new GraphApiException(HttpStatusCode.PreconditionFailed, "stale"));
        fixture.Graph.AttachmentResults.Enqueue(new HttpRequestException("connection dropped"));
        fixture.Graph.TaskResult = read => read == 1
            ? fixture.Graph.CurrentTask
            : fixture.Graph.CurrentTask with { ETag = $"W/\"latest-{read}\"" };

        var response = await fixture.Service.PostAsync("task", "First comment", CancellationToken.None);

        Assert.Equal("attachment_pending", response.State);
        Assert.Contains("comment was created", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(fixture.Graph.Creates);
        Assert.Equal(2, fixture.Graph.Attachments.Count);
        Assert.All(fixture.Graph.Attachments, attachment => Assert.Equal("new-thread", attachment.ThreadId));
        Assert.Empty(fixture.Graph.ConversationReads);
    }

    [Fact]
    public async Task PostAsync_DoesNotSwallowAuthorizationFailureWhileAttachingThread()
    {
        var fixture = CreateFixture(threadId: null);
        fixture.Graph.AttachmentResults.Enqueue(new GraphApiException(HttpStatusCode.Unauthorized, "expired"));

        var error = await Assert.ThrowsAsync<GraphApiException>(() =>
            fixture.Service.PostAsync("task", "First comment", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Single(fixture.Graph.Attachments);
    }

    [Fact]
    public async Task PostAsync_TranslatesMissingConversationConsentToForbidden()
    {
        var fixture = CreateFixture();
        fixture.Graph.ConversationException = new MsalUiRequiredException("consent_required", "Sensitive detail");

        var error = await Assert.ThrowsAsync<GraphApiException>(() =>
            fixture.Service.PostAsync("task", "Status update", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("Sensitive", error.Message);
    }

    private static Fixture CreateFixture(string? threadId = "thread", string taskPlanId = "plan")
    {
        var graph = new FakeGraph
        {
            CurrentTask = new GraphTask("task", "Task title", taskPlanId, "bucket", null, null, 0,
                "W/\"task\"", [], ConversationThreadId: threadId)
        };
        var settings = new FakeSettingsStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var details = new TaskDetailsService(graph, cache);
        return new Fixture(new TaskChatService(graph, settings, details), graph, details, cache);
    }

    private sealed record Fixture(TaskChatService Service, FakeGraph Graph, TaskDetailsService Details, MemoryCache Cache);

    private sealed class FakeSettingsStore : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SettingsDto("plan", "Plan", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) => Task.FromResult<BoardDisplay?>(null);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public GraphTask CurrentTask { get; set; } = null!;
        public GraphConversationPage Page { get; set; } = new([], null);
        public Exception? ConversationException { get; set; }
        public Exception? GroupMembersException { get; set; }
        public Queue<Exception?> AttachmentResults { get; } = [];
        public Func<int, GraphTask>? TaskResult { get; set; }
        public int TaskReads { get; private set; }
        public int ConversationAccessChecks { get; private set; }
        public int DetailReads { get; private set; }
        public List<(string GroupId, string ThreadId, Uri? Cursor)> ConversationReads { get; } = [];
        public List<(string GroupId, string ThreadId, string Message)> Replies { get; } = [];
        public List<(string GroupId, string Topic, string Message)> Creates { get; } = [];
        public List<(string TaskId, string ThreadId, string ETag)> Attachments { get; } = [];

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
        {
            TaskReads++;
            return Task.FromResult<GraphTask?>(TaskResult?.Invoke(TaskReads) ?? CurrentTask);
        }
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraphPlan>>([new("plan", "Plan", "group", null)]);
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
        {
            if (GroupMembersException is not null) throw GroupMembersException;
            return Task.FromResult<IReadOnlyList<GraphGroup>>([new("group", "Group")]);
        }
        public Task<GraphConversationPage> GetConversationPostsAsync(string groupId, string threadId, Uri? continuationUri,
            CancellationToken cancellationToken)
        {
            if (ConversationException is not null) throw ConversationException;
            ConversationReads.Add((groupId, threadId, continuationUri));
            return Task.FromResult(Page);
        }
        public Task EnsureConversationAccessAsync(CancellationToken cancellationToken)
        {
            ConversationAccessChecks++;
            if (ConversationException is not null) throw ConversationException;
            return Task.CompletedTask;
        }
        public Task ReplyToConversationAsync(string groupId, string threadId, string message, CancellationToken cancellationToken)
        {
            if (ConversationException is not null) throw ConversationException;
            Replies.Add((groupId, threadId, message));
            return Task.CompletedTask;
        }
        public Task<string> CreateConversationThreadAsync(string groupId, string topic, string message,
            CancellationToken cancellationToken)
        {
            if (ConversationException is not null) throw ConversationException;
            Creates.Add((groupId, topic, message));
            return Task.FromResult("new-thread");
        }
        public Task SetConversationThreadAsync(string taskId, string threadId, string etag, CancellationToken cancellationToken)
        {
            Attachments.Add((taskId, threadId, etag));
            if (AttachmentResults.TryDequeue(out var result) && result is not null) throw result;
            CurrentTask = CurrentTask with { ConversationThreadId = threadId };
            return System.Threading.Tasks.Task.CompletedTask;
        }
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken)
        {
            DetailReads++;
            return Task.FromResult(new GraphTaskDetails("W/\"details\"", [], "Notes"));
        }
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
