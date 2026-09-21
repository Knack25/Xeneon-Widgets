using PlannerEdge.Helper.Contracts;

namespace PlannerEdge.Helper.Storage;

public interface IPlannerSettingsStore
{
    Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken);
    Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken);
    Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken);
    Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken);
}

public sealed class PlannerSettingsStore(ILocalJsonStore jsonStore) : IPlannerSettingsStore
{
    private const string SettingsFileName = "settings";
    private const string CachedDisplayFileName = "cached-display";

    public async Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        return await jsonStore.ReadAsync<SettingsDto>(SettingsFileName, cancellationToken)
            ?? new SettingsDto(null, null, HideCompletedTasks: true);
    }

    public async Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
    {
        if (settings.PlanViews is null)
        {
            var current = await jsonStore.ReadAsync<SettingsDto>(SettingsFileName, cancellationToken);
            settings = settings with { PlanViews = current?.PlanViews };
        }

        await jsonStore.WriteAsync(SettingsFileName, settings, cancellationToken);
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
}
