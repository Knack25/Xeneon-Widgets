using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Planner;

public sealed class PlannerViewPreferenceService(IPlannerSettingsStore settingsStore)
{
    private static readonly int[] ValidPriorities = [1, 3, 5, 9];
    private static readonly int[] ValidProgressValues = [0, 50, 100];
    private static readonly string[] ValidDueDateRanges = ["overdue", "today", "this-week", "later", "no-date"];

    public async Task<PlanViewPreferences> GetAsync(string planId, CancellationToken cancellationToken)
    {
        var settings = await GetSelectedPlanSettingsAsync(planId, cancellationToken);
        if (settings.PlanViews?.TryGetValue(planId, out var preferences) != true)
        {
            return DefaultPreferences();
        }

        return Normalize(preferences);
    }

    public async Task<PlanViewPreferences> SaveAsync(string planId, PlanViewPreferences preferences,
        CancellationToken cancellationToken)
    {
        var settings = await GetSelectedPlanSettingsAsync(planId, cancellationToken);
        var normalized = Normalize(preferences);
        var planViews = settings.PlanViews is null
            ? new Dictionary<string, PlanViewPreferences>(StringComparer.Ordinal)
            : new Dictionary<string, PlanViewPreferences>(settings.PlanViews, StringComparer.Ordinal);
        planViews[planId] = normalized;
        await settingsStore.SaveSettingsAsync(settings with { PlanViews = planViews }, cancellationToken);
        return normalized;
    }

    private async Task<SettingsDto> GetSelectedPlanSettingsAsync(string planId, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(planId) ||
            !string.Equals(settings.SelectedPlanId, planId, StringComparison.Ordinal))
        {
            throw new ArgumentException("View preferences are available only for the selected board.");
        }

        return settings;
    }

    private static PlanViewPreferences DefaultPreferences() =>
        new(false, new PlannerFilterSettings([], [], [], [], [], null));

    private static PlanViewPreferences Normalize(PlanViewPreferences? preferences)
    {
        var filters = preferences?.Filters;
        return new PlanViewPreferences(preferences?.MyTasks ?? false, new PlannerFilterSettings(
            NormalizeIds(filters?.AssigneeIds),
            NormalizeIds(filters?.LabelIds),
            NormalizeValues(filters?.Priorities, ValidPriorities),
            NormalizeIds(filters?.BucketIds),
            NormalizeValues(filters?.ProgressValues, ValidProgressValues),
            ValidDueDateRanges.Contains(filters?.DueDateRange, StringComparer.Ordinal)
                ? filters!.DueDateRange
                : null));
    }

    private static IReadOnlyList<string> NormalizeIds(IReadOnlyList<string>? values) =>
        (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<int> NormalizeValues(IReadOnlyList<int>? values, IReadOnlyCollection<int> allowed) =>
        (values ?? []).Where(allowed.Contains).Distinct().ToArray();
}
