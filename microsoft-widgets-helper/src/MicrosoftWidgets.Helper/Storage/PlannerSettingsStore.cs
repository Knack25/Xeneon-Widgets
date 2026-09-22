using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Storage;

public interface IPlannerSettingsStore
{
    Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken);
    async Task<SettingsDto> UpdateSettingsAsync(Func<SettingsDto, SettingsDto> update,
        CancellationToken cancellationToken)
    {
        var current = await LoadSettingsAsync(cancellationToken);
        var updated = update(current);
        await SaveSettingsAsync(updated, cancellationToken);
        return updated;
    }
    Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken);
    Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken);
    Task PurgeWorkDataAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record PlannerSnapshot(int Version, string AccountKey, DateTimeOffset SavedAt, BoardDisplay Display);
public sealed record PlannerSettingsSnapshot(int Version, string AccountKey, SettingsDto Settings);
public sealed record PlannerSafePreferences(bool HideCompletedTasks);

public sealed class PlannerSettingsStore(
    ILocalJsonStore jsonStore,
    MicrosoftAccountState accountState,
    TimeProvider timeProvider) : IPlannerSettingsStore
{
    internal PlannerSettingsStore(ILocalJsonStore jsonStore)
        : this(jsonStore, new MicrosoftAccountState(new TestIdentityProvider(), jsonStore), TimeProvider.System) { }
    private const string SettingsFileName = "settings";
    private const string CachedDisplayFileName = "cached-display";
    private const string UiPreferencesFileName = "planner-ui-preferences";
    private const int CurrentVersion = 1;
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim settingsLock = new(1, 1);

    public async Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var result = await LoadSettingsCoreAsync(lease, cancellationToken);
            accountState.RequireCurrent(lease);
            return result;
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            if (settings.PlanViews is null)
            {
                var current = await LoadSettingsCoreAsync(lease, cancellationToken);
                settings = settings with { PlanViews = current.PlanViews };
            }

            await SaveSettingsCoreAsync(lease, settings, cancellationToken);
            accountState.RequireCurrent(lease);
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task<SettingsDto> UpdateSettingsAsync(Func<SettingsDto, SettingsDto> update,
        CancellationToken cancellationToken)
    {
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var updated = update(await LoadSettingsCoreAsync(lease, cancellationToken));
            await SaveSettingsCoreAsync(lease, updated, cancellationToken);
            accountState.RequireCurrent(lease);
            return updated;
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken)
    {
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var snapshot = await jsonStore.ReadAsync<PlannerSnapshot>(CachedDisplayFileName, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (snapshot is not { Version: CurrentVersion } || snapshot.AccountKey != lease.Key ||
                snapshot.SavedAt > now || now - snapshot.SavedAt > MaximumSnapshotAge)
                return null;
            accountState.RequireCurrent(lease);
            return snapshot.Display with { IsStale = true };
        }
        finally { settingsLock.Release(); }
    }

    public async Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken)
    {
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            await jsonStore.WriteAsync(CachedDisplayFileName,
                new PlannerSnapshot(CurrentVersion, lease.Key, timeProvider.GetUtcNow(), display with { IsStale = false }),
                cancellationToken);
            accountState.RequireCurrent(lease);
        }
        finally { settingsLock.Release(); }
    }

    public async Task PurgeWorkDataAsync(CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            var safe = await jsonStore.ReadAsync<PlannerSafePreferences>(UiPreferencesFileName, cancellationToken)
                ?? new PlannerSafePreferences(true);
            await jsonStore.WriteAsync<PlannerSettingsSnapshot?>(SettingsFileName, null, cancellationToken);
            await jsonStore.WriteAsync<PlannerSnapshot?>(CachedDisplayFileName, null, cancellationToken);
            await jsonStore.WriteAsync(UiPreferencesFileName, safe, cancellationToken);
        }
        finally { settingsLock.Release(); }
    }

    private async Task<SettingsDto> LoadSettingsCoreAsync(AccountLease lease, CancellationToken cancellationToken)
    {
        var safe = await jsonStore.ReadAsync<PlannerSafePreferences>(UiPreferencesFileName, cancellationToken)
            ?? new PlannerSafePreferences(true);
        var stored = await jsonStore.ReadAsync<PlannerSettingsSnapshot>(SettingsFileName, cancellationToken);
        if (stored is not { Version: CurrentVersion } || stored.AccountKey != lease.Key || stored.Settings is null)
            return new SettingsDto(null, null, safe.HideCompletedTasks);
        return stored.Settings with { HideCompletedTasks = safe.HideCompletedTasks };
    }

    private async Task SaveSettingsCoreAsync(AccountLease lease, SettingsDto settings,
        CancellationToken cancellationToken)
    {
        await jsonStore.WriteAsync(UiPreferencesFileName,
            new PlannerSafePreferences(settings.HideCompletedTasks), cancellationToken);
        await jsonStore.WriteAsync(SettingsFileName,
            new PlannerSettingsSnapshot(CurrentVersion, lease.Key, settings), cancellationToken);
    }

    private sealed class TestIdentityProvider : IMicrosoftAccountIdentityProvider
    {
        public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MicrosoftAccountIdentity("test-home", "test-tenant", "test-client", "test@example.invalid"));
    }
}
