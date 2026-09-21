using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Outlook;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MicrosoftWidgets.Helper.Tests")]

namespace PlannerEdge.Helper.Auth;

public sealed record AzureAdOptions
{
    public string ClientId { get; init; } = string.Empty;
    public string Tenant { get; init; } = "organizations";
}

public interface IMicrosoftAuthService : IGraphTokenProvider
{
    Task<string> GetTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken);
    Task<AzureAdOptions> GetConfigurationAsync(CancellationToken cancellationToken);
    Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions configuration, CancellationToken cancellationToken);
    Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> SignInAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> ConnectOutlookAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> EnableTaskChatAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken cancellationToken);
    Task<AuthStatusResponse> EnableBoardMembersAsync(CancellationToken cancellationToken);
    Task SignOutAsync(CancellationToken cancellationToken);
}

public sealed class MicrosoftAuthService(IOptions<AzureAdOptions> defaults, ILocalJsonStore jsonStore, string? cacheDirectory = null) : IMicrosoftAuthService
{
    internal static IReadOnlyList<string> PlannerScopes { get; } = Array.AsReadOnly(new[] { "User.Read", "Tasks.ReadWrite" });
    internal static IReadOnlyList<string> ConversationScopes { get; } =
        Array.AsReadOnly(new[] { "Group-Conversation.ReadWrite.All" });
    internal static IReadOnlyList<string> PlannerConnectScopes { get; } =
        Array.AsReadOnly(new[] { "User.Read", "Tasks.ReadWrite", "Group-Conversation.ReadWrite.All" });
    internal static IReadOnlyList<string> AssigneeNamesScopes { get; } = Array.AsReadOnly(new[] { "User.ReadBasic.All" });
    internal static IReadOnlyList<string> BoardMembersScopes { get; } = Array.AsReadOnly(new[] { "GroupMember.ReadBasic.All" });
    private const string RedirectUri = "http://localhost";
    private readonly string cacheRoot = cacheDirectory ?? LocalPaths.AppDataRoot();
    private readonly SemaphoreSlim configurationGate = new(1, 1);
    private readonly Func<IEnumerable<string>, CancellationToken, Task<string>>? acquireToken;
    private readonly Func<IEnumerable<string>, bool, CancellationToken, Task<AuthStatusResponse>>? connect;
    private IPublicClientApplication? app;
    private AzureAdOptions? currentConfiguration;

    internal MicrosoftAuthService(IOptions<AzureAdOptions> defaults, ILocalJsonStore jsonStore,
        Func<IEnumerable<string>, CancellationToken, Task<string>> acquireToken,
        Func<IEnumerable<string>, bool, CancellationToken, Task<AuthStatusResponse>> connect,
        string? cacheDirectory = null) : this(defaults, jsonStore, cacheDirectory)
    {
        this.acquireToken = acquireToken;
        this.connect = connect;
    }

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
            var configuration = saved ?? defaults.Value;
            var configuredApp = await CreateAppAsync(configuration);
            app = configuredApp;
            currentConfiguration = configuration;
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

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => GetTokenForScopesAsync(PlannerScopes, cancellationToken);

    public Task<string> GetBasicUserTokenAsync(CancellationToken cancellationToken) => GetTokenForScopesAsync(AssigneeNamesScopes, cancellationToken);

    public Task<string> GetGroupMemberTokenAsync(CancellationToken cancellationToken) => GetTokenForScopesAsync(BoardMembersScopes, cancellationToken);

    public Task<string> GetConversationTokenAsync(CancellationToken cancellationToken) =>
        GetTokenForScopesAsync(ConversationScopes, cancellationToken);

    public async Task<string> GetTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        if (acquireToken is not null) return await acquireToken(scopes, cancellationToken);
        var client = await RequireAppAsync(cancellationToken);
        var account = (await client.GetAccountsAsync()).FirstOrDefault()
            ?? throw new MsalUiRequiredException("no_account", "No Microsoft account is signed in.");
        return (await client.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken)).AccessToken;
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
        try
        {
            return await ConnectForScopesAsync(PlannerConnectScopes, requireExistingAccount: false, cancellationToken);
        }
        catch (MsalException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ConnectForScopesAsync(PlannerScopes, requireExistingAccount: false, cancellationToken);
        }
    }

    public Task<AuthStatusResponse> ConnectOutlookAsync(CancellationToken cancellationToken) =>
        ConnectForScopesAsync(OutlookScopes.All, requireExistingAccount: false, cancellationToken);

    public Task<AuthStatusResponse> EnableTaskChatAsync(CancellationToken cancellationToken) =>
        ConnectForScopesAsync(ConversationScopes, requireExistingAccount: true, cancellationToken);

    public Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken cancellationToken) =>
        ConnectForScopesAsync(AssigneeNamesScopes, requireExistingAccount: true, cancellationToken);

    public Task<AuthStatusResponse> EnableBoardMembersAsync(CancellationToken cancellationToken) =>
        ConnectForScopesAsync(BoardMembersScopes, requireExistingAccount: true, cancellationToken);

    private Task<AuthStatusResponse> ConnectForScopesAsync(IEnumerable<string> scopes, bool requireExistingAccount,
        CancellationToken cancellationToken) => connect is null
            ? ConnectAsync(scopes, requireExistingAccount, cancellationToken)
            : connect(scopes, requireExistingAccount, cancellationToken);

    private async Task<AuthStatusResponse> ConnectAsync(IEnumerable<string> scopes, bool requireExistingAccount, CancellationToken cancellationToken)
    {
        var client = await RequireAppAsync(cancellationToken);
        var account = (await client.GetAccountsAsync()).FirstOrDefault();
        if (requireExistingAccount && account is null)
            throw new MsalUiRequiredException("no_account", "Sign in to Microsoft first.");
        var result = await AcquireSilentFirstAsync(
            ct => account is null
                ? Task.FromException<AuthenticationResult>(new MsalUiRequiredException("no_account", "No Microsoft account is signed in."))
                : client.AcquireTokenSilent(scopes, account).ExecuteAsync(ct),
            (required, ct) =>
            {
                var request = client.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false);
                request = account is null ? request.WithPrompt(Prompt.SelectAccount) : request.WithAccount(account);
                if (!string.IsNullOrEmpty(required.Claims)) request = request.WithClaims(required.Claims);
                return request.ExecuteAsync(ct);
            }, cancellationToken);
        await RetainAccountAsync(client, result.Account);
        return new AuthStatusResponse(true, result.Account.Username, result.Account.Username);
    }

    internal static async Task<T> AcquireSilentFirstAsync<T>(Func<CancellationToken, Task<T>> silent,
        Func<MsalUiRequiredException, CancellationToken, Task<T>> interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return await silent(cancellationToken); }
        catch (MsalUiRequiredException required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await interactive(required, cancellationToken);
        }
    }

    private static async Task RetainAccountAsync(IPublicClientApplication client, IAccount selected)
    {
        // All integrations share one account; never silently fall back to a previously cached user.
        foreach (var cached in await client.GetAccountsAsync())
            if (cached.HomeAccountId.Identifier != selected.HomeAccountId.Identifier)
                await client.RemoveAsync(cached);
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
