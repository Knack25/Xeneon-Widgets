namespace PlannerEdge.Helper.Graph;

public sealed record GraphGroup(string Id, string DisplayName);

public sealed record GraphPlan(string Id, string Title, string GroupId, string? GroupName);

public sealed record GraphBucket(string Id, string Name, string PlanId, string? OrderHint = null);

public sealed record GraphTask(
    string Id,
    string Title,
    string PlanId,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments);
