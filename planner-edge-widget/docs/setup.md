# Planner Edge Widget Setup

## Start the helper

Double-click `dist/helper/PlannerEdge.Helper.exe`. It opens the Planner Edge setup page in your browser. Leave the helper window open while using the widget.

On the page, paste your organization's Microsoft Entra **Application (client) ID**, save the connection, sign in with your work account, and choose a board. You can change the app ID later on the same page without restarting the helper.

Your organization must have a public client app registration with delegated Microsoft Graph permissions `User.Read` and `Tasks.ReadWrite`, a mobile/desktop redirect URI of `http://localhost`, and public client flows enabled. An IT administrator may need to create or approve it. The page includes a short guide to finding the ID.

If Microsoft shows `AADSTS900971: No reply address provided`, open the app registration's **Authentication** page in Microsoft Entra, add **Mobile and desktop applications** with redirect URI `http://localhost`, and save. This registration setting cannot be changed by the helper.

The helper stores your selection and an encrypted Microsoft token cache under your Windows profile. The widget never receives tokens.

For development, `scripts/publish-helper.ps1` builds the Windows executable. Once built, normal setup needs no terminal commands.

The helper uses [http://localhost:8787](http://localhost:8787). The widget updates once per minute. A network failure can show the last cached board, labeled as an offline view.

## Widget

From the `planner-edge-widget` folder run:

```powershell
npm --prefix widget install
.\scripts\package.ps1
```

Import `dist/PlannerEdgeWidget-0.1.3.icuewidget` in iCUE and add it to the XENEON EDGE dashboard. This version embeds the task view served by the helper at `localhost:8787`, avoiding the direct request that failed in the earlier package. You can tap a task, review its title, then tap **Complete** or **Cancel**.

If the imported widget does not load, keep using the same task view with the built-in **iFrame** widget (not **Web URL**, which may try HTTPS for a local HTTP link). Paste this into the iFrame widget's code field:

```html
<iframe src="http://localhost:8787/board/index.html" width="100%" height="100%" frameborder="0"></iframe>
```

The helper must be running on the same computer. This uses the selected board and the same tap-to-confirm controls; keep the imported widget installed for later testing.

## Verify

```powershell
.\scripts\verify.ps1
```

This runs the helper and widget tests. The local iCUE Widget CLI validates and packages the widget.
