using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SettingsStore_PersistsSelectedPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        await store.SaveSettingsAsync(new SettingsDto("plan-1", "Launch Board", HideCompletedTasks: true), CancellationToken.None);

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);
        Assert.Equal("plan-1", loaded.SelectedPlanId);
        Assert.Equal("Launch Board", loaded.SelectedPlanTitle);
        Assert.True(loaded.HideCompletedTasks);
    }

    [Fact]
    public async Task SettingsStore_ReturnsDefaultsWhenNoSettingsExist()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);

        Assert.Null(loaded.SelectedPlanId);
        Assert.Null(loaded.SelectedPlanTitle);
        Assert.True(loaded.HideCompletedTasks);
    }

    [Fact]
    public async Task SettingsStore_PersistsCachedDisplayAsStaleOnRead()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));
        var display = new BoardDisplay(
            "plan-1",
            "Launch Board",
            new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero),
            IsStale: false,
            []);

        await store.SaveCachedDisplayAsync(display, CancellationToken.None);

        var loaded = await store.LoadCachedDisplayAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("plan-1", loaded.PlanId);
        Assert.True(loaded.IsStale);
    }
}
