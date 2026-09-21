using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Tests;

public sealed class MicrosoftAuthAcquisitionTests
{
    [Fact]
    public void PlannerAndConversationScopesRemainSeparated()
    {
        Assert.Equal(new[] { "User.Read", "Tasks.ReadWrite" }, MicrosoftAuthService.PlannerScopes);
        Assert.Equal(new[] { "User.Read", "Tasks.ReadWrite", "Group-Conversation.ReadWrite.All" }, MicrosoftAuthService.PlannerConnectScopes);
        Assert.Equal(new[] { "Group-Conversation.ReadWrite.All" }, MicrosoftAuthService.ConversationScopes);
    }

    [Fact]
    public async Task SilentSuccessNeverInvokesInteractiveAcquisition()
    {
        var result = await MicrosoftAuthService.AcquireSilentFirstAsync(
            _ => Task.FromResult("silent"), (_, _) => throw new InvalidOperationException("Unexpected interaction"), default);
        Assert.Equal("silent", result);
    }

    [Fact]
    public async Task OnlyUiRequiredFallsBackToInteractiveOnce()
    {
        var interactions = 0;
        var result = await MicrosoftAuthService.AcquireSilentFirstAsync<string>(
            _ => throw new MsalUiRequiredException("consent_required", "Consent required"),
            (_, _) => { interactions++; return Task.FromResult("interactive"); }, default);
        Assert.Equal("interactive", result);
        Assert.Equal(1, interactions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkOrMsalServiceFailureDoesNotLaunchInteraction(bool msal)
    {
        Exception failure = msal ? new MsalServiceException("temporarily_unavailable", "Network unavailable") : new HttpRequestException();
        var interactions = 0;
        var exception = await Record.ExceptionAsync(() => MicrosoftAuthService.AcquireSilentFirstAsync<string>(
            _ => throw failure, (_, _) => { interactions++; return Task.FromResult("interactive"); }, default));
        Assert.Same(failure, exception);
        Assert.Equal(0, interactions);
    }

    [Fact]
    public async Task CancellationBetweenSilentFailureAndFallbackDoesNotOpenBrowser()
    {
        using var source = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MicrosoftAuthService.AcquireSilentFirstAsync<string>(
            _ => { source.Cancel(); throw new MsalUiRequiredException("consent_required", "Consent required"); },
            (_, _) => throw new InvalidOperationException("Unexpected interaction"), source.Token));
    }
}
