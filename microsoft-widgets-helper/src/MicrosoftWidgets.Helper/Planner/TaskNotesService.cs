using System.Net;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskNotesService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    TaskDetailsService taskDetails)
{
    public async Task UpdateAsync(string taskId, string? description, CancellationToken cancellationToken)
    {
        if (description is null)
            throw new ArgumentException("Task notes must be text.", nameof(description));
        if (description.Length > 4000)
            throw new ArgumentException("Task notes cannot exceed 4000 characters.", nameof(description));

        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        try
        {
            await selectedPlanTasks.RunAsync(selected,
                ct => graphClient.UpdateTaskDescriptionAsync(taskId, description, details.ETag, ct), cancellationToken);
        }
        catch (GraphApiException error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            taskDetails.Invalidate(taskId);
            throw;
        }
        taskDetails.Invalidate(taskId);
    }
}
