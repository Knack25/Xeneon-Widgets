using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class ChecklistServiceTests
{
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

    private static Fixture CreateFixture(params GraphChecklistItem[] items)
    {
        var graph = new FakeGraph(new GraphTaskDetails("details-etag", items));
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set("task", "cached");
        return new Fixture(new ChecklistService(graph, cache), graph, cache);
    }

    private sealed record Fixture(ChecklistService Service, FakeGraph Graph, IMemoryCache Cache);

    private sealed class FakeGraph(GraphTaskDetails details) : IPlannerGraphClient
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
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
