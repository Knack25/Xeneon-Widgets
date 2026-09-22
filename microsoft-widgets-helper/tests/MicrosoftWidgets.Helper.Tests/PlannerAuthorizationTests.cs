using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PlannerEdge.Helper.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MicrosoftWidgets.Helper.Tests;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerAuthorizationTests
{
    public static IEnumerable<object[]> JsonBodyRoutes()
    {
        foreach (var route in new[]
        {
            ("PUT", "/settings"), ("PUT", "/selected-plan"), ("PUT", "/view-preferences/plan"),
            ("POST", "/tasks"), ("PUT", "/tasks/task/notes"), ("POST", "/tasks/task/chat"),
            ("PUT", "/tasks/task/bucket"), ("PUT", "/tasks/task/due-date"), ("PUT", "/tasks/task/title"),
            ("PUT", "/tasks/task/progress"), ("PUT", "/tasks/task/priority"), ("PUT", "/tasks/task/start-date"),
            ("PUT", "/tasks/task/labels"), ("PUT", "/tasks/task/assignments"),
            ("POST", "/tasks/task/checklist"), ("PUT", "/tasks/task/checklist/item"),
            ("PUT", "/tasks/task/checklist/item/position")
        })
        foreach (var prefix in new[] { "", "/api/planner" })
            yield return [route.Item1, prefix + route.Item2];
    }

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var route in PlannerHttpIntegrationTests.LegacyPlannerRoutes.Concat(new[] { new object[] { "GET", "/me" } }))
        foreach (var prefix in new[] { "", "/api/planner" })
        foreach (var access in new[] { "anonymous", "forged-origin", "null-origin", "missing-origin", "planner", "planner-no-origin", "planner-file", "outlook", "revoked", "account-changed", "invalidated", "owner", "owner-expired", "owner-no-origin", "owner-null", "legacy" })
            yield return [route[0], prefix + route[1], access];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryPlannerEndpointRequiresItsScope(string method, string path, string access)
    {
        await using var host = await StartAsync();
        var pairing = host.App.Services.GetRequiredService<WidgetPairingService>();
        var state = host.App.Services.GetRequiredService<OutlookAccountState>();
        var scope = access == "outlook" ? WidgetScope.Outlook : WidgetScope.Planner;
        var pending = await pairing.CreateAsync(scope, new("instance", new string('x', 64)), await state.GetAsync(default), default);
        await pairing.ApproveAsync(pending.Id, default);
        var credential = (await pairing.PollAsync(pending.Id, new string('x', 64), default)).Credential!;
        if (access == "revoked") await pairing.RevokeAsync((await pairing.GetPairedAsync(default)).Single().CredentialId, default);
        if (access == "account-changed") host.Tokens.Account = "account-b";
        if (access == "invalidated") await state.InvalidateAsync(default);
        if (access == "owner-expired") host.App.Services.GetRequiredService<OutlookClock>().Advance(TimeSpan.FromHours(8));
        using var request = new HttpRequestMessage(new(method), path);
        if (method is "PUT" or "POST") request.Content = JsonContent.Create(new { planId = "plan", title = "Title", description = "Notes", message = "Comment", bucketId = "bucket", date = "2026-09-22", startDate = "2026-09-22", progress = 50, priority = 5, labelIds = new[] { "category1" }, userIds = Array.Empty<string>(), direction = "up" });
        if (access is not ("missing-origin" or "planner-no-origin" or "owner-no-origin")) request.Headers.Add("Origin", access == "forged-origin" ? "http://localhost:9999" : access is "owner" or "owner-expired" ? host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority) : access == "planner-file" ? "file://" : "null");
        if (access == "owner-no-origin") request.Headers.Add("Sec-Fetch-Site", "same-origin");
        if (access is "planner" or "planner-no-origin" or "planner-file" or "outlook" or "revoked" or "account-changed" or "invalidated" or "forged-origin") request.Headers.Add(LocalAccessHeaders.Credential, credential);
        if (access == "legacy") request.Headers.Add("Authorization", "Bearer " + credential);
        if (access.StartsWith("owner")) request.Headers.Add(LocalAccessHeaders.Owner, host.Owner);
        using var response = await host.Client.SendAsync(request);
        var allowed = access is "planner" or "planner-no-origin" or "planner-file" or "owner" or "owner-no-origin";
        Assert.Equal(allowed ? HttpStatusCode.Accepted : access == "forged-origin" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, host.Executions);
        Assert.Equal(access is "planner" or "planner-file", response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task MatrixIncludesEveryRegisteredPlannerEndpoint()
    {
        await using var host = await StartAsync();
        var expected = Matrix().Select(row => $"{row[0]} {row[1]}").ToHashSet();
        var actual = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(method =>
                $"{method} {endpoint.RoutePattern.RawText!.Replace("{taskId}", "task").Replace("{itemId}", "item").Replace("{planId}", "plan")}"))
            .ToHashSet();
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Theory]
    [InlineData("/configuration", "PUT", "Content-Type")]
    [InlineData("/api/outlook/connect", "POST", "Content-Type")]
    [InlineData("/settings", "PUT", "Authorization")]
    [InlineData("/settings", "PUT", "X-Microsoft-Widgets-Owner")]
    [InlineData("/not-a-route", "GET", "")]
    public async Task NativePreflightDoesNotOpenManagementOrLegacyAuthentication(string path, string method, string headers)
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", "null");
        request.Headers.Add("Access-Control-Request-Method", method);
        if (headers.Length > 0) request.Headers.Add("Access-Control-Request-Headers", headers);
        var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task PreflightDoesNotAuthorizeMutation()
    {
        await using var host = await StartAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/tasks/task/checklist/item");
        preflight.Headers.Add("Origin", "null");
        preflight.Headers.Add("Access-Control-Request-Method", "DELETE");
        using var transport = await host.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, transport.StatusCode);
        Assert.Contains("DELETE", transport.Headers.GetValues("Access-Control-Allow-Methods").Single());
        using var mutation = new HttpRequestMessage(HttpMethod.Delete, "/tasks/task/checklist/item");
        mutation.Headers.Add("Origin", "null");
        var response = await host.Client.SendAsync(mutation);
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(0, host.Executions);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/api/planner/settings")]
    public async Task OversizedAnonymousPlannerBodyIsRejectedBeforeBindingOrServiceExecution(string path)
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent("{\"planId\":\"" + new string('x', 17_000) + "\"}", MediaTypeHeaderValue.Parse("application/json"))
        };
        request.Headers.Add("Origin", "null");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Executions);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/api/planner/settings")]
    public async Task ChunkedOversizedAnonymousPlannerBodyIsRejectedBeforeBindingOrServiceExecution(string path)
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ChunkedContent("{\"planId\":\"" + new string('x', 17_000) + "\"}")
        };
        request.Headers.Add("Origin", "null");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Executions);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/api/planner/settings")]
    public async Task ChunkedOversizedAuthenticatedPlannerBodyIsCappedBeforeServiceExecution(string path)
    {
        await using var host = await StartAsync();
        var credential = await PairPlannerAsync(host);
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new ChunkedContent("{\"planId\":\"" + new string('x', 17_000) + "\"}")
        };
        request.Headers.Add("Origin", "null");
        request.Headers.Add(LocalAccessHeaders.Credential, credential);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.Executions);
    }

    [Theory]
    [MemberData(nameof(JsonBodyRoutes))]
    public async Task AuthenticatedPlannerBodyRequiresJsonContentTypeBeforeBinding(string method, string path)
    {
        await using var host = await StartAsync();
        var credential = await PairPlannerAsync(host);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{\"planId\":\"plan\"}", MediaTypeHeaderValue.Parse("text/plain"))
        };
        request.Headers.Add("Origin", "null");
        request.Headers.Add(LocalAccessHeaders.Credential, credential);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(0, host.Executions);
    }

    [Fact]
    public async Task AccountChangeBeforeSerializationRejectsPlannerResult()
    {
        await using var host = await StartAsync(invalidateBeforeResult: true);
        host.Client.DefaultRequestHeaders.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        host.Client.DefaultRequestHeaders.Add(LocalAccessHeaders.Owner, host.Owner);
        var response = await host.Client.GetAsync("/api/planner/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("private-result", await response.Content.ReadAsStringAsync());
    }

    private static async Task<Host> StartAsync(bool invalidateBeforeResult = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var tokens = new OutlookTokens();
        builder.Services.AddMemoryCache();
        builder.Services.AddPlannerIntegration();
        builder.Services.AddOutlookIntegration();
        builder.Services.AddSingleton<IOutlookTokenProvider>(tokens);
        builder.Services.AddSingleton<ILocalJsonStore, OutlookMemoryStore>();
        builder.Services.AddSingleton<LocalAccessService>();
        builder.Services.AddSingleton<OutlookClock>();
        builder.Services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<OutlookClock>());
        builder.Services.AddSingleton<IMicrosoftAuthService>(new OutlookFakeAuth { SignedIn = true });
        builder.Services.AddSingleton<IGraphTokenProvider>(sp => sp.GetRequiredService<IMicrosoftAuthService>());
        builder.Services.AddSingleton<IPlannerGraphClient, ProbeGraph>();
        builder.Services.AddSingleton<IPlannerSettingsStore>(new ProbeSettings(invalidateBeforeResult));
        Host? host = null;
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (ProbeException) { host!.Executions++; context.Response.StatusCode = 202; }
        });
        app.UseRouting();
        app.UsePlannerRequestPolicy();
        app.UseWidgetCors();
        var routes = app.MapGroup("");
        if (invalidateBeforeResult) routes.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            await context.HttpContext.RequestServices.GetRequiredService<OutlookAccountState>().InvalidateAsync(default);
            return result;
        });
        routes.MapPlannerIntegration();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var local = app.Services.GetRequiredService<LocalAccessService>();
        host = new(app, new() { BaseAddress = new(address) }, tokens, local.ExchangeBootstrap(local.CreateBootstrap().Token));
        return host;
    }

    private static async Task<string> PairPlannerAsync(Host host)
    {
        const string secret = "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        var pairing = host.App.Services.GetRequiredService<WidgetPairingService>();
        var state = host.App.Services.GetRequiredService<OutlookAccountState>();
        var pending = await pairing.CreateAsync(WidgetScope.Planner, new("instance", secret), await state.GetAsync(default), default);
        await pairing.ApproveAsync(pending.Id, default);
        return (await pairing.PollAsync(pending.Id, secret, default)).Credential!;
    }

    private sealed class ChunkedContent : HttpContent
    {
        private readonly string body;

        public ChunkedContent(string body)
        {
            this.body = body;
            Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    public sealed class ProbeException : Exception;
    private sealed class ProbeGraph : IPlannerGraphClient
    {
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken ct) => throw new ProbeException();
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new ProbeException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task<GraphTask?> GetTaskAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task<string?> GetUserDisplayNameAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task<string> GetCurrentUserIdAsync(CancellationToken ct) => throw new ProbeException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string id, CancellationToken ct) => throw new ProbeException();
        public Task CompleteChecklistItemAsync(string id, string item, string etag, CancellationToken ct) => throw new ProbeException();
        public Task CompleteTaskAsync(string id, string etag, CancellationToken ct) => throw new ProbeException();
    }
    private sealed class ProbeSettings(bool respond) : IPlannerSettingsStore
    {
        public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) => respond ? Task.FromResult(new SettingsDto("plan", "private-result", true)) : throw new ProbeException();
        public Task SaveSettingsAsync(SettingsDto value, CancellationToken ct) => throw new ProbeException();
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => throw new ProbeException();
        public Task SaveCachedDisplayAsync(BoardDisplay value, CancellationToken ct) => throw new ProbeException();
    }

    private sealed class Host(WebApplication app, HttpClient client, OutlookTokens tokens, string owner) : IAsyncDisposable
    {
        public WebApplication App => app;
        public HttpClient Client => client;
        public OutlookTokens Tokens => tokens;
        public string Owner => owner;
        public int Executions;
        public async ValueTask DisposeAsync() { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
