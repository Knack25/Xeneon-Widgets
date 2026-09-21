namespace PlannerEdge.Helper.Graph;

public interface IPlannerGraphClient
{
    Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken);

    Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken);

    Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken);

    Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken);

    Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken);

    Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken);

    Task UpdateTaskDescriptionAsync(string taskId, string description, string etag, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken);

    Task MoveTaskAsync(string taskId, string bucketId, string etag, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task SetDueDateAsync(string taskId, DateTimeOffset? dueDate, string etag, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task SetAssignmentsAsync(string taskId, IReadOnlyList<string> add, IReadOnlyList<string> remove,
        string etag, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate,
        IReadOnlyList<string> assigneeIds, CancellationToken cancellationToken) => throw new NotSupportedException();

    Task<GraphConversationPage> GetConversationPostsAsync(string groupId, string threadId, Uri? continuationUri,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task ReplyToConversationAsync(string groupId, string threadId, string message, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    Task<string> CreateConversationThreadAsync(string groupId, string topic, string message,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    Task SetConversationThreadAsync(string taskId, string threadId, string etag, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
