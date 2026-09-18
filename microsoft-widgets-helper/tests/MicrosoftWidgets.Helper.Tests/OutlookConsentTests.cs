using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PlannerEdge.Helper.Outlook;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookConsentTests
{
    [Fact]
    public async Task MissingConsentReturnsActionableStatusWithoutInvalidatingSetupSession()
    {
        await using var host = await OutlookTestHost.StartAsync(true, new ConsentRequiredTokens());
        var client = host.Client;
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var session = await client.GetFromJsonAsync<JsonElement>("api/outlook/session");
        client.DefaultRequestHeaders.Add("X-Outlook-Session", session.GetProperty("token").GetString());
        var response = await client.GetAsync("api/outlook/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("ready").GetBoolean());
        Assert.Equal("consent_required", status.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("api/outlook/connect", new { })).StatusCode);
        Assert.Empty(host.Handler.Requests);
    }

    private sealed class ConsentRequiredTokens : IOutlookTokenProvider
    {
        public Task<string> GetAccountKeyAsync(CancellationToken ct) => Task.FromResult("same-account");
        public Task<string> GetTokenAsync(CancellationToken ct) => throw new OutlookException("consent_required", "Connect Outlook to request permission.", 401);
    }
}
