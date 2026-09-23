using Microsoft.Identity.Client;
using System.Net;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class BoardMemberService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
    PlannerDataLifecycle lifecycle)
{
    internal BoardMemberService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore)
        : this(graphClient, settingsStore,
            new PlannerDataLifecycle(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))) { }
    public async Task<IReadOnlyList<GraphMember>> GetAsync(CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var ticket = lifecycle.CaptureTicket();
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId))
            throw new ArgumentException("Choose a board first.");
        var plans = await graphClient.GetMyPlansAsync(cancellationToken);
        var plan = plans.FirstOrDefault(value => value.Id == settings.SelectedPlanId);
        if (plan is null || !Guid.TryParse(plan.GroupId, out _))
            throw new BoardMembersUnavailableException("This board does not have a supported member list.");
        try
        {
            var members = await graphClient.GetGroupMembersAsync(plan.GroupId, cancellationToken);
            lifecycle.RequireCurrent(ticket);
            return members;
        }
        catch (MsalUiRequiredException)
        {
            throw new BoardMembersUnavailableException("Enable board members on the setup page to edit assignees.");
        }
        catch (GraphApiException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
            await lifecycle.PurgeAsync(CancellationToken.None);
            throw new BoardMembersUnavailableException("Board member access was denied. Ask your work administrator to approve it.");
        }
    }

    public async Task ValidateAsync(IReadOnlyList<string> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return;
        var members = await GetAsync(cancellationToken);
        var valid = members.Select(member => member.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (userIds.Any(id => !valid.Contains(id)))
            throw new ArgumentException("Choose people from the selected board's member list.");
    }
}

public sealed class BoardMembersUnavailableException(string message) : Exception(message);
