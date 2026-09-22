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
        owner.MapWidgetPairingManagement();
        owner.MapGet("/configuration", async (IMicrosoftAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.GetConfigurationAsync(ct)));
        owner.MapPut("/configuration", async (AzureAdOptions configuration, IMicrosoftAuthService auth,
            IPlannerSettingsStore settings, MicrosoftAccountState outlookAccount, LocalAccessService access,
            HttpResponse response, CancellationToken ct) =>
        {
            var previous = await auth.GetConfigurationAsync(ct);
            var outcome = await outlookAccount.TransitionAsync(
                () => auth.SaveConfigurationAsync(configuration, ct),
                (saved, _, current) => Task.FromResult((Saved: saved, Replacement: previous != saved
                    ? access.IssueReplacementOwnerSession(current)
                    : null)),
                ct, invalidateWhen: value => previous != value);
            if (outcome.Replacement is not null)
            {
                await settings.UpdateSettingsAsync(selection =>
                    selection with { SelectedPlanId = null, SelectedPlanTitle = null }, ct);
                response.Headers[LocalAccessHeaders.OwnerReplacement] =
                    outcome.Replacement;
            }
            return Results.Ok(outcome.Saved);
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
        owner.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, MicrosoftAccountState outlookAccount,
            LocalAccessService access, HttpResponse response, CancellationToken ct) =>
        {
            var outcome = await outlookAccount.TransitionAsync(
                () => auth.SignInAsync(ct),
                (result, previous, current) => Task.FromResult((Result: result, Replacement: previous != current
                    ? access.IssueReplacementOwnerSession(current)
                    : null)),
                ct);
            if (outcome.Replacement is not null)
            {
                response.Headers.CacheControl = "no-store";
                response.Headers[LocalAccessHeaders.OwnerReplacement] =
                    outcome.Replacement;
            }
            return Results.Ok(outcome.Result);
        });
        owner.MapPost("/auth/enable-task-chat", async (IMicrosoftAuthService auth, MicrosoftAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableTaskChatAsync(ct), ct)));
        owner.MapPost("/auth/enable-assignee-names", async (IMicrosoftAuthService auth, MicrosoftAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableAssigneeNamesAsync(ct), ct)));
        owner.MapPost("/auth/enable-board-members", async (IMicrosoftAuthService auth, MicrosoftAccountState outlookAccount, CancellationToken ct) =>
            Results.Ok(await outlookAccount.TransitionAsync(() => auth.EnableBoardMembersAsync(ct), ct)));
        owner.MapPost("/auth/sign-out", async (IMicrosoftAuthService auth, MicrosoftAccountState outlookAccount, CancellationToken ct) =>
        {
            await outlookAccount.TransitionAsync(async () => { await auth.SignOutAsync(ct); return true; }, ct, forceInvalidate: true);
            return Results.NoContent();
        });
    }
}
