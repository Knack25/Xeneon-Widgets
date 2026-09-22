using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerHttpIntegrationTests
{
    public static TheoryData<string, string> LegacyPlannerRoutes => new()
    {
        { "GET", "/plans" },
        { "GET", "/settings" },
        { "PUT", "/settings" },
        { "PUT", "/selected-plan" },
        { "GET", "/display" },
        { "GET", "/display/cached" },
        { "GET", "/view-preferences/plan" },
        { "PUT", "/view-preferences/plan" },
        { "GET", "/members" },
        { "POST", "/tasks" },
        { "POST", "/tasks/task/complete" },
        { "GET", "/tasks/task/details" },
        { "PUT", "/tasks/task/notes" },
        { "GET", "/tasks/task/chat" },
        { "POST", "/tasks/task/chat" },
        { "PUT", "/tasks/task/bucket" },
        { "PUT", "/tasks/task/due-date" },
        { "PUT", "/tasks/task/title" },
        { "PUT", "/tasks/task/progress" },
        { "PUT", "/tasks/task/priority" },
        { "PUT", "/tasks/task/start-date" },
        { "PUT", "/tasks/task/labels" },
        { "PUT", "/tasks/task/assignments" },
        { "POST", "/tasks/task/checklist" },
        { "PUT", "/tasks/task/checklist/item" },
        { "DELETE", "/tasks/task/checklist/item" },
        { "PUT", "/tasks/task/checklist/item/position" },
        { "POST", "/tasks/task/checklist/item/complete" }
    };

    [Fact]
    public async Task Attacker_host_cannot_read_static_health_or_integration_routes()
    {
        await using var host = await BoundaryTestHost.StartAsync();

        foreach (var path in new[] { "/", "/health", "/plans", "/api/planner/plans", "/api/outlook/session" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = "attacker.invalid:" + host.Port;
            using var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Security_boundary_applies_sensitive_headers_without_blocking_widget_previews()
    {
        await using var host = await BoundaryTestHost.StartAsync();

        using var api = await host.Client.GetAsync("/api/planner/plans");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
        AssertHeader(api, "Cache-Control", "no-store");
        AssertHeader(api, "X-Content-Type-Options", "nosniff");

        using var account = await host.Client.GetAsync("/auth/status");
        Assert.Equal(HttpStatusCode.OK, account.StatusCode);
        AssertHeader(account, "Cache-Control", "no-store");
        AssertHeader(account, "X-Content-Type-Options", "nosniff");

        using var setup = await host.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        AssertHeader(setup, "Cache-Control", "no-store");
        AssertHeader(setup, "X-Content-Type-Options", "nosniff");
        AssertHeader(setup, "Content-Security-Policy", "frame-ancestors 'none'");
        AssertHeader(setup, "X-Frame-Options", "DENY");
        var policy = Assert.Single(setup.Headers.GetValues("Content-Security-Policy"));
        foreach (var directiveName in new[] { "script-src", "style-src" })
        {
            var directive = Assert.Single(policy.Split(';'), value => value.TrimStart().StartsWith(directiveName, StringComparison.Ordinal));
            Assert.DoesNotContain("'unsafe-inline'", directive, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/board/index.html", "/outlook/index.html" })
        {
            using var preview = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.False(preview.Headers.Contains("Content-Security-Policy"));
            Assert.False(preview.Headers.Contains("X-Frame-Options"));
        }
    }

    [Theory]
    [MemberData(nameof(LegacyPlannerRoutes))]
    public async Task Legacy_planner_route_responses_are_not_cached(string method, string path)
    {
        await using var host = await BoundaryTestHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertHeader(response, "Cache-Control", "no-store");
    }

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
        var origin = client.BaseAddress.GetLeftPart(UriPartial.Authority);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/tasks/task/checklist/item");
        preflight.Headers.Add("Origin", origin);
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");

        using var preflightResponse = await client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Contains("DELETE", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods")
            .Single().Split(',').Select(value => value.Trim()));

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/tasks/task/checklist/item");
        delete.Headers.Add("Origin", origin);
        using var deleteResponse = await client.SendAsync(delete);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(("task", "item", "details-etag"), Assert.Single(graph.Deletes));
        await app.StopAsync();
    }

    private static void AssertHeader(HttpResponseMessage response, string name, string value)
    {
        Assert.True(response.Headers.TryGetValues(name, out var values));
        Assert.Contains(values, candidate => candidate.Contains(value, StringComparison.OrdinalIgnoreCase));
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

internal sealed class BoundaryTestHost(WebApplication app, HttpClient client, string staticRoot) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public int Port => Client.BaseAddress!.Port;

    public static async Task<BoundaryTestHost> StartAsync()
    {
        var port = ReservePort();
        var staticRoot = Path.Combine(Path.GetTempPath(), "MicrosoftWidgets.Helper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staticRoot, "board"));
        Directory.CreateDirectory(Path.Combine(staticRoot, "outlook"));
        await File.WriteAllTextAsync(Path.Combine(staticRoot, "index.html"), "setup-secret");
        await File.WriteAllTextAsync(Path.Combine(staticRoot, "board", "index.html"), "planner-preview");
        await File.WriteAllTextAsync(Path.Combine(staticRoot, "outlook", "index.html"), "outlook-preview");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["HelperPort"] = port.ToString() });
        builder.Configuration["AllowedHosts"] = "*";
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        var app = builder.Build();
        var files = new PhysicalFileProvider(staticRoot);

        app.UseHelperSecurityBoundary();
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        app.MapGet("/health", () => Results.Text("health-secret"));
        app.MapGet("/plans", () => Results.Text("legacy-planner-secret"));
        app.MapGet("/api/planner/plans", () => Results.Text("planner-secret"));
        app.MapGet("/api/outlook/session", () => Results.Text("outlook-secret"));
        app.MapGet("/auth/status", () => Results.Text("account-secret"));
        app.MapFallback(() => Results.Text("legacy-route-secret"));
        await app.StartAsync();

        return new(app, new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/") }, staticRoot);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Directory.Delete(staticRoot, recursive: true);
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
