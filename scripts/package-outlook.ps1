param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$widgetRoot = Join-Path $root 'outlook-edge-widget'
if (-not $SkipBuild) {
    Push-Location $widgetRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Outlook widget build failed. Run npm ci in outlook-edge-widget first.' }
    } finally { Pop-Location }
}
$built = Join-Path $widgetRoot 'dist'
# iCUE imports the document through an XML parser, unlike a web browser.
$document = [xml](Get-Content -Raw -LiteralPath (Join-Path $built 'index.html'))
if ([string]::IsNullOrWhiteSpace($document.html.head.title)) { throw 'Outlook widget must have an XML-readable title.' }
$manifest = Get-Content -Raw -LiteralPath (Join-Path $built 'manifest.json') | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Outlook widget version must use major.minor.patch.' }
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
$stage = [IO.Path]::GetFullPath((Join-Path $dist 'outlook-package'))
if (-not $stage.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Outlook packaging stage must stay inside the repository dist directory.'
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $built '*') -Destination $stage -Recurse -Force
$cli = Join-Path $root 'planner-edge-widget/widget/node_modules/icuewidget-cli/node-bin/icuewidget.js'
if (-not (Test-Path -LiteralPath $cli)) { throw 'Run npm ci in planner-edge-widget/widget to install the shared packaging CLI.' }
node $cli validate $stage
if ($LASTEXITCODE -ne 0) { throw 'Outlook widget validation failed.' }
node $cli package $stage
if ($LASTEXITCODE -ne 0) { throw 'Outlook widget packaging failed.' }
$generated = Join-Path $dist 'outlook-edge-widget.icuewidget'
$versioned = Join-Path $dist "OutlookEdgeWidget-$($manifest.version).icuewidget"
if (-not (Test-Path -LiteralPath $generated)) { throw 'Outlook widget package was not created.' }
Move-Item -LiteralPath $generated -Destination $versioned -Force
Write-Host "Import: $versioned"
