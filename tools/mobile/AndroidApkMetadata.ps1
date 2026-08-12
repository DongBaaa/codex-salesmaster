function Resolve-GeoraePlanApkAnalyzerPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "apkanalyzer not found at requested path: $RequestedPath"
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkCandidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $sdkCandidates.Add($candidate) | Out-Null
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $sdkCandidates.Add(
            (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) | Out-Null
        $sdkCandidates.Add(
            (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk')) |
            Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($ProjectRoot)) {
        $sdkCandidates.Add(
            (Join-Path $ProjectRoot '.android-sdk')) | Out-Null
        $sdkCandidates.Add(
            (Join-Path $ProjectRoot '.tooling\android-sdk')) | Out-Null
    }

    foreach ($sdkRoot in $sdkCandidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($sdkRoot) -or
            -not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
            continue
        }

        foreach ($candidate in @(
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer')
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }

        $commandLineToolsRoot = Join-Path $sdkRoot 'cmdline-tools'
        if (-not (Test-Path -LiteralPath $commandLineToolsRoot -PathType Container)) {
            continue
        }

        $analyzer = Get-ChildItem `
                -LiteralPath $commandLineToolsRoot `
                -File `
                -Recurse `
                -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Name -in @('apkanalyzer.bat', 'apkanalyzer')
            } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $analyzer) {
            return $analyzer.FullName
        }
    }

    throw (
        'apkanalyzer not found. Install Android SDK command-line tools ' +
        'or pass -ApkAnalyzerPath.')
}

function Resolve-GeoraePlanJavaHome {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedJava = Join-Path $RequestedPath 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            $requestedJava = Join-Path $RequestedPath 'bin\java'
        }
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            throw "Java SDK not found at requested path: $RequestedPath"
        }

        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add($env:JAVA_HOME) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates.Add(
            (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr')) |
            Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates.Add(
            (Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\jbr')) |
            Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add(
            (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')) |
            Out-Null
    }

    foreach ($commandName in @('java', 'javac', 'keytool')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add(
                (Split-Path -Parent (Split-Path -Parent $command.Source))) |
                Out-Null
        }
    }

    $javaPatterns = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        $javaPatterns.Add(
            (Join-Path $env:USERPROFILE '.antigravity\extensions\*\jre\*\bin\java.exe')) |
            Out-Null
    }
    $javaPatterns.Add('C:\Program Files\Microsoft\jdk*\bin\java.exe') |
        Out-Null
    $javaPatterns.Add('C:\Program Files\Java\*\bin\java.exe') |
        Out-Null

    foreach ($pattern in $javaPatterns) {
        $match = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $match) {
            $candidates.Add(
                (Split-Path -Parent (Split-Path -Parent $match.FullName))) |
                Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $javaExecutable = Join-Path $candidate 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $javaExecutable -PathType Leaf)) {
            $javaExecutable = Join-Path $candidate 'bin\java'
        }
        if (Test-Path -LiteralPath $javaExecutable -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw (
        'Java SDK not found. Install JDK 17+ or pass -JavaSdkDirectory.')
}

function Invoke-GeoraePlanApkAnalyzerSingleValue {
    param(
        [Parameter(Mandatory = $true)][string]$ApkAnalyzerPath,
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $ApkAnalyzerPath manifest $Command $ApkPath 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $outputText = ($output | Out-String -Width 4096).Trim()
    if ($exitCode -ne 0) {
        throw (
            "$SourceName APK apkanalyzer manifest $Command failed " +
            "(exit=$exitCode): $outputText")
    }

    $values = @(
        $output |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($values.Count -ne 1) {
        throw (
            "$SourceName APK apkanalyzer manifest $Command must produce " +
            'exactly one non-empty output value.')
    }

    return $values[0]
}

function Get-GeoraePlanAndroidApkMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$ApkAnalyzerPath,
        [string]$JavaSdkDirectory,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if ([string]::IsNullOrWhiteSpace($SourceName)) {
        throw 'Android APK metadata source name is required.'
    }
    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
        throw "$SourceName APK must be an existing leaf file: $ApkPath"
    }

    $resolvedApkPath = (Resolve-Path -LiteralPath $ApkPath).Path
    $apkFileBefore = Get-Item -LiteralPath $resolvedApkPath
    if ($apkFileBefore.Length -le 0) {
        throw "$SourceName APK must be non-empty: $resolvedApkPath"
    }
    $hashBefore = (
        Get-FileHash -LiteralPath $resolvedApkPath -Algorithm SHA256
    ).Hash

    $resolvedAnalyzer = Resolve-GeoraePlanApkAnalyzerPath `
        -ProjectRoot $ProjectRoot `
        -RequestedPath $ApkAnalyzerPath
    $resolvedJavaHome = Resolve-GeoraePlanJavaHome `
        -RequestedPath $JavaSdkDirectory

    $previousJavaHome = $env:JAVA_HOME
    $previousPath = $env:PATH
    try {
        $env:JAVA_HOME = $resolvedJavaHome
        $javaBin = Join-Path $resolvedJavaHome 'bin'
        $env:PATH = if ([string]::IsNullOrEmpty($previousPath)) {
            $javaBin
        }
        else {
            $javaBin + [System.IO.Path]::PathSeparator + $previousPath
        }

        $applicationId = Invoke-GeoraePlanApkAnalyzerSingleValue `
            -ApkAnalyzerPath $resolvedAnalyzer `
            -Command 'application-id' `
            -ApkPath $resolvedApkPath `
            -SourceName $SourceName
        if ($applicationId -notmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') {
            throw "$SourceName APK applicationId is not one valid package identifier."
        }

        $versionName = Invoke-GeoraePlanApkAnalyzerSingleValue `
            -ApkAnalyzerPath $resolvedAnalyzer `
            -Command 'version-name' `
            -ApkPath $resolvedApkPath `
            -SourceName $SourceName
        if ([string]::IsNullOrWhiteSpace($versionName)) {
            throw "$SourceName APK versionName must be non-empty."
        }

        $versionCodeText = Invoke-GeoraePlanApkAnalyzerSingleValue `
            -ApkAnalyzerPath $resolvedAnalyzer `
            -Command 'version-code' `
            -ApkPath $resolvedApkPath `
            -SourceName $SourceName
        [long]$versionCode = 0
        if ($versionCodeText -notmatch '^\d+$' -or
            -not [long]::TryParse($versionCodeText, [ref]$versionCode) -or
            $versionCode -le 0) {
            throw "$SourceName APK versionCode must be a positive integer."
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:PATH = $previousPath
    }

    $apkFileAfter = Get-Item -LiteralPath $resolvedApkPath
    $hashAfter = (
        Get-FileHash -LiteralPath $resolvedApkPath -Algorithm SHA256
    ).Hash
    if ($apkFileAfter.Length -ne $apkFileBefore.Length -or
        -not [string]::Equals(
            $hashAfter,
            $hashBefore,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$SourceName APK changed during metadata inspection."
    }

    return [pscustomobject]@{
        ApplicationId = $applicationId
        VersionName = $versionName
        VersionCode = $versionCode
        Sha256 = $hashAfter
        FileSize = [long]$apkFileAfter.Length
    }
}

function New-GeoraePlanAndroidApkSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$ApkAnalyzerPath,
        [string]$JavaSdkDirectory,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
        throw "$SourceName APK must be an existing leaf file: $ApkPath"
    }

    $snapshotRoot = Join-Path (
        [IO.Path]::GetTempPath()) (
        'georaeplan-android-apk-' + [Guid]::NewGuid().ToString('N'))
    $snapshotPath = Join-Path $snapshotRoot 'candidate.apk'
    $snapshotLease = $null
    $snapshotWriter = $null
    $sourceStream = $null
    try {
        New-Item -ItemType Directory -Path $snapshotRoot -ErrorAction Stop |
            Out-Null
        $snapshotWriter = [IO.File]::Open(
            $snapshotPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        $sourceStream = [IO.File]::Open(
            $ApkPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        $sourceStream.CopyTo($snapshotWriter)
        $snapshotWriter.Flush($true)
        $snapshotWriter.Position = 0
        $sourceStream.Dispose()
        $sourceStream = $null
        $createdLength = $snapshotWriter.Length
        $createdSha256 =
            Get-GeoraePlanAndroidApkLeaseSha256 -Lease $snapshotWriter
        $createdIdentity =
            Get-GeoraePlanAndroidApkLeaseFileIdentity `
                -Lease $snapshotWriter
        $snapshotWriter.Dispose()
        $snapshotWriter = $null
        $snapshotLease = [IO.File]::Open(
            $snapshotPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read)
        if (
            $snapshotLease.Length -ne $createdLength -or
            -not [string]::Equals(
                (Get-GeoraePlanAndroidApkLeaseSha256 -Lease $snapshotLease),
                $createdSha256,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(
                (Get-GeoraePlanAndroidApkLeaseFileIdentity -Lease $snapshotLease),
                $createdIdentity,
                [StringComparison]::Ordinal)
        ) {
            throw "$SourceName APK snapshot changed while acquiring its lease."
        }
        $metadata = Get-GeoraePlanAndroidApkMetadata `
            -ApkPath $snapshotPath `
            -ProjectRoot $ProjectRoot `
            -ApkAnalyzerPath $ApkAnalyzerPath `
            -JavaSdkDirectory $JavaSdkDirectory `
            -SourceName $SourceName

        $snapshot = [pscustomobject]@{
            ApplicationId = $metadata.ApplicationId
            VersionName = $metadata.VersionName
            VersionCode = [long]$metadata.VersionCode
            Sha256 = $metadata.Sha256
            FileSize = [long]$metadata.FileSize
            SnapshotPath = $snapshotPath
            SnapshotRoot = $snapshotRoot
            Lease = $snapshotLease
            FileIdentity =
                Get-GeoraePlanAndroidApkLeaseFileIdentity `
                    -Lease $snapshotLease
        }
        Assert-GeoraePlanAndroidApkSnapshot `
            -Snapshot $snapshot `
            -SourceName $SourceName
        return $snapshot
    }
    catch {
        $snapshotCreationError = $_.Exception
        $cleanupErrors = @()
        if ($null -ne $sourceStream) {
            try {
                $sourceStream.Dispose()
            }
            catch {
                $cleanupErrors += $_.Exception
            }
        }
        if ($null -ne $snapshotWriter) {
            try {
                $snapshotWriter.Dispose()
            }
            catch {
                $cleanupErrors += $_.Exception
            }
        }
        try {
            Remove-GeoraePlanAndroidApkSnapshot -Snapshot ([pscustomobject]@{
                SnapshotRoot = $snapshotRoot
                SnapshotPath = $snapshotPath
                Lease = $snapshotLease
            })
        }
        catch {
            $cleanupErrors += $_.Exception
        }
        if ($cleanupErrors.Count -gt 0) {
            $allErrors = @($snapshotCreationError) + $cleanupErrors
            throw [AggregateException]::new(
                "$SourceName APK snapshot creation and cleanup both failed.",
                [Exception[]]$allErrors)
        }
        throw $snapshotCreationError
    }
}

function Get-GeoraePlanAndroidApkLeaseSha256 {
    param([Parameter(Mandatory = $true)][IO.FileStream]$Lease)

    $originalPosition = $Lease.Position
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $Lease.Position = 0
        return (
            [BitConverter]::ToString(
                $sha256.ComputeHash($Lease))).Replace('-', '')
    }
    finally {
        $Lease.Position = $originalPosition
        $sha256.Dispose()
    }
}

function Initialize-GeoraePlanAndroidApkSnapshotNativeMethods {
    if ($null -ne ('GeoraePlan.AndroidApkSnapshot.NativeMethods' -as [type])) {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.AndroidApkSnapshot
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation information);

        public static string GetIdentity(SafeFileHandle file)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(file, out information))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return String.Format(
                "{0:X8}:{1:X8}{2:X8}",
                information.VolumeSerialNumber,
                information.FileIndexHigh,
                information.FileIndexLow);
        }
    }
}
'@
}

function Get-GeoraePlanAndroidApkLeaseFileIdentity {
    param([Parameter(Mandatory = $true)][IO.FileStream]$Lease)

    Initialize-GeoraePlanAndroidApkSnapshotNativeMethods
    return [GeoraePlan.AndroidApkSnapshot.NativeMethods]::GetIdentity(
        $Lease.SafeFileHandle)
}

function Test-GeoraePlanAndroidApkSnapshotOwnership {
    param([Parameter(Mandatory = $true)][object]$Snapshot)

    if ($null -eq $Snapshot) {
        return $false
    }
    $rootProperty = $Snapshot.PSObject.Properties['SnapshotRoot']
    $pathProperty = $Snapshot.PSObject.Properties['SnapshotPath']
    if ($null -eq $rootProperty -or $null -eq $pathProperty) {
        return $false
    }
    $snapshotRootValue = [string]$rootProperty.Value
    $snapshotPathValue = [string]$pathProperty.Value
    if (
        [string]::IsNullOrWhiteSpace($snapshotRootValue) -or
        [string]::IsNullOrWhiteSpace($snapshotPathValue)
    ) {
        return $false
    }

    try {
        $snapshotRoot = [IO.Path]::GetFullPath($snapshotRootValue)
        $snapshotPath = [IO.Path]::GetFullPath($snapshotPathValue)
        $tempRoot = [IO.Path]::GetFullPath(
            [IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $snapshotParent = [IO.Path]::GetFullPath(
            (Split-Path -Parent $snapshotRoot)).TrimEnd('\', '/')
    }
    catch {
        return $false
    }
    $snapshotName = Split-Path -Leaf $snapshotRoot
    if (
        -not [string]::Equals(
            $snapshotParent,
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        $snapshotName -notmatch
            '^georaeplan-android-apk-[0-9a-fA-F]{32}$' -or
        -not [string]::Equals(
            $snapshotPath,
            (Join-Path $snapshotRoot 'candidate.apk'),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        return $false
    }

    if (Test-Path -LiteralPath $snapshotRoot) {
        $rootItem = Get-Item -LiteralPath $snapshotRoot -Force
        if (
            -not $rootItem.PSIsContainer -or
            ($rootItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            return $false
        }
    }
    if (Test-Path -LiteralPath $snapshotPath) {
        $pathItem = Get-Item -LiteralPath $snapshotPath -Force
        if (
            $pathItem.PSIsContainer -or
            ($pathItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        ) {
            return $false
        }
    }

    return $true
}

function Assert-GeoraePlanAndroidApkSnapshot {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if (-not (Test-GeoraePlanAndroidApkSnapshotOwnership -Snapshot $Snapshot)) {
        throw "$SourceName APK snapshot ownership is invalid."
    }
    $lease = $Snapshot.Lease
    if (
        $null -eq $lease -or
        -not $lease.CanRead -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath($lease.Name),
            [IO.Path]::GetFullPath([string]$Snapshot.SnapshotPath),
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$SourceName APK snapshot lease is invalid."
    }

    $pathFile = Get-Item -LiteralPath $Snapshot.SnapshotPath
    $pathLease = [IO.File]::Open(
        $Snapshot.SnapshotPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        $leaseIdentity =
            Get-GeoraePlanAndroidApkLeaseFileIdentity -Lease $lease
        $pathIdentity =
            Get-GeoraePlanAndroidApkLeaseFileIdentity -Lease $pathLease
    }
    finally {
        $pathLease.Dispose()
    }
    $pathHash = (
        Get-FileHash `
            -LiteralPath $Snapshot.SnapshotPath `
            -Algorithm SHA256).Hash
    $leaseHash = Get-GeoraePlanAndroidApkLeaseSha256 -Lease $lease
    if (
        $lease.Length -ne [long]$Snapshot.FileSize -or
        $pathFile.Length -ne [long]$Snapshot.FileSize -or
        -not [string]::Equals(
            $leaseIdentity,
            [string]$Snapshot.FileIdentity,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $pathIdentity,
            [string]$Snapshot.FileIdentity,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            $leaseHash,
            [string]$Snapshot.Sha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $pathHash,
            [string]$Snapshot.Sha256,
            [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "$SourceName APK snapshot identity changed."
    }
}

function Remove-GeoraePlanAndroidApkSnapshot {
    param([object]$Snapshot)

    if ($null -eq $Snapshot) {
        return
    }
    $leaseProperty = $Snapshot.PSObject.Properties['Lease']
    $lease = if ($null -ne $leaseProperty) {
        $leaseProperty.Value
    }
    else {
        $null
    }
    if ($lease -is [IDisposable]) {
        $lease.Dispose()
    }

    $rootProperty = $Snapshot.PSObject.Properties['SnapshotRoot']
    $pathProperty = $Snapshot.PSObject.Properties['SnapshotPath']
    if ($null -eq $rootProperty -or $null -eq $pathProperty) {
        return
    }
    if (
        [string]::IsNullOrWhiteSpace([string]$rootProperty.Value) -or
        [string]::IsNullOrWhiteSpace([string]$pathProperty.Value)
    ) {
        return
    }
    if (-not (Test-GeoraePlanAndroidApkSnapshotOwnership -Snapshot $Snapshot)) {
        throw 'Android APK snapshot cleanup ownership is invalid.'
    }

    $snapshotRoot = [IO.Path]::GetFullPath([string]$rootProperty.Value)
    $snapshotPath = [IO.Path]::GetFullPath([string]$pathProperty.Value)
    if (-not [IO.Directory]::Exists($snapshotRoot)) {
        if ([IO.File]::Exists($snapshotRoot)) {
            throw 'Android APK snapshot cleanup root is not a directory.'
        }
        return
    }

    $entries = @(
        [IO.Directory]::EnumerateFileSystemEntries(
            $snapshotRoot,
            '*',
            [IO.SearchOption]::TopDirectoryOnly))
    foreach ($entry in $entries) {
        $entryPath = [IO.Path]::GetFullPath([string]$entry)
        if (
            -not [string]::Equals(
                $entryPath,
                $snapshotPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw (
                'Android APK snapshot cleanup found an unexpected direct child: ' +
                $entryPath)
        }
    }

    if ($entries.Count -gt 1) {
        throw 'Android APK snapshot cleanup found duplicate candidate entries.'
    }
    if ($entries.Count -eq 1) {
        $candidateItem = Get-Item `
            -LiteralPath $snapshotPath `
            -Force `
            -ErrorAction Stop
        if (
            $candidateItem.PSIsContainer -or
            ($candidateItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            -not [string]::Equals(
                [IO.Path]::GetFullPath($candidateItem.FullName),
                $snapshotPath,
                [StringComparison]::OrdinalIgnoreCase)
        ) {
            throw 'Android APK snapshot cleanup candidate ownership is invalid.'
        }

        [IO.File]::Delete($snapshotPath)
        if (
            [IO.File]::Exists($snapshotPath) -or
            [IO.Directory]::Exists($snapshotPath)
        ) {
            throw 'Android APK snapshot cleanup did not delete candidate.apk.'
        }
    }

    [IO.Directory]::Delete($snapshotRoot, $false)
    if (
        [IO.Directory]::Exists($snapshotRoot) -or
        [IO.File]::Exists($snapshotRoot)
    ) {
        throw 'Android APK snapshot cleanup did not delete its empty root.'
    }
}

function Assert-GeoraePlanAndroidApkMetadata {
    param(
        [Parameter(Mandatory = $true)][object]$Metadata,
        [Parameter(Mandatory = $true)][string]$ExpectedApplicationId,
        [Parameter(Mandatory = $true)][string]$ExpectedVersionName,
        [Parameter(Mandatory = $true)][long]$ExpectedVersionCode,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    if ([string]::IsNullOrWhiteSpace($SourceName)) {
        throw 'Android APK metadata source name is required.'
    }
    if (-not [string]::Equals(
            [string]$Metadata.ApplicationId,
            $ExpectedApplicationId,
            [StringComparison]::Ordinal)) {
        throw (
            "$SourceName APK applicationId mismatch. " +
            "expected=$ExpectedApplicationId actual=$($Metadata.ApplicationId)")
    }
    if (-not [string]::Equals(
            [string]$Metadata.VersionName,
            $ExpectedVersionName,
            [StringComparison]::Ordinal)) {
        throw (
            "$SourceName APK versionName mismatch. " +
            "expected=$ExpectedVersionName actual=$($Metadata.VersionName)")
    }

    [long]$actualVersionCode = 0
    if (-not [long]::TryParse(
            [string]$Metadata.VersionCode,
            [ref]$actualVersionCode) -or
        $actualVersionCode -ne $ExpectedVersionCode) {
        throw (
            "$SourceName APK versionCode mismatch. " +
            "expected=$ExpectedVersionCode actual=$($Metadata.VersionCode)")
    }
}
