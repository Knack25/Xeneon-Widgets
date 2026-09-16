using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerBoardService(IPlannerGraphClient graphClient)
{
    public async Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var groups = await graphClient.GetMemberGroupsAsync(cancellationToken);
        var plans = new List<PlanSummary>();

        foreach (var group in groups.OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var groupPlans = await graphClient.GetPlansForGroupAsync(group.Id, cancellationToken);
            plans.AddRange(groupPlans.Select(plan => new PlanSummary(plan.Id, plan.Title, plan.GroupId, plan.GroupName ?? group.DisplayName)));
        }

        return plans
            .OrderBy(plan => plan.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
