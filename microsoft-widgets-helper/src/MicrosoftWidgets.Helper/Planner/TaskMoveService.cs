using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskMoveService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    IMemoryCache cache)
{
    public async Task MoveAsync(string taskId, string bucketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("Choose a destination bucket.");

        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);
        var task = selected.Task;

        var buckets = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetBucketsAsync(task.PlanId, ct), cancellationToken);
        if (!buckets.Any(bucket => bucket.Id == bucketId))
            throw new ArgumentException("Choose a bucket on the selected board.");
        if (task.BucketId == bucketId) return;

        await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.MoveTaskAsync(taskId, bucketId, task.ETag, ct), cancellationToken);
        cache.Remove(taskId);
    }
}
