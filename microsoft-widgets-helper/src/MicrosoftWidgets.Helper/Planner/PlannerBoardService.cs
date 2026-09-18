using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerBoardService(IPlannerGraphClient graphClient, IMemoryCache cache)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<IReadOnlyList<PlanSummary>>("plans", out var cached) && cached is not null) return cached;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue<IReadOnlyList<PlanSummary>>("plans", out cached) && cached is not null) return cached;
            var plans = await graphClient.GetMyPlansAsync(cancellationToken);
            var groups = await graphClient.GetMemberGroupsAsync(cancellationToken);
            var groupNames = groups.ToDictionary(group => group.Id, group => group.DisplayName);
            var result = plans.Select(plan => new PlanSummary(plan.Id, plan.Title, plan.GroupId,
                    plan.GroupName ?? groupNames.GetValueOrDefault(plan.GroupId) ?? "Planner"))
                .OrderBy(plan => plan.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            cache.Set("plans", (IReadOnlyList<PlanSummary>)result, TimeSpan.FromMinutes(2));
            return result;
        }
        finally { gate.Release(); }
    }
}
