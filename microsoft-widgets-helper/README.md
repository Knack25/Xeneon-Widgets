# Microsoft Widgets Helper

Shared Windows companion for Microsoft widgets on the XENEON EDGE. Helper version 0.1.2 is included in Microsoft Widgets 0.3.1; Planner widget versions remain separate.

## Install

Download **MicrosoftWidgetsSetup** from the [latest release](https://github.com/Knack25/Xeneon-Widgets/releases/latest). The per-user installer includes the helper and Planner widget, Start menu shortcuts, and optional startup at Windows sign-in. No terminal commands or separate .NET installation are needed. See the [installation guide](../docs/INSTALL.md).

From suite version 0.3.1, setup checks for updates at startup and daily. **Check for updates** checks immediately; **Update now** requires explicit approval before downloading and installing. In-app installation requires an installed copy, not a portable/development build. New iCUE widget packages still need importing manually.

## Build and run

Run `scripts/publish.ps1`, then open `dist/helper/MicrosoftWidgets.Helper.exe`. The setup page opens at http://localhost:8787. Only one helper should run at a time. Stop the old Planner Edge helper before starting this executable.

Run `scripts/verify.ps1` to test the helper independently. `MicrosoftWidgets.Helper.slnx` contains the host and test projects.

## Shared foundation

`Auth` owns the Microsoft work-account connection and encrypted token cache. `Storage/LocalJsonStore` supplies local persistence, and the host owns origin checks, setup, and connection endpoints. New integrations can request their own delegated scopes through the shared auth service. Never expose tokens to widgets or request unrelated permissions at startup.

## Planner integration

`Planner/PlannerIntegration.cs` registers Planner services and routes. New clients can use `/api/planner/plans`, `/api/planner/display`, `/api/planner/tasks`, and the other Planner routes under this prefix. Existing unprefixed endpoints remain supported for installed widgets. `/board/index.html` remains available and bundles assets from the sibling Planner widget project.

The Windows data directory remains `%LOCALAPPDATA%/PlannerEdgeWidget` intentionally. Existing connection settings, selected board, and encrypted MSAL cache are reused without moving or rewriting credentials. Existing internal `PlannerEdge.Helper` namespaces are retained during extraction to keep the behavior change small.

## Adding an integration

Add a module with service registration and namespaced routes, its own settings keys, focused Graph client and tests. Register it in the shared host and add its setup section. Keep integration-specific data and permissions out of other integrations. The current host supports one work account.
