using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskMoveService(IPlannerGraphClient graphClient, IPlannerSettingsStore settingsStore, IMemoryCache cache)
{
    public async Task MoveAsync(string taskId, string bucketId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bucketId)) throw new ArgumentException("Choose a destination bucket.");

        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId) || task.PlanId != settings.SelectedPlanId)
            throw new ArgumentException("This task is not on the selected board.");

        var buckets = await graphClient.GetBucketsAsync(task.PlanId, cancellationToken);
        if (!buckets.Any(bucket => bucket.Id == bucketId))
            throw new ArgumentException("Choose a bucket on the selected board.");
        if (task.BucketId == bucketId) return;

        await graphClient.MoveTaskAsync(taskId, bucketId, task.ETag, cancellationToken);
        cache.Remove(taskId);
    }
}
