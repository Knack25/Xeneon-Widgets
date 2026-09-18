using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class DueDateService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore, IMemoryCache cache)
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

        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId) || task.PlanId != settings.SelectedPlanId)
            throw new ArgumentException("This task is not on the selected board.");
        if (due is not null && task.StartDateTime is not null && due < task.StartDateTime)
            throw new ArgumentException("Due date cannot be before the task start date.");

        await graphClient.SetDueDateAsync(taskId, due, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
