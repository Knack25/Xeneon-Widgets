using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskCompletionService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks)
{
    public async Task<CompleteTaskResponse> CompleteAsync(string taskId, CancellationToken cancellationToken)
    {
        var task = await selectedPlanTasks.GetAsync(taskId, cancellationToken);

        if (task.PercentComplete >= 100)
        {
            return new CompleteTaskResponse(taskId, Completed: true, Board: null);
        }

        await graphClient.CompleteTaskAsync(taskId, task.ETag, cancellationToken);
        return new CompleteTaskResponse(taskId, Completed: true, Board: null);
    }
}
