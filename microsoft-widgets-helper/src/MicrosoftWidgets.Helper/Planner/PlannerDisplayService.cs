using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerDisplayService(IPlannerGraphClient graphClient)
{
    public async Task<BoardDisplay> GetDisplayAsync(
        string planId,
        string planTitle,
        bool hideCompletedTasks,
        CancellationToken cancellationToken)
    {
        var buckets = await graphClient.GetBucketsAsync(planId, cancellationToken);
        var tasks = await graphClient.GetTasksAsync(planId, cancellationToken);
        var labels = await graphClient.GetPlanLabelsAsync(planId, cancellationToken);

        var visibleTasks = hideCompletedTasks
            ? tasks.Where(task => task.PercentComplete < 100)
            : tasks;

        var tasksByBucket = visibleTasks
            .GroupBy(task => task.BucketId ?? string.Empty)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(task => task.BucketOrderHint is null)
                .ThenBy(task => task.BucketOrderHint, StringComparer.Ordinal)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Select(ToDisplay).ToList());

        var bucketDisplays = buckets
            .OrderBy(bucket => bucket.OrderHint is null)
            .ThenByDescending(bucket => bucket.OrderHint, StringComparer.Ordinal)
            .ThenBy(bucket => bucket.Id, StringComparer.Ordinal)
            .Select(bucket => new BucketDisplay(
                bucket.Id,
                bucket.Name,
                tasksByBucket.TryGetValue(bucket.Id, out var bucketTasks) ? bucketTasks : []))
            .ToList();

        if (tasksByBucket.TryGetValue(string.Empty, out var unbucketedTasks) && unbucketedTasks.Count > 0)
        {
            bucketDisplays.Add(new BucketDisplay("unbucketed", "No bucket", unbucketedTasks));
        }

        return new BoardDisplay(
            planId,
            planTitle,
            DateTimeOffset.UtcNow,
            IsStale: false,
            bucketDisplays,
            labels.Select(label => new LabelDisplay(label.Id, label.Name)).ToList());
    }

    private static TaskDisplay ToDisplay(GraphTask task)
    {
        return new TaskDisplay(
            task.Id,
            task.Title,
            task.BucketId,
            task.DueDateTime,
            task.Priority,
            task.PercentComplete,
            task.ETag,
            task.Assignments,
            task.StartDateTime,
            task.AppliedCategories ?? []);
    }
}
