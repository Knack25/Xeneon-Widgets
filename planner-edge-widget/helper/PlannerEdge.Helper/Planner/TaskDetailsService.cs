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
        var response = new TaskDetailsResponse(task.Id, task.Title, task.DueDateTime, task.Assignments,
            details.Checklist.Select(item => new ChecklistItemDisplay(item.Id, item.Title, item.IsChecked)).ToList());
        cache.Set(taskId, response, TimeSpan.FromSeconds(45));
        return response;
    }

    public void Invalidate(string taskId) => cache.Remove(taskId);
}
