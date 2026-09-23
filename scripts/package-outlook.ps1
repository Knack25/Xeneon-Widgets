param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseSafety = Join-Path $PSScriptRoot 'release-safety.ps1'
$inventoryVerifier = Join-Path $PSScriptRoot 'verify-release-inventory.ps1'
. $releaseSafety
. (Join-Path $PSScriptRoot 'release-manifests.ps1')
$widgetRoot = Join-Path $root 'outlook-edge-widget'
if (-not $SkipBuild) {
    Push-Location $widgetRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Outlook widget build failed. Run npm ci in outlook-edge-widget first.' }
    } finally { Pop-Location }
}
$built = Join-Path $widgetRoot 'dist'
& $inventoryVerifier -Stage $built -Manifest (ConvertTo-Json -InputObject $OutlookWidgetManifest -Compress)
# iCUE imports the document through an XML parser, unlike a web browser.
$document = [xml](Get-Content -Raw -LiteralPath (Join-Path $built 'index.html'))
$title = $document.DocumentElement.SelectSingleNode('head/title')
if ($null -eq $title -or [string]::IsNullOrWhiteSpace($title.InnerText)) { throw 'Outlook widget must have an XML-readable title.' }
$manifest = Get-Content -Raw -LiteralPath (Join-Path $built 'manifest.json') | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Outlook widget version must use major.minor.patch.' }
$dist = Initialize-TrustedDirectory -Path (Join-Path $root 'dist') -Anchor $root
$workspace = New-ReleaseWorkspace -Parent $dist -Prefix 'outlook-package'
$stage = Join-Path $workspace 'stage'
[void][IO.Directory]::CreateDirectory($stage)
foreach ($relative in $OutlookWidgetManifest) {
    $source = Join-Path $built ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $destination = Join-Path $stage ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $source -Destination $destination
}
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $OutlookWidgetManifest -Compress)
$cli = Join-Path $root 'planner-edge-widget/widget/node_modules/icuewidget-cli/node-bin/icuewidget.js'
if (-not (Test-Path -LiteralPath $cli)) { throw 'Run npm ci in planner-edge-widget/widget to install the shared packaging CLI.' }
$generated = Join-Path $workspace 'outlook-edge-widget.icuewidget'
$versioned = Join-Path $dist "OutlookEdgeWidget-$($manifest.version).icuewidget"
$snapshot = Open-ReleaseSnapshot -Stage $stage -Manifest $OutlookWidgetManifest
try {
    node $cli validate $stage
    if ($LASTEXITCODE -ne 0) { throw 'Outlook widget validation failed.' }
    node $cli package $stage --output $generated
    if ($LASTEXITCODE -ne 0) { throw 'Outlook widget packaging failed.' }
    Assert-ReleaseArchive -Archive $generated -Snapshot $snapshot
    $publishedLease = Publish-VerifiedReleaseArchive -Source $generated -Destination $versioned -TrustedParent $dist -Snapshot $snapshot
    Close-VerifiedReleaseArchive $publishedLease
} finally {
    Close-ReleaseSnapshot $snapshot
}
Write-Host "Import: $versioned"
