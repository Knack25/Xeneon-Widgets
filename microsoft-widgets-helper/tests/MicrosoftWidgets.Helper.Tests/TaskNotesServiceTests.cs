using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class TaskNotesServiceTests
{
    [Fact]
    public async Task UpdateAsync_ReloadsDetailsUpdatesOnceAndInvalidatesCachedDetails()
    {
        var graph = new FakeGraph();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var details = new TaskDetailsService(graph, cache);
        await details.GetAsync("task", CancellationToken.None);
        var service = new TaskNotesService(graph, details);

        await service.UpdateAsync("task", "Line one\nLine two", CancellationToken.None);
        await details.GetAsync("task", CancellationToken.None);

        Assert.Equal(3, graph.DetailReads);
        Assert.Equal(("task", "Line one\nLine two", "W/\"details-2\""), Assert.Single(graph.Updates));
    }

    [Fact]
    public async Task UpdateAsync_AllowsEmptyDescription()
    {
        var graph = new FakeGraph();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        await new TaskNotesService(graph, new TaskDetailsService(graph, cache))
            .UpdateAsync("task", "", CancellationToken.None);

        Assert.Equal("", Assert.Single(graph.Updates).Description);
    }

    [Fact]
    public async Task UpdateAsync_RejectsNullDescriptionAsValidationError()
    {
        var graph = new FakeGraph();
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new TaskNotesService(graph, new TaskDetailsService(graph, cache))
                .UpdateAsync("task", null!, CancellationToken.None));

        Assert.Contains("notes", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, graph.DetailReads);
        Assert.Empty(graph.Updates);
    }

    [Theory]
    [InlineData(4000, false)]
    [InlineData(4001, true)]
    public async Task UpdateAsync_EnforcesDescriptionLengthLimit(int length, bool shouldReject)
    {
        var graph = new FakeGraph();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TaskNotesService(graph, new TaskDetailsService(graph, cache));

        if (shouldReject)
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync("task", new string('x', length), CancellationToken.None));
        else
            await service.UpdateAsync("task", new string('x', length), CancellationToken.None);

        Assert.Equal(shouldReject ? 0 : 1, graph.Updates.Count);
        Assert.Equal(shouldReject ? 0 : 1, graph.DetailReads);
    }

    [Fact]
    public async Task UpdateAsync_WhenNonConflictGraphUpdateFails_KeepsCachedDetails()
    {
        var graph = new FakeGraph { FailUpdate = true };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var details = new TaskDetailsService(graph, cache);
        var cached = await details.GetAsync("task", CancellationToken.None);
        var service = new TaskNotesService(graph, details);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.UpdateAsync("task", "Updated notes", CancellationToken.None));
        var afterFailure = await details.GetAsync("task", CancellationToken.None);

        Assert.Same(cached, afterFailure);
        Assert.Equal(2, graph.DetailReads);
        Assert.Empty(graph.Updates);
    }

    [Fact]
    public async Task UpdateAsync_WhenGraphUpdateConflicts_InvalidatesCachedDetails()
    {
        var graph = new FakeGraph { UpdateException = new GraphApiException(HttpStatusCode.PreconditionFailed, "stale") };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var details = new TaskDetailsService(graph, cache);
        await details.GetAsync("task", CancellationToken.None);
        var service = new TaskNotesService(graph, details);

        await Assert.ThrowsAsync<GraphApiException>(() =>
            service.UpdateAsync("task", "Updated notes", CancellationToken.None));
        await details.GetAsync("task", CancellationToken.None);

        Assert.Equal(3, graph.DetailReads);
        Assert.Empty(graph.Updates);
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public int DetailReads { get; private set; }
        public bool FailUpdate { get; init; }
        public Exception? UpdateException { get; init; }
        public List<(string TaskId, string Description, string ETag)> Updates { get; } = [];

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) =>
            Task.FromResult<GraphTask?>(new GraphTask(taskId, "Task", "plan", "bucket", null, null, 0, "etag", []));

        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct)
        {
            DetailReads++;
            return Task.FromResult(new GraphTaskDetails($"W/\"details-{DetailReads}\"", [], "Cached notes"));
        }

        public Task UpdateTaskDescriptionAsync(string taskId, string description, string etag, CancellationToken ct)
        {
            if (UpdateException is not null) throw UpdateException;
            if (FailUpdate)
                throw new HttpRequestException("Update failed");
            Updates.Add((taskId, description, etag));
            return Task.CompletedTask;
        }

        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
