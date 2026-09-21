# Planner Edge Widget Setup

## Start the helper

Install **MicrosoftWidgetsSetup-0.3.0.exe** from the [latest release](https://github.com/Knack25/Xeneon-Widgets/releases/latest), then open **Microsoft Widgets Setup** from the Start menu. It opens the shared helper setup page in your browser. Leave the helper running while using the widget. Stop any old portable Planner Edge helper first; both use the same port. Existing saved sign-in and board settings are reused. See the [installation guide](../../docs/INSTALL.md) for details.

On the page, paste your organization's Microsoft Entra **Application (client) ID**, save the connection, sign in with your work account, and choose a board. You can change the app ID later on the same page without restarting the helper.

Your organization must have a public client app registration with delegated Microsoft Graph permissions `User.Read`, `Tasks.ReadWrite`, and `Group-Conversation.ReadWrite.All`, a mobile/desktop redirect URI of `http://localhost`, and public client flows enabled. An IT administrator may need to create or approve it. The page includes a short guide to finding the ID.

`Group-Conversation.ReadWrite.All` lets task chat read and post Planner comments. Existing users may need to open Microsoft Widgets Setup and select **Enable task chat** once to reconnect and approve this permission. If it is not approved, only task chat is unavailable; boards, tasks, checklists, and notes continue to work. Reading and saving notes uses the existing `Tasks.ReadWrite` permission.

To show assignee names in task details, the app also needs the optional delegated permission `User.ReadBasic.All`. Use **Show assignee names** on the setup page to request it. If your organization has not approved it, names appear as unavailable.

To edit task assignees or assign a new task, the app also needs the optional delegated permission `GroupMember.ReadBasic.All`. Use **Enable board members** on the setup page and have your administrator approve the request if prompted. Assignee editing is available for group-backed boards whose member list can be loaded; otherwise existing assignees remain visible but editing is disabled.

If Microsoft shows `AADSTS900971: No reply address provided`, open the app registration's **Authentication** page in Microsoft Entra, add **Mobile and desktop applications** with redirect URI `http://localhost`, and save. This registration setting cannot be changed by the helper.

The helper stores your selection and an encrypted Microsoft token cache under your Windows profile. The widget never receives tokens.

For development, `scripts/publish-helper.ps1` builds the Windows executable. Once built, normal setup needs no terminal commands.

The helper uses [http://localhost:8787](http://localhost:8787). The widget updates once per minute. A network failure can show the last cached board, labeled as an offline view.

## Widget

Use **Download Planner widget** on the helper setup page, then import that file through **Widgets > +** in iCUE. No build commands are required for release downloads.

For development only, from the `planner-edge-widget` folder run:

```powershell
npm --prefix widget install
.\scripts\package.ps1
```

Import `dist/PlannerEdgeWidget-0.3.0.icuewidget` in iCUE and add it to the XENEON EDGE dashboard. Tap the board name to switch boards. Use **My tasks** to show only tasks assigned to you, or **+** to add a task. The large task checkbox asks to complete the task; the rest of a task opens its details. A task card previews up to three checklist items; checklist items can be completed from the details view. In task details, tap Bucket to move a task, Due to change its date, or Assigned to to edit its assignees. Notes below the checklist can be edited and saved explicitly. Task chat shows Planner comments, loads earlier comments on demand, and can post a first comment or reply. Tap outside a dialog to close it.

If the imported widget does not load, keep using the same task view with the built-in **iFrame** widget (not **Web URL**, which may try HTTPS for a local HTTP link). Paste this into the iFrame widget's code field:

```html
<iframe src="http://localhost:8787/board/index.html" width="100%" height="100%" frameborder="0"></iframe>
```

The helper must be running on the same computer. This uses the selected board and the same tap-to-confirm controls.

## Connection diagnostic

`scripts/package-connection-test.ps1` creates `dist/PlannerEdgeConnectionTest-0.0.1.icuewidget`. This separate, read-only widget checks only the helper's `/health` endpoint with standard and opaque requests. It never reads or changes Planner tasks. Import it alongside the existing widget if native network access needs troubleshooting; report both on-screen results and whether iCUE asks to allow `localhost:8787`.

## Verify

```powershell
.\scripts\verify.ps1
```

This runs the helper and widget tests. The local iCUE Widget CLI validates and packages the widget.
