using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class MicrosoftAccountStateTests
{
    [Fact]
    public void ImmutableKeyIgnoresUsernameButSeparatesPrincipalsTenantsAndClients()
    {
        var first = Identity("home-a", "tenant-a", "client-a", "same@example.com");

        Assert.Equal(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(first with { Username = "renamed@example.com" }));
        Assert.NotEqual(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(Identity("home-b", "tenant-a", "client-a", "same@example.com")));
        Assert.NotEqual(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(Identity("home-a", "tenant-b", "client-a", "same@example.com")));
        Assert.NotEqual(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(Identity("home-a", "tenant-a", "client-b", "same@example.com")));
    }

    [Fact]
    public async Task RefreshPreservesLeaseWhilePrincipalReplacementAdvancesGenerationAndClearsCredentials()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "same@example.com"));
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(identities, store);
        var first = await state.GetAsync(default);
        await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1,
            [new("credential", WidgetScope.Planner, "instance", first.Key, "hash", DateTimeOffset.UtcNow)]), default);

        identities.Identity = identities.Identity with { Username = "renamed@example.com" };
        Assert.Equal(first, await state.GetAsync(default));

        identities.Identity = Identity("home-b", "tenant-a", "client-a", "same@example.com");
        var second = await state.GetAsync(default);

        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Empty((await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!.Credentials);
    }

    [Fact]
    public async Task ForcedInvalidationAdvancesGenerationEvenWhenAuthenticationMutationFails()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var first = await state.GetAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => state.TransitionAsync<bool>(
            () => throw new InvalidOperationException("failed sign-out"), default, forceInvalidate: true));

        var second = await state.GetAsync(default);
        Assert.True(second.Generation > first.Generation);
    }

    [Fact]
    public async Task ConfigurationTransitionToUnavailableAccountImmediatelyClearsCredentials()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(identities, store);
        var lease = await state.GetAsync(default);
        await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1,
            [new("credential", WidgetScope.Outlook, "instance", lease.Key, "hash", DateTimeOffset.UtcNow)]), default);

        await state.TransitionAsync(() =>
        {
            identities.Failure = new OutlookException("not_configured", "Not configured", 401);
            return Task.FromResult(true);
        }, default);

        Assert.Empty((await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!.Credentials);
        Assert.False(state.IsCurrent(lease));
    }

    [Fact]
    public async Task Forced_configuration_transition_invalidates_same_principal_and_tenant_alias()
    {
        var identity = Identity("home-a", "tenant-a", "client-a", "user@example.com");
        var identities = new IdentityProvider(identity);
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(identities, store);
        var first = await state.GetAsync(default);
        await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1,
            [new("credential", WidgetScope.Planner, "instance", first.Key, "hash", DateTimeOffset.UtcNow)]), default);

        await state.TransitionAsync(() => Task.FromResult(true), default, forceInvalidate: true);

        var second = await state.GetAsync(default);
        Assert.True(second.Generation > first.Generation);
        Assert.False(state.IsCurrent(first));
        Assert.Empty((await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!.Credentials);
    }

    [Fact]
    public async Task LegacyUsernameIdentityAndCredentialsAreDeletedDuringMigration()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var store = new OutlookMemoryStore();
        await store.WriteAsync("outlook-account", "legacy-username-key", default);
        await store.WriteAsync("outlook-credentials", new[]
        {
            new StoredOutlookCredential("legacy", "instance", "legacy-username-key", "hash")
        }, default);
        await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1,
            [new("legacy", WidgetScope.Outlook, "instance", "legacy-username-key", "hash", DateTimeOffset.UtcNow)]), default);

        var state = new MicrosoftAccountState(identities, store);
        var lease = await state.GetAsync(default);

        Assert.Equal(MicrosoftAccountState.Key(identities.Identity), lease.Key);
        Assert.Empty((await store.ReadAsync<StoredOutlookCredential[]>("outlook-credentials", default))!);
        Assert.Empty((await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!.Credentials);
        Assert.Null(await store.ReadAsync<string>("outlook-account", default));
    }

    [Fact]
    public async Task RestartPurgesCredentialsWhoseAccountDoesNotMatchPersistedImmutableIdentity()
    {
        var identity = Identity("home-b", "tenant-a", "client-a", "user@example.com");
        var store = new OutlookMemoryStore();
        await store.WriteAsync("microsoft-account-identity",
            new StoredMicrosoftAccountIdentity(identity.HomeAccountId, identity.TenantId, identity.ClientId), default);
        await store.WriteAsync("widget-credentials", new WidgetCredentialStore(1,
            [new("old", WidgetScope.Planner, "instance", MicrosoftAccountState.Key(Identity("home-a", "tenant-a", "client-a", "user@example.com")), "hash", DateTimeOffset.UtcNow)]), default);

        var state = new MicrosoftAccountState(new IdentityProvider(identity), store);
        await state.GetAsync(default);

        Assert.Empty((await store.ReadAsync<WidgetCredentialStore>("widget-credentials", default))!.Credentials);
    }

    [Fact]
    public async Task AccountChangeAfterAuthorizationPreventsResponsePublication()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var lease = await state.GetAsync(default);

        var error = await Assert.ThrowsAsync<OutlookException>(() => state.ExecuteAuthorizedAsync(lease, () =>
        {
            identities.Identity = Identity("home-b", "tenant-a", "client-a", "user@example.com");
            return Task.CompletedTask;
        }, default));

        Assert.Equal("account_changed", error.Code);
    }

    [Fact]
    public async Task Owner_session_rotation_is_atomic_with_the_exact_account_transition()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var access = new LocalAccessService(TimeProvider.System, state);
        var enteredCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var rotation = state.TransitionAsync(
            () =>
            {
                identities.Identity = Identity("home-b", "tenant-a", "client-a", "user@example.com");
                return Task.FromResult(true);
            },
            async (_, previous, current) =>
            {
                Assert.NotEqual(previous, current);
                enteredCompletion.TrySetResult();
                await releaseCompletion.Task;
                return access.IssueReplacementOwnerSession(current);
            },
            default);

        await enteredCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var invalidation = state.InvalidateAsync(default);
        Assert.False(invalidation.IsCompleted);

        releaseCompletion.TrySetResult();
        var replacement = await rotation;
        await invalidation;

        Assert.False(await access.ValidateOwnerSessionAsync(replacement, default));
    }

    [Fact]
    public async Task Owner_authorized_execution_requires_filter_bound_authorization()
    {
        var identities = new IdentityProvider(Identity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new OutlookMemoryStore());
        var called = false;

        await Assert.ThrowsAsync<OwnerAuthorizationException>(() => state.ExecuteOwnerAuthorizedAsync(() =>
        {
            called = true;
            return Task.FromResult(true);
        }, default));

        Assert.False(called);
    }

    private static MicrosoftAccountIdentity Identity(string home, string tenant, string client, string username) =>
        new(home, tenant, client, username);

    internal sealed class IdentityProvider(MicrosoftAccountIdentity identity) : IMicrosoftAccountIdentityProvider
    {
        public MicrosoftAccountIdentity Identity { get; set; } = identity;
        public Exception? Failure { get; set; }
        public Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Identity) : Task.FromException<MicrosoftAccountIdentity>(Failure);
    }
}
