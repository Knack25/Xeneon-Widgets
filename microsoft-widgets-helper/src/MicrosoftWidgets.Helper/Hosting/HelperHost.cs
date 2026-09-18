using System.Diagnostics;
using System.Reflection;

namespace PlannerEdge.Helper.Hosting;

public static class HelperHost
{
    public const string InstanceMutexName = "Local\\Knack25.MicrosoftWidgetsHelper";
    public static string Version => typeof(HelperHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static string PlannerPackagePath => Path.Combine(AppContext.BaseDirectory, "widgets", "PlannerEdgeWidget.icuewidget");
    public static string OutlookPackagePath => Path.Combine(AppContext.BaseDirectory, "widgets", "OutlookEdgeWidget.icuewidget");

    public static void OpenSetup() => Process.Start(new ProcessStartInfo("http://localhost:8787") { UseShellExecute = true });
    public static void OpenUpdates() => Process.Start(new ProcessStartInfo("http://localhost:8787/#updates") { UseShellExecute = true });

    public static void MapHelperHost(this WebApplication app)
    {
        app.MapGet("/installation", () => Results.Ok(new
        {
            version = Version,
            plannerWidgetAvailable = File.Exists(PlannerPackagePath),
            outlookWidgetAvailable = File.Exists(OutlookPackagePath)
        }));
        app.MapGet("/downloads/planner", () => File.Exists(PlannerPackagePath)
            ? Results.File(PlannerPackagePath, "application/octet-stream", "PlannerEdgeWidget.icuewidget")
            : Results.NotFound(new { message = "The widget package is not included in this build. Download it from the release page." }));
        app.MapGet("/downloads/outlook", () => File.Exists(OutlookPackagePath)
            ? Results.File(OutlookPackagePath, "application/octet-stream", "OutlookEdgeWidget.icuewidget")
            : Results.NotFound(new { message = "The Outlook widget package is not included in this build." }));
        app.MapPost("/host/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted();
        });
    }
}
