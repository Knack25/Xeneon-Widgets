using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerCoordinator(IPlannerSettingsStore settingsStore, PlannerDisplayService displayService,
    PlannerDataLifecycle lifecycle)
{
    internal PlannerCoordinator(IPlannerSettingsStore settingsStore, PlannerDisplayService displayService)
        : this(settingsStore, displayService,
            new PlannerDataLifecycle(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()))) { }
    public async Task<BoardDisplay?> GetCachedDisplayAsync(CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId)) return null;

        var cached = await settingsStore.LoadCachedDisplayAsync(cancellationToken);
        return cached?.PlanId == settings.SelectedPlanId ? cached : null;
    }

    public async Task<BoardDisplay?> GetDisplayAsync(CancellationToken cancellationToken)
    {
        using var operation = lifecycle.BindOperation();
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId)) return null;

        try
        {
            var display = await displayService.GetDisplayAsync(settings.SelectedPlanId,
                settings.SelectedPlanTitle ?? "Selected board", settings.HideCompletedTasks, cancellationToken);
            await settingsStore.SaveCachedDisplayAsync(display, cancellationToken);
            return display;
        }
        catch (Graph.GraphApiException exception) when (exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            await lifecycle.PurgeAsync(CancellationToken.None);
            throw;
        }
        catch (HttpRequestException)
        {
            var cached = await settingsStore.LoadCachedDisplayAsync(cancellationToken);
            if (cached?.PlanId == settings.SelectedPlanId) return cached;
            throw;
        }
        catch (Graph.GraphApiException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
            (int)exception.StatusCode >= 500)
        {
            var cached = await settingsStore.LoadCachedDisplayAsync(cancellationToken);
            if (cached?.PlanId == settings.SelectedPlanId) return cached;
            throw;
        }
    }
}
