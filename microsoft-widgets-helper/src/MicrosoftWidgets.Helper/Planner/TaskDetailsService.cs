using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskDetailsService(IPlannerGraphClient graphClient, IMemoryCache cache)
{
    public async Task<TaskDetailsResponse> GetAsync(string taskId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue<TaskDetailsResponse>(taskId, out var cached) && cached is not null)
            return cached;

        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        var assignees = new List<string>();
        foreach (var userId in task.Assignments.Distinct(StringComparer.Ordinal))
        {
            if (!cache.TryGetValue<string>($"user:{userId}", out var name))
            {
                try
                {
                    name = await graphClient.GetUserDisplayNameAsync(userId, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(name)) cache.Set($"user:{userId}", name, TimeSpan.FromHours(1));
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
        cache.Set(taskId, response, TimeSpan.FromSeconds(45));
        return response;
    }

    public void Invalidate(string taskId) => cache.Remove(taskId);
}
