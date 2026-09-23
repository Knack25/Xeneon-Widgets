param([switch]$SkipOutlookBuild)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verifier = Join-Path $repositoryRoot 'scripts\verify-release-inventory.ps1'
$releaseSafety = Join-Path $repositoryRoot 'scripts\release-safety.ps1'
$installer = Join-Path $repositoryRoot 'microsoft-widgets-helper\installer\MicrosoftWidgets.iss'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fails([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}

Assert-True (Test-Path -LiteralPath $verifier -PathType Leaf) 'Release inventory verifier is missing.'
Assert-True (Test-Path -LiteralPath $releaseSafety -PathType Leaf) 'Safe release-stage cleanup helpers are missing.'
. $releaseSafety
Assert-True ($null -ne (Get-Command Remove-TrustedFile -ErrorAction SilentlyContinue)) 'Safe release-file removal helper is missing.'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("MicrosoftWidgetsReleaseSecurity-" + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $testRoot 'stage'
$sibling = Join-Path $testRoot 'sibling'
try {
    New-Item -ItemType Directory -Path $stage, $sibling -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $stage 'allowed.txt') -Value 'reviewed'
    $manifest = ConvertTo-Json -InputObject @('allowed.txt') -Compress
    & $verifier -Stage $stage -Manifest $manifest

    Set-Content -LiteralPath (Join-Path $stage 'unexpected.txt') -Value 'must-not-ship'
    Assert-Fails { & $verifier -Stage $stage -Manifest $manifest } 'Unexpected files must fail inventory verification.'
    Remove-Item -LiteralPath (Join-Path $stage 'unexpected.txt') -Force

    Assert-Fails { & $verifier -Stage $stage -Manifest (ConvertTo-Json -InputObject @('missing.txt') -Compress) } 'Missing files must fail inventory verification.'
    Set-Content -LiteralPath (Join-Path $sibling 'escape.txt') -Value 'outside-stage'
    Assert-Fails { & $verifier -Stage $stage -Manifest (ConvertTo-Json -InputObject @('..\sibling\escape.txt') -Compress) } 'Sibling escapes must fail inventory verification.'

    $junction = Join-Path $stage 'linked'
    New-Item -ItemType Junction -Path $junction -Target $sibling | Out-Null
    Assert-Fails { & $verifier -Stage $stage -Manifest $manifest } 'Junctions and reparse points must fail inventory verification.'

    $cleanupRoot = Join-Path $testRoot 'cleanup-root'
    $cleanupStage = Join-Path $cleanupRoot 'stage'
    New-Item -ItemType Directory -Path $cleanupStage -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $cleanupStage 'stale.txt') -Value 'must-not-ship'
    Reset-TrustedDirectory -Path $cleanupStage -Root $cleanupRoot
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $cleanupStage 'stale.txt'))) 'Trusted cleanup must remove stale files.'
    Assert-Fails { Reset-TrustedDirectory -Path $sibling -Root $cleanupRoot } 'Trusted cleanup must reject sibling paths.'

    $oldArtifact = Join-Path $cleanupRoot 'old.zip'
    Set-Content -LiteralPath $oldArtifact -Value 'stale-artifact'
    Remove-TrustedFile -Path $oldArtifact -Root $cleanupRoot
    Assert-True (-not (Test-Path -LiteralPath $oldArtifact)) 'Trusted file cleanup must remove stale artifacts.'
    Assert-Fails { Remove-TrustedFile -Path (Join-Path $sibling 'escape.txt') -Root $cleanupRoot } 'Trusted file cleanup must reject sibling paths.'

    Remove-Item -LiteralPath $cleanupStage -Force
    New-Item -ItemType Junction -Path $cleanupStage -Target $sibling | Out-Null
    Assert-Fails { Reset-TrustedDirectory -Path $cleanupStage -Root $cleanupRoot } 'Trusted cleanup must reject a reparse-point stage.'
    Assert-True (Test-Path -LiteralPath (Join-Path $sibling 'escape.txt')) 'Rejected cleanup must not modify the junction target.'
} finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedTest = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTest.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unsafe test cleanup.' }
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

$installerSource = Get-Content -Raw -LiteralPath $installer
Assert-True ($installerSource -match 'PrivilegesRequired=lowest') 'Installer must remain per-user.'
Assert-True ($installerSource -match 'function\s+InitializeSetup') 'Installer must reject elevated setup before installation.'
Assert-True ($installerSource -match 'function\s+InitializeUninstall') 'Installer must reject elevated uninstall.'
Assert-True ($installerSource -match 'IsAdmin') 'Installer must detect an elevated token.'
Assert-True ($installerSource -match 'Run as administrator') 'Elevation error must explain how to rerun setup.'
Assert-True ($installerSource -match 'Knack25\.MicrosoftWidgetsHelper\.Control\.v1') 'Installer must stop the helper through the same-user control pipe.'
Assert-True ($installerSource -match 'Result\s*:=\s*RequestHelperStop;') 'Uninstall must stop through the same-user control pipe before removing files.'
Assert-True ($installerSource -notmatch 'catch \[IO\.IOException\] \{ exit 0 \}') 'Control-pipe I/O failures must not be treated as a safe stop.'
Assert-True ($installerSource -notmatch '\[UninstallRun\]') 'Uninstall must not execute the installed helper.'
Assert-True ($installerSource -notmatch 'Exec\s*\(\s*HelperPath') 'Setup must not execute the installed helper.'

if (-not $SkipOutlookBuild) {
    $outlookRoot = Join-Path $repositoryRoot 'outlook-edge-widget'
    $sentinel = Join-Path $outlookRoot 'dist\stale-sentinel.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'must-not-ship'
    Push-Location $outlookRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Outlook build failed.' }
    } finally { Pop-Location }
    Assert-True (-not (Test-Path -LiteralPath $sentinel)) 'Outlook build retained stale dist content.'
}

Write-Host 'Release security tests passed.'
