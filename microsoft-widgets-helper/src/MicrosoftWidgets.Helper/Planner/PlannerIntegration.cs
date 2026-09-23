using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Outlook;
using Microsoft.AspNetCore.Http.Features;

namespace PlannerEdge.Helper.Planner;

public static class PlannerIntegration
{
    private const long MaxRequestBodySize = 16_384;
    private sealed record PlannerEndpointMetadata;
    private sealed record PlannerJsonBodyMetadata;

    public static IServiceCollection AddPlannerIntegration(this IServiceCollection services)
    {
        services.AddSingleton<PlannerDataAccessGate>();
        services.AddSingleton<IPlannerSettingsStore, PlannerSettingsStore>();
        services.AddSingleton<PlannerDataLifecycle>();
        services.AddHostedService(provider => provider.GetRequiredService<PlannerDataLifecycle>());
        services.AddHttpClient<IPlannerGraphClient, PlannerGraphClient>(client =>
            client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"))
            .ConfigurePrimaryHttpMessageHandler(PlannerGraphHttpHandlerFactory.Create);
        services.AddSingleton<PlannerBoardService>();
        services.AddSingleton<IBoardSelectionCoordinator, BoardSelectionCoordinator>();
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
        MapRoutes(app.MapGroup(""));
        MapRoutes(app.MapGroup("/api/planner"));
    }

    public static IApplicationBuilder UsePlannerRequestPolicy(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<PlannerEndpointMetadata>() is null)
        {
            await next(http);
            return;
        }

        if (http.Request.ContentLength > MaxRequestBodySize)
        {
            await WritePolicyErrorAsync(http, StatusCodes.Status413PayloadTooLarge, "The Planner request is too large.");
            return;
        }

        var bodyLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = MaxRequestBodySize;

        try
        {
            http.Response.Headers.CacheControl = "no-store";
            var authorization = await WidgetAuthorizationFilter.AuthorizeAsync(http, WidgetScope.Planner);
            if (authorization.Failure is not null)
            {
                await authorization.Failure.ExecuteAsync(http);
                return;
            }
            var lifecycle = http.RequestServices.GetRequiredService<PlannerDataLifecycle>();
            if (!lifecycle.ReadyForWork)
            {
                await Results.Json(new { error = new { code = "planner_recovery", message = "Planner data cleanup is still in progress." } },
                    statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(http);
                return;
            }

            if (endpoint.Metadata.GetMetadata<PlannerJsonBodyMetadata>() is not null && !http.Request.HasJsonContentType())
            {
                await WritePolicyErrorAsync(http, StatusCodes.Status415UnsupportedMediaType, "Send a JSON request.");
                return;
            }

            var state = http.RequestServices.GetRequiredService<MicrosoftAccountState>();
            using var binding = state.BindRequest(authorization.Lease!.Value);
            using var plannerBinding = lifecycle.BindOperation();
            http.Items[WidgetAuthorizationFilter.PlannerDataTicketKey] = lifecycle.CaptureTicket();
            http.Items[WidgetAuthorizationFilter.PreauthorizedLeaseKey] = authorization.Lease.Value;
            try { await next(http); }
            finally
            {
                http.Items.Remove(WidgetAuthorizationFilter.PreauthorizedLeaseKey);
                http.Items.Remove(WidgetAuthorizationFilter.PlannerDataTicketKey);
            }
        }
        catch (OutlookException ex)
        {
            await Results.Json(new { error = ex.Error }, statusCode: ex.StatusCode).ExecuteAsync(http);
        }
        catch (GraphApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await http.RequestServices.GetRequiredService<PlannerDataLifecycle>().PurgeAsync(CancellationToken.None);
            throw;
        }
    });

    private static Task WritePolicyErrorAsync(HttpContext http, int statusCode, string message) =>
        Results.Json(new { error = new { code = "invalid_request", message } }, statusCode: statusCode).ExecuteAsync(http);

    private static void MapRoutes(RouteGroupBuilder app)
    {
        app.WithMetadata(new WidgetTransportMetadata());
        app.WithMetadata(new PlannerEndpointMetadata());
        app.AddEndpointFilter(new WidgetAuthorizationFilter(WidgetScope.Planner));
        app.MapGet("/me", async (IPlannerGraphClient graph, CancellationToken ct) =>
            Results.Ok(new { userId = await graph.GetCurrentUserIdAsync(ct) }));
        app.MapGet("/plans", async (PlannerBoardService boards, CancellationToken ct) =>
            Results.Ok(await boards.GetPlansAsync(ct)));
        app.MapGet("/settings", async (IPlannerSettingsStore settings, CancellationToken ct) =>
            Results.Ok(await settings.LoadSettingsAsync(ct)));
        app.MapPut("/settings", async (SettingsDto dto, BoardSelectionService selection, CancellationToken ct) =>
            Results.Ok(await selection.SaveAsync(dto, ct))).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/selected-plan", async (SelectedPlanRequest request, BoardSelectionService selection, CancellationToken ct) =>
            Results.Ok(await selection.SelectAsync(request.PlanId, ct))).WithMetadata(new PlannerJsonBodyMetadata());
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
            Results.Ok(await preferences.SaveAsync(planId, request, ct))).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapGet("/members", async (BoardMemberService members, CancellationToken ct) =>
            Results.Ok(await members.GetAsync(ct)));
        app.MapPost("/tasks", async (CreateTaskRequest request, TaskCreationService creation, CancellationToken ct) =>
        {
            await creation.CreateAsync(request.Title, request.BucketId, request.Date, request.UserIds, ct,
                request.StartDate, request.Priority, request.LabelIds);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPost("/tasks/{taskId}/complete", async (string taskId, TaskCompletionService completion, CancellationToken ct) =>
            Results.Ok(await completion.CompleteAsync(taskId, ct)));
        app.MapGet("/tasks/{taskId}/details", async (string taskId, TaskDetailsService details, CancellationToken ct) =>
            Results.Ok(await details.GetAsync(taskId, ct)));
        app.MapPut("/tasks/{taskId}/notes", async (string taskId, UpdateNotesRequest request,
            TaskNotesService notes, CancellationToken ct) =>
        {
            await notes.UpdateAsync(taskId, request.Description, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapGet("/tasks/{taskId}/chat", async (string taskId, string? cursor,
            TaskChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.GetAsync(taskId, cursor, ct)));
        app.MapPost("/tasks/{taskId}/chat", async (string taskId, PostChatRequest request,
            TaskChatService chat, CancellationToken ct) =>
            Results.Ok(await chat.PostAsync(taskId, request.Message, ct))).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/bucket", async (string taskId, MoveTaskRequest request, TaskMoveService moves, CancellationToken ct) =>
        {
            await moves.MoveAsync(taskId, request.BucketId, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/due-date", async (string taskId, DueDateRequest request, DueDateService dates, CancellationToken ct) =>
        {
            await dates.SetAsync(taskId, request.Date, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/title", async (string taskId, TitleRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetTitleAsync(taskId, request.Title, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/progress", async (string taskId, ProgressRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetProgressAsync(taskId, request.Progress, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/priority", async (string taskId, PriorityRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetPriorityAsync(taskId, request.Priority, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/start-date", async (string taskId, StartDateRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetStartDateAsync(taskId, request.Date, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/labels", async (string taskId, LabelsRequest request,
            TaskMetadataService metadata, CancellationToken ct) =>
        {
            await metadata.SetLabelsAsync(taskId, request.LabelIds, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/assignments", async (string taskId, AssignmentsRequest request,
            TaskAssignmentService assignments, CancellationToken ct) =>
        {
            await assignments.SetAsync(taskId, request.UserIds, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPost("/tasks/{taskId}/checklist", async (string taskId, ChecklistTitleRequest request,
            ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.AddAsync(taskId, request.Title, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPut("/tasks/{taskId}/checklist/{itemId}", async (string taskId, string itemId,
            ChecklistTitleRequest request, ChecklistService checklist, CancellationToken ct) =>
        {
            await checklist.RenameAsync(taskId, itemId, request.Title, ct);
            return Results.NoContent();
        }).WithMetadata(new PlannerJsonBodyMetadata());
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
        }).WithMetadata(new PlannerJsonBodyMetadata());
        app.MapPost("/tasks/{taskId}/checklist/{itemId}/complete", async (string taskId, string itemId,
            ChecklistCompletionService completion, CancellationToken ct) =>
        {
            await completion.CompleteAsync(taskId, itemId, ct);
            return Results.NoContent();
        });
    }
}
