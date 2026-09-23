using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Identity.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookEndpointTests
{
    [Theory]
    [InlineData("api/local-access/pairings/paired")]
    [InlineData("api/outlook/paired")]
    public async Task ManagementPairingResponsesFailClosedWhenAccountChangesBeforeSerialization(string path)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockResponse = false;
        await using var host = await OutlookTestHost.StartAsync(true, beforeResult: async _ =>
        {
            if (!blockResponse) return;
            entered.SetResult();
            await release.Task;
        });
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("sensitive-instance", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        await access.PollAsync(pending.Id, new string('x', 64), default);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        blockResponse = true;
        var responseTask = host.Client.GetAsync(path);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Services.GetRequiredService<MicrosoftAccountState>().InvalidateAsync(default);
        release.SetResult();
        var response = await responseTask;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("sensitive-instance", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("api/local-access/pairings/{0}/poll")]
    [InlineData("api/outlook/pairings/{0}/poll")]
    public async Task PublicPollResponsesFailClosedWhenAccountChangesBeforeSerialization(string route)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockResponse = false;
        await using var host = await OutlookTestHost.StartAsync(true, beforeResult: async _ =>
        {
            if (!blockResponse) return;
            entered.SetResult();
            await release.Task;
        });
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var secret = new string('x', 64);
        var pending = await access.CreatePairingAsync(new("native", secret), default);
        await access.ApproveAsync(pending.Id, default);
        var credential = (await access.PollAsync(pending.Id, secret, default)).Credential!;

        blockResponse = true;
        var responseTask = host.Client.PostAsJsonAsync(string.Format(route, pending.Id), new { requestSecret = secret });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Services.GetRequiredService<MicrosoftAccountState>().InvalidateAsync(default);
        release.SetResult();
        var response = await responseTask;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(credential, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BrowserOriginMustExactlyMatchHelperOriginForActualAndPreflightRequests()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        var credential = (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential!;
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, credential);
        host.Client.DefaultRequestHeaders.Add("Origin", "http://localhost:9999");

        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "api/outlook/calendars");
        preflight.Headers.Add("Origin", "http://localhost:9999");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", LocalAccessHeaders.Credential);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(preflight)).StatusCode);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    public async Task BrowserGetPreviewUsesFetchMetadataAndStillRequiresOwnerSession(bool owner, HttpStatusCode expected)
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        if (owner) host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        Assert.Equal(expected, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", "null");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"instanceId\":\"native\",\"requestSecret\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"scope\":\"other\",\"instanceId\":\"native\",\"requestSecret\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"scope\":500,\"instanceId\":\"native\",\"requestSecret\":\"0123456789abcdef0123456789abcdef\"}")]
    public async Task SharedPairingRequiresAnExplicitKnownScope(string json)
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var response = await host.Client.PostAsync("api/local-access/pairings", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SharedPairingBootstrapRejectsForeignOriginAndHost()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var request = new { scope = "outlook", instanceId = "native", requestSecret = new string('x', 64) };
        host.Client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("api/local-access/pairings", request)).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        host.Client.DefaultRequestHeaders.Host = "evil.example:" + host.Client.BaseAddress!.Port;
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("api/local-access/pairings", request)).StatusCode);
    }

    [Fact]
    public async Task NativePreflightAllowsScopedCredentialHeader()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        using var request = new HttpRequestMessage(HttpMethod.Options, "api/outlook/calendars");
        request.Headers.Add("Origin", "null");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "X-Microsoft-Widgets-Credential");
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("X-Microsoft-Widgets-Credential", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")));
    }

    [Fact]
    public async Task OwnerAndWidgetCredentialsCannotBeSubstitutedIntoOtherHeaders()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        var credential = (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential!;
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        foreach (var (header, value) in new[] { (LocalAccessHeaders.Owner, credential), (LocalAccessHeaders.Credential, host.OwnerSession), ("Authorization", "Bearer " + credential), ("X-Outlook-Session", host.OwnerSession) })
        {
            host.Client.DefaultRequestHeaders.Add(header, value);
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
            host.Client.DefaultRequestHeaders.Remove(header);
        }
    }

    [Fact]
    public async Task OwnerPreviewUsesExistingOwnerSessionWithoutIssuingOutlookSessions()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/outlook/session")).StatusCode);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        var session = await host.Client.GetFromJsonAsync<JsonElement>("api/outlook/session");
        Assert.Equal(host.OwnerSession, session.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
    }

    [Theory]
    [InlineData(null, true, HttpStatusCode.OK)]
    [InlineData("null", true, HttpStatusCode.OK)]
    [InlineData("file://", true, HttpStatusCode.OK)]
    [InlineData(null, false, HttpStatusCode.Unauthorized)]
    [InlineData("null", false, HttpStatusCode.Unauthorized)]
    [InlineData("https://evil.example", true, HttpStatusCode.Forbidden)]
    public async Task ScopedCredentialRequiresAuthenticationEvenForNativeOrigins(string? origin, bool valid, HttpStatusCode expected)
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        var credential = (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential!;
        if (origin is not null) host.Client.DefaultRequestHeaders.Add("Origin", origin);
        host.Client.DefaultRequestHeaders.Add("X-Microsoft-Widgets-Credential", valid ? credential : "invalid");
        Assert.Equal(expected, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
    }

    [Fact]
    public async Task SharedPairingRoutesExposeScopesAndRequireOwnerForManagement()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        var secret = new string('q', 64);
        var response = await host.Client.PostAsJsonAsync("api/local-access/pairings", new { scope = "planner", instanceId = "same", requestSecret = secret });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/local-access/pairings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync($"api/local-access/pairings/{id}/approve", new { })).StatusCode);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        var listed = await host.Client.GetFromJsonAsync<JsonElement>("api/local-access/pairings");
        Assert.Equal("planner", listed[0].GetProperty("scope").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PostAsJsonAsync($"api/local-access/pairings/{id}/approve", new { })).StatusCode);
        host.Client.DefaultRequestHeaders.Remove(LocalAccessHeaders.Owner);
        var poll = await (await host.Client.PostAsJsonAsync($"api/local-access/pairings/{id}/poll", new { requestSecret = secret })).Content.ReadFromJsonAsync<JsonElement>();
        host.Client.DefaultRequestHeaders.Add("X-Microsoft-Widgets-Credential", poll.GetProperty("credential").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/outlook/calendars")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("api/local-access/pairings/paired")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync("api/local-access/pairings/revoke", new { credentialId = "anything" })).StatusCode);
    }

    [Fact]
    public async Task ActualRoutesEnforceSetupBoundaryAndIssueNoDataToUnpairedClients()
    {
        await using var host = await OutlookTestHost.StartAsync();
        using var client = host.Client;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/outlook/calendars")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("api/outlook/view", new { calendarKeys = Array.Empty<string>(), start = "2026-09-01T00:00:00Z", end = "2026-09-08T00:00:00Z" })).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "null");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("api/outlook/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("api/outlook/pairings/unknown/approve", new { })).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        var session = await client.GetFromJsonAsync<JsonElement>("api/outlook/session");
        var status = await client.GetFromJsonAsync<JsonElement>("api/outlook/status");
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.False(status.GetProperty("signedIn").GetBoolean());
        Assert.Empty(host.Handler.Requests);
        client.DefaultRequestHeaders.Host = "evil.example:" + client.BaseAddress.Port;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("api/outlook/session")).StatusCode);
    }

    [Fact]
    public async Task ApprovedCredentialCanReadAndJoinKnownEventButCannotApprovePairings()
    {
        await using var host = await OutlookTestHost.StartAsync(true);
        using var setup = host.Client;
        setup.DefaultRequestHeaders.Add("Origin", setup.BaseAddress!.GetLeftPart(UriPartial.Authority));
        setup.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.OwnerSession);
        var session = await setup.GetFromJsonAsync<JsonElement>("api/outlook/session");
        using var native = new HttpClient { BaseAddress = setup.BaseAddress };
        native.DefaultRequestHeaders.Add("Origin", "null");
        var secret = new string('z', 64);
        var pending = await (await native.PostAsJsonAsync("api/outlook/pairings", new { instanceId = "native-1", requestSecret = secret })).Content.ReadFromJsonAsync<JsonElement>();
        var id = pending.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await setup.PostAsJsonAsync($"api/outlook/pairings/{id}/approve", new { })).StatusCode);
        var poll = await (await native.PostAsJsonAsync($"api/outlook/pairings/{id}/poll", new { requestSecret = secret })).Content.ReadFromJsonAsync<JsonElement>();
        native.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, poll.GetProperty("credential").GetString());
        foreach (var path in new[] { "api/outlook/Session", "api/OUTLOOK/session/", "api/outlook/Paired/", "api/outlook/Pairings" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await native.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await native.PostAsJsonAsync($"api/outlook/Pairings/{id}/Approve/", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await native.GetAsync("api/outlook/paired")).StatusCode);
        var calendars = await native.GetFromJsonAsync<CalendarDescriptor[]>("api/outlook/calendars");
        var key = Assert.Single(calendars!).Key;
        var view = await (await native.PostAsJsonAsync("api/outlook/view", new ViewRequest([key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"))).Content.ReadFromJsonAsync<CalendarViewResponse>();
        var graphRequests = host.Handler.Requests.Count;
        var cached = await (await native.PostAsJsonAsync("api/outlook/view/cached", new ViewRequest([key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"))).Content.ReadFromJsonAsync<CalendarViewResponse>();
        Assert.Equal(view!.Events, cached!.Events);
        Assert.Equal(graphRequests, host.Handler.Requests.Count);
        var request = new EventRequest(key, Assert.Single(view.Events).Reference);
        Assert.Equal(HttpStatusCode.NoContent, (await native.PostAsJsonAsync("api/outlook/join", request)).StatusCode);
        Assert.Single(host.Launcher.Uris);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await native.PostAsJsonAsync("api/outlook/join", request)).StatusCode);
        Assert.All(host.Handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task AccountSwitchBetweenAuthorizationAndHandlerCannotReadNewAccount()
    {
        var tokens = new OutlookSwitchingTokens();
        await using var host = await OutlookTestHost.StartAsync(true, tokens);
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        var credential = (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential;
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, credential);
        tokens.SwitchOnSecondRead = true;
        var response = await host.Client.GetAsync("api/outlook/calendars");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task AccountSwitchDuringEventResolutionPreventsMeetingLaunch()
    {
        var tokens = new OutlookTokens();
        var switchDuringDetails = false;
        await using var host = await OutlookTestHost.StartAsync(true, tokens, uri =>
        {
            if (switchDuringDetails && uri.AbsolutePath.EndsWith("/events/event")) tokens.Account = "account-b";
        });
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential);
        var calendar = Assert.Single((await host.Client.GetFromJsonAsync<CalendarDescriptor[]>("api/outlook/calendars"))!);
        var view = await (await host.Client.PostAsJsonAsync("api/outlook/view", new ViewRequest([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"))).Content.ReadFromJsonAsync<CalendarViewResponse>();
        switchDuringDetails = true;
        var response = await host.Client.PostAsJsonAsync("api/outlook/join", new EventRequest(calendar.Key, Assert.Single(view!.Events).Reference));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(host.Launcher.Uris);
    }

    [Fact]
    public async Task AccountSwitchDuringGraphReadPreventsResponseDelivery()
    {
        var tokens = new OutlookTokens();
        await using var host = await OutlookTestHost.StartAsync(true, tokens, uri =>
        {
            if (uri.AbsolutePath.EndsWith("/calendars")) tokens.Account = "account-b";
        });
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential);
        var response = await host.Client.GetAsync("api/outlook/calendars");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Calendar", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Account_transition_or_purge_waits_for_launch_but_not_response_backpressure(
        bool blockLaunch, bool purge)
    {
        var tokens = new OutlookTokens();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockWrite = false;
        await using var host = await OutlookTestHost.StartAsync(true, tokens, beforeRequest: http =>
        {
            if (blockWrite) http.Response.Body = new OutlookBlockedWriteStream(http.Response.Body, entered, release);
        });
        var access = host.Services.GetRequiredService<OutlookAccessService>();
        var pending = await access.CreatePairingAsync(new("native", new string('x', 64)), default);
        await access.ApproveAsync(pending.Id, default);
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Credential, (await access.PollAsync(pending.Id, new string('x', 64), default)).Credential);
        Task<HttpResponseMessage> response;
        if (blockLaunch)
        {
            var calendar = Assert.Single((await host.Client.GetFromJsonAsync<CalendarDescriptor[]>("api/outlook/calendars"))!);
            var view = await (await host.Client.PostAsJsonAsync("api/outlook/view", new ViewRequest([calendar.Key], "2026-09-01T00:00:00Z", "2026-09-08T00:00:00Z"))).Content.ReadFromJsonAsync<CalendarViewResponse>();
            host.Launcher.BeforeOpen = async () => { entered.TrySetResult(); await release.Task; };
            response = host.Client.PostAsJsonAsync("api/outlook/join", new EventRequest(calendar.Key, Assert.Single(view!.Events).Reference));
        }
        else
        {
            blockWrite = true;
            response = host.Client.GetAsync("api/outlook/calendars");
        }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var transitioned = false;
        var state = host.Services.GetRequiredService<MicrosoftAccountState>();
        Task transition = purge ? state.PurgeDataAsync(default) : state.TransitionAsync(() =>
        {
            tokens.Account = "account-b";
            transitioned = true;
            return Task.FromResult(true);
        }, default);
        try
        {
            if (blockLaunch)
            {
                Assert.False(transitioned);
                Assert.False(transition.IsCompleted);
            }
            else
            {
                await transition.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(purge || transitioned);
            }
        }
        finally { release.TrySetResult(); }
        await transition;
        await response;
        Assert.True(purge || transitioned);
    }
}

internal sealed class OutlookTestHost(WebApplication app, HttpClient client, OutlookHandler handler, OutlookFakeLauncher launcher, string ownerSession) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public OutlookHandler Handler { get; } = handler;
    public OutlookFakeLauncher Launcher { get; } = launcher;
    public string OwnerSession { get; } = ownerSession;
    public IServiceProvider Services => app.Services;
    public static async Task<OutlookTestHost> StartAsync(bool signedIn = false, IOutlookTokenProvider? tokens = null, Action<Uri>? onGraphRequest = null,
        Action<Microsoft.AspNetCore.Http.HttpContext>? beforeRequest = null, ILocalJsonStore? store = null,
        Func<Microsoft.AspNetCore.Http.HttpContext, Task>? beforeResult = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        builder.Configuration["HelperPort"] = port.ToString();
        var auth = new OutlookFakeAuth { SignedIn = signedIn };
        var handler = new OutlookHandler((uri, _) =>
        {
            onGraphRequest?.Invoke(uri);
            return uri.AbsolutePath switch
        {
            "/v1.0/me/calendars" => """{"value":[{"id":"cal","name":"Calendar","isDefaultCalendar":true}]}""",
            "/v1.0/me/memberOf" => """{"value":[]}""",
            _ when uri.AbsolutePath.EndsWith("/calendarView") => """{"value":[{"id":"event","subject":"Meeting","start":{"dateTime":"2026-09-01T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-01T11:00:00","timeZone":"UTC"}}]}""",
            _ => """{"id":"event","subject":"Meeting","start":{"dateTime":"2026-09-01T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-09-01T11:00:00","timeZone":"UTC"},"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/l/meetup-join/fake"}}"""
            };
        });
        var launcher = new OutlookFakeLauncher();
        builder.Services.AddSingleton<LocalAccessService>();
        builder.Services.AddSingleton<IMicrosoftAuthService>(auth);
        builder.Services.AddSingleton<ILocalJsonStore>(store ?? new OutlookMemoryStore());
        builder.Services.AddSingleton<IOutlookMeetingLauncher>(launcher);
        if (tokens is not null) builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(sp => new OutlookGraphClient(new HttpClient(handler), sp.GetRequiredService<IOutlookTokenProvider>()));
        builder.Services.AddOutlookIntegration();
        var app = builder.Build();
        app.UseHelperSecurityBoundary();
        app.UseWidgetCors();
        if (beforeRequest is not null) app.Use(async (context, next) => { beforeRequest(context); await next(context); });
        var routes = app.MapGroup("");
        if (beforeResult is not null)
            routes.AddEndpointFilter(async (context, next) =>
            {
                var result = await next(context);
                await beforeResult(context.HttpContext);
                return result;
            });
        routes.MapOutlookIntegration();
        routes.MapWidgetPairings();
        routes.MapGroup("").AddEndpointFilter<OwnerAuthorizationFilter>().MapWidgetPairingManagement();
        routes.MapGroup("").AddEndpointFilter<OwnerAuthorizationFilter>().MapOutlookManagement();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var access = app.Services.GetRequiredService<LocalAccessService>();
        var owner = access.ExchangeBootstrap(access.CreateBootstrap().Token);
        return new(app, new HttpClient { BaseAddress = new(address) }, handler, launcher, owner);
    }
    public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
}

internal sealed class OutlookSwitchingTokens : IOutlookTokenProvider
{
    public bool SwitchOnSecondRead { get; set; }
    private int reads;
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult("fake");
    public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken ct)
    {
        var account = SwitchOnSecondRead && ++reads >= 2 ? "account-b" : "account-a";
        return Task.FromResult(new MicrosoftAccountIdentity(account, "test-tenant", "test-client", account + "@example.com"));
    }
}

internal sealed class OutlookFakeLauncher : IOutlookMeetingLauncher
{
    public List<Uri> Uris { get; } = [];
    public Func<Task>? BeforeOpen { get; set; }
    public async Task OpenAsync(Uri uri, CancellationToken ct) { if (BeforeOpen is not null) await BeforeOpen(); Uris.Add(uri); }
}

internal sealed class OutlookBlockedWriteStream(Stream inner, TaskCompletionSource entered, TaskCompletionSource release) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        entered.TrySetResult();
        await release.Task.WaitAsync(ct);
        await inner.WriteAsync(buffer, ct);
    }
}

internal sealed class OutlookFakeAuth : IMicrosoftAuthService
{
    public bool SignedIn { get; set; }
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new InvalidOperationException("Planner token requested.");
    public Task<string> GetTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken ct) { Assert.Equal(OutlookScopes.All, scopes); return Task.FromResult("fake"); }
    public Task<AzureAdOptions> GetConfigurationAsync(CancellationToken ct) => Task.FromResult(new AzureAdOptions { ClientId = SignedIn ? "test-client" : "" });
    public Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions options, CancellationToken ct) => Task.FromResult(options);
    public Task<AuthStatusResponse> GetStatusAsync(CancellationToken ct) => Task.FromResult(new AuthStatusResponse(SignedIn, SignedIn ? "me" : null, SignedIn ? "me@example.com" : null));
    public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken ct) => SignedIn
        ? Task.FromResult(new MicrosoftAccountIdentity("test-home", "test-tenant", "test-client", "me@example.com"))
        : throw new MsalUiRequiredException("no_account", "No account");
    public Task<AuthStatusResponse> ConnectOutlookAsync(CancellationToken ct) { SignedIn = true; return GetStatusAsync(ct); }
    public Task<AuthStatusResponse> SignInAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<AuthStatusResponse> EnableBoardMembersAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task SignOutAsync(CancellationToken ct) { SignedIn = false; return Task.CompletedTask; }
}
