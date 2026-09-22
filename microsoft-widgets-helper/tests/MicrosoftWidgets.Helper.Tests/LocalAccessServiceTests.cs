using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Security;
using AccountStateTests = MicrosoftWidgets.Helper.Tests.MicrosoftAccountStateTests;
using MemoryStore = MicrosoftWidgets.Helper.Tests.OutlookMemoryStore;

namespace PlannerEdge.Helper.Tests;

public sealed class LocalAccessServiceTests
{
    [Fact]
    public void Bootstrap_tokens_are_32_random_bytes_and_can_be_exchanged_once()
    {
        var clock = new TestTimeProvider();
        var access = new LocalAccessService(clock);

        var bootstrap = access.CreateBootstrap();

        Assert.Equal(32, Base64UrlDecode(bootstrap.Token).Length);
        Assert.True(bootstrap.ExpiresAt > clock.GetUtcNow());
        var session = access.ExchangeBootstrap(bootstrap.Token);
        Assert.True(access.ValidateOwnerSession(session));
        Assert.Throws<LocalAccessException>(() => access.ExchangeBootstrap(bootstrap.Token));
    }

    [Fact]
    public void Bootstrap_and_owner_sessions_expire_at_their_security_boundaries()
    {
        var clock = new TestTimeProvider();
        var access = new LocalAccessService(clock);
        var expiredBootstrap = access.CreateBootstrap();
        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        Assert.Throws<LocalAccessException>(() => access.ExchangeBootstrap(expiredBootstrap.Token));

        var session = access.ExchangeBootstrap(access.CreateBootstrap().Token);
        clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));

        Assert.False(access.ValidateOwnerSession(session));
    }

    [Fact]
    public void Matching_length_forged_tokens_are_rejected()
    {
        var access = new LocalAccessService(new TestTimeProvider());
        var bootstrap = access.CreateBootstrap();
        var forged = new string(bootstrap.Token.Reverse().ToArray());

        Assert.Throws<LocalAccessException>(() => access.ExchangeBootstrap(forged));
        Assert.False(access.ValidateOwnerSession(forged));
    }

    [Fact]
    public void Bounded_collections_evict_the_oldest_bootstraps_and_sessions()
    {
        var access = new LocalAccessService(new TestTimeProvider());
        var bootstraps = Enumerable.Range(0, LocalAccessService.MaximumBootstrapCount + 1)
            .Select(_ => access.CreateBootstrap()).ToArray();

        Assert.Throws<LocalAccessException>(() => access.ExchangeBootstrap(bootstraps[0].Token));
        var latestSession = access.ExchangeBootstrap(bootstraps[^1].Token);
        var sessions = new List<string> { latestSession };
        sessions.AddRange(Enumerable.Range(0, LocalAccessService.MaximumOwnerSessionCount)
            .Select(_ => access.ExchangeBootstrap(access.CreateBootstrap().Token)));

        Assert.False(access.ValidateOwnerSession(sessions[0]));
        Assert.True(access.ValidateOwnerSession(sessions[^1]));
    }

    [Fact]
    public void Sessions_are_memory_only_and_can_be_invalidated()
    {
        var access = new LocalAccessService(new TestTimeProvider());
        var session = access.ExchangeBootstrap(access.CreateBootstrap().Token);

        Assert.False(new LocalAccessService(new TestTimeProvider()).ValidateOwnerSession(session));
        access.InvalidateOwnerSessions();
        Assert.False(access.ValidateOwnerSession(session));
    }

    [Fact]
    public async Task Account_invalidation_revokes_owner_sessions_and_pending_bootstraps()
    {
        var identities = new AccountStateTests.IdentityProvider(
            new MicrosoftAccountIdentity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new MemoryStore());
        var access = new LocalAccessService(new TestTimeProvider(), state);
        var owner = await access.ExchangeBootstrapAsync(access.CreateBootstrap().Token, default);
        var pending = access.CreateBootstrap();

        await state.InvalidateAsync(default);

        Assert.False(await access.ValidateOwnerSessionAsync(owner, default));
        await Assert.ThrowsAsync<LocalAccessException>(() => access.ExchangeBootstrapAsync(pending.Token, default));
    }

    [Fact]
    public async Task First_account_discovery_does_not_consume_first_run_bootstrap()
    {
        var identities = new AccountStateTests.IdentityProvider(
            new MicrosoftAccountIdentity("home-a", "tenant-a", "client-a", "user@example.com"));
        var state = new MicrosoftAccountState(identities, new MemoryStore());
        var access = new LocalAccessService(new TestTimeProvider(), state);
        var bootstrap = access.CreateBootstrap();

        var owner = await access.ExchangeBootstrapAsync(bootstrap.Token, default);

        Assert.True(await access.ValidateOwnerSessionAsync(owner, default));
    }

    private static byte[] Base64UrlDecode(string token)
    {
        var padded = token.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }
}
