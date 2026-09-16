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
    Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public sealed class MicrosoftAuthService : IMicrosoftAuthService
{
    private static readonly string[] Scopes = ["User.Read", "Tasks.ReadWrite"];
    private readonly IPublicClientApplication? app;
    private readonly Task cacheReady;

    public MicrosoftAuthService(IOptions<AzureAdOptions> options)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ClientId))
        {
            cacheReady = Task.CompletedTask;
            return;
        }

        app = PublicClientApplicationBuilder.Create(options.Value.ClientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.Value.Tenant)
            .WithDefaultRedirectUri()
            .Build();

        var cacheProperties = new StorageCreationPropertiesBuilder("msal.cache", LocalPaths.AppDataRoot()).Build();
        cacheReady = RegisterCacheAsync(app, cacheProperties);
    }

    private static async Task RegisterCacheAsync(IPublicClientApplication app, StorageCreationProperties properties)
    {
        Directory.CreateDirectory(LocalPaths.AppDataRoot());
        var helper = await MsalCacheHelper.CreateAsync(properties);
        helper.RegisterCache(app.UserTokenCache);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = RequireApp();
        await cacheReady;
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException("no_account", "No Microsoft account is signed in.");
        return (await client.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
    }

    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (app is null)
            return new AuthStatusResponse(false, null, null, new ApiErrorResponse("not_configured", "Microsoft client ID is not configured."));
        await cacheReady;
        cancellationToken.ThrowIfCancellationRequested();
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        return account is null
            ? new AuthStatusResponse(false, null, null)
            : new AuthStatusResponse(true, account.Username, account.Username);
    }

    public async Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken)
    {
        var client = RequireApp();
        await cacheReady;
        var result = await client.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(cancellationToken);
        return new AuthStatusResponse(true, result.Account.Username, result.Account.Username);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        if (app is null) return;
        await cacheReady;
        foreach (var account in await app.GetAccountsAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await app.RemoveAsync(account);
        }
    }

    private IPublicClientApplication RequireApp() =>
        app ?? throw new InvalidOperationException("Microsoft client ID is not configured.");
}
