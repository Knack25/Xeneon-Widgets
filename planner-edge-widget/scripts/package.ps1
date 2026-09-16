$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$manifest = Get-Content -Raw (Join-Path $projectRoot "widget\manifest.json") | ConvertFrom-Json
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw "Widget version must use major.minor.patch." }
$stage = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist\PlannerEdgeWidget-$($manifest.version)"))
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist"))
if (-not $stage.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Packaging stage must stay inside dist."
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $stage "resources"), (Join-Path $stage "src") -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $projectRoot "widget\index.html"), (Join-Path $projectRoot "widget\manifest.json"), (Join-Path $projectRoot "widget\styles.css") -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot "widget\resources\icon.svg") -Destination (Join-Path $stage "resources")
Copy-Item -LiteralPath (Join-Path $projectRoot "widget\src\api.js"), (Join-Path $projectRoot "widget\src\app.js"), (Join-Path $projectRoot "widget\src\state.js") -Destination (Join-Path $stage "src")

$cli = Join-Path $projectRoot "widget\node_modules\icuewidget-cli\node-bin\icuewidget.js"
if (-not (Test-Path -LiteralPath $cli)) { throw "Run npm install in widget first." }
node $cli validate $stage
if ($LASTEXITCODE -ne 0) { throw "Widget validation failed." }
node $cli package $stage
if ($LASTEXITCODE -ne 0) { throw "Widget packaging failed." }

$generated = [IO.Path]::GetFullPath((Join-Path $distRoot "planner-edge-widget.icuewidget"))
$versioned = [IO.Path]::GetFullPath((Join-Path $distRoot "PlannerEdgeWidget-$($manifest.version).icuewidget"))
if (-not $generated.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not $versioned.StartsWith($distRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay inside dist."
}
if (-not (Test-Path -LiteralPath $generated)) { throw "Widget package was not created." }
Move-Item -LiteralPath $generated -Destination $versioned -Force
Write-Host "Import: $versioned"
