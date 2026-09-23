# Security remediation appendix

Date: 2026-09-22 (America/Indianapolis)

Baseline audit: [2026-09-21-audit.md](2026-09-21-audit.md), audited at `46770b1`. This appendix records source remediation and automated evidence without rewriting the original findings. No release was published and no real Microsoft account, task, calendar, permission, or iCUE state was changed during this work.

## Finding matrix

| Finding | Source status | Positive regression evidence | Primary commits |
| --- | --- | --- | --- |
| SEC-01 anonymous Planner and management access | Remediated | `PlannerAuthorizationTests.EveryPlannerEndpointRequiresItsScope`, `OwnerManagementEndpointTests`, `WidgetPairingServiceTests` | `7b465ce`, `682cfd9`, `e378c7e` |
| SEC-02 forgeable Outlook setup bootstrap | Remediated | `OwnerBootstrapEndpointTests.Forged_same_origin_headers_cannot_create_an_owner_session`, single-use bootstrap tests | `23095e0`, `f702147`, `887ad86` |
| SEC-03 attacker-controlled Host routing | Remediated | `PlannerHttpIntegrationTests.Attacker_host_cannot_read_static_health_or_integration_routes`, `LoopbackRequestPolicyTests` | `6eab6b6`, `4fa44dd` |
| SEC-04 Planner data retained after authorization loss | Remediated | `PlannerDataLifecycleTests`, `PlannerCoordinatorTests`, Planner startup authorization-loss tests | `e201be4`, `8c60736`, `076611b`, `b4cb605`, `8d8a78e`, `67a86d1` |
| SEC-05 late Outlook response after authorization loss | Remediated | Outlook `authorization failure invalidates delayed cache and live responses`; delayed dialog and Join browser checks | `7f72770`, `ffa0901` |
| SEC-06 removed Outlook source republished by a stale read | Remediated | `OutlookConcurrencyTests.SourceRemovalAtEveryReadStageCannotPublishOrEnterCache` | `7f72770` |
| SEC-07 mutable username identity design | Hardened | `MicrosoftAccountStateTests.ImmutableKeyIgnoresUsernameButSeparatesPrincipalsTenantsAndClients` and migration/replacement tests | `4196f4b`, `e832dcd`, `8a3061b` |
| SEC-08 elevated installer executes user-writable code | Remediated in source | installer elevation assertions, real control-pipe shutdown tests, immutable embedded stop-tooling release tests | `af8be12`, `f03bafc` |
| SEC-09 stale build output enters a release | Remediated | release sentinel, exact inventory, immutable archive, snapshot, and hostile descriptor tests | `dbee484`, `83c78c2`, `ce21eb1`, `fd76874`, `2c755f4` |

## Additional defects and hardening

| Item | Source status | Positive regression evidence | Primary commits |
| --- | --- | --- | --- |
| Repeated Planner notes save erased the description | Remediated | Planner `saving unchanged notes repeatedly preserves the normalized description` | `422dd1f` |
| Details and completion bypassed selected-board scope | Remediated | `TaskDetailsServiceTests.GetAsync_RejectsTaskOutsideSelectedPlanBeforeReadingDetails`, `TaskCompletionServiceTests.CompleteAsync_RejectsTaskOutsideSelectedPlanBeforeMutation` | `422dd1f`, `d47bc59` |
| Planner pagination trusted unsafe continuation links | Remediated | hostile host/path, loop, page-limit, record-limit, and redirect tests in `GraphClientTests` | `422dd1f` |
| Setup framing, response caching, and sensitive response policy | Remediated | security-boundary header tests across setup, API, and widget preview responses | `4fa44dd` |
| Routine security and dependency automation absent | Remediated | root verification contract, Windows security workflow, Dependabot, NuGet/npm audit, tracked-history scanner | `9a93bf7`, `54dbe38` |

## Final whole-branch fixes

The final review remained open until the following regressions and the complete verification gate passed on 2026-09-23. These statuses describe repository behavior only; they do not replace the pending external checks below.

| Review item | Source status | Permanent regression evidence | Implementation commit |
| --- | --- | --- | --- |
| I-1 production board-selection/account lock inversion | Remediated | `PlannerOperationLockTests.Production_settings_store_and_member_publication_use_account_lifecycle_selection_order` for capture and change | `89971df` |
| I-2 inherited nested account work escaped the outer gate | Remediated | `PlannerOperationLockTests.Child_that_enters_before_parent_completion_cannot_outlive_the_account_gate` | `89971df` |
| I-3 response backpressure held account/lifecycle gates | Remediated | `AccountBoundResultTests` blocked-client Planner and Outlook tests plus the 4 MiB cap; owning suites 101/101 | `7c1a09e` |
| I-4 hostile bootstrap input reached identity work without bounded throttling | Remediated | `OwnerBootstrapEndpointTests.Invalid_bootstrap_flood_is_throttled_without_identity_work_or_starving_a_valid_exchange`; owning suites 70/70 | `4e15f5f` |
| M-1 Host policy accepted IPv4 loopback aliases | Remediated | `LoopbackRequestPolicyTests` accepts only `localhost`, `127.0.0.1`, and `::1` forms and rejects `127.0.0.2` | `0d220db` |
| M-2 recovery paths ignored configured helper port | Remediated | `UpdateTests.Configured_helper_port_drives_setup_tray_and_update_recovery_addresses` at port 9123; owning suites 43/43 | `0d220db` |
| M-3 final remediation evidence preceded final fixes | Remediated in this appendix | final statuses were recorded only after the focused, full, audit, secret-scan, and clean-release gates passed | this documentation commit |
| M-4 authoritative-range whitespace failure | Remediated | `git diff --check 46770b1..HEAD` passes | `38e85b9` |

## Operational status

- `scripts/verify.ps1` passed on 2026-09-23: helper .NET 1525/1525, helper JavaScript 16/16, Planner 100/100, and Outlook 29/29 (1670 enumerated tests), plus desktop/mobile setup-browser and release-security checks. Both npm audits reported zero vulnerabilities; NuGet direct and transitive package queries reported no vulnerable packages. Both npm roots fail on every low-or-higher advisory (`audit-level=low`), while NuGet restore promotes NU1901-NU1904 advisory warnings to errors.
- `scripts/scan-secrets.ps1` passed the working-tree scan and all 136 tracked revisions. This is a pattern scan, not a claim that external providers or repository hosting found no secrets.
- `scripts/build-release.ps1` completed from randomized clean staging without publishing or installing. It verified Planner and Outlook archives (9 and 8 entries), both 83-entry helper archives, and an exact eight-file release snapshot at `release-85ec79bd4b14956f7d0f8645291514b20e6ad1783a3b739143faff8bc250bdf6`.
- The release consumer accepts exactly eight top-level files: one versioned installer, Planner widget, Outlook widget, portable helper archive, `INSTALL.md`, `OUTLOOK.md`, `RELEASE-MANIFEST.json`, and `SHA256SUMS.txt`. Only the three expected archives may describe internal contents.
- Manual installed-app verification is pending. This worktree does not safely prove tray-opened setup, native Planner and Outlook pairing in iCUE, elevated setup/uninstall refusal, named-pipe ACL behavior in the installed environment, post-upgrade re-pairing, or real iCUE behavior without changing installed applications.
- Real Microsoft tenant, account-transition, and Graph behavior remain pending because verification deliberately used test doubles and did not access a real account or tenant.
- Hosted CI has not run for these commits. Hosted GitHub controls, maintainer MFA, tenant policy, and published binary provenance remain outside this repository re-audit.

## Deferred residual risk

Installer and replacement artifacts remain unsigned by deliberate scope decision. Repository-controlled SHA-256 manifests protect integrity after trusted publication, but they do not authenticate a publisher or protect against compromise of the release account. Code signing and release-account hardening remain future work; this remediation does not claim otherwise.
