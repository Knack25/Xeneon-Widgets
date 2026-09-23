# Microsoft Widgets Security Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remediate every confirmed audit finding and related integrity defect while preserving a tray-driven, non-technical setup experience for Planner and Outlook widgets.

**Architecture:** Add one helper-wide local capability service for short-lived owner sessions and account-bound, integration-scoped widget credentials. Enforce loopback Host policy before all content, move both widgets to scoped pairing, bind caches to immutable Microsoft identities, and then harden lifecycle races, Planner integrity, and release construction.

**Tech Stack:** .NET 10 Windows minimal APIs, MSAL, ASP.NET Core endpoint filters/middleware, Windows named pipes and ACLs, vanilla JavaScript ES modules, Node test runners, PowerShell packaging, Inno Setup 6, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-22-security-remediation-design.md`

## Global Constraints

- Keep the helper bound to loopback on port `8787`; loopback, Origin, and Host are not authentication.
- Owner setup is bootstrapped only from the helper process/tray through a single-use URL-fragment secret.
- Planner and Outlook credentials are independently revocable and cannot cross integration scopes.
- Do not add Graph permissions, remote administration, multi-account support, or machine-wide installation.
- Preserve both legacy Planner aliases and `/api/planner/*` during this release, with identical authorization.
- Clear work data on sign-out, account/configuration transitions, and Graph 401/403; permit bounded offline fallback only for transient failures.
- Use immutable MSAL home-account and actual tenant identifiers for authorization/cache identity; usernames are display-only.
- Keep secrets, Microsoft tokens, notes, chat, event details, and Graph bodies out of logs and URLs.
- Use Conventional Commits and do not publish a release without a separate explicit request.

## Review Focus

- IPv6, uppercase hostnames, explicit/default ports, and forwarded headers must not bypass the loopback Host boundary; Task 1 adds these cases.
- A bootstrap replay, expired token, browser refresh, or second tab must fail safely without exposing setup state; Task 2 adds these cases.
- Concurrent account transition during authorization must invalidate owner sessions, pairings, and response publication atomically; Tasks 4 and 6 add these cases.
- A native widget with malformed or missing Origin must still be decided by its scoped credential, while a valid credential on the wrong integration must fail; Tasks 4 and 5 add these cases.
- Interrupted builds and seeded ignored files must never leak into installer or portable artifacts; Task 10 adds clean-stage and inventory tests.

## Audit Traceability

| Audit item | Owning tasks |
| --- | --- |
| SEC-01 Planner/shared endpoints unauthenticated | Tasks 2-5 |
| SEC-02 forgeable Outlook setup session | Tasks 2-4 |
| SEC-03 missing global Host filtering | Task 1 |
| SEC-04 Planner data after authorization loss | Tasks 6-7 |
| SEC-05 Outlook late-response authorization race | Task 8 |
| SEC-06 Outlook source-removal publication race | Task 8 |
| SEC-07 mutable identity concern | Task 6 |
| SEC-08 elevated installer executes installed helper | Tasks 3 and 10 |
| SEC-09 stale build output enters releases | Task 10 |
| Notes data loss and inconsistent selected-board scope | Task 9 |
| Planner next-link and response bounds | Task 9 |
| Setup framing, no-store, and request policy | Tasks 1-3 |
| Missing automated security verification | Tasks 11-12 |

---

### Task 1: Global Loopback Host and Response Boundary

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/LoopbackRequestPolicy.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/SecurityHeadersMiddleware.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/appsettings.json`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/LoopbackRequestPolicyTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerHttpIntegrationTests.cs`

**Interfaces:**
- Produces: `LoopbackRequestPolicy.IsAllowed(HttpContext context, int expectedPort): bool` and `UseHelperSecurityBoundary(WebApplication app)`.
- Consumed by: every later endpoint task; this middleware must run before default files, static files, CORS, and route execution.

- [ ] **Step 1: Write failing Host-policy and response-header tests**

Add table-driven unit cases for `localhost:8787`, `LOCALHOST:8787`, `127.0.0.1:8787`, and `[::1]:8787`, plus rejection of `attacker.invalid`, `localhost:9999`, forwarded-host substitution, non-loopback local addresses, and missing Host. Extend HTTP integration coverage to assert an attacker Host cannot read static setup content, `/health`, either Planner route family, or Outlook routes. Assert API responses use `no-store`, setup uses CSP with `frame-ancestors 'none'`, and widget preview paths are not blocked by setup framing policy.

```csharp
[Theory]
[InlineData("localhost:8787", true)]
[InlineData("LOCALHOST:8787", true)]
[InlineData("127.0.0.1:8787", true)]
[InlineData("[::1]:8787", true)]
[InlineData("attacker.invalid:8787", false)]
[InlineData("localhost:9999", false)]
public void Destination_must_be_expected_loopback_host(string host, bool expected)
{
    var context = HttpContextFactory.Create(host, localPort: 8787);
    Assert.Equal(expected, LoopbackRequestPolicy.IsAllowed(context, 8787));
}
```

- [ ] **Step 2: Run the focused tests and confirm failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~LoopbackRequestPolicyTests|FullyQualifiedName~PlannerHttpIntegrationTests"`

Expected: FAIL because the global policy and headers do not exist and attacker-controlled Host still reaches content.

- [ ] **Step 3: Implement the request boundary**

Implement strict destination validation using `Request.Host`, `Connection.LocalIpAddress`, `Connection.LocalPort`, and the fixed configured port. Do not trust `X-Forwarded-Host`. Add response policy by path: API/account/setup responses get `no-store` and `nosniff`; setup HTML gets a restrictive self-only CSP and anti-framing headers; packaged/preview widget paths retain their required embedding behavior. Set `AllowedHosts` to explicit loopback hosts and install middleware before `UseDefaultFiles`/`UseStaticFiles`.

```csharp
public static bool IsAllowed(HttpContext context, int expectedPort)
{
    var host = context.Request.Host.Host.Trim('[', ']');
    var loopbackName = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    var loopbackAddress = IPAddress.TryParse(host, out var parsed) && IPAddress.IsLoopback(parsed);
    return (loopbackName || loopbackAddress)
        && (context.Request.Host.Port ?? 80) == expectedPort
        && context.Connection.LocalPort == expectedPort
        && (context.Connection.LocalIpAddress is null || IPAddress.IsLoopback(context.Connection.LocalIpAddress));
}
```

- [ ] **Step 4: Run focused and full helper tests**

Run the focused command from Step 2, then `dotnet test microsoft-widgets-helper/MicrosoftWidgets.Helper.slnx`.

Expected: all tests PASS; intentionally synthetic test servers set their local port/Host explicitly.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs microsoft-widgets-helper/src/MicrosoftWidgets.Helper/appsettings.json microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/LoopbackRequestPolicyTests.cs microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerHttpIntegrationTests.cs
git commit -m "fix(security): enforce loopback request boundary"
```

### Task 2: Tray-Bootstrapped Owner Sessions

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/LocalAccessContracts.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/LocalAccessService.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/OwnerAuthorizationFilter.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/helper-api.js`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Hosting/HelperHost.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Hosting/TrayService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/index.html`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/navigation.js`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/LocalAccessServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OwnerBootstrapEndpointTests.cs`
- Test: `microsoft-widgets-helper/tests/setup-browser.mjs`

**Interfaces:**
- Produces: `CreateBootstrap(): OwnerBootstrap`, `ExchangeBootstrap(string): string`, `ValidateOwnerSession(string): bool`, `InvalidateOwnerSessions()`.
- Produces browser helper: `helperApi.fetch(path, options)` and `helperApi.ready` with the owner session stored only in `sessionStorage`.
- Consumes: Task 1 exact-origin/Host boundary.

- [ ] **Step 1: Write failing service and browser tests**

Cover 32-byte random tokens, hashed in-memory storage, five-minute bootstrap expiry, single-use exchange, eight-hour session expiry, maximum collection bounds, restart/no-persistence behavior, and constant-time matching. Endpoint tests must prove forged same-origin headers cannot create a session, replay fails, and no configuration/install data is returned before exchange. Browser tests must prove the fragment is removed with `history.replaceState`, no token enters query strings, and direct navigation renders tray instructions.

```csharp
var bootstrap = access.CreateBootstrap();
var session = access.ExchangeBootstrap(bootstrap.Token);
Assert.True(access.ValidateOwnerSession(session));
Assert.Throws<LocalAccessException>(() => access.ExchangeBootstrap(bootstrap.Token));
clock.Advance(TimeSpan.FromHours(8).Add(TimeSpan.FromSeconds(1)));
Assert.False(access.ValidateOwnerSession(session));
```

- [ ] **Step 2: Run focused tests and confirm failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~LocalAccessServiceTests|FullyQualifiedName~OwnerBootstrapEndpointTests"`

Run: `node microsoft-widgets-helper/tests/setup-browser.mjs`

Expected: FAIL because setup currently creates Outlook sessions from headers and opens an uncredentialed URL.

- [ ] **Step 3: Implement owner bootstrap and setup API wrapper**

Use `RandomNumberGenerator.GetBytes(32)`, SHA-256 hashes, `CryptographicOperations.FixedTimeEquals`, `TimeProvider`, lock-protected bounded dictionaries, and one-use removal before session return. Change `HelperHost.OpenSetup`/`OpenUpdates` to request a fresh bootstrap and open `http://localhost:8787/#access=<token>` plus the optional section. Add `POST /api/local-access/session`, accepting only `X-Microsoft-Widgets-Bootstrap`, and require `X-Microsoft-Widgets-Owner` for owner-filtered routes. The static shell contains no sensitive state until `helperApi.ready` succeeds.

```csharp
public sealed record OwnerBootstrap(string Token, DateTimeOffset ExpiresAt);
public sealed class OwnerAuthorizationFilter(LocalAccessService access) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var token = context.HttpContext.Request.Headers[LocalAccessHeaders.Owner].ToString();
        return access.ValidateOwnerSession(token) ? await next(context) : Results.Unauthorized();
    }
}
```

- [ ] **Step 4: Verify service, browser, and tray regressions**

Run focused tests from Step 2 plus:

`dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~Tray|FullyQualifiedName~HelperPackage"`

Expected: PASS, and generated setup URLs contain only a fragment secret.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Hosting microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot microsoft-widgets-helper/tests
git commit -m "fix(security): bootstrap owner setup from tray"
```

### Task 3: Owner-Protected Management and Same-User Shutdown

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Hosting/HelperControlPipe.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Hosting/HelperHost.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Updates/UpdateEndpoints.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/updates.js`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/outlook-setup.js`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OwnerManagementEndpointTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/HelperControlPipeTests.cs`
- Test: `microsoft-widgets-helper/tests/update-ui.test.mjs`
- Test: `microsoft-widgets-helper/tests/outlook-setup.test.mjs`

**Interfaces:**
- Produces: `HelperControlPipe.RunAsync(IHostApplicationLifetime, CancellationToken)` and `HelperControlPipe.RequestStopAsync(TimeSpan, CancellationToken)` using a pipe ACL limited to the current user SID.
- Consumes: `OwnerAuthorizationFilter` and `helperApi.fetch` from Task 2.

- [ ] **Step 1: Write failing authorization and pipe tests**

Enumerate configuration GET/PUT, auth status/capabilities/me/sign-in/permission upgrades/sign-out, installation/downloads, updates status/check/install/result, Outlook setup routes, pairing administration, and `/host/stop`. Assert anonymous and widget credentials return 401/403 while an owner session succeeds. Assert GET sign-in returns 404/405. Pipe tests use a unique test pipe and verify same-user stop succeeds, random data does not stop, timeout is bounded, and pipe security names only the current SID plus LocalSystem.

```csharp
[Theory]
[MemberData(nameof(OwnerOnlyRoutes))]
public async Task Management_route_requires_owner_session(HttpMethod method, string path)
{
    Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(method, path)).StatusCode);
    Assert.NotEqual(HttpStatusCode.Unauthorized, (await SendOwnerAsync(method, path)).StatusCode);
}
```

- [ ] **Step 2: Run focused tests and confirm failure**

Run the two new .NET test classes and the two browser test files. Expected: FAIL because management routes and stop are anonymous and UI uses direct fetches.

- [ ] **Step 3: Protect management routes and replace HTTP CLI stop**

Group management routes and apply `OwnerAuthorizationFilter`; remove GET `/auth/sign-in`; retain POST only. Move update endpoint authorization from fixed headers to owner sessions. Register a hosted named-pipe listener and change `--stop` to `RequestStopAsync` without starting ASP.NET. Keep `/host/stop` only as an owner-filtered setup action. Use `helperApi.fetch` throughout setup scripts.

```csharp
var owner = app.MapGroup("").AddEndpointFilter<OwnerAuthorizationFilter>();
owner.MapGet("/configuration", async (IMicrosoftAuthService auth, CancellationToken ct) =>
    Results.Ok(await auth.GetConfigurationAsync(ct)));
owner.MapPost("/auth/sign-in", async (IMicrosoftAuthService auth, OutlookAccountState state, CancellationToken ct) =>
    Results.Ok(await state.TransitionAsync(() => auth.SignInAsync(ct), ct)));
owner.MapUpdates();
owner.MapHelperHostManagement();
```

- [ ] **Step 4: Run management, UI, and full helper tests**

Run focused tests, then `dotnet test microsoft-widgets-helper/MicrosoftWidgets.Helper.slnx`, `node microsoft-widgets-helper/tests/update-ui.test.mjs`, and `node microsoft-widgets-helper/tests/outlook-setup.test.mjs`.

Expected: PASS; no setup feature calls a protected endpoint before `helperApi.ready`.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper
git commit -m "fix(security): require owner access for helper management"
```

### Task 4: Shared Scoped Widget Pairing Service

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/WidgetPairingService.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/WidgetAuthorizationFilter.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security/LocalAccessContracts.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookAccessService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookIntegration.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookAccountState.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/WidgetPairingServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OutlookSecurityTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OutlookEndpointTests.cs`

**Interfaces:**
- Produces: `WidgetScope` enum (`Planner`, `Outlook`), `CreateAsync(WidgetScope, PairingRequest, OutlookAccountLease, ct)`, `PollAsync`, `ApproveAsync`, `RevokeAsync`, and `AuthenticateAsync(WidgetScope, credential, ct)`.
- Produces headers: `X-Microsoft-Widgets-Owner` and `X-Microsoft-Widgets-Credential`.
- Consumed by: Planner/Outlook endpoint filters and both widget clients in Task 5.
- Migration note: this task deliberately uses the existing `OutlookAccountLease` so its commit builds independently; Task 6 renames and strengthens that lease as shared `AccountLease` once immutable MSAL identity is available.

- [ ] **Step 1: Write failing scope, lifecycle, and concurrency tests**

Port existing Outlook pairing tests to the shared service and add Planner scope. Cover wrong-scope rejection, account-generation changes during poll/approval/authentication, duplicate instance replacement only within the same scope/account, 32 pending/100 paired limits, one-second poll throttle, five-minute expiry, hashed persistence, owner-only approval, invalid Origin with valid credential, and null/missing Origin with valid/invalid credential.

```csharp
var planner = await PairAsync(WidgetScope.Planner, "same-instance");
var outlook = await PairAsync(WidgetScope.Outlook, "same-instance");
Assert.NotNull(await service.AuthenticateAsync(WidgetScope.Planner, planner, ct));
Assert.Null(await service.AuthenticateAsync(WidgetScope.Outlook, planner, ct));
Assert.NotNull(await service.AuthenticateAsync(WidgetScope.Outlook, outlook, ct));
```

- [ ] **Step 2: Run pairing/security tests and confirm failure**

Run: `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftWidgets.Helper.Tests.csproj --filter "FullyQualifiedName~WidgetPairingServiceTests|FullyQualifiedName~OutlookSecurityTests|FullyQualifiedName~OutlookEndpointTests"`

Expected: FAIL because pairing is Outlook-specific and same-origin headers can still mint a setup session.

- [ ] **Step 3: Implement shared scoped pairing and migrate Outlook endpoints**

Move pairing records to a versioned `widget-credentials` store keyed by immutable account lease and scope. Delete/migrate-as-invalid `outlook-credentials` on first load. Replace `/api/outlook/session` with owner-session authorization and shared pairing endpoints under `/api/local-access/pairings`; keep integration-specific compatibility endpoints only if they call the same service and filters. `WidgetAuthorizationFilter` accepts owner sessions for browser previews or the exact requested widget scope for native calls, then binds the account lease through response serialization.

```csharp
public sealed record StoredWidgetCredential(
    string CredentialId, WidgetScope Scope, string InstanceId,
    string AccountKey, string Hash, DateTimeOffset CreatedAt);
```

- [ ] **Step 4: Run pairing, Outlook, and storage tests**

Run focused tests and all tests matching `Outlook|Storage|Contract`. Expected: PASS; old credential records are not accepted.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "fix(security): scope widget pairing credentials"
```

### Task 5: Pair and Authorize Planner and Outlook Widgets

**Files:**
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookIntegration.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/WidgetCorsExtensions.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/WidgetOriginPolicy.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/wwwroot/outlook-setup.js`
- Modify: `planner-edge-widget/widget/src/api.js`
- Modify: `planner-edge-widget/widget/src/app.js`
- Modify: `outlook-edge-widget/widget/src/api.js`
- Modify: `outlook-edge-widget/widget/src/app.js`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerAuthorizationTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/WidgetOriginPolicyTests.cs`
- Test: `planner-edge-widget/widget/tests/connection.test.mjs`
- Test: `planner-edge-widget/widget/tests/startup.test.mjs`
- Test: `outlook-edge-widget/tests/security.test.mjs`

**Interfaces:**
- Consumes: shared pairing/authorization from Task 4.
- Produces: both widget clients persist one credential per iCUE `uniqueId`, send `X-Microsoft-Widgets-Credential`, and expose the same short-code pairing state.

- [ ] **Step 1: Write failing endpoint matrix and widget pairing tests**

For every Planner GET/PUT/POST/DELETE endpoint on both route families, test anonymous, forged localhost Origin, null Origin, missing Origin, valid Planner credential, valid Outlook credential, revoked credential, and expired/account-changed credential. Assert preflight only grants transport and never endpoint access. Frontend tests cover credential storage by instance, no credential in URL/log/DOM, pairing retry/expiry, and preview owner-session mode.

```javascript
api.credential='planner-secret';
await api.get('/display');
assert.equal(fetch.calls[0].options.headers['X-Microsoft-Widgets-Credential'],'planner-secret');
assert.equal(fetch.calls[0].url.includes('planner-secret'),false);
```

- [ ] **Step 2: Run helper and widget tests and confirm failure**

Run focused .NET filters, `npm test` in `planner-edge-widget/widget`, and `npm test` in `outlook-edge-widget`. Expected: Planner anonymous probes still succeed and Planner has no pairing UI.

- [ ] **Step 3: Apply scoped authorization and implement Planner pairing UX**

Apply `WidgetAuthorizationFilter(Planner)` to both Planner route groups and `WidgetAuthorizationFilter(Outlook)` to Outlook data/join routes. Keep pairing create/poll as the only unpaired endpoints and require valid Host plus accepted native transport. Narrow CORS so it sets headers only after route auth has identified an allowed native request/preflight policy. Add Planner pairing state modeled on Outlook, but with Planner labels and no Outlook permissions. Update setup to display/approve/revoke both scopes.

- [ ] **Step 4: Run all helper and widget tests**

Run `dotnet test microsoft-widgets-helper/MicrosoftWidgets.Helper.slnx`, Planner `npm test`, Outlook `npm test`, and both setup browser test files.

Expected: PASS; audit probes for anonymous Planner mutation now return 401/403 without invoking fake Graph mutations.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper planner-edge-widget outlook-edge-widget
git commit -m "fix(planner): require paired widget access"
```

### Task 6: Immutable Microsoft Identity and Account Leases

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/MicrosoftAccountIdentity.cs`
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/MicrosoftAccountState.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/MicrosoftAuthService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth/IGraphTokenProvider.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookAccountState.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/OutlookTokenProvider.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAccountStateTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/MicrosoftAuthAcquisitionTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OutlookSecurityTests.cs`

**Interfaces:**
- Produces: `MicrosoftAccountIdentity(HomeAccountId, TenantId, ClientId, Username)` and `AccountLease(Key, Generation)`.
- Produces: `IMicrosoftAuthService.GetAccountIdentityAsync(ct)` based on selected MSAL `IAccount.HomeAccountId.Identifier` and authenticated tenant ID.
- Replaces: Outlook-only account lease with shared `MicrosoftAccountState` events `Invalidated` and `SourceInvalidated`.

- [ ] **Step 1: Write failing identity and transition tests**

Use fake MSAL accounts to prove equal usernames with different home-account or tenant IDs get distinct keys; username changes for the same immutable identity preserve the key; token refresh preserves generation; sign-out/config change/principal replacement increments generation and clears credentials. Add a race where an account changes after credential validation but before response publication and assert publication fails.

```csharp
var first = Identity("home-a", "tenant-a", "client", "same@example.com");
var second = Identity("home-b", "tenant-a", "client", "same@example.com");
Assert.NotEqual(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(second));
Assert.Equal(MicrosoftAccountState.Key(first), MicrosoftAccountState.Key(first with { Username = "renamed@example.com" }));
```

- [ ] **Step 2: Run identity tests and confirm failure**

Run the three focused classes. Expected: FAIL because account keys currently hash configured tenant and mutable username.

- [ ] **Step 3: Implement immutable identity and shared leases**

Capture selected MSAL account plus actual tenant from `AuthenticationResult.TenantId` and persist only the immutable IDs needed to recognize the current account. Refactor Outlook and local pairing services onto shared leases. Configuration and sign-out transition through `MicrosoftAccountState`, which owns generation changes and invalidation events. Delete username-derived credential records during migration.

- [ ] **Step 4: Run auth, Outlook, pairing, and full helper tests**

Run focused tests, all `MicrosoftAuth|Outlook|WidgetPairing` tests, then the full helper suite. Expected: PASS with no username-based cache keys remaining (`rg "AccountHint.ToLower|configuration.Tenant.*AccountHint"` returns no production matches).

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Auth microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Security microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Program.cs microsoft-widgets-helper/tests
git commit -m "fix(auth): bind local access to immutable identity"
```

### Task 7: Planner Account-Bound Cache Lifecycle

**Files:**
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerDataLifecycle.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Storage/PlannerSettingsStore.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerCoordinator.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerBoardService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskDetailsService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/PlannerIntegration.cs`
- Modify: `planner-edge-widget/widget/src/state.js`
- Modify: `planner-edge-widget/widget/src/app.js`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerDataLifecycleTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/PlannerIntegrationTests.cs`
- Test: `planner-edge-widget/widget/tests/state.test.mjs`
- Test: `planner-edge-widget/widget/tests/startup.test.mjs`

**Interfaces:**
- Consumes: `MicrosoftAccountState` from Task 6.
- Produces: `PlannerSnapshot(AccountKey, SavedAt, Display)` with a 24-hour maximum age and `PlannerDataLifecycle.PurgeAsync`.
- Produces frontend generation APIs `beginRequest()`, `isCurrent(ticket)`, and `clearAuthorization()`.

- [ ] **Step 1: Write failing cache and late-response tests**

Warm selected-plan, board list, display, details, member, and preference caches. Assert sign-out/config/account change and Graph 401/403 purge them from memory and disk; cached endpoint returns 204/401, not prior work. Assert a network exception can return a current-account snapshot younger than 24 hours, while old or wrong-account snapshots fail. Frontend tests hold board/details/chat responses, clear authorization, release responses, and assert no old content/dialog/action returns.

```csharp
await lifecycle.WarmAllAsync();
graph.FailWith(HttpStatusCode.Forbidden);
await Assert.ThrowsAsync<GraphApiException>(() => coordinator.GetDisplayAsync(ct));
Assert.Null(await store.LoadCachedDisplayAsync(currentLease, ct));
```

- [ ] **Step 2: Run focused helper and Planner tests and confirm failure**

Run Planner lifecycle/integration filters and Planner `npm test`. Expected: FAIL because authorization errors return old snapshots and frontend tests currently preserve signed-out boards.

- [ ] **Step 3: Implement account-bound snapshots and purge generation**

Version Planner persistence records with account key and timestamp; treat unversioned legacy snapshots as expired. Subscribe `PlannerDataLifecycle` to shared account invalidation, clear known `IMemoryCache` keys via generation-prefixed keys, and wipe work-data files while retaining non-sensitive UI preferences only when safely account-independent. In `PlannerCoordinator`, catch only transient `HttpRequestException` and eligible 429/5xx Graph failures for fallback; purge/rethrow 401/403. Add `no-store` to sensitive routes. Frontend authorization clear aborts controllers, advances generation, and clears all work state.

- [ ] **Step 4: Run Planner, helper, and hostile cache probes**

Run focused tests, full helper suite, Planner `npm test`, and the retained frontend audit probe after converting it to positive assertions. Expected: PASS; 401/403 never restores saved content.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Storage planner-edge-widget/widget microsoft-widgets-helper/tests
git commit -m "fix(planner): purge data when authorization changes"
```

### Task 8: Outlook Authorization and Source Race Fixes

**Files:**
- Modify: `outlook-edge-widget/widget/src/state.js`
- Modify: `outlook-edge-widget/widget/src/app.js`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/CalendarViewService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook/CalendarCatalogService.cs`
- Test: `outlook-edge-widget/tests/state.test.mjs`
- Test: `outlook-edge-widget/tests/security.test.mjs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OutlookConcurrencyTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/OutlookSecurityTests.cs`

**Interfaces:**
- Consumes: shared account generation from Task 6.
- Produces: `RefreshState.clearAuthorization()` that increments generation and invalidates all tickets; source reads carry a version captured before resolution through publication.

- [ ] **Step 1: Write failing deterministic race tests**

Frontend: begin cache/live requests, inject 401/403/account-change, release old cache and live successes, and assert `seed`/`commit` return false with no event/detail/Join state. Backend: block immediately after source version capture, resolve source, remove source at each interleaving point, resume, and assert response/cache omit it for 20 repetitions.

```javascript
const ticket=state.begin();
state.fail(ticket,{status:401});
assert.equal(state.seed(ticket,[oldEvent]),false);
assert.deepEqual(state.events,[]);
```

- [ ] **Step 2: Run race tests and confirm failure**

Run Outlook npm tests and the two focused .NET classes. Expected: SEC-05 and SEC-06 reproductions FAIL against current behavior.

- [ ] **Step 3: Implement generation invalidation and source-version capture**

Make authorization clear increment the generation before clearing data; abort active controllers in the app. In `ReadSourceAsync`, capture source version while synchronized before calling catalog resolution, verify it before flight registration, pass that exact version to fetch/publication, and reject/cancel when removal changes it. Ensure removed-source failures cannot be converted to stale fallback.

- [ ] **Step 4: Stress and regression test**

Run Outlook npm tests, focused .NET tests repeatedly (`--blame-hang-timeout 2m` where supported), then all Outlook helper tests. Expected: PASS for at least 100 controlled source-removal interleavings.

- [ ] **Step 5: Commit**

```powershell
git add outlook-edge-widget/widget/src outlook-edge-widget/tests microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Outlook microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests
git commit -m "fix(outlook): reject stale authorization responses"
```

### Task 9: Planner Notes, Task Scope, and Graph Pagination

**Files:**
- Modify: `planner-edge-widget/widget/src/app.js`
- Modify: `planner-edge-widget/widget/tests/interactions.test.mjs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskCompletionService.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner/TaskDetailsService.cs`
- Modify: remaining Planner task services that duplicate selected-plan checks
- Create: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/GraphNextLinkPolicy.cs`
- Modify: `microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph/PlannerGraphClient.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskCompletionServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/TaskDetailsServiceTests.cs`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/GraphClientTests.cs`

**Interfaces:**
- Consumes: `SelectedPlanTaskService.GetAsync(taskId, ct)` as the single task scope guard.
- Produces: `GraphNextLinkPolicy.RequireAllowed(Uri current, string candidate, ISet<string> visited): Uri` plus page/record limits used by all Planner pagination loops.

- [ ] **Step 1: Write failing integrity and pagination tests**

Save notes twice without editing and assert both PUT bodies contain the original text. Test details, completion, and every task mutation with a task from another plan and assert Graph mutation/details calls do not proceed. Feed pagination links with wrong host, HTTP scheme, wrong API version/path, credentials, fragments, loops, over 100 pages, and over the configured record cap; assert rejection without sending a bearer token to that destination.

```javascript
await saveNotes();
await saveNotes();
assert.deepEqual(api.noteBodies,['keep this note','keep this note']);
```

- [ ] **Step 2: Run focused tests and confirm failure**

Run Planner interaction tests and .NET `TaskCompletion|TaskDetails|GraphClient` filters. Expected: second notes body is empty, cross-plan completion/details proceed, and foreign next links are accepted.

- [ ] **Step 3: Implement minimal integrity fixes**

After notes save, retain the normalized saved text in dialog state rather than setting draft to null. Inject `SelectedPlanTaskService` into details/completion and consolidate duplicated guards where behavior is identical. Validate all Planner next links against HTTPS `graph.microsoft.com`, allowed `/v1.0/` route families, and a visited set; cap each operation at 100 pages and a service-appropriate total record count. Configure Planner `HttpClientHandler.AllowAutoRedirect = false`.

- [ ] **Step 4: Run Planner and helper regression suites**

Run focused tests, all Planner helper tests, Planner `npm test`, and full helper tests. Expected: PASS and no service directly compares task plan IDs outside the shared guard unless its semantics require board-level validation.

- [ ] **Step 5: Commit**

```powershell
git add planner-edge-widget microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Planner microsoft-widgets-helper/src/MicrosoftWidgets.Helper/Graph microsoft-widgets-helper/tests
git commit -m "fix(planner): preserve notes and enforce task scope"
```

### Task 10: Unelevated Installer and Clean Release Staging

**Files:**
- Modify: `microsoft-widgets-helper/installer/MicrosoftWidgets.iss`
- Modify: `microsoft-widgets-helper/scripts/publish.ps1`
- Modify: `outlook-edge-widget/scripts/build.mjs`
- Modify: `scripts/package-outlook.ps1`
- Modify: `planner-edge-widget/scripts/package.ps1`
- Modify: `scripts/build-release.ps1`
- Create: `scripts/verify-release-inventory.ps1`
- Test: `microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests/HelperPackageTests.cs`
- Create: `microsoft-widgets-helper/tests/release-security.test.ps1`

**Interfaces:**
- Produces: `verify-release-inventory.ps1 -Stage <path> -Manifest <json>` which rejects missing, unexpected, escaping, and reparse-point entries.
- Consumes: same-user `--stop` named pipe from Task 3 before installer launch/update, never an elevated installed executable.

- [ ] **Step 1: Write failing installer-source and sentinel tests**

Static installer tests assert an elevated administrative token is rejected in `[Code]`, no `[UninstallRun]` or `PrepareToInstall` executes `{app}\MicrosoftWidgets.Helper.exe`, and per-user paths remain. Seed sentinel files into Outlook `dist`, Planner package output, helper publish output, and release stage; run each build and assert sentinels are absent or the inventory verifier fails. Test path-boundary checks against a sibling directory and junction/reparse point.

```powershell
Set-Content -LiteralPath $sentinel -Value 'must-not-ship'
& $build
if (Get-ChildItem $artifact -Recurse | Select-String 'must-not-ship') { throw 'Stale output entered release.' }
```

- [ ] **Step 2: Run package tests and confirm failure**

Run HelperPackage tests and `pwsh microsoft-widgets-helper/tests/release-security.test.ps1`. Expected: FAIL because stale Outlook/helper stage files survive and installer runs the installed helper.

- [ ] **Step 3: Enforce unelevated install and fresh inventories**

In Inno `[Code]`, abort early when `IsAdminLoggedOn`/administrative install mode indicates elevation, with clear text to rerun normally. Remove installed-binary execution from setup/uninstall. Ensure helper shutdown occurs through the same-user pipe before update setup is launched. Add path-resolved cleanup helpers that verify targets remain under known repo `dist` roots, then recreate stages. Clean Outlook `dist` before esbuild. Define expected relative file manifests for each widget/helper stage and reject extras, missing files, links, or paths outside stage.

- [ ] **Step 4: Build twice with sentinels and verify artifacts**

Run the package tests, both widget package scripts, helper publish, and root release build with the installed Inno compiler. Seed new sentinels between runs. Expected: both runs pass inventory checks and no sentinel appears in `.icuewidget`, portable ZIP, or installer staging.

- [ ] **Step 5: Commit**

```powershell
git add microsoft-widgets-helper/installer microsoft-widgets-helper/scripts microsoft-widgets-helper/tests outlook-edge-widget/scripts planner-edge-widget/scripts scripts
git commit -m "fix(release): build from trusted clean staging"
```

### Task 11: Security Automation, Documentation, and Re-Audit

**Files:**
- Create: `.github/workflows/security.yml`
- Create: `.github/dependabot.yml`
- Modify: `microsoft-widgets-helper/scripts/verify.ps1`
- Modify: `planner-edge-widget/scripts/verify.ps1`
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `microsoft-widgets-helper/README.md`
- Modify: `docs/security/2026-09-21-audit.md`
- Create: `docs/security/2026-09-22-remediation.md`
- Promote/adapt: `out/security-audit/SecurityAuditProbeTests.cs` into the helper test project
- Promote/adapt: `out/security-audit/frontend-probes.mjs` into widget tests

**Interfaces:**
- Consumes: all prior tasks.
- Produces: a least-privilege CI workflow and a remediation matrix mapping SEC-01 through SEC-09 plus additional defects to tests and commits.

- [ ] **Step 1: Add failing verification hooks for security regressions**

Promote hostile probes as positive security tests: anonymous Planner operations must fail, forged Outlook setup bootstrap must fail, attacker Host must fail, late cache responses must be discarded, removed sources must not republish, notes must persist, and stale stages must be rejected. Add a root verification invocation that fails until all promoted tests are wired into normal suites.

- [ ] **Step 2: Run root verification and confirm missing automation**

Run `microsoft-widgets-helper/scripts/verify.ps1`, Planner verify, Outlook `npm test`, and release-security tests. Expected before wiring: at least one promoted-test discovery/invocation assertion FAILS.

- [ ] **Step 3: Add CI and remediation documentation**

Create a Windows workflow with `contents: read`, locked Node installs (`npm ci`), fresh NuGet restore/audit, full .NET/JS/security tests, and clean package inventory verification without publishing. Configure Dependabot for both npm roots, NuGet, and GitHub Actions. Document tray-only setup bootstrap, one-time post-upgrade widget re-pairing, direct-navigation behavior, and unelevated installation. Update the audit with a clearly dated remediation appendix instead of rewriting original evidence.

```yaml
permissions:
  contents: read
jobs:
  verify:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet test microsoft-widgets-helper/MicrosoftWidgets.Helper.slnx
```

- [ ] **Step 4: Run complete verification and inspect the diff**

Run:

```powershell
dotnet test microsoft-widgets-helper/MicrosoftWidgets.Helper.slnx
Push-Location planner-edge-widget/widget; npm test; Pop-Location
Push-Location outlook-edge-widget; npm test; Pop-Location
node microsoft-widgets-helper/tests/setup-browser.mjs
node microsoft-widgets-helper/tests/outlook-setup.test.mjs
node microsoft-widgets-helper/tests/update-ui.test.mjs
pwsh microsoft-widgets-helper/tests/release-security.test.ps1
git diff --check
```

Also rerun dependency audits and the tracked-history secret-pattern scan described in the audit. Expected: all tests pass, zero known dependency advisories, no tracked secret patterns, and no unaccounted audit finding.

- [ ] **Step 5: Manually verify installed behavior**

Install the local package without elevation. Confirm tray click opens authenticated setup, direct localhost navigation exposes no setup data, Planner and Outlook each pair once, both render and mutate only their permitted data, revoked pairings fail, sign-out clears both screens, update check works, `--stop` stops only the same-user helper, and uninstall does not execute a substituted installed helper. Record results in `docs/security/2026-09-22-remediation.md` without including work data or secrets.

- [ ] **Step 6: Commit**

```powershell
git add .github README.md docs microsoft-widgets-helper planner-edge-widget outlook-edge-widget
git commit -m "chore(security): automate hardened release checks"
```

### Task 12: Final Branch Review and Release Readiness

**Files:**
- Modify only files required by review findings.
- Finalize: `docs/security/2026-09-22-remediation.md`

**Interfaces:**
- Consumes: the complete remediation branch and all verification evidence.
- Produces: a review-ready branch with no unresolved high/medium audit item; no release publication.

- [ ] **Step 1: Run a whole-branch security review**

Compare the branch against the approved spec and audited commit `46770b1`. Review endpoint inventory, all authorization metadata, static-file ordering, account transitions, cancellation/publication gates, credential persistence, installer code, and archive inventory. Search for unfiltered route mappings and raw setup fetches:

```powershell
rg -n "Map(Get|Post|Put|Delete)|fetch\(" microsoft-widgets-helper/src planner-edge-widget/widget/src outlook-edge-widget/widget/src
```

Every sensitive route must map to owner or exact widget scope; every setup fetch must use the owner-aware wrapper.

- [ ] **Step 2: Fix review findings test-first**

For each actionable finding, add the smallest failing regression test, run it to confirm failure, implement the correction, and rerun its owning suite. Use a Conventional Commit matching the affected scope, for example `fix(security): reject stale owner lease`.

- [ ] **Step 3: Run final clean verification**

From a clean checkout/stage, run every command in Task 11 Step 4 plus root release construction. Confirm `git status --short` contains only intentional documentation updates and generated outputs remain ignored.

- [ ] **Step 4: Complete remediation matrix**

For SEC-01 through SEC-09 and each additional defect, record implementation commit, permanent test name, verification result, residual limitation, and any manual evidence. Mark SEC-07 as resolved only if distinct immutable identities and actual tenant behavior are covered by tests.

- [ ] **Step 5: Commit final evidence**

```powershell
git add docs/security/2026-09-22-remediation.md
git commit -m "docs(security): record remediation verification"
```
