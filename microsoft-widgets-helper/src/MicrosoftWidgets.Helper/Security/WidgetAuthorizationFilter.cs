using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;

namespace PlannerEdge.Helper.Security;

public sealed class WidgetAuthorizationFilter(WidgetScope scope) : IEndpointFilter
{
    internal static readonly object PreauthorizedLeaseKey = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        try
        {
            var state = http.RequestServices.GetRequiredService<MicrosoftAccountState>();
            var preauthorized = http.Items.TryGetValue(PreauthorizedLeaseKey, out var value) && value is AccountLease;
            AccountLease? lease = value is AccountLease existing ? existing : null;
            if (lease is null)
            {
                var authorization = await AuthorizeAsync(http, scope);
                if (authorization.Failure is not null) return authorization.Failure;
                lease = authorization.Lease;
            }
            WidgetCorsExtensions.AllowNativeResponse(http);
            using var binding = preauthorized ? null : state.BindRequest(lease!.Value);
            var result = await next(context);
            await state.GetIdentityAsync(false, http.RequestAborted);
            state.RequireCurrent(lease!.Value);
            return result is IResult response ? new AccountBoundResult(response, state, lease.Value) : Results.StatusCode(503);
        }
        catch (OutlookException ex) { return Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode); }
    }

    internal static async Task<WidgetAuthorizationDecision> AuthorizeAsync(HttpContext http, WidgetScope scope)
    {
        if (!OutlookAccessService.IsLocalHost(http.Request)) return new(null, Results.BadRequest());
        if (!IsAllowedOrigin(http.Request)) return new(null, Results.StatusCode(StatusCodes.Status403Forbidden));
        var state = http.RequestServices.GetRequiredService<MicrosoftAccountState>();
        AccountLease? lease = null;
        var owner = http.Request.Headers[LocalAccessHeaders.Owner].ToString();
        if (OutlookAccessService.IsSameOrigin(http.Request) &&
            await http.RequestServices.GetRequiredService<LocalAccessService>()
                .ValidateOwnerSessionAsync(owner, http.RequestAborted))
            lease = await state.GetAsync(http.RequestAborted);
        if (lease is null)
            lease = await http.RequestServices.GetRequiredService<WidgetPairingService>().AuthenticateAsync(
                scope, http.Request.Headers[LocalAccessHeaders.Credential].ToString(), http.RequestAborted);
        return lease is null ? new(null, Results.Unauthorized()) : new(lease, null);
    }

    internal static bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || OutlookAccessService.IsSameOrigin(request) || WidgetOriginPolicy.IsAllowed(origin);
    }

    internal sealed record WidgetAuthorizationDecision(AccountLease? Lease, IResult? Failure);
}
