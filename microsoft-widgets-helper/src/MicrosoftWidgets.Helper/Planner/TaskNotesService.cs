using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskNotesService(IPlannerGraphClient graphClient, TaskDetailsService taskDetails)
{
    public async Task UpdateAsync(string taskId, string description, CancellationToken cancellationToken)
    {
        if (description.Length > 4000)
            throw new ArgumentException("Task notes cannot exceed 4000 characters.", nameof(description));

        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        await graphClient.UpdateTaskDescriptionAsync(taskId, description, details.ETag, cancellationToken);
        taskDetails.Invalidate(taskId);
    }
}
