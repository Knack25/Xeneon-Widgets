$ErrorActionPreference = 'Stop'

$pattern = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{36,}|AKIA[0-9A-Z]{16}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'
$findings = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)

function Find-TrackedSecretPattern([string]$Revision) {
    $arguments = @('grep', '-I', '-l', '-E', '--no-color', '-e', $pattern)
    if ($Revision) { $arguments += $Revision }
    $arguments += @('--', '.')
    $paths = @(& git @arguments 2>$null)
    $exitCode = $LASTEXITCODE
    if ($exitCode -gt 1) { throw "Secret-pattern scan failed for revision: $Revision" }
    foreach ($path in $paths) {
        $label = if ($Revision) { "$Revision`:$path" } else { "working-tree:$path" }
        [void]$findings.Add($label)
    }
}

Find-TrackedSecretPattern ''
$revisions = @(& git rev-list --all)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked Git history.' }
foreach ($revision in $revisions) { Find-TrackedSecretPattern $revision }

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Error "Tracked secret pattern found in $_" }
    throw "Tracked secret-pattern scan found $($findings.Count) matching file revision(s)."
}

Write-Host "Tracked secret-pattern scan passed across the working tree and $($revisions.Count) revision(s)."
# GitHub Actions propagates the last native exit code from a PowerShell step.
$global:LASTEXITCODE = 0
