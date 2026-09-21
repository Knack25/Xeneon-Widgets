using System.Net;
using System.Diagnostics;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Updates;
using PlannerEdge.Helper.Outlook;
using Microsoft.AspNetCore.Http.Features;

if (args.FirstOrDefault() == "--apply-update")
{
    await UpdateInstaller.ApplyAsync(args);
    return;
}

if (args.Contains("--stop"))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { await client.PostAsync("http://localhost:8787/host/stop", null); }
    catch (HttpRequestException) { }
    catch (TaskCanceledException) { }
    return;
}

using var instance = new Mutex(false, HelperHost.InstanceMutexName, out var firstInstance);
if (!firstInstance)
{
    if (!args.Contains("--no-browser")) HelperHost.OpenSetup();
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseUrls("http://localhost:8787");
builder.Services.Configure<AzureAdOptions>(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddSingleton<ILocalJsonStore>(_ => new LocalJsonStore(LocalPaths.AppDataRoot()));
builder.Services.AddSingleton<IMicrosoftAuthService, MicrosoftAuthService>();
builder.Services.AddSingleton<MicrosoftAuthCapabilityService>();
builder.Services.AddSingleton<IGraphTokenProvider>(provider => provider.GetRequiredService<IMicrosoftAuthService>());
builder.Services.AddMemoryCache();
builder.Services.AddPlannerIntegration();
builder.Services.AddOutlookIntegration();
builder.Services.AddHttpClient<IReleaseClient, ReleaseClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("MicrosoftWidgetsHelper/" + HelperHost.Version);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IUpdateInstaller, UpdateInstaller>();
builder.Services.AddSingleton(provider => new UpdateService(provider.GetRequiredService<IReleaseClient>(),
    provider.GetRequiredService<IUpdateInstaller>(), UpdateService.ReleaseVersion));
builder.Services.AddHostedService<UpdateWorker>();
builder.Services.AddHostedService<TrayService>();

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
    await next(context);
});
app.UseWidgetCors();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        var (status, code, message) = exception switch
        {
            BadHttpRequestException error => (error.StatusCode, "invalid_request", "The local helper request is invalid or too large."),
            MsalUiRequiredException => (401, "signed_out", "Sign in to your Microsoft work account."),
            MsalException => (401, "auth_required", "Microsoft sign-in needs attention."),
            BoardMembersUnavailableException error => (403, "board_members_unavailable", error.Message),
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

app.Use(async (context, next) =>
{
    // Limit Outlook bodies before minimal-API JSON binding, including chunked requests.
    if (context.Request.Path.StartsWithSegments("/api/outlook"))
    {
        if (context.Request.ContentLength > 16384)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_request", message = "The Outlook request is too large." } });
            return;
        }
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = 16384;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = HelperHost.Version, service = "Microsoft Widgets Helper", integrations = new[] { "planner", "outlook" } }));
app.MapHelperHost();
app.MapUpdates();
app.MapGet("/configuration", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.GetConfigurationAsync(ct)));
app.MapPut("/configuration", async (AzureAdOptions configuration, IMicrosoftAuthService auth,
    IPlannerSettingsStore settings, OutlookAccountState outlookAccount, CancellationToken ct) =>
{
    var previous = await auth.GetConfigurationAsync(ct);
    var saved = await outlookAccount.TransitionAsync(() => auth.SaveConfigurationAsync(configuration, ct), ct);
    if (previous != saved)
    {
        await settings.UpdateSettingsAsync(selection =>
            selection with { SelectedPlanId = null, SelectedPlanTitle = null }, ct);
    }
    return Results.Ok(saved);
});
app.MapGet("/auth/status", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.GetStatusAsync(ct)));
app.MapGet("/auth/capabilities", async (MicrosoftAuthCapabilityService capabilities, HttpResponse response, CancellationToken ct) =>
{
    response.Headers.CacheControl = "no-store";
    return Results.Ok(await capabilities.GetAsync(ct));
});
app.MapGet("/auth/me", async (IPlannerGraphClient graph, CancellationToken ct) =>
    Results.Ok(new { userId = await graph.GetCurrentUserIdAsync(ct) }));
app.MapGet("/auth/sign-in", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
    Results.Ok(await outlookAccount.TransitionAsync(() => auth.SignInAsync(ct), ct)));
app.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
    Results.Ok(await outlookAccount.TransitionAsync(() => auth.SignInAsync(ct), ct)));
app.MapPost("/auth/enable-task-chat", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
    Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableTaskChatAsync(ct), ct)));
app.MapPost("/auth/enable-assignee-names", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
    Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableAssigneeNamesAsync(ct), ct)));
app.MapPost("/auth/enable-board-members", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
    Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableBoardMembersAsync(ct), ct)));
app.MapPost("/auth/sign-out", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
{
    await outlookAccount.TransitionAsync(async () => { await auth.SignOutAsync(ct); return true; }, ct, forceInvalidate: true);
    return Results.NoContent();
});
app.MapPlannerIntegration();
app.MapOutlookIntegration();

await app.StartAsync();
if (OperatingSystem.IsWindows() && !args.Contains("--no-browser"))
{
    try { HelperHost.OpenSetup(); }
    catch (Exception error) { app.Logger.LogWarning(error, "Could not open setup page automatically."); }
}
await app.WaitForShutdownAsync();
