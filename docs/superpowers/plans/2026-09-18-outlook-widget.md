# Outlook Widget Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver the approved read-only Outlook calendar widget through the shared helper.

**Architecture:** Isolated Outlook helper services, with a separate bundled FullCalendar widget. Account-bound pairing protects new localhost routes. Setup handles a single permission bundle, native-instance approval, and local calendar references.

**Tech Stack:** Existing .NET 10 Windows host and MSAL; vanilla JavaScript, FullCalendar Standard, Node tests and browser verification; existing iCUE packaging CLI and Inno installer.

**Spec:** `docs/superpowers/specs/2026-09-18-outlook-widget-design.md`

## Global Constraints

- Outlook Graph traffic is GET-only. Local POST may read data or launch a user-selected meeting.
- Delegated scopes: User.Read, Calendars.Read.Shared, MailboxSettings.Read, Group.Read.All. Request the complete bundle in one interactive flow.
- Preserve Planner routes/settings and the pending logo edits. Do not publish, merge, install, or send a consent request automatically.
- Bundle dependencies locally and preserve licenses. Keep exact versions in the lockfile.
- Use the existing isolated implementation worktree. Conventional Commits, with explicitly staged task files only.
- Native-widget acceptance requires a user/device test; do not report browser tests as native tests.

## Shared Wire Contract

All JSON uses camelCase. Backend route prefix `/api/outlook`. The browser preview at `/outlook/` uses a same-origin session obtained through `GET /api/outlook/session`; the response is a random CSRF/bearer token accessible only to the exact helper origin. Mutating setup requests carry `X-Outlook-Session`. Native requests carry `Authorization: Bearer <paired credential>`. Pairing bootstrap routes accept no calendar data. Cross-origin session/approval/config routes return 403.

```js
// GET /calendars -> CalendarDescriptor[]
// { key, name, owner, kind: 'personal'|'shared'|'group', color, canViewPrivateItems }
// GET /preferences -> { workingHours: { daysOfWeek, startTime, endTime, timeZone }|null,
//                      pcTimeZone, timeZones: string[] }
// POST /view { calendarKeys: string[], start: ISO_WITH_OFFSET, end: ISO_WITH_OFFSET }
// -> { events: EventSummary[], sources: SourceStatus[] }
// EventSummary: { reference, calendarKey, title, start, end, isAllDay, isPrivate, isCancelled }
// SourceStatus: { calendarKey, fetchedAt: ISO|null, stale: boolean, error: {code,message}|null }
// POST /event-details { calendarKey, reference }
// -> { reference, calendarKey, title, start, end, isAllDay, isPrivate,
//      location, organizer, attendees: string[], description, joinUrl: string|null }
// POST /join { calendarKey, reference } -> 204
// POST /sources { ownerEmail } -> CalendarDescriptor
// POST /sources/{key}/remove -> 204
// POST /pairings { instanceId, requestSecret } -> { id, code, expiresAt }
// POST /pairings/{id}/poll { requestSecret } -> { status:'pending'|'approved', credential?:string }
// GET /pairings -> [{id, code, instanceId, expiresAt}] (setup only)
// POST /pairings/{id}/approve -> 204 (setup only)
// POST /pairings/revoke { credentialId } -> 204 (setup only)
// GET /paired -> [{ credentialId, instanceId }] (setup only)
// GET /status -> { configured, signedIn, ready, error?:{code,message} }
// POST /connect -> shared AuthStatusResponse (setup only)
```

## Task 1: Graph Read Services

**Files:** Create helper `Outlook/OutlookContracts.cs`, `OutlookScopes.cs`, `OutlookGraphClient.cs`, `OutlookTokenProvider.cs`, `CalendarCatalogService.cs`, `CalendarViewService.cs`, `EventDetailsService.cs`, `OutlookSettingsStore.cs`, and focused `Outlook*Tests.cs` in the existing test project.

**Interfaces:** `IOutlookTokenProvider.GetTokenAsync(CancellationToken)` and `GetAccountKeyAsync(CancellationToken)`; account key derives from configured app/tenant and current signed-in account, never a token. Services expose async calendar list/preferences/view/details methods matching the wire contract. Backend implementation owns concrete C# types and passes them to route integration.

- [ ] Write fake-HTTP tests that fail on a missing scope bundle, external nextLink, incomplete pagination, private-title leak, or source-route confusion.
```csharp
Assert.Equal(new[] { "User.Read", "Calendars.Read.Shared", "MailboxSettings.Read", "Group.Read.All" }, OutlookScopes.All);
// Capture actual HttpRequestMessages in a recording handler:
Assert.All(requests, r => Assert.Equal(HttpMethod.Get, r.Method));
```
- [ ] Run `dotnet test microsoft-widgets-helper/tests/MicrosoftWidgets.Helper.Tests --filter Outlook` and record the expected missing-feature failure.
- [ ] Implement fixed Graph route builders, source-aware catalog, typed responses, bounded pagination, four-request concurrency, Retry-After, normalized recurrence instances, cache freshness and account invalidation. Use JsonDocument/typed JSON, not HTML/string scraping.
- [ ] Tests additionally assert 403/404 cache invalidation, 401 global purge, 24-hour expiry, 62-day bound, cancellation, all-day exclusive ends, and allowed private details only on demand.
- [ ] Run helper tests and review the Graph GET-only boundary.

## Task 2: Local API and Pairing

**Files:** Create `Outlook/OutlookIntegration.cs`, `OutlookAccessService.cs`, `OutlookJoinService.cs`, integration/security tests. Register from Program in Task 4.

**Interfaces:** `AddOutlookIntegration()` and `MapOutlookIntegration()` extension methods; `OutlookAccessService` issues same-origin sessions and account-bound native credentials. Route contracts are defined above. Use a injectable meeting launcher so tests never launch real URLs.

- [ ] Write failing tests for unpaired access, hostile Origin/Host, null-origin approval, expired request, wrong request secret, revoked credential, and account change.
- [ ] Implement credential hashes, five-minute requests, bounded/rate-limited bootstrap, exact-origin setup approval, and separate native read/join authorization.
```csharp
// Representative endpoint assertions; fixture builds the actual route pipeline.
Assert.Equal(HttpStatusCode.Forbidden, approvalFromNullOrigin.StatusCode);
Assert.Equal(HttpStatusCode.Unauthorized, viewWithoutCredential.StatusCode);
Assert.Equal(HttpStatusCode.NoContent, approvedKnownEventJoin.StatusCode);
```
- [ ] Add safe event-resolved HTTPS launch, loopback/private literal rejection, debounce, and launch-error feedback.
- [ ] Run all helper tests and independently review auth, cache, and URL-launch boundaries.

## Task 3: Native Widget

**Files:** Create `outlook-edge-widget/widget/` HTML/manifest/translation/resources, `src/{api,settings,state,calendar,dialogs,app}.js`, package/lockfile/build script, tests, and fixture preview.

**Interfaces:** Shared wire contract above. Native identity is `uniqueId`; preview uses an explicit instance query parameter. Store only settings and a paired credential, not Graph tokens or event details.

- [ ] Write failing Node tests for default Week/Sunday/7-day settings, per-instance storage, malformed saved settings, range boundaries, private masking and stale response rejection.
```js
assert.equal(defaultSettings().view, 'timeGridWeek');
assert.equal(defaultSettings().firstDay, 0);
assert.equal(defaultSettings().weekends, true);
assert.equal(defaultSettings().agendaDays, 7);
```
- [ ] Bundle FullCalendar Standard and required timezone dependencies with exact locked versions and no remote runtime assets. Set `editable:false`, `selectable:false`; implement timeGridWeek/dayGridMonth/list range via a calendar adapter.
- [ ] Build the approved calendar picker, saved weekend/week-start/hours/zone controls, agenda range, details, Join, private masking, month overflow dialog, and stable refresh handling. Add native pairing code/polling and preview same-origin session setup.
- [ ] Exercise missing/partial source errors, offline/expired data, details canceled by newer tap, focus/backdrop close, and two isolated instances using DOM/browser tests.
- [ ] Verify screenshots at 2560x720, tile widths, and 390px; preserve scroll and dialog state on refresh.

## Task 4: Authentication, Setup, and Packaging

**Files:** Modify auth service, Program, helper csproj, setup index; create setup `outlook-setup.js` and tests, Outlook package/verify PowerShell scripts; update root release scripts and installation docs.

- [ ] Write failing scope/configuration and setup tests asserting one interactive request and no automatic consent/meeting launch.
- [ ] Add `ConnectOutlookAsync` to shared auth, calling `AcquireTokenInteractive(OutlookScopes.All)` for the current account (or selecting one on first sign-in), with the same protected MSAL cache.
- [ ] Register Outlook without changing Planner's existing contract; include Outlook in health integration names. Handle Outlook errors within its route group.
- [ ] Setup adds Outlook consent/status, shared-owner references, pairing approvals/revocations, preview link, and native package download. Static setup never acquires tokens interactively on load.
- [ ] Publish `/outlook/` bundled assets and include Outlook `.icuewidget` beside Planner in release/install outputs; keep version metadata unchanged until release requested.
- [ ] Run helper, both widgets, setup tests, and packaging CLI validation.

## Task 5: End-to-End Review

- [ ] Run full regression and build; review changes against every spec section.
- [ ] Launch a separate local development helper on an unused loopback port with isolated test data only when needed; do not stop/replace the user's installed helper or read live calendars without setup authorization.
- [ ] Test fixture preview, native packaging, offline recovery and malicious payload fixtures. No automatic admin consent or live meeting opening.
- [ ] Record real-device/native and tenant-consent checks still requiring the user; deliver the build and precise next setup steps.
- [ ] Commit explicitly scoped files with Conventional Commits, but do not merge, push, or publish.

## Execution Ledger

- Initial state: approved design and group permission; pending logo work remains uncommitted and must be preserved.
- Ruling: use the existing isolated implementation worktree to retain the shared-helper tray base; scope commits to Outlook work and leave the logo files unstaged unless an integration edit shares that file.
- Ruling: delegate backend and frontend to distinct file owners; coordinator owns auth/setup/packaging. Do not run concurrent git commits or shared test builds from agents.

### Verified Development Checkpoint (2026-09-18)

The task checklists above are the original execution recipe. Current delivery status:

- Tasks 1-4 implemented: GET-only Graph services, account-bound pairing, native widget, bundled consent, setup, and packaging.
- Full `scripts/build-release.ps1` passed: 144 helper tests (67 Outlook-specific), 23 Planner widget tests, 18 Outlook tests, six Outlook setup tests, and two update UI tests. Total: 193 passing, none skipped.
- Outlook browser suite passed at XL/L/M/mobile widths across Week/Agenda/Month, with privacy, stale-response, offline/recovery, settings, two-instance, session-renewal, and native-file pairing fixtures.
- Setup browser checks passed on desktop/mobile with fake endpoints; consent requires an explicit click and calendar text is rendered literally.
- iCUE CLI validates and packages Outlook 0.1.0 without warnings. Planner retains its existing nonblocking translation/icueEvents warnings. Both packages, hosted assets, licenses, portable archive, checksums, and Inno installer were built locally.
- Independent security review found authorization-case and account-switch races. Endpoint metadata, request-bound identity, and serialized account transitions/response writes/meeting launches/global purge address them; deterministic regression tests pass.
- Fixture preview is running at `http://127.0.0.1:8788/outlook/?demo=1`; it does not read Microsoft data or launch real meetings. Existing helper on 8787 is untouched.
- Prior purple-W logo changes are preserved separately, not folded into the Outlook commit. Build output includes the current worktree's logo, but is not a release.
- Still required: install the development helper with user approval, grant the one Outlook scope bundle, verify personal/shared/delegated/group calendars against the tenant, then test native iCUE pairing, two-instance persistence, touch interaction, and deliberate meeting handoff. No live Graph reads, consent, installation, merge, push, or publication occurred.

### Installation Follow-Up (2026-09-18)

- User approved proceeding with installation and tenant setup. Installed the local development installer and restarted the helper; Planner account and board selection remained available.
- Live first-time setup exposed missing-consent status invalidating its own session and leaving Connect disabled. Added a failing endpoint regression, then preflighted silent token availability before data-service reads; added a failing setup regression and kept explicit reconnect available after status failures.
- Rebuilt successfully with 145 helper tests, seven Outlook setup tests, and unchanged remaining suites (195 total); setup browser smoke test passed. Installed the corrected build and verified an enabled Connect Outlook button with the expected consent-required message.
- Started the bundled interactive sign-in through setup. User must complete Microsoft's approval flow. Native pairing and actual calendar acceptance remain pending; no publication or push performed.
