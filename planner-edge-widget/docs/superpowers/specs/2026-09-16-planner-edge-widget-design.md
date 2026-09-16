# Planner Edge Widget Design

Date: 2026-09-16

## Goal

Build a XENEON EDGE widget that connects to a Microsoft work account, lets the user select which Microsoft Planner board to display, shows active tasks from that board, and allows completing a task with tap plus confirmation.

The first version should feel glanceable on the XENEON EDGE, keep Microsoft tokens out of the widget runtime, and avoid becoming a full Planner client.

## Scope

Included:

- Microsoft work or school account sign-in.
- Board selection from accessible Microsoft Planner plans.
- Display of active tasks from the selected plan.
- Bucket-grouped task list optimized for the XENEON EDGE display.
- Tap a task checkbox, confirm, then mark the task complete.
- Loading, signed-out, offline, permission-error, and last-synced states.
- Local caching so the widget can show the last known board when Graph or network access is unavailable.

Excluded from the first version:

- Creating tasks.
- Editing titles, dates, buckets, assignments, or labels.
- Deleting tasks.
- Planner Premium or Project-backed task support beyond best-effort display.
- Multi-account switching.
- Cloud hosting.

## Architecture

The project has two runtime pieces.

### Local Helper

The helper is a small Windows app/service that runs locally and exposes a private HTTP API on localhost. It owns Microsoft authentication, token cache, Microsoft Graph calls, local cache, conflict handling, and writes to Planner.

Recommended stack:

- .NET for the helper runtime.
- MSAL for Microsoft sign-in and token cache.
- Microsoft Graph REST or SDK for Planner calls.
- Kestrel or minimal ASP.NET Core for the localhost API.

The helper should bind only to loopback and should not expose tokens through its API.

### iCUE Widget

The widget is a packaged iCUE widget for `dashboard_lcd`, targeting XENEON EDGE first. It contains static HTML, CSS, and JavaScript, plus manifest metadata and resources. It requests only the localhost URL permission needed to talk to the helper.

The widget should be responsive across common XENEON EDGE widget sizes, with primary optimization for the wide dashboard display.

## Microsoft Graph Integration

The helper uses delegated permissions for a work or school account:

- `User.Read` for sign-in and signed-in user group membership discovery.
- `Tasks.ReadWrite` for Planner reads and marking tasks complete.

Some tenants may require admin consent for `Tasks.ReadWrite`.

### Board Discovery

Planner plans are usually contained by Microsoft 365 groups. The helper discovers candidate groups for the signed-in user, then queries each group for Planner plans.

Planned calls:

- `GET /me/memberOf/microsoft.graph.group` or an equivalent group membership query.
- `GET /groups/{group-id}/planner/plans` for each candidate group.

The helper stores the selected plan ID and display name locally.

### Task Display

For the selected plan, the helper loads buckets and tasks:

- `GET /planner/plans/{plan-id}/buckets`
- `GET /planner/plans/{plan-id}/tasks`

The helper returns a compact display model to the widget:

- plan ID and title
- sync timestamp
- bucket list
- active tasks grouped by bucket
- task title, due date, priority, percent complete, assignments if available, and ETag

Completed tasks are hidden by default in the first version.

### Completing Tasks

When the user confirms completion, the widget calls the helper with the task ID. The helper refreshes or validates the task ETag, then updates Planner:

- `PATCH /planner/tasks/{task-id}`
- body: `{ "percentComplete": 100 }`
- header: `If-Match: <latest task etag>`

The helper handles conflicts by refetching the task and retrying only when it is still safe to complete. If the task is already complete, the helper returns success.

## Local Helper API

Initial API shape:

- `GET /health`
  Returns helper status and version.

- `GET /auth/status`
  Returns whether a user is signed in and the display name or account hint.

- `POST /auth/sign-in`
  Starts interactive Microsoft sign-in through the system browser.

- `POST /auth/sign-out`
  Clears local token/account state.

- `GET /plans`
  Returns discovered Planner boards.

- `GET /settings`
  Returns selected plan and display preferences.

- `PUT /settings`
  Updates selected plan and display preferences.

- `GET /display`
  Returns the selected board's display model.

- `POST /tasks/{taskId}/complete`
  Marks a task complete after widget-side confirmation.

All responses should be JSON. Error responses should include a stable code and user-readable message.

## Widget UI

The widget has these states:

- Signed out: prompt to open the local helper sign-in flow.
- Loading: compact status while the helper or Graph is loading.
- No board selected: show instruction to select a board from settings/helper.
- Board view: selected board name, sync status, and active tasks grouped by bucket.
- Confirm complete: overlay with task title and confirm/cancel controls.
- Offline/error: show last known board if available with a clear stale indicator.

Interaction:

- Tapping a checkbox opens the confirmation overlay.
- Confirm completes the task.
- Cancel returns to the board.
- After completion, the task is removed or visually marked complete after the helper confirms success.

The first version should avoid dense editing controls. It should be quick to read from a distance and hard to mis-tap.

## Persistence

The helper stores:

- MSAL token cache using the platform-appropriate secure cache pattern.
- selected plan ID/title
- cached plan list
- cached display model
- basic display preferences

The widget can use local storage only for non-sensitive UI state such as collapsed buckets or last viewed timestamp. It must not store Microsoft tokens.

## Security

- Bind the helper to localhost only.
- Do not expose tokens through helper responses or logs.
- Declare only the needed iCUE URL permission for the localhost helper port.
- Keep the helper API small and purpose-built.
- Require confirmation before completing tasks.
- Avoid write operations other than marking tasks complete in the first version.

## Error Handling

The helper should map Graph and auth failures into simple states for the widget:

- signed_out
- consent_required
- forbidden
- network_unavailable
- graph_unavailable
- task_conflict
- unknown_error

For stale data, the helper returns the last known display model with a stale flag and timestamp. The widget should keep the display useful rather than blank.

## Testing

Helper tests:

- plan discovery response mapping
- display model grouping
- task completion ETag handling
- already-complete task behavior
- Graph error mapping

Widget tests/manual checks:

- browser preview at XENEON EDGE dimensions
- loading, signed-out, no-board, board, confirm, error, and stale states
- tap target sizing and text overflow
- localhost permission behavior in iCUE

End-to-end manual test:

1. Start helper.
2. Sign in with a work account.
3. Select a board.
4. Import widget into iCUE.
5. Confirm the selected board appears.
6. Complete a test task with tap plus confirmation.
7. Confirm Planner shows the task complete.

## Open Decisions

- Exact helper port.
- Whether the helper should ship as a tray app, Windows service, or simple console app for the first build.
- Public or private GitHub repository visibility.
- Final GitHub repository name.
