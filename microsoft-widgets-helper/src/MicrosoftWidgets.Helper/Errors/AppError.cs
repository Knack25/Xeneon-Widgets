using PlannerEdge.Helper.Contracts;

namespace PlannerEdge.Helper.Errors;

public sealed record AppError(string Code, string Message)
{
    public ApiErrorResponse ToResponse() => new(Code, Message);

    public static AppError SignedOut(string message = "Sign in to Microsoft Planner.") => new("signed_out", message);
    public static AppError ConsentRequired(string message = "Microsoft consent is required.") => new("consent_required", message);
    public static AppError Forbidden(string message = "Planner denied access.") => new("forbidden", message);
    public static AppError NetworkUnavailable(string message = "Network unavailable.") => new("network_unavailable", message);
    public static AppError GraphUnavailable(string message = "Microsoft Graph unavailable.") => new("graph_unavailable", message);
    public static AppError TaskConflict(string message = "The task changed in Planner.") => new("task_conflict", message);
    public static AppError Unknown(string message = "Something went wrong.") => new("unknown_error", message);
}
