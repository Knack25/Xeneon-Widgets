using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Hosting;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Updates;

namespace PlannerEdge.Helper.Tests;

public sealed class OwnerManagementEndpointTests
{
    public static TheoryData<string, string> OwnerOnlyRoutes => new()
    {
        { "GET", "/configuration" },
        { "PUT", "/configuration" },
        { "GET", "/auth/status" },
        { "GET", "/auth/capabilities" },
        { "GET", "/auth/me" },
        { "POST", "/auth/sign-in" },
        { "POST", "/auth/enable-task-chat" },
        { "POST", "/auth/enable-assignee-names" },
        { "POST", "/auth/enable-board-members" },
        { "POST", "/auth/sign-out" },
        { "GET", "/installation" },
        { "GET", "/downloads/planner" },
        { "GET", "/downloads/outlook" },
        { "GET", "/updates" },
        { "POST", "/updates/check" },
        { "POST", "/updates/install" },
        { "GET", "/updates/result" },
        { "GET", "/api/outlook/session" },
        { "GET", "/api/outlook/status" },
        { "POST", "/api/outlook/connect" },
        { "POST", "/api/outlook/sources" },
        { "POST", "/api/outlook/sources/calendar/remove" },
        { "GET", "/api/outlook/pairings" },
        { "POST", "/api/outlook/pairings/pair/approve" },
        { "POST", "/api/outlook/pairings/revoke" },
        { "GET", "/api/outlook/paired" },
        { "GET", "/api/local-access/pairings" },
        { "GET", "/api/local-access/pairings/paired" },
        { "POST", "/api/local-access/pairings/pair/approve" },
        { "POST", "/api/local-access/pairings/revoke" },
        { "POST", "/host/stop" }
    };

    [Theory]
    [MemberData(nameof(OwnerOnlyRoutes))]
    public async Task Management_route_requires_an_owner_session(string method, string path)
    {
        await using var host = await OwnerManagementTestHost.StartAsync();

        using var anonymous = await host.SendAsync(method, path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var widget = await host.SendAsync(method, path, authorization: "Bearer widget-credential");
        Assert.Equal(HttpStatusCode.Unauthorized, widget.StatusCode);

        using var owner = await host.SendAsync(method, path, owner: host.OwnerSession);
        var ownerCode = (int)owner.StatusCode;
        Assert.True(owner.StatusCode != HttpStatusCode.Unauthorized &&
            (ownerCode is >= 200 and < 300 or >= 400 and < 500),
            $"Expected an owner-authorized business response but received {ownerCode}: {await owner.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Sign_in_is_not_available_via_get()
    {
        await using var host = await OwnerManagementTestHost.StartAsync();

        using var response = await host.SendAsync("GET", "/auth/sign-in", owner: host.OwnerSession);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }
}

internal sealed class OwnerManagementTestHost(WebApplication app, HttpClient client, string ownerSession) : IAsyncDisposable
{
    public string OwnerSession { get; } = ownerSession;

    public static async Task<OwnerManagementTestHost> StartAsync()
    {
        var port = ReservePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LocalAccessService>();
        builder.Services.AddSingleton<IMicrosoftAuthService>(new OwnerManagementAuth());
        builder.Services.AddSingleton<MicrosoftAuthCapabilityService>();
        builder.Services.AddSingleton<IPlannerGraphClient, OwnerManagementGraph>();
        builder.Services.AddSingleton<IPlannerSettingsStore, OwnerManagementSettings>();
        builder.Services.AddSingleton<ILocalJsonStore>(new OwnerManagementStore());
        builder.Services.AddOutlookIntegration();
        builder.Services.AddSingleton<IReleaseClient, OwnerManagementReleaseClient>();
        builder.Services.AddSingleton<IUpdateInstaller, OwnerManagementInstaller>();
        builder.Services.AddSingleton(provider => new UpdateService(provider.GetRequiredService<IReleaseClient>(),
            provider.GetRequiredService<IUpdateInstaller>(), "0.3.1"));
        var app = builder.Build();
        app.MapManagementEndpoints();
        await app.StartAsync();

        var access = app.Services.GetRequiredService<LocalAccessService>();
        var owner = access.ExchangeBootstrap(access.CreateBootstrap().Token);
        return new(app, new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port + "/") }, owner);
    }

    public async Task<HttpResponseMessage> SendAsync(string method, string path, string? owner = null, string? authorization = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        if (owner is not null) request.Headers.Add(LocalAccessHeaders.Owner, owner);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal sealed class OwnerManagementAuth : IMicrosoftAuthService
{
    private static readonly AuthStatusResponse Status = new(true, "owner", "owner@example.com");
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => Task.FromResult("token");
    public Task<string> GetTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken ct) => Task.FromResult("token");
    public Task<AzureAdOptions> GetConfigurationAsync(CancellationToken ct) => Task.FromResult(new AzureAdOptions { ClientId = "11111111-1111-1111-1111-111111111111" });
    public Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions configuration, CancellationToken ct) => Task.FromResult(configuration);
    public Task<AuthStatusResponse> GetStatusAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<AuthStatusResponse> SignInAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<AuthStatusResponse> ConnectOutlookAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<AuthStatusResponse> EnableTaskChatAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<AuthStatusResponse> EnableBoardMembersAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OwnerManagementGraph : IPlannerGraphClient
{
    public Task<string> GetCurrentUserIdAsync(CancellationToken ct) => Task.FromResult("owner");
    public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
    public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
    public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
    public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
    public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class OwnerManagementSettings : IPlannerSettingsStore
{
    private SettingsDto settings = new(null, null, false);
    public Task<SettingsDto> LoadSettingsAsync(CancellationToken ct) => Task.FromResult(settings);
    public Task SaveSettingsAsync(SettingsDto value, CancellationToken ct) { settings = value; return Task.CompletedTask; }
    public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken ct) => Task.FromResult<BoardDisplay?>(null);
    public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OwnerManagementStore : ILocalJsonStore
{
    private readonly Dictionary<string, object> values = [];
    public Task<T?> ReadAsync<T>(string name, CancellationToken ct) => Task.FromResult(values.TryGetValue(name, out var value) ? (T)value : default);
    public Task WriteAsync<T>(string name, T value, CancellationToken ct) { values[name] = value!; return Task.CompletedTask; }
}

internal sealed class OwnerManagementReleaseClient : IReleaseClient
{
    public Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken ct) => Task.FromResult<UpdateRelease?>(null);
    public Task DownloadAsync(UpdateRelease release, string path, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class OwnerManagementInstaller : IUpdateInstaller
{
    public bool CanInstall => true;
    public string CreateDownloadPath() => Path.GetTempFileName();
    public void Launch(string path, UpdateRelease release) { }
}
