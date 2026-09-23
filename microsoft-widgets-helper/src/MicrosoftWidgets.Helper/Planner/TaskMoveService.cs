using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskMoveService(IPlannerGraphClient graphClient, SelectedPlanTaskService selectedPlanTasks,
    IMemoryCache cache)
{
    public async Task MoveAsync(string taskId, string bucketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("Choose a destination bucket.");

        var task = await selectedPlanTasks.GetAsync(taskId, cancellationToken);

        var buckets = await graphClient.GetBucketsAsync(task.PlanId, cancellationToken);
        if (!buckets.Any(bucket => bucket.Id == bucketId))
            throw new ArgumentException("Choose a bucket on the selected board.");
        if (task.BucketId == bucketId) return;

        await graphClient.MoveTaskAsync(taskId, bucketId, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
