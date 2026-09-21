using Microsoft.Identity.Client;
using Microsoft.Extensions.Options;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;
using PlannerEdge.Helper.Storage;

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
    public async Task CorePlannerTokenRequestsOnlyCoreScopes()
    {
        var operations = new RecordingAuthOperations();
        var service = CreateService(operations);

        await service.GetAccessTokenAsync(default);

        Assert.Equal(["User.Read", "Tasks.ReadWrite"], Assert.Single(operations.TokenRequests));
    }

    [Fact]
    public async Task PlannerSignInRequestsBundledScopesOnceWhenSuccessful()
    {
        var operations = new RecordingAuthOperations();
        var service = CreateService(operations);

        await service.SignInAsync(default);

        var request = Assert.Single(operations.ConnectionRequests);
        Assert.Equal(["User.Read", "Tasks.ReadWrite", "Group-Conversation.ReadWrite.All"], request.Scopes);
        Assert.False(request.RequireExistingAccount);
    }

    [Fact]
    public async Task ConversationTokenRequestsOnlyConversationScope()
    {
        var operations = new RecordingAuthOperations();
        var service = CreateService(operations);

        await service.GetConversationTokenAsync(default);

        Assert.Equal(["Group-Conversation.ReadWrite.All"], Assert.Single(operations.TokenRequests));
    }

    [Fact]
    public async Task EnableTaskChatRequestsOnlyConversationScopeForExistingAccount()
    {
        var operations = new RecordingAuthOperations();
        var service = CreateService(operations);

        await service.EnableTaskChatAsync(default);

        var request = Assert.Single(operations.ConnectionRequests);
        Assert.Equal(["Group-Conversation.ReadWrite.All"], request.Scopes);
        Assert.True(request.RequireExistingAccount);
    }

    [Fact]
    public async Task PlannerSignInFallsBackToCoreScopesWhenBundledConsentFails()
    {
        var operations = new RecordingAuthOperations
        {
            Connect = (scopes, _, _) => scopes.Contains("Group-Conversation.ReadWrite.All")
                ? throw new MsalServiceException("access_denied", "Conversation consent denied")
                : Task.FromResult(SignedInStatus)
        };
        var service = CreateService(operations);

        var result = await service.SignInAsync(default);

        Assert.True(result.IsSignedIn);
        Assert.Collection(operations.ConnectionRequests,
            request =>
            {
                Assert.Equal(["User.Read", "Tasks.ReadWrite", "Group-Conversation.ReadWrite.All"], request.Scopes);
                Assert.False(request.RequireExistingAccount);
            },
            request =>
            {
                Assert.Equal(["User.Read", "Tasks.ReadWrite"], request.Scopes);
                Assert.False(request.RequireExistingAccount);
            });
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

    private static readonly AuthStatusResponse SignedInStatus = new(true, "User", "user@example.com");

    private static MicrosoftAuthService CreateService(RecordingAuthOperations operations) =>
        new(Options.Create(new AzureAdOptions()),
            new LocalJsonStore(Path.Combine(Path.GetTempPath(), "PlannerEdgeTests", Guid.NewGuid().ToString("N"))),
            operations.AcquireTokenAsync, operations.ConnectAsync);

    private sealed class RecordingAuthOperations
    {
        public List<string[]> TokenRequests { get; } = [];
        public List<(string[] Scopes, bool RequireExistingAccount)> ConnectionRequests { get; } = [];
        public Func<string[], bool, CancellationToken, Task<AuthStatusResponse>> Connect { get; init; } =
            (_, _, _) => Task.FromResult(SignedInStatus);

        public Task<string> AcquireTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenRequests.Add(scopes.ToArray());
            return Task.FromResult("token");
        }

        public Task<AuthStatusResponse> ConnectAsync(IEnumerable<string> scopes, bool requireExistingAccount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = scopes.ToArray();
            ConnectionRequests.Add((values, requireExistingAccount));
            return Connect(values, requireExistingAccount, cancellationToken);
        }
    }
}
