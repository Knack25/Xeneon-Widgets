using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;
using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Outlook;

public interface IOutlookTokenProvider : IMicrosoftAccountIdentityProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
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

    public async Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken)
    {
        try { return await auth.GetAccountIdentityAsync(cancellationToken); }
        catch (MsalUiRequiredException) { throw new OutlookException("sign_in_required", "Sign in to Microsoft to use Outlook.", 401); }
        catch (InvalidOperationException) { throw new OutlookException("not_configured", "Configure the Microsoft application first.", 401); }
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record StoredOutlookCredential(string CredentialId, string InstanceId, string AccountKey, string Hash);
