using System.Diagnostics;
using System.Reflection;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Hosting;

public static class HelperHost
{
    public const string InstanceMutexName = "Local\\Knack25.MicrosoftWidgetsHelper";
    public static string Version => typeof(HelperHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static string PlannerPackagePath => Path.Combine(AppContext.BaseDirectory, "widgets", "PlannerEdgeWidget.icuewidget");
    public static string OutlookPackagePath => Path.Combine(AppContext.BaseDirectory, "widgets", "OutlookEdgeWidget.icuewidget");

    public static void OpenSetup(LocalAccessService access) => Open(CreateSetupUrl(access));
    public static void OpenUpdates(LocalAccessService access) => Open(CreateSetupUrl(access, "updates"));

    public static string CreateSetupUrl(LocalAccessService access, string? section = null)
    {
        var bootstrap = access.CreateBootstrap();
        var fragment = $"access={Uri.EscapeDataString(bootstrap.Token)}";
        if (!string.IsNullOrWhiteSpace(section)) fragment += $"&section={Uri.EscapeDataString(section)}";
        return "http://localhost:8787/#" + fragment;
    }

    public static void MapHelperHostManagement(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/installation", () => Results.Ok(new
        {
            version = Version,
            plannerWidgetAvailable = File.Exists(PlannerPackagePath),
            outlookWidgetAvailable = File.Exists(OutlookPackagePath)
        }));
        routes.MapGet("/downloads/planner", () => File.Exists(PlannerPackagePath)
            ? Results.File(PlannerPackagePath, "application/octet-stream", "PlannerEdgeWidget.icuewidget")
            : Results.NotFound(new { message = "The widget package is not included in this build. Download it from the release page." }));
        routes.MapGet("/downloads/outlook", () => File.Exists(OutlookPackagePath)
            ? Results.File(OutlookPackagePath, "application/octet-stream", "OutlookEdgeWidget.icuewidget")
            : Results.NotFound(new { message = "The Outlook widget package is not included in this build." }));
        routes.MapPost("/host/stop", async (HttpContext context, IHostApplicationLifetime lifetime,
            MicrosoftAccountState state, CancellationToken ct) =>
            await state.ExecuteOwnerAuthorizedAsync(() =>
            {
                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Task.FromResult(Results.Accepted());
            }, ct));
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
