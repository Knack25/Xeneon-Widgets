using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerDataLifecycle
{
    private readonly MicrosoftAccountState? accountState;
    private readonly IPlannerSettingsStore? settingsStore;
    private readonly IMemoryCache cache;
    private readonly ConcurrentDictionary<string, byte> memoryKeys = new(StringComparer.Ordinal);

    [ActivatorUtilitiesConstructor]
    public PlannerDataLifecycle(MicrosoftAccountState accountState, IPlannerSettingsStore settingsStore,
        IMemoryCache cache)
    {
        this.accountState = accountState;
        this.settingsStore = settingsStore;
        this.cache = cache;
        accountState.Invalidated += PurgeAfterInvalidation;
    }

    internal PlannerDataLifecycle(IMemoryCache cache)
    {
        this.cache = cache;
    }

    public async Task<(bool Found, T? Value)> TryGetAsync<T>(string category, string id,
        CancellationToken cancellationToken)
    {
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            memoryKeys.TryAdd(legacyKey, 0);
            return cache.TryGetValue<T>(legacyKey, out var legacyValue) ? (true, legacyValue) : (false, default);
        }
        var lease = await accountState.GetAsync(cancellationToken);
        var key = Key(category, id);
        memoryKeys.TryAdd(key, 0);
        if (!cache.TryGetValue<AccountBoundValue<T>>(key, out var stored) || stored is null)
            return (false, default);
        if (stored.Lease != lease)
        {
            cache.Remove(key);
            memoryKeys.TryRemove(key, out _);
            return (false, default);
        }
        accountState.RequireCurrent(lease);
        return (true, stored.Value);
    }

    public async Task SetAsync<T>(string category, string id, T value, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            memoryKeys.TryAdd(legacyKey, 0);
            cache.Set(legacyKey, value, lifetime);
            return;
        }
        var lease = await accountState.GetAsync(cancellationToken);
        accountState.RequireCurrent(lease);
        var key = Key(category, id);
        memoryKeys.TryAdd(key, 0);
        try
        {
            cache.Set(key, new AccountBoundValue<T>(lease, value), lifetime);
            accountState.RequireCurrent(lease);
        }
        catch
        {
            cache.Remove(key);
            memoryKeys.TryRemove(key, out _);
            throw;
        }
    }

    public async Task RemoveAsync(string category, string id, CancellationToken cancellationToken = default)
    {
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            cache.Remove(legacyKey);
            memoryKeys.TryRemove(legacyKey, out _);
            return;
        }
        var lease = await accountState.GetAsync(cancellationToken);
        var key = Key(category, id);
        cache.Remove(key);
        memoryKeys.TryRemove(key, out _);
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        PurgeMemory();
        if (settingsStore is not null) await settingsStore.PurgeWorkDataAsync(cancellationToken);
    }

    private void PurgeAfterInvalidation() => PurgeAsync(CancellationToken.None).GetAwaiter().GetResult();

    private void PurgeMemory()
    {
        foreach (var key in memoryKeys.Keys)
        {
            cache.Remove(key);
            memoryKeys.TryRemove(key, out _);
        }
    }

    private static string Key(string category, string id) => category switch
    {
        "plans" => "plans",
        "task-details" => id,
        "user-name" => $"user:{id}",
        _ => $"planner:{category}:{id}"
    };

    private sealed record AccountBoundValue<T>(AccountLease Lease, T Value);
}
