$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$source = Join-Path $projectRoot "connection-test"
$cli = Join-Path $projectRoot "widget\node_modules\icuewidget-cli\node-bin\icuewidget.js"
$manifest = Get-Content -Raw (Join-Path $source "manifest.json") | ConvertFrom-Json
$inventoryVerifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
. (Join-Path $repositoryRoot 'scripts\release-safety.ps1')
. (Join-Path $repositoryRoot 'scripts\release-manifests.ps1')
$dist = Initialize-TrustedDirectory -Path (Join-Path $projectRoot 'dist') -Anchor $projectRoot
$workspace = New-ReleaseWorkspace -Parent $dist -Prefix 'planner-connection-test'
$stage = Join-Path $workspace 'stage'
[void][IO.Directory]::CreateDirectory($stage)
foreach ($relative in $PlannerConnectionTestManifest) {
    $sourcePath = Join-Path $source ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    $destination = Join-Path $stage ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    Copy-Item -LiteralPath $sourcePath -Destination $destination
}
& $inventoryVerifier -Stage $stage -Manifest (ConvertTo-Json -InputObject $PlannerConnectionTestManifest -Compress)
$generated = Join-Path $workspace 'planner-edge-connection-test.icuewidget'
$output = [IO.Path]::GetFullPath((Join-Path $dist "PlannerEdgeConnectionTest-$($manifest.version).icuewidget"))
if (-not $output.StartsWith([IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must stay inside dist."
}
if (-not (Test-Path -LiteralPath $cli)) { throw "Run npm ci in widget first." }

$snapshot = Open-ReleaseSnapshot -Stage $stage -Manifest $PlannerConnectionTestManifest
try {
    node $cli validate $stage
    if ($LASTEXITCODE -ne 0) { throw "Connection test validation failed." }
    node $cli package $stage --output $generated
    if ($LASTEXITCODE -ne 0) { throw "Connection test packaging failed." }
    Assert-ReleaseArchive -Archive $generated -Snapshot $snapshot
    $publishedLease = Publish-VerifiedReleaseArchive -Source $generated -Destination $output -TrustedParent $dist -Snapshot $snapshot
    Close-VerifiedReleaseArchive $publishedLease
} finally {
    Close-ReleaseSnapshot $snapshot
}
Write-Host "Import: $output"
