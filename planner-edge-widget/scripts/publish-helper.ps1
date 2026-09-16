$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $projectRoot "helper\PlannerEdge.Helper\PlannerEdge.Helper.csproj"
$output = Join-Path $projectRoot "dist\helper"

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw "Helper publish failed." }

Write-Host "Open: $(Join-Path $output 'PlannerEdge.Helper.exe')"
