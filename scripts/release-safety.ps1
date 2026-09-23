Set-StrictMode -Version Latest

function Test-ReleasePathWithin([string]$Candidate, [string]$Root, [switch]$AllowRoot) {
    $candidatePath = [IO.Path]::GetFullPath($Candidate).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($AllowRoot -and $candidatePath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    $rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    return $candidatePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NotReparsePoint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $attributes = [IO.File]::GetAttributes([IO.Path]::GetFullPath($Path))
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release paths cannot contain links or reparse points: $Path"
    }
}

function Initialize-TrustedDirectory([string]$Path, [string]$Anchor) {
    $anchorPath = [IO.Path]::GetFullPath($Anchor).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $targetPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $anchorPath -PathType Container)) { throw "Trusted release anchor does not exist: $anchorPath" }
    Assert-NotReparsePoint $anchorPath
    if (-not (Test-ReleasePathWithin $targetPath $anchorPath)) { throw "Release directory must stay inside its trusted anchor: $targetPath" }

    $relative = [IO.Path]::GetRelativePath($anchorPath, $targetPath)
    $current = $anchorPath
    foreach ($segment in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [IO.Path]::GetFullPath((Join-Path $current $segment))
        Assert-NotReparsePoint $current
        if (-not (Test-Path -LiteralPath $current)) { [void][IO.Directory]::CreateDirectory($current) }
        if (-not (Test-Path -LiteralPath $current -PathType Container)) { throw "Release directory component is not a directory: $current" }
    }

    return $targetPath
}

function Assert-TreeHasNoReparsePoints([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-NotReparsePoint $directory
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            Assert-NotReparsePoint $entry
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
        }
    }
}

function Reset-TrustedDirectory([string]$Path, [string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $targetPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { throw "Trusted release root does not exist: $rootPath" }
    Assert-NotReparsePoint $rootPath
    if (-not (Test-ReleasePathWithin $targetPath $rootPath)) { throw "Release cleanup target must stay inside its trusted root: $targetPath" }

    $relative = [IO.Path]::GetRelativePath($rootPath, $targetPath)
    $current = $rootPath
    foreach ($segment in $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [IO.Path]::GetFullPath((Join-Path $current $segment))
        Assert-NotReparsePoint $current
    }

    if (Test-Path -LiteralPath $targetPath) {
        if (-not (Test-Path -LiteralPath $targetPath -PathType Container)) { throw "Release cleanup target is not a directory: $targetPath" }
        Assert-TreeHasNoReparsePoints $targetPath
        Remove-Item -LiteralPath $targetPath -Recurse -Force
    }
    [void][IO.Directory]::CreateDirectory($targetPath)
    return $targetPath
}

function Remove-TrustedFile([string]$Path, [string]$Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $filePath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) { throw "Trusted release root does not exist: $rootPath" }
    Assert-NotReparsePoint $rootPath
    if (-not (Test-ReleasePathWithin $filePath $rootPath)) { throw "Release file must stay inside its trusted root: $filePath" }

    $relative = [IO.Path]::GetRelativePath($rootPath, $filePath)
    $current = $rootPath
    $segments = $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        $current = [IO.Path]::GetFullPath((Join-Path $current $segments[$index]))
        Assert-NotReparsePoint $current
    }

    if (Test-Path -LiteralPath $filePath) {
        Assert-NotReparsePoint $filePath
        if (Test-Path -LiteralPath $filePath -PathType Container) { throw "Release artifact is a directory, not a file: $filePath" }
        Remove-Item -LiteralPath $filePath -Force
    }
}
