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
    private readonly SemaphoreSlim purgeGate = new(1, 1);
    private long purgeVersion;
    private int purgeRequired;
    private readonly object workerSync = new();
    private Task? purgeWorker;

    [ActivatorUtilitiesConstructor]
    public PlannerDataLifecycle(MicrosoftAccountState accountState, IPlannerSettingsStore settingsStore,
        IMemoryCache cache)
    {
        this.accountState = accountState;
        this.settingsStore = settingsStore;
        this.cache = cache;
        accountState.Invalidated += OnInvalidated;
        StartRecoveryCheck();
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
        Interlocked.Increment(ref purgeVersion);
        Volatile.Write(ref purgeRequired, 1);
        await RetryPendingPurgeAsync(cancellationToken);
    }

    public bool PurgeRequired => Volatile.Read(ref purgeRequired) != 0;

    public async Task RetryPendingPurgeAsync(CancellationToken cancellationToken)
    {
        if (settingsStore is null)
        {
            Volatile.Write(ref purgeRequired, 0);
            return;
        }

        await purgeGate.WaitAsync(cancellationToken);
        try
        {
            while (PurgeRequired)
            {
                var version = Interlocked.Read(ref purgeVersion);
                await settingsStore.MarkPurgeRequiredAsync(cancellationToken);
                await settingsStore.PurgeWorkDataAsync(cancellationToken);
                if (version != Interlocked.Read(ref purgeVersion)) continue;
                await settingsStore.ClearPurgeRequiredAsync(cancellationToken);
                if (version == Interlocked.Read(ref purgeVersion))
                    Volatile.Write(ref purgeRequired, 0);
            }
        }
        finally { purgeGate.Release(); }
    }

    private void OnInvalidated()
    {
        PurgeMemory();
        Interlocked.Increment(ref purgeVersion);
        Volatile.Write(ref purgeRequired, 1);
        StartPurgeWorker();
    }

    private void StartRecoveryCheck()
    {
        if (settingsStore is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await settingsStore.IsPurgeRequiredAsync(CancellationToken.None))
                {
                    Interlocked.Increment(ref purgeVersion);
                    Volatile.Write(ref purgeRequired, 1);
                    StartPurgeWorker();
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Planner purge recovery check failed: {0}", error);
            }
        });
    }

    private void StartPurgeWorker()
    {
        lock (workerSync)
        {
            if (purgeWorker is { IsCompleted: false }) return;
            purgeWorker = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 5 && PurgeRequired; attempt++)
                {
                    try
                    {
                        await RetryPendingPurgeAsync(CancellationToken.None);
                        return;
                    }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Planner purge attempt failed: {0}", error);
                        if (attempt < 4) await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                    }
                }
            });
        }
    }

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
