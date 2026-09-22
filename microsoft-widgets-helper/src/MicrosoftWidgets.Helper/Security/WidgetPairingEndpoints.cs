using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public static class WidgetPairingEndpoints
{
    public static void MapWidgetPairings(this IEndpointRouteBuilder app)
    {
        var routes = CreateRoutes(app);
        routes.MapPost("", async (ScopedPairingRequest request, WidgetPairingService service, OutlookAccountState state, CancellationToken ct) =>
            Results.Ok(await service.CreateAsync(request.Scope, new(request.InstanceId, request.RequestSecret), await state.GetAsync(ct), ct)));
        routes.MapPost("/{id}/poll", async (string id, PairingPollRequest request, WidgetPairingService service, CancellationToken ct) =>
            Results.Ok(await service.PollAsync(id, request.RequestSecret, ct)));
    }

    // Called only on the owner's management group, alongside the Outlook compatibility routes.
    public static void MapWidgetPairingManagement(this IEndpointRouteBuilder owner)
    {
        var routes = CreateRoutes(owner);
        routes.MapGet("", async (WidgetPairingService service, CancellationToken ct) => Results.Ok(await service.GetPendingAsync(ct)));
        routes.MapGet("/paired", async (WidgetPairingService service, CancellationToken ct) => Results.Ok(await service.GetPairedAsync(ct)));
        routes.MapPost("/{id}/approve", async (string id, WidgetPairingService service, CancellationToken ct) =>
        { await service.ApproveAsync(id, ct); return Results.NoContent(); });
        routes.MapPost("/revoke", async (RevokePairingRequest request, WidgetPairingService service, CancellationToken ct) =>
        { await service.RevokeAsync(request.CredentialId, ct); return Results.NoContent(); });
    }

    private static RouteGroupBuilder CreateRoutes(IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/local-access/pairings");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!OutlookAccessService.IsLocalHost(request)) return Results.BadRequest();
            if (!WidgetAuthorizationFilter.IsAllowedOrigin(request)) return Results.StatusCode(403);
            if (HttpMethods.IsPost(request.Method) && (!request.HasJsonContentType() || request.ContentLength > 16384)) return Results.BadRequest();
            try { return await next(context); }
            catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
            catch (IOException) { return Results.StatusCode(503); }
        });
        return routes;
    }
}
