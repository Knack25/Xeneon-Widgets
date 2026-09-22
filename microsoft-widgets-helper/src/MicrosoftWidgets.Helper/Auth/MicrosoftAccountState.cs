using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Auth;

public readonly record struct AccountLease(string Key, long Generation);

// Serializes account transitions and prevents work authorized under an old identity from being published.
public sealed class MicrosoftAccountState(IMicrosoftAccountIdentityProvider identities, ILocalJsonStore store)
{
    private const string IdentityStore = "microsoft-account-identity";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<AccountLease?> authorizedRequest = new();
    private string? account;
    private long generation;
    private bool loaded;
    private bool credentialsValidated;
    private StoredMicrosoftAccountIdentity? persistedIdentity;

    public event Action? Invalidated;
    public event Action<string>? SourceInvalidated;

    public static string Key(MicrosoftAccountIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.HomeAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ClientId);
        var value = "microsoft-account-v1\n" + identity.HomeAccountId + "\n" +
            identity.TenantId.ToLowerInvariant() + "\n" + identity.ClientId.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public Task<AccountLease> GetAsync(CancellationToken ct) => GetIdentityAsync(true, ct);

    public async Task<AccountLease> GetIdentityAsync(bool requireAccount, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await SynchronizeIdentityLockedAsync(requireAccount, ct); }
        finally { gate.Release(); }
    }

    private async Task<AccountLease> SynchronizeIdentityLockedAsync(bool requireAccount, CancellationToken ct)
    {
        if (!loaded) await LoadAndMigrateAsync(ct);

        MicrosoftAccountIdentity? identity = null;
        Exception? failure = null;
        try { identity = await identities.GetAccountIdentityAsync(ct); }
        catch (OutlookException ex) when (ex.Code is "not_configured" or "sign_in_required") { failure = ex; }
        catch (MsalUiRequiredException ex) { failure = ex; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("client ID", StringComparison.OrdinalIgnoreCase)) { failure = ex; }

        var key = identity is null ? "setup:" + FailureCode(failure) : Key(identity);
        var stored = identity is null ? null : StoredMicrosoftAccountIdentity.From(identity);
        if (!credentialsValidated)
        {
            var credentials = await store.ReadAsync<WidgetCredentialStore>("widget-credentials", ct);
            if (credentials is not null && (credentials.Version != 1 || credentials.Credentials.Any(item => item.AccountKey != key)))
                await ClearWidgetCredentialsAsync(ct);
            credentialsValidated = true;
        }
        if (identity is not null && (persistedIdentity is null || !persistedIdentity.Matches(identity)))
        {
            await ClearWidgetCredentialsAsync(ct);
            await store.WriteAsync(IdentityStore, stored, ct);
            persistedIdentity = stored;
        }
        if (account != key)
        {
            if (account is not null)
            {
                await ClearWidgetCredentialsAsync(ct);
                Reset();
            }
            account = key;
        }
        if (authorizedRequest.Value is { } required) RequireCurrent(required);
        if (requireAccount && failure is not null) throw NormalizeFailure(failure);
        return new(key, generation);
    }

    private async Task LoadAndMigrateAsync(CancellationToken ct)
    {
        persistedIdentity = await store.ReadAsync<StoredMicrosoftAccountIdentity>(IdentityStore, ct);
        var legacyAccount = await store.ReadAsync<string>("outlook-account", ct);
        await store.WriteAsync("outlook-credentials", Array.Empty<StoredOutlookCredential>(), ct);
        await store.WriteAsync<string?>("outlook-account", null, ct);
        if (legacyAccount is not null || persistedIdentity is null) await ClearWidgetCredentialsAsync(ct);
        credentialsValidated = legacyAccount is not null || persistedIdentity is null;
        loaded = true;
    }

    public async Task<T> TransitionAsync<T>(Func<Task<T>> transition, CancellationToken cancellationToken,
        bool forceInvalidate = false, Func<T, bool>? invalidateWhen = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeIdentityLockedAsync(false, cancellationToken);
            T? result = default;
            var completed = false;
            try
            {
                result = await transition();
                completed = true;
                return result;
            }
            finally
            {
                if (forceInvalidate || completed && invalidateWhen?.Invoke(result!) == true)
                {
                    Reset();
                    await ClearWidgetCredentialsAsync(CancellationToken.None);
                }
                await SynchronizeIdentityLockedAsync(false, CancellationToken.None);
            }
        }
        finally { gate.Release(); }
    }

    public bool IsCurrent(AccountLease lease) => lease.Key == account && lease.Generation == Interlocked.Read(ref generation);

    public void RequireCurrent(AccountLease lease)
    {
        if (!IsCurrent(lease) || authorizedRequest.Value is { } required && required != lease)
            throw new OutlookException("account_changed", "The Microsoft account changed. Reconnect the widget.", 401);
    }

    internal IDisposable BindRequest(AccountLease lease)
    {
        RequireCurrent(lease);
        var previous = authorizedRequest.Value;
        authorizedRequest.Value = lease;
        return new RequestScope(() => authorizedRequest.Value = previous);
    }

    internal async Task<StoredWidgetCredential[]> ReadWidgetCredentialsAsync(AccountLease lease, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            var data = await store.ReadAsync<WidgetCredentialStore>("widget-credentials", ct);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            return data is { Version: 1, Credentials: not null } ? data.Credentials : [];
        }
        finally { gate.Release(); }
    }

    internal Task LaunchAsync(AccountLease lease, Func<Task> launch, CancellationToken ct) => ExecuteAuthorizedAsync(lease, launch, ct);

    internal async Task ExecuteAuthorizedAsync(AccountLease lease, Func<Task> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            await action();
            await SynchronizeIdentityLockedAsync(false, CancellationToken.None);
            RequireCurrent(lease);
        }
        finally { gate.Release(); }
    }

    internal async Task<T> ExecuteBoundAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var lease = authorizedRequest.Value
            ?? throw new OutlookException("account_changed", "The Microsoft account changed. Reconnect the widget.", 401);
        await gate.WaitAsync(ct);
        try
        {
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            T? result = default;
            try
            {
                result = await action();
                await SynchronizeIdentityLockedAsync(false, CancellationToken.None);
                RequireCurrent(lease);
                return result;
            }
            catch
            {
                if (result is IDisposable disposable) disposable.Dispose();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Reset();
            await ClearWidgetCredentialsAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task PurgeDataAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { Reset(); }
        finally { gate.Release(); }
    }

    public void PurgeSource(string key) => SourceInvalidated?.Invoke(key);

    internal async Task SaveWidgetCredentialsAsync(AccountLease lease, StoredWidgetCredential[] credentials, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1, credentials), ct);
        }
        finally { gate.Release(); }
    }

    private void Reset()
    {
        account = null;
        Interlocked.Increment(ref generation);
        Invalidated?.Invoke();
    }

    private Task ClearWidgetCredentialsAsync(CancellationToken ct) =>
        store.WriteAsync("widget-credentials", new WidgetCredentialStore(1, []), ct);

    private static string FailureCode(Exception? failure) => failure switch
    {
        OutlookException error => error.Code,
        InvalidOperationException => "not_configured",
        _ => "sign_in_required"
    };

    private static OutlookException NormalizeFailure(Exception failure) => failure switch
    {
        OutlookException error => error,
        InvalidOperationException => new("not_configured", "Configure the Microsoft application first.", 401),
        _ => new("sign_in_required", "Sign in to Microsoft to use widgets.", 401)
    };

    private sealed class RequestScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
