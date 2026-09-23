using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MicrosoftWidgets.Helper.Tests;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Graph;
using PlannerEdge.Helper.Planner;
using PlannerEdge.Helper.Storage;

namespace PlannerEdge.Helper.Tests;

public sealed class PlannerOperationLockTests
{
    [Theory]
    [InlineData("capture")]
    [InlineData("change")]
    public async Task Production_settings_store_and_member_publication_use_account_lifecycle_selection_order(
        string operation)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var identity = new MutableIdentity();
        var store = new OutlookMemoryStore();
        var account = new MicrosoftAccountState(identity, store);
        var accessGate = new PlannerDataAccessGate();
        var productionSettings = new PlannerSettingsStore(store, account, TimeProvider.System, accessGate);
        var settingsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new SignalingSettingsStore(productionSettings, settingsEntered);
        var selection = new BoardSelectionCoordinator(settings, account);
        var lifecycle = new PlannerDataLifecycle(new MemoryCache(new MemoryCacheOptions()), accessGate);
        var lease = await account.GetAsync(cancellation.Token);
        using var request = account.BindRequest(lease);
        await productionSettings.SaveSettingsAsync(new SettingsDto("plan", "Board", true), cancellation.Token);
        var selectionTicket = await selection.CaptureAsync(cancellation.Token);
        var lifecycleTicket = lifecycle.CaptureTicket();
        var publicationHasOuterGates = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var publication = account.ExecuteAuthorizedAsync(lease,
            () => lifecycle.ExecutePublicationAsync(lifecycleTicket, async () =>
            {
                publicationHasOuterGates.TrySetResult();
                await releasePublication.Task.WaitAsync(cancellation.Token);
                await selection.RunAsync(selectionTicket, _ => Task.CompletedTask, cancellation.Token);
            }, cancellation.Token), cancellation.Token);
        await publicationHasOuterGates.Task.WaitAsync(TimeSpan.FromSeconds(2));

        settings.Arm();
        Task boardWork = operation == "capture"
            ? selection.CaptureAsync(cancellation.Token)
            : selection.ChangeAsync(_ =>
            {
                settingsEntered.TrySetResult();
                return productionSettings.UpdateSettingsAsync(
                    value => value with { SelectedPlanTitle = "Renamed" }, cancellation.Token);
            }, cancellation.Token);
        var selectionWasAvailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = selection.RunAsync(selectionTicket, async _ =>
        {
            selectionWasAvailable.TrySetResult();
            await releaseProbe.Task.WaitAsync(cancellation.Token);
        }, cancellation.Token);

        await Task.WhenAny(settingsEntered.Task, selectionWasAvailable.Task).WaitAsync(TimeSpan.FromSeconds(2));
        releaseProbe.TrySetResult();
        releasePublication.TrySetResult();

        await Task.WhenAll(boardWork, probe, publication).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Real_graph_mutation_and_member_publication_use_one_lock_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Account.BindRequest(fixture.Lease);
        var selectionTicket = await fixture.Selection.CaptureAsync(cancellation.Token);
        var lifecycleTicket = fixture.Lifecycle.CaptureTicket();
        var publicationHasOuterGates = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var publication = fixture.Account.ExecuteAuthorizedAsync(fixture.Lease,
            () => fixture.Lifecycle.ExecutePublicationAsync(lifecycleTicket, async () =>
            {
                publicationHasOuterGates.TrySetResult();
                await fixture.ReleasePublication.Task.WaitAsync(cancellation.Token);
                await fixture.Selection.RunAsync(selectionTicket, _ => Task.CompletedTask, cancellation.Token);
            }, cancellation.Token), cancellation.Token);
        await publicationHasOuterGates.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var completion = fixture.Completion.CompleteAsync("task", cancellation.Token);
        await Task.WhenAny(fixture.Tokens.Started, Task.Delay(200, cancellation.Token));
        fixture.ReleasePublication.TrySetResult();

        fixture.Tokens.Release();

        await Task.WhenAll(completion, publication).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Handler.Mutations);
    }

    [Fact]
    public async Task Account_invalidation_waits_for_real_graph_mutation_send_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Account.BindRequest(fixture.Lease);

        var completion = fixture.Completion.CompleteAsync("task", cancellation.Token);
        await fixture.Tokens.Started.WaitAsync(TimeSpan.FromSeconds(2));

        var accountChange = fixture.Account.PurgeDataAsync(cancellation.Token);
        Assert.False(accountChange.IsCompleted);

        fixture.Tokens.Release();

        await completion.WaitAsync(TimeSpan.FromSeconds(2));
        await accountChange.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(fixture.Account.IsCurrent(fixture.Lease));
        Assert.Equal(1, fixture.Handler.Mutations);
    }

    [Fact]
    public async Task Board_change_waits_for_real_graph_mutation_send_boundary()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Account.BindRequest(fixture.Lease);

        var completion = fixture.Completion.CompleteAsync("task", cancellation.Token);
        await fixture.Tokens.Started.WaitAsync(TimeSpan.FromSeconds(2));

        var boardChange = fixture.Selection.ChangeAsync(
            _ => fixture.Settings.SelectAsync("other-plan", "Other board"), cancellation.Token);
        Assert.False(boardChange.IsCompleted);

        fixture.Tokens.Release();

        await completion.WaitAsync(TimeSpan.FromSeconds(2));
        await boardChange.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, fixture.Handler.Mutations);
    }

    [Fact]
    public async Task Escaped_child_cannot_reuse_an_account_execution_after_its_gate_is_released()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Account.BindRequest(fixture.Lease);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;

        await fixture.Account.ExecuteBoundAsync(() =>
        {
            child = Task.Run(async () =>
            {
                await releaseChild.Task;
                childAttempted.TrySetResult();
                await fixture.Account.ExecuteBoundAsync(() => Task.FromResult(true), cancellation.Token);
            }, cancellation.Token);
            return Task.FromResult(true);
        }, cancellation.Token);

        var transitionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = fixture.Account.TransitionAsync(async () =>
        {
            transitionEntered.TrySetResult();
            await releaseTransition.Task.WaitAsync(cancellation.Token);
            return true;
        }, cancellation.Token);
        await transitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        releaseChild.TrySetResult();
        await childAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100, cancellation.Token);
        Assert.False(child!.IsCompleted);

        releaseTransition.TrySetResult();
        await Task.WhenAll(child, transition).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Child_that_enters_before_parent_completion_cannot_outlive_the_account_gate()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = fixture.Account.BindRequest(fixture.Lease);
        var childEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;

        var parent = fixture.Account.ExecuteBoundAsync(async () =>
        {
            child = Task.Run(() => fixture.Account.ExecuteBoundAsync(async () =>
            {
                childEntered.TrySetResult();
                await releaseChild.Task.WaitAsync(cancellation.Token);
                return true;
            }, cancellation.Token), cancellation.Token);
            await childEntered.Task.WaitAsync(cancellation.Token);
            return true;
        }, cancellation.Token);
        await childEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var transitionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = fixture.Account.TransitionAsync(() =>
        {
            transitionEntered.TrySetResult();
            return Task.FromResult(true);
        }, cancellation.Token);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            transitionEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(parent.IsCompleted);

        releaseChild.TrySetResult();
        await Task.WhenAll(parent, child!, transition).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private Fixture(ServiceProvider services, MutableIdentity identity, AccountLease lease,
            BlockingTokenProvider tokens, MutationHandler handler, MutableSettings settings)
        {
            this.services = services;
            Identity = identity;
            Lease = lease;
            Tokens = tokens;
            Handler = handler;
            Settings = settings;
        }

        public MutableIdentity Identity { get; }
        public AccountLease Lease { get; }
        public BlockingTokenProvider Tokens { get; }
        public MutationHandler Handler { get; }
        public MutableSettings Settings { get; }
        public TaskCompletionSource ReleasePublication { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MicrosoftAccountState Account => services.GetRequiredService<MicrosoftAccountState>();
        public PlannerDataLifecycle Lifecycle => services.GetRequiredService<PlannerDataLifecycle>();
        public IBoardSelectionCoordinator Selection => services.GetRequiredService<IBoardSelectionCoordinator>();
        public TaskCompletionService Completion => services.GetRequiredService<TaskCompletionService>();

        public static async Task<Fixture> CreateAsync()
        {
            var identity = new MutableIdentity();
            var settings = new MutableSettings();
            var tokens = new BlockingTokenProvider();
            var handler = new MutationHandler();
            var services = new ServiceCollection();
            services.AddMemoryCache();
            services.AddSingleton<IMicrosoftAccountIdentityProvider>(identity);
            services.AddSingleton<ILocalJsonStore, OutlookMemoryStore>();
            services.AddSingleton<MicrosoftAccountState>();
            services.AddSingleton<PlannerDataAccessGate>();
            services.AddSingleton<IPlannerSettingsStore>(settings);
            services.AddSingleton<PlannerDataLifecycle>();
            services.AddSingleton<IBoardSelectionCoordinator, BoardSelectionCoordinator>();
            services.AddSingleton<IGraphTokenProvider>(tokens);
            services.AddSingleton<IPlannerGraphClient>(provider => new PlannerGraphClient(
                new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
                provider.GetRequiredService<IGraphTokenProvider>(),
                provider.GetRequiredService<MicrosoftAccountState>(),
                provider.GetRequiredService<PlannerDataAccessGate>()));
            services.AddSingleton<SelectedPlanTaskService>();
            services.AddSingleton<TaskCompletionService>();
            var provider = services.BuildServiceProvider();
            var lease = await provider.GetRequiredService<MicrosoftAccountState>().GetAsync(default);
            return new Fixture(provider, identity, lease, tokens, handler, settings);
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }

    private sealed class MutableIdentity : IMicrosoftAccountIdentityProvider
    {
        public MicrosoftAccountIdentity Current { get; set; } =
            new("home", "tenant", "client", "person@example.test");

        public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class MutableSettings : IPlannerSettingsStore
    {
        private SettingsDto current = new("plan", "Board", true);

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(current);
        public Task<SettingsDto> SelectAsync(string planId, string title)
        {
            current = current with { SelectedPlanId = planId, SelectedPlanTitle = title };
            return Task.FromResult(current);
        }
        public Task SaveSettingsAsync(SettingsDto value, CancellationToken cancellationToken)
        {
            current = value;
            return Task.CompletedTask;
        }
        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BoardDisplay?>(null);
        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SignalingSettingsStore(IPlannerSettingsStore inner, TaskCompletionSource entered)
        : IPlannerSettingsStore
    {
        private int armed;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public Task<SettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref armed) != 0) entered.TrySetResult();
            return inner.LoadSettingsAsync(cancellationToken);
        }

        public Task SaveSettingsAsync(SettingsDto settings, CancellationToken cancellationToken) =>
            inner.SaveSettingsAsync(settings, cancellationToken);

        public Task<SettingsDto> UpdateSettingsAsync(Func<SettingsDto, SettingsDto> update,
            CancellationToken cancellationToken) => inner.UpdateSettingsAsync(update, cancellationToken);

        public Task<BoardDisplay?> LoadCachedDisplayAsync(CancellationToken cancellationToken) =>
            inner.LoadCachedDisplayAsync(cancellationToken);

        public Task SaveCachedDisplayAsync(BoardDisplay display, CancellationToken cancellationToken) =>
            inner.SaveCachedDisplayAsync(display, cancellationToken);
    }

    private sealed class BlockingTokenProvider : IGraphTokenProvider
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int calls;
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) != 2) return "token";
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return "token";
        }
    }

    private sealed class MutationHandler : HttpMessageHandler
    {
        private int mutations;
        public int Mutations => Volatile.Read(ref mutations);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(Json("""
                    {"id":"task","title":"Task","planId":"plan","bucketId":"bucket","percentComplete":0,"@odata.etag":"W/\"etag\""}
                    """));
            }

            Interlocked.Increment(ref mutations);
            return Task.FromResult(Json("{}"));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
