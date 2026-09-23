using Microsoft.AspNetCore.Routing.Template;

namespace PlannerEdge.Helper;

public sealed record WidgetTransportMetadata;

public static class WidgetCorsExtensions
{
    private static readonly object AuthorizedNativeResponse = new();
    private const string Methods = "GET, POST, PUT, DELETE, OPTIONS";
    private const string Headers = "Content-Type, X-Microsoft-Widgets-Credential";

    public static void AllowNativeResponse(HttpContext context)
    {
        if (WidgetOriginPolicy.IsAllowed(context.Request.Headers.Origin.ToString()))
            context.Items[AuthorizedNativeResponse] = true;
    }

    public static IApplicationBuilder UseWidgetCors(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            var sameOrigin = Outlook.OutlookAccessService.IsSameOrigin(context.Request);
            var native = WidgetOriginPolicy.IsAllowed(origin);
            if (origin.Length > 0 && !sameOrigin && !native)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                var method = context.Request.Headers.AccessControlRequestMethod.ToString();
                var requestedHeaders = context.Request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var routeAllowed = context.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Any(endpoint =>
                    endpoint.Metadata.GetMetadata<WidgetTransportMetadata>() is not null &&
                    (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false) &&
                    new TemplateMatcher(new RouteTemplate(endpoint.RoutePattern), new RouteValueDictionary()).TryMatch(context.Request.Path, new RouteValueDictionary()));
                if ((!native && !sameOrigin) || !routeAllowed || requestedHeaders.Any(header => !Headers.Split(", ").Contains(header, StringComparer.OrdinalIgnoreCase)))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlAllowMethods = Methods;
                context.Response.Headers.AccessControlAllowHeaders = Headers;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            context.Response.OnStarting(() =>
            {
                if (native && context.Items.ContainsKey(AuthorizedNativeResponse))
                {
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.Vary = "Origin";
                }
                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
