using Microsoft.AspNetCore.Http;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Security;

public sealed class OwnerAuthorizationFilter(LocalAccessService access, MicrosoftAccountState accountState) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var token = context.HttpContext.Request.Headers[LocalAccessHeaders.Owner].ToString();
        var authorization = await access.AuthorizeOwnerSessionAsync(token, context.HttpContext.RequestAborted);
        if (authorization is null) return Results.Unauthorized();

        using var request = accountState.BindOwnerRequest(authorization);
        try { return await next(context); }
        catch (OwnerAuthorizationException) { return Results.Unauthorized(); }
    }
}
