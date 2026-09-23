using PlannerEdge.Helper.Graph;
using Microsoft.Extensions.Caching.Memory;

namespace PlannerEdge.Helper.Planner;

public sealed class ChecklistCompletionService(
    IPlannerGraphClient graphClient,
    SelectedPlanTaskService selectedPlanTasks,
    IMemoryCache cache)
{
    public async Task CompleteAsync(string taskId, string itemId, CancellationToken cancellationToken)
    {
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        var item = details.Checklist.SingleOrDefault(value => value.Id == itemId)
            ?? throw new InvalidOperationException("Checklist item was not found.");
        if (!item.IsChecked)
            await selectedPlanTasks.RunAsync(selected,
                ct => graphClient.CompleteChecklistItemAsync(taskId, itemId, details.ETag, ct), cancellationToken);
        cache.Remove(taskId);
    }
}
