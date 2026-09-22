using Microsoft.AspNetCore.Http;

namespace PlannerEdge.Helper.Security;

public sealed class OwnerAuthorizationFilter(LocalAccessService access) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var token = context.HttpContext.Request.Headers[LocalAccessHeaders.Owner].ToString();
        return access.ValidateOwnerSession(token) ? await next(context) : Results.Unauthorized();
    }
}
