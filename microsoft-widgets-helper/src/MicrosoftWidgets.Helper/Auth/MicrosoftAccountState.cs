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
    private readonly AsyncLocal<AccountExecutionScope?> executingRequest = new();
    private readonly AsyncLocal<LocalAccessService.OwnerAuthorization?> authorizedOwnerRequest = new();
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
        if (TryGetExecutingLease(out var executingLease))
        {
            RequireCurrent(executingLease);
            return executingLease;
        }
        await gate.WaitAsync(ct);
        try { return await SynchronizeIdentityLockedAsync(requireAccount, ct); }
        finally { gate.Release(); }
    }

    private async Task<AccountLease> SynchronizeIdentityLockedAsync(bool requireAccount, CancellationToken ct,
        bool validateOwnerRequest = true)
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
        var lease = new AccountLease(key, generation);
        if (authorizedRequest.Value is { } required) RequireCurrent(required);
        if (validateOwnerRequest) RequireOwnerCurrent(lease);
        if (requireAccount && failure is not null) throw NormalizeFailure(failure);
        return lease;
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
                await SynchronizeIdentityLockedAsync(false, CancellationToken.None, validateOwnerRequest: false);
            }
        }
        finally { gate.Release(); }
    }

    // The completion runs while the account gate is held. It may acquire LocalAccessService's
    // token gate; code holding that token gate must never call back into account state.
    public async Task<TCompletion> TransitionAsync<T, TCompletion>(Func<Task<T>> transition,
        Func<T, AccountLease, AccountLease, Task<TCompletion>> completion, CancellationToken cancellationToken,
        bool forceInvalidate = false, Func<T, bool>? invalidateWhen = null)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await SynchronizeIdentityLockedAsync(false, cancellationToken);
            T? result = default;
            var completed = false;
            AccountLease current;
            try
            {
                result = await transition();
                completed = true;
            }
            finally
            {
                if (forceInvalidate || completed && invalidateWhen?.Invoke(result!) == true)
                {
                    Reset();
                    await ClearWidgetCredentialsAsync(CancellationToken.None);
                }
                current = await SynchronizeIdentityLockedAsync(false, CancellationToken.None, validateOwnerRequest: false);
            }

            return await completion(result!, previous, current);
        }
        finally { gate.Release(); }
    }

    public bool IsCurrent(AccountLease lease) => lease.Key == account && lease.Generation == Interlocked.Read(ref generation);

    public void RequireCurrent(AccountLease lease)
    {
        if (!IsCurrent(lease) || authorizedRequest.Value is { } required && required != lease)
            throw new OutlookException("account_changed", "The Microsoft account changed. Reconnect the widget.", 401);
        RequireOwnerCurrent(lease);
    }

    private void RequireOwnerCurrent(AccountLease lease) => authorizedOwnerRequest.Value?.RequireCurrent(lease);

    internal IDisposable BindOwnerRequest(LocalAccessService.OwnerAuthorization authorization)
    {
        var previous = authorizedOwnerRequest.Value;
        authorizedOwnerRequest.Value = authorization;
        return new RequestScope(() => authorizedOwnerRequest.Value = previous);
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

    internal Task ExecuteAuthorizedAsync(AccountLease lease, Func<Task> action, CancellationToken ct) =>
        ExecuteAuthorizedAsync(lease, async () => { await action(); return true; }, ct);

    internal async Task<T> ExecuteAuthorizedAsync<T>(AccountLease lease, Func<Task<T>> action, CancellationToken ct)
    {
        if (TryEnterNestedExecution(lease, out var nested))
        {
            var nestedPrevious = executingRequest.Value;
            try
            {
                executingRequest.Value = nested.Scope;
                RequireCurrent(lease);
                var result = await action();
                RequireCurrent(lease);
                return result;
            }
            finally
            {
                executingRequest.Value = nestedPrevious;
                nested.Dispose();
            }
        }
        await gate.WaitAsync(ct);
        var previous = executingRequest.Value;
        var execution = new AccountExecution(lease);
        try
        {
            executingRequest.Value = execution.RootScope;
            RequireCurrent(lease);
            await SynchronizeIdentityLockedAsync(false, ct);
            RequireCurrent(lease);
            var result = await action();
            await SynchronizeIdentityLockedAsync(false, CancellationToken.None);
            RequireCurrent(lease);
            return result;
        }
        finally
        {
            await execution.CloseAsync();
            execution.Revoke();
            executingRequest.Value = previous;
            gate.Release();
        }
    }

    internal async Task<T> ExecuteBoundAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var lease = authorizedRequest.Value
            ?? throw new OutlookException("account_changed", "The Microsoft account changed. Reconnect the widget.", 401);
        if (TryEnterNestedExecution(lease, out var nested))
        {
            var nestedPrevious = executingRequest.Value;
            try
            {
                executingRequest.Value = nested.Scope;
                RequireCurrent(lease);
                var result = await action();
                RequireCurrent(lease);
                return result;
            }
            finally
            {
                executingRequest.Value = nestedPrevious;
                nested.Dispose();
            }
        }
        await gate.WaitAsync(ct);
        var previous = executingRequest.Value;
        var execution = new AccountExecution(lease);
        try
        {
            executingRequest.Value = execution.RootScope;
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
        finally
        {
            await execution.CloseAsync();
            execution.Revoke();
            executingRequest.Value = previous;
            gate.Release();
        }
    }

    internal async Task<T> ExecuteOwnerAuthorizedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var authorization = authorizedOwnerRequest.Value ?? throw new OwnerAuthorizationException();
            var lease = await SynchronizeIdentityLockedAsync(false, ct, validateOwnerRequest: false);
            authorization.RequireCurrent(lease);
            return await action();
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
        foreach (Action subscriber in Invalidated?.GetInvocationList() ?? [])
        {
            try { subscriber(); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Account invalidation subscriber failed: {0}", error);
            }
        }
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

    private bool TryEnterNestedExecution(AccountLease lease, out AccountExecutionRegistration registration)
    {
        var current = executingRequest.Value;
        if (current is not null && current.Execution.TryEnter(current, lease, out registration)) return true;
        registration = null!;
        return false;
    }

    private bool TryGetExecutingLease(out AccountLease lease)
    {
        var current = executingRequest.Value;
        if (current is not null && current.Execution.TryGetLease(current, out lease)) return true;
        lease = default;
        return false;
    }

    private sealed class AccountExecution
    {
        private readonly AccountLease lease;
        private readonly object sync = new();
        private TaskCompletionSource? drained;
        private bool active = true;
        private bool accepting = true;
        private int nestedCount;

        public AccountExecution(AccountLease lease)
        {
            this.lease = lease;
            RootScope = new AccountExecutionScope(this, false);
        }

        public AccountExecutionScope RootScope { get; }

        public bool TryEnter(AccountExecutionScope current, AccountLease candidate,
            out AccountExecutionRegistration registration)
        {
            lock (sync)
            {
                if (!active || candidate != lease || !accepting && (!current.Registered || nestedCount == 0))
                {
                    registration = null!;
                    return false;
                }

                nestedCount++;
                registration = new AccountExecutionRegistration(this,
                    new AccountExecutionScope(this, true));
                return true;
            }
        }

        public bool TryGetLease(AccountExecutionScope current, out AccountLease currentLease)
        {
            lock (sync)
            {
                if (active && (accepting || current.Registered && nestedCount > 0))
                {
                    currentLease = lease;
                    return true;
                }
                currentLease = default;
                return false;
            }
        }

        public Task CloseAsync()
        {
            lock (sync)
            {
                accepting = false;
                if (nestedCount == 0) return Task.CompletedTask;
                drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return drained.Task;
            }
        }

        public void Exit()
        {
            TaskCompletionSource? completion = null;
            lock (sync)
            {
                nestedCount--;
                if (!accepting && nestedCount == 0) completion = drained;
            }
            completion?.TrySetResult();
        }

        public void Revoke()
        {
            lock (sync) active = false;
        }
    }

    private sealed class AccountExecutionScope(AccountExecution? execution, bool registered)
    {
        public AccountExecution Execution { get; } = execution!;
        public bool Registered { get; } = registered;
    }

    private sealed class AccountExecutionRegistration(AccountExecution execution, AccountExecutionScope scope)
        : IDisposable
    {
        private int disposed;
        public AccountExecutionScope Scope { get; } = scope;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) execution.Exit();
        }
    }
}
