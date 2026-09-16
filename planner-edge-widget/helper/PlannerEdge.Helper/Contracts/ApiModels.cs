namespace PlannerEdge.Helper.Contracts;

public sealed record ApiErrorResponse(string Code, string Message);

public sealed record AuthStatusResponse(
    bool IsSignedIn,
    string? DisplayName,
    string? AccountHint,
    ApiErrorResponse? Error = null);

public sealed record PlanSummary(
    string PlanId,
    string Title,
    string? GroupId,
    string? GroupName);

public sealed record SettingsDto(
    string? SelectedPlanId,
    string? SelectedPlanTitle,
    bool HideCompletedTasks);

public sealed record BucketDisplay(
    string BucketId,
    string Name,
    IReadOnlyList<TaskDisplay> Tasks);

public sealed record TaskDisplay(
    string TaskId,
    string Title,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    int? Priority,
    int PercentComplete,
    string ETag,
    IReadOnlyList<string> Assignments);

public sealed record BoardDisplay(
    string PlanId,
    string PlanTitle,
    DateTimeOffset SyncedAt,
    bool IsStale,
    IReadOnlyList<BucketDisplay> Buckets);

public sealed record CompleteTaskResponse(
    string TaskId,
    bool Completed,
    BoardDisplay? Board);
