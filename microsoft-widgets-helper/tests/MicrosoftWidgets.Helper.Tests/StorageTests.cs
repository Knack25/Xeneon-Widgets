using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task LocalJsonStore_serializes_concurrent_writes_to_the_same_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "MicrosoftWidgetsTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalJsonStore(root);
            var payloads = Enumerable.Range(0, 32)
                .Select(index => new ConcurrentPayload(index, new string((char)('A' + index % 26), 256 * 1024)))
                .ToArray();

            await Task.WhenAll(payloads.Select(payload =>
                store.WriteAsync("microsoft-account-identity", payload, default)));

            var stored = await store.ReadAsync<ConcurrentPayload>("microsoft-account-identity", default);
            Assert.NotNull(stored);
            Assert.Contains(stored, payloads);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalJsonStore_preserves_same_key_write_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "MicrosoftWidgetsTests", Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new LocalJsonStore(root);
            var first = Task.Run(() => store.WriteAsync("ordered", new BlockingPayload(1, started, release), default));
            await started.Task;
            var second = store.WriteAsync("ordered", new ConcurrentPayload(2, "second"), default);
            Assert.False(second.IsCompleted);

            release.TrySetResult();
            await Task.WhenAll(first, second);

            Assert.Equal(2, (await store.ReadAsync<ConcurrentPayload>("ordered", default))!.Sequence);
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalJsonStore_does_not_block_writes_to_different_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), "MicrosoftWidgetsTests", Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new LocalJsonStore(root);
            var blocked = Task.Run(() => store.WriteAsync("first", new BlockingPayload(1, started, release), default));
            await started.Task;

            await store.WriteAsync("second", new ConcurrentPayload(2, "available"), default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, (await store.ReadAsync<ConcurrentPayload>("second", default))!.Sequence);

            release.TrySetResult();
            await blocked;
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalJsonStore_reads_complete_value_while_replacement_is_in_progress()
    {
        var root = Path.Combine(Path.GetTempPath(), "MicrosoftWidgetsTests", Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new LocalJsonStore(root);
            await store.WriteAsync("replace", new ConcurrentPayload(1, "original"), default);
            var replacement = Task.Run(() => store.WriteAsync("replace", new BlockingPayload(2, started, release), default));
            await started.Task;

            Assert.Equal(1, (await store.ReadAsync<ConcurrentPayload>("replace", default))!.Sequence);
            release.TrySetResult();
            await replacement;
            Assert.Equal(2, (await store.ReadAsync<ConcurrentPayload>("replace", default))!.Sequence);
        }
        finally
        {
            release.TrySetResult();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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
    public async Task SettingsStore_AtomicUpdateAndLegacyWriteShareOneLock()
    {
        var planB = new PlanViewPreferences(true, new PlannerFilterSettings([], [], [3], [], [], null));
        var json = new CoordinatedSettingsJsonStore(new SettingsDto("plan-a", "Plan A", true,
            new Dictionary<string, PlanViewPreferences> { ["plan-b"] = planB }));
        var store = new PlannerSettingsStore(json);

        var preferenceWrite = store.UpdateSettingsAsync(settings =>
        {
            var views = new Dictionary<string, PlanViewPreferences>(settings.PlanViews!)
            {
                ["plan-a"] = new(true, new PlannerFilterSettings([], [], [1], [], [], null))
            };
            return settings with { PlanViews = views };
        }, CancellationToken.None);
        await json.FirstReadStarted;
        var selectedPlanWrite = store.SaveSettingsAsync(
            new SettingsDto("plan-new", "New plan", false), CancellationToken.None);

        Assert.False(selectedPlanWrite.IsCompleted);
        json.AllowFirstRead();
        await Task.WhenAll(preferenceWrite, selectedPlanWrite);

        var loaded = await store.LoadSettingsAsync(CancellationToken.None);
        Assert.Equal("plan-new", loaded.SelectedPlanId);
        Assert.False(loaded.HideCompletedTasks);
        Assert.True(loaded.PlanViews!["plan-a"].MyTasks);
        Assert.Equal([3], loaded.PlanViews["plan-b"].Filters.Priorities);
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

    private sealed class CoordinatedSettingsJsonStore(SettingsDto current) : ILocalJsonStore
    {
        private readonly TaskCompletionSource firstReadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource allowFirstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int settingsReadCount;

        public Task FirstReadStarted => firstReadStarted.Task;
        public void AllowFirstRead() => allowFirstRead.TrySetResult();

        public async Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken)
        {
            if (typeof(T) != typeof(SettingsDto)) return default;
            if (Interlocked.Increment(ref settingsReadCount) == 1)
            {
                firstReadStarted.TrySetResult();
                await allowFirstRead.Task.WaitAsync(cancellationToken);
            }

            return (T)(object)current;
        }

        public Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
        {
            current = (SettingsDto)(object)value!;
            return Task.CompletedTask;
        }
    }

    private sealed record ConcurrentPayload(int Sequence, string Content);

    private sealed class BlockingPayload(int sequence, TaskCompletionSource started, TaskCompletionSource release)
    {
        public int Sequence { get; } = sequence;
        public string Content
        {
            get
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return "blocked";
            }
        }
    }
}
