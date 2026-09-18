namespace PlannerEdge.Helper.Updates;

public static class UpdateEndpoints
{
    public static void MapUpdates(this WebApplication app)
    {
        var group = app.MapGroup("/updates");
        group.AddEndpointFilter(async (context, next) => IsSetupRequest(context.HttpContext.Request)
            ? await next(context) : Results.StatusCode(403));
        group.MapGet("", (UpdateService service) => Results.Ok(service.Status));
        group.MapPost("/check", async (UpdateService service, CancellationToken ct) =>
        {
            await service.CheckAsync(ct);
            return Results.Ok(service.Status);
        });
        group.MapPost("/install", async (UpdateApproval approval, UpdateService service, CancellationToken ct) =>
        {
            try { await service.InstallAsync(approval.Version, ct); return Results.Ok(service.Status); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { message = error.Message }); }
        });
        group.MapGet("/result", async () => File.Exists(UpdateInstaller.ResultPath)
            ? Results.Text(await File.ReadAllTextAsync(UpdateInstaller.ResultPath), "application/json")
            : Results.Ok(new { message = "" }));
    }

    public static bool IsSetupRequest(HttpRequest request) =>
        request.Host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && request.Host.Port == 8787 &&
        request.Headers["X-Microsoft-Widgets-Update"] == "1" &&
        (request.Headers.Origin.Count == 0 || request.Headers.Origin == "http://localhost:8787") &&
        request.Headers["Sec-Fetch-Site"] != "cross-site";
}

public sealed record UpdateApproval(string Version);
