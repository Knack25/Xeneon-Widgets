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

function New-ReleaseWorkspace([string]$Parent, [string]$Prefix = 'build') {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) { throw "Release workspace parent does not exist: $parentPath" }
    Assert-NotReparsePoint $parentPath
    if ($Prefix -notmatch '^[A-Za-z0-9._-]+$') { throw "Release workspace prefix is invalid: $Prefix" }

    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
        $candidate = Join-Path $parentPath "$Prefix-$random"
        try {
            $created = New-Item -ItemType Directory -Path $candidate -ErrorAction Stop
            Assert-NotReparsePoint $created.FullName
            return [IO.Path]::GetFullPath($created.FullName)
        } catch [IO.IOException] {
            continue
        }
    }

    throw 'Could not create a unique release workspace.'
}

function Assert-NormalizedReleaseEntry([string]$Entry) {
    if ([string]::IsNullOrWhiteSpace($Entry) -or $Entry.Contains('\')) { throw "Archive entry is not normalized: $Entry" }
    if ([IO.Path]::IsPathRooted($Entry) -or $Entry.StartsWith('/') -or $Entry.Contains(':')) { throw "Archive entry must be relative: $Entry" }
    $segments = $Entry.Split('/')
    if ($segments.Count -eq 0 -or $segments | Where-Object { $_ -in @('', '.', '..') }) {
        throw "Archive entry escapes or is not normalized: $Entry"
    }
}

function Open-ReleaseSnapshot([string]$Stage, [string[]]$Manifest, [switch]$AllowDeleteShare) {
    $stagePath = [IO.Path]::GetFullPath($Stage).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $stagePath -PathType Container)) { throw "Release stage does not exist: $stagePath" }
    Assert-TreeHasNoReparsePoints $stagePath
    if ($Manifest.Count -eq 0) { throw 'Release snapshot manifest cannot be empty.' }

    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    $entries = [Collections.Generic.List[object]]::new()
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($relative in $Manifest) {
            Assert-NormalizedReleaseEntry $relative
            if (-not $names.Add($relative)) { throw "Duplicate release snapshot entry: $relative" }
            $path = [IO.Path]::GetFullPath((Join-Path $stagePath ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            if (-not (Test-ReleasePathWithin $path $stagePath) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Release snapshot file is missing or escapes its stage: $relative"
            }
            Assert-NotReparsePoint $path
            $share = if ($AllowDeleteShare) { [IO.FileShare]::Read -bor [IO.FileShare]::Delete } else { [IO.FileShare]::Read }
            $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            $handles.Add($stream)
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            $stream.Position = 0
            $entries.Add([pscustomobject]@{ Name = $relative; Path = $path; Length = $stream.Length; Hash = $hash; Stream = $stream })
        }
        return [pscustomobject]@{ Stage = $stagePath; Entries = $entries.ToArray(); Handles = $handles.ToArray() }
    } catch {
        foreach ($handle in $handles) { $handle.Dispose() }
        throw
    }
}

function Close-ReleaseSnapshot($Snapshot) {
    if ($null -eq $Snapshot) { return }
    foreach ($handle in $Snapshot.Handles) { $handle.Dispose() }
}

function Assert-ReleaseSnapshotUnchanged($Snapshot) {
    foreach ($entry in $Snapshot.Entries) {
        if ($entry.Stream.Length -ne $entry.Length) { throw "Release snapshot length changed: $($entry.Name)" }
        $entry.Stream.Position = 0
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.Stream)).ToLowerInvariant()
        $entry.Stream.Position = 0
        if ($hash -ne $entry.Hash) { throw "Release snapshot hash changed: $($entry.Name)" }
    }
}

function Assert-ReleaseSnapshotsMatch($Expected, $Actual) {
    Assert-ReleaseSnapshotUnchanged $Actual
    if ($Expected.Entries.Count -ne $Actual.Entries.Count) { throw 'Published release inventory changed.' }
    $expectedEntries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Expected.Entries) { $expectedEntries.Add($entry.Name, $entry) }
    foreach ($entry in $Actual.Entries) {
        if (-not $expectedEntries.ContainsKey($entry.Name)) { throw "Unexpected published release file: $($entry.Name)" }
        $expected = $expectedEntries[$entry.Name]
        if ($entry.Length -ne $expected.Length -or $entry.Hash -ne $expected.Hash) {
            throw "Published release file changed: $($entry.Name)"
        }
    }
}

function Open-VerifiedReleaseDirectory([string]$Path, $Snapshot, [scriptblock]$Verifier) {
    $manifest = @($Snapshot.Entries | ForEach-Object { $_.Name })
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $manifest) { [void]$expected.Add($name) }
    Assert-TreeHasNoReparsePoints $Path
    $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in [IO.Directory]::EnumerateFiles([IO.Path]::GetFullPath($Path), '*', [IO.SearchOption]::AllDirectories)) {
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Path), $file).Replace('\', '/')
        Assert-NormalizedReleaseEntry $relative
        if (-not $expected.Contains($relative)) { throw "Unexpected published release file: $relative" }
        if (-not $found.Add($relative)) { throw "Duplicate published release file: $relative" }
    }
    if ($found.Count -ne $expected.Count) { throw 'Published release inventory is incomplete.' }
    $lease = Open-ReleaseSnapshot -Stage $Path -Manifest $manifest -AllowDeleteShare
    try {
        Assert-ReleaseSnapshotsMatch -Expected $Snapshot -Actual $lease
        if ($null -ne $Verifier) { & $Verifier $lease.Stage $lease }
        return $lease
    } catch {
        Close-ReleaseSnapshot $lease
        throw
    }
}

function Assert-ReleaseArchive(
    [string]$Archive,
    $Snapshot,
    [long]$MaxEntryBytes = 268435456,
    [long]$MaxTotalBytes = 536870912
) {
    $archivePath = [IO.Path]::GetFullPath($Archive)
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Release archive does not exist: $archivePath" }
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-ReleaseArchiveStream -Stream $stream -DisplayPath $archivePath -Snapshot $Snapshot -MaxEntryBytes $MaxEntryBytes -MaxTotalBytes $MaxTotalBytes
    } finally { $stream.Dispose() }
}

function Assert-ReleaseArchiveStream(
    [IO.Stream]$Stream,
    [string]$DisplayPath,
    $Snapshot,
    [long]$MaxEntryBytes = 268435456,
    [long]$MaxTotalBytes = 536870912
) {
    if ($null -eq $Stream -or -not $Stream.CanRead -or -not $Stream.CanSeek) { throw 'Release archive stream must be readable and seekable.' }
    if ($MaxEntryBytes -le 0 -or $MaxTotalBytes -le 0) { throw 'Archive size limits must be positive.' }
    Assert-ReleaseSnapshotUnchanged $Snapshot

    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Snapshot.Entries) { $expected.Add($entry.Name, $entry) }
    $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total = [long]0
    $Stream.Position = 0
    try {
        $zip = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName
                Assert-NormalizedReleaseEntry $name
                if ([string]::IsNullOrEmpty($entry.Name)) { throw "Archive directory entries are not allowed: $name" }
                if (-not $found.Add($name)) { throw "Duplicate or case-colliding archive entry: $name" }
                if (-not $expected.ContainsKey($name)) { throw "Unexpected archive entry: $name" }
                if ($entry.Length -gt $MaxEntryBytes) { throw "Archive entry exceeds the size limit: $name" }
                if ([long]$entry.Length -gt ([long]::MaxValue - $total)) { throw 'Archive uncompressed size overflowed.' }
                $total += [long]$entry.Length
                if ($total -gt $MaxTotalBytes) { throw 'Archive exceeds the total uncompressed size limit.' }

                $attributeBits = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$entry.ExternalAttributes), 0)
                $unixType = (($attributeBits -shr 16) -band 0xF000)
                $dosAttributes = ($attributeBits -band 0xFFFF)
                if ($unixType -eq 0xA000 -or ($dosAttributes -band [uint32][IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Archive links are not allowed: $name"
                }

                $entryStream = $entry.Open()
                try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant() }
                finally { $entryStream.Dispose() }
                $source = $expected[$name]
                if ($entry.Length -ne $source.Length -or $hash -ne $source.Hash) { throw "Archive entry does not match its immutable source: $name" }
            }
        } finally { $zip.Dispose() }
    } finally { $Stream.Position = 0 }

    foreach ($entry in $Snapshot.Entries) {
        if (-not $found.Contains($entry.Name)) { throw "Missing archive entry: $($entry.Name)" }
    }
    Assert-ReleaseSnapshotUnchanged $Snapshot
    Write-Host "Verified release archive: $($found.Count) entries in $DisplayPath"
}

function Publish-ReleaseFile([string]$Source, [string]$Destination, [string]$TrustedParent) {
    $sourcePath = [IO.Path]::GetFullPath($Source)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $parentPath = [IO.Path]::GetFullPath($TrustedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Release source does not exist: $sourcePath" }
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) { throw "Release publication parent does not exist: $parentPath" }
    Assert-NotReparsePoint $sourcePath
    Assert-NotReparsePoint $parentPath
    if ((Split-Path -Parent $destinationPath) -ne $parentPath) { throw "Release publication must target a direct child of its trusted parent: $destinationPath" }
    if ([IO.Path]::GetPathRoot($sourcePath) -ne [IO.Path]::GetPathRoot($destinationPath)) { throw 'Release publication must stay on one volume.' }
    if (Test-Path -LiteralPath $destinationPath -PathType Container) { throw "Release destination is a directory: $destinationPath" }
    if (Test-Path -LiteralPath $destinationPath) { Assert-NotReparsePoint $destinationPath }
    [IO.File]::Move($sourcePath, $destinationPath, $true)
}

function Open-VerifiedReleaseArchive(
    [string]$Archive,
    $Snapshot,
    [scriptblock]$WhileLockedProbe,
    [long]$MaxEntryBytes = 268435456,
    [long]$MaxTotalBytes = 536870912
) {
    $archivePath = [IO.Path]::GetFullPath($Archive)
    Assert-NotReparsePoint $archivePath
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($null -ne $WhileLockedProbe) { & $WhileLockedProbe }
        Assert-ReleaseArchiveStream -Stream $stream -DisplayPath $archivePath -Snapshot $Snapshot -MaxEntryBytes $MaxEntryBytes -MaxTotalBytes $MaxTotalBytes
        return [pscustomobject]@{ Path = $archivePath; Stream = $stream }
    } catch {
        $stream.Dispose()
        throw
    }
}

function Close-VerifiedReleaseArchive($Lease) {
    if ($null -ne $Lease -and $null -ne $Lease.Stream) { $Lease.Stream.Dispose() }
}

function Publish-VerifiedReleaseArchive(
    [string]$Source,
    [string]$Destination,
    [string]$TrustedParent,
    $Snapshot,
    [scriptblock]$AfterMoveProbe,
    [scriptblock]$WhileLockedProbe,
    [long]$MaxEntryBytes = 268435456,
    [long]$MaxTotalBytes = 536870912
) {
    Publish-ReleaseFile -Source $Source -Destination $Destination -TrustedParent $TrustedParent
    if ($null -ne $AfterMoveProbe) { & $AfterMoveProbe }
    return Open-VerifiedReleaseArchive -Archive $Destination -Snapshot $Snapshot -WhileLockedProbe $WhileLockedProbe -MaxEntryBytes $MaxEntryBytes -MaxTotalBytes $MaxTotalBytes
}

function New-InnoFileManifest($Snapshot, [string]$Output, [string[]]$TemporaryEntries = @()) {
    Assert-ReleaseSnapshotUnchanged $Snapshot
    $outputPath = [IO.Path]::GetFullPath($Output)
    if (Test-ReleasePathWithin $outputPath $Snapshot.Stage -AllowRoot) { throw 'Inno manifest must be written outside the immutable source stage.' }
    $temporary = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $TemporaryEntries) {
        Assert-NormalizedReleaseEntry $name
        if (-not $temporary.Add($name)) { throw "Duplicate temporary Inno entry: $name" }
    }
    $lines = foreach ($entry in $Snapshot.Entries) {
        $relative = $entry.Name.Replace('/', '\')
        $directory = [IO.Path]::GetDirectoryName($relative)
        $destination = if ([string]::IsNullOrEmpty($directory)) { '{app}' } else { "{app}\$directory" }
        "Source: `"$($entry.Path)`"; DestDir: `"$destination`"; Flags: ignoreversion"
        if ($temporary.Contains($entry.Name)) {
            "Source: `"$($entry.Path)`"; Flags: dontcopy"
        }
    }
    foreach ($name in $temporary) {
        if (-not ($Snapshot.Entries | Where-Object { $_.Name.Equals($name, [StringComparison]::OrdinalIgnoreCase) })) {
            throw "Temporary Inno entry is not part of the immutable snapshot: $name"
        }
    }
    [IO.File]::WriteAllLines($outputPath, $lines, [Text.UTF8Encoding]::new($false))
    Assert-ReleaseSnapshotUnchanged $Snapshot
    $stream = [IO.File]::Open($outputPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    return [pscustomobject]@{ Path = $outputPath; Stream = $stream }
}

function Close-InnoFileManifest($Lease) {
    if ($null -ne $Lease -and $null -ne $Lease.Stream) { $Lease.Stream.Dispose() }
}

function Publish-ReleaseDirectory(
    [string]$Source,
    [string]$Destination,
    [string]$TrustedParent,
    $Snapshot,
    [scriptblock]$Verifier,
    [scriptblock]$AfterHandoffMoveProbe
) {
    $sourcePath = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parentPath = [IO.Path]::GetFullPath($TrustedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) { throw "Release directory source does not exist: $sourcePath" }
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) { throw "Release publication parent does not exist: $parentPath" }
    Assert-NotReparsePoint $sourcePath
    Assert-NotReparsePoint $parentPath
    if (-not (Test-ReleasePathWithin $sourcePath $parentPath)) { throw "Release directory source must be controlled under its trusted parent: $sourcePath" }
    if ((Split-Path -Parent $destinationPath) -ne $parentPath) { throw "Release publication must target a direct child of its trusted parent: $destinationPath" }
    if ([IO.Path]::GetPathRoot($sourcePath) -ne [IO.Path]::GetPathRoot($destinationPath)) { throw 'Release publication must stay on one volume.' }

    if ($null -eq $Snapshot) {
        $quarantine = $null
        if (Test-Path -LiteralPath $destinationPath) {
            if (-not (Test-Path -LiteralPath $destinationPath -PathType Container)) { throw "Release destination is not a directory: $destinationPath" }
            Assert-NotReparsePoint $destinationPath
            do {
                $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                $quarantine = Join-Path $parentPath "release-quarantine-$random"
            } while (Test-Path -LiteralPath $quarantine)
            [IO.Directory]::Move($destinationPath, $quarantine)
        }

        try {
            [IO.Directory]::Move($sourcePath, $destinationPath)
        } catch {
            if ($null -ne $quarantine -and (Test-Path -LiteralPath $quarantine) -and -not (Test-Path -LiteralPath $destinationPath)) {
                [IO.Directory]::Move($quarantine, $destinationPath)
            }
            throw
        }
        return $destinationPath
    }

    Assert-ReleaseSnapshotUnchanged $Snapshot
    $expectedSnapshot = [pscustomobject]@{
        Stage = $Snapshot.Stage
        Entries = @($Snapshot.Entries | ForEach-Object {
            [pscustomobject]@{ Name = $_.Name; Length = $_.Length; Hash = $_.Hash }
        })
        Handles = @()
    }
    Close-ReleaseSnapshot $Snapshot
    do {
        $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
        $handoff = Join-Path $parentPath "release-handoff-$random"
    } while (Test-Path -LiteralPath $handoff)

    $handoffLease = $null
    $publishedLease = $null
    $quarantine = $null
    $candidateIsAtDestination = $false
    try {
        [IO.Directory]::Move($sourcePath, $handoff)
        if ($null -ne $AfterHandoffMoveProbe) { & $AfterHandoffMoveProbe $handoff }
        $handoffLease = Open-VerifiedReleaseDirectory -Path $handoff -Snapshot $expectedSnapshot -Verifier $Verifier

        if (Test-Path -LiteralPath $destinationPath) {
            if (-not (Test-Path -LiteralPath $destinationPath -PathType Container)) { throw "Release destination is not a directory: $destinationPath" }
            Assert-NotReparsePoint $destinationPath
            do {
                $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                $quarantine = Join-Path $parentPath "release-quarantine-$random"
            } while (Test-Path -LiteralPath $quarantine)
            [IO.Directory]::Move($destinationPath, $quarantine)
        }

        Close-ReleaseSnapshot $handoffLease
        $handoffLease = $null
        [IO.Directory]::Move($handoff, $destinationPath)
        $candidateIsAtDestination = $true
        $publishedLease = Open-VerifiedReleaseDirectory -Path $destinationPath -Snapshot $expectedSnapshot -Verifier $Verifier
        return $publishedLease
    } catch {
        if ($null -ne $publishedLease) { Close-ReleaseSnapshot $publishedLease; $publishedLease = $null }
        if ($candidateIsAtDestination -and (Test-Path -LiteralPath $destinationPath -PathType Container)) {
            do {
                $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                $failed = Join-Path $parentPath "release-quarantine-$random"
            } while (Test-Path -LiteralPath $failed)
            [IO.Directory]::Move($destinationPath, $failed)
        } elseif (Test-Path -LiteralPath $handoff -PathType Container) {
            do {
                $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                $failed = Join-Path $parentPath "release-quarantine-$random"
            } while (Test-Path -LiteralPath $failed)
            [IO.Directory]::Move($handoff, $failed)
        }
        if ($null -ne $quarantine -and (Test-Path -LiteralPath $quarantine) -and -not (Test-Path -LiteralPath $destinationPath)) {
            [IO.Directory]::Move($quarantine, $destinationPath)
        }
        throw
    } finally {
        if ($null -ne $handoffLease) { Close-ReleaseSnapshot $handoffLease }
    }
}
