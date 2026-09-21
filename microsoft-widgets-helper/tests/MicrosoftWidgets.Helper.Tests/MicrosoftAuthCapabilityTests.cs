using System.Text.Json;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Contracts;

namespace PlannerEdge.Helper.Tests;

public sealed class MicrosoftAuthCapabilityTests
{
    [Fact]
    public async Task ChecksEachFeatureSilentlyAndNeverReturnsTokens()
    {
        var auth = new CapabilityAuth();
        var result = await new MicrosoftAuthCapabilityService(auth).GetAsync(default);
        Assert.Equal("available", result.Planner.State);
        Assert.Equal("available", result.TaskChat.State);
        Assert.Equal("available", result.AssigneeNames.State);
        Assert.Equal("available", result.BoardMembers.State);
        Assert.Equal(new[] { "User.Read", "Tasks.ReadWrite" }, auth.Requests[0]);
        Assert.Equal(new[] { "Group-Conversation.ReadWrite.All" }, auth.Requests[1]);
        Assert.Equal(new[] { "User.ReadBasic.All" }, auth.Requests[2]);
        Assert.Equal(new[] { "GroupMember.ReadBasic.All" }, auth.Requests[3]);
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"taskChat\"", json);
        Assert.Contains("\"assigneeNames\"", json);
        Assert.DoesNotContain("secret-token", json);
    }

    [Fact]
    public async Task InteractionRequiredIsDistinctFromNetworkFailurePerFeature()
    {
        var auth = new CapabilityAuth
        {
            Acquire = scopes => scopes[0] switch
            {
                "User.ReadBasic.All" => throw new MsalUiRequiredException("consent_required", "Sensitive provider detail"),
                "GroupMember.ReadBasic.All" => throw new HttpRequestException("Sensitive network detail"),
                _ => Task.FromResult("secret-token")
            }
        };
        var result = await new MicrosoftAuthCapabilityService(auth).GetAsync(default);
        Assert.Equal("available", result.Planner.State);
        Assert.Equal("available", result.TaskChat.State);
        Assert.Equal("interaction_required", result.AssigneeNames.State);
        Assert.Equal("unavailable", result.BoardMembers.State);
        Assert.DoesNotContain("Sensitive", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(true, "signed_out")]
    [InlineData(false, "unavailable")]
    public async Task SignedOutOrUnconfiguredSkipsTokenAcquisition(bool configured, string state)
    {
        var auth = new CapabilityAuth { SignedIn = false, Configured = configured };
        var result = await new MicrosoftAuthCapabilityService(auth).GetAsync(default);
        Assert.Equal(state, result.Planner.State);
        Assert.Equal(state, result.TaskChat.State);
        Assert.Equal(state, result.AssigneeNames.State);
        Assert.Equal(state, result.BoardMembers.State);
        Assert.Empty(auth.Requests);
    }

    [Fact]
    public async Task CapabilityChecksAreNotCached()
    {
        var auth = new CapabilityAuth { Acquire = _ => throw new MsalUiRequiredException("consent_required", "Consent required") };
        var service = new MicrosoftAuthCapabilityService(auth);
        Assert.Equal("interaction_required", (await service.GetAsync(default)).Planner.State);
        auth.Acquire = _ => Task.FromResult("secret-token");
        Assert.Equal("available", (await service.GetAsync(default)).Planner.State);
        Assert.Equal(8, auth.Requests.Count);
    }

    [Fact]
    public async Task SignOutDuringProbeCannotReturnAvailableCapabilities()
    {
        var auth = new CapabilityAuth();
        auth.Acquire = _ => { auth.SignedIn = false; return Task.FromResult("secret-token"); };
        var result = await new MicrosoftAuthCapabilityService(auth).GetAsync(default);
        Assert.Equal("signed_out", result.Planner.State);
        Assert.Equal("signed_out", result.TaskChat.State);
        Assert.Equal("signed_out", result.AssigneeNames.State);
        Assert.Equal("signed_out", result.BoardMembers.State);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutPermissionDiagnosis()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var auth = new CapabilityAuth();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MicrosoftAuthCapabilityService(auth).GetAsync(source.Token));
        Assert.Empty(auth.Requests);
    }

    [Fact]
    public async Task TaskChatConsentIsIndependentFromPlannerAccess()
    {
        var auth = new CapabilityAuth
        {
            Acquire = scopes => scopes[0] == "Group-Conversation.ReadWrite.All"
                ? throw new MsalUiRequiredException("consent_required", "Consent required")
                : Task.FromResult("secret-token")
        };

        var capabilities = await new MicrosoftAuthCapabilityService(auth).GetAsync(default);

        Assert.Equal("available", capabilities.Planner.State);
        Assert.Equal("interaction_required", capabilities.TaskChat.State);
    }
}

internal sealed class CapabilityAuth : IMicrosoftAuthService
{
    public bool SignedIn { get; set; } = true;
    public bool Configured { get; set; } = true;
    public List<string[]> Requests { get; } = [];
    public Func<string[], Task<string>> Acquire { get; set; } = _ => Task.FromResult("secret-token");
    public Task<string> GetTokenForScopesAsync(IEnumerable<string> scopes, CancellationToken ct) { ct.ThrowIfCancellationRequested(); var values = scopes.ToArray(); Requests.Add(values); return Acquire(values); }
    public Task<AzureAdOptions> GetConfigurationAsync(CancellationToken ct) => Task.FromResult(new AzureAdOptions { ClientId = Configured ? "configured-client" : "" });
    public Task<AuthStatusResponse> GetStatusAsync(CancellationToken ct) => Task.FromResult(new AuthStatusResponse(SignedIn, "User", SignedIn ? "user@example.com" : null));
    public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<AzureAdOptions> SaveConfigurationAsync(AzureAdOptions configuration, CancellationToken ct) => throw new NotSupportedException();
    public Task<AuthStatusResponse> SignInAsync(CancellationToken ct) => throw new NotSupportedException("Capability checks must never interact.");
    public Task<AuthStatusResponse> ConnectOutlookAsync(CancellationToken ct) => throw new NotSupportedException("Capability checks must never interact.");
    public Task<AuthStatusResponse> EnableTaskChatAsync(CancellationToken ct) => throw new NotSupportedException("Capability checks must never interact.");
    public Task<AuthStatusResponse> EnableAssigneeNamesAsync(CancellationToken ct) => throw new NotSupportedException("Capability checks must never interact.");
    public Task<AuthStatusResponse> EnableBoardMembersAsync(CancellationToken ct) => throw new NotSupportedException("Capability checks must never interact.");
    public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
}
