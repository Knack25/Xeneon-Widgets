$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'audit-dependencies.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Dependency auditing failed.' }

& (Join-Path $PSScriptRoot '..\planner-edge-widget\scripts\verify.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Repository security verification failed.' }
