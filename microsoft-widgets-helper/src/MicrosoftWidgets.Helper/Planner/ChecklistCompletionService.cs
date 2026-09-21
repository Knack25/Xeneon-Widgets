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
        await selectedPlanTasks.GetAsync(taskId, cancellationToken);
        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        var item = details.Checklist.SingleOrDefault(value => value.Id == itemId)
            ?? throw new InvalidOperationException("Checklist item was not found.");
        if (!item.IsChecked)
            await graphClient.CompleteChecklistItemAsync(taskId, itemId, details.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
