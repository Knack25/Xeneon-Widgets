# Install Microsoft Widgets

## Recommended: Windows installer

1. Download **MicrosoftWidgetsSetup-0.3.0.exe** from the GitHub release. This contains Microsoft Widgets Helper 0.1.1 and Planner Edge Widget 0.3.0.
2. Close any older portable Planner Edge or Microsoft Widgets helper before installing. For the installed helper, upgrades close it automatically.
3. Run the installer. It installs for your Windows account without requiring administrator access. Optionally select **Start the helper when I sign in to Windows**.
4. Leave **Open Microsoft Widgets setup** selected on the final page.
5. Connect your work account and select a Planner board. Existing saved sign-in and board settings are preserved when upgrading on the same Windows account.
6. In setup, click **Download Planner widget**. In iCUE, choose **XENEON EDGE > Widgets > +**, import that `.icuewidget` file, and add Planner Edge to your display. Allow `localhost:8787` if requested.

The installer includes the .NET runtime. You do not need PowerShell, Node.js, or a separate .NET installation.

## Microsoft account setup

Your organization supplies the Application (client) ID for a Microsoft Entra public-client app. The setup page explains where to find it. Initial Planner access requires delegated `User.Read` and `Tasks.ReadWrite`; showing names uses `User.ReadBasic.All`, and editing assignees uses `GroupMember.ReadBasic.All`. The desktop redirect URI is `http://localhost`.

Work-account permissions may require administrator approval. Once approved, return to setup and repeat the relevant permission button. The installer cannot grant Microsoft permissions for your organization.

## Everyday use

- The helper runs in the background. **Microsoft Widgets Setup** in the Start menu opens the setup page without starting another copy.
- **Stop Microsoft Widgets Helper** in the Start menu, or **Stop helper** on the setup page, stops it.
- Enable or disable startup in Windows **Settings > Apps > Startup** if you selected it during installation.
- To upgrade, run the newer installer and import a newer widget package if the release includes one.
- Uninstall through Windows **Installed apps**. Your account settings and encrypted token cache are retained for reinstallation. Sign out before uninstalling if you want to remove the cached account.

## Downloads explained

- **MicrosoftWidgetsSetup-0.3.0.exe**: recommended; installs the helper and includes the widget.
- **PlannerEdgeWidget-0.3.0.icuewidget**: widget only, for an existing helper installation.
- **MicrosoftWidgetsHelper-0.1.1-portable-win-x64.zip**: optional portable helper and widget package. Extract the entire archive before opening `MicrosoftWidgets.Helper.exe`.
- **SHA256SUMS.txt**: checksums for the release downloads.

Requires 64-bit Windows 10 22H2 or later and a compatible iCUE installation with XENEON EDGE. The installer is currently unsigned, so Windows may identify the publisher as unknown. Follow your organization's software-installation policy.

Support and releases: https://github.com/Knack25/Xeneon-Widgets
