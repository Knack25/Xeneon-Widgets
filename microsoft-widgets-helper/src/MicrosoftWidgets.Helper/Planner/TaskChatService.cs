using System.Net;
using System.Text;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskChatService(
    IPlannerGraphClient graphClient,
    SelectedPlanTaskService selectedPlanTasks,
    TaskDetailsService taskDetails,
    PlannerDataLifecycle lifecycle)
{
    private const string PermissionMessage = "Enable task chat to read and post Planner comments.";
    private const string AttachmentPendingMessage =
        "Your comment was created, but Planner could not attach the conversation. Refresh task details before posting again.";

    public async Task<TaskChatResponse> GetAsync(string taskId, string? cursor, CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var ticket = lifecycle.CaptureTicket();
        var context = await ResolveAsync(taskId, cancellationToken);
        if (string.IsNullOrWhiteSpace(context.Selected.Task.ConversationThreadId))
        {
            if (!string.IsNullOrWhiteSpace(cursor))
                throw new ArgumentException("The conversation cursor is no longer valid.", nameof(cursor));
            try
            {
                await selectedPlanTasks.RunAsync(context.Selected,
                    ct => graphClient.EnsureConversationAccessAsync(ct), cancellationToken);
                var response = new TaskChatResponse("available", []);
                lifecycle.RequireCurrent(ticket);
                return response;
            }
            catch (MsalUiRequiredException)
            {
                return new TaskChatResponse("interaction_required", [], Message: PermissionMessage);
            }
        }

        var continuation = DecodeCursor(cursor, context.GroupId, context.Selected.Task.ConversationThreadId);
        try
        {
            var response = await LoadAsync(context.Selected, context.GroupId,
                context.Selected.Task.ConversationThreadId, continuation, cancellationToken);
            lifecycle.RequireCurrent(ticket);
            return response;
        }
        catch (MsalUiRequiredException)
        {
            return new TaskChatResponse("interaction_required", [], Message: PermissionMessage);
        }
    }

    public async Task<TaskChatResponse> PostAsync(string taskId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Enter a comment.", nameof(message));
        if (message.Length > 4000)
            throw new ArgumentException("Task comments cannot exceed 4000 characters.", nameof(message));

        var context = await ResolveAsync(taskId, cancellationToken);
        var threadId = context.Selected.Task.ConversationThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            try
            {
                threadId = await selectedPlanTasks.RunAsync(context.Selected,
                    ct => graphClient.CreateConversationThreadAsync(
                        context.GroupId, context.Selected.Task.Title, message, ct), cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                throw ConversationPermissionRequired();
            }

            if (!await AttachCreatedThreadAsync(context.Selected, threadId, cancellationToken))
                return new TaskChatResponse("attachment_pending", [], Message: AttachmentPendingMessage);
        }
        else
        {
            try
            {
                await selectedPlanTasks.RunAsync(context.Selected,
                    ct => graphClient.ReplyToConversationAsync(context.GroupId, threadId, message, ct),
                    cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                throw ConversationPermissionRequired();
            }
        }

        try
        {
            return await LoadAsync(context.Selected, context.GroupId, threadId, null, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            throw ConversationPermissionRequired();
        }
    }

    private async Task<(SelectedPlanTask Selected, string GroupId)> ResolveAsync(string taskId,
        CancellationToken cancellationToken)
    {
        var selected = await selectedPlanTasks.GetBoundAsync(taskId, cancellationToken);

        var plan = (await selectedPlanTasks.RunAsync(selected,
                ct => graphClient.GetMyPlansAsync(ct), cancellationToken))
            .SingleOrDefault(candidate => candidate.Id == selected.Task.PlanId);
        if (string.IsNullOrWhiteSpace(plan?.GroupId))
            throw new InvalidOperationException("The selected Planner board is unavailable.");
        return (selected, plan.GroupId);
    }

    private async Task<TaskChatResponse> LoadAsync(SelectedPlanTask selected, string groupId, string threadId,
        Uri? continuation,
        CancellationToken cancellationToken)
    {
        var page = await selectedPlanTasks.RunAsync(selected,
            ct => graphClient.GetConversationPostsAsync(groupId, threadId, continuation, ct), cancellationToken);
        var messages = page.Posts
            .Select(post => new TaskChatMessage(post.Id, post.Author, post.CreatedAt,
                post.ContentType.Equals("html", StringComparison.OrdinalIgnoreCase)
                    ? ConversationText.ToPlainText(post.Body)
                    : post.Body))
            .OrderBy(message => message.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(message => message.Id, StringComparer.Ordinal)
            .ToList();
        return new TaskChatResponse("available", messages,
            page.NextLink is null ? null : EncodeCursor(page.NextLink));
    }

    private async Task<bool> AttachCreatedThreadAsync(SelectedPlanTask selected, string threadId,
        CancellationToken cancellationToken)
    {
        var originalTask = selected.Task;
        try
        {
            await selectedPlanTasks.RunAsync(selected,
                ct => graphClient.SetConversationThreadAsync(originalTask.Id, threadId, originalTask.ETag, ct),
                cancellationToken);
            taskDetails.Invalidate(originalTask.Id);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException && !IsAuthorizationFailure(error))
        {
            var refreshed = await TryReloadTaskAsync(selected, cancellationToken);
            if (refreshed?.Task.ConversationThreadId == threadId)
            {
                taskDetails.Invalidate(originalTask.Id);
                return true;
            }
            if (refreshed is null || !string.IsNullOrWhiteSpace(refreshed.Task.ConversationThreadId))
                return false;

            try
            {
                await selectedPlanTasks.RunAsync(refreshed,
                    ct => graphClient.SetConversationThreadAsync(originalTask.Id, threadId, refreshed.Task.ETag, ct),
                    cancellationToken);
                taskDetails.Invalidate(originalTask.Id);
                return true;
            }
            catch (Exception retryError) when (retryError is not OperationCanceledException && !IsAuthorizationFailure(retryError))
            {
                var afterRetry = await TryReloadTaskAsync(selected, cancellationToken);
                if (afterRetry?.Task.ConversationThreadId != threadId) return false;
                taskDetails.Invalidate(originalTask.Id);
                return true;
            }
        }
    }

    private async Task<SelectedPlanTask?> TryReloadTaskAsync(SelectedPlanTask selected,
        CancellationToken cancellationToken)
    {
        try
        {
            return await selectedPlanTasks.RefreshAsync(selected, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException && !IsAuthorizationFailure(error))
        {
            return null;
        }
    }

    private static bool IsAuthorizationFailure(Exception error) => error is GraphApiException
        { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };

    private static string EncodeCursor(Uri uri) => Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.AbsoluteUri))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Uri? DecodeCursor(string? cursor, string groupId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)) throw new FormatException();
            var expectedPath = $"/v1.0/groups/{Uri.EscapeDataString(groupId)}/threads/{Uri.EscapeDataString(threadId)}/posts";
            if (uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.Equals(expectedPath, StringComparison.Ordinal))
                throw new FormatException();
            return uri;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            throw new ArgumentException("The conversation cursor is invalid.", nameof(cursor));
        }
    }

    private static GraphApiException ConversationPermissionRequired() =>
        new(HttpStatusCode.Forbidden, PermissionMessage);
}
