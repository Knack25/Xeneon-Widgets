using System.Net;
using System.Diagnostics;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseUrls("http://localhost:8787");
builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddSingleton<ILocalJsonStore>(_ => new LocalJsonStore(LocalPaths.AppDataRoot()));
builder.Services.AddSingleton<IPlannerSettingsStore, PlannerSettingsStore>();
builder.Services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
builder.Services.AddSingleton<IGraphTokenProvider>(provider => provider.GetRequiredService<IMicrosoftAuthService>());
builder.Services.AddHttpClient<IPlannerGraphClient, PlannerGraphClient>(client =>
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
builder.Services.AddSingleton<PlannerBoardService>();
builder.Services.AddSingleton<BoardSelectionService>();
builder.Services.AddSingleton<PlannerDisplayService>();
builder.Services.AddSingleton<TaskCompletionService>();
builder.Services.AddSingleton<PlannerCoordinator>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TaskDetailsService>();
builder.Services.AddSingleton<TaskMoveService>();
builder.Services.AddSingleton<ChecklistCompletionService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (app.Configuration.GetValue<bool>("TraceWidgetHealth") && context.Request.Path == "/health")
    {
        app.Logger.LogInformation("Widget health request: method={Method} origin={Origin} preflightMethod={PreflightMethod} privateNetwork={PrivateNetwork} fetchSite={FetchSite}",
            context.Request.Method, origin, context.Request.Headers.AccessControlRequestMethod.ToString(),
            context.Request.Headers["Access-Control-Request-Private-Network"].ToString(),
            context.Request.Headers["Sec-Fetch-Site"].ToString());
    }
    if (!string.IsNullOrEmpty(origin) && !WidgetOriginPolicy.IsAllowed(origin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (WidgetOriginPolicy.IsAllowed(origin))
    {
        context.Response.Headers.AccessControlAllowOrigin = origin;
        context.Response.Headers.Vary = "Origin";
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    }
    if (context.Request.Method == "OPTIONS")
    {
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }
    try
    {
        await next(context);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var (status, code, message) = exception switch
        {
            MsalUiRequiredException => (401, "signed_out", "Sign in to your Microsoft work account."),
            MsalException => (401, "auth_required", "Microsoft sign-in needs attention."),
            GraphApiException { StatusCode: HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict } => (409, "task_conflict", "The task changed. Refresh and try again."),
            GraphApiException { StatusCode: HttpStatusCode.Forbidden } => (403, "permission_denied", "This account cannot access the requested Planner board."),
            GraphApiException { StatusCode: HttpStatusCode.Unauthorized } => (401, "auth_required", "Microsoft sign-in needs attention."),
            GraphApiException { StatusCode: HttpStatusCode.NotFound } => (404, "not_found", "The requested Planner item was not found."),
            GraphApiException { StatusCode: HttpStatusCode.TooManyRequests } => (429, "throttled", "Microsoft Planner is busy. Please try again shortly."),
            GraphApiException => (502, "graph_error", "Microsoft Planner could not complete the request."),
            HttpRequestException => (503, "network_unavailable", "Microsoft Planner is unavailable."),
            InvalidOperationException error when (error.Message.Contains("client ID")) => (503, "not_configured", "Microsoft client ID is not configured."),
            ArgumentException error => (400, "invalid_configuration", error.Message),
            InvalidOperationException => (404, "not_found", "The requested Planner item was not found."),
            _ => (500, "unknown_error", "The local helper encountered an error.")
        };
        app.Logger.LogError(exception, "Helper request failed: {Code}", code);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ApiErrorResponse(code, message));
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = "0.2.3" }));
app.MapGet("/configuration", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.GetConfigurationAsync(ct)));
app.MapPut("/configuration", async (AzureAdOptions configuration, IMicrosoftAuthService auth,
    IPlannerSettingsStore settings, CancellationToken ct) =>
{
    var previous = await auth.GetConfigurationAsync(ct);
    var saved = await auth.SaveConfigurationAsync(configuration, ct);
    if (previous != saved)
    {
        var selection = await settings.LoadSettingsAsync(ct);
        await settings.SaveSettingsAsync(selection with { SelectedPlanId = null, SelectedPlanTitle = null }, ct);
    }
    return Results.Ok(saved);
});
app.MapGet("/auth/status", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.GetStatusAsync(ct)));
app.MapGet("/auth/sign-in", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.SignInAsync(ct)));
app.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.SignInAsync(ct)));
app.MapPost("/auth/enable-assignee-names", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.EnableAssigneeNamesAsync(ct)));
app.MapPost("/auth/sign-out", async (IMicrosoftAuthService auth, CancellationToken ct) =>
{
    await auth.SignOutAsync(ct);
    return Results.NoContent();
});
app.MapGet("/plans", async (PlannerBoardService boards, CancellationToken ct) =>
    Results.Ok(await boards.GetPlansAsync(ct)));
app.MapGet("/settings", async (IPlannerSettingsStore settings, CancellationToken ct) =>
    Results.Ok(await settings.LoadSettingsAsync(ct)));
app.MapPut("/settings", async (SettingsDto dto, IPlannerSettingsStore settings, CancellationToken ct) =>
{
    await settings.SaveSettingsAsync(dto, ct);
    return Results.Ok(dto);
});
app.MapPut("/selected-plan", async (SelectedPlanRequest request, BoardSelectionService selection, CancellationToken ct) =>
    Results.Ok(await selection.SelectAsync(request.PlanId, ct)));
app.MapGet("/display", async (PlannerCoordinator coordinator, CancellationToken ct) =>
{
    var display = await coordinator.GetDisplayAsync(ct);
    return display is null ? Results.NoContent() : Results.Ok(display);
});
app.MapPost("/tasks/{taskId}/complete", async (string taskId, TaskCompletionService completion, CancellationToken ct) =>
    Results.Ok(await completion.CompleteAsync(taskId, ct)));
app.MapGet("/tasks/{taskId}/details", async (string taskId, TaskDetailsService details, CancellationToken ct) =>
    Results.Ok(await details.GetAsync(taskId, ct)));
app.MapPut("/tasks/{taskId}/bucket", async (string taskId, MoveTaskRequest request, TaskMoveService moves, CancellationToken ct) =>
{
    await moves.MoveAsync(taskId, request.BucketId, ct);
    return Results.NoContent();
});
app.MapPost("/tasks/{taskId}/checklist/{itemId}/complete", async (string taskId, string itemId,
    ChecklistCompletionService completion, CancellationToken ct) =>
{
    await completion.CompleteAsync(taskId, itemId, ct);
    return Results.NoContent();
});

await app.StartAsync();
if (OperatingSystem.IsWindows() && !args.Contains("--no-browser"))
{
    try { Process.Start(new ProcessStartInfo("http://localhost:8787") { UseShellExecute = true }); }
    catch (Exception error) { app.Logger.LogWarning(error, "Could not open setup page automatically."); }
}
await app.WaitForShutdownAsync();
