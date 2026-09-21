using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public static class PlannerIntegration
{
    public static IServiceCollection AddPlannerIntegration(this IServiceCollection services)
    {
        services.AddSingleton<IPlannerSettingsStore, PlannerSettingsStore>();
        services.AddHttpClient<IPlannerGraphClient, PlannerGraphClient>(client =>
            client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
        services.AddSingleton<PlannerBoardService>();
        services.AddSingleton<BoardSelectionService>();
        services.AddSingleton<PlannerDisplayService>();
        services.AddSingleton<TaskCompletionService>();
        services.AddSingleton<PlannerCoordinator>();
        services.AddSingleton<PlannerViewPreferenceService>();
        services.AddSingleton<TaskDetailsService>();
        services.AddSingleton<TaskNotesService>();
        services.AddSingleton<TaskChatService>();
        services.AddSingleton<TaskMoveService>();
        services.AddSingleton<DueDateService>();
        services.AddSingleton<BoardMemberService>();
        services.AddSingleton<TaskAssignmentService>();
        services.AddSingleton<TaskCreationService>();
        services.AddSingleton<TaskMetadataService>();
        services.AddSingleton<SelectedPlanTaskService>();
        services.AddSingleton<ChecklistService>();
        services.AddSingleton<ChecklistCompletionService>();
        return services;
    }

    public static void MapPlannerIntegration(this IEndpointRouteBuilder app)
    {
        // Keep existing widget URLs while new clients use a namespaced API.
        MapRoutes(app);
        MapRoutes(app.MapGroup("/api/planner"));
    }

    private static void MapRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/plans", async (PlannerBoardService boards, CancellationToken ct) =>
            Results.Ok(await boards.GetPlansAsync(ct)));
        app.MapGet("/settings", async (IPlannerSettingsStore settings, CancellationToken ct) =>
            Results.Ok(await settings.LoadSettingsAsync(ct)));
        app.MapPut("/settings", async (SettingsDto dto, IPlannerSettingsStore settings, CancellationToken ct) =>
        {
            await settings.SaveSettingsAsync(dto, ct);
            return Results.Ok(dto);
        });
        app.MapPut("/selected-plan", async (SelectedPlanRequest request, BoardSelectionService selection, CancellationToken ct) =>
            Results.Ok(await selection.SelectAsync(request.PlanId, ct)));
        app.MapGet("/display", async (PlannerCoordinator coordinator, CancellationToken ct) =>
        {
            var display = await coordinator.GetDisplayAsync(ct);
            return display is null ? Results.NoContent() : Results.Ok(display);
        });
        app.MapGet("/display/cached", async (PlannerCoordinator coordinator, CancellationToken ct) =>
        {
            var display = await coordinator.GetCachedDisplayAsync(ct);
            return display is null ? Results.NoContent() : Results.Ok(display);
        });
        app.MapGet("/view-preferences/{planId}", async (string planId,
            PlannerViewPreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.GetAsync(planId, ct)));
        app.MapPut("/view-preferences/{planId}", async (string planId, PlanViewPreferences request,
            PlannerViewPreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.SaveAsync(planId, request, ct)));
        app.MapGet("/members", async (BoardMemberService members, CancellationToken ct) =>
            Results.Ok(await members.GetAsync(ct)));
        app.MapPost("/tasks", async (CreateTaskRequest request, TaskCreationService creation, CancellationToken ct) =>
        {
            await creation.CreateAsync(request.Title, request.BucketId, request.Date, request.UserIds, ct,
                request.StartDate, request.Priority, request.LabelIds);
            return Results.NoContent();
        });
        app.MapPost("/tasks/{taskId}/complete", async (string taskId, TaskCompletionService completion, CancellationToken ct) =>
            Results.Ok(await completion.CompleteAsync(taskId, ct)));
        app.MapGet("/tasks/{taskId}/details", async (string taskId, TaskDetailsService details, CancellationToken ct) =>
            Results.Ok(await details.GetAsync(taskId, ct)));
        app.MapPut("/tasks/{taskId}/notes", async (string taskId, UpdateNotesRequest request,
            TaskNotesService notes, CancellationToken ct) =>
        {
            await notes.UpdateAsync(taskId, request.Description, ct);
            return Results.NoContent();
        });
        app.MapGet("/tasks/{taskId}/chat", async (string taskId, string? cursor,
            TaskChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.GetAsync(taskId, cursor, ct)));
        app.MapPost("/tasks/{taskId}/chat", async (string taskId, PostChatRequest request,
            TaskChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.PostAsync(taskId, request.Message, ct)));
        app.MapPut("/tasks/{taskId}/bucket", async (string taskId, MoveTaskRequest request, TaskMoveService moves, CancellationToken ct) =>
        {
            await moves.MoveAsync(taskId, request.BucketId, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/due-date", async (string taskId, DueDateRequest request, DueDateService dates, CancellationToken ct) =>
        {
            await dates.SetAsync(taskId, request.Date, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/title", async (string taskId, TitleRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetTitleAsync(taskId, request.Title, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/progress", async (string taskId, ProgressRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetProgressAsync(taskId, request.Progress, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/priority", async (string taskId, PriorityRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetPriorityAsync(taskId, request.Priority, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/start-date", async (string taskId, StartDateRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetStartDateAsync(taskId, request.Date, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/labels", async (string taskId, LabelsRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetLabelsAsync(taskId, request.LabelIds, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/assignments", async (string taskId, AssignmentsRequest request,
            TaskAssignmentService assignments, CancellationToken ct) =>
        {
            await assignments.SetAsync(taskId, request.UserIds, ct);
            return Results.NoContent();
        });
        app.MapPost("/tasks/{taskId}/checklist", async (string taskId, ChecklistTitleRequest request,
            ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.AddAsync(taskId, request.Title, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/checklist/{itemId}", async (string taskId, string itemId,
            ChecklistTitleRequest request, ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.RenameAsync(taskId, itemId, request.Title, ct);
            return Results.NoContent();
        });
        app.MapDelete("/tasks/{taskId}/checklist/{itemId}", async (string taskId, string itemId,
            ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.DeleteAsync(taskId, itemId, ct);
            return Results.NoContent();
        });
        app.MapPut("/tasks/{taskId}/checklist/{itemId}/position", async (string taskId, string itemId,
            ChecklistPositionRequest request, ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.MoveAsync(taskId, itemId, request.Direction, ct);
            return Results.NoContent();
        });
        app.MapPost("/tasks/{taskId}/checklist/{itemId}/complete", async (string taskId, string itemId,
            ChecklistCompletionService completion, CancellationToken ct) =>
        {
            await completion.CompleteAsync(taskId, itemId, ct);
            return Results.NoContent();
        });
    }
}
