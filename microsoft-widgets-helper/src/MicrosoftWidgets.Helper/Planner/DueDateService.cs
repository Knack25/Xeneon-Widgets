using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class DueDateService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    IMemoryCache cache)
{
    public async Task SetAsync(string taskId, string? date, CancellationToken cancellationToken)
    {
        DateTimeOffset? due = null;
        if (date is not null)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new ArgumentException("Choose a valid due date.");
            due = new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.Zero);
        }

        var task = await selectedPlanTasks.GetAsync(taskId, cancellationToken);
        if (due is not null && task.StartDateTime is not null && due < task.StartDateTime)
            throw new ArgumentException("Due date cannot be before the task start date.");

        await graphClient.SetDueDateAsync(taskId, due, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
