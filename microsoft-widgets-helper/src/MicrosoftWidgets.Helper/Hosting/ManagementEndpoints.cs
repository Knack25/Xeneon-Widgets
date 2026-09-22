using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Updates;

namespace PlannerEdge.Helper.Hosting;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this WebApplication app)
    {
        var owner = app.MapGroup("").AddEndpointFilter<OwnerAuthorizationFilter>();
        owner.MapHelperHostManagement();
        owner.MapUpdates();
        owner.MapOutlookManagement();
        owner.MapGet("/configuration", async (IMicrosoftAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.GetConfigurationAsync(ct)));
        owner.MapPut("/configuration", async (AzureAdOptions configuration, IMicrosoftAuthService auth,
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
        owner.MapGet("/auth/status", async (IMicrosoftAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.GetStatusAsync(ct)));
        owner.MapGet("/auth/capabilities", async (MicrosoftAuthCapabilityService capabilities, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            return Results.Ok(await capabilities.GetAsync(ct));
        });
        owner.MapGet("/auth/me", async (IPlannerGraphClient graph, CancellationToken ct) =>
            Results.Ok(new { userId = await graph.GetCurrentUserIdAsync(ct) }));
        owner.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.SignInAsync(ct), ct)));
        owner.MapPost("/auth/enable-task-chat", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableTaskChatAsync(ct), ct)));
        owner.MapPost("/auth/enable-assignee-names", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableAssigneeNamesAsync(ct), ct)));
        owner.MapPost("/auth/enable-board-members", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableBoardMembersAsync(ct), ct)));
        owner.MapPost("/auth/sign-out", async (IMicrosoftAuthService auth, OutlookAccountState outlookAccount, CancellationToken ct) =>
        {
            await outlookAccount.TransitionAsync(async () => { await auth.SignOutAsync(ct); return true; }, ct, forceInvalidate: true);
            return Results.NoContent();
        });
    }
}
