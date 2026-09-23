using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Planner;

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
    Task MarkPurgeRequiredAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task<bool> IsPurgeRequiredAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    Task ClearPurgeRequiredAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed record PlannerSnapshot(int Version, string AccountKey, DateTimeOffset SavedAt, BoardDisplay Display);
public sealed record PlannerSettingsSnapshot(int Version, string AccountKey, SettingsDto Settings);
public sealed record PlannerSafePreferences(bool HideCompletedTasks);
public sealed record PlannerPurgeMarker(int Version, bool Required);

public sealed class PlannerSettingsStore : IPlannerSettingsStore
{
    private readonly ILocalJsonStore jsonStore;
    private readonly MicrosoftAccountState accountState;
    private readonly TimeProvider timeProvider;
    private readonly PlannerDataAccessGate accessGate;

    public PlannerSettingsStore(ILocalJsonStore jsonStore, MicrosoftAccountState accountState,
        TimeProvider timeProvider, PlannerDataAccessGate accessGate)
    {
        this.jsonStore = jsonStore;
        this.accountState = accountState;
        this.timeProvider = timeProvider;
        this.accessGate = accessGate;
    }

    internal PlannerSettingsStore(ILocalJsonStore jsonStore, MicrosoftAccountState accountState,
        TimeProvider timeProvider) : this(jsonStore, accountState, timeProvider, new PlannerDataAccessGate()) { }

    internal PlannerSettingsStore(ILocalJsonStore jsonStore)
        : this(jsonStore, new MicrosoftAccountState(new TestIdentityProvider(), jsonStore), TimeProvider.System,
            new PlannerDataAccessGate()) { }
    private const string SettingsFileName = "settings";
    private const string CachedDisplayFileName = "cached-display";
    private const string UiPreferencesFileName = "planner-ui-preferences";
    private const string PurgeMarkerFileName = "planner-purge-required";
    private const int CurrentVersion = 1;
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim settingsLock = new(1, 1);

    public async Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var ticket = accessGate.CaptureTicket();
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var result = await LoadSettingsCoreAsync(lease, ticket, cancellationToken);
            accountState.RequireCurrent(lease);
            accessGate.RequireCurrent(ticket);
            return result;
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        var ticket = accessGate.CaptureTicket();
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            if (settings.PlanViews is null)
            {
                var current = await LoadSettingsCoreAsync(lease, ticket, cancellationToken);
                settings = settings with { PlanViews = current.PlanViews };
            }

            await SaveSettingsCoreAsync(lease, ticket, settings, cancellationToken);
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
        var ticket = accessGate.CaptureTicket();
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var updated = update(await LoadSettingsCoreAsync(lease, ticket, cancellationToken));
            await SaveSettingsCoreAsync(lease, ticket, updated, cancellationToken);
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
        var ticket = accessGate.CaptureTicket();
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            var snapshot = await jsonStore.ReadAsync<PlannerSnapshot>(CachedDisplayFileName, cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (snapshot is not { Version: CurrentVersion } || snapshot.AccountKey != lease.Key ||
                snapshot.SavedAt > now || now - snapshot.SavedAt > MaximumSnapshotAge)
            {
                await accessGate.ExecutePublicationAsync(ticket,
                    () => jsonStore.DeleteAsync(CachedDisplayFileName, cancellationToken), cancellationToken);
                return null;
            }
            accountState.RequireCurrent(lease);
            accessGate.RequireCurrent(ticket);
            return snapshot.Display with { IsStale = true };
        }
        finally { settingsLock.Release(); }
    }

    public async Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken)
    {
        var ticket = accessGate.CaptureTicket();
        var lease = await accountState.GetAsync(cancellationToken);
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            accountState.RequireCurrent(lease);
            await accessGate.ExecutePublicationAsync(ticket, () => jsonStore.WriteAsync(CachedDisplayFileName,
                    new PlannerSnapshot(CurrentVersion, lease.Key, timeProvider.GetUtcNow(), display with { IsStale = false }),
                    cancellationToken), cancellationToken);
            accountState.RequireCurrent(lease);
        }
        finally { settingsLock.Release(); }
    }

    public async Task PurgeWorkDataAsync(CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            PlannerSafePreferences safe;
            try
            {
                safe = await jsonStore.ReadAsync<PlannerSafePreferences>(UiPreferencesFileName, cancellationToken)
                    ?? new PlannerSafePreferences(true);
            }
            catch (System.Text.Json.JsonException)
            {
                safe = new PlannerSafePreferences(true);
            }
            await jsonStore.DeleteAsync(SettingsFileName, cancellationToken);
            await jsonStore.DeleteAsync(CachedDisplayFileName, cancellationToken);
            await jsonStore.WriteAsync(UiPreferencesFileName, safe, cancellationToken);
        }
        finally { settingsLock.Release(); }
    }

    public Task MarkPurgeRequiredAsync(CancellationToken cancellationToken) =>
        jsonStore.WriteAsync(PurgeMarkerFileName, new PlannerPurgeMarker(1, true), cancellationToken);

    public async Task<bool> IsPurgeRequiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Missing or malformed state is an unclean/uncertain previous runtime.
            return await jsonStore.ReadAsync<PlannerPurgeMarker>(PurgeMarkerFileName, cancellationToken)
                is not { Version: 1, Required: false };
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
        catch (InvalidCastException)
        {
            return true;
        }
    }

    public Task ClearPurgeRequiredAsync(CancellationToken cancellationToken) =>
        jsonStore.WriteAsync(PurgeMarkerFileName, new PlannerPurgeMarker(1, false), cancellationToken);

    private async Task<SettingsDto> LoadSettingsCoreAsync(AccountLease lease, PlannerDataTicket ticket,
        CancellationToken cancellationToken)
    {
        var safe = await jsonStore.ReadAsync<PlannerSafePreferences>(UiPreferencesFileName, cancellationToken)
            ?? new PlannerSafePreferences(true);
        var stored = await jsonStore.ReadAsync<PlannerSettingsSnapshot>(SettingsFileName, cancellationToken);
        if (stored is not { Version: CurrentVersion } || stored.AccountKey != lease.Key || stored.Settings is null)
        {
            await accessGate.ExecutePublicationAsync(ticket,
                () => jsonStore.DeleteAsync(SettingsFileName, cancellationToken), cancellationToken);
            return new SettingsDto(null, null, safe.HideCompletedTasks);
        }
        return stored.Settings with { HideCompletedTasks = safe.HideCompletedTasks };
    }

    private Task SaveSettingsCoreAsync(AccountLease lease, PlannerDataTicket ticket, SettingsDto settings,
        CancellationToken cancellationToken)
    {
        return accessGate.ExecutePublicationAsync(ticket, async () =>
        {
            await jsonStore.WriteAsync(UiPreferencesFileName,
                new PlannerSafePreferences(settings.HideCompletedTasks), cancellationToken);
            await jsonStore.WriteAsync(SettingsFileName,
                new PlannerSettingsSnapshot(CurrentVersion, lease.Key, settings), cancellationToken);
        }, cancellationToken);
    }

    private sealed class TestIdentityProvider : IMicrosoftAccountIdentityProvider
    {
        public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MicrosoftAccountIdentity("test-home", "test-tenant", "test-client", "test@example.invalid"));
    }
}
