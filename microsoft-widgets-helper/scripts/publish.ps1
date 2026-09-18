$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$project = Join-Path $projectRoot "src\MicrosoftWidgets.Helper\MicrosoftWidgets.Helper.csproj"
$output = Join-Path $projectRoot "dist\helper"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw "Helper publish failed." }
[xml]$projectFile = Get-Content -Raw -LiteralPath $project
$version = $projectFile.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $projectRoot "dist\MicrosoftWidgetsHelper-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
Write-Host "Open: $(Join-Path $output 'MicrosoftWidgets.Helper.exe')"
Write-Host "Package: $archive"
