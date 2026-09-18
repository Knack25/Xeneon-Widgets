# Helper Updates Implementation Plan

**Goal:** Check at startup, daily, and on demand; install only after explicit approval.

**Architecture:** A shared update service reads public stable GitHub releases without Microsoft tokens. A local-only setup UI approves a specific version. A separate copy of the helper runs the verified installer and restarts the installed helper, including after installer cancellation/failure. Preserve account data and installer preferences. Portable/development builds check but do not self-install.

**Tech Stack:** .NET 10, HttpClient, SHA256, existing Inno Setup installer, vanilla setup UI.

## Constraints

- Fixed source: Knack25/Xeneon-Widgets. HTTPS only, bounded downloads, exact release asset name and SHA256 digest required.
- Compare suite release versions, not the separately versioned helper or widget. Reject prereleases, malformed versions, and downgrades.
- Same-origin setup-only update routes; native widget origins cannot invoke installation.
- No automatic installation or Microsoft permission changes. Network failures must not interrupt Planner.
- Imported iCUE widgets still require manual reimport; bundle them in the normal installer.

## Tasks

- [x] Add failing tests for release parsing, version comparison, digest validation, download failures, approval, concurrent operations, and endpoint origin checks.
- [x] Implement `Updates/ReleaseClient.cs` for bounded GitHub metadata/downloads and strict selection; `UpdateService.cs` for scheduling and approval states; `UpdateInstaller.cs` for independent installer handoff/restart.
- [x] Add local-only `/updates`, `/updates/check`, `/updates/install` routes, startup/daily background worker, and setup buttons with progress/error/retry states.
- [x] Add suite release metadata, increment helper version, and teach Inno the updater mode without changing startup preferences.
- [x] Run helper and widget tests, publish/build installer, check real GitHub metadata, inspect the setup page, and test installer handoff without changing Planner tasks.
- [x] Document the one-time upgrade needed to obtain the updater and the remaining manual widget import.

## Verification

66 helper tests, 2 setup UI tests, and 23 widget tests pass. The local published runner upgraded the installed helper successfully; sign-in, board settings, startup preference, and the installed executable hash were verified. Manual checking advanced the last-check timestamp against live GitHub. Native-widget origins received HTTP 403 on update status/check/install routes. Browser checks passed at desktop and 390px width. Code review found cancellation and slow-shutdown races; both were corrected with regression tests and a second successful installer handoff.

GitHub 0.3.1 has not been published. The full future-release download-to-install path is covered in parts (deterministic download/approval tests and real local installer handoff), not by publishing a synthetic newer release. Daily scheduling has not been observed for a full 24 hours. Installer failure recovery and readiness timeouts retain residual integration-test gaps.
