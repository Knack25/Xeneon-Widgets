$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$source = Join-Path $projectRoot "connection-test"
$cli = Join-Path $projectRoot "widget\node_modules\icuewidget-cli\node-bin\icuewidget.js"
$manifest = Get-Content -Raw (Join-Path $source "manifest.json") | ConvertFrom-Json
$dist = Join-Path $projectRoot "dist"
$generated = [IO.Path]::GetFullPath((Join-Path $projectRoot "planner-edge-connection-test.icuewidget"))
$output = [IO.Path]::GetFullPath((Join-Path $dist "PlannerEdgeConnectionTest-$($manifest.version).icuewidget"))
if (-not $output.StartsWith([IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay inside dist."
}
if (-not (Test-Path -LiteralPath $cli)) { throw "Run npm install in widget first." }

node $cli validate $source
if ($LASTEXITCODE -ne 0) { throw "Connection test validation failed." }
node $cli package $source
if ($LASTEXITCODE -ne 0) { throw "Connection test packaging failed." }
if (-not (Test-Path -LiteralPath $generated)) { throw "Connection test package was not created." }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Move-Item -LiteralPath $generated -Destination $output -Force
Write-Host "Import: $output"
