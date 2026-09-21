using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class ChecklistServiceTests
{
    public static TheoryData<string> ForeignPlanWrites => new()
    {
        "add", "rename", "delete", "move"
    };

    [Fact]
    public async Task AddAsync_UsesGuidAndAppendsAfterLastItem()
    {
        var fixture = CreateFixture(new GraphChecklistItem("first", "First", false, "first"));

        await fixture.Service.AddAsync("task", " New item ", CancellationToken.None);

        var write = Assert.Single(fixture.Graph.Writes);
        Assert.True(Guid.TryParse(write.ItemId, out _));
        Assert.Equal("New item", write.Patch!.Title);
        Assert.Equal("first !", write.Patch.OrderHint);
        Assert.Equal("details-etag", write.ETag);
        Assert.False(fixture.Cache.TryGetValue("task", out _));
    }

    [Fact]
    public async Task AddAsync_UsesEmptyListHint()
    {
        var fixture = CreateFixture();

        await fixture.Service.AddAsync("task", "New item", CancellationToken.None);

        Assert.Equal(" !", Assert.Single(fixture.Graph.Writes).Patch!.OrderHint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddAsync_RejectsEmptyTitles(string title)
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.AddAsync("task", title, CancellationToken.None));

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task AddAsync_RejectsTitlesLongerThanOneHundredCharacters()
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.AddAsync("task", new string('x', 101), CancellationToken.None));

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task RenameAsync_TrimsTitleAndUsesCurrentDetailsEtag()
    {
        var fixture = CreateFixture(new GraphChecklistItem("item", "Old", false, "hint"));

        await fixture.Service.RenameAsync("task", "item", " Renamed ", CancellationToken.None);

        var write = Assert.Single(fixture.Graph.Writes);
        Assert.Equal("Renamed", write.Patch!.Title);
        Assert.Null(write.Patch.OrderHint);
        Assert.Equal("details-etag", write.ETag);
    }

    [Fact]
    public async Task RenameAsync_RejectsMissingItemAndInvalidTitle()
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.RenameAsync("task", "missing", "Renamed", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.RenameAsync("task", "missing", new string('x', 101), CancellationToken.None));

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task DeleteAsync_UsesNullPatchAndCurrentDetailsEtag()
    {
        var fixture = CreateFixture(new GraphChecklistItem("item", "Item", false, "hint"));

        await fixture.Service.DeleteAsync("task", "item", CancellationToken.None);

        var write = Assert.Single(fixture.Graph.Writes);
        Assert.Null(write.Patch);
        Assert.Equal("details-etag", write.ETag);
    }

    [Fact]
    public async Task DeleteAsync_RejectsMissingItem()
    {
        var fixture = CreateFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteAsync("task", "missing", CancellationToken.None));

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task MoveAsync_UsesPlannerRelativeHint()
    {
        var fixture = CreateFixture(
            new GraphChecklistItem("first", "First", false, "first"),
            new GraphChecklistItem("second", "Second", false, "second"));

        await fixture.Service.MoveAsync("task", "second", "up", CancellationToken.None);

        Assert.Equal(" first!", Assert.Single(fixture.Graph.Writes).Patch!.OrderHint);
    }

    [Fact]
    public async Task MoveAsync_DownUsesNeighborsAfterReordering()
    {
        var fixture = CreateFixture(
            new GraphChecklistItem("first", "First", false, "first"),
            new GraphChecklistItem("second", "Second", false, "second"),
            new GraphChecklistItem("third", "Third", false, "third"));

        await fixture.Service.MoveAsync("task", "first", "down", CancellationToken.None);

        var write = Assert.Single(fixture.Graph.Writes);
        Assert.Equal("second third!", write.Patch!.OrderHint);
        Assert.Equal("details-etag", write.ETag);
    }

    [Theory]
    [InlineData("up", " a!")]
    [InlineData("down", "z !")]
    public async Task MoveAsync_CanonicalizesAscendingGraphHintsBeforeCalculatingPosition(
        string direction, string expectedHint)
    {
        var fixture = CreateFixture(
            new GraphChecklistItem("last", "Last", false, "z"),
            new GraphChecklistItem("first", "First", false, "a"),
            new GraphChecklistItem("middle", "Middle", false, "m"));

        await fixture.Service.MoveAsync("task", "middle", direction, CancellationToken.None);

        Assert.Equal(expectedHint, Assert.Single(fixture.Graph.Writes).Patch!.OrderHint);
    }

    [Fact]
    public async Task MoveAsync_PreservesSourceOrderForEqualHints()
    {
        var fixture = CreateFixture(
            new GraphChecklistItem("same-first", "Same first", false, "a"),
            new GraphChecklistItem("same-second", "Same second", false, "a"),
            new GraphChecklistItem("last", "Last", false, "z"));

        await fixture.Service.MoveAsync("task", "same-second", "down", CancellationToken.None);

        Assert.Equal("z !", Assert.Single(fixture.Graph.Writes).Patch!.OrderHint);
    }

    [Theory]
    [InlineData("first", "up")]
    [InlineData("second", "down")]
    public async Task MoveAsync_DoesNotWriteAtBoundaries(string itemId, string direction)
    {
        var fixture = CreateFixture(
            new GraphChecklistItem("first", "First", false, "first"),
            new GraphChecklistItem("second", "Second", false, "second"));

        await fixture.Service.MoveAsync("task", itemId, direction, CancellationToken.None);

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task MoveAsync_RejectsMissingItemAndDirection()
    {
        var fixture = CreateFixture(new GraphChecklistItem("item", "Item", false, "hint"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.MoveAsync("task", "missing", "up", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Service.MoveAsync("task", "item", "sideways", CancellationToken.None));

        Assert.Empty(fixture.Graph.Writes);
    }

    [Fact]
    public async Task AddAsync_PropagatesGraphConflictAndKeepsCache()
    {
        var fixture = CreateFixture();
        fixture.Graph.Error = new GraphApiException(HttpStatusCode.Conflict, "conflict");

        var error = await Assert.ThrowsAsync<GraphApiException>(() =>
            fixture.Service.AddAsync("task", "New item", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.True(fixture.Cache.TryGetValue("task", out _));
    }

    [Theory]
    [MemberData(nameof(ForeignPlanWrites))]
    public async Task Writes_RejectTaskOutsideSelectedPlanBeforeGraphMutation(string operation)
    {
        var fixture = CreateFixture("other-plan",
            new GraphChecklistItem("first", "First", false, "a"),
            new GraphChecklistItem("second", "Second", false, "z"));

        await Assert.ThrowsAsync<ArgumentException>(() => operation switch
        {
            "add" => fixture.Service.AddAsync("task", "New item", CancellationToken.None),
            "rename" => fixture.Service.RenameAsync("task", "first", "Renamed", CancellationToken.None),
            "delete" => fixture.Service.DeleteAsync("task", "first", CancellationToken.None),
            "move" => fixture.Service.MoveAsync("task", "first", "down", CancellationToken.None),
            _ => throw new InvalidOperationException()
        });

        Assert.Empty(fixture.Graph.Writes);
    }

    private static Fixture CreateFixture(params GraphChecklistItem[] items) => CreateFixture("plan", items);

    private static Fixture CreateFixture(string taskPlan, params GraphChecklistItem[] items)
    {
        var graph = new FakeGraph(new GraphTask("task", "Task", taskPlan, "bucket", null, 5, 0,
            "task-etag", []), new GraphTaskDetails("details-etag", items));
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("task", "cached");
        var settings = new FakeSettings();
        return new Fixture(new ChecklistService(graph, new SelectedPlanTaskService(graph, settings), cache), graph, cache);
    }

    private sealed record Fixture(ChecklistService Service, FakeGraph Graph, IMemoryCache Cache);

    private sealed class FakeSettings : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) =>
            Task.FromResult(new SettingsDto("plan", "Board", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeGraph(GraphTask task, GraphTaskDetails details) : IPlannerGraphClient
    {
        public List<(string TaskId, string ItemId, GraphChecklistPatch? Patch, string ETag)> Writes { get; } = [];
        public Exception? Error { get; set; }

        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => Task.FromResult(details);

        public Task PatchChecklistAsync(string taskId, string itemId, GraphChecklistPatch? patch, string etag,
            CancellationToken ct)
        {
            if (Error is not null) throw Error;
            Writes.Add((taskId, itemId, patch, etag));
            return Task.CompletedTask;
        }

        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(task);
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
