param(
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory)][string]$Manifest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-IsWithin([string]$Candidate, [string]$Root) {
    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $Candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

$stageRoot = [IO.Path]::GetFullPath($Stage).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $stageRoot -PathType Container)) {
    throw "Release stage does not exist: $stageRoot"
}

$stageAttributes = [IO.File]::GetAttributes($stageRoot)
if (($stageAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Release stage cannot be a reparse point: $stageRoot"
}

try { $manifestEntries = @(ConvertFrom-Json -InputObject $Manifest) }
catch { throw 'Release manifest must be a JSON array of relative file paths.' }
if ($manifestEntries.Count -eq 0) { throw 'Release manifest cannot be empty.' }

$expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$expectedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entryValue in $manifestEntries) {
    if ($entryValue -isnot [string] -or [string]::IsNullOrWhiteSpace($entryValue)) {
        throw 'Release manifest entries must be non-empty strings.'
    }

    $entry = $entryValue.Replace('\', '/').Trim()
    if ([IO.Path]::IsPathRooted($entry) -or $entry.Contains(':')) {
        throw "Release manifest entry must be relative: $entry"
    }

    $segments = $entry.Split('/')
    if ($segments.Count -eq 0 -or $segments | Where-Object { $_ -in @('', '.', '..') }) {
        throw "Release manifest entry escapes or is not normalized: $entry"
    }

    $resolved = [IO.Path]::GetFullPath((Join-Path $stageRoot ($segments -join [IO.Path]::DirectorySeparatorChar)))
    if (-not (Test-IsWithin $resolved $stageRoot)) {
        throw "Release manifest entry escapes the stage: $entry"
    }
    if (-not $expectedFiles.Add($entry)) { throw "Duplicate release manifest entry: $entry" }

    for ($index = 1; $index -lt $segments.Count; $index++) {
        [void]$expectedDirectories.Add(($segments[0..($index - 1)] -join '/'))
    }
}

$foundFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($stageRoot)
while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
        $resolved = [IO.Path]::GetFullPath($path)
        if (-not (Test-IsWithin $resolved $stageRoot)) { throw "Release entry escapes the stage: $resolved" }
        $attributes = [IO.File]::GetAttributes($resolved)
        $relative = [IO.Path]::GetRelativePath($stageRoot, $resolved).Replace('\', '/')
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release entries cannot be links or reparse points: $relative"
        }

        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            if (-not $expectedDirectories.Contains($relative)) { throw "Unexpected release directory: $relative" }
            $pending.Push($resolved)
        } else {
            if (-not $expectedFiles.Contains($relative)) { throw "Unexpected release file: $relative" }
            [void]$foundFiles.Add($relative)
        }
    }
}

foreach ($entry in $expectedFiles) {
    if (-not $foundFiles.Contains($entry)) { throw "Missing release file: $entry" }
}

Write-Host "Verified release inventory: $($foundFiles.Count) files in $stageRoot"
