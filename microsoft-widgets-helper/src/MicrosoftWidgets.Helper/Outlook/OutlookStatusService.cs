using PlannerEdge.Helper.Auth;

namespace PlannerEdge.Helper.Outlook;

public sealed record OutlookStatusResponse(bool Configured, bool SignedIn, bool Ready, OutlookError? Error, IReadOnlyList<OutlookError> DiscoveryErrors);

public sealed class OutlookStatusService
{
    private readonly IMicrosoftAuthService auth;
    private readonly OutlookAccountState state;
    private readonly CalendarCatalogService catalog;
    private readonly OutlookPreferencesService preferences;
    private readonly TimeProvider clock;
    private readonly IOutlookTokenProvider tokens;
    private readonly SemaphoreSlim gate = new(1, 1);
    private OutlookStatusResponse? cached;
    private DateTimeOffset expires;
    private OutlookError? preferencesWarning;
    public OutlookStatusService(IMicrosoftAuthService auth, OutlookAccountState state, CalendarCatalogService catalog, OutlookPreferencesService preferences, TimeProvider clock, IOutlookTokenProvider tokens)
    {
        this.auth = auth; this.state = state; this.catalog = catalog; this.preferences = preferences; this.clock = clock;
        this.tokens = tokens;
        state.Invalidated += Refresh;
    }
    public void Refresh() { cached = null; expires = default; preferencesWarning = null; }
    public async Task<OutlookStatusResponse> GetAsync(CancellationToken ct)
    {
        var lease = await state.GetIdentityAsync(false, ct);
        await gate.WaitAsync(ct);
        try
        {
            if (cached is not null && clock.GetUtcNow() < expires) return cached with { DiscoveryErrors = Warnings() };
            var config = await auth.GetConfigurationAsync(ct);
            var authStatus = await auth.GetStatusAsync(ct);
            var configured = !string.IsNullOrWhiteSpace(config.ClientId);
            OutlookError? error = !configured ? new("not_configured", "Configure the Microsoft application first.") : !authStatus.IsSignedIn ? new("sign_in_required", "Sign in to Microsoft to connect Outlook.") : null;
            if (error is null)
            {
                try
                {
                    // Missing initial consent is setup state, not an account change.
                    // Check before data services invalidate leases on Graph auth failures.
                    await tokens.GetTokenAsync(ct);
                    await catalog.GetAsync(ct);
                    preferencesWarning = null;
                    try { await preferences.GetAsync(ct); }
                    catch (OutlookException ex) when (ex.StatusCode != 401) { preferencesWarning = ex.Error; }
                }
                catch (OutlookException ex) { error = ex.Error; }
            }
            var response = new OutlookStatusResponse(configured, authStatus.IsSignedIn, error is null, error, Warnings());
            if (state.IsCurrent(lease)) { cached = response; expires = clock.GetUtcNow().AddSeconds(60); }
            return response;
        }
        finally { gate.Release(); }
    }

    private IReadOnlyList<OutlookError> Warnings() => preferencesWarning is { } warning ? [.. catalog.DiscoveryErrors, warning] : catalog.DiscoveryErrors;
}
