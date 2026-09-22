using PlannerEdge.Helper.Auth;
using Microsoft.AspNetCore.Http;
using PlannerEdge.Helper.Outlook;
using PlannerEdge.Helper.Security;

namespace MicrosoftWidgets.Helper.Tests;

public sealed class OutlookSecurityTests
{
    [Fact]
    public async Task TransitionPreservesSameAccountPairingAndForceResetSurvivesAuthFailure()
    {
        var tokens = new OutlookTokens();
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(tokens, store);
        var access = new OutlookAccessService(new WidgetPairingService(state, new OutlookClock()), state);
        var request = await access.CreatePairingAsync(new("native", new string('a', 64)), default);
        await access.ApproveAsync(request.Id, default);
        var credential = (await access.PollAsync(request.Id, new string('a', 64), default)).Credential!;
        await state.TransitionAsync(() => Task.FromResult(true), default);
        Assert.True(await access.ValidateCredentialAsync(credential, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.TransitionAsync<bool>(() => throw new InvalidOperationException(), default, forceInvalidate: true));
        Assert.False(await access.ValidateCredentialAsync(credential, default));
    }

    [Fact]
    public async Task PairingRequiresApprovalSecretAndCurrentAccount()
    {
        var tokens = new OutlookTokens();
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(tokens, store);
        var access = new OutlookAccessService(new WidgetPairingService(state, new OutlookClock()), state);
        var secret = new string('a', 64);
        var request = await access.CreatePairingAsync(new("instance-one", secret), default);
        Assert.Equal("pending", (await access.PollAsync(request.Id, secret, default)).Status);
        await Assert.ThrowsAsync<OutlookException>(() => access.PollAsync(request.Id, new string('b', 64), default));
        await access.ApproveAsync(request.Id, default);
        var credential = (await access.PollAsync(request.Id, secret, default)).Credential!;
        Assert.Equal(credential, (await access.PollAsync(request.Id, secret, default)).Credential);
        Assert.True(await access.ValidateCredentialAsync(credential, default));
        Assert.False(await access.ValidateCredentialAsync("wrong", default));
        var restartState = new MicrosoftAccountState(tokens, store);
        var restarted = new OutlookAccessService(new WidgetPairingService(restartState, new OutlookClock()), restartState);
        Assert.True(await restarted.ValidateCredentialAsync(credential, default));
        tokens.Account = "account-b";
        Assert.False(await access.ValidateCredentialAsync(credential, default));
        tokens.Account = "account-a";
        Assert.False(await access.ValidateCredentialAsync(credential, default));
    }

    [Fact]
    public async Task ExplicitResetAndRevocationInvalidateCredentials()
    {
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(new OutlookTokens(), store);
        var access = new OutlookAccessService(new WidgetPairingService(state, new OutlookClock()), state);
        async Task<string> Pair()
        {
            var request = await access.CreatePairingAsync(new("instance", new string('c', 64)), default);
            await access.ApproveAsync(request.Id, default);
            return (await access.PollAsync(request.Id, new string('c', 64), default)).Credential!;
        }
        var credential = await Pair();
        await access.RevokeAsync(Assert.Single(await access.GetPairedAsync(default)).CredentialId, default);
        Assert.False(await access.ValidateCredentialAsync(credential, default));
        credential = await Pair();
        await state.InvalidateAsync(default);
        Assert.False(await access.ValidateCredentialAsync(credential, default));
    }

    [Fact]
    public async Task PairingExpiresAndBootstrapIsBounded()
    {
        var clock = new OutlookClock();
        var store = new OutlookMemoryStore();
        var state = new MicrosoftAccountState(new OutlookTokens(), store);
        var access = new OutlookAccessService(new WidgetPairingService(state, clock), state);
        var request = await access.CreatePairingAsync(new("instance", new string('c', 64)), default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<OutlookException>(() => access.ApproveAsync(request.Id, default));
        for (var i = 0; i < 10; i++) await access.CreatePairingAsync(new("instance" + i, new string('c', 64)), default);
        await Assert.ThrowsAsync<OutlookException>(() => access.CreatePairingAsync(new("extra", new string('c', 64)), default));
    }

    [Theory]
    [InlineData("localhost:5123", "http://localhost:5123", true)]
    [InlineData("localhost:5123", "http://localhost:5124", false)]
    [InlineData("evil.example:5123", "http://evil.example:5123", false)]
    [InlineData("localhost:5123", "null", false)]
    [InlineData("127.0.0.1:5123", "http://127.0.0.1:5123", true)]
    public void SessionRequiresLoopbackHostAndExactOrigin(string host, string origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString(host);
        context.Connection.LocalPort = 5123;
        context.Request.Headers.Origin = origin;
        Assert.Equal(expected, OutlookAccessService.IsSameOrigin(context.Request));
    }

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/test", true)]
    [InlineData("http://teams.microsoft.com/test", false)]
    [InlineData("https://localhost/test", false)]
    [InlineData("https://127.1/test", false)]
    [InlineData("https://[::1]/test", false)]
    [InlineData("https://10.0.0.1/test", false)]
    [InlineData("https://[::ffff:192.168.1.1]/test", false)]
    [InlineData("https://[fc00::1]/test", false)]
    [InlineData("https://[fe80::1]/test", false)]
    [InlineData("https://192.168.1.1/test", false)]
    [InlineData("https://172.16.0.1/test", false)]
    [InlineData("https://100.64.0.1/test", false)]
    [InlineData("https://169.254.169.254/test", false)]
    [InlineData("https://user:pass@example.com/test", false)]
    [InlineData("https://example.com/%0aevil", false)]
    [InlineData("msteams://example.com/test", false)]
    public void JoinUrlValidation(string url, bool expected) => Assert.Equal(expected, OutlookJoinService.IsSafeUrl(url));
}
