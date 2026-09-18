using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Outlook;

public static class OutlookIntegration
{
    private enum Access { Session, Bootstrap, Setup, Read, Connect }
    private sealed record OutlookAuthorization(Access Access);
    public static IServiceCollection AddOutlookIntegration(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutlookTokenProvider, OutlookTokenProvider>();
        services.AddHttpClient("OutlookGraph", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.TryAddSingleton(sp => new OutlookGraphClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("OutlookGraph"), sp.GetRequiredService<IOutlookTokenProvider>()));
        services.TryAddSingleton<OutlookAccountState>();
        services.TryAddSingleton<OutlookSettingsStore>();
        services.TryAddSingleton<CalendarCatalogService>();
        services.TryAddSingleton<CalendarViewService>();
        services.TryAddSingleton<OutlookPreferencesService>();
        services.TryAddSingleton<EventDetailsService>();
        services.TryAddSingleton<OutlookAccessService>();
        services.TryAddSingleton<OutlookStatusService>();
        services.TryAddSingleton<IOutlookMeetingLauncher, OutlookMeetingLauncher>();
        services.TryAddSingleton<OutlookJoinService>();
        return services;
    }

    public static void MapOutlookIntegration(this IEndpointRouteBuilder app)
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
                if (!OutlookAccessService.IsLocalHost(request)) return Error("forbidden", "A local helper Host is required.", 403);
                if (HttpMethods.IsPost(request.Method) && (!request.HasJsonContentType() || request.ContentLength > 16384))
                    return Error("invalid_request", "Send a bounded JSON request.", 400);
                var role = http.GetEndpoint()?.Metadata.GetMetadata<OutlookAuthorization>()?.Access ?? Access.Setup;
                var access = http.RequestServices.GetRequiredService<OutlookAccessService>();
                var state = http.RequestServices.GetRequiredService<OutlookAccountState>();
                var sameOrigin = OutlookAccessService.IsSameOrigin(request);
                var session = request.Headers["X-Outlook-Session"].ToString();
                var setupOnly = role is Access.Session or Access.Setup or Access.Connect;
                if (setupOnly && !sameOrigin) return Error("forbidden", "Use the same-origin helper setup page.", 403);
                if (role == Access.Session) return await next(context);
                OutlookAccountLease? authorizedLease = null;
                if (setupOnly)
                {
                    authorizedLease = await access.AuthenticateSessionAsync(session, http.RequestAborted);
                    if (authorizedLease is null) return Error("unauthorized", "Refresh the helper setup session.", 401);
                }
                else if (role != Access.Bootstrap)
                {
                    if (sameOrigin) authorizedLease = await access.AuthenticateSessionAsync(session, http.RequestAborted);
                    if (authorizedLease is null)
                    {
                        var origin = request.Headers.Origin.ToString();
                        if (origin.Length > 0 && !sameOrigin && !WidgetOriginPolicy.IsAllowed(origin)) return Error("forbidden", "This origin is not approved for Outlook.", 403);
                        var authorization = request.Headers.Authorization.ToString();
                        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) authorizedLease = await access.AuthenticateCredentialAsync(authorization[7..], http.RequestAborted);
                    }
                    if (authorizedLease is null) return Error("unauthorized", "Pair this widget or refresh the helper session.", 401);
                }
                else
                {
                    var origin = request.Headers.Origin.ToString();
                    if (origin.Length > 0 && !sameOrigin && !WidgetOriginPolicy.IsAllowed(origin)) return Error("forbidden", "This origin is not approved for pairing.", 403);
                }
                if (authorizedLease is null) return await next(context);
                state.RequireCurrent(authorizedLease.Value);
                // Connect is the explicitly authorized account transition; every other
                // authenticated operation must retain its authorizing identity throughout.
                if (role == Access.Connect) return await next(context);
                using var scope = state.BindRequest(authorizedLease.Value);
                var result = await next(context);
                await state.GetIdentityAsync(false, http.RequestAborted);
                state.RequireCurrent(authorizedLease.Value);
                return result is IResult response ? new AuthorizedOutlookResult(response, state, authorizedLease.Value) : Error("unavailable", "Outlook returned an invalid local response.", 503);
            }
            catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
            catch (MsalClientException ex) when (ex.ErrorCode is "authentication_canceled" or "user_canceled") { return Error("connect_cancelled", "Outlook connection was cancelled.", 400); }
            catch (MsalUiRequiredException) { return Error("consent_required", "Connect Outlook with the complete permission bundle. Your organization may require administrator approval.", 401); }
            catch (MsalServiceException) { return Error("consent_required", "Microsoft could not authorize Outlook. Check the application configuration and organizational approval for the complete permission bundle.", 401); }
            catch (MsalException) { return Error("unavailable", "Microsoft sign-in is currently unavailable. Try connecting again.", 503); }
            catch (BadHttpRequestException) { return Error("invalid_request", "The Outlook request is invalid.", 400); }
            catch (ArgumentException) { return Error("invalid_request", "The Outlook request is invalid.", 400); }
            catch (IOException) { return Error("unavailable", "Outlook local settings are unavailable.", 503); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Outlook").LogError("Outlook request failed with {ExceptionType}.", ex.GetType().Name);
                return Error("unavailable", "Outlook is currently unavailable. Check the helper configuration and try again.", 503);
            }
        });

        routes.MapGet("/session", async (OutlookAccessService access, CancellationToken ct) => Results.Ok(new { token = await access.CreateSessionAsync(ct) })).WithMetadata(new OutlookAuthorization(Access.Session));
        routes.MapGet("/status", async (OutlookStatusService status, CancellationToken ct) => Results.Ok(await status.GetAsync(ct)));
        routes.MapPost("/connect", async (IMicrosoftAuthService auth, OutlookAccountState state, OutlookStatusService status, CancellationToken ct) =>
        {
            var response = await state.TransitionAsync(() => auth.ConnectOutlookAsync(ct), ct);
            status.Refresh();
            return Results.Ok(response);
        }).WithMetadata(new OutlookAuthorization(Access.Connect));
        routes.MapGet("/calendars", async (CalendarCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.GetAsync(ct)));
        routes.MapGet("/preferences", async (OutlookPreferencesService preferences, CancellationToken ct) => Results.Ok(await preferences.GetAsync(ct)));
        routes.MapPost("/view", async (ViewRequest request, CalendarViewService views, CancellationToken ct) => Results.Ok(await views.GetAsync(request, ct)));
        routes.MapPost("/event-details", async (EventRequest request, EventDetailsService details, CancellationToken ct) => Results.Ok(await details.GetAsync(request, ct)));
        routes.MapPost("/join", async (EventRequest request, OutlookJoinService join, CancellationToken ct) => { await join.JoinAsync(request, ct); return Results.NoContent(); });
        routes.MapPost("/sources", async (SourceRequest request, CalendarCatalogService catalog, CancellationToken ct) => Results.Ok(await catalog.AddAsync(request.OwnerEmail, ct))).WithMetadata(new OutlookAuthorization(Access.Setup));
        routes.MapPost("/sources/{key}/remove", async (string key, CalendarCatalogService catalog, CancellationToken ct) => { await catalog.RemoveAsync(key, ct); return Results.NoContent(); }).WithMetadata(new OutlookAuthorization(Access.Setup));
        routes.MapPost("/pairings", async (PairingRequest request, OutlookAccessService access, CancellationToken ct) => Results.Ok(await access.CreatePairingAsync(request, ct))).WithMetadata(new OutlookAuthorization(Access.Bootstrap));
        routes.MapPost("/pairings/{id}/poll", async (string id, PairingPollRequest request, OutlookAccessService access, CancellationToken ct) => Results.Ok(await access.PollAsync(id, request.RequestSecret, ct))).WithMetadata(new OutlookAuthorization(Access.Bootstrap));
        routes.MapGet("/pairings", async (OutlookAccessService access, CancellationToken ct) => Results.Ok(await access.GetPendingAsync(ct))).WithMetadata(new OutlookAuthorization(Access.Setup));
        routes.MapPost("/pairings/{id}/approve", async (string id, OutlookAccessService access, CancellationToken ct) => { await access.ApproveAsync(id, ct); return Results.NoContent(); }).WithMetadata(new OutlookAuthorization(Access.Setup));
        routes.MapPost("/pairings/revoke", async (RevokePairingRequest request, OutlookAccessService access, CancellationToken ct) => { await access.RevokeAsync(request.CredentialId, ct); return Results.NoContent(); }).WithMetadata(new OutlookAuthorization(Access.Setup));
        routes.MapGet("/paired", async (OutlookAccessService access, CancellationToken ct) => Results.Ok(await access.GetPairedAsync(ct))).WithMetadata(new OutlookAuthorization(Access.Setup));
    }

    private static IResult Error(string code, string message, int status) => Results.Json(new { error = new OutlookError(code, message) }, statusCode: status);

    private sealed class AuthorizedOutlookResult(IResult inner, OutlookAccountState state, OutlookAccountLease lease) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            try { await state.ExecuteAuthorizedAsync(lease, () => inner.ExecuteAsync(http), http.RequestAborted); }
            catch (OutlookException ex) when (!http.Response.HasStarted)
            {
                await Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode).ExecuteAsync(http);
            }
        }
    }
}
