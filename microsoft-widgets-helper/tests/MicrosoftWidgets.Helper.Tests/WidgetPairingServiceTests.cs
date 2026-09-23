using PlannerEdge.Helper.Auth;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;
using PlannerEdge.Helper.Storage;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class WidgetPairingServiceTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CredentialsAreScopedAndReplacementAndRevocationStayWithinScope()
    {
        var f = new Fixture();
        var planner = await f.Pair(WidgetScope.Planner, "same");
        var outlook = await f.Pair(WidgetScope.Outlook, "same");
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Planner, planner, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, planner, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Planner, outlook, default));
        var replacement = await f.Pair(WidgetScope.Planner, "same");
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Planner, planner, default));
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Outlook, outlook, default));
        var paired = await f.Service.GetPairedAsync(default);
        Assert.Equal(2, paired.Count);
        await f.Service.RevokeAsync(paired.Single(p => p.Scope == WidgetScope.Planner).CredentialId, default);
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Planner, replacement, default));
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Outlook, outlook, default));
    }

    [Fact]
    public async Task ApprovalRequiresSecretAndRevocationCannotBeUndoneByPolling()
    {
        var f = new Fixture();
        var request = await f.Create();
        Assert.Equal("pending", (await f.Service.PollAsync(request.Id, Secret, default)).Status);
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(request.Id, new string('z', 64), default));
        await f.Service.ApproveAsync(request.Id, default);
        var credential = (await f.Service.PollAsync(request.Id, Secret, default)).Credential!;
        Assert.Equal(credential, (await f.Service.PollAsync(request.Id, Secret, default)).Credential);
        await f.Service.RevokeAsync(Assert.Single(await f.Service.GetPairedAsync(default)).CredentialId, default);
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(request.Id, Secret, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, credential, default));
    }

    [Fact]
    public async Task ReplacedRequestCannotResurrectItsCredential()
    {
        var f = new Fixture();
        var old = await f.Create();
        await f.Service.ApproveAsync(old.Id, default);
        var credential = (await f.Service.PollAsync(old.Id, Secret, default)).Credential!;
        var current = await f.Pair(WidgetScope.Outlook, "instance");
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(old.Id, Secret, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, credential, default));
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Outlook, current, default));
    }

    [Fact]
    public async Task PendingPollsThrottleForOneSecondAndExpireAtFiveMinutes()
    {
        var f = new Fixture();
        var request = await f.Create();
        await f.Service.PollAsync(request.Id, Secret, default);
        Assert.Equal(429, (await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(request.Id, Secret, default))).StatusCode);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("pending", (await f.Service.PollAsync(request.Id, Secret, default)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.Empty(await f.Service.GetPendingAsync(default));
        Assert.Equal(404, (await Assert.ThrowsAsync<OutlookException>(() => f.Service.ApproveAsync(request.Id, default))).StatusCode);
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(request.Id, Secret, default));
    }

    [Fact]
    public async Task PendingAndBootstrapLimitsAreGlobalAcrossScopes()
    {
        var f = new Fixture();
        for (var i = 0; i < 32; i++)
        {
            if (i > 0 && i % 10 == 0) f.Clock.Advance(TimeSpan.FromMinutes(1));
            await f.Create(i % 2 == 0 ? WidgetScope.Planner : WidgetScope.Outlook, i.ToString());
            if (i == 9) Assert.Equal(429, (await Assert.ThrowsAsync<OutlookException>(() => f.Create())).StatusCode);
        }
        Assert.Equal(32, (await f.Service.GetPendingAsync(default)).Count);
        Assert.Equal(429, (await Assert.ThrowsAsync<OutlookException>(() => f.Create())).StatusCode);
    }

    [Fact]
    public async Task PairedLimitAllowsRetryAndReplacementButRejectsNewInstance()
    {
        var f = new Fixture();
        PairingCreated? last = null;
        for (var i = 0; i < 100; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            last = await f.Create(WidgetScope.Outlook, i.ToString());
            await f.Service.ApproveAsync(last.Id, default);
            await f.Service.PollAsync(last.Id, Secret, default);
        }
        Assert.Equal("approved", (await f.Service.PollAsync(last!.Id, Secret, default)).Status);
        Assert.NotNull(await f.Pair(WidgetScope.Outlook, "99"));
        var extra = await f.Create(WidgetScope.Planner, "99");
        await f.Service.ApproveAsync(extra.Id, default);
        Assert.Equal("pairing_limit", (await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(extra.Id, Secret, default))).Code);
        Assert.Equal(100, (await f.Service.GetPairedAsync(default)).Count);
    }

    [Fact]
    public async Task VersionedStoreContainsOnlyHashesAndLegacyCredentialIsNeverPromoted()
    {
        var f = new Fixture(new JsonRoundTripStore());
        await f.Store.WriteAsync("outlook-account", "account-a", default);
        await f.Store.WriteAsync("outlook-credentials", new[] { new StoredOutlookCredential("old", "instance", "account-a", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("legacy")))) }, default);
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, "legacy", default));
        Assert.Empty((await f.Store.ReadAsync<StoredOutlookCredential[]>("outlook-credentials", default))!);
        var credential = await f.Pair(WidgetScope.Outlook, "instance");
        var stored = await f.Store.ReadAsync<WidgetCredentialStore>("widget-credentials", default);
        Assert.Equal(1, stored!.Version);
        var entry = Assert.Single(stored.Credentials);
        Assert.Equal(WidgetScope.Outlook, entry.Scope);
        Assert.Equal(MicrosoftAccountState.Key(f.Tokens.Identity), entry.AccountKey);
        Assert.Equal(f.Clock.GetUtcNow(), entry.CreatedAt);
        Assert.DoesNotContain(credential, JsonSerializer.Serialize(stored));
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(stored));
        var restarted = new WidgetPairingService(new MicrosoftAccountState(f.Tokens, f.Store), f.Clock);
        Assert.NotNull(await restarted.AuthenticateAsync(WidgetScope.Outlook, credential, default));
    }

    [Fact]
    public async Task ApprovedPollThatExpiresDuringCredentialReadDoesNotMintOrPersistCredential()
    {
        var store = new BlockingCredentialStore();
        var f = new Fixture(store);
        var pending = await f.Create();
        await f.Service.ApproveAsync(pending.Id, default);
        store.BlockRead = true;

        var poll = f.Service.PollAsync(pending.Id, Secret, default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        store.Release.SetResult();

        var error = await Assert.ThrowsAsync<OutlookException>(() => poll);
        Assert.Equal("pairing_expired", error.Code);
        var stored = await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default);
        Assert.True(stored is null || stored.Credentials.Length == 0);
    }

    [Fact]
    public async Task ReplacementThatExpiresDuringCredentialWriteRestoresPreviousCredential()
    {
        var store = new BlockingCredentialStore();
        var f = new Fixture(store);
        var previous = await f.Pair(WidgetScope.Outlook, "instance");
        var pending = await f.Create(WidgetScope.Outlook, "instance");
        await f.Service.ApproveAsync(pending.Id, default);
        store.Block = true;

        var poll = f.Service.PollAsync(pending.Id, Secret, default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        store.Release.SetResult();

        var error = await Assert.ThrowsAsync<OutlookException>(() => poll);
        Assert.Equal("pairing_expired", error.Code);
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Outlook, previous, default));
        Assert.Single(await f.Service.GetPairedAsync(default));
    }

    [Fact]
    public async Task AuthenticationRejectsAccountChangeAfterLeaseCaptureDuringCredentialRead()
    {
        var store = new BlockingCredentialStore();
        var f = new Fixture(store);
        var credential = await f.Pair(WidgetScope.Outlook, "instance");
        store.BlockRead = true;

        var authentication = f.Service.AuthenticateAsync(WidgetScope.Outlook, credential, default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        f.Tokens.Account = "account-b";
        store.Release.SetResult();

        Assert.Null(await authentication);
    }

    [Fact]
    public async Task UnknownStoreVersionFailsClosed()
    {
        var f = new Fixture();
        var credential = await f.Pair(WidgetScope.Outlook, "instance");
        var stored = (await f.Store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!;
        await f.Store.WriteAsync("widget-credentials", stored with { Version = 99 }, default);
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, credential, default));
    }

    [Fact]
    public async Task AccountTransitionsInvalidateBothScopesAndPendingRequests()
    {
        var f = new Fixture();
        var planner = await f.Pair(WidgetScope.Planner, "instance");
        var outlook = await f.Pair(WidgetScope.Outlook, "instance");
        var pending = await f.Create();
        var oldLease = await f.State.GetAsync(default);
        await f.State.TransitionAsync(() => Task.FromResult(true), default);
        Assert.NotNull(await f.Service.AuthenticateAsync(WidgetScope.Planner, planner, default));
        await f.State.TransitionAsync(() => { f.Tokens.Account = "account-b"; return Task.FromResult(true); }, default);
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.CreateAsync(WidgetScope.Outlook, new("stale", Secret), oldLease, default));
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.ApproveAsync(pending.Id, default));
        await Assert.ThrowsAsync<OutlookException>(() => f.Service.PollAsync(pending.Id, Secret, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Planner, planner, default));
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, outlook, default));
        f.Tokens.Account = "account-a";
        Assert.Null(await f.Service.AuthenticateAsync(WidgetScope.Outlook, outlook, default));
        Assert.Empty(await f.Service.GetPairedAsync(default));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("poll")]
    [InlineData("authenticate")]
    public async Task GenerationChangeWhileOperationWaitsCannotPublishOldAuthorization(string operation)
    {
        var f = new Fixture();
        var credential = await f.Pair(WidgetScope.Outlook, "paired");
        var pending = await f.Create();
        await f.Service.ApproveAsync(pending.Id, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transition = f.State.TransitionAsync(async () => { entered.SetResult(); await release.Task; return true; }, default, forceInvalidate: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task action = operation switch
        {
            "approve" => f.Service.ApproveAsync(pending.Id, default),
            "poll" => f.Service.PollAsync(pending.Id, Secret, default),
            _ => f.Service.AuthenticateAsync(WidgetScope.Outlook, credential, default)
        };
        try { Assert.False(action.IsCompleted); }
        finally { release.SetResult(); }
        await transition;
        if (operation == "authenticate") Assert.Null(await (Task<AccountLease?>)action);
        else await Assert.ThrowsAsync<OutlookException>(() => action);
    }

    [Fact]
    public async Task TransitionWaitsForCredentialWriteAndThenClearsIt()
    {
        var store = new BlockingCredentialStore();
        var f = new Fixture(store);
        var pending = await f.Create();
        await f.Service.ApproveAsync(pending.Id, default);
        store.Block = true;
        var poll = f.Service.PollAsync(pending.Id, Secret, default);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var transition = f.State.InvalidateAsync(default);
        try { Assert.False(transition.IsCompleted); }
        finally { store.Release.SetResult(); }
        try { await poll; } catch (OutlookException ex) { Assert.Equal("account_changed", ex.Code); }
        await transition;
        Assert.Empty(await f.Service.GetPairedAsync(default));
    }

    private sealed class Fixture
    {
        public OutlookTokens Tokens { get; } = new();
        public OutlookClock Clock { get; } = new();
        public ILocalJsonStore Store { get; }
        public MicrosoftAccountState State { get; }
        public WidgetPairingService Service { get; }
        public Fixture(ILocalJsonStore? store = null)
        {
            Store = store ?? new OutlookMemoryStore();
            State = new(Tokens, Store);
            Service = new(State, Clock);
        }
        public async Task<PairingCreated> Create(WidgetScope scope = WidgetScope.Outlook, string instance = "instance") =>
            await Service.CreateAsync(scope, new(instance, Secret), await State.GetAsync(default), default);
        public async Task<string> Pair(WidgetScope scope, string instance)
        {
            var pending = await Create(scope, instance);
            await Service.ApproveAsync(pending.Id, default);
            return (await Service.PollAsync(pending.Id, Secret, default)).Credential!;
        }
    }

    private sealed class BlockingCredentialStore : ILocalJsonStore
    {
        private readonly OutlookMemoryStore inner = new();
        public bool Block { get; set; }
        public bool BlockRead { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T?> ReadAsync<T>(string name, CancellationToken ct)
        {
            if (BlockRead && name == "widget-credentials")
            {
                BlockRead = false;
                Entered.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.ReadAsync<T>(name, ct);
        }
        public async Task WriteAsync<T>(string name, T value, CancellationToken ct)
        {
            if (Block && name == "widget-credentials") { Block = false; Entered.SetResult(); await Release.Task.WaitAsync(ct); }
            await inner.WriteAsync(name, value, ct);
        }
    }

    private sealed class JsonRoundTripStore : ILocalJsonStore
    {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, string> values = [];
        public Task<T?> ReadAsync<T>(string name, CancellationToken ct) => Task.FromResult(
            values.TryGetValue(name, out var json) ? JsonSerializer.Deserialize<T>(json, Options) : default);
        public Task WriteAsync<T>(string name, T value, CancellationToken ct)
        {
            values[name] = JsonSerializer.Serialize(value, Options);
            return Task.CompletedTask;
        }
    }
}
