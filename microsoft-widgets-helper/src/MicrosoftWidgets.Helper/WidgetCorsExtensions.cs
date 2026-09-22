namespace PlannerEdge.Helper;

public static class WidgetCorsExtensions
{
    public static IApplicationBuilder UseWidgetCors(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            var sameOrigin = Outlook.OutlookAccessService.IsSameOrigin(context.Request);
            if (!string.IsNullOrEmpty(origin) && !sameOrigin && !WidgetOriginPolicy.IsAllowed(origin))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (origin.Length > 0 && (sameOrigin || WidgetOriginPolicy.IsAllowed(origin)))
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, X-Microsoft-Widgets-Credential, X-Microsoft-Widgets-Owner, X-Microsoft-Widgets-Bootstrap";
            }
            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await next(context);
        });
    }
}
