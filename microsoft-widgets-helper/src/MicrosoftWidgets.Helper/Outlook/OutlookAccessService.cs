using PlannerEdge.Helper.Auth;
using PlannerEdge.Helper.Security;

namespace PlannerEdge.Helper.Outlook;

// Compatibility adapter; all pairing state and validation live in the shared service.
public sealed class OutlookAccessService(WidgetPairingService pairing, MicrosoftAccountState state)
{
    public async Task<PairingCreated> CreatePairingAsync(PairingRequest request, CancellationToken ct) =>
        await pairing.CreateAsync(WidgetScope.Outlook, request, await state.GetAsync(ct), ct);
    public Task<PairingResult> PollAsync(string id, string secret, CancellationToken ct) => pairing.PollAsync(id, secret, ct);
    public Task<PairingResult> PollAsync(string id, string secret, AccountLease lease, CancellationToken ct) => pairing.PollAsync(id, secret, lease, ct);
    public Task ApproveAsync(string id, CancellationToken ct) => pairing.ApproveAsync(id, ct);
    public Task ApproveAsync(string id, AccountLease lease, CancellationToken ct) => pairing.ApproveAsync(id, lease, ct);
    public Task RevokeAsync(string id, CancellationToken ct) => pairing.RevokeAsync(id, ct);
    public Task RevokeAsync(string id, AccountLease lease, CancellationToken ct) => pairing.RevokeAsync(id, lease, ct);
    public async Task<IReadOnlyList<WidgetPendingPairing>> GetPendingAsync(CancellationToken ct) =>
        (await pairing.GetPendingAsync(ct)).Where(p => p.Scope == WidgetScope.Outlook).ToArray();
    public async Task<IReadOnlyList<WidgetPairedInstance>> GetPairedAsync(CancellationToken ct) =>
        (await pairing.GetPairedAsync(ct)).Where(p => p.Scope == WidgetScope.Outlook).ToArray();
    public IReadOnlyList<WidgetPendingPairing> GetPending(AccountLease lease) =>
        pairing.GetPending(lease).Where(p => p.Scope == WidgetScope.Outlook).ToArray();
    public async Task<IReadOnlyList<WidgetPairedInstance>> GetPairedAsync(AccountLease lease, CancellationToken ct) =>
        (await pairing.GetPairedAsync(lease, ct)).Where(p => p.Scope == WidgetScope.Outlook).ToArray();
    public async Task<bool> ValidateCredentialAsync(string credential, CancellationToken ct) =>
        await pairing.AuthenticateAsync(WidgetScope.Outlook, credential, ct) is not null;

    public static bool IsLocalHost(HttpRequest request) =>
        LoopbackRequestPolicy.IsAllowed(request.HttpContext, request.HttpContext.Connection.LocalPort);
    public static bool IsSameOrigin(HttpRequest request)
    {
        if (!IsLocalHost(request)) return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 ? request.Headers["Sec-Fetch-Site"].ToString() == "same-origin" :
            string.Equals(origin, request.Scheme + "://" + request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
