# Microsoft Widgets Security Remediation Design

Status: approved in chat on 2026-09-22. This specification turns the findings in `docs/security/2026-09-21-audit.md` into one coherent hardening release.

## Goal

Close every confirmed security gap and related integrity defect from the repository audit without making setup or daily widget use more technical. The helper must trust only its owning Windows user and explicitly paired widget instances, isolate Microsoft work data by immutable account identity, fail closed when authorization changes, and package only reviewed build output.

This release does not add remote administration, multi-account support, an elevated machine-wide installation, or new Microsoft Graph permissions. Existing Planner and Outlook product behavior should remain intact except where insecure offline retention, anonymous access, or inconsistent task scope is intentionally removed.

## Security Boundaries

The helper has three caller classes:

1. **Owner setup:** the browser page opened from the helper tray icon or first-run process. It may configure Microsoft authentication, approve/revoke widget pairings, manage Outlook sources, check/install updates, download bundled widgets, and stop the helper.
2. **Paired Planner widget:** a native widget instance with a Planner-scoped credential. It may use Planner routes, including task mutations, but may not use Outlook, setup, update, configuration, or host-management routes.
3. **Paired Outlook widget:** a native widget instance with an Outlook-scoped credential. It may read Outlook data and launch a known meeting, but may not use Planner, setup, update, configuration, or host-management routes.

Loopback location, `Origin`, `Host`, and fetch-metadata headers are defense-in-depth signals, not caller identity. Every sensitive request needs an authenticated capability belonging to one of these classes. The public surface is limited to a minimal health response, static bootstrap shell, and pairing initiation/polling needed by an unpaired native widget.

## Unified Local Access Service

Introduce one helper-owned access service with three responsibilities: owner bootstrap/session issuance, scoped widget pairing, and capability validation. Planner and Outlook route filters consume this service instead of implementing separate trust models.

### Owner Bootstrap

- Each request to open setup from the tray or first-run path creates a cryptographically random, single-use bootstrap token with a short expiration.
- The token is placed in the URL fragment, not the query string. Fragments are not sent in the HTTP request, server logs, or referrer headers.
- Setup JavaScript reads the fragment, sends it once in a dedicated bootstrap request header, immediately removes it from browser history, and stores the returned owner session only in `sessionStorage`.
- A bootstrap token is invalidated after its first exchange, expiration, account transition, or helper restart. Failed exchanges are rate-limited and return no account information.
- Navigating directly to `http://localhost:8787` serves a safe shell explaining that setup must be opened from the tray. It does not create a session or expose configuration, account, update, pairing, or installation state.
- Owner sessions are random, hashed in helper memory, time-bounded, revocable, and invalidated on helper restart. They are accepted only from the exact helper origin in addition to the session credential.
- Browser requests use a dedicated header rather than cookies, avoiding ambient browser authority and ordinary cross-site request forgery. Setup responses use `Cache-Control: no-store`.

The command-line `--stop` path does not call an anonymous HTTP endpoint. It signals the existing same-user helper through an owner-only Windows primitive. The preferred implementation is a per-user named pipe whose ACL is restricted to the current user SID. Tray shutdown remains in-process. `/host/stop`, if retained for setup UI compatibility, requires an owner session.

### Widget Pairing

- Pairing uses the established request-secret plus short-code flow, generalized for an explicit integration scope (`planner` or `outlook`).
- Each widget instance generates an unguessable request secret and stable instance identifier, displays a short code, and polls with the request secret.
- The owner setup page shows pending requests with widget type and instance identity. Only an owner session may approve, list, or revoke pairings.
- Approval returns a credential only to the requester that proves possession of the request secret. The helper persists only a credential hash, integration scope, instance ID, immutable account key, creation time, and credential ID.
- Credentials are sent in a dedicated authorization header, never in URLs or generated packages. They are independently revocable and cannot cross integration scopes.
- Pairing requests expire after five minutes, are rate-limited, and are cleared on account/configuration transitions.
- Existing Outlook credentials and all formerly anonymous Planner access are migrated by requiring a fresh pairing. There is no compatibility bypass for old unauthenticated clients.

Browser previews opened inside authenticated setup use the owner session and an explicit integration scope. They do not silently mint durable widget credentials.

## HTTP and Browser Protections

A global middleware runs before default files, static files, CORS, and API routing:

- Accept only HTTP requests whose destination is loopback and whose `Host` is `localhost`, `127.0.0.1`, or `[::1]` on the helper's actual listening port.
- Reject attacker-controlled hostnames, mismatched ports, forwarded-host attempts, and non-loopback connections.
- Keep the server bound to loopback only.
- Apply an explicit response policy: `X-Content-Type-Options: nosniff`, a restrictive setup Content Security Policy, `frame-ancestors 'none'`/`X-Frame-Options: DENY` for setup, and `Cache-Control: no-store` for API responses containing account or work data.
- Preserve widget embedding requirements on widget preview/package content rather than applying setup anti-framing headers indiscriminately.
- Allow native null/file-origin transport only after a valid scoped widget credential is presented. CORS does not grant access by itself.
- Remove the state-changing GET sign-in route. All mutations use non-GET methods and their required capability.
- Cap request bodies consistently for both integrations and reject unexpected content types on JSON mutation endpoints.

Legacy Planner route aliases remain temporarily available but receive the same Planner capability filter as `/api/planner/*`. Tests must cover both route families. New documentation and clients use only the namespaced routes.

## Account Identity and Data Lifecycle

Expose a shared immutable authenticated identity from the Microsoft auth service. The account key is derived from the MSAL home-account identifier plus the actual authenticated tenant identifier and application client ID. Usernames remain display data and are never cache or authorization keys.

All owner sessions, widget credentials, Planner selection/data, Outlook sources/data, in-memory caches, and in-flight work carry an account lease. A same-account token refresh preserves the lease; sign-out, configuration change, tenant change, or principal replacement advances the generation and invalidates the previous lease.

### Planner

- Bind selected-plan settings, display snapshots, task details, members, and preferences to the immutable account key.
- Give persisted display snapshots a creation time and bounded offline lifetime. Protect persisted work-data records with Windows current-user data protection where practical; migration treats unbound legacy work data as expired.
- Clear persisted and in-memory Planner work data on sign-out, configuration change, account replacement, Graph 401, or Graph 403.
- Never turn 401/403 into offline fallback. Transient network and service errors may use only a current-account, unexpired snapshot.
- The Planner frontend advances an authorization generation and aborts outstanding requests when access is lost. It clears board, detail, notes, chat, members, dialogs, and actions before rendering signed-out state. A late response from an older generation is ignored.

### Outlook

- Clearing authorization advances the widget refresh generation and aborts outstanding fetches, so late cache or live responses cannot repopulate events, details, or Join actions.
- Calendar source generation is captured before source resolution, checked before request registration, and checked again before response publication. Removing a source invalidates both active work and cached publication for that generation.
- Existing Outlook pairing and cache identity moves to the immutable account key. Username-derived credentials are invalidated during migration.

## Planner Integrity Hardening

- Preserve the current notes value after a successful save. A second save without edits submits the displayed description instead of an empty string.
- Route task completion and task details through `SelectedPlanTaskService`, matching every other task mutation. A paired Planner credential authorizes the selected board workflow, not arbitrary tasks available to the Microsoft account.
- Centralize task-scope validation so future task endpoints cannot omit it accidentally.
- Follow Microsoft Graph pagination only for validated HTTPS `graph.microsoft.com` links under the expected API version/path family. Disable automatic cross-host redirects, cap pages and total records, and reject malformed/repeated next links.

## Installer and Build Hardening

The product remains a per-user installation:

- Setup and uninstall refuse to continue while running elevated. User-facing text explains that Microsoft Widgets should be installed normally, without "Run as administrator."
- Installer script must not execute the existing user-writable helper binary during elevated setup or uninstall.
- Normal same-user shutdown uses the owner-only named pipe before setup starts replacement. If the helper cannot be stopped safely, setup asks the user to close it or schedules replacement without executing untrusted installed code.
- Update installation preserves this unelevated boundary and verifies the release digest immediately before launch.

Every release build starts from newly created, path-validated staging directories. Widget `dist` directories and helper publish staging are cleaned within verified repository boundaries before output. Packaging copies an explicit inventory of expected files and fails on missing or unexpected files. Broad recursive globs are permitted only inside a freshly created validated stage whose contents are checked before archive/installer creation.

Seeded sentinel files in old output directories must either be excluded or cause verification failure. Locked dependencies and the existing SHA-256 release manifest remain required.

## Operational Hardening

- Add routine GitHub Actions checks for .NET tests, both widget test suites, dependency auditing, secret-pattern scanning, and clean-package inventory validation. Workflows use least permissions and no long-lived repository secrets.
- Keep signing as a documented future enhancement; this remediation does not claim publisher authenticity beyond GitHub release controls and SHA-256 digests.
- Do not log bootstrap/session/pairing credentials, Microsoft tokens, notes, chat, event details, or Graph response bodies.
- Security errors use stable, non-sensitive codes so both widgets can distinguish pairing required, signed out, forbidden, stale, and transient offline states.

## Migration and User Experience

After upgrading, setup opens normally from the tray. Existing users sign in only if their Microsoft session is no longer valid, but each installed widget instance must complete the short pairing flow once because formerly anonymous Planner access and username-bound Outlook credentials cannot be retained safely.

The helper page groups pending Planner and Outlook pairings together and clearly labels the requesting widget. Direct browser visits provide a one-step instruction to click the tray icon. Failed authorization clears work data immediately and offers reconnect/pairing actions without exposing prior content.

No additional administrator consent is expected because the remediation changes local authorization and identity handling, not Graph permission scopes.

## Verification

Implementation follows test-driven slices. Each audit finding gets a regression test that fails against the audited commit and passes after remediation.

### Access-control acceptance

- Anonymous Planner reads and writes fail on both route families for null origin, missing origin, arbitrary localhost origin, forged same-origin headers, and native HTTP clients.
- A valid Planner credential succeeds only on Planner routes. Outlook credentials and owner sessions cannot be substituted across scopes.
- A second local client cannot mint an owner session by forging Host, Origin, or fetch metadata. Single-use bootstrap replay and expiry fail.
- Configuration, auth, pairing approval/revocation, updates, downloads, and stop require the owner capability.
- Invalid Host and port values fail before static or API content is served.

### Lifecycle and race acceptance

- Warm every Planner cache, then sign out, change configuration/account, or inject Graph 401/403; no prior board, details, chat, members, or actions remain in helper responses or widget state.
- A current-account transient network failure still uses a fresh bounded snapshot.
- Release delayed Outlook cache and live responses after authorization loss; neither can restore content.
- Pause an Outlook source read, remove the source, resume it, and verify neither the response nor cached endpoint contains that source.
- Distinct immutable principals with the same username receive different account keys; token refresh for one principal preserves its key.

### Integrity and release acceptance

- Saving unchanged notes repeatedly preserves them.
- Details, completion, and every task mutation reject a task outside the selected plan.
- Pagination rejects foreign hosts, wrong API paths, loops, excessive pages, and excessive records.
- Elevated setup/uninstall refuses before running installed code; ordinary per-user install, update, stop, and uninstall still work.
- Seed unrelated files in every old output directory, produce a release, and confirm the final inventory contains only expected files.

Run the complete helper, Planner widget, Outlook widget, setup-page, installer, and packaging suites. Re-run focused hostile-request probes against the assembled helper and manually verify tray-opened setup plus native Planner and Outlook pairing in iCUE before release.

## Delivery Sequence

1. Add the shared access service, owner bootstrap, same-user shutdown channel, global Host policy, and route capability filters.
2. Pair Planner and migrate Outlook to shared scoped credentials; update setup and widget clients.
3. Introduce immutable account leases and correct Planner/Outlook cache lifecycle and races.
4. Fix Planner notes, task scope, and pagination hardening.
5. Harden installer, clean packaging, and CI security checks.
6. Run a focused re-audit, update the audit report with remediation status, and prepare but do not publish a release until explicitly requested.

Use Conventional Commits for each independently verified slice. No security control is considered complete until its negative tests and the full relevant regression suite pass.
