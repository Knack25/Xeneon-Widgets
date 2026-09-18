using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskAssignmentService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore,
    BoardMemberService members, IMemoryCache cache)
{
    public async Task SetAsync(string taskId, IReadOnlyList<string> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count > 50 || userIds.Any(id => !Guid.TryParse(id, out _)))
            throw new ArgumentException("Choose valid board members.");
        var desired = userIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId) || task.PlanId != settings.SelectedPlanId)
            throw new ArgumentException("This task is not on the selected board.");
        var added = desired.Except(task.Assignments, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = task.Assignments.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray();
        if (added.Length == 0 && removed.Length == 0) return;
        await members.ValidateAsync(added, cancellationToken);
        await graphClient.SetAssignmentsAsync(taskId, added, removed, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
