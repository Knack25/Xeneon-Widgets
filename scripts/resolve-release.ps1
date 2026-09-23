param([string]$ReleaseRoot = (Join-Path $PSScriptRoot '..\dist'))

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'The release resolver requires PowerShell 7 or later.' }
. (Join-Path $PSScriptRoot 'release-safety.ps1')

$lease = Resolve-VerifiedReleaseDescriptor -TrustedParent $ReleaseRoot
try {
    Write-Output $lease.Snapshot.Stage
} finally {
    Close-VerifiedReleaseSnapshot $lease
}
