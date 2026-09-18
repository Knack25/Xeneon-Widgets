$ErrorActionPreference = "Stop"
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
& (Join-Path $projectRoot "..\microsoft-widgets-helper\scripts\publish.ps1")
