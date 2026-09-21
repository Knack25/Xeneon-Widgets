using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerViewPreferenceServiceTests
{
    [Fact]
    public async Task Preferences_AreIndependentPerPlanAndWritesPreserveOtherPlans()
    {
        var store = new FakeStore(new SettingsDto("plan-a", "Plan A", true,
            new Dictionary<string, PlanViewPreferences>
            {
                ["plan-b"] = Preferences(myTasks: true)
            }));
        var service = new PlannerViewPreferenceService(store);

        await service.SaveAsync("plan-a", Preferences(myTasks: true), CancellationToken.None);

        Assert.True((await service.GetAsync("plan-a", CancellationToken.None)).MyTasks);
        Assert.True(store.Current.PlanViews!["plan-b"].MyTasks);
        store.Current = store.Current with { SelectedPlanId = "plan-b" };
        Assert.True((await service.GetAsync("plan-b", CancellationToken.None)).MyTasks);
        store.Current = store.Current with { SelectedPlanId = "plan-c" };
        Assert.False((await service.GetAsync("plan-c", CancellationToken.None)).MyTasks);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefaultsForLegacySettings()
    {
        var service = new PlannerViewPreferenceService(
            new FakeStore(new SettingsDto("plan-a", "Plan A", true)));

        var preferences = await service.GetAsync("plan-a", CancellationToken.None);

        Assert.False(preferences.MyTasks);
        Assert.Empty(preferences.Filters.AssigneeIds);
        Assert.Empty(preferences.Filters.LabelIds);
        Assert.Empty(preferences.Filters.Priorities);
        Assert.Empty(preferences.Filters.BucketIds);
        Assert.Empty(preferences.Filters.ProgressValues);
        Assert.Null(preferences.Filters.DueDateRange);
    }

    [Fact]
    public async Task SaveAsync_NormalizesPersistedFilters()
    {
        var store = new FakeStore(new SettingsDto("plan-a", "Plan A", true));
        var service = new PlannerViewPreferenceService(store);
        var preferences = new PlanViewPreferences(true, new PlannerFilterSettings(
            ["person", "person", ""], ["label", "label", " "], [1, 2, 3, 9],
            ["bucket", "bucket", ""], [0, 10, 50, 100], "tomorrow"));

        var saved = await service.SaveAsync("plan-a", preferences, CancellationToken.None);

        Assert.Equal(["person"], saved.Filters.AssigneeIds);
        Assert.Equal(["label"], saved.Filters.LabelIds);
        Assert.Equal([1, 3, 9], saved.Filters.Priorities);
        Assert.Equal(["bucket"], saved.Filters.BucketIds);
        Assert.Equal([0, 50, 100], saved.Filters.ProgressValues);
        Assert.Null(saved.Filters.DueDateRange);
    }

    [Fact]
    public async Task GetAndSave_RejectPlansOtherThanSelectedPlan()
    {
        var service = new PlannerViewPreferenceService(
            new FakeStore(new SettingsDto("plan-a", "Plan A", true)));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetAsync("plan-b", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.SaveAsync("plan-b", Preferences(myTasks: true), CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_InterleavedWithGeneralSettingsWriteLosesNeitherUpdate()
    {
        var planB = Preferences(myTasks: true);
        var store = new InterleavingStore(new SettingsDto("plan-a", "Plan A", true,
            new Dictionary<string, PlanViewPreferences> { ["plan-b"] = planB }));
        var service = new PlannerViewPreferenceService(store);

        var preferenceWrite = service.SaveAsync("plan-a", Preferences(myTasks: true), CancellationToken.None);
        await store.PreferenceOperationStarted;
        var selectedPlanWrite = store.SaveSettingsAsync(
            new SettingsDto("plan-new", "New plan", false), CancellationToken.None);
        store.AllowPreferenceWrite();
        await Task.WhenAll(preferenceWrite, selectedPlanWrite);

        Assert.Equal("plan-new", store.Current.SelectedPlanId);
        Assert.False(store.Current.HideCompletedTasks);
        Assert.True(store.Current.PlanViews!["plan-a"].MyTasks);
        Assert.Equal(planB, store.Current.PlanViews["plan-b"]);
    }

    private static PlanViewPreferences Preferences(bool myTasks) => new(myTasks,
        new PlannerFilterSettings([], [], [], [], [], null));

    private sealed class FakeStore(SettingsDto current) : IPlannerSettingsStore
    {
        public SettingsDto Current { get; set; } = current;

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
        {
            Current = settings;
            return Task.CompletedTask;
        }

        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class InterleavingStore(SettingsDto current) : IPlannerSettingsStore
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        private readonly TaskCompletionSource preferenceStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowPreferenceWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SettingsDto Current { get; private set; } = current;
        public Task PreferenceOperationStarted => preferenceStarted.Task;

        public void AllowPreferenceWrite() => allowPreferenceWrite.TrySetResult();

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
        {
            preferenceStarted.TrySetResult();
            return Task.FromResult(Current);
        }

        public async Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken)
        {
            if (settings.PlanViews?.ContainsKey("plan-a") == true &&
                Current.PlanViews?.ContainsKey("plan-a") != true)
            {
                await allowPreferenceWrite.Task.WaitAsync(cancellationToken);
            }

            await writeLock.WaitAsync(cancellationToken);
            try
            {
                Current = settings.PlanViews is null
                    ? settings with { PlanViews = Current.PlanViews }
                    : settings;
            }
            finally
            {
                writeLock.Release();
            }
        }

        public async Task<SettingsDto> UpdateSettingsAsync(Func<SettingsDto, SettingsDto> update,
            CancellationToken cancellationToken)
        {
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                preferenceStarted.TrySetResult();
                await allowPreferenceWrite.Task.WaitAsync(cancellationToken);
                Current = update(Current);
                return Current;
            }
            finally
            {
                writeLock.Release();
            }
        }

        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
