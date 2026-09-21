using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskMetadataServiceTests
{
    [Fact]
    public async Task SetTitleAsync_TrimsTitleUsesLatestEtagAndInvalidatesDetails()
    {
        var fixture = CreateFixture();
        fixture.Cache.Set("task", new object());

        await fixture.Service.SetTitleAsync("task", "  Updated title  ", CancellationToken.None);

        var update = Assert.Single(fixture.Graph.Updates);
        Assert.Equal("Updated title", update.Update.Title);
        Assert.Equal("latest-etag", update.ETag);
        Assert.False(fixture.Cache.TryGetValue("task", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetTitleAsync_RejectsBlankTitles(string title)
    {
        var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetTitleAsync("task", title, CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Fact]
    public async Task SetTitleAsync_RejectsTitlesLongerThan255Characters()
    {
        var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetTitleAsync("task", new string('x', 256), CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task SetProgressAsync_AcceptsPlannerValues(int value)
    {
        var fixture = CreateFixture();
        await fixture.Service.SetProgressAsync("task", value, CancellationToken.None);
        Assert.Equal(value, Assert.Single(fixture.Graph.Updates).Update.PercentComplete);
    }

    [Fact]
    public async Task SetProgressAsync_RejectsUnsupportedValue()
    {
        var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetProgressAsync("task", 25, CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public async Task SetPriorityAsync_AcceptsPlannerValues(int value)
    {
        var fixture = CreateFixture();
        await fixture.Service.SetPriorityAsync("task", value, CancellationToken.None);
        Assert.Equal(value, Assert.Single(fixture.Graph.Updates).Update.Priority);
    }

    [Fact]
    public async Task SetPriorityAsync_RejectsUnsupportedValue()
    {
        var fixture = CreateFixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetPriorityAsync("task", 2, CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Fact]
    public async Task SetStartDateAsync_ParsesNoonUtc()
    {
        var fixture = CreateFixture();
        await fixture.Service.SetStartDateAsync("task", "2026-09-19", CancellationToken.None);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
            Assert.Single(fixture.Graph.Updates).Update.StartDateTime);
    }

    [Fact]
    public async Task SetStartDateAsync_NullClearsDate()
    {
        var fixture = CreateFixture();
        await fixture.Service.SetStartDateAsync("task", null, CancellationToken.None);
        var update = Assert.Single(fixture.Graph.Updates).Update;
        Assert.True(update.ClearStartDate);
        Assert.Null(update.StartDateTime);
    }

    [Fact]
    public async Task SetStartDateAsync_RejectsDateAfterDue()
    {
        var fixture = CreateFixture(due: new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetStartDateAsync("task", "2026-09-21", CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Fact]
    public async Task Updates_RejectTaskOutsideSelectedPlan()
    {
        var fixture = CreateFixture(taskPlan: "other-plan");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetTitleAsync("task", "Updated", CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Fact]
    public async Task SetLabelsAsync_SendsOnlyNamedCategoryChangesAndPreservesUnnamedAppliedCategory()
    {
        var fixture = CreateFixture(appliedCategories: ["category1", "category3"]);

        await fixture.Service.SetLabelsAsync("task", ["category2"], CancellationToken.None);

        var changes = Assert.Single(fixture.Graph.Updates).Update.AppliedCategories!;
        Assert.Equal(2, changes.Count);
        Assert.False(changes["category1"]);
        Assert.True(changes["category2"]);
        Assert.DoesNotContain("category3", changes.Keys);
    }

    [Fact]
    public async Task SetLabelsAsync_RejectsUnnamedCategory()
    {
        var fixture = CreateFixture(appliedCategories: ["category3"]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.SetLabelsAsync("task", ["category3"], CancellationToken.None));
        Assert.Empty(fixture.Graph.Updates);
    }

    [Fact]
    public async Task CreateAsync_DefaultsPriorityAndPassesValidatedMetadata()
    {
        var fixture = CreateFixture();
        var members = new BoardMemberService(fixture.Graph, fixture.Settings);
        var creation = new TaskCreationService(fixture.Graph, fixture.Settings, members);

        await creation.CreateAsync(" New task ", "bucket", "2026-09-25", [], CancellationToken.None,
            startDate: "2026-09-21", priority: null, labelIds: ["category2"]);

        var created = Assert.Single(fixture.Graph.Creations);
        Assert.Equal("New task", created.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), created.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), created.Due);
        Assert.Equal(5, created.Priority);
        Assert.Equal(["category2"], created.LabelIds);
    }

    [Fact]
    public async Task CreateAsync_RejectsStartAfterDueBeforeWrite()
    {
        var fixture = CreateFixture();
        var creation = new TaskCreationService(fixture.Graph, fixture.Settings,
            new BoardMemberService(fixture.Graph, fixture.Settings));

        await Assert.ThrowsAsync<ArgumentException>(() => creation.CreateAsync("Task", "bucket", "2026-09-20", [],
            CancellationToken.None, startDate: "2026-09-21", priority: 5, labelIds: ["category2"]));

        Assert.Empty(fixture.Graph.Creations);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnknownLabelsBeforeWrite()
    {
        var fixture = CreateFixture();
        var creation = new TaskCreationService(fixture.Graph, fixture.Settings,
            new BoardMemberService(fixture.Graph, fixture.Settings));

        await Assert.ThrowsAsync<ArgumentException>(() => creation.CreateAsync("Task", "bucket", null, [],
            CancellationToken.None, labelIds: ["category3"]));

        Assert.Empty(fixture.Graph.Creations);
    }

    private static Fixture CreateFixture(DateTimeOffset? due = null, string taskPlan = "plan",
        IReadOnlyList<string>? appliedCategories = null)
    {
        var graph = new FakeGraph(new GraphTask("task", "Task", taskPlan, "bucket", due, 5, 0,
            "latest-etag", [], AppliedCategories: appliedCategories ?? []));
        var settings = new FakeSettings();
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new Fixture(new TaskMetadataService(graph, settings, cache), graph, settings, cache);
    }

    private sealed record Fixture(TaskMetadataService Service, FakeGraph Graph, FakeSettings Settings, IMemoryCache Cache);

    private sealed class FakeSettings : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) =>
            Task.FromResult(new SettingsDto("plan", "Board", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeGraph(GraphTask task) : IPlannerGraphClient
    {
        public List<(string TaskId, GraphTaskUpdate Update, string ETag)> Updates { get; } = [];
        public List<(string Title, DateTimeOffset? Due, DateTimeOffset? Start, int Priority,
            IReadOnlyList<string> LabelIds)> Creations { get; } = [];

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(task);
        public Task<IReadOnlyList<GraphPlanLabel>> GetPlanLabelsAsync(string planId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphPlanLabel>>([
                new GraphPlanLabel("category1", "Blocked"), new GraphPlanLabel("category2", "Release")]);
        Task IPlannerGraphClient.UpdateTaskAsync(string taskId, GraphTaskUpdate update, string etag, CancellationToken ct)
        {
            Updates.Add((taskId, update, etag));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GraphBucket>>([new GraphBucket("bucket", "Bucket", planId)]);
        public Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate,
            IReadOnlyList<string> assigneeIds, DateTimeOffset? startDate, int priority,
            IReadOnlyList<string> labelIds, CancellationToken ct)
        {
            Creations.Add((title, dueDate, startDate, priority, labelIds));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
