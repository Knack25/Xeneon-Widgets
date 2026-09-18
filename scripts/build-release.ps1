param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$widgetRoot = Join-Path $root 'planner-edge-widget'
$helperRoot = Join-Path $root 'microsoft-widgets-helper'
$manifest = Get-Content -Raw (Join-Path $widgetRoot 'widget\manifest.json') | ConvertFrom-Json
[xml]$project = Get-Content -Raw (Join-Path $helperRoot 'src\MicrosoftWidgets.Helper\MicrosoftWidgets.Helper.csproj')
$helperVersion = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$releaseVersion = $manifest.version
$release = Join-Path $root 'dist\release'
$stage = Join-Path $helperRoot 'dist\release-helper'
if (-not $InnoCompiler) {
    $candidates = @((Join-Path ${env:LOCALAPPDATA} 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 or pass -InnoCompiler.' }
& (Join-Path $widgetRoot 'scripts\verify.ps1')
& (Join-Path $widgetRoot 'scripts\package.ps1')
& (Join-Path $helperRoot 'scripts\publish.ps1') -OutputDirectory $stage -SkipArchive
New-Item -ItemType Directory -Force -Path $release, (Join-Path $stage 'widgets') | Out-Null
$widget = Join-Path $widgetRoot "dist\PlannerEdgeWidget-$releaseVersion.icuewidget"
Copy-Item -LiteralPath $widget -Destination (Join-Path $stage 'widgets\PlannerEdgeWidget.icuewidget') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination $stage -Force
& $InnoCompiler "/DReleaseVersion=$releaseVersion" "/DHelperSource=$stage" (Join-Path $helperRoot 'installer\MicrosoftWidgets.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Copy-Item -LiteralPath $widget -Destination $release -Force
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination $release -Force
$portable = Join-Path $release "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -Force
$assets = @("MicrosoftWidgetsSetup-$releaseVersion.exe", "PlannerEdgeWidget-$releaseVersion.icuewidget", "MicrosoftWidgetsHelper-$helperVersion-portable-win-x64.zip", 'INSTALL.md')
$checksums = foreach ($asset in $assets) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $release $asset)
    "$($hash.Hash.ToLowerInvariant())  $asset"
}
$checksums | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release files: $release"
