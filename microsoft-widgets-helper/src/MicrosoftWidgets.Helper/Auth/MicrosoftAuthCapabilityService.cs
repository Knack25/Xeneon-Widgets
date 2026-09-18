using System.Text.Json.Serialization;
using Microsoft.Identity.Client;

namespace PlannerEdge.Helper.Auth;

public sealed record MicrosoftAuthCapability(string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null);

public sealed record MicrosoftAuthCapabilities(MicrosoftAuthCapability Planner, MicrosoftAuthCapability AssigneeNames, MicrosoftAuthCapability BoardMembers);

public sealed class MicrosoftAuthCapabilityService(IMicrosoftAuthService auth)
{
    public async Task<MicrosoftAuthCapabilities> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var configuration = await auth.GetConfigurationAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(configuration.ClientId))
                return All(new("unavailable", "Configure the Microsoft application first."));
            var status = await auth.GetStatusAsync(cancellationToken);
            if (!status.IsSignedIn) return All(SignedOut());

            var planner = await CheckAsync(MicrosoftAuthService.PlannerScopes, cancellationToken);
            var assigneeNames = await CheckAsync(MicrosoftAuthService.AssigneeNamesScopes, cancellationToken);
            var boardMembers = await CheckAsync(MicrosoftAuthService.BoardMembersScopes, cancellationToken);

            // A concurrent sign-out/configuration change must not leave a misleading
            // available result for the account that was present at the start of the probe.
            var currentStatus = await auth.GetStatusAsync(cancellationToken);
            if (!currentStatus.IsSignedIn) return All(SignedOut());
            var currentConfiguration = await auth.GetConfigurationAsync(cancellationToken);
            if (configuration != currentConfiguration || !string.Equals(status.AccountHint, currentStatus.AccountHint, StringComparison.OrdinalIgnoreCase))
                return All(new("unavailable", "The Microsoft account changed. Refresh the connection status."));
            return new(planner, assigneeNames, boardMembers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return All(Unavailable()); }
        catch (Exception ex) when (ex is MsalException or HttpRequestException or IOException or InvalidOperationException)
        { return All(Unavailable()); }
    }

    private async Task<MicrosoftAuthCapability> CheckAsync(IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        try
        {
            await auth.GetTokenForScopesAsync(scopes, cancellationToken);
            return new("available");
        }
        catch (MsalUiRequiredException ex)
        {
            return ex.ErrorCode == "no_account" ? SignedOut() : new("interaction_required", "Microsoft requires sign-in or permission approval to enable this feature.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception ex) when (ex is MsalException or HttpRequestException or IOException or InvalidOperationException)
        { return Unavailable(); }
    }

    private static MicrosoftAuthCapability SignedOut() => new("signed_out", "Sign in to Microsoft to check this feature.");
    private static MicrosoftAuthCapability Unavailable() => new("unavailable", "Microsoft permissions could not be checked. Try again when the connection is available.");
    private static MicrosoftAuthCapabilities All(MicrosoftAuthCapability capability) => new(capability, capability, capability);
}
