param([switch]$SkipOutlookBuild)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
$releaseSafety = Join-Path $repositoryRoot 'scripts\release-safety.ps1'
$installer = Join-Path $repositoryRoot 'microsoft-widgets-helper\installer\MicrosoftWidgets.iss'
$stopScript = Join-Path $repositoryRoot 'microsoft-widgets-helper\installer\Stop-MicrosoftWidgetsHelper.ps1'
$releaseResolver = Join-Path $repositoryRoot 'scripts\resolve-release.ps1'
$rootVerifier = Join-Path $repositoryRoot 'scripts\verify.ps1'
$securityWorkflow = Join-Path $repositoryRoot '.github\workflows\security.yml'
$dependabot = Join-Path $repositoryRoot '.github\dependabot.yml'
$secretScanner = Join-Path $repositoryRoot 'scripts\scan-secrets.ps1'
$dependencyAuditor = Join-Path $repositoryRoot 'scripts\audit-dependencies.ps1'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

Assert-True (Test-Path -LiteralPath $verifier -PathType Leaf) 'Release inventory verifier is missing.'
Assert-True (Test-Path -LiteralPath $releaseSafety -PathType Leaf) 'Safe release-stage cleanup helpers are missing.'
. $releaseSafety
Assert-True ($null -ne (Get-Command New-ReleaseWorkspace -ErrorAction SilentlyContinue)) 'Fresh release workspace helper is missing.'
Assert-True ($null -ne (Get-Command Open-ReleaseSnapshot -ErrorAction SilentlyContinue)) 'Immutable release snapshot helper is missing.'
Assert-True ($null -ne (Get-Command Assert-ReleaseArchive -ErrorAction SilentlyContinue)) 'Archive verifier is missing.'
Assert-True ($null -ne (Get-Command Publish-ReleaseFile -ErrorAction SilentlyContinue)) 'Atomic release publisher is missing.'
Assert-True ($null -ne (Get-Command Publish-VerifiedReleaseArchive -ErrorAction SilentlyContinue)) 'Verified archive publisher is missing.'
Assert-True ($null -ne (Get-Command Close-VerifiedReleaseArchive -ErrorAction SilentlyContinue)) 'Verified archive lease cleanup is missing.'
Assert-True ($null -ne (Get-Command New-InnoFileManifest -ErrorAction SilentlyContinue)) 'Exact Inno file manifest generator is missing.'
Assert-True ($null -ne (Get-Command Close-InnoFileManifest -ErrorAction SilentlyContinue)) 'Exact Inno file manifest lease cleanup is missing.'
Assert-True ($null -ne (Get-Command Publish-ReleaseDirectory -ErrorAction SilentlyContinue)) 'Atomic release-directory publisher is missing.'
Assert-True ($null -ne (Get-Command Open-VerifiedReleaseDirectory -ErrorAction SilentlyContinue)) 'Verified release-directory opener is missing.'
Assert-True ($null -ne (Get-Command Publish-VerifiedReleaseSnapshot -ErrorAction SilentlyContinue)) 'Verified content-addressed release publisher is missing.'
Assert-True ($null -ne (Get-Command Resolve-VerifiedReleaseSnapshot -ErrorAction SilentlyContinue)) 'Verified release consumer resolver is missing.'
Assert-True ($null -ne (Get-Command Resolve-VerifiedReleaseDescriptor -ErrorAction SilentlyContinue)) 'Descriptor-backed release resolver is missing.'
Assert-True (Test-Path -LiteralPath $releaseResolver -PathType Leaf) 'Verified release resolver command is missing.'
Assert-True (Test-Path -LiteralPath $rootVerifier -PathType Leaf) 'Root security verification command is missing.'

$rootVerifierSource = Get-Content -Raw -LiteralPath $rootVerifier
$helperVerifierSource = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'microsoft-widgets-helper\scripts\verify.ps1')
$plannerVerifierSource = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'planner-edge-widget\scripts\verify.ps1')
Assert-True ($rootVerifierSource -match 'planner-edge-widget[\\/]scripts[\\/]verify\.ps1') 'Root verification must invoke the combined widget verifier.'
foreach ($requiredHelperSuite in @('setup-browser\.mjs', 'outlook-setup\.test\.mjs', 'helper-api\.test\.mjs', 'update-ui\.test\.mjs', 'release-security\.test\.ps1')) {
    Assert-True ($helperVerifierSource -match $requiredHelperSuite) "Helper verification does not invoke required security suite: $requiredHelperSuite"
}
Assert-True ($plannerVerifierSource -match 'microsoft-widgets-helper[\\/]scripts[\\/]verify\.ps1') 'Combined widget verification must invoke the complete helper verifier.'
Assert-True ($plannerVerifierSource -match 'outlook-edge-widget') 'Combined widget verification does not invoke the Outlook suite.'
Assert-True (Test-Path -LiteralPath $securityWorkflow -PathType Leaf) 'Security CI workflow is missing.'
Assert-True (Test-Path -LiteralPath $dependabot -PathType Leaf) 'Dependabot configuration is missing.'
Assert-True (Test-Path -LiteralPath $secretScanner -PathType Leaf) 'Tracked-history secret scanner is missing.'
Assert-True (Test-Path -LiteralPath $dependencyAuditor -PathType Leaf) 'Fail-closed dependency auditor is missing.'
$securityWorkflowSource = Get-Content -Raw -LiteralPath $securityWorkflow
$dependabotSource = Get-Content -Raw -LiteralPath $dependabot
$dependencyAuditorSource = Get-Content -Raw -LiteralPath $dependencyAuditor
Assert-True ($securityWorkflowSource -match 'permissions:\s*\r?\n\s*contents:\s*read') 'Security CI must grant only read access to repository contents.'
Assert-True ($securityWorkflowSource -match 'runs-on:\s*windows-latest') 'Security CI must run on Windows.'
Assert-True (($securityWorkflowSource | Select-String -AllMatches 'npm ci --ignore-scripts').Matches.Count -eq 2) 'Security CI must use locked installs for both npm projects.'
Assert-True ($securityWorkflowSource -match 'scripts[/\\]verify\.ps1' -and $securityWorkflowSource -match 'scripts[/\\]scan-secrets\.ps1') 'Security CI must run complete verification and tracked-history secret scanning.'
Assert-True ($rootVerifierSource -match 'audit-dependencies\.ps1') 'Root verification must run the same fail-closed dependency audits as CI.'
Assert-True (($dependencyAuditorSource | Select-String -AllMatches 'npm\s+audit\s+--audit-level=low').Matches.Count -eq 2) 'Dependency auditing must fail on every known npm advisory in both npm roots.'
Assert-True ($dependencyAuditorSource -match 'planner-edge-widget' -and $dependencyAuditorSource -match 'outlook-edge-widget') 'Dependency auditing must cover both npm roots.'
Assert-True ($dependencyAuditorSource -match 'NuGetAuditMode=all' -and $dependencyAuditorSource -match 'warnaserror:NU1901,NU1902,NU1903,NU1904') 'NuGet advisory warnings NU1901-NU1904 must fail dependency auditing.'
Assert-True ($dependencyAuditorSource -match 'package\s+--vulnerable\s+--include-transitive') 'Dependency auditing must query direct and transitive NuGet advisories.'
Assert-True (($dependencyAuditorSource | Select-String -AllMatches '\$LASTEXITCODE\s+-ne\s+0').Matches.Count -ge 4) 'Every native dependency-audit command must preserve a nonzero exit.'
$firstLockedInstall = $securityWorkflowSource.IndexOf('npm ci --ignore-scripts', [StringComparison]::Ordinal)
$rootVerification = $securityWorkflowSource.IndexOf('./scripts/verify.ps1', [StringComparison]::Ordinal)
Assert-True ($firstLockedInstall -ge 0 -and $rootVerification -gt $firstLockedInstall) 'CI must run fail-closed dependency auditing after locked npm installs.'
foreach ($ecosystem in @('npm', 'nuget', 'github-actions')) {
    Assert-True ($dependabotSource -match "package-ecosystem:\s*'$ecosystem'") "Dependabot does not cover $ecosystem."
}
foreach ($directory in @('/planner-edge-widget/widget', '/outlook-edge-widget')) {
    Assert-True ($dependabotSource -match [regex]::Escape("directory: '$directory'")) "Dependabot does not cover npm root $directory."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("MicrosoftWidgetsReleaseSecurity-" + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $testRoot 'stage'
$sibling = Join-Path $testRoot 'sibling'
try {
    New-Item -ItemType Directory -Path $stage, $sibling -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $stage 'allowed.txt') -Value 'reviewed'
    $manifest = ConvertTo-Json -InputObject @('allowed.txt') -Compress
    & $verifier -Stage $stage -Manifest $manifest

    Set-Content -LiteralPath (Join-Path $stage 'unexpected.txt') -Value 'must-not-ship'
    Assert-Fails { & $verifier -Stage $stage -Manifest $manifest } 'Unexpected files must fail inventory verification.'
    Remove-Item -LiteralPath (Join-Path $stage 'unexpected.txt') -Force

    Assert-Fails { & $verifier -Stage $stage -Manifest (ConvertTo-Json -InputObject @('missing.txt') -Compress) } 'Missing files must fail inventory verification.'
    Set-Content -LiteralPath (Join-Path $sibling 'escape.txt') -Value 'outside-stage'
    Assert-Fails { & $verifier -Stage $stage -Manifest (ConvertTo-Json -InputObject @('..\sibling\escape.txt') -Compress) } 'Sibling escapes must fail inventory verification.'

    $junction = Join-Path $stage 'linked'
    New-Item -ItemType Junction -Path $junction -Target $sibling | Out-Null
    Assert-Fails { & $verifier -Stage $stage -Manifest $manifest } 'Junctions and reparse points must fail inventory verification.'

    $workspaceRoot = Join-Path $testRoot 'workspaces'
    New-Item -ItemType Directory -Path $workspaceRoot | Out-Null
    $firstWorkspace = New-ReleaseWorkspace -Parent $workspaceRoot -Prefix 'package'
    $secondWorkspace = New-ReleaseWorkspace -Parent $workspaceRoot -Prefix 'package'
    Assert-True ($firstWorkspace -ne $secondWorkspace) 'Release stages must never be reused.'
    Set-Content -LiteralPath (Join-Path $firstWorkspace 'sentinel.txt') -Value 'first-build'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $secondWorkspace 'sentinel.txt'))) 'A fresh build inherited stale stage content.'

    $attackerTarget = Join-Path $testRoot 'attacker-target'
    New-Item -ItemType Directory -Path $attackerTarget | Out-Null
    Set-Content -LiteralPath (Join-Path $attackerTarget 'keep.txt') -Value 'outside'
    $predictableStage = Join-Path $workspaceRoot 'package'
    New-Item -ItemType Junction -Path $predictableStage -Target $attackerTarget | Out-Null
    $thirdWorkspace = New-ReleaseWorkspace -Parent $workspaceRoot -Prefix 'package'
    Assert-True ($thirdWorkspace -ne $predictableStage) 'Fresh staging must not reuse a predictable replaced path.'
    Assert-True (Test-Path -LiteralPath (Join-Path $attackerTarget 'keep.txt')) 'Workspace creation must not clean an attacker-controlled junction.'

    $publishRoot = Join-Path $testRoot 'published'
    New-Item -ItemType Directory -Path $publishRoot | Out-Null
    $archiveStage = New-ReleaseWorkspace -Parent $workspaceRoot -Prefix 'archive'
    Set-Content -NoNewline -LiteralPath (Join-Path $archiveStage 'allowed.txt') -Value 'reviewed'
    New-Item -ItemType Directory -Path (Join-Path $archiveStage 'nested') | Out-Null
    Set-Content -NoNewline -LiteralPath (Join-Path $archiveStage 'nested/data.txt') -Value 'data'
    $archiveManifest = @('allowed.txt', 'nested/data.txt')
    $snapshot = Open-ReleaseSnapshot -Stage $archiveStage -Manifest $archiveManifest
    try {
        Assert-Fails { Set-Content -LiteralPath (Join-Path $archiveStage 'allowed.txt') -Value 'tampered' } 'An immutable snapshot must deny stage mutation while packaging.'
        $validArchive = Join-Path $firstWorkspace 'valid.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($archiveStage, $validArchive)
        Assert-ReleaseArchive -Archive $validArchive -Snapshot $snapshot

        function New-TestArchive([string]$Path, [scriptblock]$Populate) {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
                try { & $Populate $zip } finally { $zip.Dispose() }
            } finally { $stream.Dispose() }
        }
        function Add-TestEntry($Zip, [string]$Name, [string]$Value, [int]$ExternalAttributes = 0) {
            $entry = $Zip.CreateEntry($Name)
            $entry.ExternalAttributes = $ExternalAttributes
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($Value) } finally { $writer.Dispose() }
        }

        $duplicateArchive = Join-Path $firstWorkspace 'duplicate.zip'
        New-TestArchive $duplicateArchive { param($zip) Add-TestEntry $zip 'allowed.txt' 'reviewed'; Add-TestEntry $zip 'ALLOWED.TXT' 'reviewed'; Add-TestEntry $zip 'nested/data.txt' 'data' }
        Assert-Fails { Assert-ReleaseArchive -Archive $duplicateArchive -Snapshot $snapshot } 'Archive verification must reject duplicate or case-colliding entries.'

        foreach ($unsafeName in @('../allowed.txt', '/allowed.txt', 'C:/allowed.txt', 'nested\data.txt')) {
            $unsafeArchive = Join-Path $firstWorkspace (([Guid]::NewGuid().ToString('N')) + '.zip')
            New-TestArchive $unsafeArchive { param($zip) Add-TestEntry $zip $unsafeName 'reviewed'; Add-TestEntry $zip 'nested/data.txt' 'data' }
            Assert-Fails { Assert-ReleaseArchive -Archive $unsafeArchive -Snapshot $snapshot } "Archive verification must reject unsafe entry $unsafeName."
        }

        $linkArchive = Join-Path $firstWorkspace 'link.zip'
        New-TestArchive $linkArchive { param($zip) Add-TestEntry $zip 'allowed.txt' 'reviewed' ([int](0xA000 -shl 16)); Add-TestEntry $zip 'nested/data.txt' 'data' }
        Assert-Fails { Assert-ReleaseArchive -Archive $linkArchive -Snapshot $snapshot } 'Archive verification must reject link entries.'

        $mismatchArchive = Join-Path $firstWorkspace 'mismatch.zip'
        New-TestArchive $mismatchArchive { param($zip) Add-TestEntry $zip 'allowed.txt' 'changed!'; Add-TestEntry $zip 'nested/data.txt' 'data' }
        Assert-Fails { Assert-ReleaseArchive -Archive $mismatchArchive -Snapshot $snapshot } 'Archive verification must reject staged-file hash mismatches.'
        Assert-Fails { Assert-ReleaseArchive -Archive $validArchive -Snapshot $snapshot -MaxEntryBytes 3 } 'Archive verification must enforce per-entry size limits.'
        Assert-Fails { Assert-ReleaseArchive -Archive $validArchive -Snapshot $snapshot -MaxTotalBytes 10 } 'Archive verification must enforce total size limits.'

        $handoffArchive = Join-Path $firstWorkspace 'handoff.zip'
        Copy-Item -LiteralPath $validArchive -Destination $handoffArchive
        $handoffTarget = Join-Path $publishRoot 'handoff.zip'
        Assert-Fails {
            Publish-VerifiedReleaseArchive -Source $handoffArchive -Destination $handoffTarget -TrustedParent $publishRoot -Snapshot $snapshot -AfterMoveProbe {
                Copy-Item -LiteralPath $mismatchArchive -Destination $handoffTarget -Force
            }
        } 'Replacing an archive during the publish handoff must be detected at the destination.'

        $lockedArchive = Join-Path $firstWorkspace 'locked.zip'
        Copy-Item -LiteralPath $validArchive -Destination $lockedArchive
        $lockedTarget = Join-Path $publishRoot 'locked.zip'
        $replacementDenied = $false
        $lease = Publish-VerifiedReleaseArchive -Source $lockedArchive -Destination $lockedTarget -TrustedParent $publishRoot -Snapshot $snapshot -WhileLockedProbe {
            try { Copy-Item -LiteralPath $mismatchArchive -Destination $lockedTarget -Force }
            catch { $script:replacementDenied = $true }
        }
        try {
            Assert-True $replacementDenied 'A verified destination must deny write/delete replacement while its lease is held.'
            Assert-Fails { Remove-Item -LiteralPath $lockedTarget -Force } 'A verified destination must remain delete-locked for parent consumption.'
        } finally {
            Close-VerifiedReleaseArchive $lease
        }

        $innoManifestPath = Join-Path $firstWorkspace 'helper-files.iss'
        $innoLease = New-InnoFileManifest -Snapshot $snapshot -Output $innoManifestPath -TemporaryEntries @('allowed.txt')
        try {
            Assert-Fails { Set-Content -LiteralPath $innoManifestPath -Value 'Source: "late-extra.dll"' } 'The generated Inno include must stay immutable through compilation.'
            Set-Content -LiteralPath (Join-Path $archiveStage 'late-extra.dll') -Value 'must-not-ship'
            $innoManifest = Get-Content -Raw -LiteralPath $innoManifestPath
            Assert-True ($innoManifest -match [regex]::Escape('allowed.txt')) 'Generated Inno manifest omitted an expected root file.'
            Assert-True ($innoManifest -match [regex]::Escape('nested\data.txt')) 'Generated Inno manifest omitted an expected nested file.'
            Assert-True ($innoManifest -notmatch 'late-extra') 'A file injected after manifest generation must not enter the installer file list.'
            Assert-True ($innoManifest -notmatch '\\\*') 'Generated Inno manifest must not contain recursive wildcards.'
            Assert-True ($innoManifest -match 'allowed\.txt[^\r\n]*dontcopy') 'Temporary installer files must come from the same immutable generated manifest.'
        } finally {
            Close-InnoFileManifest $innoLease
        }
    } finally {
        Close-ReleaseSnapshot $snapshot
    }

    $publishSource = Join-Path $secondWorkspace 'artifact.zip'
    Set-Content -NoNewline -LiteralPath $publishSource -Value 'new'
    $publishTarget = Join-Path $publishRoot 'artifact.zip'
    Set-Content -NoNewline -LiteralPath $publishTarget -Value 'old'
    Publish-ReleaseFile -Source $publishSource -Destination $publishTarget -TrustedParent $publishRoot
    Assert-True ((Get-Content -Raw -LiteralPath $publishTarget) -eq 'new') 'Atomic publication did not replace the prior artifact.'

    $oldRelease = Join-Path $publishRoot 'release'
    New-Item -ItemType Directory -Path $oldRelease | Out-Null
    Set-Content -LiteralPath (Join-Path $oldRelease 'old.txt') -Value 'preserve-in-quarantine'
    $newRelease = New-ReleaseWorkspace -Parent $publishRoot -Prefix 'release-candidate'
    Set-Content -LiteralPath (Join-Path $newRelease 'new.txt') -Value 'publish'
    $published = Publish-ReleaseDirectory -Source $newRelease -Destination $oldRelease -TrustedParent $publishRoot
    Assert-True ($published -eq $oldRelease) 'Release directory was not published to the stable path.'
    Assert-True (Test-Path -LiteralPath (Join-Path $oldRelease 'new.txt')) 'New release directory was not published.'
    $quarantines = @(Get-ChildItem -LiteralPath $publishRoot -Directory -Filter 'release-quarantine-*')
    Assert-True ($quarantines.Count -eq 1) 'Prior release directory was not preserved in a unique quarantine.'
    Assert-True (Test-Path -LiteralPath (Join-Path $quarantines[0].FullName 'old.txt')) 'Prior release content was recursively deleted instead of quarantined.'

    $snapshotRoot = Join-Path $publishRoot 'release-snapshots'
    New-Item -ItemType Directory -Path $snapshotRoot | Out-Null
    $currentPointer = Join-Path $publishRoot 'release-current.txt'
    Set-Content -NoNewline -LiteralPath $currentPointer -Value 'release-previous'
    $releaseCandidate = New-ReleaseWorkspace -Parent $publishRoot -Prefix 'verified-candidate'
    $releaseManifest = @(
        'MicrosoftWidgetsSetup-test.exe',
        'PlannerEdgeWidget-test.icuewidget',
        'OutlookEdgeWidget-test.icuewidget',
        'MicrosoftWidgetsHelper-test-portable-win-x64.zip',
        'INSTALL.md',
        'OUTLOOK.md',
        'SHA256SUMS.txt'
    )
    foreach ($name in $releaseManifest) {
        Set-Content -NoNewline -LiteralPath (Join-Path $releaseCandidate $name) -Value "trusted:$name"
    }
    $releaseSnapshot = Open-ReleaseSnapshot -Stage $releaseCandidate -Manifest $releaseManifest -AllowDeleteShare
    try {
        $preActivationDenied = $false
        $postActivationDenied = $false
        $postValidationDenied = $false
        $lease = Publish-VerifiedReleaseSnapshot -Source $releaseCandidate -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $publishRoot -Snapshot $releaseSnapshot `
            -AfterSnapshotValidationProbe {
                param($snapshotPath, $pointerPath)
                try { Set-Content -LiteralPath (Join-Path $snapshotPath 'INSTALL.md') -Value 'pre-activation-tamper' }
                catch { $script:preActivationDenied = $true }
                Assert-True ((Get-Content -Raw -LiteralPath $pointerPath) -eq 'release-previous') 'An unverified snapshot became current before activation.'
            } `
            -AfterActivationProbe {
                param($snapshotPath, $pointerPath)
                try { Set-Content -LiteralPath $pointerPath -Value 'release-attacker' }
                catch { $script:postActivationDenied = $true }
            } `
            -AfterFinalValidationProbe {
                param($snapshotPath, $pointerPath)
                try { Remove-Item -LiteralPath (Join-Path $snapshotPath 'INSTALL.md') -Force }
                catch { $script:postValidationDenied = $true }
            }
        try {
            Assert-True $preActivationDenied 'A validated snapshot must deny tampering before activation.'
            Assert-True $postActivationDenied 'The activated pointer must deny tampering before final validation.'
            Assert-True $postValidationDenied 'The published snapshot must remain locked after final validation while the build runs.'
            Assert-True ($lease.SnapshotName -match '^release-[0-9a-f]{64}$') 'Published snapshots must use a content-addressed name.'
            Assert-True ((Get-Content -Raw -LiteralPath $currentPointer) -eq $lease.SnapshotName) 'The current pointer does not identify the exact verified snapshot.'
            Assert-True ($lease.Snapshot.Stage -eq (Join-Path $snapshotRoot $lease.SnapshotName)) 'The returned release path is not the activated content-addressed snapshot.'

            $resolved = Resolve-VerifiedReleaseSnapshot -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $publishRoot -Manifest $releaseManifest
            try {
                Assert-True ($resolved.Snapshot.Stage -eq $lease.Snapshot.Stage) 'Resolver did not return the activated verified snapshot.'
            } finally { Close-VerifiedReleaseSnapshot $resolved }

            Assert-Fails { Set-Content -LiteralPath (Join-Path $lease.Snapshot.Stage 'INSTALL.md') -Value 'consumer-tamper' } 'The active build lease unexpectedly allowed snapshot tampering.'
            $activeSnapshotPath = $lease.Snapshot.Stage
        } finally {
            Close-VerifiedReleaseSnapshot $lease
        }

        $originalPointer = Get-Content -Raw -LiteralPath $currentPointer
        Set-Content -NoNewline -LiteralPath $currentPointer -Value '..\\release-attacker'
        Assert-Fails {
            $bad = Resolve-VerifiedReleaseSnapshot -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $publishRoot -Manifest $releaseManifest
            if ($null -ne $bad) { Close-VerifiedReleaseSnapshot $bad }
        } 'Resolver must reject a pointer that escapes the snapshot root.'
        Set-Content -NoNewline -LiteralPath $currentPointer -Value $originalPointer

        $installPath = Join-Path $activeSnapshotPath 'INSTALL.md'
        $originalInstall = Get-Content -Raw -LiteralPath $installPath
        Set-Content -NoNewline -LiteralPath $installPath -Value 'consumer-tamper'
        Assert-Fails {
            $bad = Resolve-VerifiedReleaseSnapshot -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $publishRoot -Manifest $releaseManifest
            if ($null -ne $bad) { Close-VerifiedReleaseSnapshot $bad }
        } 'Resolver must reject a snapshot whose content-addressed identity no longer matches.'
        Set-Content -NoNewline -LiteralPath $installPath -Value $originalInstall

        $previousPointer = Get-Content -Raw -LiteralPath $currentPointer
        $failedCandidate = New-ReleaseWorkspace -Parent $publishRoot -Prefix 'failed-candidate'
        foreach ($name in $releaseManifest) {
            Set-Content -NoNewline -LiteralPath (Join-Path $failedCandidate $name) -Value "replacement:$name"
        }
        $failedSnapshot = Open-ReleaseSnapshot -Stage $failedCandidate -Manifest $releaseManifest -AllowDeleteShare
        try {
            Assert-Fails {
                $failedLease = Publish-VerifiedReleaseSnapshot -Source $failedCandidate -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $publishRoot -Snapshot $failedSnapshot -AfterActivationProbe {
                    throw 'simulated final-validation race'
                }
                if ($null -ne $failedLease) { Close-VerifiedReleaseSnapshot $failedLease }
            } 'A failure after activation must fail the release build.'
            Assert-True ((Get-Content -Raw -LiteralPath $currentPointer) -eq $previousPointer) 'A failed activation did not restore the prior verified pointer.'
            $failedQuarantines = @(Get-ChildItem -LiteralPath $snapshotRoot -Directory -Filter 'release-quarantine-*')
            Assert-True ($failedQuarantines.Count -eq 1) 'A snapshot that failed after activation was not quarantined.'
        } finally {
            Close-ReleaseSnapshot $failedSnapshot
        }
    } finally {
        Close-ReleaseSnapshot $releaseSnapshot
    }

    $unexpectedConsumerRoot = Join-Path $testRoot 'unexpected-consumer-release'
    $unexpectedConsumerSnapshots = Join-Path $unexpectedConsumerRoot 'release-snapshots'
    New-Item -ItemType Directory -Path $unexpectedConsumerRoot, $unexpectedConsumerSnapshots | Out-Null
    $unexpectedConsumerCandidate = New-ReleaseWorkspace -Parent $unexpectedConsumerRoot -Prefix 'consumer-candidate'
    Set-Content -NoNewline -LiteralPath (Join-Path $unexpectedConsumerCandidate 'unexpected.bin') -Value 'self-consistent but unreviewed'
    $unexpectedConsumerFiles = @(
        [ordered]@{
            name = 'unexpected.bin'
            length = (Get-Item -LiteralPath (Join-Path $unexpectedConsumerCandidate 'unexpected.bin')).Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $unexpectedConsumerCandidate 'unexpected.bin')).Hash.ToLowerInvariant()
        }
    )
    [ordered]@{ schemaVersion = 1; files = $unexpectedConsumerFiles } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $unexpectedConsumerCandidate 'RELEASE-MANIFEST.json') -Encoding utf8
    @('unexpected.bin', 'RELEASE-MANIFEST.json' | ForEach-Object {
        "$(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $unexpectedConsumerCandidate $_) | Select-Object -ExpandProperty Hash)  $_".ToLowerInvariant()
    }) | Set-Content -LiteralPath (Join-Path $unexpectedConsumerCandidate 'SHA256SUMS.txt') -Encoding ascii
    $unexpectedConsumerManifest = @('unexpected.bin', 'RELEASE-MANIFEST.json', 'SHA256SUMS.txt')
    $unexpectedConsumerSnapshot = Open-ReleaseSnapshot -Stage $unexpectedConsumerCandidate -Manifest $unexpectedConsumerManifest -AllowDeleteShare
    $unexpectedConsumerLease = Publish-VerifiedReleaseSnapshot -Source $unexpectedConsumerCandidate -SnapshotRoot $unexpectedConsumerSnapshots -CurrentPointer (Join-Path $unexpectedConsumerRoot 'release-current.txt') -TrustedParent $unexpectedConsumerRoot -Snapshot $unexpectedConsumerSnapshot
    Close-VerifiedReleaseSnapshot $unexpectedConsumerLease
    Close-ReleaseSnapshot $unexpectedConsumerSnapshot
    Assert-Fails { & $releaseResolver -ReleaseRoot $unexpectedConsumerRoot } 'Resolver must reject a self-consistent release with an unexpected top-level file.'

    $consumerRoot = Join-Path $testRoot 'consumer-release'
    $consumerSnapshots = Join-Path $consumerRoot 'release-snapshots'
    New-Item -ItemType Directory -Path $consumerRoot, $consumerSnapshots | Out-Null
    $consumerArchiveStage = New-ReleaseWorkspace -Parent $workspaceRoot -Prefix 'consumer-archive'
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerArchiveStage 'payload.txt') -Value 'payload'
    $consumerArchiveSnapshot = Open-ReleaseSnapshot -Stage $consumerArchiveStage -Manifest @('payload.txt')
    $consumerCandidate = New-ReleaseWorkspace -Parent $consumerRoot -Prefix 'consumer-candidate'
    $consumerArchiveNames = @(
        'PlannerEdgeWidget-0.3.0.icuewidget',
        'OutlookEdgeWidget-0.2.0.icuewidget',
        'MicrosoftWidgetsHelper-0.1.4-portable-win-x64.zip'
    )
    foreach ($archiveName in $consumerArchiveNames) {
        [IO.Compression.ZipFile]::CreateFromDirectory($consumerArchiveStage, (Join-Path $consumerCandidate $archiveName))
    }
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerCandidate 'MicrosoftWidgetsSetup-0.3.3.exe') -Value 'installer'
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerCandidate 'INSTALL.md') -Value 'install'
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerCandidate 'OUTLOOK.md') -Value 'outlook'
    $consumerAssetNames = @('MicrosoftWidgetsSetup-0.3.3.exe') + $consumerArchiveNames + @('INSTALL.md', 'OUTLOOK.md')
    $consumerFiles = @($consumerAssetNames | ForEach-Object {
        $assetName = $_
        $assetPath = Join-Path $consumerCandidate $assetName
        $record = [ordered]@{
            name = $assetName
            length = (Get-Item -LiteralPath $assetPath).Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $assetPath).Hash.ToLowerInvariant()
        }
        if ($assetName -in $consumerArchiveNames) {
            $record.archiveEntries = @($consumerArchiveSnapshot.Entries | ForEach-Object { [ordered]@{ name = $_.Name; length = $_.Length; sha256 = $_.Hash } })
        }
        $record
    })
    [ordered]@{ schemaVersion = 1; files = $consumerFiles } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $consumerCandidate 'RELEASE-MANIFEST.json') -Encoding utf8
    $consumerChecksumTargets = @($consumerAssetNames) + 'RELEASE-MANIFEST.json'
    @($consumerChecksumTargets | ForEach-Object {
        "$(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $consumerCandidate $_) | Select-Object -ExpandProperty Hash)  $_".ToLowerInvariant()
    }) | Set-Content -LiteralPath (Join-Path $consumerCandidate 'SHA256SUMS.txt') -Encoding ascii
    $consumerManifest = @($consumerChecksumTargets) + 'SHA256SUMS.txt'
    $consumerCandidateSnapshot = Open-ReleaseSnapshot -Stage $consumerCandidate -Manifest $consumerManifest -AllowDeleteShare
    $consumerLease = Publish-VerifiedReleaseSnapshot -Source $consumerCandidate -SnapshotRoot $consumerSnapshots -CurrentPointer (Join-Path $consumerRoot 'release-current.txt') -TrustedParent $consumerRoot -Snapshot $consumerCandidateSnapshot
    $consumerSnapshotName = $consumerLease.SnapshotName
    Close-VerifiedReleaseSnapshot $consumerLease
    Close-ReleaseSnapshot $consumerCandidateSnapshot
    Close-ReleaseSnapshot $consumerArchiveSnapshot

    $resolvedPath = & $releaseResolver -ReleaseRoot $consumerRoot
    Assert-True ($resolvedPath -eq (Join-Path $consumerSnapshots $consumerSnapshotName)) 'Standalone resolver did not return the verified active snapshot.'
    $unexpectedDirectory = Join-Path $resolvedPath 'unexpected-empty-directory'
    New-Item -ItemType Directory -Path $unexpectedDirectory | Out-Null
    Assert-Fails { & $releaseResolver -ReleaseRoot $consumerRoot } 'Standalone resolver must reject unexpected empty directories.'
    Remove-Item -LiteralPath $unexpectedDirectory -Force
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerRoot 'release-current.txt') -Value '..\\escape'
    Assert-Fails { & $releaseResolver -ReleaseRoot $consumerRoot } 'Standalone resolver must reject pointer tampering.'
    Set-Content -NoNewline -LiteralPath (Join-Path $consumerRoot 'release-current.txt') -Value $consumerSnapshotName
    $consumerInstall = Join-Path $consumerSnapshots "$consumerSnapshotName\INSTALL.md"
    Set-Content -NoNewline -LiteralPath $consumerInstall -Value 'tampered'
    Assert-Fails { & $releaseResolver -ReleaseRoot $consumerRoot } 'Standalone resolver must reject snapshot tampering.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTest = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unsafe test cleanup.' }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$installerSource = Get-Content -Raw -LiteralPath $installer
$stopScriptSource = Get-Content -Raw -LiteralPath $stopScript
$releaseScriptPaths = @(
    (Join-Path $repositoryRoot 'scripts\build-release.ps1'),
    (Join-Path $repositoryRoot 'scripts\package-outlook.ps1'),
    (Join-Path $repositoryRoot 'planner-edge-widget\scripts\package.ps1'),
    (Join-Path $repositoryRoot 'planner-edge-widget\scripts\package-connection-test.ps1'),
    (Join-Path $repositoryRoot 'microsoft-widgets-helper\scripts\publish.ps1')
)
$releaseScripts = ($releaseScriptPaths | ForEach-Object { Get-Content -Raw -LiteralPath $_ }) -join "`n"
Assert-True ($releaseScripts -notmatch 'Reset-TrustedDirectory|Remove-TrustedFile') 'Release builds must not delete and reuse predictable staging or artifact paths.'
Assert-True ($releaseScripts -notmatch 'Remove-Item[^\r\n]*-Recurse') 'Release builds must not recursively delete path-validated stages.'
foreach ($releaseScriptPath in $releaseScriptPaths) {
    $releaseScript = Get-Content -Raw -LiteralPath $releaseScriptPath
    Assert-True ($releaseScript -match 'New-ReleaseWorkspace') "Release script must use a fresh unpredictable workspace: $releaseScriptPath"
    Assert-True ($releaseScript -match 'Open-ReleaseSnapshot') "Release script must hold an immutable package snapshot: $releaseScriptPath"
    Assert-True ($releaseScript -match 'Assert-ReleaseArchive') "Release script must verify completed archive contents: $releaseScriptPath"
}
$buildReleaseSource = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'scripts\build-release.ps1')
Assert-True ($buildReleaseSource -match 'npm\s+ci\s+--ignore-scripts') 'Release builds must verify locked npm dependencies with npm ci.'
Assert-True ($buildReleaseSource -match 'Resolve-VerifiedReleaseDescriptor') 'Release output must be consumed through the verified descriptor resolver after activation.'
Assert-True ($buildReleaseSource -match 'RELEASE-MANIFEST\.json') 'Release builds must publish an archive-content descriptor.'
Assert-True ($installerSource -match 'PrivilegesRequired=lowest') 'Installer must remain per-user.'
Assert-True ($installerSource -match 'function\s+InitializeSetup') 'Installer must reject elevated setup before installation.'
Assert-True ($installerSource -match 'function\s+InitializeUninstall') 'Installer must reject elevated uninstall.'
Assert-True ($installerSource -match 'IsAdmin') 'Installer must detect an elevated token.'
Assert-True ($installerSource -match 'Run as administrator') 'Elevation error must explain how to rerun setup.'
Assert-True ($stopScriptSource -match 'Knack25\.MicrosoftWidgetsHelper\.Control\.v1') 'Installer must stop the helper through the same-user control pipe.'
Assert-True ($stopScriptSource -match 'Local\\Knack25\.MicrosoftWidgetsHelper') 'Installer must check the helper instance mutex before accepting a connection timeout.'
Assert-True ($stopScriptSource -notmatch '(ReadTimeout|WriteTimeout)\s*=') 'NamedPipeClientStream timeout properties cannot provide a real bounded shutdown.'
Assert-True ($stopScriptSource -notmatch '\$PSCommandPath') 'Encoded installer shutdown must not depend on a script pathname.'
Assert-True ($installerSource -match '\{#StopScriptEncodedCommand\}') 'Installer must execute the reviewed shutdown logic from immutable encoded bytes.'
Assert-True ($installerSource -match '-EncodedCommand') 'Installer must not reopen shutdown logic from a script pathname.'
Assert-True ($installerSource -notmatch '\{app\}\\Stop-MicrosoftWidgetsHelper\.ps1|InstalledScript|TrustedSource') 'Installer must have no installed-script fallback.'
Assert-True ($installerSource -notmatch 'ExtractTemporaryFile|CopyFile\(|GetSHA256OfFile|StopScriptTempName|-File\s') 'Installer shutdown must not use a mutable extracted or copied script file.'
Assert-True ($buildReleaseSource -match 'StopScriptEncodedCommand') 'Release build must compile the reviewed shutdown logic into an encoded command.'
$tamperedInstalledScript = Join-Path $testRoot 'installed\Stop-MicrosoftWidgetsHelper.ps1'
New-Item -ItemType Directory -Path (Split-Path -Parent $tamperedInstalledScript) -Force | Out-Null
Set-Content -NoNewline -LiteralPath $tamperedInstalledScript -Value 'exit 0 # malicious installed bypass'
$trustedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stopScript).Hash
$tamperedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $tamperedInstalledScript).Hash
Assert-True ($trustedHash -ne $tamperedHash) 'Tampered installed stop-script fixture unexpectedly matches the trusted script.'
Assert-True ($installerSource -notmatch [regex]::Escape($tamperedInstalledScript)) 'Installer source unexpectedly references the tampered installed-script fixture.'
Remove-Item -LiteralPath $testRoot -Recurse -Force
Assert-True ($installerSource -match '#include\s+HelperManifest') 'Installer must consume an exact generated file manifest.'
Assert-True ($installerSource -notmatch 'Source:\s*"\{#HelperSource\}\\\*"') 'Installer must not recursively enumerate the helper stage.'
Assert-True ($installerSource -notmatch 'Source:\s*"Stop-MicrosoftWidgetsHelper\.ps1"') 'Installer stop script must come from the immutable generated stage manifest.'
Assert-True ($stopScriptSource -notmatch 'catch \[TimeoutException\] \{ exit 0 \}') 'A pipe timeout alone must not be treated as proof that the helper is absent.'
Assert-True ($installerSource -match 'Result\s*:=\s*RequestHelperStop;') 'Uninstall must stop through the same-user control pipe before removing files.'
Assert-True ($stopScriptSource -notmatch 'catch \[IO\.IOException\] \{ exit 0 \}') 'Control-pipe I/O failures must not be treated as a safe stop.'
Assert-True ($installerSource -notmatch '\[UninstallRun\]') 'Uninstall must not execute the installed helper.'
Assert-True ($installerSource -notmatch 'Exec\s*\(\s*HelperPath') 'Setup must not execute the installed helper.'

if (-not $SkipOutlookBuild) {
    $outlookRoot = Join-Path $repositoryRoot 'outlook-edge-widget'
    $sentinel = Join-Path $outlookRoot 'dist\stale-sentinel.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'must-not-ship'
    Push-Location $outlookRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Outlook build failed.' }
    } finally { Pop-Location }
    Assert-True (-not (Test-Path -LiteralPath $sentinel)) 'Outlook build retained stale dist content.'
}

Write-Host 'Release security tests passed.'
