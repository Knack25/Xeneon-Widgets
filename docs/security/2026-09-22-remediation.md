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
| Routine security and dependency automation absent | Remediated | root verification contract, Windows security workflow, Dependabot, NuGet/npm audit, tracked-history scanner | Task 11 automation commit |

## Operational status

- Automated verification covers the helper .NET suite; Planner and Outlook widget suites; setup-browser, Outlook setup, helper API, and update UI tests; release-security checks; dependency audits; and tracked-history secret-pattern scanning.
- The release consumer accepts exactly eight top-level files: one versioned installer, Planner widget, Outlook widget, portable helper archive, `INSTALL.md`, `OUTLOOK.md`, `RELEASE-MANIFEST.json`, and `SHA256SUMS.txt`. Only the three expected archives may describe internal contents.
- Manual installed-app verification is pending. This worktree does not safely prove tray-opened setup, native Planner and Outlook pairing in iCUE, elevated setup/uninstall refusal, or post-upgrade re-pairing without interacting with installed applications or a real Microsoft account.
- Hosted GitHub controls, maintainer MFA, tenant policy, and published binary provenance remain outside this repository re-audit.

## Deferred residual risk

Installer and replacement artifacts remain unsigned by deliberate scope decision. Repository-controlled SHA-256 manifests protect integrity after trusted publication, but they do not authenticate a publisher or protect against compromise of the release account. Code signing and release-account hardening remain future work; this remediation does not claim otherwise.
