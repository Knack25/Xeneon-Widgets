using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace PlannerEdge.Helper.Security;

public sealed class SecurityHeadersMiddleware(RequestDelegate next, int expectedPort)
{
    private static readonly PathString[] LegacyPlannerPaths =
    [
        "/plans",
        "/settings",
        "/selected-plan",
        "/display",
        "/view-preferences",
        "/members",
        "/tasks"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!LoopbackRequestPolicy.IsAllowed(context, expectedPort))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return;
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (IsSensitivePath(context.Request.Path)) context.Response.Headers.CacheControl = "no-store";
        if (IsSetupPath(context.Request.Path))
        {
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
            context.Response.Headers["X-Frame-Options"] = "DENY";
        }

        await next(context);
    }

    private static bool IsSensitivePath(PathString path) =>
        IsSetupPath(path) ||
        path.StartsWithSegments("/api") ||
        path.StartsWithSegments("/auth") ||
        path.StartsWithSegments("/configuration") ||
        path.StartsWithSegments("/downloads") ||
        path.StartsWithSegments("/host") ||
        path.StartsWithSegments("/updates") ||
        LegacyPlannerPaths.Any(path.StartsWithSegments);

    private static bool IsSetupPath(PathString path) =>
        string.Equals(path.Value, "/", StringComparison.Ordinal) ||
        string.Equals(path.Value, "/index.html", StringComparison.OrdinalIgnoreCase);
}

public static class HelperSecurityBoundaryExtensions
{
    public static void UseHelperSecurityBoundary(this WebApplication app)
    {
        var expectedPort = app.Configuration.GetValue<int>("HelperPort", 8787);
        app.UseMiddleware<SecurityHeadersMiddleware>(expectedPort);
    }
}
