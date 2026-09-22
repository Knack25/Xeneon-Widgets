using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public sealed class WidgetAuthorizationFilter(WidgetScope scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        try
        {
            if (!OutlookAccessService.IsLocalHost(http.Request)) return Results.BadRequest();
            if (!IsAllowedOrigin(http.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var state = http.RequestServices.GetRequiredService<OutlookAccountState>();
            OutlookAccountLease? lease = null;
            var owner = http.Request.Headers[LocalAccessHeaders.Owner].ToString();
            if (OutlookAccessService.IsSameOrigin(http.Request) &&
                http.RequestServices.GetRequiredService<LocalAccessService>().ValidateOwnerSession(owner))
                lease = await state.GetAsync(http.RequestAborted);
            if (lease is null)
                lease = await http.RequestServices.GetRequiredService<WidgetPairingService>().AuthenticateAsync(
                    scope, http.Request.Headers[LocalAccessHeaders.Credential].ToString(), http.RequestAborted);
            if (lease is null) return Results.Unauthorized();
            using var binding = state.BindRequest(lease.Value);
            var result = await next(context);
            await state.GetIdentityAsync(false, http.RequestAborted);
            state.RequireCurrent(lease.Value);
            return result is IResult response ? new AccountBoundResult(response, state, lease.Value) : Results.StatusCode(503);
        }
        catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
    }

    internal static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || OutlookAccessService.IsSameOrigin(request) || WidgetOriginPolicy.IsAllowed(origin);
    }
}
