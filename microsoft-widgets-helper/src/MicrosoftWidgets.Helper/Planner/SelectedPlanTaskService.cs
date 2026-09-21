using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class SelectedPlanTaskService(
    IPlannerGraphClient graphClient,
    IPlannerSettingsStore settingsStore)
{
    public async Task<GraphTask> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId) ||
            !string.Equals(task.PlanId, settings.SelectedPlanId, StringComparison.Ordinal))
            throw new ArgumentException("This task is not on the selected board.");
        return task;
    }
}
