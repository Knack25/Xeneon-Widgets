# Outlook Edge Widget: Read-Only Design

Status: visual design approved; technical design awaiting user review.

## Goal

Create a separate native XENEON EDGE calendar widget backed by Microsoft Widgets Helper. Reuse the signed-in work account, installer, tray, and update mechanism. Keep Planner operational and its configuration unchanged. No calendar writes, RSVP changes, automatic meeting joining, or mail features in this version.

The approved interactive mockup is `outlook-week-concept.html` in the thread visualization directory. It establishes the toolbar, combined time grid, calendar colors, picker, and event dialog. Sample data, hardcoded dates, and mock rendering algorithms are not production code.

## Product Behavior

- Week is the initial view. Agenda and Month are always selectable.
- Week combines selected calendars into one time grid. Overlaps use separate lanes, with an overflow list when touch targets would become too narrow. All-day and multiday events occupy a separate band.
- Show all seven days initially. Show weekends is a saved per-instance toggle. It changes the visible columns, not the fetched date range. Week starts Sunday by default; users may select any weekday.
- Agenda starts at the selected date and covers seven consecutive dates. A range selector offers 7, 14, or 30 days, defaulting to 7. Days with no events have an explicit empty state. The weekend toggle also applies here.
- Month displays all weeks intersecting the selected month, including six-row months. Overflow uses a '+N more' button opening the day's scrollable event list, with event details available on tap.
- Previous/next moves by week, agenda range, or month respectively. Today returns to the current date without changing the view or resetting preferences.
- Calendar selection is a checkbox popover with Select all, an indeterminate partial-selection state, calendar swatches, names, and owner labels. Select all is a saved mode that includes newly discovered calendars; individual selection stores explicit calendar keys. Deselecting everything shows 'No calendars selected', not a network error.
- Default selection is the user's primary calendar; other calendars remain available in the picker. Users can choose all with one action.
- Use Outlook `hexColor` when present, otherwise map `color`, otherwise use a stable per-calendar fallback. Keep the source color as a stripe/swatch and derive readable surface/text colors. Duplicate Outlook colors stay unchanged; labels disambiguate them. Do not modify Outlook colors.
- The full 24-hour time grid remains scrollable. Initially scroll to working-hours start; shading identifies the working period. Working hours do not suppress events outside that period or nonworking days. Offer 'Use Outlook hours' or a local start/end override, falling back to 8 AM-6 PM if unavailable. Overnight ranges wrap through midnight.
- Default display time zone is the PC's current zone. Offer a searchable named-zone selector; changing it affects this instance only. Show the active zone near the grid. Recompute after system-zone and DST changes.
- Keep sticky day/time headers and stable scroll positions during refresh. Preserve open dialogs, focus, selected date, and calendar selections. Never rebuild the entire UI on a polling tick.

## Event Details and Joining

Tapping an event opens title, date/time, calendar, location, organizer, attendees, description, and Join meeting when a meeting link exists. Missing fields have neutral placeholders. Render text through textContent, not unsanitized Outlook HTML. Attendees use the names/email already included in the event; no directory lookup or additional people permission is needed.

The main grid, Agenda, Month overflow list, tooltips, and accessibility labels show only 'Private event' plus its time for private events. Fetch the authorized details on demand. Respect Graph's redaction and private-item permissions; never infer hidden information. Opening a private event does not make its title visible elsewhere. Closing a detail view clears its private detail state.

Dialogs close using Close, Escape, or a tap on the backdrop. A drag that started inside the dialog must not count as a backdrop tap. Trap focus and restore it to the originating event or a surviving calendar control.

Join is a deliberate user action. The helper resolves a known event and opens its HTTPS meeting URL in the PC's default browser. Teams/provider handoff, lobby, camera, and microphone remain controlled by that provider. Keep the widget dialog open. No RSVP request is sent. Do not add OnlineMeetings.Read simply to obtain a URL already present in the calendar event.

Prefer `onlineMeeting.joinUrl`; use the event's legacy meeting URL only when valid. Do not guess meeting links from arbitrary description text in version one. The local launch endpoint accepts calendar/event identifiers, never a caller-supplied URL. Reject non-HTTPS URLs, credentials, control characters, localhost/private-network literals, and executable/custom-protocol schemes. Display the destination hostname in details. Launch using the existing Windows shell pattern without constructing shell commands; debounce repeat taps and return visible failures.

## Calendar Coverage and Graph Limits

1. Enumerate paged `/me/calendars` results for personal calendars and recipient-side shared calendars.
2. Provide 'Add shared calendar' in helper setup for an owner's email address. Resolve that owner's primary calendar through `/users/{owner}/calendar` under the current user's delegated access. Do not scan the tenant directory. Adding this reference is local configuration, not a sharing-permission mutation. Custom shared calendars should first be accepted/added in Outlook so their recipient-side IDs can be discovered.
3. Include Microsoft 365 group calendars for the signed-in user's direct Unified-group memberships, discovered through `/me/memberOf`. Read each group's calendar and calendarView using its group ID. Do not enumerate the entire organization.
4. Keep source-aware identifiers: recipient mailbox, owner mailbox, and group routes are not interchangeable. Never take arbitrary Graph paths from the browser. The helper gives each known source an opaque calendar key.
5. Graph-accessible resource/shared-mailbox calendars may be added through the owner-address path. A missing calendar is not evidence of missing consent; report membership, sharing, unsupported source, or discovery limitations distinctly.

This is not a promise to mirror every Outlook navigation-pane item. Internet subscriptions, on-premises calendars, cross-tenant sharing, and free/busy-only access may not expose full events through these routes. Show available data without bypassing sharing restrictions. For recipient and direct-owner aliases, deduplicate only when source identity is proven; identical names are not sufficient. Keep distinct event copies on distinct selected calendars rather than accidentally hiding attendee copies.

Microsoft documents recipient-side and owner-side shared-calendar access separately: [shared/delegated access](https://learn.microsoft.com/en-us/graph/outlook-get-shared-events-calendars). Calendar color and private visibility fields are documented in the [calendar resource](https://learn.microsoft.com/en-us/graph/api/resources/calendar?view=graph-rest-1.0).

## One Permission Bundle

Proposed delegated Graph scopes, requested together by one 'Connect Outlook' action:

| Scope | Purpose |
| --- | --- |
| `User.Read` | Shared account identity and own group memberships; already used by the helper. |
| `Calendars.Read.Shared` | Read personal, shared, and delegated calendar events and details. |
| `MailboxSettings.Read` | Read working hours and mailbox time-zone metadata. |
| `Group.Read.All` | Discover group metadata and read Microsoft 365 group calendars and event details. |

Important review point: `Group.Read.All` requires administrator consent and authorizes broader group content than calendars, including accessible group conversations and files. This implementation will only call the membership/calendar routes. It is included to cover the user's 'any calendar' requirement; removing it before implementation would explicitly remove group-calendar support, not personal/shared/delegated mailbox calendars. See [permission definitions](https://learn.microsoft.com/en-us/graph/permissions-reference) and the [group event API](https://learn.microsoft.com/en-us/graph/api/group-get-event?view=graph-rest-1.0).

No new application permissions, client secrets, Mail.Read, directory-wide user permissions, calendar write scopes, or Teams meeting scopes. MSAL manages its usual OpenID sign-in/refresh scopes. Existing Planner grants stay intact; Outlook token acquisition does not request Tasks.ReadWrite. Authentication can work for Outlook-only installations without first consenting to Planner.

Before asking an admin, register the complete Outlook bundle on the existing Entra application and provide a single checklist naming every scope and its purpose. One interactive request asks for the entire bundle. Tenant policy controls the approval workflow; approval does not guarantee that a user's current token already includes the new scopes. A single reconnect action retries the full bundle and verifies actual read operations. Never launch incremental consent from calendar, attendee, working-hours, or group controls.

Read/write support later requires a separately reviewed, bundled permission upgrade. This release must not request it early for convenience.

## Architecture

Recommended: keep the existing .NET 10 Windows helper and add an isolated Outlook integration. Add a separate `outlook-edge-widget/` package with a bundled FullCalendar Standard renderer and a small touch-oriented shell matching the approved design. Use the currently supported stable FullCalendar release, lock exact dependency versions during implementation, and include the required date-library/polyfill dependencies for its named-zone support. Preserve license notices and bundle all assets locally; do not rely on CDNs in iCUE.

FullCalendar provides the established layout/date/view engine instead of productionizing the hand-built mockup. Use nonpremium TimeGrid, DayGrid, and List views. Disable dragging, resizing, date selection for creation, and editing. [Standard licensing](https://fullcalendar.io/license), [date library](https://fullcalendar.io/docs/date-library), [time zones](https://fullcalendar.io/docs/timeZone).

Alternatives considered: a second Outlook-specific helper would duplicate sign-in, tray, installation, and updates; an embedded Outlook website would not offer reliable native interaction or controlled read-only behavior. Neither fits the approved product.

### Ownership

- `Outlook/OutlookIntegration.cs`: dependency registration and `/api/outlook` route mapping, mirroring PlannerIntegration without touching Planner route contracts.
- `Outlook/OutlookGraphClient.cs`: typed Graph GET requests, fixed routes, pagination, timeout/retry handling, and Outlook-scoped tokens.
- `Outlook/CalendarCatalogService.cs`: calendar discovery, owner references, group sources, and stable opaque keys.
- `Outlook/CalendarViewService.cs`: bounded range queries, normalized event summaries, partial failures, cache, and freshness.
- `Outlook/EventDetailsService.cs`: on-demand authorized details and meeting-link resolution.
- `Outlook/OutlookContracts.cs`: source-free browser DTOs and integration error codes.
- `Outlook/OutlookSettingsStore.cs`: account-bound shared-source references and helper configuration. Event data must not be written to the existing plaintext LocalJsonStore.
- `outlook-edge-widget/widget/src/`: separate modules for API, instance settings, calendar adapter, picker, details, and refresh state.
- Helper setup: Outlook connection status, bundled consent, add/remove local shared-calendar references, instance pairing, package download, and preview.
- Root release scripts: verify/package both widgets and include them in the existing installer. Do not replace or silently reimport Planner.

### Instance Identity and Local Security

Use iCUE's documented `uniqueId` to key per-widget settings, preserving unrelated fields in the host storage object. Persist selections, view, weekend visibility, week start, agenda span, hours, and zone under an Outlook namespace. A browser preview gets its own explicit instance ID. Never use one origin-wide singleton for all native instances. Verify two native instances through restart and reimport. [iCUE storage](https://docs.elgato.com/icue/widgets/references/local-storage/).

Keep OAuth tokens solely in the helper/MSAL protected cache. New Outlook routes require an account-bound random pairing credential for native-widget access; null/file origin is not authentication. A widget generates a pairing request and displays a short code, and the same-origin helper page approves it. Return the credential only to the requester holding its unguessable request secret. Requests expire in five minutes and are rate-limited. Store only a hash on the helper side; permit revocation in setup. Pair each instance independently without another Microsoft consent. The browser preview uses the same-origin setup session. Reject invalid Host/Origin, require JSON and a CSRF token for setup mutations, and do not expose pairing approval to native origins.

The credential authorizes only Outlook read/configuration and deliberate meeting-launch operations, not helper installation, shutdown, or Planner writes. Shared-account sign-out/configuration changes invalidate credentials and in-memory Outlook data. No secrets in query strings, generated packages, previews, diagnostic logs, or screenshots. This is a focused safeguard for new endpoints, not an unrelated rewrite of Planner.

### Data and Refresh

CalendarView endpoints accept a half-open range with explicit UTC offsets, at most 62 days per request. Fetch each selected source using its proper Graph route; follow validated same-origin Graph next links. Limit concurrent source requests to four, honor Retry-After, cap each HTTP request at 20 seconds, and cancel superseded view requests. Fully finish pagination before replacing a source's cached snapshot.

Use calendarView rather than raw events lists so Graph expands recurring instances and exceptions. Normalize timed events to instants plus relevant zone metadata; preserve all-day date semantics and exclusive end dates. Split multiday display segments through the calendar engine. Cover DST, recurrence exceptions, cancellations, midnight boundaries, and non-hour offsets. [CalendarView behavior](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0).

Refresh visible ranges every 60 seconds, immediately after calendar/view changes, and on visibility return. Deduplicate in-flight requests across instances where source/range/account match. Refresh calendar metadata and Outlook hours every 15 minutes. Working hours expose a common start/end and days; they do not reproduce modern Outlook's entire per-day work-location schedule. [Working-hours model](https://learn.microsoft.com/en-us/graph/api/resources/workinghours?view=graph-rest-1.0).

Keep last successful summaries in memory only, keyed by account, calendar, and range, for at most 24 hours with an LRU cap of 32 ranges. Do not persist event bodies, attendees, or meeting URLs to disk/browser storage. An offline refresh keeps the same-range snapshot, visibly labeled 'Offline - Last updated ...'; a new range without cache shows unavailable rather than another range's events. Partial failures retain only the affected source's snapshot and identify its age. Successful sources can update independently.

Sign-out, tenant/account changes, revoked access (403), and deleted sources (404) immediately clear affected cached content rather than treating it as offline. On 401, stop serving cached data and offer reconnect. If the helper itself stops, a widget may keep the already rendered masked summaries with a stale warning until expiry, but cannot load private details or launch meetings. On reconnect, revalidate account identity before reusing any data. Event details are fetched fresh; a detail request failure must never show another event's previously loaded details.

## Local API Shape

- `POST /api/outlook/connect`: same-origin setup only; one interactive bundled consent flow.
- `GET /api/outlook/status`: configured, signed-in, consent-required, ready, or unavailable, with safe integration-specific errors.
- `GET /api/outlook/calendars`: descriptors with opaque key, name, owner label, source kind, resolved color, and private-item capability.
- `GET /api/outlook/preferences`: Outlook working hours, PC time-zone identity, and supported display zones.
- `POST /api/outlook/sources`: same-origin setup only; add an owner-email reference without changing Graph permissions.
- `POST /api/outlook/sources/{key}/remove`: same-origin setup only; remove a local reference and its cached data.
- `POST /api/outlook/view`: JSON body contains known calendar keys and bounded start/end instants; reads Graph only. Returns summaries and per-source freshness/errors.
- `POST /api/outlook/event-details`: JSON calendar key and opaque event reference; returns authorized details.
- `POST /api/outlook/join`: JSON known calendar/event references; resolves and opens the meeting URL after an explicit tap.
- Pairing request/status/approval/revocation routes enforce the separation above; native requests cannot approve themselves.

Local POST does not imply a Microsoft write: Graph traffic for the integration is GET-only. Tests assert this. Outlook-specific exceptions must avoid the current middleware's Planner wording. Return stable error codes for consent, sign-in, source access, throttling, offline, stale snapshot, invalid range, and invalid meeting URL.

## Delivery and Verification

1. Build the Outlook auth/catalog/calendarView service with deterministic HTTP fakes before connecting the user's tenant.
2. Implement the approved views and settings using fixtures, including native-instance isolation and pairing.
3. Integrate details, meeting launch, freshness, and partial failures. Inject a fake URL launcher in tests; never open a real meeting as a smoke test without user approval.
4. Add the Outlook package/download entry to helper setup, preserve existing installer/update paths, and document the single permission bundle.
5. Run the full Planner/helper regression suite plus Outlook unit and browser tests; package both native widgets. No publishing or release-version bump until requested.

Acceptance tests must cover paged and partial catalog/view results, direct shared sources, group events, exact bundled scopes, no Graph writes, privacy masks in every surface, join validation, expired pairing, origin/CSRF rejection, two instances with different settings, source removal, account switches, and cache expiry/recovery.

Layout/browser tests cover XL at 2560x720, L and M tile widths, and a 390px helper preview. Verify nonblank views, touch targets, focus handling, overlays, six-week months, overlapping meetings, all-day spans, and preserved scroll/dialog state across refresh. Confirm the actual native widget in iCUE, including localhost permission, calendar selection, two-instance persistence, and safe Join handoff, before calling the integration complete. Browser-only success is not evidence of native compatibility.

## Approval Boundary

User has approved the read-only feature set and visual mockup. This document adds the concrete permission bundle (including the broader group scope), discovery fallback, per-instance pairing, and implementation boundaries. Obtain review of this technical design before writing the implementation plan and application code. No Graph consent, live event read, installation, or release has been performed as part of this design task.
