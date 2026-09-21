using System.Net;
using System.Text;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class TaskChatService(
    IPlannerGraphClient graphClient,
    IPlannerSettingsStore settingsStore,
    TaskDetailsService taskDetails)
{
    private const string PermissionMessage = "Enable task chat to read and post Planner comments.";

    public async Task<TaskChatResponse> GetAsync(string taskId, string? cursor, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(taskId, cancellationToken);
        if (string.IsNullOrWhiteSpace(context.Task.ConversationThreadId))
        {
            if (!string.IsNullOrWhiteSpace(cursor))
                throw new ArgumentException("The conversation cursor is no longer valid.", nameof(cursor));
            return new TaskChatResponse("available", []);
        }

        var continuation = DecodeCursor(cursor, context.GroupId, context.Task.ConversationThreadId);
        try
        {
            return await LoadAsync(context.GroupId, context.Task.ConversationThreadId, continuation, cancellationToken);
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
        var threadId = context.Task.ConversationThreadId;
        if (string.IsNullOrWhiteSpace(threadId))
        {
            try
            {
                threadId = await graphClient.CreateConversationThreadAsync(
                    context.GroupId, context.Task.Title, message, cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                throw ConversationPermissionRequired();
            }

            await graphClient.SetConversationThreadAsync(
                context.Task.Id, threadId, context.Task.ETag, cancellationToken);
            taskDetails.Invalidate(taskId);
        }
        else
        {
            try
            {
                await graphClient.ReplyToConversationAsync(context.GroupId, threadId, message, cancellationToken);
            }
            catch (MsalUiRequiredException)
            {
                throw ConversationPermissionRequired();
            }
        }

        try
        {
            return await LoadAsync(context.GroupId, threadId, null, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            throw ConversationPermissionRequired();
        }
    }

    private async Task<(GraphTask Task, string GroupId)> ResolveAsync(string taskId,
        CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId))
            throw new ArgumentException("Choose a board first.");

        var task = await graphClient.GetTaskAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("Planner task was not found.");
        if (!task.PlanId.Equals(settings.SelectedPlanId, StringComparison.Ordinal))
            throw new ArgumentException("This task is not on the selected board.", nameof(taskId));

        var plan = (await graphClient.GetMyPlansAsync(cancellationToken))
            .SingleOrDefault(candidate => candidate.Id == settings.SelectedPlanId);
        if (string.IsNullOrWhiteSpace(plan?.GroupId))
            throw new InvalidOperationException("The selected Planner board is unavailable.");
        return (task, plan.GroupId);
    }

    private async Task<TaskChatResponse> LoadAsync(string groupId, string threadId, Uri? continuation,
        CancellationToken cancellationToken)
    {
        var page = await graphClient.GetConversationPostsAsync(groupId, threadId, continuation, cancellationToken);
        var messages = page.Posts
            .Select(post => new TaskChatMessage(post.Id, post.Author, post.CreatedAt,
                ConversationText.ToPlainText(post.Body)))
            .OrderBy(message => message.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(message => message.Id, StringComparer.Ordinal)
            .ToList();
        return new TaskChatResponse("available", messages,
            page.NextLink is null ? null : EncodeCursor(page.NextLink));
    }

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
