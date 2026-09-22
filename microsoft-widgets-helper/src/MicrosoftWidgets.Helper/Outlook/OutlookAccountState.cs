using PlannerEdge.Helper.Storage;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Outlook;

public readonly record struct OutlookAccountLease(string Key, long Generation);

// All long-running reads validate a lease before publishing data after an account reset.
public sealed class OutlookAccountState(IOutlookTokenProvider tokens, ILocalJsonStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? account;
    private long generation;
    private bool loaded;
    private string? persistedAccount;
    private readonly AsyncLocal<OutlookAccountLease?> authorizedRequest = new();
    public event Action? Invalidated;
    public event Action<string>? SourceInvalidated;

    public Task<OutlookAccountLease> GetAsync(CancellationToken ct) => GetIdentityAsync(true, ct);

    public async Task<OutlookAccountLease> GetIdentityAsync(bool requireAccount, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await SynchronizeIdentityLockedAsync(requireAccount, ct); }
        finally { gate.Release(); }
    }

    private async Task<OutlookAccountLease> SynchronizeIdentityLockedAsync(bool requireAccount, CancellationToken ct)
    {
        if (!loaded)
        {
            persistedAccount = await store.ReadAsync<string>("outlook-account", ct);
            // Legacy credentials have no scope and must never authorize shared routes.
            await store.WriteAsync("outlook-credentials", Array.Empty<StoredOutlookCredential>(), ct);
            loaded = true;
        }
        string key;
        OutlookException? failure = null;
        try { key = await tokens.GetAccountKeyAsync(ct); }
        catch (OutlookException ex) when (ex.Code is "not_configured" or "sign_in_required")
        {
            key = "setup:" + ex.Code;
            failure = ex;
        }
        if (persistedAccount != key)
        {
            await store.WriteAsync("outlook-credentials", Array.Empty<StoredOutlookCredential>(), ct);
            await ClearWidgetCredentialsAsync(ct);
            await store.WriteAsync("outlook-account", key, ct);
            persistedAccount = key;
        }
        if (account != key) { Reset(); account = key; }
        if (authorizedRequest.Value is { } required) RequireCurrent(required);
        if (requireAccount && failure is not null) throw failure;
        return new(key, generation);
    }

    // The delegate performs only the auth mutation, never another state operation.
    // This gate also covers meeting launch and the actual response serialization/write.
    public async Task<T> TransitionAsync<T>(Func<Task<T>> transition, CancellationToken cancellationToken, bool forceInvalidate = false)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await SynchronizeIdentityLockedAsync(false, cancellationToken);
            try { return await transition(); }
            finally
            {
                if (forceInvalidate)
                {
                    Reset();
                    await store.WriteAsync("outlook-credentials", Array.Empty<StoredOutlookCredential>(), CancellationToken.None);
                    await ClearWidgetCredentialsAsync(CancellationToken.None);
                }
                await SynchronizeIdentityLockedAsync(false, CancellationToken.None);
            }
        }
        finally { gate.Release(); }
    }

    public bool IsCurrent(OutlookAccountLease lease) => lease.Key == account && lease.Generation == Interlocked.Read(ref generation);
    public void RequireCurrent(OutlookAccountLease lease)
    {
        if (!IsCurrent(lease) || authorizedRequest.Value is { } required && required != lease)
            throw new OutlookException("account_changed", "The Microsoft account changed. Reconnect Outlook.", 401);
    }

    internal IDisposable BindRequest(OutlookAccountLease lease)
    {
        RequireCurrent(lease);
        var previous = authorizedRequest.Value;
        authorizedRequest.Value = lease;
        return new RequestScope(() => authorizedRequest.Value = previous);
    }

    private sealed class RequestScope(Action restore) : IDisposable { public void Dispose() => restore(); }

    internal async Task<StoredWidgetCredential[]> ReadWidgetCredentialsAsync(OutlookAccountLease lease, CancellationToken ct)
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

    internal Task LaunchAsync(OutlookAccountLease lease, Func<Task> launch, CancellationToken ct) => ExecuteAuthorizedAsync(lease, launch, ct);

    internal async Task ExecuteAuthorizedAsync(OutlookAccountLease lease, Func<Task> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            await action();
        }
        finally { gate.Release(); }
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Reset();
            await store.WriteAsync("outlook-credentials", Array.Empty<StoredOutlookCredential>(), cancellationToken);
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
    private void Reset()
    {
        account = null;
        Interlocked.Increment(ref generation);
        Invalidated?.Invoke();
    }

    internal async Task SaveWidgetCredentialsAsync(OutlookAccountLease lease, StoredWidgetCredential[] credentials, CancellationToken ct)
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

    private Task ClearWidgetCredentialsAsync(CancellationToken ct) => store.WriteAsync("widget-credentials", new WidgetCredentialStore(1, []), ct);
}

public sealed record StoredOutlookCredential(string CredentialId, string InstanceId, string AccountKey, string Hash);
