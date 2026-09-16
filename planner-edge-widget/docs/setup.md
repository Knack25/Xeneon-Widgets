# Planner Edge Widget Setup

## Microsoft work account

Register a public client application in your organization's Microsoft Entra tenant. Add delegated Microsoft Graph permissions `User.Read` and `Tasks.ReadWrite`. Add a mobile/desktop redirect URI of `http://localhost`, then enable public client flows. Your organization's administrator may need to approve consent.

Copy the app's **Application (client) ID**. It is an identifier, not a client secret. In the PowerShell window where you will start the helper:

```powershell
$env:AzureAd__ClientId = "YOUR-APPLICATION-CLIENT-ID"
$env:AzureAd__Tenant = "organizations"
dotnet run --project helper/PlannerEdge.Helper/PlannerEdge.Helper.csproj --no-launch-profile
```

Run these commands from the `planner-edge-widget` folder. To use only your organization's tenant, set `AzureAd__Tenant` to its tenant ID instead of `organizations`.

The helper listens at [http://localhost:8787](http://localhost:8787). Open that page, sign in, select a Planner board, and save it. The helper stores the selected board and an encrypted Microsoft token cache under your Windows profile. The widget never receives tokens.

Keep the helper running while using the display. The widget updates once per minute. A network failure can show the last cached board, labeled as an offline view.

## Widget

From the `planner-edge-widget` folder run:

```powershell
npm --prefix widget install
.\scripts\package.ps1
```

Import `dist/planner-edge-widget.icuewidget` in iCUE and add it to the XENEON EDGE dashboard. The widget requests access to `localhost:8787` to read the helper. You can tap a task, review its title, then tap **Complete** or **Cancel**.

## Verify

```powershell
.\scripts\verify.ps1
```

This runs the helper and widget tests. The local iCUE Widget CLI validates and packages the widget.
