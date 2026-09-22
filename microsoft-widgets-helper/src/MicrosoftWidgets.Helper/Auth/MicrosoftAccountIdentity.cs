namespace PlannerEdge.Helper.Auth;

public sealed record MicrosoftAccountIdentity(
    string HomeAccountId,
    string TenantId,
    string ClientId,
    string Username);

public interface IMicrosoftAccountIdentityProvider
{
    Task<MicrosoftAccountIdentity> GetAccountIdentityAsync(CancellationToken cancellationToken);
}

internal sealed record StoredMicrosoftAccountIdentity(string HomeAccountId, string TenantId, string ClientId)
{
    public bool Matches(MicrosoftAccountIdentity identity) =>
        string.Equals(HomeAccountId, identity.HomeAccountId, StringComparison.Ordinal) &&
        string.Equals(TenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ClientId, identity.ClientId, StringComparison.OrdinalIgnoreCase);

    public static StoredMicrosoftAccountIdentity From(MicrosoftAccountIdentity identity) =>
        new(identity.HomeAccountId, identity.TenantId, identity.ClientId);
}
