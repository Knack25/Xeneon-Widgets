using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerBoardService(IPlannerGraphClient graphClient, PlannerDataLifecycle lifecycle)
{
    internal PlannerBoardService(IPlannerGraphClient graphClient, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
        : this(graphClient, new PlannerDataLifecycle(cache)) { }
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var cached = await lifecycle.TryGetAsync<IReadOnlyList<PlanSummary>>("plans", "all", cancellationToken);
        if (cached.Found && cached.Value is not null) return cached.Value;
        await gate.WaitAsync(cancellationToken);
        try
        {
            cached = await lifecycle.TryGetAsync<IReadOnlyList<PlanSummary>>("plans", "all", cancellationToken);
            if (cached.Found && cached.Value is not null) return cached.Value;
            var plans = await graphClient.GetMyPlansAsync(cancellationToken);
            var groups = await graphClient.GetMemberGroupsAsync(cancellationToken);
            var groupNames = groups.ToDictionary(group => group.Id, group => group.DisplayName);
            var result = plans.Select(plan => new PlanSummary(plan.Id, plan.Title, plan.GroupId,
                    plan.GroupName ?? groupNames.GetValueOrDefault(plan.GroupId) ?? "Planner"))
                .OrderBy(plan => plan.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(plan => plan.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            await lifecycle.SetAsync("plans", "all", (IReadOnlyList<PlanSummary>)result,
                TimeSpan.FromMinutes(2), cancellationToken);
            return result;
        }
        finally { gate.Release(); }
    }
}
