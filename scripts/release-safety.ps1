Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Release tooling requires PowerShell 7 or later.' }

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
    $lease = Open-ReleaseSnapshot -Stage $Path -Manifest $manifest
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

function Get-ReleaseSnapshotName($Snapshot) {
    $builder = [Text.StringBuilder]::new()
    foreach ($entry in @($Snapshot.Entries | Sort-Object -Property Name)) {
        [void]$builder.Append($entry.Name).Append("`0").Append($entry.Length).Append("`0").Append($entry.Hash).Append("`n")
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($builder.ToString()))).ToLowerInvariant()
    return "release-$digest"
}

function Assert-CurrentReleasePointer([IO.Stream]$Stream, [string]$ExpectedName) {
    if ($null -eq $Stream -or -not $Stream.CanRead -or -not $Stream.CanSeek) { throw 'Current release pointer must be readable and seekable.' }
    $Stream.Position = 0
    $reader = [IO.StreamReader]::new($Stream, [Text.Encoding]::ASCII, $false, 1024, $true)
    try { $actual = $reader.ReadToEnd() } finally { $reader.Dispose(); $Stream.Position = 0 }
    if ($actual -ne $ExpectedName) { throw 'Current release pointer does not identify the verified snapshot.' }
}

function Resolve-VerifiedReleaseSnapshot(
    [string]$SnapshotRoot,
    [string]$CurrentPointer,
    [string]$TrustedParent,
    [string[]]$Manifest,
    [scriptblock]$Verifier
) {
    $parentPath = [IO.Path]::GetFullPath($TrustedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $snapshotRootPath = [IO.Path]::GetFullPath($SnapshotRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pointerPath = [IO.Path]::GetFullPath($CurrentPointer)
    if ((Split-Path -Parent $snapshotRootPath) -ne $parentPath) { throw 'Release snapshot root must be a direct child of its trusted parent.' }
    if ((Split-Path -Parent $pointerPath) -ne $parentPath) { throw 'Current release pointer must be a direct child of its trusted parent.' }
    if (-not (Test-Path -LiteralPath $snapshotRootPath -PathType Container)) { throw 'Release snapshot root does not exist.' }
    if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) { throw 'Current release pointer does not exist.' }
    Assert-NotReparsePoint $parentPath
    Assert-NotReparsePoint $snapshotRootPath
    Assert-NotReparsePoint $pointerPath

    $pointerStream = $null
    $snapshot = $null
    try {
        $pointerStream = [IO.File]::Open($pointerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $reader = [IO.StreamReader]::new($pointerStream, [Text.Encoding]::ASCII, $false, 1024, $true)
        try { $snapshotName = $reader.ReadToEnd() } finally { $reader.Dispose(); $pointerStream.Position = 0 }
        if ($snapshotName -notmatch '^release-[0-9a-f]{64}$') { throw 'Current release pointer is invalid.' }

        $snapshotPath = [IO.Path]::GetFullPath((Join-Path $snapshotRootPath $snapshotName))
        if ((Split-Path -Parent $snapshotPath) -ne $snapshotRootPath) { throw 'Current release pointer escapes the snapshot root.' }
        Assert-TreeHasNoReparsePoints $snapshotPath

        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $expectedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in $Manifest) {
            Assert-NormalizedReleaseEntry $name
            if (-not $expected.Add($name)) { throw "Duplicate release snapshot entry: $name" }
            $segments = $name.Split('/')
            for ($index = 1; $index -lt $segments.Count; $index++) {
                [void]$expectedDirectories.Add(($segments[0..($index - 1)] -join '/'))
            }
        }
        if ($expected.Count -eq 0) { throw 'Release snapshot manifest cannot be empty.' }
        $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($snapshotPath)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $relative = [IO.Path]::GetRelativePath($snapshotPath, $path).Replace('\', '/')
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Release entries cannot be links or reparse points: $relative" }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    if (-not $expectedDirectories.Contains($relative)) { throw "Unexpected release directory: $relative" }
                    $pending.Push($path)
                } else {
                    Assert-NormalizedReleaseEntry $relative
                    if (-not $expected.Contains($relative) -or -not $found.Add($relative)) { throw "Unexpected or duplicate release file: $relative" }
                }
            }
        }
        if ($found.Count -ne $expected.Count) { throw 'Published release inventory is incomplete.' }

        $snapshot = Open-ReleaseSnapshot -Stage $snapshotPath -Manifest $Manifest
        if ((Get-ReleaseSnapshotName $snapshot) -ne $snapshotName) { throw 'Activated release content does not match its content-addressed name.' }
        Assert-CurrentReleasePointer -Stream $pointerStream -ExpectedName $snapshotName
        if ($null -ne $Verifier) { & $Verifier $snapshot.Stage $snapshot }
        Assert-ReleaseSnapshotUnchanged $snapshot
        Assert-CurrentReleasePointer -Stream $pointerStream -ExpectedName $snapshotName
        return [pscustomobject]@{ SnapshotName = $snapshotName; Snapshot = $snapshot; PointerStream = $pointerStream; PointerPath = $pointerPath }
    } catch {
        if ($null -ne $snapshot) { Close-ReleaseSnapshot $snapshot }
        if ($null -ne $pointerStream) { $pointerStream.Dispose() }
        throw
    }
}

function Assert-DescribedReleaseArchive([IO.Stream]$Stream, [string]$DisplayName, [object[]]$ExpectedEntries) {
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $describedTotal = [long]0
    foreach ($entry in @($ExpectedEntries)) {
        $name = [string]$entry.name
        Assert-NormalizedReleaseEntry $name
        $length = [long]$entry.length
        if ($entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $length -lt 0 -or $length -gt 268435456) { throw "Archive descriptor is invalid: $DisplayName/$name" }
        $describedTotal += $length
        if ($describedTotal -gt 536870912) { throw "Archive descriptor exceeds the total size limit: $DisplayName" }
        $expected.Add($name, $entry)
    }
    if ($expected.Count -eq 0) { throw "Archive descriptor is empty: $DisplayName" }

    $found = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $Stream.Position = 0
    $zip = [IO.Compression.ZipArchive]::new($Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            Assert-NormalizedReleaseEntry $name
            if ([string]::IsNullOrEmpty($entry.Name) -or -not $found.Add($name) -or -not $expected.ContainsKey($name)) {
                throw "Archive inventory does not match its descriptor: $DisplayName/$name"
            }
            $attributeBits = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$entry.ExternalAttributes), 0)
            if ((($attributeBits -shr 16) -band 0xF000) -eq 0xA000 -or (($attributeBits -band 0xFFFF) -band [uint32][IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Archive links are not allowed: $DisplayName/$name"
            }
            $described = $expected[$name]
            if ($entry.Length -ne [long]$described.length) { throw "Archive entry length does not match: $DisplayName/$name" }
            $entryStream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)).ToLowerInvariant() }
            finally { $entryStream.Dispose() }
            if ($hash -ne [string]$described.sha256) { throw "Archive entry hash does not match: $DisplayName/$name" }
        }
    } finally {
        $zip.Dispose()
        $Stream.Position = 0
    }
    if ($found.Count -ne $expected.Count) { throw "Archive inventory is incomplete: $DisplayName" }
}

function Resolve-VerifiedReleaseDescriptor([string]$TrustedParent) {
    $parentPath = [IO.Path]::GetFullPath($TrustedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $snapshotRoot = Join-Path $parentPath 'release-snapshots'
    $currentPointer = Join-Path $parentPath 'release-current.txt'
    if (-not (Test-Path -LiteralPath $currentPointer -PathType Leaf)) { throw 'Current release pointer does not exist.' }
    Assert-NotReparsePoint $currentPointer
    $snapshotName = Get-Content -Raw -LiteralPath $currentPointer
    if ($snapshotName -notmatch '^release-[0-9a-f]{64}$') { throw 'Current release pointer is invalid.' }
    $snapshotPath = [IO.Path]::GetFullPath((Join-Path $snapshotRoot $snapshotName))
    if ((Split-Path -Parent $snapshotPath) -ne [IO.Path]::GetFullPath($snapshotRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)) {
        throw 'Current release pointer escapes the snapshot root.'
    }
    $descriptorPath = Join-Path $snapshotPath 'RELEASE-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) { throw 'Release descriptor is missing.' }
    Assert-NotReparsePoint $descriptorPath
    try { $descriptor = Get-Content -Raw -LiteralPath $descriptorPath | ConvertFrom-Json }
    catch { throw 'Release descriptor is not valid JSON.' }
    if ([int]$descriptor.schemaVersion -ne 1) { throw 'Release descriptor schema is unsupported.' }
    $describedFiles = @($descriptor.files)
    if ($describedFiles.Count -eq 0) { throw 'Release descriptor contains no files.' }

    $manifest = [Collections.Generic.List[string]]::new()
    $describedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $describedFiles) {
        $name = [string]$file.name
        Assert-NormalizedReleaseEntry $name
        if ($name -in @('RELEASE-MANIFEST.json', 'SHA256SUMS.txt') -or -not $describedNames.Add($name)) { throw "Release descriptor has an invalid file name: $name" }
        if ($file.sha256 -notmatch '^[0-9a-f]{64}$' -or [long]$file.length -lt 0) { throw "Release descriptor entry is invalid: $name" }
        $manifest.Add($name)
    }
    $manifest.Add('RELEASE-MANIFEST.json')
    $manifest.Add('SHA256SUMS.txt')

    $verifier = {
        param($publishedPath, $published)
        $byName = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $published.Entries) { $byName.Add($entry.Name, $entry) }
        foreach ($file in $describedFiles) {
            $entry = $byName[[string]$file.name]
            if ($entry.Length -ne [long]$file.length -or $entry.Hash -ne [string]$file.sha256) { throw "Release file does not match its descriptor: $($file.name)" }
            $archiveEntries = @()
            if ($null -ne $file.PSObject.Properties['archiveEntries']) { $archiveEntries = @($file.archiveEntries) }
            if ($entry.Name -match '\.(zip|icuewidget)$' -and $archiveEntries.Count -eq 0) { throw "Release archive has no internal descriptor: $($entry.Name)" }
            if ($archiveEntries.Count -gt 0) { Assert-DescribedReleaseArchive -Stream $entry.Stream -DisplayName $entry.Name -ExpectedEntries $archiveEntries }
        }

        $checksumTargets = @($describedFiles | ForEach-Object { [string]$_.name }) + 'RELEASE-MANIFEST.json'
        $checksumEntry = $byName['SHA256SUMS.txt']
        $checksumEntry.Stream.Position = 0
        $reader = [IO.StreamReader]::new($checksumEntry.Stream, [Text.Encoding]::ASCII, $true, 1024, $true)
        try { $checksumLines = @($reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ }) }
        finally { $reader.Dispose(); $checksumEntry.Stream.Position = 0 }
        if ($checksumLines.Count -ne $checksumTargets.Count) { throw 'Release checksum inventory is incomplete.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in $checksumLines) {
            if ($line -notmatch '^([0-9a-f]{64})  ([^/\\]+)$') { throw "Release checksum line is invalid: $line" }
            $name = $Matches[2]
            if ($name -notin $checksumTargets -or -not $seen.Add($name) -or $byName[$name].Hash -ne $Matches[1]) { throw "Release checksum does not match: $name" }
        }
    }

    return Resolve-VerifiedReleaseSnapshot -SnapshotRoot $snapshotRoot -CurrentPointer $currentPointer -TrustedParent $parentPath -Manifest $manifest.ToArray() -Verifier $verifier
}

function Publish-VerifiedReleaseSnapshot(
    [string]$Source,
    [string]$SnapshotRoot,
    [string]$CurrentPointer,
    [string]$TrustedParent,
    $Snapshot,
    [scriptblock]$Verifier,
    [scriptblock]$AfterSnapshotValidationProbe,
    [scriptblock]$AfterActivationProbe,
    [scriptblock]$AfterFinalValidationProbe
) {
    $sourcePath = [IO.Path]::GetFullPath($Source).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parentPath = [IO.Path]::GetFullPath($TrustedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $snapshotRootPath = [IO.Path]::GetFullPath($SnapshotRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pointerPath = [IO.Path]::GetFullPath($CurrentPointer)
    if ((Split-Path -Parent $snapshotRootPath) -ne $parentPath) { throw 'Release snapshot root must be a direct child of its trusted parent.' }
    if ((Split-Path -Parent $pointerPath) -ne $parentPath) { throw 'Current release pointer must be a direct child of its trusted parent.' }
    if (-not (Test-ReleasePathWithin $sourcePath $parentPath)) { throw 'Release candidate must stay inside its trusted parent.' }
    Assert-NotReparsePoint $parentPath
    if (-not (Test-Path -LiteralPath $snapshotRootPath)) { [void][IO.Directory]::CreateDirectory($snapshotRootPath) }
    Assert-NotReparsePoint $snapshotRootPath

    Assert-ReleaseSnapshotUnchanged $Snapshot
    $expectedSnapshot = [pscustomobject]@{
        Stage = $Snapshot.Stage
        Entries = @($Snapshot.Entries | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Length = $_.Length; Hash = $_.Hash } })
        Handles = @()
    }
    $snapshotName = Get-ReleaseSnapshotName $expectedSnapshot
    $snapshotPath = Join-Path $snapshotRootPath $snapshotName
    Close-ReleaseSnapshot $Snapshot

    $publishedSnapshot = $null
    $pointerStream = $null
    $movablePointer = $null
    $pointerTemp = $null
    $createdSnapshot = $false
    $activated = $false
    $previousPointer = $null
    if (Test-Path -LiteralPath $pointerPath -PathType Leaf) {
        Assert-NotReparsePoint $pointerPath
        $previousPointer = Get-Content -Raw -LiteralPath $pointerPath
    }
    try {
        if (-not (Test-Path -LiteralPath $snapshotPath)) {
            [IO.Directory]::Move($sourcePath, $snapshotPath)
            $createdSnapshot = $true
        }
        $publishedSnapshot = Open-VerifiedReleaseDirectory -Path $snapshotPath -Snapshot $expectedSnapshot -Verifier $Verifier
        if ($null -ne $AfterSnapshotValidationProbe) { & $AfterSnapshotValidationProbe $snapshotPath $pointerPath }

        do {
            $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
            $pointerTemp = Join-Path $parentPath "release-current-$random.tmp"
        } while (Test-Path -LiteralPath $pointerTemp)
        $pointerBytes = [Text.Encoding]::ASCII.GetBytes($snapshotName)
        $pointerWriter = [IO.File]::Open($pointerTemp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $pointerWriter.Write($pointerBytes, 0, $pointerBytes.Length)
            $pointerWriter.Flush($true)
        } finally { $pointerWriter.Dispose() }
        $movablePointer = [IO.File]::Open($pointerTemp, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
        Assert-CurrentReleasePointer -Stream $movablePointer -ExpectedName $snapshotName
        [IO.File]::Move($pointerTemp, $pointerPath, $true)
        $activated = $true
        $pointerTemp = $null
        $pointerStream = [IO.File]::Open($pointerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        Assert-CurrentReleasePointer -Stream $pointerStream -ExpectedName $snapshotName
        $movablePointer.Dispose()
        $movablePointer = $null

        if ($null -ne $AfterActivationProbe) { & $AfterActivationProbe $snapshotPath $pointerPath }
        Assert-ReleaseSnapshotUnchanged $publishedSnapshot
        if ($null -ne $Verifier) { & $Verifier $publishedSnapshot.Stage $publishedSnapshot }
        Assert-CurrentReleasePointer -Stream $pointerStream -ExpectedName $snapshotName
        if ($null -ne $AfterFinalValidationProbe) { & $AfterFinalValidationProbe $snapshotPath $pointerPath }
        Assert-ReleaseSnapshotUnchanged $publishedSnapshot
        Assert-CurrentReleasePointer -Stream $pointerStream -ExpectedName $snapshotName

        return [pscustomobject]@{ SnapshotName = $snapshotName; Snapshot = $publishedSnapshot; PointerStream = $pointerStream; PointerPath = $pointerPath }
    } catch {
        $failure = $_
        if ($null -ne $pointerStream) { $pointerStream.Dispose() }
        if ($null -ne $movablePointer) { $movablePointer.Dispose() }
        if ($null -ne $publishedSnapshot) { Close-ReleaseSnapshot $publishedSnapshot }
        if ($null -ne $pointerTemp -and (Test-Path -LiteralPath $pointerTemp)) { Remove-Item -LiteralPath $pointerTemp -Force }
        if ($activated) {
            if ($null -ne $previousPointer) {
                do {
                    $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                    $restoreTemp = Join-Path $parentPath "release-restore-$random.tmp"
                } while (Test-Path -LiteralPath $restoreTemp)
                [IO.File]::WriteAllText($restoreTemp, $previousPointer, [Text.Encoding]::ASCII)
                [IO.File]::Move($restoreTemp, $pointerPath, $true)
            } elseif ((Test-Path -LiteralPath $pointerPath -PathType Leaf) -and ((Get-Content -Raw -LiteralPath $pointerPath) -eq $snapshotName)) {
                Remove-Item -LiteralPath $pointerPath -Force
            }
        }
        if ($createdSnapshot -and (Test-Path -LiteralPath $snapshotPath -PathType Container)) {
            do {
                $random = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
                $quarantine = Join-Path $snapshotRootPath "release-quarantine-$random"
            } while (Test-Path -LiteralPath $quarantine)
            [IO.Directory]::Move($snapshotPath, $quarantine)
        }
        throw $failure
    }
}

function Close-VerifiedReleaseSnapshot($Lease) {
    if ($null -eq $Lease) { return }
    if ($null -ne $Lease.PointerStream) { $Lease.PointerStream.Dispose() }
    if ($null -ne $Lease.Snapshot) { Close-ReleaseSnapshot $Lease.Snapshot }
}
