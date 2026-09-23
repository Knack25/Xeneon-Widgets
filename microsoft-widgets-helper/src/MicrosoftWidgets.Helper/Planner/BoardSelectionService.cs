using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class BoardSelectionService(PlannerBoardService boards, IPlannerSettingsStore settings,
    PlannerDataLifecycle lifecycle)
{
    internal BoardSelectionService(PlannerBoardService boards, IPlannerSettingsStore settings)
        : this(boards, settings, new PlannerDataLifecycle(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))) { }

    public async Task<SettingsDto> SelectAsync(string planId, CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var plan = (await boards.GetPlansAsync(cancellationToken)).SingleOrDefault(item => item.PlanId == planId)
            ?? throw new ArgumentException("That board is no longer available.");
        return await settings.UpdateSettingsAsync(current =>
            current with { SelectedPlanId = plan.PlanId, SelectedPlanTitle = plan.Title }, cancellationToken);
    }
}
