using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerDataLifecycleTests
{
    [Fact]
    public async Task Unclean_runtime_marker_forces_purge_on_next_start()
    {
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(new MutableIdentity(), json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        await settings.ClearPurgeRequiredAsync(default);
        var first = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await first.StartAsync(default);
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);

        Assert.True(await settings.IsPurgeRequiredAsync(default));

        var restarted = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await restarted.StartAsync(default);

        Assert.False(json.Contains("settings"));
        await restarted.StopAsync(default);
    }

    [Fact]
    public async Task Missing_or_malformed_runtime_marker_is_treated_as_unclean()
    {
        foreach (var marker in new object?[] { null, "malformed" })
        {
            var json = new FaultingPlannerJsonStore();
            var account = new MicrosoftAccountState(new MutableIdentity(), json);
            await account.GetAsync(default);
            var settings = new PlannerSettingsStore(json, account,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
            await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
            if (marker is not null) json.Set("planner-purge-required", marker);
            var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));

            await lifecycle.StartAsync(default);

            Assert.False(json.Contains("settings"));
            await lifecycle.StopAsync(default);
        }
    }

    [Fact]
    public async Task Purge_worker_retries_beyond_five_failures_until_recovery()
    {
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(new MutableIdentity(), json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        await settings.ClearPurgeRequiredAsync(default);
        var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await lifecycle.StartAsync(default);
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        json.DeleteFailuresRemaining = 7;

        await account.InvalidateAsync(default);
        await WaitUntilAsync(() => json.DeleteAttempts >= 8 && !lifecycle.PurgeRequired, 10);

        Assert.False(json.Contains("settings"));
        await lifecycle.StopAsync(default);
    }

    [Fact]
    public async Task Direct_purge_retries_transient_failure_without_restart()
    {
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(new MutableIdentity(), json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        json.DeleteFailuresRemaining = 1;

        try { await lifecycle.PurgeAsync(default); }
        catch (IOException) { }

        await WaitUntilAsync(() => !lifecycle.PurgeRequired && !json.Contains("settings"));
        Assert.True(json.DeleteAttempts >= 2);
    }

    [Fact]
    public async Task Board_load_started_before_purge_cannot_publish_after_purge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var graph = PausedPlannerGraph.ForPlans();
        var service = new PlannerBoardService(graph, fixture.Lifecycle);

        var load = service.GetPlansAsync(default);
        await graph.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Lifecycle.PurgeAsync(default);
        graph.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => load);
        Assert.Equal("planner_data_changed", error.Code);
        Assert.False((await fixture.Lifecycle.TryGetAsync<IReadOnlyList<PlanSummary>>(
            "plans", "all", default)).Found);
    }

    [Fact]
    public async Task Settings_selection_started_before_purge_cannot_restore_selected_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var graph = PausedPlannerGraph.ForPlans();
        var service = new BoardSelectionService(new PlannerBoardService(graph, fixture.Lifecycle), fixture.Settings);

        var selection = service.SelectAsync("plan", default);
        await graph.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Lifecycle.PurgeAsync(default);
        graph.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => selection);
        Assert.Equal("planner_data_changed", error.Code);
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
    }

    [Fact]
    public async Task Details_load_started_before_purge_cannot_publish_after_purge()
    {
        await using var fixture = await Fixture.CreateAsync();
        var graph = PausedPlannerGraph.ForDetails();
        var service = new TaskDetailsService(graph, fixture.Lifecycle);

        var load = service.GetAsync("task", default);
        await graph.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Lifecycle.PurgeAsync(default);
        graph.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => load);
        Assert.Equal("planner_data_changed", error.Code);
        Assert.False((await fixture.Lifecycle.TryGetAsync<TaskDetailsResponse>(
            "task-details", "task", default)).Found);
    }

    [Fact]
    public async Task Member_load_started_before_purge_cannot_return_after_purge()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        var graph = PausedPlannerGraph.ForMembers();
        var service = new BoardMemberService(graph, fixture.Settings, fixture.Lifecycle);

        var load = service.GetAsync(default);
        await graph.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Lifecycle.PurgeAsync(default);
        graph.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => load);
        Assert.Equal("planner_data_changed", error.Code);
    }

    [Fact]
    public async Task Chat_load_started_before_purge_cannot_return_after_purge()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        var graph = PausedPlannerGraph.ForChat();
        var details = new TaskDetailsService(graph, fixture.Lifecycle);
        var service = new TaskChatService(graph, fixture.Settings, details, fixture.Lifecycle);

        var load = service.GetAsync("task", null, default);
        await graph.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Lifecycle.PurgeAsync(default);
        graph.Release();

        var error = await Assert.ThrowsAsync<OutlookException>(() => load);
        Assert.Equal("planner_data_changed", error.Code);
    }

    [Fact]
    public async Task Shutdown_waits_for_required_cleanup_before_marking_runtime_clean()
    {
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(new MutableIdentity(), json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        await settings.ClearPurgeRequiredAsync(default);
        var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await lifecycle.StartAsync(default);
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        json.BlockDeletes = true;
        await account.InvalidateAsync(default);
        await json.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = lifecycle.StopAsync(default);
        Assert.False(stop.IsCompleted);
        Assert.True(await settings.IsPurgeRequiredAsync(default));

        json.ReleaseDeletes();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await settings.IsPurgeRequiredAsync(default));
        Assert.False(json.Contains("settings"));
    }

    [Fact]
    public async Task Shutdown_timeout_leaves_runtime_marked_unclean()
    {
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(new MutableIdentity(), json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        await settings.ClearPurgeRequiredAsync(default);
        var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
        await lifecycle.StartAsync(default);
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        json.BlockDeletes = true;
        await account.InvalidateAsync(default);
        await json.DeleteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.StopAsync(timeout.Token));

        Assert.True(await settings.IsPurgeRequiredAsync(default));
        Assert.True(json.Contains("settings"));
        json.ReleaseDeletes();
        await lifecycle.RetryPendingPurgeAsync(default);
    }

    [Fact]
    public async Task InvalidationContinuesPastFailedPlannerPurgeAndRetriesUntilFilesAreRemoved()
    {
        var identity = new MutableIdentity();
        var json = new FaultingPlannerJsonStore();
        var account = new MicrosoftAccountState(identity, json);
        await account.GetAsync(default);
        var settings = new PlannerSettingsStore(json, account,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
        var lifecycle = new PlannerDataLifecycle(account, settings,
            new MemoryCache(new MemoryCacheOptions()));
        var laterSubscriberRan = false;
        account.Invalidated += () => laterSubscriberRan = true;
        await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await settings.SaveCachedDisplayAsync(Display("plan"), default);
        json.FailDeletes = true;

        await account.InvalidateAsync(default);
        await json.FirstFailedDelete.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(laterSubscriberRan);
        Assert.True(lifecycle.PurgeRequired);
        json.FailDeletes = false;
        await lifecycle.RetryPendingPurgeAsync(default);
        Assert.False(lifecycle.PurgeRequired);
        Assert.False(json.Contains("settings"));
        Assert.False(json.Contains("cached-display"));
    }

    [Fact]
    public async Task StartupCompletesDurablyMarkedPurge()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlannerLifecycleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var json = new LocalJsonStore(root);
            var account = new MicrosoftAccountState(new MutableIdentity(), json);
            await account.GetAsync(default);
            var settings = new PlannerSettingsStore(json, account,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
            await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
            await settings.SaveCachedDisplayAsync(Display("plan"), default);
            await settings.MarkPurgeRequiredAsync(default);

            var lifecycle = new PlannerDataLifecycle(account, settings,
                new MemoryCache(new MemoryCacheOptions()));
            await lifecycle.StartAsync(default);
            await WaitUntilAsync(() => !lifecycle.PurgeRequired &&
                !File.Exists(Path.Combine(root, "settings.json")) &&
                !File.Exists(Path.Combine(root, "cached-display.json")));

            Assert.True(await settings.IsPurgeRequiredAsync(default));
            await lifecycle.StopAsync(default);
            Assert.False(await settings.IsPurgeRequiredAsync(default));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MalformedSafePreferencesDoNotPreventPlannerPurge()
    {
        var root = Path.Combine(Path.GetTempPath(), "PlannerLifecycleTests", Guid.NewGuid().ToString("N"));
        try
        {
            var json = new LocalJsonStore(root);
            var account = new MicrosoftAccountState(new MutableIdentity(), json);
            await account.GetAsync(default);
            var settings = new PlannerSettingsStore(json, account,
                new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z")));
            await settings.SaveSettingsAsync(new SettingsDto("plan", "Board", false), default);
            await settings.SaveCachedDisplayAsync(Display("plan"), default);
            await File.WriteAllTextAsync(Path.Combine(root, "planner-ui-preferences.json"), "{broken");
            var lifecycle = new PlannerDataLifecycle(account, settings,
                new MemoryCache(new MemoryCacheOptions()));

            await lifecycle.PurgeAsync(default);

            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
            Assert.False(File.Exists(Path.Combine(root, "cached-display.json")));
            Assert.True((await json.ReadAsync<PlannerSafePreferences>("planner-ui-preferences", default))!.HideCompletedTasks);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Legacy_unversioned_work_data_is_expired()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Json.WriteAsync("settings", new SettingsDto("legacy-plan", "Legacy", false), default);
        await fixture.Json.WriteAsync("cached-display", Display("legacy-plan"), default);

        var settings = await fixture.Settings.LoadSettingsAsync(default);
        var display = await fixture.Settings.LoadCachedDisplayAsync(default);

        Assert.Null(settings.SelectedPlanId);
        Assert.True(settings.HideCompletedTasks);
        Assert.Null(display);
    }

    [Fact]
    public async Task Snapshot_is_available_only_to_same_account_for_twenty_four_hours()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);

        Assert.NotNull(await fixture.Settings.LoadCachedDisplayAsync(default));
        fixture.Clock.Advance(TimeSpan.FromHours(24).Add(TimeSpan.FromSeconds(1)));
        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));

        fixture.Identity.Current = fixture.Identity.Current with { HomeAccountId = "other-home" };
        await fixture.Account.GetAsync(default);
        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
    }

    [Fact]
    public async Task Account_invalidation_purges_persisted_and_memory_work_data()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", false,
            new Dictionary<string, PlanViewPreferences> { ["plan"] = Preferences() }), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        await fixture.Lifecycle.SetAsync("plans", "all", new[] { new PlanSummary("plan", "Board", null, null) },
            TimeSpan.FromMinutes(2), default);

        await fixture.Account.InvalidateAsync(default);

        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        var settings = await fixture.Settings.LoadSettingsAsync(default);
        Assert.Null(settings.SelectedPlanId);
        Assert.Null(settings.PlanViews);
        Assert.False(settings.HideCompletedTasks);
        Assert.False((await fixture.Lifecycle.TryGetAsync<IReadOnlyList<PlanSummary>>("plans", "all", default)).Found);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authorization_graph_failures_purge_and_never_fall_back(HttpStatusCode status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        var coordinator = new PlannerCoordinator(fixture.Settings,
            new PlannerDisplayService(new FailingGraph(new GraphApiException(status, "denied"))), fixture.Lifecycle);

        await Assert.ThrowsAsync<GraphApiException>(() => coordinator.GetDisplayAsync(default));

        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task BucketFormatAuthorizationFailurePurgesEveryPlannerCache(HttpStatusCode status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        await fixture.Lifecycle.SetAsync("plans", "all", new[] { new PlanSummary("plan", "Board", null, null) },
            TimeSpan.FromMinutes(2), default);
        var graph = new PlannerGraphClient(new HttpClient(new FormatAuthorizationHandler(status))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        }, new StaticTokenProvider());
        var coordinator = new PlannerCoordinator(fixture.Settings, new PlannerDisplayService(graph), fixture.Lifecycle);

        await Assert.ThrowsAsync<GraphApiException>(() => coordinator.GetDisplayAsync(default));

        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
        Assert.False((await fixture.Lifecycle.TryGetAsync<IReadOnlyList<PlanSummary>>("plans", "all", default)).Found);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("throttle")]
    [InlineData("service")]
    public async Task Eligible_transient_failure_uses_current_fresh_snapshot(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        Exception error = failure switch
        {
            "network" => new HttpRequestException("offline"),
            "throttle" => new GraphApiException(HttpStatusCode.TooManyRequests, "slow"),
            _ => new GraphApiException(HttpStatusCode.ServiceUnavailable, "down")
        };
        var coordinator = new PlannerCoordinator(fixture.Settings,
            new PlannerDisplayService(new FailingGraph(error)), fixture.Lifecycle);

        var result = await coordinator.GetDisplayAsync(default);

        Assert.NotNull(result);
        Assert.True(result.IsStale);
        Assert.Equal("plan", result.PlanId);
    }

    [Fact]
    public async Task Forbidden_assignee_name_lookup_purges_instead_of_returning_cached_work()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        var service = new TaskDetailsService(new AssigneeForbiddenGraph(), fixture.Lifecycle);

        await Assert.ThrowsAsync<GraphApiException>(() => service.GetAsync("task", default));

        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
    }

    [Fact]
    public async Task Forbidden_board_member_lookup_purges_before_permission_error_is_returned()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), default);
        await fixture.Settings.SaveCachedDisplayAsync(Display("plan"), default);
        var service = new BoardMemberService(new MembersForbiddenGraph(), fixture.Settings, fixture.Lifecycle);

        await Assert.ThrowsAsync<BoardMembersUnavailableException>(() => service.GetAsync(default));

        Assert.Null(await fixture.Settings.LoadCachedDisplayAsync(default));
        Assert.Null((await fixture.Settings.LoadSettingsAsync(default)).SelectedPlanId);
    }

    private static BoardDisplay Display(string planId) =>
        new(planId, "Board", DateTimeOffset.Parse("2026-09-22T12:00:00Z"), false, []);

    private static PlanViewPreferences Preferences() =>
        new(true, new PlannerFilterSettings([], [], [], [], [], null));

    private static async Task WaitUntilAsync(Func<bool> condition, int seconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private Fixture(string root, LocalJsonStore json, MutableIdentity identity, MicrosoftAccountState account,
            FakeTimeProvider clock, PlannerSettingsStore settings, PlannerDataLifecycle lifecycle)
        {
            this.root = root; Json = json; Identity = identity; Account = account; Clock = clock;
            Settings = settings; Lifecycle = lifecycle;
        }

        public LocalJsonStore Json { get; }
        public MutableIdentity Identity { get; }
        public MicrosoftAccountState Account { get; }
        public FakeTimeProvider Clock { get; }
        public PlannerSettingsStore Settings { get; }
        public PlannerDataLifecycle Lifecycle { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "PlannerLifecycleTests", Guid.NewGuid().ToString("N"));
            var json = new LocalJsonStore(root);
            var identity = new MutableIdentity();
            var account = new MicrosoftAccountState(identity, json);
            await account.GetAsync(default);
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-22T12:00:00Z"));
            var access = new PlannerDataAccessGate();
            var settings = new PlannerSettingsStore(json, account, clock, access);
            var lifecycle = new PlannerDataLifecycle(account, settings,
                new MemoryCache(new MemoryCacheOptions()), access);
            return new Fixture(root, json, identity, account, clock, settings, lifecycle);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableIdentity : IMicrosoftAccountIdentityProvider
    {
        public MicrosoftAccountIdentity Current { get; set; } =
            new("home", "tenant", "client", "person@example.test");
        public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class StaticTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");
    }

    private sealed class FormatAuthorizationHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response = path.EndsWith("/buckets")
                ? Json("""{"value":[{"id":"bucket","name":"Doing","planId":"plan","orderHint":"a"}]}""")
                : path.EndsWith("/tasks")
                    ? Json("""{"value":[{"id":"task","title":"Work","planId":"plan","bucketId":"bucket","percentComplete":0}]}""")
                    : path.EndsWith("/bucketTaskBoardFormat")
                        ? new HttpResponseMessage(status) { Content = new StringContent("denied") }
                        : Json("""{"categoryDescriptions":{}}""");
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class FaultingPlannerJsonStore : ILocalJsonStore
    {
        private readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource firstFailedDelete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailDeletes { get; set; }
        public int DeleteFailuresRemaining { get; set; }
        public int DeleteAttempts { get; private set; }
        public bool BlockDeletes { get; set; }
        private readonly TaskCompletionSource deleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseDeletes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FirstFailedDelete => firstFailedDelete.Task;
        public Task DeleteStarted => deleteStarted.Task;
        public bool Contains(string name) => values.ContainsKey(name);
        public void Set(string name, object? value) => values[name] = value;
        public void ReleaseDeletes() => releaseDeletes.TrySetResult();

        public Task<T?> ReadAsync<T>(string name, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(name, out var value) ? (T?)value : default);

        public Task WriteAsync<T>(string name, T value, CancellationToken cancellationToken)
        {
            values[name] = value;
            return Task.CompletedTask;
        }

        public async Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            if (BlockDeletes)
            {
                deleteStarted.TrySetResult();
                await releaseDeletes.Task.WaitAsync(cancellationToken);
            }
            if (FailDeletes || DeleteFailuresRemaining > 0)
            {
                if (DeleteFailuresRemaining > 0) DeleteFailuresRemaining--;
                firstFailedDelete.TrySetResult();
                throw new IOException("disk unavailable");
            }
            values.Remove(name);
        }
    }

    private sealed class PausedPlannerGraph(string pausePoint) : IPlannerGraphClient
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;
        public void Release() => release.TrySetResult();
        public static PausedPlannerGraph ForPlans() => new("plans");
        public static PausedPlannerGraph ForDetails() => new("details");
        public static PausedPlannerGraph ForMembers() => new("members");
        public static PausedPlannerGraph ForChat() => new("chat");

        private async Task PauseAsync(string point, CancellationToken cancellationToken)
        {
            if (pausePoint != point) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken cancellationToken)
        {
            await PauseAsync("plans", cancellationToken);
            return [new GraphPlan("plan", "Board", "11111111-1111-1111-1111-111111111111", "Group")];
        }

        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GraphGroup>>([]);

        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GraphPlan>>([]);

        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GraphBucket>>([]);

        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GraphTask>>([]);

        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("Person");

        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken) =>
            Task.FromResult<GraphTask?>(new GraphTask("task", "Task", "plan", "bucket", null, null, 0,
                "etag", [], null, null, "thread"));

        public async Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId,
            CancellationToken cancellationToken)
        {
            await PauseAsync("details", cancellationToken);
            return new GraphTaskDetails("etag", [], "Notes");
        }

        public async Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId,
            CancellationToken cancellationToken)
        {
            await PauseAsync("members", cancellationToken);
            return [new GraphMember("person", "Person")];
        }

        public async Task<GraphConversationPage> GetConversationPostsAsync(string groupId, string threadId,
            Uri? continuationUri, CancellationToken cancellationToken)
        {
            await PauseAsync("chat", cancellationToken);
            return new GraphConversationPage([], null);
        }
    }

    private sealed class FailingGraph(Exception failure) : IPlannerGraphClient
    {
        private Task<T> Fail<T>() => Task.FromException<T>(failure);
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => Fail<IReadOnlyList<GraphGroup>>();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => Fail<IReadOnlyList<GraphPlan>>();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => Fail<IReadOnlyList<GraphBucket>>();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => Fail<IReadOnlyList<GraphTask>>();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Fail<GraphTask?>();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => Fail<string?>();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => Fail<GraphTaskDetails>();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => Fail<object>();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => Fail<object>();
    }

    private sealed class AssigneeForbiddenGraph : IPlannerGraphClient
    {
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => Task.FromResult<GraphTask?>(
            new GraphTask(taskId, "Task", "plan", "bucket", null, null, 0, "etag", ["person"]));
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) =>
            Task.FromResult(new GraphTaskDetails("etag", [], "Notes"));
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) =>
            Task.FromException<string?>(new GraphApiException(HttpStatusCode.Forbidden, "denied"));
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MembersForbiddenGraph : IPlannerGraphClient
    {
        public Task<IReadOnlyList<GraphPlan>> GetMyPlansAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GraphPlan>>(
            [new GraphPlan("plan", "Board", Guid.NewGuid().ToString(), null)]);
        public Task<IReadOnlyList<GraphMember>> GetGroupMembersAsync(string groupId, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<GraphMember>>(new GraphApiException(HttpStatusCode.Forbidden, "denied"));
        public Task<IReadOnlyList<GraphGroup>> GetMemberGroupsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphPlan>> GetPlansForGroupAsync(string groupId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphBucket>> GetBucketsAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<GraphTask>> GetTasksAsync(string planId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTask?> GetTaskAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserDisplayNameAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<GraphTaskDetails> GetTaskDetailsAsync(string taskId, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteChecklistItemAsync(string taskId, string itemId, string etag, CancellationToken ct) => throw new NotSupportedException();
        public Task CompleteTaskAsync(string taskId, string etag, CancellationToken ct) => throw new NotSupportedException();
    }
}
