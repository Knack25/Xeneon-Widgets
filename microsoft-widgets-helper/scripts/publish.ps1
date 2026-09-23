param([string]$OutputDirectory, [switch]$SkipArchive, [switch]$SkipWidgetBuild)
$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$inventoryVerifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
. (Join-Path $repositoryRoot 'scripts\release-safety.ps1')
. (Join-Path $repositoryRoot 'scripts\release-manifests.ps1')
$project = Join-Path $projectRoot "src\MicrosoftWidgets.Helper\MicrosoftWidgets.Helper.csproj"
if (-not $SkipWidgetBuild) {
    Push-Location (Join-Path $projectRoot '../outlook-edge-widget')
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Outlook build failed. Run npm ci in outlook-edge-widget first.' }
    } finally { Pop-Location }
}
$outlookDist = Join-Path $repositoryRoot 'outlook-edge-widget\dist'
& $inventoryVerifier -Stage $outlookDist -Manifest (ConvertTo-Json -InputObject $OutlookWidgetManifest -Compress)
$distRoot = Initialize-TrustedDirectory -Path (Join-Path $projectRoot 'dist') -Anchor $projectRoot
$workspace = New-ReleaseWorkspace -Parent $distRoot -Prefix 'helper-publish'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $workspace 'helper' }
if (Test-Path -LiteralPath $output) { throw "Helper output must be a fresh path: $output" }
if (-not (Test-ReleasePathWithin $output $distRoot)) { throw "Helper output must stay under the helper dist root: $output" }
[void][IO.Directory]::CreateDirectory($output)
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $output
if ($LASTEXITCODE -ne 0) { throw "Helper publish failed." }
& $inventoryVerifier -Stage $output -Manifest (ConvertTo-Json -InputObject $HelperPublishManifest -Compress)
[xml]$projectFile = Get-Content -Raw -LiteralPath $project
$version = $projectFile.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $distRoot "MicrosoftWidgetsHelper-$version-win-x64.zip"
if (-not $SkipArchive) {
    $pendingArchive = Join-Path $workspace 'helper.zip'
    $snapshot = Open-ReleaseSnapshot -Stage $output -Manifest $HelperPublishManifest
    try {
        Compress-Archive -Path (Join-Path $output '*') -DestinationPath $pendingArchive
        Assert-ReleaseArchive -Archive $pendingArchive -Snapshot $snapshot
        Publish-ReleaseFile -Source $pendingArchive -Destination $archive -TrustedParent $distRoot
    } finally {
        Close-ReleaseSnapshot $snapshot
    }
}
Write-Host "Open: $(Join-Path $output 'MicrosoftWidgets.Helper.exe')"
if (-not $SkipArchive) { Write-Host "Package: $archive" }
