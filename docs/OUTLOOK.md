# Outlook Edge Widget

Read-only Outlook calendars for XENEON EDGE, using the same Microsoft Widgets Helper as Planner. This is development documentation for the next widget, not an announcement of a published release.

## One-Time Microsoft Approval

Use the helper's existing Microsoft Entra public-client application and `http://localhost` mobile/desktop redirect URI. No client secret is required.

Add all four delegated Microsoft Graph permissions before submitting the organization's consent request:

| Permission | Used for |
| --- | --- |
| `User.Read` | Identify the signed-in account and its group memberships. |
| `Calendars.Read.Shared` | Read personal, shared, and delegated calendars. |
| `MailboxSettings.Read` | Read Outlook working hours and time-zone settings. |
| `Group.Read.All` | Read Microsoft 365 group metadata, calendars, and event details. |

`Group.Read.All` requires administrator consent. Microsoft also permits reading other accessible group content under that permission, including conversations and files. The Outlook widget only uses membership and calendar endpoints. There are no calendar write, mail, directory-wide user, or Teams meeting permissions in this bundle.

Select **Connect Outlook** in helper setup to request the whole bundle at once. If approval is pending, return and select **Connect Outlook** again after the admin grants it. Loading setup, choosing a calendar, and opening event details never trigger an interactive consent request. Existing Planner consent stays intact, but Outlook can be connected without first enabling Planner.

Setup checks existing access silently. When Outlook is available, it loads calendars automatically and disables the button as **Outlook connected**. Planner's names and board-member buttons similarly show **enabled** once their scopes are available. A failed network check offers **Retry** without opening consent; a sign-in or consent requirement keeps the connection action available. Each new user still signs in on their own PC.

For another user in the same organization, confirm both installations use the same application/client ID and tenant. Tenant-wide admin consent must cover the exact delegated permissions requested by each integration, not only the administrator's own account or an older subset. Configuring a permission on an app registration is not the same as granting it. The helper no longer forces the consent dialog when access has already been granted. Calendar sharing and application-assignment policies still apply independently of consent.

See [Microsoft's permission reference](https://learn.microsoft.com/en-us/graph/permissions-reference) and [group-event permissions](https://learn.microsoft.com/en-us/graph/api/group-get-event?view=graph-rest-1.0).

## Add the Widget

1. In helper setup, connect Outlook and check **Available calendars**.
2. Download the Outlook widget from setup when using a build that includes it.
3. In iCUE, select **XENEON EDGE > Widgets > +** and import the `.icuewidget` file.
4. Allow `localhost:8787`, then compare the connection code shown on the widget with the pending request in setup. Approve only matching requests you initiated.
5. Choose calendars on the display. Add more instances for different selections or views; each instance remembers its own settings.

The helper includes an actual calendar preview and a separate sample-data preview. The sample does not contact Microsoft or open meeting links. The installed helper does not gain Outlook until a new helper build is installed; importing just the new widget into an older helper is insufficient.

## Shared Calendars

The picker includes the personal/shared calendars exposed by Graph and calendars of Microsoft 365 groups you belong to. If a primary shared/delegated calendar is missing, add its owner's email address in setup. This only saves a local reference and does not grant access or change Microsoft sharing permissions.

For a custom shared calendar, accept or add it in Outlook first. Some internet subscriptions, on-premises calendars, cross-tenant shares, and free/busy-only sources cannot provide complete event data through Graph. An administrator consent grant does not override calendar sharing restrictions.

## Views and Privacy

- Week is the default; Agenda and Month are available from the toolbar.
- Week starts Sunday initially and shows weekends. Both are configurable per instance.
- Outlook working hours set the initial scroll position; earlier/later times remain available. A widget-only hours override does not change Outlook.
- Time zone defaults to the PC's current zone and can be overridden.
- Calendar colors follow Outlook when Graph provides them; otherwise the widget assigns a stable fallback.
- Private events appear as **Private event** on the calendar. Tapping reveals only information your account is authorized to see.
- Event details include attendees, organizer, description, and a **Join meeting** action when a usable meeting link is available. Join opens the PC's default browser; the provider handles app handoff, lobby, camera, and microphone. It never sends an RSVP.
- A failed refresh retains the last loaded summaries with a stale warning. Data is not persisted to disk for offline use, and signing out or losing access clears affected data.
- There are no create/edit/delete actions. Read/write support will have its own reviewed permission bundle later.

## Development Acceptance

Run both widget suites and the helper suite before packaging. Verify Week/Agenda/Month, overlapping and all-day events, recurrence exceptions, DST, six-week months, private masking, two-instance persistence, offline recovery, and pairing rejection.

Native iCUE localhost access and meeting handoff require testing on the real device after installing an Outlook-enabled helper. Fixture/browser tests do not prove native compatibility. Do not test by changing work events or automatically joining a real meeting.
