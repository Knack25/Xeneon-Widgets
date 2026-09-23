param([switch]$SkipOutlookBuild)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
$releaseSafety = Join-Path $repositoryRoot 'scripts\release-safety.ps1'
$installer = Join-Path $repositoryRoot 'microsoft-widgets-helper\installer\MicrosoftWidgets.iss'
$stopScript = Join-Path $repositoryRoot 'microsoft-widgets-helper\installer\Stop-MicrosoftWidgetsHelper.ps1'

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
        } finally {
            Close-VerifiedReleaseSnapshot $lease
        }

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
Assert-True ($installerSource -match 'PrivilegesRequired=lowest') 'Installer must remain per-user.'
Assert-True ($installerSource -match 'function\s+InitializeSetup') 'Installer must reject elevated setup before installation.'
Assert-True ($installerSource -match 'function\s+InitializeUninstall') 'Installer must reject elevated uninstall.'
Assert-True ($installerSource -match 'IsAdmin') 'Installer must detect an elevated token.'
Assert-True ($installerSource -match 'Run as administrator') 'Elevation error must explain how to rerun setup.'
Assert-True ($stopScriptSource -match 'Knack25\.MicrosoftWidgetsHelper\.Control\.v1') 'Installer must stop the helper through the same-user control pipe.'
Assert-True ($stopScriptSource -match 'Local\\Knack25\.MicrosoftWidgetsHelper') 'Installer must check the helper instance mutex before accepting a connection timeout.'
Assert-True ($stopScriptSource -notmatch '(ReadTimeout|WriteTimeout)\s*=') 'NamedPipeClientStream timeout properties cannot provide a real bounded shutdown.'
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
