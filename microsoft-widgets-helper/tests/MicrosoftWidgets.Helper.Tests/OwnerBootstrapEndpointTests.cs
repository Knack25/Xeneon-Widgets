using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Tests;

public sealed class OwnerBootstrapEndpointTests
{
    [Fact]
    public async Task Forged_same_origin_headers_cannot_create_an_owner_session()
    {
        await using var host = await OwnerBootstrapTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-access/session");
        request.Headers.Add("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add(LocalAccessHeaders.Bootstrap, "forged-bootstrap-token");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Bootstrap_exchange_is_single_use_and_unlocks_only_owner_filtered_data()
    {
        await using var host = await OwnerBootstrapTestHost.StartAsync();

        using (var configuration = await host.Client.GetAsync("/configuration"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, configuration.StatusCode);
            Assert.DoesNotContain("sensitive-client-id", await configuration.Content.ReadAsStringAsync());
        }
        using (var installation = await host.Client.GetAsync("/installation"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, installation.StatusCode);
            Assert.DoesNotContain("sensitive-installation-data", await installation.Content.ReadAsStringAsync());
        }

        var bootstrap = host.Access.CreateBootstrap();
        var session = await ExchangeAsync(host.Client, bootstrap.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => ExchangeAsync(host.Client, bootstrap.Token));

        using var authorizedConfiguration = new HttpRequestMessage(HttpMethod.Get, "/configuration");
        authorizedConfiguration.Headers.Add(LocalAccessHeaders.Owner, session);
        using var configurationResponse = await host.Client.SendAsync(authorizedConfiguration);
        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        Assert.Contains("sensitive-client-id", await configurationResponse.Content.ReadAsStringAsync());

        using var authorizedInstallation = new HttpRequestMessage(HttpMethod.Get, "/installation");
        authorizedInstallation.Headers.Add(LocalAccessHeaders.Owner, session);
        using var installationResponse = await host.Client.SendAsync(authorizedInstallation);
        Assert.Equal(HttpStatusCode.OK, installationResponse.StatusCode);
        Assert.Contains("sensitive-installation-data", await installationResponse.Content.ReadAsStringAsync());
    }

    private static async Task<string> ExchangeAsync(HttpClient client, string bootstrap)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-access/session");
        request.Headers.Add(LocalAccessHeaders.Bootstrap, bootstrap);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OwnerSessionResponse>();
        return Assert.IsType<string>(result?.Token);
    }
}

internal sealed class OwnerBootstrapTestHost(WebApplication app, HttpClient client, LocalAccessService access) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;
    public LocalAccessService Access { get; } = access;

    public static async Task<OwnerBootstrapTestHost> StartAsync()
    {
        var port = ReservePort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:" + port);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LocalAccessService>();
        var app = builder.Build();
        app.MapLocalAccess();
        app.MapGet("/configuration", () => Results.Ok(new { clientId = "sensitive-client-id" }))
            .AddEndpointFilter<OwnerAuthorizationFilter>();
        app.MapGet("/installation", () => Results.Ok(new { value = "sensitive-installation-data" }))
            .AddEndpointFilter<OwnerAuthorizationFilter>();
        await app.StartAsync();

        var access = app.Services.GetRequiredService<LocalAccessService>();
        return new(app, new HttpClient { BaseAddress = new Uri("http://localhost:" + port + "/") }, access);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
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
