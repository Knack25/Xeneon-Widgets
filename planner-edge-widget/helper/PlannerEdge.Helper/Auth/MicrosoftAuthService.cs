using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Auth;

public sealed record AzureAdOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string Tenant { get; init; } = "organizations";
}

public interface IMicrosoftAuthService : IGraphTokenProvider
{
    Task<AzureAdOptions> GetConfigurationAsync(CancellationToken cancellationToken);
    Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions configuration, CancellationToken cancellationToken);
    Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public sealed class MicrosoftAuthService(IOptions<AzureAdOptions> defaults, ILocalJsonStore jsonStore, string? cacheDirectory = null) : IMicrosoftAuthService
{
    private static readonly string[] Scopes = ["User.Read", "Tasks.ReadWrite"];
    private static readonly string[] BasicUserScopes = ["User.ReadBasic.All"];
    private const string RedirectUri = "http://localhost";
    private readonly string cacheRoot = cacheDirectory ?? LocalPaths.AppDataRoot();
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private IPublicClientApplication? app;
    private AzureAdOptions? currentConfiguration;

    public async Task<AzureAdOptions> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return currentConfiguration!;
    }

    public async Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions configuration, CancellationToken cancellationToken)
    {
        var normalized = Normalize(configuration);
        await configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (currentConfiguration == normalized) return normalized;
            var configuredApp = await CreateAppAsync(normalized);
            await jsonStore.WriteAsync("microsoft-auth", normalized, cancellationToken);
            app = configuredApp;
            currentConfiguration = normalized;
            return normalized;
        }
        finally { configurationGate.Release(); }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (currentConfiguration is not null) return;
        await configurationGate.WaitAsync(cancellationToken);
        try
        {
            if (currentConfiguration is not null) return;
            var saved = await jsonStore.ReadAsync<AzureAdOptions>("microsoft-auth", cancellationToken);
            currentConfiguration = saved ?? defaults.Value;
            app = await CreateAppAsync(currentConfiguration);
        }
        finally { configurationGate.Release(); }
    }

    private static AzureAdOptions Normalize(AzureAdOptions configuration)
    {
        if (!Guid.TryParse(configuration.ClientId, out var clientId))
            throw new ArgumentException("Enter a valid Application (client) ID.");
        var tenant = string.IsNullOrWhiteSpace(configuration.Tenant) ? "organizations" : configuration.Tenant.Trim();
        if (tenant != "organizations" && !Guid.TryParse(tenant, out _))
            throw new ArgumentException("Enter a tenant ID or use organizations.");
        return new AzureAdOptions { ClientId = clientId.ToString(), Tenant = tenant };
    }

    private async Task<IPublicClientApplication?> CreateAppAsync(AzureAdOptions configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ClientId)) return null;
        var client = PublicClientApplicationBuilder.Create(configuration.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, configuration.Tenant)
            .WithRedirectUri(RedirectUri)
            .Build();
        var cacheProperties = new StorageCreationPropertiesBuilder($"msal-{configuration.ClientId}-{configuration.Tenant}.cache", cacheRoot).Build();
        await RegisterCacheAsync(client, cacheProperties);
        return client;
    }

    private async Task RegisterCacheAsync(IPublicClientApplication app, StorageCreationProperties properties)
    {
        Directory.CreateDirectory(cacheRoot);
        var helper = await MsalCacheHelper.CreateAsync(properties);
        helper.RegisterCache(app.UserTokenCache);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = await RequireAppAsync(cancellationToken);
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException("no_account", "No Microsoft account is signed in.");
        return (await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
    }

    public async Task<string> GetBasicUserTokenAsync(CancellationToken cancellationToken)
    {
        var client = await RequireAppAsync(cancellationToken);
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException("no_account", "No Microsoft account is signed in.");
        return (await client.AcquireTokenSilent(BasicUserScopes, account).ExecuteAsync(cancellationToken)).AccessToken;
    }

    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (app is null)
            return new AuthStatusResponse(false, null, null, new ApiErrorResponse("not_configured", "Microsoft client ID is not configured."));
        cancellationToken.ThrowIfCancellationRequested();
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        return account is null
            ? new AuthStatusResponse(false, null, null)
            : new AuthStatusResponse(true, account.Username, account.Username);
    }

    public async Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken)
    {
        var client = await RequireAppAsync(cancellationToken);
        var result = await client.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken);
        return new AuthStatusResponse(true, result.Account.Username, result.Account.Username);
    }

    public async Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken cancellationToken)
    {
        var client = await RequireAppAsync(cancellationToken);
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException("no_account", "Sign in to Planner first.");
        var result = await client.AcquireTokenInteractive(BasicUserScopes)
            .WithAccount(account)
            .WithPrompt(Prompt.Consent)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken);
        return new AuthStatusResponse(true, result.Account.Username, result.Account.Username);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (app is null) return;
        foreach (var account in await app.GetAccountsAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await app.RemoveAsync(account);
        }
    }

    private async Task<IPublicClientApplication> RequireAppAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return app ?? throw new InvalidOperationException("Microsoft client ID is not configured.");
    }
}
