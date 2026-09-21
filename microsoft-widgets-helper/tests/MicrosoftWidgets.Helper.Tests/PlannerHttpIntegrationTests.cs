using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerHttpIntegrationTests
{
    [Fact]
    public async Task CorsPreflightAllowsDeleteAndDeleteRouteExecutes()
    {
        var graph = new FakeGraph();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMemoryCache();
        builder.Services.AddPlannerIntegration();
        builder.Services.AddSingleton<IPlannerGraphClient>(graph);
        builder.Services.AddSingleton<IPlannerSettingsStore, FakeSettings>();
        await using var app = builder.Build();
        app.UseWidgetCors();
        app.MapPlannerIntegration();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/tasks/task/checklist/item");
        preflight.Headers.Add("Origin", "http://localhost:8787");
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");

        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Contains("DELETE", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods")
            .Single().Split(',').Select(value => value.Trim()));

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/tasks/task/checklist/item");
        delete.Headers.Add("Origin", "http://localhost:8787");
        using var deleteResponse = await client.SendAsync(delete);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(("task", "item", "details-etag"), Assert.Single(graph.Deletes));
        await app.StopAsync();
    }

    private sealed class FakeSettings : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) =>
            Task.FromResult(new SettingsDto("plan", "Board", true));
        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeGraph : IPlannerGraphClient
    {
        public List<(string TaskId, string ItemId, string ETag)> Deletes { get; } = [];
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(
            new GraphTask(taskId, "Task", "plan", "bucket", null, 5, 0, "task-etag", []));
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => Task.FromResult(
            new GraphTaskDetails("details-etag", [new GraphChecklistItem("item", "Item", false, "a")]));
        public Task PatchChecklistAsync(string taskId, string itemId, GraphChecklistPatch? patch, string etag,
            CancellationToken ct)
        {
            if (patch is null) Deletes.Add((taskId, itemId, etag));
            return Task.CompletedTask;
        }
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
