[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [string]$Channel = 'stable',
    [int]$KeepDesktopPackageCount = 10,
    [int]$KeepAndroidPackageCount = 10,
    [switch]$SkipPackagePrune,
    [string]$DesktopPackagePath,
    [string]$AndroidPackagePath,
    [string]$DesktopVersion,
    [string]$AndroidVersion,
    [string]$DesktopMinimumSupportedVersion,
    [string]$AndroidMinimumSupportedVersion,
    [string]$DesktopNotes,
    [string]$AndroidNotes,
    [switch]$MandatoryDesktop,
    [switch]$MandatoryAndroid
)

function Resolve-ProjectRoot {
    param([string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Get-CsprojPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    [xml]$xml = Get-Content -LiteralPath $ProjectFile -Raw
    foreach ($group in $xml.Project.PropertyGroup) {
        $property = $group.$PropertyName
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property)) {
            return ([string]$property).Trim()
        }
    }

    return $null
}

function Copy-PackageWithMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$OutputFileName,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][bool]$Mandatory,
        [string]$MinimumSupportedVersion
    )

    if ($Platform -eq 'desktop') {
        Test-DesktopUpdatePackage -PackagePath $SourcePath
    }

    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    $destinationPath = Join-Path $DestinationDirectory $OutputFileName
    Copy-Item -LiteralPath $SourcePath -Destination $destinationPath -Force

    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $destinationPath
    $fileInfo = Get-Item -LiteralPath $destinationPath

    $resolvedMinimumSupportedVersion = if ([string]::IsNullOrWhiteSpace($MinimumSupportedVersion)) {
        if ($Mandatory) { $Version } else { '' }
    }
    else {
        $MinimumSupportedVersion.Trim()
    }

    return [ordered]@{
        platform = $Platform
        version = $Version
        mandatory = $Mandatory
        minimumSupportedVersion = $resolvedMinimumSupportedVersion
        packageUrl = "/updates/download/$Platform/$([Uri]::EscapeDataString($OutputFileName))"
        fileName = $OutputFileName
        sha256 = $hash.Hash
        fileSize = [int64]$fileInfo.Length
        notes = $Notes
        releasedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
}

function Test-DesktopUpdatePackage {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $requiredEntries = @(
        'Install-GeoraePlan.ps1',
        'App/거래플랜.exe',
        'App/appsettings.json',
        'App/Updater/거래플랜.Updater.exe'
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entryNames = $archive.Entries | ForEach-Object {
            $_.FullName.Replace('\', '/').TrimStart('/')
        }

        foreach ($requiredEntry in $requiredEntries) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "데스크톱 업데이트 패키지 필수 항목이 누락되었습니다: $requiredEntry"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-ManifestReferencedFileNames {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestRoot,
        [Parameter(Mandatory = $true)][string]$Platform
    )

    $fileNames = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    if (-not (Test-Path -LiteralPath $ManifestRoot)) {
        return $fileNames
    }

    $manifestFiles = Get-ChildItem -LiteralPath $ManifestRoot -File -Filter '*.json' -ErrorAction SilentlyContinue
    foreach ($manifestFile in $manifestFiles) {
        try {
            $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $platformNode = $manifest.$Platform
            $fileName = [string]$platformNode.fileName
            if (-not [string]::IsNullOrWhiteSpace($fileName)) {
                [void]$fileNames.Add($fileName.Trim())
            }
        }
        catch {
        }
    }

    return $fileNames
}

function Remove-OldPackages {
    param(
        [Parameter(Mandatory = $true)][string]$DirectoryPath,
        [Parameter(Mandatory = $true)][int]$KeepCount,
        [Parameter(Mandatory = $true)]$PreserveFileNames
    )

    if ($KeepCount -lt 1 -or -not (Test-Path -LiteralPath $DirectoryPath)) {
        return @()
    }

    $removed = New-Object System.Collections.Generic.List[string]
    $keptNonPreserved = 0
    $files = Get-ChildItem -LiteralPath $DirectoryPath -File -ErrorAction SilentlyContinue |
        Sort-Object -Property @(
            @{ Expression = 'LastWriteTimeUtc'; Descending = $true },
            @{ Expression = 'Name'; Descending = $false }
        )

    foreach ($file in $files) {
        if ($PreserveFileNames.Contains($file.Name)) {
            continue
        }

        if ($keptNonPreserved -lt $KeepCount) {
            $keptNonPreserved++
            continue
        }

        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        $removed.Add($file.Name) | Out-Null
    }

    return $removed
}

function Write-JsonFileAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)]$InputObject
    )

    $directory = Split-Path -Parent $TargetPath
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    $fileName = Split-Path -Leaf $TargetPath
    $tempPath = Join-Path $directory ($fileName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $backupPath = Join-Path $directory ($fileName + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
    $json = $InputObject | ConvertTo-Json -Depth 10

    try {
        Set-Content -LiteralPath $tempPath -Value $json -Encoding UTF8

        if (Test-Path -LiteralPath $TargetPath) {
            [System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)
            Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        }
        else {
            Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force
        }
    }
    catch {
        foreach ($path in @($tempPath, $backupPath)) {
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            }
        }

        throw
    }
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot '배포\업데이트'
}

if ([string]::IsNullOrWhiteSpace($DesktopPackagePath)) {
    $desktopCandidates = @(
        (Join-Path $ProjectRoot '배포\관리자용\거래플랜-PC-설치패키지.zip'),
        (Join-Path $ProjectRoot '배포\설치패키지\관리자용\거래플랜-PC-설치패키지.zip'),
        (Join-Path $ProjectRoot '배포\설치패키지\거래플랜-PC-설치패키지.zip')
    )
    $DesktopPackagePath = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($AndroidPackagePath)) {
    $androidCandidates = @(
        (Get-ChildItem -Path (Join-Path $ProjectRoot '배포') -File -Filter '거래플랜-안드로이드-v*-signed.apk' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1),
        (Get-ChildItem -Path (Join-Path $ProjectRoot 'Mobile\artifacts\android') -File -Filter '거래플랜-안드로이드-*.apk' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1)
    ) | Where-Object { $null -ne $_ }

    $androidNamed = $androidCandidates | Select-Object -First 1
    if ($null -ne $androidNamed) {
        $AndroidPackagePath = $androidNamed.FullName
    }
}

$desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
$androidProject = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'

if ([string]::IsNullOrWhiteSpace($DesktopVersion)) {
    $DesktopVersion = Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
}
if ([string]::IsNullOrWhiteSpace($AndroidVersion)) {
    $AndroidVersion = Get-CsprojPropertyValue -ProjectFile $androidProject -PropertyName 'ApplicationDisplayVersion'
}
if ([string]::IsNullOrWhiteSpace($DesktopNotes)) {
    $DesktopNotes = '내부 업데이트 패키지/동기화 안정화 개선 반영'
}
if ([string]::IsNullOrWhiteSpace($AndroidNotes)) {
    $AndroidNotes = '모바일 상세탭/첨부 보기/내부 업데이트 확인 기능 반영'
}

$manifestRoot = Join-Path $OutputRoot 'manifest'
$downloadsRoot = Join-Path $OutputRoot 'downloads'
New-Item -ItemType Directory -Force -Path $manifestRoot | Out-Null
New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null

$manifest = [ordered]@{
    channel = $Channel
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    desktop = $null
    android = $null
}

if (-not [string]::IsNullOrWhiteSpace($DesktopPackagePath) -and (Test-Path -LiteralPath $DesktopPackagePath)) {
    $desktopFileName = "tradeplan-pc-installer-v$DesktopVersion.zip"
    $manifest.desktop = Copy-PackageWithMetadata -SourcePath $DesktopPackagePath -DestinationDirectory (Join-Path $downloadsRoot 'desktop') -OutputFileName $desktopFileName -Platform 'desktop' -Version $DesktopVersion -Notes $DesktopNotes -Mandatory:$MandatoryDesktop -MinimumSupportedVersion $DesktopMinimumSupportedVersion
}

if (-not [string]::IsNullOrWhiteSpace($AndroidPackagePath) -and (Test-Path -LiteralPath $AndroidPackagePath)) {
    $androidFileName = "tradeplan-android-v$AndroidVersion.apk"
    $manifest.android = Copy-PackageWithMetadata -SourcePath $AndroidPackagePath -DestinationDirectory (Join-Path $downloadsRoot 'android') -OutputFileName $androidFileName -Platform 'android' -Version $AndroidVersion -Notes $AndroidNotes -Mandatory:$MandatoryAndroid -MinimumSupportedVersion $AndroidMinimumSupportedVersion
}

$manifestPath = Join-Path $manifestRoot ($Channel + '.json')
Write-JsonFileAtomically -TargetPath $manifestPath -InputObject $manifest

$removedDesktopPackages = @()
$removedAndroidPackages = @()
if (-not $SkipPackagePrune) {
    $preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'
    $preservedAndroidFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'android'
    $removedDesktopPackages = Remove-OldPackages -DirectoryPath (Join-Path $downloadsRoot 'desktop') -KeepCount $KeepDesktopPackageCount -PreserveFileNames $preservedDesktopFiles
    $removedAndroidPackages = Remove-OldPackages -DirectoryPath (Join-Path $downloadsRoot 'android') -KeepCount $KeepAndroidPackageCount -PreserveFileNames $preservedAndroidFiles
}

Write-Host "update_manifest=$manifestPath"
if ($manifest.desktop) { Write-Host "desktop_package=$($manifest.desktop.fileName)" }
if ($manifest.android) { Write-Host "android_package=$($manifest.android.fileName)" }
if ($removedDesktopPackages.Count -gt 0) { Write-Host "desktop_packages_pruned=$($removedDesktopPackages.Count)" }
if ($removedAndroidPackages.Count -gt 0) { Write-Host "android_packages_pruned=$($removedAndroidPackages.Count)" }
