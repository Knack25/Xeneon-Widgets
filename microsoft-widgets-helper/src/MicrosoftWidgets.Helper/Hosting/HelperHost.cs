using System.Diagnostics;
using System.Reflection;

namespace PlannerEdge.Helper.Hosting;

public static class HelperHost
{
    public static string Version => typeof(HelperHost).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    public static string PlannerPackagePath => Path.Combine(AppContext.BaseDirectory, "widgets", "PlannerEdgeWidget.icuewidget");

    public static void OpenSetup() => Process.Start(new ProcessStartInfo("http://localhost:8787") { UseShellExecute = true });

    public static void MapHelperHost(this WebApplication app)
    {
        app.MapGet("/installation", () => Results.Ok(new
        {
            version = Version,
            plannerWidgetAvailable = File.Exists(PlannerPackagePath)
        }));
        app.MapGet("/downloads/planner", () => File.Exists(PlannerPackagePath)
            ? Results.File(PlannerPackagePath, "application/octet-stream", "PlannerEdgeWidget.icuewidget")
            : Results.NotFound(new { message = "The widget package is not included in this build. Download it from the release page." }));
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
