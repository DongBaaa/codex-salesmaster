function Get-DesktopPayloadManifest {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$IconFileName
    )
    $root = Get-Item -LiteralPath $SourceRoot -Force -ErrorAction Stop
    if (-not $root.PSIsContainer) { throw 'Desktop payload root must be a directory.' }
    for ($parent = $root; $null -ne $parent; $parent = $parent.Parent) {
        if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Desktop payload must not traverse a reparse point.'
        }
    }
    $entries = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Queue[object]
    $pending.Enqueue($root)
    $prefix = $root.FullName.TrimEnd('\') + '\'
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Dequeue().FullName -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Desktop payload must not contain a reparse point.'
            }
            if ($item.PSIsContainer) { $pending.Enqueue($item); continue }
            $entries.Add([pscustomobject]@{
                Path = $item.FullName.Substring($prefix.Length).Replace('\', '/')
                Length = $item.Length
                Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
            })
        }
    }
    if ($entries.Count -eq 0) { throw 'Desktop payload must not be empty.' }
    return [pscustomobject]@{
        SchemaVersion = 1
        Version = $Version
        IconFileName = $IconFileName
        Files = @($entries.ToArray() | Sort-Object Path)
    }
}

function Assert-DesktopPayloadArchive {
    param([string]$ArchivePath, [object]$Manifest)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $files = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/').StartsWith('App/', [StringComparison]::OrdinalIgnoreCase) -and -not $_.FullName.Replace('\', '/').EndsWith('/') })
        if ($files.Count -ne @($Manifest.Files).Count) { throw 'ZIP desktop payload file count mismatch.' }
        $expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $Manifest.Files) { $expected.Add('App/' + $file.Path, $file) }
        foreach ($entry in $files) {
            $entryPath = $entry.FullName.Replace('\', '/')
            if (-not $expected.ContainsKey($entryPath)) { throw 'Unexpected or duplicate ZIP desktop payload file.' }
            $file = $expected[$entryPath]
            $stream = $entry.Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
            finally { $hasher.Dispose(); $stream.Dispose() }
            if ($file.Length -ne $entry.Length -or $file.Sha256 -ne $hash) { throw "ZIP desktop payload hash mismatch: $($entry.FullName)" }
            [void]$expected.Remove($entryPath)
        }
        $manifestEntries = @($archive.Entries | Where-Object { $_.FullName -ieq 'desktop-payload.json' })
        if ($manifestEntries.Count -ne 1) { throw 'ZIP desktop payload manifest missing or duplicated.' }
        $reader = New-Object IO.StreamReader ($manifestEntries[0].Open())
        try { $archivedManifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if (($archivedManifest | ConvertTo-Json -Depth 5 -Compress) -cne ($Manifest | ConvertTo-Json -Depth 5 -Compress)) {
            throw 'ZIP desktop payload manifest mismatch.'
        }
    }
    finally { $archive.Dispose() }
}

function Assert-DesktopPayloadManifest {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Version
    )
    if ($Manifest.SchemaVersion -ne 1 -or $Manifest.Version -cne $Version) {
        throw 'Desktop payload manifest schema or version mismatch.'
    }
    $icon = [string]$Manifest.IconFileName
    if ([string]::IsNullOrWhiteSpace($icon) -or $icon -match '[\\/:]' -or
        $icon -in @('.', '..') -or [IO.Path]::GetExtension($icon) -ne '.ico') {
        throw 'Desktop payload manifest icon must be one ICO file name.'
    }
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($Manifest.Files)) {
        $path = [string]$file.Path
        if ([string]::IsNullOrWhiteSpace($path) -or $path -match '(^/|\\|:|(^|/)\.\.?(/|$)|//)' -or
            $path.EndsWith('/') -or $file.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            [long]$file.Length -lt 0 -or $expected.ContainsKey($path)) {
            throw 'Desktop payload manifest has an invalid or duplicate file.'
        }
        $expected.Add($path, $file)
    }
    if (-not $expected.ContainsKey($icon)) { throw 'Desktop payload icon is missing from the manifest.' }
    $actual = Get-DesktopPayloadManifest -SourceRoot $SourceRoot -Version $Version -IconFileName $icon
    if ($actual.Files.Count -ne $expected.Count) { throw 'Desktop payload file count mismatch.' }
    foreach ($file in $actual.Files) {
        if (-not $expected.ContainsKey($file.Path)) { throw "Unexpected desktop payload file: $($file.Path)" }
        $entry = $expected[$file.Path]
        if ([long]$entry.Length -ne $file.Length -or $entry.Sha256 -ne $file.Sha256) {
            throw "Desktop payload hash mismatch: $($file.Path)"
        }
    }
}
