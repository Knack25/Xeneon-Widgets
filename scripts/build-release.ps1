param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inventoryVerifier = Join-Path $PSScriptRoot 'verify-release-inventory.ps1'
. (Join-Path $PSScriptRoot 'release-safety.ps1')
. (Join-Path $PSScriptRoot 'release-manifests.ps1')
$widgetRoot = Join-Path $root 'planner-edge-widget'
$helperRoot = Join-Path $root 'microsoft-widgets-helper'
$manifest = Get-Content -Raw (Join-Path $widgetRoot 'widget\manifest.json') | ConvertFrom-Json
[xml]$project = Get-Content -Raw (Join-Path $helperRoot 'src\MicrosoftWidgets.Helper\MicrosoftWidgets.Helper.csproj')
$helperVersion = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$releaseVersion = $project.Project.PropertyGroup.ReleaseVersion | Where-Object { $_ } | Select-Object -First 1
$widgetVersion = $manifest.version
$outlookRoot = Join-Path $root 'outlook-edge-widget'
$outlookManifest = Get-Content -Raw (Join-Path $outlookRoot 'widget/manifest.json') | ConvertFrom-Json
$outlookVersion = $outlookManifest.version
$distRoot = Initialize-TrustedDirectory -Path (Join-Path $root 'dist') -Anchor $root
$buildWorkspace = New-ReleaseWorkspace -Parent $distRoot -Prefix 'release-build'
$releaseCandidate = Join-Path $buildWorkspace 'release'
[void][IO.Directory]::CreateDirectory($releaseCandidate)
$helperDist = Initialize-TrustedDirectory -Path (Join-Path $helperRoot 'dist') -Anchor $helperRoot
$helperWorkspace = New-ReleaseWorkspace -Parent $helperDist -Prefix 'release-helper'
$stage = Join-Path $helperWorkspace 'stage'
if (-not $InnoCompiler) {
    $candidates = @((Join-Path ${env:LOCALAPPDATA} 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 or pass -InnoCompiler.' }
Push-Location (Join-Path $widgetRoot 'widget')
try {
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Planner locked dependency install failed.' }
} finally { Pop-Location }
Push-Location $outlookRoot
try {
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Outlook locked dependency install failed.' }
} finally { Pop-Location }
& (Join-Path $widgetRoot 'scripts\verify.ps1')
node (Join-Path $helperRoot 'tests/outlook-setup.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Outlook setup tests failed.' }
Push-Location $outlookRoot
try {
    npm test
    if ($LASTEXITCODE -ne 0) { throw 'Outlook widget tests failed.' }
} finally { Pop-Location }
& (Join-Path $widgetRoot 'scripts\package.ps1')
& (Join-Path $root 'scripts/package-outlook.ps1')
& (Join-Path $helperRoot 'scripts\publish.ps1') -OutputDirectory $stage -SkipArchive -SkipWidgetBuild
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'widgets'))
$widget = Join-Path $widgetRoot "dist\PlannerEdgeWidget-$widgetVersion.icuewidget"
Copy-Item -LiteralPath $widget -Destination (Join-Path $stage 'widgets\PlannerEdgeWidget.icuewidget') -Force
$outlookWidget = Join-Path $root "dist/OutlookEdgeWidget-$outlookVersion.icuewidget"
Copy-Item -LiteralPath $outlookWidget -Destination (Join-Path $stage 'widgets/OutlookEdgeWidget.icuewidget') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\OUTLOOK.md') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $helperRoot 'installer\Stop-MicrosoftWidgetsHelper.ps1') -Destination $stage -Force
$installerStageManifest = @($HelperPublishManifest) + @('widgets/PlannerEdgeWidget.icuewidget', 'widgets/OutlookEdgeWidget.icuewidget', 'INSTALL.md', 'OUTLOOK.md', 'Stop-MicrosoftWidgetsHelper.ps1')
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $installerStageManifest -Compress)
$stageSnapshot = Open-ReleaseSnapshot -Stage $stage -Manifest $installerStageManifest
$stopScriptText = Get-Content -Raw -LiteralPath (Join-Path $stage 'Stop-MicrosoftWidgetsHelper.ps1')
$stopScriptEncodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($stopScriptText))
$innoFileManifest = Join-Path $helperWorkspace 'helper-files.iss'
$lockedReleaseAssets = [Collections.Generic.List[IDisposable]]::new()
$plannerSnapshot = $null
$outlookSnapshot = $null
$candidateSnapshot = $null
$publishedSnapshot = $null
try {
    $innoManifestLease = New-InnoFileManifest -Snapshot $stageSnapshot -Output $innoFileManifest
    try {
        & $InnoCompiler "/DReleaseVersion=$releaseVersion" "/DHelperManifest=$($innoManifestLease.Path)" "/DStopScriptEncodedCommand=$stopScriptEncodedCommand" "/DReleaseOutput=$releaseCandidate" (Join-Path $helperRoot 'installer\MicrosoftWidgets.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    } finally {
        Close-InnoFileManifest $innoManifestLease
    }
    $installerOutput = Join-Path $releaseCandidate "MicrosoftWidgetsSetup-$releaseVersion.exe"
    $lockedReleaseAssets.Add([IO.File]::Open($installerOutput, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
    Copy-Item -LiteralPath $widget -Destination $releaseCandidate
    Copy-Item -LiteralPath $outlookWidget -Destination $releaseCandidate
    Copy-Item -LiteralPath (Join-Path $root 'docs/OUTLOOK.md') -Destination $releaseCandidate
    Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination $releaseCandidate
    $plannerSnapshot = Open-ReleaseSnapshot -Stage (Join-Path $widgetRoot 'widget') -Manifest $PlannerWidgetManifest
    $plannerLease = Open-VerifiedReleaseArchive -Archive (Join-Path $releaseCandidate (Split-Path -Leaf $widget)) -Snapshot $plannerSnapshot
    $lockedReleaseAssets.Add($plannerLease.Stream)
    $outlookSnapshot = Open-ReleaseSnapshot -Stage (Join-Path $outlookRoot 'dist') -Manifest $OutlookWidgetManifest
    $outlookLease = Open-VerifiedReleaseArchive -Archive (Join-Path $releaseCandidate (Split-Path -Leaf $outlookWidget)) -Snapshot $outlookSnapshot
    $lockedReleaseAssets.Add($outlookLease.Stream)
    $portable = Join-Path $releaseCandidate "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip"
    $pendingPortable = Join-Path $helperWorkspace 'portable.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $pendingPortable
    Assert-ReleaseArchive -Archive $pendingPortable -Snapshot $stageSnapshot
    $portableLease = Publish-VerifiedReleaseArchive -Source $pendingPortable -Destination $portable -TrustedParent $releaseCandidate -Snapshot $stageSnapshot
    $lockedReleaseAssets.Add($portableLease.Stream)
    $assets = @("MicrosoftWidgetsSetup-$releaseVersion.exe", "PlannerEdgeWidget-$widgetVersion.icuewidget", "OutlookEdgeWidget-$outlookVersion.icuewidget", "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip", 'INSTALL.md', 'OUTLOOK.md')
    & $inventoryVerifier -Stage $releaseCandidate -Manifest (ConvertTo-Json -InputObject $assets -Compress)
    $checksums = foreach ($asset in $assets) {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $releaseCandidate $asset)
        "$($hash.Hash.ToLowerInvariant())  $asset"
    }
    $checksums | Set-Content -LiteralPath (Join-Path $releaseCandidate 'SHA256SUMS.txt') -Encoding ascii
    $releaseManifest = @($assets) + 'SHA256SUMS.txt'
    & $inventoryVerifier -Stage $releaseCandidate -Manifest (ConvertTo-Json -InputObject $releaseManifest -Compress)
    $candidateSnapshot = Open-ReleaseSnapshot -Stage $releaseCandidate -Manifest $releaseManifest -AllowDeleteShare
    foreach ($lockedAsset in $lockedReleaseAssets) { $lockedAsset.Dispose() }
    $lockedReleaseAssets.Clear()

    $verifyFinalRelease = {
        param($publishedPath, $published)
        & $inventoryVerifier -Stage $publishedPath -Manifest (ConvertTo-Json -InputObject $releaseManifest -Compress)
        $byName = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $published.Entries) { $byName.Add($entry.Name, $entry) }
        Assert-ReleaseArchiveStream -Stream $byName[(Split-Path -Leaf $widget)].Stream -DisplayPath (Split-Path -Leaf $widget) -Snapshot $plannerSnapshot
        Assert-ReleaseArchiveStream -Stream $byName[(Split-Path -Leaf $outlookWidget)].Stream -DisplayPath (Split-Path -Leaf $outlookWidget) -Snapshot $outlookSnapshot
        Assert-ReleaseArchiveStream -Stream $byName[(Split-Path -Leaf $portable)].Stream -DisplayPath (Split-Path -Leaf $portable) -Snapshot $stageSnapshot

        $checksumEntry = $byName['SHA256SUMS.txt']
        $checksumEntry.Stream.Position = 0
        $reader = [IO.StreamReader]::new($checksumEntry.Stream, [Text.Encoding]::ASCII, $true, 1024, $true)
        try { $checksumLines = @($reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $reader.Dispose() }
        if ($checksumLines.Count -ne $assets.Count) { throw 'Published checksum inventory is incomplete.' }
        $seenChecksums = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in $checksumLines) {
            if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Published checksum line is invalid: $line" }
            $name = $Matches[2]
            if (-not $assets.Contains($name) -or -not $seenChecksums.Add($name)) { throw "Published checksum entry is unexpected or duplicated: $name" }
            if ($byName[$name].Hash -ne $Matches[1]) { throw "Published checksum does not match: $name" }
        }
    }

    $publishedSnapshot = Publish-VerifiedReleaseSnapshot -Source $releaseCandidate -SnapshotRoot (Join-Path $distRoot 'release-snapshots') -CurrentPointer (Join-Path $distRoot 'release-current.txt') -TrustedParent $distRoot -Snapshot $candidateSnapshot -Verifier $verifyFinalRelease
    Write-Host "Release snapshot: $($publishedSnapshot.Snapshot.Stage)"
    Write-Host "Current release pointer: $($publishedSnapshot.PointerPath) -> $($publishedSnapshot.SnapshotName)"
} finally {
    foreach ($lockedAsset in $lockedReleaseAssets) { $lockedAsset.Dispose() }
    if ($null -ne $publishedSnapshot) { Close-VerifiedReleaseSnapshot $publishedSnapshot }
    if ($null -ne $candidateSnapshot) { Close-ReleaseSnapshot $candidateSnapshot }
    if ($null -ne $outlookSnapshot) { Close-ReleaseSnapshot $outlookSnapshot }
    if ($null -ne $plannerSnapshot) { Close-ReleaseSnapshot $plannerSnapshot }
    Close-ReleaseSnapshot $stageSnapshot
}
