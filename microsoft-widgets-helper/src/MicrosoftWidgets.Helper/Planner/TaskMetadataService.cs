using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskMetadataService(
    IPlannerGraphClient graphClient,
    IPlannerSettingsStore settingsStore,
    IMemoryCache cache)
{
    public async Task SetTitleAsync(string taskId, string title, CancellationToken cancellationToken)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 255)
            throw new ArgumentException("Enter a task title (up to 255 characters).");
        await UpdateAsync(taskId, task => new GraphTaskUpdate(Title: title), cancellationToken);
    }

    public async Task SetProgressAsync(string taskId, int progress, CancellationToken cancellationToken)
    {
        if (progress is not (0 or 50 or 100))
            throw new ArgumentException("Choose not started, in progress, or complete.");
        await UpdateAsync(taskId, task => new GraphTaskUpdate(PercentComplete: progress), cancellationToken);
    }

    public async Task SetPriorityAsync(string taskId, int priority, CancellationToken cancellationToken)
    {
        if (priority is not (1 or 3 or 5 or 9))
            throw new ArgumentException("Choose a valid priority.");
        await UpdateAsync(taskId, task => new GraphTaskUpdate(Priority: priority), cancellationToken);
    }

    public async Task SetStartDateAsync(string taskId, string? date, CancellationToken cancellationToken)
    {
        var start = ParseDate(date);
        await UpdateAsync(taskId, task =>
        {
            if (start is not null && task.DueDateTime is not null && start > task.DueDateTime)
                throw new ArgumentException("Start date cannot be after the due date.");
            return new GraphTaskUpdate(StartDateTime: start, ClearStartDate: start is null);
        }, cancellationToken);
    }

    public async Task SetLabelsAsync(string taskId, IReadOnlyList<string> labelIds,
        CancellationToken cancellationToken)
    {
        var task = await LoadOwnedTaskAsync(taskId, cancellationToken);
        var labels = await graphClient.GetPlanLabelsAsync(task.PlanId, cancellationToken);
        var namedIds = labels.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var selectedIds = labelIds.ToHashSet(StringComparer.Ordinal);
        if (selectedIds.Any(labelId => !namedIds.Contains(labelId)))
            throw new ArgumentException("Choose labels from the selected board.");

        var appliedIds = (task.AppliedCategories ?? []).ToHashSet(StringComparer.Ordinal);
        var changes = new Dictionary<string, bool?>();
        foreach (var label in labels)
        {
            var selected = selectedIds.Contains(label.Id);
            if (selected != appliedIds.Contains(label.Id)) changes[label.Id] = selected;
        }
        await graphClient.UpdateTaskAsync(taskId, new GraphTaskUpdate(AppliedCategories: changes),
            task.ETag, cancellationToken);
        cache.Remove(taskId);
    }

    private async Task UpdateAsync(string taskId, Func<GraphTask, GraphTaskUpdate> createUpdate,
        CancellationToken cancellationToken)
    {
        var task = await LoadOwnedTaskAsync(taskId, cancellationToken);
        await graphClient.UpdateTaskAsync(taskId, createUpdate(task), task.ETag, cancellationToken);
        cache.Remove(taskId);
    }

    private async Task<GraphTask> LoadOwnedTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId) || task.PlanId != settings.SelectedPlanId)
            throw new ArgumentException("This task is not on the selected board.");
        return task;
    }

    private static DateTimeOffset? ParseDate(string? date)
    {
        if (date is null) return null;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            throw new ArgumentException("Choose a valid start date.");
        return new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.Zero);
    }
}
