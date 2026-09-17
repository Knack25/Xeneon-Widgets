namespace PlannerEdge.Helper.Graph;

public interface IPlannerGraphClient
{
    Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken);

    Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken);

    Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken);

    Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken);

    Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken);
}
