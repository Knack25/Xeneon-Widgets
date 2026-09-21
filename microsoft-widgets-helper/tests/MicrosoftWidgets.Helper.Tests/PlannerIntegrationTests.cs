using Microsoft.AspNetCore.Builder;
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
            "/tasks/{taskId}/checklist/{itemId}/complete" })
        {
            Assert.Contains(path, routes);
            Assert.Contains("/api/planner" + path, routes);
        }
    }
}
