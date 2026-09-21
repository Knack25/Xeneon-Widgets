using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerIntegrationTests
{
    [Fact]
    public async Task PlannerRoutes_PreserveLegacyUrlsAndExposeNamespacedAliases()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddMemoryCache();
        builder.Services.AddPlannerIntegration();
        await using var app = builder.Build();
        app.MapPlannerIntegration();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet();
        foreach (var path in new[] { "/plans", "/settings", "/display", "/selected-plan", "/members", "/tasks",
            "/tasks/{taskId}/details", "/tasks/{taskId}/complete", "/tasks/{taskId}/bucket",
            "/tasks/{taskId}/due-date", "/tasks/{taskId}/assignments", "/tasks/{taskId}/notes",
            "/tasks/{taskId}/chat", "/tasks/{taskId}/title", "/tasks/{taskId}/progress",
            "/tasks/{taskId}/priority", "/tasks/{taskId}/start-date", "/tasks/{taskId}/labels",
            "/tasks/{taskId}/checklist", "/tasks/{taskId}/checklist/{itemId}",
            "/tasks/{taskId}/checklist/{itemId}/position",
            "/tasks/{taskId}/checklist/{itemId}/complete" })
        {
            Assert.Contains(path, routes);
            Assert.Contains("/api/planner" + path, routes);
        }
    }

    [Fact]
    public async Task PreferenceAndCachedDisplayRoutes_PreserveLegacyUrlsAndNamespacedAliases()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddMemoryCache();
        builder.Services.AddPlannerIntegration();
        await using var app = builder.Build();
        app.MapPlannerIntegration();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => (Path: endpoint.RoutePattern.RawText,
                Methods: endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        AssertRoute(routes, "/display/cached", "GET");
        AssertRoute(routes, "/view-preferences/{planId}", "GET");
        AssertRoute(routes, "/view-preferences/{planId}", "PUT");
        AssertRoute(routes, "/api/planner/display/cached", "GET");
        AssertRoute(routes, "/api/planner/view-preferences/{planId}", "GET");
        AssertRoute(routes, "/api/planner/view-preferences/{planId}", "PUT");
    }

    private static void AssertRoute(IEnumerable<(string? Path, IReadOnlyList<string> Methods)> routes,
        string path, string method)
    {
        Assert.Contains(routes, route => route.Path == path && route.Methods.Contains(method));
    }
}
