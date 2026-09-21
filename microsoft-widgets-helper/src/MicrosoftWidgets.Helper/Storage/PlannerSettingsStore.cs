using PlannerEdge.Helper.Contracts;

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
}

public sealed class PlannerSettingsStore(ILocalJsonStore jsonStore) : IPlannerSettingsStore
{
    private const string SettingsFileName = "settings";
    private const string CachedDisplayFileName = "cached-display";
    private readonly SemaphoreSlim settingsLock = new(1, 1);

    public async Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            return await LoadSettingsCoreAsync(cancellationToken);
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            if (settings.PlanViews is null)
            {
                var current = await LoadSettingsCoreAsync(cancellationToken);
                settings = settings with { PlanViews = current.PlanViews };
            }

            await jsonStore.WriteAsync(SettingsFileName, settings, cancellationToken);
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task<SettingsDto> UpdateSettingsAsync(Func<SettingsDto, SettingsDto> update,
        CancellationToken cancellationToken)
    {
        await settingsLock.WaitAsync(cancellationToken);
        try
        {
            var updated = update(await LoadSettingsCoreAsync(cancellationToken));
            await jsonStore.WriteAsync(SettingsFileName, updated, cancellationToken);
            return updated;
        }
        finally
        {
            settingsLock.Release();
        }
    }

    public async Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken)
    {
        var display = await jsonStore.ReadAsync<BoardDisplay>(CachedDisplayFileName, cancellationToken);
        return display is null ? null : display with { IsStale = true };
    }

    public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken)
    {
        return jsonStore.WriteAsync(CachedDisplayFileName, display with { IsStale = false }, cancellationToken);
    }

    private async Task<SettingsDto> LoadSettingsCoreAsync(CancellationToken cancellationToken) =>
        await jsonStore.ReadAsync<SettingsDto>(SettingsFileName, cancellationToken)
            ?? new SettingsDto(null, null, HideCompletedTasks: true);
}
