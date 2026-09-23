using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Planner;

namespace PlannerEdge.Helper.Graph;


public sealed class PlannerGraphClient : IPlannerGraphClient
{
    private readonly HttpClient httpClient;
    private readonly IGraphTokenProvider tokenProvider;
    private readonly MicrosoftAccountState? accountState;
    private readonly PlannerDataAccessGate dataAccess;
    private readonly ConcurrentDictionary<string, OrderHintEntry> orderHints = new();
    private readonly SemaphoreSlim formatGate = new(4);

    [ActivatorUtilitiesConstructor]
    public PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider,
        MicrosoftAccountState accountState, PlannerDataAccessGate dataAccess)
    {
        this.httpClient = httpClient;
        this.tokenProvider = tokenProvider;
        this.accountState = accountState;
        this.dataAccess = dataAccess;
    }

    internal PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider,
        MicrosoftAccountState accountState) : this(httpClient, tokenProvider, accountState,
        new PlannerDataAccessGate()) { }

    internal PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider)
        : this(httpClient, tokenProvider, new PlannerDataAccessGate()) { }

    private PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider,
        PlannerDataAccessGate dataAccess)
    {
        this.httpClient = httpClient;
        this.tokenProvider = tokenProvider;
        this.dataAccess = dataAccess;
    }

    public async Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "me?$select=id"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId, CancellationToken cancellationToken)
    {
        var members = new List<GraphMember>();
        var token = await tokenProvider.GetGroupMemberTokenAsync(cancellationToken);
        string? next = $"groups/{Uri.EscapeDataString(groupId)}/members/microsoft.graph.user?$count=true&$select=id,displayName";
        while (next is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("ConsistencyLevel", "eventual");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new GraphApiException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            foreach (var member in document.RootElement.GetProperty("value").EnumerateArray())
                members.Add(new GraphMember(member.GetProperty("id").GetString()!, member.GetProperty("displayName").GetString() ?? "Unnamed member"));
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return members;
    }

    public async Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken)
    {
        var plans = new List<GraphPlan>();
        await foreach (var item in GetCollectionAsync("me/planner/plans", cancellationToken))
            plans.Add(new GraphPlan(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString() ?? "Untitled plan",
                item.TryGetProperty("owner", out var owner) ? owner.GetString() ?? string.Empty : string.Empty, null));
        return plans;
    }

    public async Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = new List<GraphGroup>();
        await foreach (var item in GetCollectionAsync("me/memberOf/microsoft.graph.group?$select=id,displayName", cancellationToken))
            groups.Add(new GraphGroup(item.GetProperty("id").GetString()!, item.GetProperty("displayName").GetString() ?? "Planner"));
        return groups;
    }

    public async Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        var plans = new List<GraphPlan>();
        await foreach (var item in GetCollectionAsync($"groups/{Uri.EscapeDataString(groupId)}/planner/plans", cancellationToken))
            plans.Add(new GraphPlan(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString() ?? "Untitled plan", groupId, null));
        return plans;
    }

    public async Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken cancellationToken)
    {
        var buckets = new List<GraphBucket>();
        await foreach (var item in GetCollectionAsync($"planner/plans/{Uri.EscapeDataString(planId)}/buckets", cancellationToken))
            buckets.Add(new GraphBucket(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString() ?? "Unnamed bucket", planId,
                item.TryGetProperty("orderHint", out var hint) ? hint.GetString() : null));
        return buckets;
    }

    public async Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken)
    {
        var lease = accountState is null ? (AccountLease?)null : await accountState.GetAsync(cancellationToken);
        var tasks = new List<GraphTask>();
        await foreach (var item in GetCollectionAsync($"planner/plans/{Uri.EscapeDataString(planId)}/tasks", cancellationToken))
            tasks.Add(ToTask(item));
        return await Task.WhenAll(tasks.Select(async task => task with
        {
            BucketOrderHint = await GetBucketOrderHintAsync(task.Id, lease, cancellationToken)
        }));
    }

    public async Task<IReadOnlyList<GraphPlanLabel>> GetPlanLabelsAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"planner/plans/{Uri.EscapeDataString(planId)}/details"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("categoryDescriptions", out var descriptions)
            || descriptions.ValueKind != JsonValueKind.Object)
            return [];

        var labels = new List<GraphPlanLabel>();
        for (var index = 1; index <= 25; index++)
        {
            var id = $"category{index}";
            if (!descriptions.TryGetProperty(id, out var description)
                || description.ValueKind != JsonValueKind.String)
                continue;
            var name = description.GetString();
            if (!string.IsNullOrWhiteSpace(name)) labels.Add(new GraphPlanLabel(id, name));
        }
        return labels;
    }

    private async Task<string?> GetBucketOrderHintAsync(string taskId, AccountLease? lease,
        CancellationToken cancellationToken)
    {
        var ticket = dataAccess.CaptureTicket();
        if (orderHints.TryGetValue(taskId, out var previous) &&
            (previous.Lease != lease || previous.Ticket != ticket))
            orderHints.TryRemove(new KeyValuePair<string, OrderHintEntry>(taskId, previous));
        if (orderHints.TryGetValue(taskId, out var cached) && cached.Lease == lease && cached.Ticket == ticket &&
            cached.Expires > DateTimeOffset.UtcNow)
            return cached.Hint;
        await formatGate.WaitAsync(cancellationToken);
        try
        {
            if (orderHints.TryGetValue(taskId, out cached) && cached.Lease == lease && cached.Ticket == ticket &&
                cached.Expires > DateTimeOffset.UtcNow)
                return cached.Hint;
            try
            {
                using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
                    $"planner/tasks/{Uri.EscapeDataString(taskId)}/bucketTaskBoardFormat"), cancellationToken);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var hint = document.RootElement.TryGetProperty("orderHint", out var value) ? value.GetString() : null;
                await PublishOrderHintAsync(taskId, lease, ticket, hint, TimeSpan.FromMinutes(5), cancellationToken);
                return hint;
            }
            catch (GraphApiException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw;
            }
            catch (Exception error) when (error is GraphApiException or HttpRequestException or System.Text.Json.JsonException)
            {
                await PublishOrderHintAsync(taskId, lease, ticket, null, TimeSpan.FromMinutes(1), cancellationToken);
                return null;
            }
        }
        finally { formatGate.Release(); }
    }

    public async Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"planner/tasks/{Uri.EscapeDataString(taskId)}"), cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return ToTask(document.RootElement);
        }
        catch (GraphApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"users/{Uri.EscapeDataString(userId)}?$select=displayName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokenProvider.GetBasicUserTokenAsync(cancellationToken));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GraphApiException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("displayName", out var name) ? name.GetString() : null;
    }

    public async Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"planner/tasks/{Uri.EscapeDataString(taskId)}/details"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var checklist = new List<GraphChecklistItem>();
        if (root.TryGetProperty("checklist", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var item = entry.Value;
                checklist.Add(new GraphChecklistItem(entry.Name,
                    item.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("isChecked", out var checkedValue) && checkedValue.GetBoolean(),
                    item.TryGetProperty("orderHint", out var hint) ? hint.GetString() : null));
            }
        }
        return new GraphTaskDetails(root.TryGetProperty("@odata.etag", out var etag) ? etag.GetString() ?? string.Empty : string.Empty,
            PlannerOrderHints.InCanonicalOrder(checklist),
            root.TryGetProperty("description", out var description) ? description.GetString() : null);
    }

    public async Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken cancellationToken)
        => await PatchChecklistAsync(taskId, itemId, new GraphChecklistPatch(IsChecked: true), etag,
            cancellationToken);

    public async Task PatchChecklistAsync(string taskId, string itemId, GraphChecklistPatch? patch, string etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"planner/tasks/{Uri.EscapeDataString(taskId)}/details");
        request.Headers.IfMatch.ParseAdd(etag);
        object? item = null;
        if (patch is not null)
        {
            var fields = new Dictionary<string, object>
            {
                ["@odata.type"] = "microsoft.graph.plannerChecklistItem"
            };
            if (patch.Title is not null) fields["title"] = patch.Title;
            if (patch.OrderHint is not null) fields["orderHint"] = patch.OrderHint;
            if (patch.IsChecked is not null) fields["isChecked"] = patch.IsChecked;
            item = fields;
        }
        var body = new Dictionary<string, object?>
        {
            ["checklist"] = new Dictionary<string, object?> { [itemId] = item }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task UpdateTaskDescriptionAsync(string taskId, string description, string etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"planner/tasks/{Uri.EscapeDataString(taskId)}/details");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent(JsonSerializer.Serialize(new { description }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent("{\"percentComplete\":100}", Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task MoveTaskAsync(string taskId, string bucketId, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent(JsonSerializer.Serialize(new { bucketId }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
        orderHints.TryRemove(taskId, out _);
    }

    public async Task SetDueDateAsync(string taskId, DateTimeOffset? dueDate, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent(JsonSerializer.Serialize(new { dueDateTime = dueDate }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task SetAssignmentsAsync(string taskId, IReadOnlyList<string> add, IReadOnlyList<string> remove,
        string etag, CancellationToken cancellationToken)
    {
        var changes = new Dictionary<string, object?>();
        foreach (var userId in remove) changes[userId] = null;
        foreach (var userId in add) changes[userId] = new Dictionary<string, string>
        {
            ["@odata.type"] = "#microsoft.graph.plannerAssignment", ["orderHint"] = " !"
        };
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent(JsonSerializer.Serialize(new { assignments = changes }), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    async Task IPlannerGraphClient.UpdateTaskAsync(string taskId, GraphTaskUpdate update, string etag,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>();
        if (update.Title is not null) body["title"] = update.Title;
        if (update.PercentComplete is not null) body["percentComplete"] = update.PercentComplete;
        if (update.Priority is not null) body["priority"] = update.Priority;
        if (update.ClearStartDate) body["startDateTime"] = null;
        else if (update.StartDateTime is not null) body["startDateTime"] = update.StartDateTime;
        if (update.AppliedCategories is not null) body["appliedCategories"] = update.AppliedCategories;

        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate,
        IReadOnlyList<string> assigneeIds, CancellationToken cancellationToken)
        => await CreateTaskAsync(planId, bucketId, title, dueDate, assigneeIds, null, 5, [], cancellationToken);

    public async Task CreateTaskAsync(string planId, string bucketId, string title, DateTimeOffset? dueDate,
        IReadOnlyList<string> assigneeIds, DateTimeOffset? startDate, int priority,
        IReadOnlyList<string> labelIds, CancellationToken cancellationToken)
    {
        var assignments = assigneeIds.ToDictionary(id => id, _ => new Dictionary<string, string>
        {
            ["@odata.type"] = "#microsoft.graph.plannerAssignment", ["orderHint"] = " !"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "planner/tasks");
        var body = new Dictionary<string, object> { ["planId"] = planId, ["bucketId"] = bucketId, ["title"] = title };
        if (dueDate is not null) body["dueDateTime"] = dueDate;
        if (startDate is not null) body["startDateTime"] = startDate;
        body["priority"] = priority;
        if (labelIds.Count > 0) body["appliedCategories"] = labelIds.ToDictionary(id => id, _ => true);
        if (assignments.Count > 0) body["assignments"] = assignments;
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, cancellationToken);
    }

    public async Task<GraphConversationPage> GetConversationPostsAsync(string groupId, string threadId,
        Uri? continuationUri, CancellationToken cancellationToken)
    {
        var escapedGroup = Uri.EscapeDataString(groupId);
        var escapedThread = Uri.EscapeDataString(threadId);
        var requestUri = continuationUri ?? new Uri(
            $"groups/{escapedGroup}/threads/{escapedThread}/posts?$select=id,body,sender,from,createdDateTime",
            UriKind.Relative);
        if (continuationUri is not null)
            ValidateConversationContinuation(continuationUri, escapedGroup, escapedThread);

        using var response = await SendConversationAsync(new HttpRequestMessage(HttpMethod.Get, requestUri), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var posts = new List<GraphConversationPost>();
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var hasBody = item.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.Object;
            var body = hasBody && bodyValue.TryGetProperty("content", out var content)
                ? content.GetString() ?? string.Empty : string.Empty;
            var contentType = hasBody && bodyValue.TryGetProperty("contentType", out var type)
                ? type.GetString() ?? string.Empty : string.Empty;
            var author = ReadConversationAuthor(item, "sender") ?? ReadConversationAuthor(item, "from");
            posts.Add(new GraphConversationPost(
                item.GetProperty("id").GetString()!,
                body,
                string.IsNullOrWhiteSpace(author) ? "Unknown" : author,
                item.TryGetProperty("createdDateTime", out var createdAt) && createdAt.ValueKind != JsonValueKind.Null
                    ? createdAt.GetDateTimeOffset()
                    : null,
                contentType));
        }

        Uri? nextLink = null;
        if (document.RootElement.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String)
        {
            var value = next.GetString();
            if (!string.IsNullOrWhiteSpace(value)) nextLink = new Uri(value, UriKind.Absolute);
        }
        return new GraphConversationPage(posts, nextLink);
    }

    public async Task EnsureConversationAccessAsync(CancellationToken cancellationToken) =>
        _ = await tokenProvider.GetConversationTokenAsync(cancellationToken);

    private static string? ReadConversationAuthor(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("emailAddress", out var email)
            || email.ValueKind != JsonValueKind.Object)
            return null;
        var name = email.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
        return !string.IsNullOrWhiteSpace(name) ? name
            : email.TryGetProperty("address", out var address) ? address.GetString() : null;
    }

    public async Task ReplyToConversationAsync(string groupId, string threadId, string message,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"groups/{Uri.EscapeDataString(groupId)}/threads/{Uri.EscapeDataString(threadId)}/reply");
        request.Content = JsonContent(new
        {
            post = new { body = new { contentType = "text", content = message } }
        });
        using var response = await SendConversationAsync(request, cancellationToken);
    }

    public async Task<string> CreateConversationThreadAsync(string groupId, string topic, string message,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"groups/{Uri.EscapeDataString(groupId)}/threads");
        request.Content = JsonContent(new
        {
            topic,
            posts = new[] { new { body = new { contentType = "text", content = message } } }
        });
        using var response = await SendConversationAsync(request, cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("id").GetString()
            ?? throw new JsonException("Microsoft Graph did not return a conversation thread ID.");
    }

    public async Task SetConversationThreadAsync(string taskId, string threadId, string etag,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch,
            $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = JsonContent(new { conversationThreadId = threadId });
        using var response = await SendAsync(request, cancellationToken);
    }

    private async IAsyncEnumerable<JsonElement> GetCollectionAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = path;
        while (next is not null)
        {
            using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, next), cancellationToken);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
                yield return item.Clone();
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var retryGet = request.Method == HttpMethod.Get;
        var uri = request.RequestUri;
        for (var attempt = 0; ; attempt++)
        {
            using var current = attempt == 0 ? request : new HttpRequestMessage(HttpMethod.Get, uri);
            current.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetAccessTokenAsync(cancellationToken));
            var response = await SendTransportAsync(current, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && retryGet && attempt < 2)
            {
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (delay <= TimeSpan.FromSeconds(10))
                {
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
            }
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = response.StatusCode;
            response.Dispose();
            throw new GraphApiException(status, body);
        }
    }

    private async Task<HttpResponseMessage> SendConversationAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            await tokenProvider.GetConversationTokenAsync(cancellationToken));
        var response = await SendTransportAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = response.StatusCode;
        response.Dispose();
        throw new GraphApiException(status, body);
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> SendTransportAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get || accountState is null)
            return httpClient.SendAsync(request, cancellationToken);
        return accountState.ExecuteBoundAsync(() => httpClient.SendAsync(request, cancellationToken), cancellationToken);
    }

    private void RequireCurrent(AccountLease? lease)
    {
        if (lease is { } value) accountState!.RequireCurrent(value);
    }

    private async Task PublishOrderHintAsync(string taskId, AccountLease? lease, PlannerDataTicket ticket, string? hint,
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var entry = new OrderHintEntry(lease, ticket, hint, DateTimeOffset.UtcNow.Add(lifetime));
        if (lease is null)
        {
            await dataAccess.ExecutePublicationAsync(ticket, () =>
            {
                orderHints[taskId] = entry;
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }

        await accountState!.ExecuteAuthorizedAsync(lease.Value, () =>
            dataAccess.ExecutePublicationAsync(ticket, () =>
            {
                orderHints[taskId] = entry;
                return Task.CompletedTask;
            }, cancellationToken), cancellationToken);
    }

    private sealed record OrderHintEntry(AccountLease? Lease, PlannerDataTicket Ticket, string? Hint,
        DateTimeOffset Expires);

    private static void ValidateConversationContinuation(Uri uri, string escapedGroup, string escapedThread)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals($"/v1.0/groups/{escapedGroup}/threads/{escapedThread}/posts",
                StringComparison.Ordinal))
            throw new ArgumentException("The conversation cursor is invalid.", nameof(uri));
    }

    private static GraphTask ToTask(JsonElement item)
    {
        var assignments = item.TryGetProperty("assignments", out var assigned)
            ? assigned.EnumerateObject().Select(property => property.Name).ToList()
            : [];
        var appliedCategories = item.TryGetProperty("appliedCategories", out var categories)
            && categories.ValueKind == JsonValueKind.Object
            ? categories.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.True)
                .Select(property => property.Name)
                .ToList()
            : [];
        return new GraphTask(
            item.GetProperty("id").GetString()!,
            item.GetProperty("title").GetString() ?? "Untitled task",
            item.GetProperty("planId").GetString() ?? string.Empty,
            item.TryGetProperty("bucketId", out var bucket) ? bucket.GetString() : null,
            item.TryGetProperty("dueDateTime", out var due) && due.ValueKind != JsonValueKind.Null ? due.GetDateTimeOffset() : null,
            item.TryGetProperty("priority", out var priority) && priority.ValueKind != JsonValueKind.Null ? priority.GetInt32() : null,
            item.TryGetProperty("percentComplete", out var percent) ? percent.GetInt32() : 0,
            item.TryGetProperty("@odata.etag", out var etag) ? etag.GetString() ?? string.Empty : string.Empty,
            assignments, null,
            item.TryGetProperty("startDateTime", out var start) && start.ValueKind != JsonValueKind.Null ? start.GetDateTimeOffset() : null,
            item.TryGetProperty("conversationThreadId", out var thread) ? thread.GetString() : null,
            appliedCategories);
    }
}
