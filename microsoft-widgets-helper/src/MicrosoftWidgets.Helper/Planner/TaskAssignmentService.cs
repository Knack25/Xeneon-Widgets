using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskAssignmentService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    BoardMemberService members, IMemoryCache cache)
{
    public async Task SetAsync(string taskId, IReadOnlyList<string> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count > 50 || userIds.Any(id => !Guid.TryParse(id, out _)))
            throw new ArgumentException("Choose valid board members.");
        var desired = userIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var task = await selectedPlanTasks.GetAsync(taskId, cancellationToken);
        var added = desired.Except(task.Assignments, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = task.Assignments.Except(desired, StringComparer.OrdinalIgnoreCase).ToArray();
        if (added.Length == 0 && removed.Length == 0) return;
        await members.ValidateAsync(added, cancellationToken);
        await graphClient.SetAssignmentsAsync(taskId, added, removed, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
