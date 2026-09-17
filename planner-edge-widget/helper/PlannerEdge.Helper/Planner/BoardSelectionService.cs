using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class BoardSelectionService(PlannerBoardService boards, IPlannerSettingsStore settings)
{
    public async Task<SettingsDto> SelectAsync(string planId, CancellationToken cancellationToken)
    {
        var plan = (await boards.GetPlansAsync(cancellationToken)).SingleOrDefault(item => item.PlanId == planId)
            ?? throw new ArgumentException("That board is no longer available.");
        var current = await settings.LoadSettingsAsync(cancellationToken);
        var selected = current with { SelectedPlanId = plan.PlanId, SelectedPlanTitle = plan.Title };
        await settings.SaveSettingsAsync(selected, cancellationToken);
        return selected;
    }
}
