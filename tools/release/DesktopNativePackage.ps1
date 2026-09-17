function Get-DesktopMsiIdentity {
    param([Parameter(Mandatory = $true)][string]$Path)
    $installer = $null; $database = $null; $view = $null; $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase([IO.Path]::GetFullPath($Path), 0)
        $view = $database.OpenView('SELECT `Property`, `Value` FROM `Property`')
        $null = $view.Execute()
        $values = @{}
        while ($null -ne ($record = $view.Fetch())) {
            $name = [string]$record.StringData(1)
            if ($name -in @('ProductCode', 'UpgradeCode', 'ProductVersion')) { $values[$name] = [string]$record.StringData(2) }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            $record = $null
        }
        foreach ($name in @('ProductCode', 'UpgradeCode', 'ProductVersion')) {
            if (-not $values.ContainsKey($name)) { throw "MSI identity property missing: $name" }
        }
        [void][guid]::Parse($values.ProductCode)
        [void][guid]::Parse($values.UpgradeCode)
        return [pscustomobject]$values
    }
    finally {
        foreach ($item in @($record, $view, $database, $installer)) {
            if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) }
        }
    }
}

function Add-DesktopNativePackage {
    param([string]$MsiPath, [string]$PackageRoot, [string]$Version, [string]$PayloadManifestPath)
    $identity = Get-DesktopMsiIdentity -Path $MsiPath
    if ($identity.ProductVersion -cne $Version -or [guid]$identity.UpgradeCode -ne [guid]'0E5C8E78-44C0-4585-A2E9-5E74071A3A11') {
        throw 'Embedded MSI product identity mismatch.'
    }
    $nativeRoot = Join-Path $PackageRoot 'Native'
    if (Test-Path -LiteralPath $nativeRoot) { throw 'Embedded native package directory already exists.' }
    [void](New-Item -ItemType Directory -Path $nativeRoot)
    $target = Join-Path $nativeRoot 'installer.msi'
    $hash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $MsiPath -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) { throw 'Embedded MSI copy hash mismatch.' }
    $manifest = [pscustomobject]@{
        SchemaVersion = 1; Version = $Version; ProductCode = $identity.ProductCode; UpgradeCode = $identity.UpgradeCode
        Path = 'Native/installer.msi'; Length = (Get-Item -LiteralPath $target).Length; Sha256 = $hash
        PayloadManifestSha256 = (Get-FileHash -LiteralPath $PayloadManifestPath -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $nativeRoot 'installer.json') -Encoding UTF8
    return $manifest
}

function Assert-DesktopNativePackageArchive {
    param([string]$ArchivePath, [object]$Manifest)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($item in @(
            @{ Path = 'Native/installer.msi'; Hash = $Manifest.Sha256; Length = $Manifest.Length },
            @{ Path = 'desktop-payload.json'; Hash = $Manifest.PayloadManifestSha256; Length = -1 })) {
            $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq $item.Path })
            if ($entries.Count -ne 1) { throw 'Embedded native archive entry missing or duplicated.' }
            $entry = $entries[0]
            $stream = $entry.Open(); $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
            finally { $hasher.Dispose(); $stream.Dispose() }
            if ($hash -ne $item.Hash -or ($item.Length -ge 0 -and $entry.Length -ne $item.Length)) { throw 'Embedded native archive hash mismatch.' }
        }
        $entries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ieq 'Native/installer.json' })
        if ($entries.Count -ne 1) { throw 'Embedded native manifest missing or duplicated.' }
        $reader = New-Object IO.StreamReader ($entries[0].Open())
        try { $actual = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if (($actual | ConvertTo-Json -Compress) -cne ($Manifest | ConvertTo-Json -Compress)) { throw 'Embedded native manifest mismatch.' }
    }
    finally { $archive.Dispose() }
}

function Publish-DesktopNativeFiles {
    param([string]$PreparedRoot, [string]$OutputRoot, [string]$PackageName, [string]$Version, [object]$NativeManifest)
    # The caller holds the package builder lock. Stage and validate every file
    # before replacing individual public files; never stream over a valid MSI.
    Assert-DirectoryTreeHasNoReparsePoints -RootPath $PreparedRoot -Description 'prepared native outputs'
    $adminName = '관리자용'
    $archiveName = '버전보관'
    $relativeNames = @(
        ($PackageName + '.exe'),
        ($adminName + '\' + $PackageName + '.msi'),
        ($adminName + '\' + $archiveName + '\' + $PackageName + '-v' + $Version + '.exe'),
        ($adminName + '\' + $archiveName + '\' + $PackageName + '-v' + $Version + '.msi')
    )
    $staged = New-Object System.Collections.Generic.List[object]
    foreach ($relative in $relativeNames) {
        $artifact = Join-Path $PreparedRoot $relative
        $artifactHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
        if ($relative.EndsWith('.msi') -and $artifactHash -ne $NativeManifest.Sha256) { throw 'Public MSI differs from the embedded MSI.' }
        $sidecar = (Get-Content -LiteralPath ($artifact + '.sha256.txt') -Raw).Trim()
        if ($sidecar -notmatch '^([A-Fa-f0-9]{64}) \*' -or $Matches[1] -ne $artifactHash) { throw 'Prepared native SHA sidecar mismatch.' }
        foreach ($suffix in @('', '.sha256.txt')) {
            $source = Join-Path $PreparedRoot ($relative + $suffix)
            $target = Join-Path $OutputRoot ($relative + $suffix)
            Assert-PathIsNotReparsePoint -Path $target -Description 'native output'
            $parent = Split-Path -Parent $target
            [void](New-Item -ItemType Directory -Path $parent -Force)
            $temporary = Get-ContainedDirectChildPath -ParentPath $parent -ChildName ('.native-' + [guid]::NewGuid().ToString('N') + '.staged') -Description 'staged native output'
            Copy-Item -LiteralPath $source -Destination $temporary -ErrorAction Stop
            $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            if ($suffix -eq '' -and $hash -ne $artifactHash) { throw 'Prepared native artifact changed during staging.' }
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $hash) { throw 'Native output staging hash mismatch.' }
            $staged.Add([pscustomobject]@{ Path = $temporary; Target = $target; Hash = $hash })
        }
    }
    foreach ($relative in @('README.txt', ($adminName + '\README.txt'))) {
        $source = Join-Path $PreparedRoot $relative
        $target = Join-Path $OutputRoot $relative
        Assert-PathIsNotReparsePoint -Path $target -Description 'native package instructions'
        $temporary = Get-ContainedDirectChildPath -ParentPath (Split-Path -Parent $target) -ChildName ('.native-' + [guid]::NewGuid().ToString('N') + '.staged') -Description 'staged native instructions'
        Copy-Item -LiteralPath $source -Destination $temporary -ErrorAction Stop
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $hash) { throw 'Native instructions staging hash mismatch.' }
        $staged.Add([pscustomobject]@{ Path = $temporary; Target = $target; Hash = $hash })
    }
    foreach ($file in $staged) {
        Assert-PathIsNotReparsePoint -Path $file.Target -Description 'native output replacement'
        if (Test-Path -LiteralPath $file.Target) { [IO.File]::Replace($file.Path, $file.Target, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($file.Path, $file.Target) }
        if ((Get-FileHash -LiteralPath $file.Target -Algorithm SHA256).Hash -ne $file.Hash) { throw 'Published native file hash mismatch.' }
    }
    $archiveRoot = Join-Path (Join-Path $OutputRoot $adminName) $archiveName
    Assert-DirectoryTreeHasNoReparsePoints -RootPath $archiveRoot -Description 'native version archive'
    $null = Remove-OldVersionedInstallerArchives -ArchiveRoot $archiveRoot -PackageName $PackageName -KeepVersionCount 1
}

function Remove-OldVersionedInstallerArchives {
    param(
        [Parameter(Mandatory = $true)][string]$ArchiveRoot,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][int]$KeepVersionCount
    )

    if ($KeepVersionCount -lt 1 -or -not (Test-Path -LiteralPath $ArchiveRoot)) {
        return @()
    }

    $escapedPackageName = [regex]::Escape($PackageName)
    $versionedFiles = Get-ChildItem -LiteralPath $ArchiveRoot -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^$escapedPackageName-v(?<version>\d+\.\d+\.\d+)\.(exe|msi)(\.sha256\.txt)?$" }

    $versionsToKeep = $versionedFiles |
        ForEach-Object {
            if ($_.Name -match "^$escapedPackageName-v(?<version>\d+\.\d+\.\d+)\.") {
                [pscustomobject]@{
                    Version = [version]$Matches.version
                    Text = $Matches.version
                }
            }
        } |
        Sort-Object Version -Descending -Unique |
        Select-Object -First $KeepVersionCount

    $keepVersionTextSet = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($versionToKeep in $versionsToKeep) {
        [void]$keepVersionTextSet.Add($versionToKeep.Text)
    }

    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($file in $versionedFiles) {
        if ($file.Name -notmatch "^$escapedPackageName-v(?<version>\d+\.\d+\.\d+)\.") {
            continue
        }

        if ($keepVersionTextSet.Contains($Matches.version)) {
            continue
        }

        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        $removed.Add($file.Name) | Out-Null
    }

    return $removed
}
