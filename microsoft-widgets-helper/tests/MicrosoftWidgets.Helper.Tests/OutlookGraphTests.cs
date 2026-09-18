using System.Net;
using System.Text;
using PlannerEdge.Helper.Outlook;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookGraphTests
{
    [Fact]
    public async Task RetryAfterIsHonoredWithoutUnboundedWaiting()
    {
        var retry = new OutlookRetryHandler(TimeSpan.Zero);
        Assert.Empty(await new OutlookGraphClient(new HttpClient(retry), new OutlookTokens()).GetCollectionAsync("me/calendars", default));
        Assert.Equal(2, retry.Count);
        var longRetry = new OutlookRetryHandler(TimeSpan.FromMinutes(2));
        var error = await Assert.ThrowsAsync<OutlookException>(() => new OutlookGraphClient(new HttpClient(longRetry), new OutlookTokens()).GetCollectionAsync("me/calendars", default));
        Assert.Equal("throttled", error.Code);
        Assert.Equal(1, longRetry.Count);
    }
    [Fact]
    public async Task OversizedGraphResponseIsRejected()
    {
        var handler = new OutlookHandler((_, _) => "{\"value\":[],\"padding\":\"" + new string('x', 8 * 1024 * 1024) + "\"}");
        await Assert.ThrowsAsync<OutlookException>(() => new OutlookGraphClient(new HttpClient(handler), new OutlookTokens()).GetCollectionAsync("me/calendars", default));
    }
    [Fact]
    public async Task ScopeBundleAndPagedRequestsAreReadOnly()
    {
        Assert.Equal(new[] { "User.Read", "Calendars.Read.Shared", "MailboxSettings.Read", "Group.Read.All" }, OutlookScopes.All);
        var handler = new OutlookHandler((uri, _) => uri.Query.Contains("skiptoken")
            ? "{\"value\":[{\"id\":\"two\"}]}"
            : "{\"value\":[{\"id\":\"one\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars?$skiptoken=two\"}");
        var graph = new OutlookGraphClient(new HttpClient(handler), new OutlookTokens());
        Assert.Equal(2, (await graph.GetCollectionAsync("me/calendars", default)).Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("https://evil.example/v1.0/me/calendars")]
    [InlineData("https://graph.microsoft.com/v1.0/me/messages")]
    [InlineData("https://graph.microsoft.com:444/v1.0/me/calendars")]
    public async Task RejectsUntrustedNextLinks(string next)
    {
        var handler = new OutlookHandler((_, _) => "{\"value\":[],\"@odata.nextLink\":\"" + next + "\"}");
        var graph = new OutlookGraphClient(new HttpClient(handler), new OutlookTokens());
        await Assert.ThrowsAsync<OutlookException>(() => graph.GetCollectionAsync("me/calendars", default));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task IncompletePaginationNeverReturnsPartialSnapshot()
    {
        var handler = new OutlookHandler((_, n) => n == 1
            ? "{\"value\":[{\"id\":\"one\"}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/me/calendars?$skiptoken=two\"}"
            : throw new HttpRequestException());
        await Assert.ThrowsAsync<OutlookException>(() => new OutlookGraphClient(new HttpClient(handler), new OutlookTokens()).GetCollectionAsync("me/calendars", default));
    }
}

internal sealed class OutlookRetryHandler(TimeSpan delay) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        var response = new HttpResponseMessage(++Count == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) { Content = new StringContent("{\"value\":[]}") };
        response.Headers.RetryAfter = new(delay);
        return Task.FromResult(response);
    }
}

internal sealed class OutlookTokens : IOutlookTokenProvider
{
    public string Account { get; set; } = "account-a";
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult("test-token");
    public Task<string> GetAccountKeyAsync(CancellationToken ct) => Task.FromResult(Account);
}

internal sealed class OutlookHandler(Func<Uri, int, string> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add((request.Method, request.RequestUri!));
        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(respond(request.RequestUri!, Requests.Count), Encoding.UTF8, "application/json")
        });
    }
}
