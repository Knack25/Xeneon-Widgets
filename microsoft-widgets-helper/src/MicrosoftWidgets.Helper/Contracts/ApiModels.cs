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

public sealed record SelectedPlanRequest(string PlanId);

public sealed record MoveTaskRequest(string BucketId);

public sealed record DueDateRequest(string? Date);
public sealed record AssignmentsRequest(IReadOnlyList<string> UserIds);
public sealed record CreateTaskRequest(string Title, string BucketId, string? Date, IReadOnlyList<string> UserIds);
public sealed record UpdateNotesRequest(string? Description);

public sealed record TaskChatMessage(string Id, string Author, DateTimeOffset? CreatedAt, string Body);

public sealed record TaskChatResponse(string State, IReadOnlyList<TaskChatMessage> Messages,
    string? NextCursor = null, string? Message = null);

public sealed record PostChatRequest(string Message);

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

public sealed record ChecklistItemDisplay(string ItemId, string Title, bool IsChecked);

public sealed record TaskDetailsResponse(
    string TaskId,
    string Title,
    string? BucketId,
    DateTimeOffset? DueDateTime,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<ChecklistItemDisplay> Checklist,
    string? Description = null,
    IReadOnlyList<string>? AssigneeIds = null);
