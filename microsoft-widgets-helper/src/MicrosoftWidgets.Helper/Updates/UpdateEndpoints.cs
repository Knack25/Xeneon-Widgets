namespace PlannerEdge.Helper.Updates;

public static class UpdateEndpoints
{
    public static void MapUpdates(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/updates");
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

}

public sealed record UpdateApproval(string Version);
