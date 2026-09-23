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
$stage = [IO.Path]::GetFullPath((Join-Path $dist 'outlook-package'))
$stage = Reset-TrustedDirectory -Path $stage -Root $dist
foreach ($relative in $OutlookWidgetManifest) {
    $source = Join-Path $built ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $destination = Join-Path $stage ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $source -Destination $destination
}
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $OutlookWidgetManifest -Compress)
$cli = Join-Path $root 'planner-edge-widget/widget/node_modules/icuewidget-cli/node-bin/icuewidget.js'
if (-not (Test-Path -LiteralPath $cli)) { throw 'Run npm ci in planner-edge-widget/widget to install the shared packaging CLI.' }
$generated = Join-Path $dist 'outlook-edge-widget.icuewidget'
$versioned = Join-Path $dist "OutlookEdgeWidget-$($manifest.version).icuewidget"
Remove-TrustedFile -Path $generated -Root $dist
Remove-TrustedFile -Path $versioned -Root $dist
node $cli validate $stage
if ($LASTEXITCODE -ne 0) { throw 'Outlook widget validation failed.' }
node $cli package $stage
if ($LASTEXITCODE -ne 0) { throw 'Outlook widget packaging failed.' }
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $OutlookWidgetManifest -Compress)
if (-not (Test-Path -LiteralPath $generated)) { throw 'Outlook widget package was not created.' }
Move-Item -LiteralPath $generated -Destination $versioned -Force
Write-Host "Import: $versioned"
