using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlannerEdge.Helper.Outlook;

public sealed class OutlookGraphClient(HttpClient http, IOutlookTokenProvider tokens)
{
    private static readonly Uri Root = new("https://graph.microsoft.com/v1.0/");
    private readonly SemaphoreSlim concurrency = new(4, 4);
    private static readonly Regex AllowedPath = new(@"^/v1\.0/(me/(calendars(?:/[^/]+(?:/(?:calendarView|events/[^/]+))?)?|memberOf|mailboxSettings)|users/[^/]+/calendar(?:/(?:calendarView|events/[^/]+))?|groups/[^/]+/(calendar(?:/events/[^/]+)?|calendarView))$", RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<JsonElement>> GetCollectionAsync(string route, CancellationToken ct)
    {
        var first = Validate(new Uri(Root, route));
        var next = first;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<JsonElement>();
        for (var page = 0; page < 200; page++)
        {
            if (!seen.Add(next.AbsoluteUri)) throw InvalidResponse();
            var json = await ReadAsync(next, ct);
            if (!json.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array) throw InvalidResponse();
            foreach (var value in values.EnumerateArray()) items.Add(value.Clone());
            if (items.Count > 50000) throw InvalidResponse();
            if (!json.TryGetProperty("@odata.nextLink", out var link) || link.ValueKind == JsonValueKind.Null) return items;
            if (!Uri.TryCreate(link.GetString(), UriKind.Absolute, out next) || next.AbsolutePath != first.AbsolutePath) throw InvalidResponse();
            Validate(next);
        }
        throw InvalidResponse();
    }

    public Task<JsonElement> GetAsync(string route, CancellationToken ct) => ReadAsync(Validate(new Uri(Root, route)), ct);

    private static Uri Validate(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Host != Root.Host || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !AllowedPath.IsMatch(uri.AbsolutePath))
            throw InvalidResponse();
        return uri;
    }

    private async Task<JsonElement> ReadAsync(Uri uri, CancellationToken ct)
    {
        await concurrency.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            var token = await tokens.GetTokenAsync(ct);
            for (var attempt = 0; ; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.Add(new("application/json"));
                request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
                    if (delay > TimeSpan.FromSeconds(20)) throw Failure(response.StatusCode);
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
                const int maxBytes = 8 * 1024 * 1024;
                if (response.Content.Headers.ContentLength > maxBytes) throw InvalidResponse();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bounded = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (bounded.Length + read > maxBytes) throw InvalidResponse();
                    bounded.Write(buffer, 0, read);
                }
                bounded.Position = 0;
                using var document = await JsonDocument.ParseAsync(bounded, cancellationToken: timeout.Token);
                return document.RootElement.Clone();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new OutlookException("offline", "Outlook did not respond in time."); }
        catch (HttpRequestException) { throw new OutlookException("offline", "Outlook is currently unreachable."); }
        catch (JsonException) { throw InvalidResponse(); }
        finally { concurrency.Release(); }
    }

    private static OutlookException InvalidResponse() => new("invalid_response", "Outlook returned an unsupported response.");
    private static OutlookException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new("sign_in_required", "Reconnect Outlook to refresh Microsoft authorization.", 401),
        HttpStatusCode.Forbidden => new("source_access_denied", "This calendar is not accessible with the current sharing or membership permissions.", 403),
        HttpStatusCode.NotFound => new("source_not_found", "This calendar or event is no longer available.", 404),
        HttpStatusCode.TooManyRequests => new("throttled", "Microsoft is limiting requests. Try again shortly.", 429),
        _ => new("offline", "Outlook is currently unavailable.")
    };
}
