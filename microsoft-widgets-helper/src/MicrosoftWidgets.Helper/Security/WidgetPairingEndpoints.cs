using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public static class WidgetPairingEndpoints
{
    public static void MapWidgetPairings(this IEndpointRouteBuilder app)
    {
        var routes = CreateRoutes(app);
        routes.WithMetadata(new WidgetTransportMetadata());
        routes.MapPost("", async (ScopedPairingRequest request, WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            return new AccountBoundResult(Results.Ok(await service.CreateAsync(request.Scope, new(request.InstanceId, request.RequestSecret), lease, ct)), state, lease);
        });
        routes.MapPost("/{id}/poll", async (string id, PairingPollRequest request, WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            return new AccountBoundResult(Results.Ok(await service.PollAsync(id, request.RequestSecret, lease, ct)), state, lease);
        });
    }

    // Called only on the owner's management group, alongside the Outlook compatibility routes.
    public static void MapWidgetPairingManagement(this IEndpointRouteBuilder owner)
    {
        var routes = CreateRoutes(owner);
        routes.MapGet("", async (WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetIdentityAsync(false, ct);
            return new AccountBoundResult(Results.Ok(service.GetPending(lease)), state, lease);
        });
        routes.MapGet("/paired", async (WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetIdentityAsync(false, ct);
            return new AccountBoundResult(Results.Ok(await service.GetPairedAsync(lease, ct)), state, lease);
        });
        routes.MapPost("/{id}/approve", async (string id, WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            await service.ApproveAsync(id, lease, ct);
            return new AccountBoundResult(Results.NoContent(), state, lease);
        });
        routes.MapPost("/revoke", async (RevokePairingRequest request, WidgetPairingService service, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            await service.RevokeAsync(request.CredentialId, lease, ct);
            return new AccountBoundResult(Results.NoContent(), state, lease);
        });
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
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<WidgetTransportMetadata>() is not null)
                WidgetCorsExtensions.AllowNativeResponse(context.HttpContext);
            if (HttpMethods.IsPost(request.Method) && (!request.HasJsonContentType() || request.ContentLength > 16384)) return Results.BadRequest();
            try { return await next(context); }
            catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
            catch (IOException) { return Results.StatusCode(503); }
        });
        return routes;
    }
}
