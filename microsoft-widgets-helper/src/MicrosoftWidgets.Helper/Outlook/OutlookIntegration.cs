using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Outlook;

public static class OutlookIntegration
{
    private enum Access { Bootstrap, Read, Owner }
    private sealed record OutlookAuthorization(Access Access);
    public static IServiceCollection AddOutlookIntegration(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutlookTokenProvider, OutlookTokenProvider>();
        services.TryAddSingleton<IMicrosoftAccountIdentityProvider>(sp => sp.GetRequiredService<IOutlookTokenProvider>());
        services.AddHttpClient("OutlookGraph", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton(sp => new OutlookGraphClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("OutlookGraph"), sp.GetRequiredService<IOutlookTokenProvider>()));
        services.TryAddSingleton<MicrosoftAccountState>();
        services.TryAddSingleton<OutlookSettingsStore>();
        services.TryAddSingleton<CalendarCatalogService>();
        services.TryAddSingleton<CalendarViewService>();
        services.TryAddSingleton<OutlookPreferencesService>();
        services.TryAddSingleton<EventDetailsService>();
        services.TryAddSingleton<WidgetPairingService>();
        services.TryAddSingleton<OutlookAccessService>();
        services.TryAddSingleton<OutlookStatusService>();
        services.TryAddSingleton<IOutlookMeetingLauncher, OutlookMeetingLauncher>();
        services.TryAddSingleton<OutlookJoinService>();
        return services;
    }

    public static void MapOutlookIntegration(this IEndpointRouteBuilder app)
    {
        var routes = CreateRoutes(app);
        routes.WithMetadata(new WidgetTransportMetadata());
        routes.MapGet("/calendars", async (CalendarCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.GetAsync(ct)));
        routes.MapGet("/preferences", async (OutlookPreferencesService preferences, CancellationToken ct) => Results.Ok(await preferences.GetAsync(ct)));
        routes.MapPost("/view", async (ViewRequest request, CalendarViewService views, CancellationToken ct) => Results.Ok(await views.GetAsync(request, ct)));
        routes.MapPost("/view/cached", async (ViewRequest request, CalendarViewService views, CancellationToken ct) => Results.Ok(await views.GetCachedAsync(request, ct)));
        routes.MapPost("/event-details", async (EventRequest request, EventDetailsService details, CancellationToken ct) => Results.Ok(await details.GetAsync(request, ct)));
        routes.MapPost("/join", async (EventRequest request, OutlookJoinService join, CancellationToken ct) => { await join.JoinAsync(request, ct); return Results.NoContent(); });
        routes.MapPost("/pairings", async (PairingRequest request, WidgetPairingService pairing, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            return new AccountBoundResult(Results.Ok(await pairing.CreateAsync(WidgetScope.Outlook, request, lease, ct)), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Bootstrap));
        routes.MapPost("/pairings/{id}/poll", async (string id, PairingPollRequest request, OutlookAccessService access, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            return new AccountBoundResult(Results.Ok(await access.PollAsync(id, request.RequestSecret, lease, ct)), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Bootstrap));
    }

    public static void MapOutlookManagement(this IEndpointRouteBuilder app)
    {
        var routes = CreateRoutes(app);
        routes.MapGet("/session", (HttpContext http) => Results.Ok(new OwnerSessionResponse(http.Request.Headers[LocalAccessHeaders.Owner].ToString()))).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapGet("/status", async (OutlookStatusService status, CancellationToken ct) => Results.Ok(await status.GetAsync(ct))).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapPost("/connect", async (IMicrosoftAuthService auth, MicrosoftAccountState state, OutlookStatusService status, CancellationToken ct) =>
        {
            var response = await state.TransitionAsync(() => auth.ConnectOutlookAsync(ct), ct);
            status.Refresh();
            return Results.Ok(response);
        }).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapPost("/sources", async (SourceRequest request, CalendarCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.AddAsync(request.OwnerEmail, ct))).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapPost("/sources/{key}/remove", async (string key, CalendarCatalogService catalog, CancellationToken ct) => { await catalog.RemoveAsync(key, ct); return Results.NoContent(); }).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapGet("/pairings", async (OutlookAccessService access, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetIdentityAsync(false, ct);
            return new AccountBoundResult(Results.Ok(access.GetPending(lease)), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapPost("/pairings/{id}/approve", async (string id, OutlookAccessService access, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            await access.ApproveAsync(id, lease, ct);
            return new AccountBoundResult(Results.NoContent(), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapPost("/pairings/revoke", async (RevokePairingRequest request, OutlookAccessService access, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetAsync(ct);
            await access.RevokeAsync(request.CredentialId, lease, ct);
            return new AccountBoundResult(Results.NoContent(), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Owner));
        routes.MapGet("/paired", async (OutlookAccessService access, MicrosoftAccountState state, CancellationToken ct) =>
        {
            var lease = await state.GetIdentityAsync(false, ct);
            return new AccountBoundResult(Results.Ok(await access.GetPairedAsync(lease, ct)), state, lease);
        }).WithMetadata(new OutlookAuthorization(Access.Owner));
    }

    private static RouteGroupBuilder CreateRoutes(IEndpointRouteBuilder app)
    {
        var routes = app.MapGroup("/api/outlook");
        routes.WithMetadata(new OutlookAuthorization(Access.Read));
        routes.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var request = http.Request;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            try
            {
                if (!OutlookAccessService.IsLocalHost(request)) return Error("forbidden", "A local helper Host is required.", 400);
                if (HttpMethods.IsPost(request.Method) && (!request.HasJsonContentType() || request.ContentLength > 16384))
                    return Error("invalid_request", "Send a bounded JSON request.", 400);
                if (!WidgetAuthorizationFilter.IsAllowedOrigin(request)) return Error("forbidden", "This origin is not approved.", 403);
                var role = http.GetEndpoint()?.Metadata.GetMetadata<OutlookAuthorization>()?.Access ?? Access.Read;
                if (role is Access.Owner) return await next(context);
                if (role is Access.Bootstrap)
                {
                    WidgetCorsExtensions.AllowNativeResponse(http);
                    return await next(context);
                }
                return await new WidgetAuthorizationFilter(WidgetScope.Outlook).InvokeAsync(context, next);
            }
            catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
            catch (MsalClientException ex) when (ex.ErrorCode is "authentication_canceled" or "user_canceled") { return Error("connect_cancelled", "Outlook connection was cancelled.", 400); }
            catch (MsalUiRequiredException) { return Error("consent_required", "Connect Outlook with the complete permission bundle. Your organization may require administrator approval.", 401); }
            catch (MsalServiceException) { return Error("consent_required", "Microsoft could not authorize Outlook. Check the application configuration and organizational approval for the complete permission bundle.", 401); }
            catch (MsalException) { return Error("unavailable", "Microsoft sign-in is currently unavailable. Try connecting again.", 503); }
            catch (BadHttpRequestException) { return Error("invalid_request", "The Outlook request is invalid.", 400); }
            catch (ArgumentException) { return Error("invalid_request", "The Outlook request is invalid.", 400); }
            catch (IOException) { return Error("unavailable", "Outlook local settings are unavailable.", 503); }
            catch (OwnerAuthorizationException) { return Results.Unauthorized(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Outlook").LogError("Outlook request failed with {ExceptionType}.", ex.GetType().Name);
                return Error("unavailable", "Outlook is currently unavailable. Check the helper configuration and try again.", 503);
            }
        });

        return routes;
    }

    private static IResult Error(string code, string message, int status) => Results.Json(new { error = new OutlookError(code, message) }, statusCode: status);

}
