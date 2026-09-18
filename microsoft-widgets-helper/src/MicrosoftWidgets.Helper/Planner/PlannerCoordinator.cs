using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerCoordinator(IPlannerSettingsStore settingsStore, PlannerDisplayService displayService)
{
    public async Task<BoardDisplay?> GetDisplayAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedPlanId)) return null;

        try
        {
            var display = await displayService.GetDisplayAsync(settings.SelectedPlanId,
                settings.SelectedPlanTitle ?? "Selected board", settings.HideCompletedTasks, cancellationToken);
            await settingsStore.SaveCachedDisplayAsync(display, cancellationToken);
            return display;
        }
        catch (Exception exception) when (exception is HttpRequestException or Graph.GraphApiException)
        {
            var cached = await settingsStore.LoadCachedDisplayAsync(cancellationToken);
            if (cached?.PlanId == settings.SelectedPlanId) return cached;
            throw;
        }
    }
}
