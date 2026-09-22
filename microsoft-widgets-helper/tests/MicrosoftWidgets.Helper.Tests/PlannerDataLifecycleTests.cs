using System.Net;
using Microsoft.Extensions.Caching.Memory;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerDataLifecycleTests
{
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
            var settings = new PlannerSettingsStore(json, account, clock);
            var lifecycle = new PlannerDataLifecycle(account, settings, new MemoryCache(new MemoryCacheOptions()));
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
