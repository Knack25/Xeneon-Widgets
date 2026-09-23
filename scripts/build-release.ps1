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
$installerStageManifest = @($HelperPublishManifest) + @('widgets/PlannerEdgeWidget.icuewidget', 'widgets/OutlookEdgeWidget.icuewidget', 'INSTALL.md', 'OUTLOOK.md')
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $installerStageManifest -Compress)
$stageSnapshot = Open-ReleaseSnapshot -Stage $stage -Manifest $installerStageManifest
try {
    & $InnoCompiler "/DReleaseVersion=$releaseVersion" "/DHelperSource=$stage" "/DReleaseOutput=$releaseCandidate" (Join-Path $helperRoot 'installer\MicrosoftWidgets.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    Copy-Item -LiteralPath $widget -Destination $releaseCandidate
    Copy-Item -LiteralPath $outlookWidget -Destination $releaseCandidate
    Copy-Item -LiteralPath (Join-Path $root 'docs/OUTLOOK.md') -Destination $releaseCandidate
    Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination $releaseCandidate
    $plannerSnapshot = Open-ReleaseSnapshot -Stage (Join-Path $widgetRoot 'widget') -Manifest $PlannerWidgetManifest
    try {
        Assert-ReleaseArchive -Archive (Join-Path $releaseCandidate (Split-Path -Leaf $widget)) -Snapshot $plannerSnapshot
    } finally { Close-ReleaseSnapshot $plannerSnapshot }
    $outlookSnapshot = Open-ReleaseSnapshot -Stage (Join-Path $outlookRoot 'dist') -Manifest $OutlookWidgetManifest
    try {
        Assert-ReleaseArchive -Archive (Join-Path $releaseCandidate (Split-Path -Leaf $outlookWidget)) -Snapshot $outlookSnapshot
    } finally { Close-ReleaseSnapshot $outlookSnapshot }
    $portable = Join-Path $releaseCandidate "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable
    Assert-ReleaseArchive -Archive $portable -Snapshot $stageSnapshot
} finally {
    Close-ReleaseSnapshot $stageSnapshot
}
$assets = @("MicrosoftWidgetsSetup-$releaseVersion.exe", "PlannerEdgeWidget-$widgetVersion.icuewidget", "OutlookEdgeWidget-$outlookVersion.icuewidget", "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip", 'INSTALL.md', 'OUTLOOK.md')
& $inventoryVerifier -Stage $releaseCandidate -Manifest (ConvertTo-Json -InputObject $assets -Compress)
$checksums = foreach ($asset in $assets) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $releaseCandidate $asset)
    "$($hash.Hash.ToLowerInvariant())  $asset"
}
$checksums | Set-Content -LiteralPath (Join-Path $releaseCandidate 'SHA256SUMS.txt') -Encoding ascii
& $inventoryVerifier -Stage $releaseCandidate -Manifest (ConvertTo-Json -InputObject (@($assets) + 'SHA256SUMS.txt') -Compress)
$release = Publish-ReleaseDirectory -Source $releaseCandidate -Destination (Join-Path $distRoot 'release') -TrustedParent $distRoot
Write-Host "Release files: $release"
