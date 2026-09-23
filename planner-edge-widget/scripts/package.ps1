$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$inventoryVerifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
. (Join-Path $repositoryRoot 'scripts\release-safety.ps1')
. (Join-Path $repositoryRoot 'scripts\release-manifests.ps1')
$manifest = Get-Content -Raw (Join-Path $projectRoot "widget\manifest.json") | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw "Widget version must use major.minor.patch." }
$distRoot = Initialize-TrustedDirectory -Path (Join-Path $projectRoot 'dist') -Anchor $projectRoot
$stage = Reset-TrustedDirectory -Path (Join-Path $distRoot "PlannerEdgeWidget-$($manifest.version)") -Root $distRoot
foreach ($relative in $PlannerWidgetManifest) {
    $source = Join-Path (Join-Path $projectRoot 'widget') ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $destination = Join-Path $stage ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $source -Destination $destination
}
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $PlannerWidgetManifest -Compress)

$cli = Join-Path $projectRoot "widget\node_modules\icuewidget-cli\node-bin\icuewidget.js"
if (-not (Test-Path -LiteralPath $cli)) { throw "Run npm install in widget first." }
$generated = [IO.Path]::GetFullPath((Join-Path $distRoot "planner-edge-widget.icuewidget"))
$versioned = [IO.Path]::GetFullPath((Join-Path $distRoot "PlannerEdgeWidget-$($manifest.version).icuewidget"))
Remove-TrustedFile -Path $generated -Root $distRoot
Remove-TrustedFile -Path $versioned -Root $distRoot
node $cli validate $stage
if ($LASTEXITCODE -ne 0) { throw "Widget validation failed." }
node $cli package $stage
if ($LASTEXITCODE -ne 0) { throw "Widget packaging failed." }
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $PlannerWidgetManifest -Compress)
if (-not (Test-Path -LiteralPath $generated)) { throw "Widget package was not created." }
Move-Item -LiteralPath $generated -Destination $versioned -Force
Write-Host "Import: $versioned"
