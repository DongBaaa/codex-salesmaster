[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [string]$Channel = 'stable',
    [ValidateRange(1, 300)]
    [int]$LockTimeoutSeconds = 30,
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Channel -cnotin @('stable', 'test', 'beta')) {
    throw 'Channel은 lowercase stable, test, beta만 허용됩니다.'
}

function Get-RollbackFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::SequentialScan)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Write-JsonFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$InputObject
    )

    $directory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $tempPath = Join-Path $directory ((Split-Path -Leaf $TargetPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ((Split-Path -Leaf $TargetPath) + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $stream = $null
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
            ($InputObject | ConvertTo-Json -Depth 10))
        $stream = [IO.FileStream]::new(
            $tempPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
            $stream = $null
        }
        if (Test-Path -LiteralPath $TargetPath) {
            [System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
        else {
            [IO.File]::Move($tempPath, $TargetPath)
        }
        $targetStream = [IO.File]::Open(
            $TargetPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        try {
            $targetStream.Flush($true)
        }
        finally {
            $targetStream.Dispose()
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        foreach ($path in @($tempPath, $backupPath)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Assert-RollbackPathWithinRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $rootWithSeparator =
        $resolvedRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (
        -not [string]::Equals(
            $resolvedPath,
            $resolvedRoot,
            [StringComparison]::OrdinalIgnoreCase) -and
        -not $resolvedPath.StartsWith(
            $rootWithSeparator,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$Label 경로가 허용 root를 벗어났습니다."
    }
    return $resolvedPath
}

function Assert-RollbackPathHasNoReparsePoint {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $volumeRoot = [IO.Path]::GetPathRoot($resolvedPath)
    $relativePath = $resolvedPath.Substring($volumeRoot.Length)
    $currentPath = $volumeRoot
    foreach ($segment in $relativePath.Split(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar),
        [StringSplitOptions]::RemoveEmptyEntries
    )) {
        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            continue
        }
        $item =
            Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            throw "$Label 경로에 reparse point가 포함되어 있습니다."
        }
    }
}

function Test-ManifestArtifact {
    param(
        [Parameter(Mandatory = $true)]$Artifact,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$DownloadsRoot,
        [Parameter(Mandatory = $true)][string]$ArtifactLabel
    )

    $fileName = [string]$Artifact.fileName
    if (
        [string]::IsNullOrWhiteSpace($fileName) -or
        -not [string]::Equals(
            $fileName,
            $fileName.Trim(),
            [StringComparison]::Ordinal) -or
        [IO.Path]::IsPathRooted($fileName) -or
        -not [string]::Equals(
            [IO.Path]::GetFileName($fileName),
            $fileName,
            [StringComparison]::Ordinal) -or
        $fileName -in @('.', '..') -or
        $fileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
    ) {
        throw "$Platform $ArtifactLabel fileName이 안전하지 않습니다."
    }
    $platformRoot = Assert-RollbackPathWithinRoot `
        -Path (Join-Path $DownloadsRoot $Platform) `
        -Root $DownloadsRoot `
        -Label "$Platform download"
    $artifactPath = Assert-RollbackPathWithinRoot `
        -Path (Join-Path $platformRoot $fileName) `
        -Root $platformRoot `
        -Label "$Platform $ArtifactLabel"
    Assert-RollbackPathHasNoReparsePoint `
        -Path $artifactPath `
        -Label "$Platform $ArtifactLabel"
    if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
        throw "$Platform $ArtifactLabel 파일을 찾을 수 없습니다: $artifactPath"
    }
    $file = Get-Item -LiteralPath $artifactPath -Force
    [long]$expectedSize = -1
    $expectedHash = [string]$Artifact.sha256
    if (
        $file.PSIsContainer -or
        ($file.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        -not [long]::TryParse(
            [string]$Artifact.fileSize,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$expectedSize) -or
        $expectedSize -lt 0 -or
        $file.Length -ne $expectedSize -or
        $expectedHash -notmatch '^[0-9A-Fa-f]{64}$'
    ) {
        throw "$Platform $ArtifactLabel size/SHA-256 metadata가 유효하지 않습니다."
    }
    $actualHash = Get-RollbackFileSha256 -Path $artifactPath
    if (-not [string]::Equals(
        $expectedHash,
        $actualHash,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "$Platform $ArtifactLabel SHA-256이 manifest와 다릅니다."
    }
}

function Test-ManifestPackage {
    param(
        [Parameter(Mandatory = $true)]$Package,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$DownloadsRoot
    )

    Test-ManifestArtifact `
        -Artifact $Package `
        -Platform $Platform `
        -DownloadsRoot $DownloadsRoot `
        -ArtifactLabel '이전 package'
    $installers = if (
        $null -eq $Package.PSObject.Properties['installers']
    ) {
        @()
    }
    else {
        @($Package.installers)
    }
    foreach ($installer in $installers) {
        if ($null -eq $installer) {
            throw "$Platform 이전 installer 항목이 비어 있습니다."
        }
        Test-ManifestArtifact `
            -Artifact $installer `
            -Platform $Platform `
            -DownloadsRoot $DownloadsRoot `
            -ArtifactLabel '이전 installer'
    }
}

function Get-ManifestPlatformVersion {
    param(
        $Manifest,
        [Parameter(Mandatory = $true)][string]$Platform
    )

    if ($null -eq $Manifest) {
        return ''
    }

    $platformNode = $Manifest.$Platform
    if ($null -eq $platformNode) {
        return ''
    }

    return ([string]$platformNode.version).Trim()
}

function Get-ManifestGenerationId {
    param(
        $Manifest,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (
        $null -eq $Manifest -or
        $null -eq $Manifest.PSObject.Properties['generationId'] -or
        [string]$Manifest.generationId -notmatch '^[0-9a-f]{32}$'
    ) {
        throw "$Label manifest generationId가 유효하지 않습니다."
    }
    return [string]$Manifest.generationId
}

function Assert-ManifestGenerationBinding {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$GenerationId,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (
        -not [string]::Equals(
            [string]$Manifest.generationId,
            $GenerationId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$Manifest.channel,
            $Channel,
            [StringComparison]::Ordinal)
    ) {
        throw "$Label manifest 세대/채널 binding이 유효하지 않습니다."
    }
}

function Get-VerifiedManifestFileEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedFileSize,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-RollbackPathHasNoReparsePoint `
        -Path $Path `
        -Label $Label
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 파일을 찾을 수 없습니다: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $hash = Get-RollbackFileSha256 -Path $Path
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne $ExpectedFileSize -or
        -not [string]::Equals(
            $hash,
            $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$Label hash/size 증거가 유효하지 않습니다."
    }
    return [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Path)
        Sha256 = $hash
        FileSize = [long]$item.Length
    }
}

function Copy-ManifestEvidenceAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedFileSize
    )

    $null = Get-VerifiedManifestFileEvidence `
        -Path $SourcePath `
        -ExpectedSha256 $ExpectedSha256 `
        -ExpectedFileSize $ExpectedFileSize `
        -Label '롤백 delivery 세대 원본'
    if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        $null = Get-VerifiedManifestFileEvidence `
            -Path $TargetPath `
            -ExpectedSha256 $ExpectedSha256 `
            -ExpectedFileSize $ExpectedFileSize `
            -Label '롤백 delivery 세대 대상'
        return
    }

    $directory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporaryPath = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($TargetPath) + '.' +
        [Guid]::NewGuid().ToString('N') + '.pending')
    $sourceStream = $null
    $targetStream = $null
    try {
        $sourceStream = [IO.File]::Open(
            $SourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $targetStream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            81920,
            [IO.FileOptions]::WriteThrough)
        try {
            $sourceStream.CopyTo($targetStream)
            $targetStream.Flush($true)
        }
        finally {
            if ($null -ne $targetStream) {
                $targetStream.Dispose()
                $targetStream = $null
            }
            if ($null -ne $sourceStream) {
                $sourceStream.Dispose()
                $sourceStream = $null
            }
        }
        [IO.File]::Move($temporaryPath, $TargetPath)
        $null = Get-VerifiedManifestFileEvidence `
            -Path $TargetPath `
            -ExpectedSha256 $ExpectedSha256 `
            -ExpectedFileSize $ExpectedFileSize `
            -Label '롤백 delivery 세대 대상'
    }
    finally {
        if ($null -ne $targetStream) {
            $targetStream.Dispose()
        }
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
        Remove-Item `
            -LiteralPath $temporaryPath `
            -Force `
            -ErrorAction SilentlyContinue
    }
}

function Get-RollbackRegularManifestPointerItem {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Label = '현재 update manifest pointer',
        [switch]$AllowMissing
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $parentPath = Split-Path -Parent $resolvedPath
    $leafName = [IO.Path]::GetFileName($resolvedPath)
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        if ($AllowMissing) {
            return $null
        }
        throw "$Label 파일이 없습니다: $resolvedPath"
    }

    $matchingItems = @(
        Get-ChildItem `
            -LiteralPath $parentPath `
            -Force `
            -ErrorAction Stop |
            Where-Object {
                [string]::Equals(
                    $_.Name,
                    $leafName,
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($matchingItems.Count -eq 0) {
        if ($AllowMissing) {
            return $null
        }
        throw "$Label 파일이 없습니다: $resolvedPath"
    }
    if ($matchingItems.Count -ne 1) {
        throw "$Label 경로가 모호합니다: $resolvedPath"
    }

    $item = $matchingItems[0]
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$Label is not a regular file: $resolvedPath"
    }
    return $item
}

function Read-VerifiedManifestPointer {
    param(
        [Parameter(Mandatory = $true)][string]$PointerPath,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $null =
        Get-RollbackRegularManifestPointerItem `
            -Path $PointerPath `
            -Label '현재 update manifest pointer'
    $pointer =
        Get-Content -LiteralPath $PointerPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    $expectedProperties = @(
        'owner',
        'schemaVersion',
        'channel',
        'generationId',
        'manifestRelativePath',
        'manifestSha256',
        'manifestFileSize',
        'deliveryManifestPath',
        'deliveryManifestSha256',
        'deliveryManifestFileSize')
    $actualProperties = @(
        $pointer.PSObject.Properties | ForEach-Object { $_.Name })
    [long]$manifestFileSize = -1
    [long]$deliveryFileSize = -1
    if (
        $actualProperties.Count -ne $expectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $expectedProperties
        }).Count -ne 0 -or
        -not [string]::Equals(
            [string]$pointer.owner,
            'georaeplan-release-manifest-pointer',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$pointer.schemaVersion,
            '1',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$pointer.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        [string]$pointer.generationId -notmatch '^[0-9a-f]{32}$' -or
        [string]$pointer.manifestSha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        [string]$pointer.deliveryManifestSha256 -notmatch
            '^[0-9A-Fa-f]{64}$' -or
        -not [long]::TryParse(
            [string]$pointer.manifestFileSize,
            [ref]$manifestFileSize) -or
        -not [long]::TryParse(
            [string]$pointer.deliveryManifestFileSize,
            [ref]$deliveryFileSize) -or
        $manifestFileSize -lt 0 -or
        $manifestFileSize -ne $deliveryFileSize -or
        -not [string]::Equals(
            [string]$pointer.manifestSha256,
            [string]$pointer.deliveryManifestSha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw '현재 update manifest pointer 값이 유효하지 않습니다.'
    }

    $generationId = [string]$pointer.generationId
    $relativePath =
        'generations/{0}/{1}.json' -f $Channel, $generationId
    $runtimePath = Join-Path (
        Join-Path (Join-Path $ManifestRoot 'generations') $Channel
    ) ($generationId + '.json')
    $expectedDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $ProjectRoot '배포\.georaeplan-release-generations'
        ) $Channel
    ) ($generationId + '.json')
    if (
        -not [string]::Equals(
            [string]$pointer.manifestRelativePath,
            $relativePath,
            [StringComparison]::Ordinal) -or
        [string]::IsNullOrWhiteSpace(
            [string]$pointer.deliveryManifestPath) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath(
                [string]$pointer.deliveryManifestPath),
            [IO.Path]::GetFullPath($expectedDeliveryPath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw '현재 update manifest pointer 경로 binding이 유효하지 않습니다.'
    }

    $runtimeEvidence = Get-VerifiedManifestFileEvidence `
        -Path $runtimePath `
        -ExpectedSha256 ([string]$pointer.manifestSha256) `
        -ExpectedFileSize $manifestFileSize `
        -Label '현재 runtime manifest 세대'
    $deliveryEvidence = Get-VerifiedManifestFileEvidence `
        -Path $expectedDeliveryPath `
        -ExpectedSha256 ([string]$pointer.deliveryManifestSha256) `
        -ExpectedFileSize $deliveryFileSize `
        -Label '현재 delivery manifest 세대'
    $manifest =
        Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    Assert-ManifestGenerationBinding `
        -Manifest $manifest `
        -GenerationId $generationId `
        -Channel $Channel `
        -Label '현재 pointer-selected'
    return [pscustomobject]@{
        Pointer = $pointer
        GenerationId = $generationId
        Manifest = $manifest
        RuntimeEvidence = $runtimeEvidence
        DeliveryEvidence = $deliveryEvidence
    }
}

function Get-VerifiedRollbackGeneration {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $generationId =
        Get-ManifestGenerationId -Manifest $Manifest -Label '이전 정상'
    Assert-ManifestGenerationBinding `
        -Manifest $Manifest `
        -GenerationId $generationId `
        -Channel $Channel `
        -Label '이전 정상'
    $runtimePath = Join-Path (
        Join-Path (Join-Path $ManifestRoot 'generations') $Channel
    ) ($generationId + '.json')
    Assert-RollbackPathHasNoReparsePoint `
        -Path $runtimePath `
        -Label '이전 runtime manifest 세대'
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
        throw "이전 runtime manifest 세대를 찾을 수 없습니다: $runtimePath"
    }
    $runtimeItem = Get-Item -LiteralPath $runtimePath -Force
    if (
        $runtimeItem.PSIsContainer -or
        ($runtimeItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw '이전 runtime manifest 세대가 regular file이 아닙니다.'
    }
    $runtimeHash = Get-RollbackFileSha256 -Path $runtimePath
    $runtimeManifest =
        Get-Content -LiteralPath $runtimePath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    Assert-ManifestGenerationBinding `
        -Manifest $runtimeManifest `
        -GenerationId $generationId `
        -Channel $Channel `
        -Label '이전 runtime'
    $expectedDeliveryPath = Join-Path (
        Join-Path (
            Join-Path $ProjectRoot '배포\.georaeplan-release-generations'
        ) $Channel
    ) ($generationId + '.json')
    $stagedDeliveryPath = Join-Path (
        Join-Path (Join-Path $ManifestRoot 'delivery-generations') $Channel
    ) ($generationId + '.json')
    $deliverySourcePath = if (
        Test-Path -LiteralPath $expectedDeliveryPath -PathType Leaf
    ) {
        $expectedDeliveryPath
    }
    else {
        $stagedDeliveryPath
    }
    $deliveryEvidence = Get-VerifiedManifestFileEvidence `
        -Path $deliverySourcePath `
        -ExpectedSha256 $runtimeHash `
        -ExpectedFileSize ([long]$runtimeItem.Length) `
        -Label '이전 delivery manifest 세대'
    return [pscustomobject]@{
        GenerationId = $generationId
        Manifest = $runtimeManifest
        RuntimePath = [IO.Path]::GetFullPath($runtimePath)
        DeliverySourcePath = $deliveryEvidence.Path
        ExpectedDeliveryPath = [IO.Path]::GetFullPath($expectedDeliveryPath)
        Sha256 = $runtimeHash
        FileSize = [long]$runtimeItem.Length
    }
}

function Assert-RollbackExactPropertySet {
    param(
        [Parameter(Mandatory = $true)]$InputObject,
        [Parameter(Mandatory = $true)][string[]]$ExpectedProperties,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $actualProperties = @(
        $InputObject.PSObject.Properties |
            ForEach-Object { $_.Name })
    if (
        $actualProperties.Count -ne $ExpectedProperties.Count -or
        @($actualProperties | Where-Object {
            $_ -notin $ExpectedProperties
        }).Count -ne 0
    ) {
        throw "$Label property 집합이 유효하지 않습니다."
    }
}

function Remove-RollbackOwnedDirectoryTree {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedPath
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedExpectedPath = [IO.Path]::GetFullPath($ExpectedPath)
    if (-not [string]::Equals(
        $resolvedPath,
        $resolvedExpectedPath,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw '롤백 소유 디렉터리 삭제 경로가 예상 경로와 다릅니다.'
    }
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        return
    }
    Assert-RollbackPathHasNoReparsePoint `
        -Path $resolvedPath `
        -Label '롤백 소유 디렉터리'
    $rootItem = Get-Item -LiteralPath $resolvedPath -Force
    if (
        -not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw '롤백 소유 디렉터리가 regular directory가 아닙니다.'
    }

    function Remove-RollbackOwnedDirectoryNode {
        param([Parameter(Mandatory = $true)][string]$DirectoryPath)

        foreach ($child in @(
            Get-ChildItem `
                -LiteralPath $DirectoryPath `
                -Force `
                -ErrorAction Stop
        )) {
            if (($child.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
            ) {
                throw "롤백 소유 디렉터리에 reparse point가 있습니다: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                Remove-RollbackOwnedDirectoryNode `
                    -DirectoryPath $child.FullName
                [IO.Directory]::Delete($child.FullName, $false)
            }
            else {
                [IO.File]::Delete($child.FullName)
            }
        }
    }

    Remove-RollbackOwnedDirectoryNode -DirectoryPath $resolvedPath
    [IO.Directory]::Delete($resolvedPath, $false)
}

function Read-RollbackPreparationOwner {
    param(
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if (-not (Test-Path -LiteralPath $OwnerPath -PathType Leaf)) {
        return $null
    }
    Assert-RollbackPathHasNoReparsePoint `
        -Path $OwnerPath `
        -Label '롤백 preparation owner'
    $owner =
        Get-Content -LiteralPath $OwnerPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    Assert-RollbackExactPropertySet `
        -InputObject $owner `
        -ExpectedProperties @(
            'owner',
            'schemaVersion',
            'channel',
            'projectRoot',
            'outputRoot',
            'transactionRoot',
            'phase',
            'completedTransactionFingerprint',
            'completedEntries') `
        -Label '롤백 preparation owner'
    if (
        -not [string]::Equals(
            [string]$owner.owner,
            'georaeplan-update-rollback-owner',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$owner.schemaVersion,
            '2',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$owner.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$owner.projectRoot),
            [IO.Path]::GetFullPath($ProjectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$owner.outputRoot),
            [IO.Path]::GetFullPath($OutputRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$owner.transactionRoot),
            [IO.Path]::GetFullPath($TransactionRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$owner.phase -cnotin @('Preparing', 'Cleanup')
    ) {
        throw '롤백 preparation owner binding이 유효하지 않습니다.'
    }
    $completedEntries = @($owner.completedEntries)
    if ([string]$owner.phase -ceq 'Preparing') {
        if (
            -not [string]::IsNullOrEmpty(
                [string]$owner.completedTransactionFingerprint) -or
            $completedEntries.Count -ne 0
        ) {
            throw '롤백 preparation owner의 Completed 증거가 비어 있지 않습니다.'
        }
        return $owner
    }
    if (
        [string]$owner.completedTransactionFingerprint -notmatch
            '^[0-9A-F]{64}$' -or
        $completedEntries.Count -notin @(3, 5)
    ) {
        throw '롤백 cleanup owner의 Completed 증거가 유효하지 않습니다.'
    }
    $expectedNames = if ($completedEntries.Count -eq 5) {
        @(
            'deliveryGeneration',
            'currentManifest',
            'previousManifest',
            'deliveryManifest',
            'pointer')
    }
    else {
        @(
            'currentManifest',
            'previousManifest',
            'deliveryManifest')
    }
    $fixedTargets = @{
        currentManifest =
            Join-Path (Join-Path $OutputRoot 'manifest') ($Channel + '.json')
        previousManifest =
            Join-Path (Join-Path $OutputRoot 'manifest') (
                $Channel + '.previous.json')
        deliveryManifest =
            Join-Path (Join-Path $ProjectRoot '배포') ($Channel + '.json')
        pointer =
            Join-Path (Join-Path $OutputRoot 'manifest') (
                $Channel + '.current.json')
    }
    $deliveryGenerationRoot =
        [IO.Path]::GetFullPath((
            Join-Path (
                Join-Path $ProjectRoot (
                    '배포\.georaeplan-release-generations')
            ) $Channel))
    $seenTargets =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    for (
        $entryIndex = 0;
        $entryIndex -lt $completedEntries.Count;
        $entryIndex++
    ) {
        $entry = $completedEntries[$entryIndex]
        Assert-RollbackExactPropertySet `
            -InputObject $entry `
            -ExpectedProperties @(
                'name',
                'targetPath',
                'sha256',
                'fileSize') `
            -Label "롤백 cleanup owner entry[$entryIndex]"
        [long]$entryFileSize = -1
        $entryName = [string]$entry.name
        $entryTarget = [IO.Path]::GetFullPath(
            [string]$entry.targetPath)
        $targetBindingValid = if (
            $entryName -ceq 'deliveryGeneration'
        ) {
            $generationLeaf = [IO.Path]::GetFileName($entryTarget)
            [string]::Equals(
                [IO.Path]::GetDirectoryName($entryTarget),
                $deliveryGenerationRoot,
                [StringComparison]::OrdinalIgnoreCase) -and
            $generationLeaf -match '^[0-9a-f]{32}\.json$'
        }
        else {
            $null -ne $fixedTargets[$entryName] -and
            [string]::Equals(
                $entryTarget,
                [IO.Path]::GetFullPath(
                    [string]$fixedTargets[$entryName]),
                [StringComparison]::OrdinalIgnoreCase)
        }
        if (
            $entryName -cne $expectedNames[$entryIndex] -or
            -not $targetBindingValid -or
            -not $seenTargets.Add($entryTarget) -or
            [string]$entry.sha256 -notmatch '^[0-9A-F]{64}$' -or
            -not [long]::TryParse(
                [string]$entry.fileSize,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$entryFileSize) -or
            $entryFileSize -lt 0
        ) {
            throw "롤백 cleanup owner entry[$entryIndex] binding이 유효하지 않습니다."
        }
        $targetState =
            Get-RollbackFileState `
                -Path $entryTarget `
                -Label "롤백 cleanup owner final target[$entryIndex]"
        if (-not (Test-RollbackFileStateMatches `
            -State $targetState `
            -ExpectedExists $true `
            -ExpectedSha256 ([string]$entry.sha256) `
            -ExpectedFileSize $entryFileSize
        )) {
            throw (
                "롤백 cleanup owner final target[$entryIndex]가 " +
                'Completed 결과와 다릅니다.')
        }
    }
    return $owner
}

function Write-RollbackPreparationOwner {
    param(
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Preparing', 'Cleanup')]
        [string]$Phase,
        $CompletedJournal
    )

    if ($Phase -eq 'Cleanup') {
        if (
            $null -eq $CompletedJournal -or
            [string]$CompletedJournal.phase -cne 'Completed' -or
            [string]$CompletedJournal.transactionFingerprint -notmatch
                '^[0-9A-F]{64}$'
        ) {
            throw '롤백 cleanup owner에는 검증된 Completed journal이 필요합니다.'
        }
    }
    elseif ($null -ne $CompletedJournal) {
        throw '롤백 preparation owner에는 Completed journal을 기록할 수 없습니다.'
    }
    [object[]]$completedEntries = @()
    if ($Phase -eq 'Cleanup') {
        $completedEntries = @(
            foreach ($entry in @($CompletedJournal.entries)) {
                [ordered]@{
                    name = [string]$entry.name
                    targetPath =
                        [IO.Path]::GetFullPath(
                            [string]$entry.targetPath)
                    sha256 =
                        ([string]$entry.sha256).ToUpperInvariant()
                    fileSize = [string]$entry.fileSize
                }
            })
    }
    Write-JsonFileAtomically `
        -TargetPath $OwnerPath `
        -InputObject ([ordered]@{
            owner = 'georaeplan-update-rollback-owner'
            schemaVersion = '2'
            channel = $Channel
            projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
            outputRoot = [IO.Path]::GetFullPath($OutputRoot)
            transactionRoot = [IO.Path]::GetFullPath($TransactionRoot)
            phase = $Phase
            completedTransactionFingerprint = if ($Phase -eq 'Cleanup') {
                [string]$CompletedJournal.transactionFingerprint
            }
            else {
                ''
            }
            completedEntries = $completedEntries
        })
    $null = Read-RollbackPreparationOwner `
        -OwnerPath $OwnerPath `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -TransactionRoot $TransactionRoot `
        -Channel $Channel
}

function Resume-RollbackPreparationOrCleanup {
    param(
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $owner = Read-RollbackPreparationOwner `
        -OwnerPath $OwnerPath `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -TransactionRoot $TransactionRoot `
        -Channel $Channel
    if ($null -ne $owner) {
        if ([string]$owner.phase -ceq 'Cleanup') {
            if (Test-Path -LiteralPath $TransactionRoot) {
                $completedJournal = Read-RollbackTransactionJournal `
                    -JournalPath $JournalPath `
                    -TransactionRoot $TransactionRoot `
                    -ManifestRoot $ManifestRoot `
                    -ProjectRoot $ProjectRoot `
                    -OutputRoot $OutputRoot `
                    -Channel $Channel
                if ([string]$completedJournal.phase -cne 'Completed') {
                    throw (
                        '롤백 cleanup owner에는 검증된 Completed journal이 ' +
                        '필요합니다.')
                }
                if (-not [string]::Equals(
                    [string]$owner.completedTransactionFingerprint,
                    [string]$completedJournal.transactionFingerprint,
                    [StringComparison]::Ordinal
                )) {
                    throw (
                        '롤백 cleanup owner와 Completed journal fingerprint가 ' +
                        '다릅니다.')
                }
            }
            Remove-RollbackOwnedDirectoryTree `
                -Path $TransactionRoot `
                -ExpectedPath $TransactionRoot
            Remove-Item -LiteralPath $OwnerPath -Force -ErrorAction Stop
            return 'CompletedCleanup'
        }
        if (Test-Path -LiteralPath $JournalPath -PathType Leaf) {
            return 'JournalReady'
        }
        Remove-RollbackOwnedDirectoryTree `
            -Path $TransactionRoot `
            -ExpectedPath $TransactionRoot
        Remove-Item -LiteralPath $OwnerPath -Force -ErrorAction Stop
        return 'PreparationDiscarded'
    }

    if (
        (Test-Path -LiteralPath $TransactionRoot -PathType Container) -and
        -not (Test-Path -LiteralPath $JournalPath -PathType Leaf)
    ) {
        Assert-RollbackPathHasNoReparsePoint `
            -Path $TransactionRoot `
            -Label '롤백 transaction'
        if (@(
            Get-ChildItem -LiteralPath $TransactionRoot -Force
        ).Count -ne 0) {
            throw 'owner 또는 journal 없는 롤백 transaction root를 거부했습니다.'
        }
        [IO.Directory]::Delete($TransactionRoot, $false)
    }
    return 'None'
}

function New-RollbackStageEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)]$InputObject,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Write-JsonFileAtomically `
        -TargetPath $StagePath `
        -InputObject $InputObject
    $item = Get-Item -LiteralPath $StagePath -Force
    return Get-VerifiedManifestFileEvidence `
        -Path $StagePath `
        -ExpectedSha256 (Get-RollbackFileSha256 -Path $StagePath) `
        -ExpectedFileSize ([long]$item.Length) `
        -Label $Label
}

function Get-RollbackFileState {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label
    )

    Assert-RollbackPathHasNoReparsePoint -Path $Path -Label $Label
    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{
            Exists = $false
            Sha256 = ''
            FileSize = -1L
        }
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label 대상이 regular file이 아닙니다."
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "$Label 대상이 regular file이 아닙니다."
    }
    return [pscustomobject]@{
        Exists = $true
        Sha256 = Get-RollbackFileSha256 -Path $Path
        FileSize = [long]$item.Length
    }
}

function New-RollbackTransactionEntry {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$StageEvidence
    )

    $resolvedTargetPath = [IO.Path]::GetFullPath($TargetPath)
    $preImage = Get-RollbackFileState `
        -Path $resolvedTargetPath `
        -Label "롤백 pre-image $Name"
    return [ordered]@{
        name = $Name
        targetPath = $resolvedTargetPath
        stagedPath = [IO.Path]::GetFullPath(
            [string]$StageEvidence.Path)
        sha256 = [string]$StageEvidence.Sha256
        fileSize = [string]$StageEvidence.FileSize
        preImageExists = [bool]$preImage.Exists
        preImageSha256 = [string]$preImage.Sha256
        preImageFileSize = [string]$preImage.FileSize
    }
}

function Get-RollbackTransactionFingerprint {
    param(
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Mode,
        [string]$GenerationId = '',
        [Parameter(Mandatory = $true)]$Entries
    )

    $canonicalEntries = @(
        foreach ($entry in @($Entries)) {
            [ordered]@{
                name = [string]$entry.name
                targetPath =
                    [IO.Path]::GetFullPath([string]$entry.targetPath)
                stagedPath =
                    [IO.Path]::GetFullPath([string]$entry.stagedPath)
                sha256 = ([string]$entry.sha256).ToUpperInvariant()
                fileSize = [string]$entry.fileSize
                preImageExists = [bool]$entry.preImageExists
                preImageSha256 =
                    ([string]$entry.preImageSha256).ToUpperInvariant()
                preImageFileSize = [string]$entry.preImageFileSize
            }
        })
    $payload = [ordered]@{
        schemaVersion = '1'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        outputRoot = [IO.Path]::GetFullPath($OutputRoot)
        mode = $Mode
        generationId = $GenerationId
        entries = $canonicalEntries
    }
    $json = $payload | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return (
            [BitConverter]::ToString(
                $sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Read-RollbackTransactionJournal {
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    if (-not (Test-Path -LiteralPath $JournalPath -PathType Leaf)) {
        throw '롤백 transaction journal을 찾을 수 없습니다.'
    }
    Assert-RollbackPathHasNoReparsePoint `
        -Path $JournalPath `
        -Label '롤백 transaction journal'
    $journal =
        Get-Content -LiteralPath $JournalPath -Raw -Encoding UTF8 |
            ConvertFrom-Json
    Assert-RollbackExactPropertySet `
        -InputObject $journal `
        -ExpectedProperties @(
            'owner',
            'schemaVersion',
            'channel',
            'projectRoot',
            'outputRoot',
            'transactionRoot',
            'mode',
            'generationId',
            'transactionFingerprint',
            'phase',
            'nextIndex',
            'entries') `
        -Label '롤백 transaction journal'
    [int]$nextIndex = -1
    if (
        -not [string]::Equals(
            [string]$journal.owner,
            'georaeplan-update-rollback-transaction',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$journal.schemaVersion,
            '2',
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string]$journal.channel,
            $Channel,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$journal.projectRoot),
            [IO.Path]::GetFullPath($ProjectRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$journal.outputRoot),
            [IO.Path]::GetFullPath($OutputRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$journal.transactionRoot),
            [IO.Path]::GetFullPath($TransactionRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$journal.mode -cnotin @('Legacy', 'Pointer') -or
        [string]$journal.transactionFingerprint -notmatch
            '^[0-9A-F]{64}$' -or
        [string]$journal.phase -cnotin @('Applying', 'Completed') -or
        -not [int]::TryParse(
            [string]$journal.nextIndex,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$nextIndex)
    ) {
        throw '롤백 transaction journal binding이 유효하지 않습니다.'
    }
    if (
        ([string]$journal.mode -ceq 'Pointer' -and
            [string]$journal.generationId -notmatch '^[0-9a-f]{32}$') -or
        ([string]$journal.mode -ceq 'Legacy' -and
            -not [string]::IsNullOrEmpty(
                [string]$journal.generationId))
    ) {
        throw '롤백 transaction generation binding이 유효하지 않습니다.'
    }

    $expectedNames = if ([string]$journal.mode -ceq 'Pointer') {
        @(
            'deliveryGeneration',
            'currentManifest',
            'previousManifest',
            'deliveryManifest',
            'pointer')
    }
    else {
        @(
            'currentManifest',
            'previousManifest',
            'deliveryManifest')
    }
    $entries = @($journal.entries)
    if (
        $entries.Count -ne $expectedNames.Count -or
        $nextIndex -lt 0 -or
        $nextIndex -gt $entries.Count -or
        (
            [string]$journal.phase -ceq 'Completed' -and
            $nextIndex -ne $entries.Count
        )
    ) {
        throw '롤백 transaction journal 진행 상태가 유효하지 않습니다.'
    }

    $stagingRoot = Join-Path $TransactionRoot 'staging'
    $expectedTargets = @{
        currentManifest = Join-Path $ManifestRoot ($Channel + '.json')
        previousManifest =
            Join-Path $ManifestRoot ($Channel + '.previous.json')
        deliveryManifest =
            Join-Path $ProjectRoot ("배포\" + $Channel + '.json')
        pointer =
            Join-Path $ManifestRoot ($Channel + '.current.json')
        deliveryGeneration = Join-Path (
            Join-Path (
                Join-Path $ProjectRoot (
                    '배포\.georaeplan-release-generations')
            ) $Channel
        ) ([string]$journal.generationId + '.json')
    }
    $expectedStages = @{
        currentManifest = Join-Path $stagingRoot 'current.json'
        previousManifest = Join-Path $stagingRoot 'previous.json'
        deliveryManifest = Join-Path $stagingRoot 'delivery.json'
        pointer = Join-Path $stagingRoot 'pointer.json'
        deliveryGeneration =
            Join-Path $stagingRoot 'delivery-generation.json'
    }
    $seenTargets =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    $seenNames =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::Ordinal)
    for ($index = 0; $index -lt $entries.Count; $index++) {
        $entry = $entries[$index]
        Assert-RollbackExactPropertySet `
            -InputObject $entry `
            -ExpectedProperties @(
                'name',
                'targetPath',
                'stagedPath',
                'sha256',
                'fileSize',
                'preImageExists',
                'preImageSha256',
                'preImageFileSize') `
            -Label "롤백 transaction entry[$index]"
        $entryName = [string]$entry.name
        [long]$entryFileSize = -1
        [long]$preImageFileSize = -2
        $resolvedEntryTarget =
            [IO.Path]::GetFullPath([string]$entry.targetPath)
        if (
            -not [string]::Equals(
                $entryName,
                $expectedNames[$index],
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $resolvedEntryTarget,
                [IO.Path]::GetFullPath(
                    [string]$expectedTargets[$entryName]),
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath([string]$entry.stagedPath),
                [IO.Path]::GetFullPath(
                    [string]$expectedStages[$entryName]),
                [StringComparison]::OrdinalIgnoreCase) -or
            [string]$entry.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
            -not [long]::TryParse(
                [string]$entry.fileSize,
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$entryFileSize) -or
            $entryFileSize -lt 0 -or
            $entry.preImageExists -isnot [bool] -or
            -not [long]::TryParse(
                [string]$entry.preImageFileSize,
                [Globalization.NumberStyles]::AllowLeadingSign,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$preImageFileSize) -or
            (
                [bool]$entry.preImageExists -and
                (
                    [string]$entry.preImageSha256 -notmatch
                        '^[0-9A-Fa-f]{64}$' -or
                    $preImageFileSize -lt 0
                )
            ) -or
            (
                -not [bool]$entry.preImageExists -and
                (
                    -not [string]::IsNullOrEmpty(
                        [string]$entry.preImageSha256) -or
                    $preImageFileSize -ne -1
                )
            ) -or
            -not $seenNames.Add($entryName) -or
            -not $seenTargets.Add($resolvedEntryTarget)
        ) {
            throw "롤백 transaction entry[$index] binding이 유효하지 않습니다."
        }
        $null = Get-VerifiedManifestFileEvidence `
            -Path ([string]$entry.stagedPath) `
            -ExpectedSha256 ([string]$entry.sha256) `
            -ExpectedFileSize $entryFileSize `
            -Label "롤백 transaction staged entry[$index]"
        $targetState = Get-RollbackFileState `
            -Path $resolvedEntryTarget `
            -Label "롤백 transaction target entry[$index]"
        $matchesStaged =
            [bool]$targetState.Exists -and
            $targetState.FileSize -eq $entryFileSize -and
            [string]::Equals(
                [string]$targetState.Sha256,
                [string]$entry.sha256,
                [StringComparison]::OrdinalIgnoreCase)
        $matchesPreImage =
            [bool]$targetState.Exists -eq
                [bool]$entry.preImageExists -and
            (
                -not [bool]$entry.preImageExists -or
                (
                    $targetState.FileSize -eq $preImageFileSize -and
                    [string]::Equals(
                        [string]$targetState.Sha256,
                        [string]$entry.preImageSha256,
                        [StringComparison]::OrdinalIgnoreCase)
                )
            )
        if (-not $matchesStaged -and -not $matchesPreImage) {
            throw (
                "롤백 transaction target entry[$index]가 최초 pre-image " +
                '또는 staged 결과와 일치하지 않습니다.')
        }
        if (
            (
                $index -lt $nextIndex -or
                [string]$journal.phase -ceq 'Completed'
            ) -and
            -not $matchesStaged
        ) {
            throw (
                "롤백 transaction committed entry[$index]가 final " +
                'staged 결과와 일치하지 않습니다.')
        }
    }
    $calculatedFingerprint =
        Get-RollbackTransactionFingerprint `
            -Channel $Channel `
            -ProjectRoot $ProjectRoot `
            -OutputRoot $OutputRoot `
            -Mode ([string]$journal.mode) `
            -GenerationId ([string]$journal.generationId) `
            -Entries $entries
    if (-not [string]::Equals(
        [string]$journal.transactionFingerprint,
        $calculatedFingerprint,
        [StringComparison]::Ordinal
    )) {
        throw '롤백 transaction fingerprint가 journal 내용과 다릅니다.'
    }
    return $journal
}

function Test-RollbackFileStateMatches {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][bool]$ExpectedExists,
        [string]$ExpectedSha256 = '',
        [long]$ExpectedFileSize = -1
    )

    if ([bool]$State.Exists -ne $ExpectedExists) {
        return $false
    }
    if (-not $ExpectedExists) {
        return $true
    }
    return (
        [long]$State.FileSize -eq $ExpectedFileSize -and
        [string]::Equals(
            [string]$State.Sha256,
        $ExpectedSha256,
            [StringComparison]::OrdinalIgnoreCase))
}

function Invoke-RollbackTestPausePoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    $configuredPoints =
        @(
            ([string]$env:GEORAEPLAN_ROLLBACK_TEST_PAUSE_POINTS).Split(
                ';',
                [StringSplitOptions]::RemoveEmptyEntries))
    if ($Name -cnotin $configuredPoints) {
        return
    }
    $readyPath =
        [string]$env:GEORAEPLAN_ROLLBACK_TEST_PAUSE_READY_PATH
    $releasePath =
        [string]$env:GEORAEPLAN_ROLLBACK_TEST_PAUSE_RELEASE_PATH
    if (
        [string]::IsNullOrWhiteSpace($readyPath) -or
        [string]::IsNullOrWhiteSpace($releasePath)
    ) {
        throw 'Rollback test pause point paths are not configured.'
    }
    [IO.File]::WriteAllText(
        [IO.Path]::GetFullPath($readyPath),
        $Name,
        [Text.UTF8Encoding]::new($false, $true))
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $releasePath -PathType Leaf) {
            $releaseValue =
                [IO.File]::ReadAllText(
                    [IO.Path]::GetFullPath($releasePath))
            if ([string]::Equals(
                $releaseValue,
                $Name,
                [StringComparison]::Ordinal
            )) {
                [IO.File]::Delete([IO.Path]::GetFullPath($releasePath))
                [IO.File]::Delete([IO.Path]::GetFullPath($readyPath))
                return
            }
        }
        Start-Sleep -Milliseconds 25
    }
    throw "Rollback test pause point timed out: $Name"
}

function Stop-RollbackCommitConflict {
    param([Parameter(Mandatory = $true)][string]$Message)

    $script:rollbackCommitConflictMessage = $Message
    [Console]::Error.WriteLine(
        'rollback_commit_conflict: ' + $Message)
    return $false
}

function Get-RollbackLeaseSha256 {
    param(
        [Parameter(Mandatory = $true)][IO.FileStream]$Lease
    )

    if (-not $Lease.CanRead -or -not $Lease.CanSeek) {
        throw '롤백 대상 lease가 읽기/seek 가능하지 않습니다.'
    }
    $position = $Lease.Position
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $Lease.Position = 0
        return (
            [BitConverter]::ToString(
                $sha256.ComputeHash($Lease))).Replace('-', '')
    }
    finally {
        $Lease.Position = $position
        $sha256.Dispose()
    }
}

function Copy-RollbackStagedFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedFileSize,
        [Parameter(Mandatory = $true)][bool]$ExpectedPreImageExists,
        [string]$ExpectedPreImageSha256 = '',
        [long]$ExpectedPreImageFileSize = -1
    )

    $null = Get-VerifiedManifestFileEvidence `
        -Path $SourcePath `
        -ExpectedSha256 $ExpectedSha256 `
        -ExpectedFileSize $ExpectedFileSize `
        -Label '롤백 staged 원본'
    $initialTargetState =
        Get-RollbackFileState `
            -Path $TargetPath `
            -Label '롤백 transaction 대상'
    if (Test-RollbackFileStateMatches `
        -State $initialTargetState `
        -ExpectedExists $true `
        -ExpectedSha256 $ExpectedSha256 `
        -ExpectedFileSize $ExpectedFileSize
    ) {
        return $true
    }
    if (-not (Test-RollbackFileStateMatches `
        -State $initialTargetState `
        -ExpectedExists $ExpectedPreImageExists `
        -ExpectedSha256 $ExpectedPreImageSha256 `
        -ExpectedFileSize $ExpectedPreImageFileSize
    )) {
        throw (
            '롤백 transaction 대상이 기록된 pre-image와 달라 ' +
            '덮어쓰기를 거부했습니다.')
    }
    $directory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Assert-RollbackPathHasNoReparsePoint `
        -Path $directory `
        -Label '롤백 transaction 대상 디렉터리'
    $temporaryPath = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($TargetPath) + '.' +
        [Guid]::NewGuid().ToString('N') + '.pending')
    $backupPath = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($TargetPath) + '.' +
        [Guid]::NewGuid().ToString('N') + '.backup')
    $restoreDiscardPath = Join-Path $directory (
        '.' + [IO.Path]::GetFileName($TargetPath) + '.' +
        [Guid]::NewGuid().ToString('N') + '.restore-discard')
    $sourceStream = $null
    $targetStream = $null
    $preImageLease = $null
    $stagedTargetLease = $null
    $preserveBackup = $false
    $preserveRestoreDiscard = $false
    try {
        $sourceStream = [IO.File]::Open(
            $SourcePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $targetStream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            81920,
            [IO.FileOptions]::WriteThrough)
        try {
            $sourceStream.CopyTo($targetStream)
            $targetStream.Flush($true)
        }
        finally {
            if ($null -ne $targetStream) {
                $targetStream.Dispose()
                $targetStream = $null
            }
            if ($null -ne $sourceStream) {
                $sourceStream.Dispose()
                $sourceStream = $null
            }
        }
        $commitTargetState =
            Get-RollbackFileState `
                -Path $TargetPath `
                -Label '롤백 transaction commit 대상'
        if (Test-RollbackFileStateMatches `
            -State $commitTargetState `
            -ExpectedExists $true `
            -ExpectedSha256 $ExpectedSha256 `
            -ExpectedFileSize $ExpectedFileSize
        ) {
            return $true
        }
        if (-not (Test-RollbackFileStateMatches `
            -State $commitTargetState `
            -ExpectedExists $ExpectedPreImageExists `
            -ExpectedSha256 $ExpectedPreImageSha256 `
            -ExpectedFileSize $ExpectedPreImageFileSize
        )) {
            throw (
                '롤백 transaction 대상이 commit 직전에 변경되어 ' +
                '덮어쓰기를 거부했습니다.')
        }
        if ($ExpectedPreImageExists) {
            $preImageLease = [IO.File]::Open(
                $TargetPath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                (
                    [IO.FileShare]::Read -bor
                    [IO.FileShare]::Delete))
            $leasedPreImageSha256 =
                Get-RollbackLeaseSha256 -Lease $preImageLease
            if (
                $preImageLease.Length -ne $ExpectedPreImageFileSize -or
                -not [string]::Equals(
                    $leasedPreImageSha256,
                    $ExpectedPreImageSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw (
                    '롤백 transaction 대상 lease가 기록된 pre-image와 ' +
                    '다릅니다.')
            }
            Invoke-RollbackTestPausePoint `
                -Name 'BeforeRollbackTargetReplace'
            [IO.File]::Replace(
                $temporaryPath,
                $TargetPath,
                $backupPath,
                $true)
            $replacedBackupState =
                Get-RollbackFileState `
                    -Path $backupPath `
                    -Label '롤백 transaction 교체 backup'
            if (-not (Test-RollbackFileStateMatches `
                -State $replacedBackupState `
                -ExpectedExists $true `
                -ExpectedSha256 $ExpectedPreImageSha256 `
                -ExpectedFileSize $ExpectedPreImageFileSize
            )) {
                $preImageLease.Dispose()
                $preImageLease = $null
                Invoke-RollbackTestPausePoint `
                    -Name 'BeforeRollbackConflictRestoreCheck'
                $currentTargetState =
                    Get-RollbackFileState `
                        -Path $TargetPath `
                        -Label '롤백 conflict 복구 전 대상'
                if (-not (Test-RollbackFileStateMatches `
                    -State $currentTargetState `
                    -ExpectedExists $true `
                    -ExpectedSha256 $ExpectedSha256 `
                    -ExpectedFileSize $ExpectedFileSize
                )) {
                    $preserveBackup = $true
                    return (Stop-RollbackCommitConflict -Message (
                        '롤백 transaction conflict 복구 전에 대상이 다시 ' +
                        '변경되었습니다. 현재 대상과 displaced pre-image를 ' +
                        "모두 보존했습니다. backup=$backupPath"))
                }
                $stagedTargetLease = [IO.File]::Open(
                    $TargetPath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    (
                        [IO.FileShare]::Read -bor
                        [IO.FileShare]::Delete))
                $leasedStagedSha256 =
                    Get-RollbackLeaseSha256 -Lease $stagedTargetLease
                if (
                    $stagedTargetLease.Length -ne $ExpectedFileSize -or
                    -not [string]::Equals(
                        $leasedStagedSha256,
                        $ExpectedSha256,
                        [StringComparison]::OrdinalIgnoreCase)
                ) {
                    $preserveBackup = $true
                    return (Stop-RollbackCommitConflict -Message (
                        '롤백 transaction conflict 복구 대상 lease가 staged ' +
                        "결과와 다릅니다. backup=$backupPath"))
                }
                try {
                    [IO.File]::Replace(
                        $backupPath,
                        $TargetPath,
                        $restoreDiscardPath,
                        $true)
                }
                catch {
                    $preserveBackup =
                        Test-Path -LiteralPath $backupPath -PathType Leaf
                    throw
                }
                $restoreDiscardState =
                    Get-RollbackFileState `
                        -Path $restoreDiscardPath `
                        -Label '롤백 conflict 복구 displaced 대상'
                if (-not (Test-RollbackFileStateMatches `
                    -State $restoreDiscardState `
                    -ExpectedExists $true `
                    -ExpectedSha256 $ExpectedSha256 `
                    -ExpectedFileSize $ExpectedFileSize
                )) {
                    $preserveRestoreDiscard = $true
                    return (Stop-RollbackCommitConflict -Message (
                        '롤백 transaction conflict 복구 중 제3자 쓰기가 ' +
                        '감지되었습니다. 복구된 pre-image와 displaced 대상 ' +
                        '모두를 보존했습니다. ' +
                        "displaced=$restoreDiscardPath"))
                }
                Remove-Item `
                    -LiteralPath $restoreDiscardPath `
                    -Force `
                    -ErrorAction Stop
                return (Stop-RollbackCommitConflict -Message (
                    '롤백 transaction 대상 identity가 commit 중 변경되어 ' +
                    '원래 대상 bytes를 복구하고 중단했습니다.'))
            }
            Remove-Item `
                -LiteralPath $backupPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
        else {
            [IO.File]::Move($temporaryPath, $TargetPath)
        }
        $committedStream = [IO.File]::Open(
            $TargetPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        try {
            $committedStream.Flush($true)
        }
        finally {
            $committedStream.Dispose()
        }
        $null = Get-VerifiedManifestFileEvidence `
            -Path $TargetPath `
            -ExpectedSha256 $ExpectedSha256 `
            -ExpectedFileSize $ExpectedFileSize `
            -Label '롤백 transaction 대상'
        return $true
    }
    finally {
        if ($null -ne $targetStream) {
            $targetStream.Dispose()
        }
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
        if ($null -ne $preImageLease) {
            $preImageLease.Dispose()
        }
        if ($null -ne $stagedTargetLease) {
            $stagedTargetLease.Dispose()
        }
        Remove-Item `
            -LiteralPath $temporaryPath `
            -Force `
            -ErrorAction SilentlyContinue
        if (-not $preserveBackup) {
            Remove-Item `
                -LiteralPath $backupPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
        if (-not $preserveRestoreDiscard) {
            Remove-Item `
                -LiteralPath $restoreDiscardPath `
                -Force `
                -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-RollbackTestKillPoint {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not [string]::Equals(
        [string]$env:GEORAEPLAN_ROLLBACK_TEST_KILL_POINT,
        $Name,
        [StringComparison]::Ordinal
    )) {
        return
    }
    [Diagnostics.Process]::GetCurrentProcess().Kill()
}

function New-RollbackTransactionJournal {
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)]$RollbackTargetManifest,
        [Parameter(Mandatory = $true)]$RollbackPreviousManifest,
        [string]$RollbackTargetSourcePath = '',
        [string]$RollbackPreviousSourcePath = '',
        $RollbackGeneration,
        $RollbackPointer
    )

    Write-RollbackPreparationOwner `
        -OwnerPath $OwnerPath `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -TransactionRoot $TransactionRoot `
        -Channel $Channel `
        -Phase 'Preparing'
    New-Item -ItemType Directory -Path $TransactionRoot | Out-Null
    $stagingRoot = Join-Path $TransactionRoot 'staging'
    New-Item -ItemType Directory -Path $stagingRoot | Out-Null
    $entries =
        [Collections.Generic.List[object]]::new()

    if ($null -ne $RollbackGeneration) {
        $generationStagePath =
            Join-Path $stagingRoot 'delivery-generation.json'
        Copy-ManifestEvidenceAtomically `
            -SourcePath $RollbackGeneration.DeliverySourcePath `
            -TargetPath $generationStagePath `
            -ExpectedSha256 $RollbackGeneration.Sha256 `
            -ExpectedFileSize $RollbackGeneration.FileSize
        $generationStageEvidence =
            Get-VerifiedManifestFileEvidence `
                -Path $generationStagePath `
                -ExpectedSha256 $RollbackGeneration.Sha256 `
                -ExpectedFileSize $RollbackGeneration.FileSize `
                -Label '롤백 staged delivery 세대'
        [void]$entries.Add((
            New-RollbackTransactionEntry `
                -Name 'deliveryGeneration' `
                -TargetPath $RollbackGeneration.ExpectedDeliveryPath `
                -StageEvidence $generationStageEvidence))
    }

    $stageDefinitions = @(
        [pscustomobject]@{
            Name = 'currentManifest'
            FileName = 'current.json'
            TargetPath =
                Join-Path $ManifestRoot ($Channel + '.json')
            Value = $RollbackTargetManifest
            SourcePath = $RollbackTargetSourcePath
        },
        [pscustomobject]@{
            Name = 'previousManifest'
            FileName = 'previous.json'
            TargetPath =
                Join-Path $ManifestRoot ($Channel + '.previous.json')
            Value = $RollbackPreviousManifest
            SourcePath = $RollbackPreviousSourcePath
        },
        [pscustomobject]@{
            Name = 'deliveryManifest'
            FileName = 'delivery.json'
            TargetPath =
                Join-Path $ProjectRoot ("배포\" + $Channel + '.json')
            Value = $RollbackTargetManifest
            SourcePath = $RollbackTargetSourcePath
        })
    if ($null -ne $RollbackPointer) {
        $stageDefinitions += [pscustomobject]@{
            Name = 'pointer'
            FileName = 'pointer.json'
            TargetPath =
                Join-Path $ManifestRoot ($Channel + '.current.json')
            Value = $RollbackPointer
            SourcePath = ''
        }
    }
    foreach ($definition in $stageDefinitions) {
        $stagePath =
            Join-Path $stagingRoot ([string]$definition.FileName)
        $stageEvidence = if (
            -not [string]::IsNullOrWhiteSpace(
                [string]$definition.SourcePath)
        ) {
            $sourceItem =
                Get-Item `
                    -LiteralPath ([string]$definition.SourcePath) `
                    -Force `
                    -ErrorAction Stop
            $sourceHash = Get-RollbackFileSha256 `
                -Path ([string]$definition.SourcePath)
            Copy-ManifestEvidenceAtomically `
                -SourcePath ([string]$definition.SourcePath) `
                -TargetPath $stagePath `
                -ExpectedSha256 $sourceHash `
                -ExpectedFileSize ([long]$sourceItem.Length)
            Get-VerifiedManifestFileEvidence `
                -Path $stagePath `
                -ExpectedSha256 $sourceHash `
                -ExpectedFileSize ([long]$sourceItem.Length) `
                -Label (
                    "롤백 staged " +
                    [string]$definition.Name)
        }
        else {
            New-RollbackStageEvidence `
                -StagePath $stagePath `
                -InputObject $definition.Value `
                -Label (
                    "롤백 staged " +
                    [string]$definition.Name)
        }
        [void]$entries.Add((
            New-RollbackTransactionEntry `
                -Name ([string]$definition.Name) `
                -TargetPath ([string]$definition.TargetPath) `
                -StageEvidence $stageEvidence))
    }

    $mode = if ($null -eq $RollbackGeneration) {
        'Legacy'
    }
    else {
        'Pointer'
    }
    $generationId = if ($null -eq $RollbackGeneration) {
        ''
    }
    else {
        [string]$RollbackGeneration.GenerationId
    }
    $entryArray = $entries.ToArray()
    $seenTargets =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entryArray) {
        if (-not $seenTargets.Add(
            [IO.Path]::GetFullPath([string]$entry.targetPath)
        )) {
            throw '롤백 transaction에 중복 target path가 있습니다.'
        }
    }
    $transactionFingerprint =
        Get-RollbackTransactionFingerprint `
            -Channel $Channel `
            -ProjectRoot $ProjectRoot `
            -OutputRoot $OutputRoot `
            -Mode $mode `
            -GenerationId $generationId `
            -Entries $entryArray
    $journal = [ordered]@{
        owner = 'georaeplan-update-rollback-transaction'
        schemaVersion = '2'
        channel = $Channel
        projectRoot = [IO.Path]::GetFullPath($ProjectRoot)
        outputRoot = [IO.Path]::GetFullPath($OutputRoot)
        transactionRoot = [IO.Path]::GetFullPath($TransactionRoot)
        mode = $mode
        generationId = $generationId
        transactionFingerprint = $transactionFingerprint
        phase = 'Applying'
        nextIndex = '0'
        entries = $entryArray
    }
    Write-JsonFileAtomically `
        -TargetPath $JournalPath `
        -InputObject $journal
    $validated = Read-RollbackTransactionJournal `
        -JournalPath $JournalPath `
        -TransactionRoot $TransactionRoot `
        -ManifestRoot $ManifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    Remove-Item -LiteralPath $OwnerPath -Force -ErrorAction Stop
    return $validated
}

function Complete-RollbackTransactionCleanup {
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $completedJournal = Read-RollbackTransactionJournal `
        -JournalPath $JournalPath `
        -TransactionRoot $TransactionRoot `
        -ManifestRoot $ManifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    if ([string]$completedJournal.phase -cne 'Completed') {
        throw '검증된 Completed journal 없이는 롤백 cleanup을 시작할 수 없습니다.'
    }
    Write-RollbackPreparationOwner `
        -OwnerPath $OwnerPath `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -TransactionRoot $TransactionRoot `
        -Channel $Channel `
        -Phase 'Cleanup' `
        -CompletedJournal $completedJournal
    Remove-RollbackOwnedDirectoryTree `
        -Path $TransactionRoot `
        -ExpectedPath $TransactionRoot
    Invoke-RollbackTestKillPoint -Name 'AfterRollbackCleanupRootDelete'
    Remove-Item -LiteralPath $OwnerPath -Force -ErrorAction Stop
}

function Resume-RollbackTransaction {
    param(
        [Parameter(Mandatory = $true)][string]$JournalPath,
        [Parameter(Mandatory = $true)][string]$OwnerPath,
        [Parameter(Mandatory = $true)][string]$TransactionRoot,
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$Channel
    )

    $journal = Read-RollbackTransactionJournal `
        -JournalPath $JournalPath `
        -TransactionRoot $TransactionRoot `
        -ManifestRoot $ManifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    if ([string]$journal.phase -ceq 'Applying') {
        $entries = @($journal.entries)
        [int]$nextIndex = [int]$journal.nextIndex
        $killPoints = @{
            deliveryGeneration = 'AfterRollbackDeliveryGenerationWrite'
            currentManifest = 'AfterRollbackCurrentWrite'
            previousManifest = 'AfterRollbackPreviousWrite'
            deliveryManifest = 'AfterRollbackDeliveryWrite'
            pointer = 'AfterRollbackPointerWrite'
        }
        for (
            $index = $nextIndex;
            $index -lt $entries.Count;
            $index++
        ) {
            $entry = $entries[$index]
            $copySucceeded = Copy-RollbackStagedFileAtomically `
                -SourcePath ([string]$entry.stagedPath) `
                -TargetPath ([string]$entry.targetPath) `
                -ExpectedSha256 ([string]$entry.sha256) `
                -ExpectedFileSize ([long]$entry.fileSize) `
                -ExpectedPreImageExists (
                    [bool]$entry.preImageExists) `
                -ExpectedPreImageSha256 (
                    [string]$entry.preImageSha256) `
                -ExpectedPreImageFileSize (
                    [long]$entry.preImageFileSize)
            if (-not $copySucceeded) {
                return $false
            }
            Invoke-RollbackTestKillPoint `
                -Name ([string]$killPoints[[string]$entry.name])
            $journal.nextIndex =
                [string]($index + 1)
            Write-JsonFileAtomically `
                -TargetPath $JournalPath `
                -InputObject $journal
            $journal = Read-RollbackTransactionJournal `
                -JournalPath $JournalPath `
                -TransactionRoot $TransactionRoot `
                -ManifestRoot $ManifestRoot `
                -ProjectRoot $ProjectRoot `
                -OutputRoot $OutputRoot `
                -Channel $Channel
        }
        $journal.phase = 'Completed'
        Write-JsonFileAtomically `
            -TargetPath $JournalPath `
            -InputObject $journal
        $journal = Read-RollbackTransactionJournal `
            -JournalPath $JournalPath `
            -TransactionRoot $TransactionRoot `
            -ManifestRoot $ManifestRoot `
            -ProjectRoot $ProjectRoot `
            -OutputRoot $OutputRoot `
            -Channel $Channel
        Invoke-RollbackTestKillPoint -Name 'AfterRollbackCompletedJournal'
    }

    if ([string]$journal.mode -ceq 'Pointer') {
        $activated =
            Read-VerifiedManifestPointer `
                -PointerPath (
                    Join-Path $ManifestRoot (
                        $Channel + '.current.json')) `
                -ManifestRoot $ManifestRoot `
                -ProjectRoot $ProjectRoot `
                -Channel $Channel
        if (-not [string]::Equals(
            [string]$activated.GenerationId,
            [string]$journal.generationId,
            [StringComparison]::Ordinal
        )) {
            throw '롤백 pointer 활성화 검증에 실패했습니다.'
        }
        Write-Host (
            'rollback_manifest=SWAPPED generation=' +
            [string]$activated.GenerationId)
    }
    else {
        Write-Host 'rollback_manifest=SWAPPED'
    }
    Complete-RollbackTransactionCleanup `
        -JournalPath $JournalPath `
        -OwnerPath $OwnerPath `
        -TransactionRoot $TransactionRoot `
        -ManifestRoot $ManifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    return $true
}

function Open-RollbackExclusiveFileLease {
    param(
        [Parameter(Mandatory = $true)][string]$LockPath,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            return [IO.FileStream]::new(
                $LockPath,
                [IO.FileMode]::OpenOrCreate,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None,
                4096,
                [IO.FileOptions]::WriteThrough)
        }
        catch [IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw (
                    'Timed out waiting for the shared release publish lock: ' +
                    $LockPath)
            }
            Start-Sleep -Milliseconds 25
        }
    }
}

function Open-RollbackDeliveryPublishLock {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$Channel,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $deliveryRoot =
        [IO.Path]::GetFullPath((Join-Path $ProjectRoot '배포'))
    if (-not (Test-Path -LiteralPath $deliveryRoot)) {
        Assert-RollbackPathHasNoReparsePoint `
            -Path (Split-Path -Parent $deliveryRoot) `
            -Label 'shared delivery lock parent'
        [void][IO.Directory]::CreateDirectory($deliveryRoot)
    }
    Assert-RollbackPathHasNoReparsePoint `
        -Path $deliveryRoot `
        -Label 'shared delivery lock parent'
    $deliveryItem =
        Get-Item -LiteralPath $deliveryRoot -Force -ErrorAction Stop
    if (-not $deliveryItem.PSIsContainer) {
        throw 'Shared delivery lock parent is not a regular directory.'
    }
    $lockPath = Join-Path $deliveryRoot (
        '.georaeplan-release-publish-' + $Channel + '.lock')
    $lease = Open-RollbackExclusiveFileLease `
        -LockPath $lockPath `
        -TimeoutSeconds $TimeoutSeconds
    try {
        Assert-RollbackPathHasNoReparsePoint `
            -Path $lockPath `
            -Label 'shared delivery lock'
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($lease.Name),
            [IO.Path]::GetFullPath($lockPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Shared delivery lock binding is invalid.'
        }
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

function Open-RollbackOutputPublishLock {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    if (-not (Test-Path -LiteralPath $OutputRoot)) {
        Assert-RollbackPathHasNoReparsePoint `
            -Path (Split-Path -Parent $OutputRoot) `
            -Label 'release publish lock parent'
        [void][IO.Directory]::CreateDirectory($OutputRoot)
    }
    Assert-RollbackPathHasNoReparsePoint `
        -Path $OutputRoot `
        -Label 'release publish lock parent'
    $outputItem =
        Get-Item -LiteralPath $OutputRoot -Force -ErrorAction Stop
    if (-not $outputItem.PSIsContainer) {
        throw 'Release publish lock parent is not a regular directory.'
    }
    $lockPath =
        Join-Path $OutputRoot '.georaeplan-release-publish.lock'
    $lease = Open-RollbackExclusiveFileLease `
        -LockPath $lockPath `
        -TimeoutSeconds $TimeoutSeconds
    try {
        Assert-RollbackPathHasNoReparsePoint `
            -Path $lockPath `
            -Label 'release publish lock'
        if (-not [string]::Equals(
            [IO.Path]::GetFullPath($lease.Name),
            [IO.Path]::GetFullPath($lockPath),
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw 'Release publish lock binding is invalid.'
        }
        return $lease
    }
    catch {
        $lease.Dispose()
        throw
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
}
else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot '배포\업데이트'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$preflightManifestRoot = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $OutputRoot 'manifest') `
    -Root $OutputRoot `
    -Label 'update manifest root'
$preflightPointerPath = Assert-RollbackPathWithinRoot `
    -Path (
        Join-Path $preflightManifestRoot (
            $Channel + '.current.json')) `
    -Root $preflightManifestRoot `
    -Label 'update manifest pointer'
$null =
    Get-RollbackRegularManifestPointerItem `
        -Path $preflightPointerPath `
        -Label '현재 update manifest pointer' `
        -AllowMissing

$rollbackDeliveryPublishLease = $null
$rollbackOutputPublishLease = $null
$rollbackFailureMessage = $null
$script:rollbackCommitConflictMessage = $null
try {
$rollbackDeliveryPublishLease =
    Open-RollbackDeliveryPublishLock `
        -ProjectRoot $ProjectRoot `
        -Channel $Channel `
        -TimeoutSeconds $LockTimeoutSeconds
$rollbackOutputPublishLease =
    Open-RollbackOutputPublishLock `
        -OutputRoot $OutputRoot `
        -TimeoutSeconds $LockTimeoutSeconds

$manifestRoot = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $OutputRoot 'manifest') `
    -Root $OutputRoot `
    -Label 'update manifest root'
$downloadsRoot = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $OutputRoot 'downloads') `
    -Root $OutputRoot `
    -Label 'update downloads root'
$deliveryRoot = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $ProjectRoot '배포') `
    -Root $ProjectRoot `
    -Label 'delivery root'
$currentPath = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $manifestRoot ($Channel + '.json')) `
    -Root $manifestRoot `
    -Label '현재 update manifest'
$previousPath = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $manifestRoot ($Channel + '.previous.json')) `
    -Root $manifestRoot `
    -Label '이전 update manifest'
$deliveryPath = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $deliveryRoot ($Channel + '.json')) `
    -Root $deliveryRoot `
    -Label 'delivery manifest'
$pointerPath = Assert-RollbackPathWithinRoot `
    -Path (Join-Path $manifestRoot ($Channel + '.current.json')) `
    -Root $manifestRoot `
    -Label 'update manifest pointer'
$transactionRoot = Assert-RollbackPathWithinRoot `
    -Path (
        Join-Path $OutputRoot (
            '.georaeplan-update-rollback-' + $Channel)) `
    -Root $OutputRoot `
    -Label 'rollback transaction'
$journalPath = Join-Path $transactionRoot 'journal.json'
$preparationOwnerPath = Assert-RollbackPathWithinRoot `
    -Path (
        Join-Path $OutputRoot (
            '.georaeplan-update-rollback-' +
            $Channel +
            '.owner.json')) `
    -Root $OutputRoot `
    -Label 'rollback transaction owner'

foreach ($pathToCheck in @(
    [pscustomobject]@{ Path = $ProjectRoot; Label = 'project root' },
    [pscustomobject]@{ Path = $OutputRoot; Label = 'output root' },
    [pscustomobject]@{ Path = $manifestRoot; Label = 'manifest root' },
    [pscustomobject]@{ Path = $downloadsRoot; Label = 'downloads root' },
    [pscustomobject]@{ Path = $deliveryRoot; Label = 'delivery root' },
    [pscustomobject]@{
        Path = $currentPath
        Label = '현재 update manifest'
    },
    [pscustomobject]@{
        Path = $previousPath
        Label = '이전 update manifest'
    },
    [pscustomobject]@{
        Path = $deliveryPath
        Label = 'delivery manifest'
    },
    [pscustomobject]@{
        Path = $pointerPath
        Label = 'update manifest pointer'
    },
    [pscustomobject]@{
        Path = $transactionRoot
        Label = 'rollback transaction'
    },
    [pscustomobject]@{
        Path = $preparationOwnerPath
        Label = 'rollback transaction owner'
    }
)) {
    Assert-RollbackPathHasNoReparsePoint `
        -Path ([string]$pathToCheck.Path) `
        -Label ([string]$pathToCheck.Label)
}

$recoveryState = Resume-RollbackPreparationOrCleanup `
    -OwnerPath $preparationOwnerPath `
    -JournalPath $journalPath `
    -ProjectRoot $ProjectRoot `
    -OutputRoot $OutputRoot `
    -TransactionRoot $transactionRoot `
    -ManifestRoot $manifestRoot `
    -Channel $Channel
if ($recoveryState -ceq 'CompletedCleanup') {
    Write-Host 'rollback_cleanup_recovery=complete'
    exit 0
}
if ($recoveryState -ceq 'JournalReady') {
    $null = Read-RollbackTransactionJournal `
        -JournalPath $journalPath `
        -TransactionRoot $transactionRoot `
        -ManifestRoot $manifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    Remove-Item `
        -LiteralPath $preparationOwnerPath `
        -Force `
        -ErrorAction Stop
}
if (Test-Path -LiteralPath $journalPath -PathType Leaf) {
    $pendingJournal = Read-RollbackTransactionJournal `
        -JournalPath $journalPath `
        -TransactionRoot $transactionRoot `
        -ManifestRoot $manifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    if (-not $Apply) {
        Write-Host (
            'rollback_manifest=RECOVERY_PENDING phase=' +
            [string]$pendingJournal.phase +
            ' next_index=' +
            [string]$pendingJournal.nextIndex)
        Write-Host '중단된 롤백 재개는 -Apply를 지정해야 합니다.'
        exit 0
    }
    $resumeSucceeded = Resume-RollbackTransaction `
        -JournalPath $journalPath `
        -OwnerPath $preparationOwnerPath `
        -TransactionRoot $transactionRoot `
        -ManifestRoot $manifestRoot `
        -ProjectRoot $ProjectRoot `
        -OutputRoot $OutputRoot `
        -Channel $Channel
    if (-not $resumeSucceeded) {
        if ([string]::IsNullOrWhiteSpace(
            [string]$script:rollbackCommitConflictMessage
        )) {
            throw '롤백 transaction commit conflict로 재개를 중단했습니다.'
        }
        throw [string]$script:rollbackCommitConflictMessage
    }
    exit 0
}

if (-not (Test-Path -LiteralPath $currentPath)) {
    throw "현재 update manifest를 찾을 수 없습니다: $currentPath"
}
if (-not (Test-Path -LiteralPath $previousPath)) {
    throw "이전 정상 update manifest를 찾을 수 없습니다: $previousPath"
}

$current = Get-Content -LiteralPath $currentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$previous = Get-Content -LiteralPath $previousPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pointerEvidence = $null
$rollbackGeneration = $null
$pointerItem =
    Get-RollbackRegularManifestPointerItem `
        -Path $pointerPath `
        -Label '현재 update manifest pointer' `
        -AllowMissing
if ($null -ne $pointerItem) {
    $pointerEvidence =
        Read-VerifiedManifestPointer `
            -PointerPath $pointerPath `
            -ManifestRoot $manifestRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
    $currentGenerationId =
        Get-ManifestGenerationId -Manifest $current -Label '현재'
    if (-not [string]::Equals(
        $currentGenerationId,
        [string]$pointerEvidence.GenerationId,
        [StringComparison]::Ordinal
    )) {
        throw '현재 stable manifest와 pointer-selected 세대가 다릅니다.'
    }
    $rollbackGeneration =
        Get-VerifiedRollbackGeneration `
            -Manifest $previous `
            -ManifestRoot $manifestRoot `
            -ProjectRoot $ProjectRoot `
            -Channel $Channel
}
$rollbackTargetManifest = if ($null -eq $pointerEvidence) {
    $previous
}
else {
    $rollbackGeneration.Manifest
}
$rollbackPreviousManifest = if ($null -eq $pointerEvidence) {
    $current
}
else {
    $pointerEvidence.Manifest
}
$rollbackTargetSourcePath = if ($null -eq $pointerEvidence) {
    ''
}
else {
    [string]$rollbackGeneration.RuntimePath
}
$rollbackPreviousSourcePath = if ($null -eq $pointerEvidence) {
    ''
}
else {
    [string]$pointerEvidence.RuntimeEvidence.Path
}
$rollbackPointer = $null
if ($null -ne $pointerEvidence) {
    $rollbackPointer = [ordered]@{
        owner = 'georaeplan-release-manifest-pointer'
        schemaVersion = '1'
        channel = $Channel
        generationId = [string]$rollbackGeneration.GenerationId
        manifestRelativePath =
            'generations/{0}/{1}.json' -f (
                $Channel,
                [string]$rollbackGeneration.GenerationId)
        manifestSha256 = [string]$rollbackGeneration.Sha256
        manifestFileSize = [string]$rollbackGeneration.FileSize
        deliveryManifestPath =
            [string]$rollbackGeneration.ExpectedDeliveryPath
        deliveryManifestSha256 = [string]$rollbackGeneration.Sha256
        deliveryManifestFileSize = [string]$rollbackGeneration.FileSize
    }
}

foreach ($platform in @('desktop', 'android')) {
    $package = $rollbackTargetManifest.$platform
    if ($null -ne $package) {
        Test-ManifestPackage -Package $package -Platform $platform -DownloadsRoot $downloadsRoot
    }
}

Write-Host "rollback_current_desktop=$(Get-ManifestPlatformVersion -Manifest $current -Platform 'desktop')"
Write-Host "rollback_target_desktop=$(Get-ManifestPlatformVersion -Manifest $rollbackTargetManifest -Platform 'desktop')"
Write-Host "rollback_current_android=$(Get-ManifestPlatformVersion -Manifest $current -Platform 'android')"
Write-Host "rollback_target_android=$(Get-ManifestPlatformVersion -Manifest $rollbackTargetManifest -Platform 'android')"
if ($null -ne $pointerEvidence) {
    Write-Host (
        'rollback_current_generation=' +
        [string]$pointerEvidence.GenerationId)
    Write-Host (
        'rollback_target_generation=' +
        [string]$rollbackGeneration.GenerationId)
}

if (-not $Apply) {
    Write-Host 'rollback_manifest=PREVIEW_OK'
    Write-Host '실제 전환은 -Apply를 지정해야 합니다.'
    exit 0
}

$null = New-RollbackTransactionJournal `
    -JournalPath $journalPath `
    -OwnerPath $preparationOwnerPath `
    -TransactionRoot $transactionRoot `
    -ManifestRoot $manifestRoot `
    -ProjectRoot $ProjectRoot `
    -OutputRoot $OutputRoot `
    -Channel $Channel `
    -RollbackTargetManifest $rollbackTargetManifest `
    -RollbackPreviousManifest $rollbackPreviousManifest `
    -RollbackTargetSourcePath $rollbackTargetSourcePath `
    -RollbackPreviousSourcePath $rollbackPreviousSourcePath `
    -RollbackGeneration $rollbackGeneration `
    -RollbackPointer $rollbackPointer
$resumeSucceeded = Resume-RollbackTransaction `
    -JournalPath $journalPath `
    -OwnerPath $preparationOwnerPath `
    -TransactionRoot $transactionRoot `
    -ManifestRoot $manifestRoot `
    -ProjectRoot $ProjectRoot `
    -OutputRoot $OutputRoot `
    -Channel $Channel
if (-not $resumeSucceeded) {
    if ([string]::IsNullOrWhiteSpace(
        [string]$script:rollbackCommitConflictMessage
    )) {
        throw '롤백 transaction commit conflict로 적용을 중단했습니다.'
    }
    throw [string]$script:rollbackCommitConflictMessage
}
}
catch {
    $rollbackFailureMessage = [string]$_.Exception.Message
}
finally {
    if ($null -ne $rollbackOutputPublishLease) {
        $rollbackOutputPublishLease.Dispose()
        $rollbackOutputPublishLease = $null
    }
    if ($null -ne $rollbackDeliveryPublishLease) {
        $rollbackDeliveryPublishLease.Dispose()
        $rollbackDeliveryPublishLease = $null
    }
}
if ($null -ne $rollbackFailureMessage) {
    [Console]::Error.WriteLine($rollbackFailureMessage)
    [Environment]::Exit(1)
}
