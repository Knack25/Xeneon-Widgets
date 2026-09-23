using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerDataLifecycle : IHostedService
{
    private readonly MicrosoftAccountState? accountState;
    private readonly IPlannerSettingsStore? settingsStore;
    private readonly IMemoryCache cache;
    private readonly PlannerDataAccessGate accessGate;
    private readonly ConcurrentDictionary<string, byte> memoryKeys = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim purgeGate = new(1, 1);
    private long purgeVersion;
    private int purgeRequired;
    private readonly object workerSync = new();
    private readonly CancellationTokenSource workerShutdown = new();
    private Task? purgeWorker;
    private int started;

    [ActivatorUtilitiesConstructor]
    public PlannerDataLifecycle(MicrosoftAccountState accountState, IPlannerSettingsStore settingsStore,
        IMemoryCache cache, PlannerDataAccessGate accessGate)
    {
        this.accountState = accountState;
        this.settingsStore = settingsStore;
        this.cache = cache;
        this.accessGate = accessGate;
        accountState.Invalidated += OnInvalidated;
    }

    internal PlannerDataLifecycle(MicrosoftAccountState accountState, IPlannerSettingsStore settingsStore,
        IMemoryCache cache) : this(accountState, settingsStore, cache, new PlannerDataAccessGate()) { }

    internal PlannerDataLifecycle(IMemoryCache cache) : this(cache, new PlannerDataAccessGate()) { }

    internal PlannerDataLifecycle(IMemoryCache cache, PlannerDataAccessGate accessGate)
    {
        this.cache = cache;
        this.accessGate = accessGate;
    }

    public PlannerDataTicket CaptureTicket() => accessGate.CaptureTicket();
    public IDisposable BindOperation() => accessGate.BindOperation();
    public void RequireCurrent(PlannerDataTicket ticket) => accessGate.RequireCurrent(ticket);
    public Task ExecutePublicationAsync(PlannerDataTicket ticket, Func<Task> publication,
        CancellationToken cancellationToken) =>
        accessGate.ExecutePublicationAsync(ticket, publication, cancellationToken);

    public async Task<(bool Found, T? Value)> TryGetAsync<T>(string category, string id,
        CancellationToken cancellationToken)
    {
        var ticket = accessGate.CaptureTicket();
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            memoryKeys.TryAdd(legacyKey, 0);
            var result = cache.TryGetValue<T>(legacyKey, out var legacyValue)
                ? (true, legacyValue)
                : (false, default);
            accessGate.RequireCurrent(ticket);
            return result;
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
        accessGate.RequireCurrent(ticket);
        return (true, stored.Value);
    }

    public async Task SetAsync<T>(string category, string id, T value, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var ticket = accessGate.CaptureTicket();
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            await accessGate.ExecutePublicationAsync(ticket, () =>
            {
                memoryKeys.TryAdd(legacyKey, 0);
                cache.Set(legacyKey, value, lifetime);
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }
        var lease = await accountState.GetAsync(cancellationToken);
        accountState.RequireCurrent(lease);
        var key = Key(category, id);
        try
        {
            await accessGate.ExecutePublicationAsync(ticket, () =>
            {
                accountState.RequireCurrent(lease);
                memoryKeys.TryAdd(key, 0);
                cache.Set(key, new AccountBoundValue<T>(lease, value), lifetime);
                accountState.RequireCurrent(lease);
                return Task.CompletedTask;
            }, cancellationToken);
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
        var ticket = accessGate.CaptureTicket();
        if (accountState is null)
        {
            var legacyKey = $"planner:test:{category}:{id}";
            await accessGate.ExecutePublicationAsync(ticket, () =>
            {
                cache.Remove(legacyKey);
                memoryKeys.TryRemove(legacyKey, out _);
                return Task.CompletedTask;
            }, cancellationToken);
            return;
        }
        await accountState.GetAsync(cancellationToken);
        var key = Key(category, id);
        await accessGate.ExecutePublicationAsync(ticket, () =>
        {
            cache.Remove(key);
            memoryKeys.TryRemove(key, out _);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await BeginPurgeAsync(cancellationToken);
        StartPurgeWorker();
        await RetryPendingPurgeAsync(cancellationToken);
    }

    public bool PurgeRequired => Volatile.Read(ref purgeRequired) != 0;
    public bool ReadyForWork => !PurgeRequired;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (settingsStore is null || Interlocked.Exchange(ref started, 1) != 0) return;

        // The active marker is durable before the helper can serve Planner data. A crash leaves
        // it behind, so the next runtime purges before treating any prior data as recoverable.
        var requiresRecovery = await settingsStore.IsPurgeRequiredAsync(cancellationToken);
        await settingsStore.MarkPurgeRequiredAsync(cancellationToken);
        if (!requiresRecovery) return;

        await BeginPurgeAsync(cancellationToken);
        try { await RetryPendingPurgeAsync(cancellationToken); }
        catch when (!cancellationToken.IsCancellationRequested) { StartPurgeWorker(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (settingsStore is null || Volatile.Read(ref started) == 0) return;
        if (accountState is not null) accountState.Invalidated -= OnInvalidated;

        while (PurgeRequired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await RetryPendingPurgeAsync(cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        await settingsStore.ClearPurgeRequiredAsync(cancellationToken);
        workerShutdown.Cancel();
        Task? worker;
        lock (workerSync) worker = purgeWorker;
        if (worker is not null)
        {
            try { await worker.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (workerShutdown.IsCancellationRequested) { }
        }
    }

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
                await settingsStore.PurgeWorkDataAsync(cancellationToken);
                if (version != Interlocked.Read(ref purgeVersion)) continue;
                if (version == Interlocked.Read(ref purgeVersion))
                    Volatile.Write(ref purgeRequired, 0);
            }
        }
        finally { purgeGate.Release(); }
    }

    private void OnInvalidated()
    {
        BeginPurgeAsync(CancellationToken.None).GetAwaiter().GetResult();
        StartPurgeWorker();
    }

    private Task BeginPurgeAsync(CancellationToken cancellationToken) => accessGate.AdvanceAsync(() =>
    {
        PurgeMemory();
        Interlocked.Increment(ref purgeVersion);
        Volatile.Write(ref purgeRequired, 1);
    }, cancellationToken);

    private void StartPurgeWorker()
    {
        lock (workerSync)
        {
            if (purgeWorker is { IsCompleted: false }) return;
            purgeWorker = Task.Run(async () =>
            {
                var attempt = 0;
                while (PurgeRequired && !workerShutdown.IsCancellationRequested)
                {
                    try
                    {
                        await RetryPendingPurgeAsync(workerShutdown.Token);
                        return;
                    }
                    catch (OperationCanceledException) when (workerShutdown.IsCancellationRequested) { return; }
                    catch (Exception error)
                    {
                        System.Diagnostics.Trace.TraceError("Planner purge attempt failed: {0}", error);
                        attempt++;
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, 50 * attempt)), workerShutdown.Token);
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
