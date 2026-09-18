using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Outlook;

public interface IOutlookTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
    Task<string> GetAccountKeyAsync(CancellationToken cancellationToken);
}

public sealed class OutlookTokenProvider(IMicrosoftAuthService auth) : IOutlookTokenProvider
{
    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try { return await auth.GetTokenForScopesAsync(OutlookScopes.All, cancellationToken); }
        catch (MsalUiRequiredException) { throw new OutlookException("consent_required", "Reconnect Outlook to grant the complete permission bundle.", 401); }
        catch (MsalException) { throw new OutlookException("unavailable", "Microsoft sign-in is unavailable."); }
        catch (InvalidOperationException) { throw new OutlookException("not_configured", "Configure the Microsoft application first.", 401); }
    }

    public async Task<string> GetAccountKeyAsync(CancellationToken cancellationToken)
    {
        var configuration = await auth.GetConfigurationAsync(cancellationToken);
        var status = await auth.GetStatusAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuration.ClientId)) throw new OutlookException("not_configured", "Configure the Microsoft application first.", 401);
        if (!status.IsSignedIn || string.IsNullOrWhiteSpace(status.AccountHint)) throw new OutlookException("sign_in_required", "Sign in to Microsoft to use Outlook.", 401);
        return Hash(configuration.ClientId + "\n" + configuration.Tenant + "\n" + status.AccountHint.ToLowerInvariant());
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
