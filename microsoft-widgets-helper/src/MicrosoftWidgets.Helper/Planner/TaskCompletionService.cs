using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskCompletionService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks)
{
    public async Task<CompleteTaskResponse> CompleteAsync(string taskId, CancellationToken cancellationToken)
    {
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var task = selected.Task;

        if (task.PercentComplete >= 100)
        {
            return new CompleteTaskResponse(taskId, Completed: true, Board: null);
        }

        await selectedPlanTasks.RunAsync(selected, ct => graphClient.CompleteTaskAsync(taskId, task.ETag, ct),
            cancellationToken);
        return new CompleteTaskResponse(taskId, Completed: true, Board: null);
    }
}
