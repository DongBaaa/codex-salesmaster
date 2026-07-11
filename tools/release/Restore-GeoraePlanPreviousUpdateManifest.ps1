[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [string]$Channel = 'stable',
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$InputObject
    )

    $directory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $tempPath = Join-Path $directory ((Split-Path -Leaf $TargetPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ((Split-Path -Leaf $TargetPath) + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    try {
        Set-Content -LiteralPath $tempPath -Value ($InputObject | ConvertTo-Json -Depth 10) -Encoding UTF8
        if (Test-Path -LiteralPath $TargetPath) {
            [System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
        else {
            Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force
        }
    }
    finally {
        foreach ($path in @($tempPath, $backupPath)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Test-ManifestPackage {
    param(
        [Parameter(Mandatory = $true)]$Package,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$DownloadsRoot
    )

    $fileName = [string]$Package.fileName
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "$Platform 이전 manifest에 package fileName이 없습니다."
    }

    $packagePath = Join-Path (Join-Path $DownloadsRoot $Platform) $fileName
    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "$Platform 이전 패키지를 찾을 수 없습니다: $packagePath"
    }

    $file = Get-Item -LiteralPath $packagePath
    $expectedSize = [int64]$Package.fileSize
    if ($expectedSize -gt 0 -and $file.Length -ne $expectedSize) {
        throw "$Platform 이전 패키지 크기가 manifest와 다릅니다: expected=$expectedSize actual=$($file.Length)"
    }

    $expectedHash = ([string]$Package.sha256).Trim()
    if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $packagePath).Hash
        if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Platform 이전 패키지 SHA256이 manifest와 다릅니다."
        }
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

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
}
else {
    $ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot '배포\업데이트'
}

$manifestRoot = Join-Path $OutputRoot 'manifest'
$downloadsRoot = Join-Path $OutputRoot 'downloads'
$currentPath = Join-Path $manifestRoot ($Channel + '.json')
$previousPath = Join-Path $manifestRoot ($Channel + '.previous.json')
$deliveryPath = Join-Path $ProjectRoot ("배포\" + $Channel + '.json')

if (-not (Test-Path -LiteralPath $currentPath)) {
    throw "현재 update manifest를 찾을 수 없습니다: $currentPath"
}
if (-not (Test-Path -LiteralPath $previousPath)) {
    throw "이전 정상 update manifest를 찾을 수 없습니다: $previousPath"
}

$current = Get-Content -LiteralPath $currentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$previous = Get-Content -LiteralPath $previousPath -Raw -Encoding UTF8 | ConvertFrom-Json

foreach ($platform in @('desktop', 'android')) {
    $package = $previous.$platform
    if ($null -ne $package) {
        Test-ManifestPackage -Package $package -Platform $platform -DownloadsRoot $downloadsRoot
    }
}

Write-Host "rollback_current_desktop=$(Get-ManifestPlatformVersion -Manifest $current -Platform 'desktop')"
Write-Host "rollback_target_desktop=$(Get-ManifestPlatformVersion -Manifest $previous -Platform 'desktop')"
Write-Host "rollback_current_android=$(Get-ManifestPlatformVersion -Manifest $current -Platform 'android')"
Write-Host "rollback_target_android=$(Get-ManifestPlatformVersion -Manifest $previous -Platform 'android')"

if (-not $Apply) {
    Write-Host 'rollback_manifest=PREVIEW_OK'
    Write-Host '실제 전환은 -Apply를 지정해야 합니다.'
    exit 0
}

Write-JsonFileAtomically -TargetPath $currentPath -InputObject $previous
Write-JsonFileAtomically -TargetPath $previousPath -InputObject $current
Write-JsonFileAtomically -TargetPath $deliveryPath -InputObject $previous
Write-Host 'rollback_manifest=SWAPPED'
