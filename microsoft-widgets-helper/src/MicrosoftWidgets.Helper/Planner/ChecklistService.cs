using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class ChecklistService(
    IPlannerGraphClient graphClient,
    SelectedPlanTaskService selectedPlanTasks,
    IMemoryCache cache)
{
    public async Task AddAsync(string taskId, string title, CancellationToken cancellationToken)
    {
        title = ValidateTitle(title);
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        var hint = $"{details.Checklist.LastOrDefault()?.OrderHint ?? string.Empty} !";
        await PatchAsync(selected, taskId, Guid.NewGuid().ToString("D"), new GraphChecklistPatch(title, hint),
            details.ETag, cancellationToken);
    }

    public async Task RenameAsync(string taskId, string itemId, string title, CancellationToken cancellationToken)
    {
        title = ValidateTitle(title);
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        FindItem(details, itemId);
        await PatchAsync(selected, taskId, itemId, new GraphChecklistPatch(Title: title), details.ETag, cancellationToken);
    }

    public async Task DeleteAsync(string taskId, string itemId, CancellationToken cancellationToken)
    {
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        FindItem(details, itemId);
        await PatchAsync(selected, taskId, itemId, null, details.ETag, cancellationToken);
    }

    public async Task MoveAsync(string taskId, string itemId, string direction, CancellationToken cancellationToken)
    {
        if (direction is not ("up" or "down"))
            throw new ArgumentException("Choose up or down.");

        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var details = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetTaskDetailsAsync(taskId, ct), cancellationToken);
        var reordered = PlannerOrderHints.InCanonicalOrder(details.Checklist);
        var current = reordered.FindIndex(item => item.Id == itemId);
        if (current < 0) throw new InvalidOperationException("Checklist item was not found.");

        var target = direction == "up" ? current - 1 : current + 1;
        if (target < 0 || target >= reordered.Count) return;

        var item = reordered[current];
        reordered.RemoveAt(current);
        reordered.Insert(target, item);
        var previous = target > 0 ? reordered[target - 1].OrderHint ?? "" : "";
        var next = target + 1 < reordered.Count ? reordered[target + 1].OrderHint ?? "" : "";
        var hint = $"{previous} {next}!";
        await PatchAsync(selected, taskId, itemId, new GraphChecklistPatch(OrderHint: hint), details.ETag,
            cancellationToken);
    }

    private async Task PatchAsync(SelectedPlanTask selected, string taskId, string itemId,
        GraphChecklistPatch? patch, string etag,
        CancellationToken cancellationToken)
    {
        await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.PatchChecklistAsync(taskId, itemId, patch, etag, ct), cancellationToken);
        cache.Remove(taskId);
    }

    private static GraphChecklistItem FindItem(GraphTaskDetails details, string itemId) =>
        details.Checklist.SingleOrDefault(item => item.Id == itemId)
        ?? throw new InvalidOperationException("Checklist item was not found.");

    private static string ValidateTitle(string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 100)
            throw new ArgumentException("Enter a checklist item title (up to 100 characters).");
        return title;
    }
}
