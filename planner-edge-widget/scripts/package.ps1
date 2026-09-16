$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$stage = [IO.Path]::GetFullPath((Join-Path $projectRoot "dist\widget"))
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
