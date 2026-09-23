using Microsoft.Identity.Client;
using System.Net;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class BoardMemberService(IPlannerGraphClient graphClient, PlannerDataLifecycle lifecycle,
    IBoardSelectionCoordinator selection)
{
    internal BoardMemberService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
        PlannerDataLifecycle lifecycle)
        : this(graphClient, lifecycle, new BoardSelectionCoordinator(settingsStore)) { }

    internal BoardMemberService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore)
        : this(graphClient, new PlannerDataLifecycle(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            new BoardSelectionCoordinator(settingsStore)) { }

    public async Task<BoardMemberSelection> GetAsync(CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var lifecycleTicket = lifecycle.CaptureTicket();
        var selectionTicket = await selection.CaptureAsync(cancellationToken);
        try
        {
            var members = await selection.RunAsync(selectionTicket,
                ct => GetForPlanAsync(selectionTicket.PlanId, lifecycleTicket, ct), cancellationToken);
            return new BoardMemberSelection(members, selectionTicket);
        }
        catch (BoardMemberAuthorizationLostException error)
        {
            await lifecycle.PurgeAsync(CancellationToken.None);
            throw new BoardMembersUnavailableException(error.Message);
        }
    }

    internal async Task ValidateWithinSelectionAsync(IReadOnlyList<string> userIds, string planId,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0) return;
        using var operation = lifecycle.BindOperation();
        var members = await GetForPlanAsync(planId, lifecycle.CaptureTicket(), cancellationToken);
        var valid = members.Select(member => member.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (userIds.Any(id => !valid.Contains(id)))
            throw new ArgumentException("Choose people from the selected board's member list.");
    }

    private async Task<IReadOnlyList<GraphMember>> GetForPlanAsync(string planId, PlannerDataTicket lifecycleTicket,
        CancellationToken cancellationToken)
    {
        var plans = await graphClient.GetMyPlansAsync(cancellationToken);
        var plan = plans.FirstOrDefault(value => value.Id == planId);
        if (plan is null || !Guid.TryParse(plan.GroupId, out _))
            throw new BoardMembersUnavailableException("This board does not have a supported member list.");
        try
        {
            var members = await graphClient.GetGroupMembersAsync(plan.GroupId, cancellationToken);
            lifecycle.RequireCurrent(lifecycleTicket);
            return members;
        }
        catch (MsalUiRequiredException)
        {
            throw new BoardMembersUnavailableException("Enable board members on the setup page to edit assignees.");
        }
        catch (GraphApiException error) when (error.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new BoardMemberAuthorizationLostException(
                "Board member access was denied. Ask your work administrator to approve it.", error);
        }
    }
}

public sealed record BoardMemberSelection(IReadOnlyList<GraphMember> Members, BoardSelectionTicket Selection);

public sealed class BoardMembersUnavailableException(string message) : Exception(message);

internal sealed class BoardMemberAuthorizationLostException(string message, Exception inner)
    : Exception(message, inner);
