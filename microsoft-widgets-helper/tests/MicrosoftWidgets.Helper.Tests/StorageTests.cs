using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SettingsStore_LoadsLegacyThreeFieldSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
            """{"selectedPlanId":"plan-1","selectedPlanTitle":"Launch Board","hideCompletedTasks":false}""");
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);

        Assert.Equal("plan-1", loaded.SelectedPlanId);
        Assert.False(loaded.HideCompletedTasks);
        Assert.Null(loaded.PlanViews);
    }

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
    public async Task SettingsStore_LegacySettingsWritePreservesPlanViews()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new PlannerSettingsStore(new LocalJsonStore(root));
        var preferences = new PlanViewPreferences(true, new PlannerFilterSettings([], [], [1], [], [], null));
        await store.SaveSettingsAsync(new SettingsDto("plan-1", "Launch Board", true,
            new Dictionary<string, PlanViewPreferences> { ["plan-1"] = preferences }), CancellationToken.None);

        await store.SaveSettingsAsync(new SettingsDto("plan-1", "Launch Board", false), CancellationToken.None);

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);
        Assert.False(loaded.HideCompletedTasks);
        var loadedPreferences = loaded.PlanViews!["plan-1"];
        Assert.True(loadedPreferences.MyTasks);
        Assert.Equal([1], loadedPreferences.Filters.Priorities);
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

    [Fact]
    public async Task SettingsStore_LoadsLegacyCachedDisplay()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "cached-display.json"), """
            {
              "planId": "plan-1",
              "planTitle": "Launch Board",
              "syncedAt": "2026-09-16T12:30:00+00:00",
              "isStale": false,
              "buckets": [{
                "bucketId": "bucket-1",
                "name": "Backlog",
                "tasks": [{
                  "taskId": "task-1",
                  "title": "Legacy task",
                  "bucketId": "bucket-1",
                  "dueDateTime": null,
                  "priority": null,
                  "percentComplete": 0,
                  "eTag": "etag",
                  "assignments": []
                }]
              }]
            }
            """);
        var store = new PlannerSettingsStore(new LocalJsonStore(root));

        var loaded = await store.LoadCachedDisplayAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("Legacy task", Assert.Single(Assert.Single(loaded.Buckets).Tasks).Title);
        Assert.Null(loaded.Labels);
        Assert.True(loaded.IsStale);
    }
}
