namespace PlannerEdge.Helper.Graph;

public sealed record GraphGroup(string Id, string DisplayName);
public sealed record GraphMember(string Id, string DisplayName);

public sealed record GraphPlan(string Id, string Title, string GroupId, string? GroupName);

public sealed record GraphPlanLabel(string Id, string Name);

public sealed record GraphBucket(string Id, string Name, string PlanId, string? OrderHint = null);

public sealed record GraphChecklistItem(string Id, string Title, bool IsChecked, string? OrderHint);

public sealed record GraphTaskDetails(string ETag, IReadOnlyList<GraphChecklistItem> Checklist, string? Description = null);

public sealed record GraphConversationPost(
    string Id,
    string Body,
    string Author,
    DateTimeOffset? CreatedAt,
    string ContentType = "html");

public sealed record GraphConversationPage(IReadOnlyList<GraphConversationPost> Posts, Uri? NextLink);

public sealed record GraphTask(
    string Id,
    string Title,
    string PlanId,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments,
    string? BucketOrderHint = null,
    DateTimeOffset? StartDateTime = null,
    string? ConversationThreadId = null,
    IReadOnlyList<string>? AppliedCategories = null);
