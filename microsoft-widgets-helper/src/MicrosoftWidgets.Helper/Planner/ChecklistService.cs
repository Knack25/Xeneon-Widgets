using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class ChecklistService(IPlannerGraphClient graphClient, IMemoryCache cache)
{
    public async Task AddAsync(string taskId, string title, CancellationToken cancellationToken)
    {
        title = ValidateTitle(title);
        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        var hint = $"{details.Checklist.LastOrDefault()?.OrderHint ?? string.Empty} !";
        await PatchAsync(taskId, Guid.NewGuid().ToString("D"), new GraphChecklistPatch(title, hint),
            details.ETag, cancellationToken);
    }

    public async Task RenameAsync(string taskId, string itemId, string title, CancellationToken cancellationToken)
    {
        title = ValidateTitle(title);
        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        FindItem(details, itemId);
        await PatchAsync(taskId, itemId, new GraphChecklistPatch(Title: title), details.ETag, cancellationToken);
    }

    public async Task DeleteAsync(string taskId, string itemId, CancellationToken cancellationToken)
    {
        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        FindItem(details, itemId);
        await PatchAsync(taskId, itemId, null, details.ETag, cancellationToken);
    }

    public async Task MoveAsync(string taskId, string itemId, string direction, CancellationToken cancellationToken)
    {
        if (direction is not ("up" or "down"))
            throw new ArgumentException("Choose up or down.");

        var details = await graphClient.GetTaskDetailsAsync(taskId, cancellationToken);
        var reordered = details.Checklist.ToList();
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
        await PatchAsync(taskId, itemId, new GraphChecklistPatch(OrderHint: hint), details.ETag,
            cancellationToken);
    }

    private async Task PatchAsync(string taskId, string itemId, GraphChecklistPatch? patch, string etag,
        CancellationToken cancellationToken)
    {
        await graphClient.PatchChecklistAsync(taskId, itemId, patch, etag, cancellationToken);
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
