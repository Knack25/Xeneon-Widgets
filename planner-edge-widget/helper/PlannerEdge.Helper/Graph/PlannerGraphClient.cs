using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlannerEdge.Helper.Graph;

public interface IGraphTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class PlannerGraphClient(HttpClient httpClient, IGraphTokenProvider tokenProvider) : IPlannerGraphClient
{
    public async Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = new List<GraphGroup>();
        await foreach (var item in GetCollectionAsync("me/memberOf/microsoft.graph.group?$select=id,displayName", cancellationToken))
            groups.Add(new GraphGroup(item.GetProperty("id").GetString()!, item.GetProperty("displayName").GetString() ?? "Unnamed group"));
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
            buckets.Add(new GraphBucket(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString() ?? "Unnamed bucket", planId));
        return buckets;
    }

    public async Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken cancellationToken)
    {
        var tasks = new List<GraphTask>();
        await foreach (var item in GetCollectionAsync($"planner/plans/{Uri.EscapeDataString(planId)}/tasks", cancellationToken))
            tasks.Add(ToTask(item));
        return tasks;
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

    public async Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"planner/tasks/{Uri.EscapeDataString(taskId)}");
        request.Headers.IfMatch.ParseAdd(etag);
        request.Content = new StringContent("{\"percentComplete\":100}", Encoding.UTF8, "application/json");
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
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokenProvider.GetAccessTokenAsync(cancellationToken));
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var status = response.StatusCode;
            response.Dispose();
            throw new GraphApiException(status, body);
        }
    }

    private static GraphTask ToTask(JsonElement item)
    {
        var assignments = item.TryGetProperty("assignments", out var assigned)
            ? assigned.EnumerateObject().Select(property => property.Name).ToList()
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
            assignments);
    }
}
