using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskDetailsService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    PlannerDataLifecycle lifecycle)
{
    public async Task<TaskDetailsResponse> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var task = await selectedPlanTasks.GetAsync(taskId, cancellationToken);
        var cached = await lifecycle.TryGetAsync<TaskDetailsResponse>("task-details", taskId, cancellationToken);
        if (cached.Found && cached.Value is not null) return cached.Value;

        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        var assignees = new List<string>();
        foreach (var userId in task.Assignments.Distinct(StringComparer.Ordinal))
        {
            var cachedName = await lifecycle.TryGetAsync<string>("user-name", userId, cancellationToken);
            var name = cachedName.Value;
            if (!cachedName.Found)
            {
                try
                {
                    name = await graphClient.GetUserDisplayNameAsync(userId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(name))
                        await lifecycle.SetAsync("user-name", userId, name, TimeSpan.FromHours(1), cancellationToken);
                }
                catch (GraphApiException error) when (error.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    await lifecycle.PurgeAsync(CancellationToken.None);
                    throw;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    name = null;
                }
            }
            assignees.Add(string.IsNullOrWhiteSpace(name) ? "Assigned person unavailable" : name);
        }
        var response = new TaskDetailsResponse(task.Id, task.Title, task.BucketId, task.DueDateTime, assignees,
            details.Checklist.Select(item => new ChecklistItemDisplay(item.Id, item.Title, item.IsChecked)).ToList(),
            details.Description, task.Assignments, task.StartDateTime, task.Priority, task.PercentComplete,
            task.AppliedCategories ?? []);
        await lifecycle.SetAsync("task-details", taskId, response, TimeSpan.FromSeconds(45), cancellationToken);
        return response;
    }

    public void Invalidate(string taskId) => lifecycle.RemoveAsync("task-details", taskId).GetAwaiter().GetResult();
}
