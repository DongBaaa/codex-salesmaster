[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SourceFolder,
    [string]$OutputRoot,
    [string]$PackageName,
    [string]$AppDisplayName,
    [string]$Manufacturer,
    [string]$LaunchExeName,
    [string]$Version,
    [string]$WixToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DeploymentRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Deployment root not found under project root.'
    }

    return $candidate
}

function Get-DefaultClientSourceFolder {
    param([Parameter(Mandatory = $true)][string]$DeploymentRoot)

    $candidates = Get-ChildItem -LiteralPath $DeploymentRoot -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'appsettings.json')) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName '거래플랜.exe'))
        } |
        Sort-Object FullName

    $preferred = $candidates |
        Where-Object { $_.Name -eq '거래플랜' } |
        Select-Object -First 1

    if ($null -eq $preferred) {
        $preferred = $candidates | Select-Object -First 1
    }

    if ($null -eq $preferred) {
        throw 'Desktop client deployment source folder not found.'
    }

    return $preferred.FullName
}

function Get-DefaultOutputRoot {
    param([Parameter(Mandatory = $true)][string]$DeploymentRoot)

    $candidate = Get-ChildItem -LiteralPath $DeploymentRoot -Directory |
        Where-Object { $_.Name -eq '설치패키지' } |
        Select-Object -First 1 -ExpandProperty FullName

    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        return $candidate
    }

    return (Join-Path $DeploymentRoot '설치패키지')
}

function Get-ProjectVersion {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $projectPath = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        return '1.0.0'
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        return '1.0.0'
    }

    return [string]$versionNode
}

function Ensure-WixTool {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$PreferredToolPath
    )

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($PreferredToolPath)) {
        if ($PreferredToolPath.EndsWith('.exe', [System.StringComparison]::OrdinalIgnoreCase)) {
            $candidates += $PreferredToolPath
        }
        else {
            $candidates += (Join-Path $PreferredToolPath 'wix.exe')
        }
    }

    $candidates += @(
        (Join-Path $ProjectRoot '.tooling\wix\wix.exe'),
        'wix.exe'
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $resolved = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($null -ne $resolved) {
            return $resolved.Source
        }
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $toolPath = Join-Path $ProjectRoot '.tooling\wix'
    New-Item -ItemType Directory -Force -Path $toolPath | Out-Null
    & dotnet tool install wix --tool-path $toolPath --version 6.0.2 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to install WiX Toolset.'
    }

    return (Join-Path $toolPath 'wix.exe')
}

function Convert-ToXmlAttribute {
    param([AllowEmptyString()][string]$Value)
    if ($null -eq $Value) { return '' }
    return [System.Security.SecurityElement]::Escape($Value)
}

function Get-RelativeWindowsPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($Root)
    if (-not $normalizedRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $normalizedRoot += [System.IO.Path]::DirectorySeparatorChar
    }

    $rootUri = [System.Uri]::new($normalizedRoot)
    $pathUri = [System.Uri]::new([System.IO.Path]::GetFullPath($Path))
    $relative = $rootUri.MakeRelativeUri($pathUri).ToString()
    return ([System.Uri]::UnescapeDataString($relative) -replace '/', '\')
}

function New-GeneratedWxsContent {
    param([Parameter(Mandatory = $true)][string]$SourceRoot)

    $directories = Get-ChildItem -LiteralPath $SourceRoot -Directory -Recurse | Sort-Object FullName
    $dirMap = @{}
    $index = 1
    foreach ($directory in $directories) {
        $dirMap[$directory.FullName] = ('DIR{0:D4}' -f $index)
        $index++
    }

    $script:__GeoraePlanComponentIds = New-Object System.Collections.Generic.List[string]
    $script:__GeoraePlanComponentIndex = 1
    $script:__GeoraePlanFileIndex = 1
    $script:__GeoraePlanDirMap = $dirMap

    function RenderDirectoryChildren {
        param(
            [string]$DirectoryPath,
            [int]$Depth
        )

        $indent = ('  ' * $Depth)
        $lines = New-Object System.Collections.Generic.List[string]

        $files = Get-ChildItem -LiteralPath $DirectoryPath -File | Sort-Object Name
        foreach ($file in $files) {
            $componentId = 'CMP{0:D4}' -f $script:__GeoraePlanComponentIndex
            $fileId = 'FIL{0:D4}' -f $script:__GeoraePlanFileIndex
            $script:__GeoraePlanComponentIndex++
            $script:__GeoraePlanFileIndex++
            $script:__GeoraePlanComponentIds.Add($componentId)

            $sourcePath = '$(var.SourceDir)\' + (Get-RelativeWindowsPath -Root $SourceRoot -Path $file.FullName)
            $fileName = Convert-ToXmlAttribute $file.Name
            $sourceAttr = Convert-ToXmlAttribute $sourcePath

            $lines.Add("$indent<Component Id=`"$componentId`" Guid=`"*`">")
            $lines.Add("$indent  <File Id=`"$fileId`" KeyPath=`"yes`" Name=`"$fileName`" Source=`"$sourceAttr`" />")
            $lines.Add("$indent</Component>")
        }

        $subDirectories = Get-ChildItem -LiteralPath $DirectoryPath -Directory | Sort-Object Name
        foreach ($subDirectory in $subDirectories) {
            $dirId = $script:__GeoraePlanDirMap[$subDirectory.FullName]
            $dirName = Convert-ToXmlAttribute $subDirectory.Name
            $lines.Add("$indent<Directory Id=`"$dirId`" Name=`"$dirName`">")
            foreach ($line in (RenderDirectoryChildren -DirectoryPath $subDirectory.FullName -Depth ($Depth + 1))) {
                $lines.Add($line)
            }
            $lines.Add("$indent</Directory>")
        }

        return $lines
    }

    $directoryLines = New-Object System.Collections.Generic.List[string]
    foreach ($line in (RenderDirectoryChildren -DirectoryPath $SourceRoot -Depth 2)) {
        $directoryLines.Add($line)
    }

    $componentGroupLines = New-Object System.Collections.Generic.List[string]
    foreach ($componentId in $script:__GeoraePlanComponentIds) {
        $componentGroupLines.Add("      <ComponentRef Id=`"$componentId`" />")
    }

    return @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
$($directoryLines -join [Environment]::NewLine)
    </DirectoryRef>
  </Fragment>
  <Fragment>
    <ComponentGroup Id="AppFiles">
$($componentGroupLines -join [Environment]::NewLine)
    </ComponentGroup>
  </Fragment>
</Wix>
"@
}

function New-ProductWxsContent {
    param(
        [Parameter(Mandatory = $true)][string]$AppDisplayName,
        [Parameter(Mandatory = $true)][string]$Manufacturer,
        [Parameter(Mandatory = $true)][string]$LaunchExeName,
        [Parameter(Mandatory = $true)][string]$UpgradeCode
    )

    $productName = Convert-ToXmlAttribute $AppDisplayName
    $manufacturerEscaped = Convert-ToXmlAttribute $Manufacturer
    $launchExeEscaped = Convert-ToXmlAttribute $LaunchExeName
    $upgradeCodeEscaped = Convert-ToXmlAttribute $UpgradeCode
    $installFolderEscaped = Convert-ToXmlAttribute $AppDisplayName
    $downgradeMessage = Convert-ToXmlAttribute '이미 최신 버전의 거래플랜이 설치되어 있습니다.'

    return @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="$productName"
           Manufacturer="$manufacturerEscaped"
           Version="`$(var.ProductVersion)"
           UpgradeCode="$upgradeCodeEscaped"
           Language="1042"
           Scope="perUser"
           InstallerVersion="500"
           Compressed="yes"
           Codepage="65001">
    <SummaryInformation Description="$productName" Manufacturer="$manufacturerEscaped" />
    <MajorUpgrade DowngradeErrorMessage="$downgradeMessage" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />

    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="ProgramsFolder" Name="Programs">
        <Directory Id="INSTALLFOLDER" Name="$installFolderEscaped" />
      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ProgramMenuDir" Name="$installFolderEscaped" />
    </StandardDirectory>

    <Feature Id="MainFeature" Title="$productName" Level="1">
      <ComponentGroupRef Id="AppFiles" />
      <ComponentRef Id="ApplicationShortcutComponent" />
    </Feature>
  </Package>

  <Fragment>
    <Component Id="ApplicationShortcutComponent" Directory="INSTALLFOLDER" Guid="*">
      <Shortcut Id="ApplicationStartMenuShortcut"
                Directory="ProgramMenuDir"
                Name="$productName"
                Target="[INSTALLFOLDER]$launchExeEscaped"
                WorkingDirectory="INSTALLFOLDER"
                Advertise="no" />
      <Shortcut Id="ApplicationDesktopShortcut"
                Directory="DesktopFolder"
                Name="$productName"
                Target="[INSTALLFOLDER]$launchExeEscaped"
                WorkingDirectory="INSTALLFOLDER"
                Advertise="no" />
      <RemoveFolder Id="CleanProgramMenuDir" Directory="ProgramMenuDir" On="uninstall" />
      <RegistryValue Root="HKCU" Key="Software\$manufacturerEscaped\$installFolderEscaped" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
    </Component>
  </Fragment>
</Wix>
"@
}

function New-IExpressInstallCmd {
    param([Parameter(Mandatory = $true)][string]$MsiFileName)

    return @"
@echo off
setlocal
set MSI_PATH=%~dp0$MsiFileName
if /I "%~1"=="/quiet" (
  msiexec.exe /i "%MSI_PATH%" /qn /norestart
) else (
  msiexec.exe /i "%MSI_PATH%"
)
set EXITCODE=%ERRORLEVEL%
endlocal & exit /b %EXITCODE%
"@
}

function New-IExpressSedContent {
    param(
        [Parameter(Mandatory = $true)][string]$TargetExePath,
        [Parameter(Mandatory = $true)][string]$FriendlyName,
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string[]]$Files
    )

    $escapedTarget = $TargetExePath
    $escapedFriendly = 'GeoraePlan Installer'
    $escapedSource = $SourceDirectory

    $stringLines = New-Object System.Collections.Generic.List[string]
    $sourceEntryLines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Files.Length; $i++) {
        $stringLines.Add(("FILE{0}={1}" -f $i, $Files[$i]))
        $sourceEntryLines.Add(("%FILE{0}%=" -f $i))
    }

    return @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$escapedTarget
FriendlyName=$escapedFriendly
AppLaunched=cmd.exe /c install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=cmd.exe /c install.cmd /quiet
UserQuietInstCmd=cmd.exe /c install.cmd /quiet
SourceFiles=SourceFiles

[Strings]
$($stringLines -join [Environment]::NewLine)

[SourceFiles]
SourceFiles0=$escapedSource

[SourceFiles0]
$($sourceEntryLines -join [Environment]::NewLine)
"@
}

function Write-Sha256File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Algorithm
    )

    $hash = Get-FileHash -LiteralPath $Path -Algorithm $Algorithm
    ("{0} *{1}" -f $hash.Hash, (Split-Path -Leaf $Path)) | Set-Content -LiteralPath ($Path + '.sha256.txt') -Encoding ASCII
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
}

if ([string]::IsNullOrWhiteSpace($AppDisplayName)) {
    $AppDisplayName = '거래플랜'
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = '거래플랜-PC-설치패키지'
}

if ([string]::IsNullOrWhiteSpace($Manufacturer)) {
    $Manufacturer = '거래플랜'
}

if ([string]::IsNullOrWhiteSpace($LaunchExeName)) {
    $LaunchExeName = '거래플랜.exe'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion -ProjectRoot $ProjectRoot
}

$deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot
if ([string]::IsNullOrWhiteSpace($SourceFolder)) {
    $SourceFolder = Get-DefaultClientSourceFolder -DeploymentRoot $deploymentRoot
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Get-DefaultOutputRoot -DeploymentRoot $deploymentRoot
}

$SourceFolder = (Resolve-Path -LiteralPath $SourceFolder).Path
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path

$wixExe = Ensure-WixTool -ProjectRoot $ProjectRoot -PreferredToolPath $WixToolPath

$stagingRoot = Join-Path ([System.IO.Path]::GetPathRoot($ProjectRoot)) 'GeoraePlanInstallerBuild'
Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$productWxsPath = Join-Path $stagingRoot 'Product.wxs'
$generatedWxsPath = Join-Path $stagingRoot 'GeneratedFiles.wxs'
$productWxs = New-ProductWxsContent -AppDisplayName $AppDisplayName -Manufacturer $Manufacturer -LaunchExeName $LaunchExeName -UpgradeCode '{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}'
$generatedWxs = New-GeneratedWxsContent -SourceRoot $SourceFolder
$productWxs | Set-Content -LiteralPath $productWxsPath -Encoding UTF8
$generatedWxs | Set-Content -LiteralPath $generatedWxsPath -Encoding UTF8

$tempMsiPath = Join-Path $stagingRoot 'georaeplan-installer.msi'
$wixIntermediateRoot = Join-Path $stagingRoot 'wix-intermediate'
New-Item -ItemType Directory -Force -Path $wixIntermediateRoot | Out-Null
if (Test-Path -LiteralPath $tempMsiPath) { Remove-Item -LiteralPath $tempMsiPath -Force }

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $wixIntermediateRoot
$env:TMP = $wixIntermediateRoot
try {
    & $wixExe build $productWxsPath $generatedWxsPath -arch x64 -intermediatefolder $wixIntermediateRoot -d SourceDir=$SourceFolder -d ProductVersion=$Version -o $tempMsiPath
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempMsiPath)) {
    throw 'Failed to build MSI installer.'
}

$versionedMsiPath = Join-Path $OutputRoot ("{0}-v{1}.msi" -f $PackageName, $Version)
$stableMsiPath = Join-Path $OutputRoot ($PackageName + '.msi')
Copy-Item -LiteralPath $tempMsiPath -Destination $versionedMsiPath -Force
Copy-Item -LiteralPath $tempMsiPath -Destination $stableMsiPath -Force
Write-Sha256File -Path $versionedMsiPath -Algorithm 'SHA256'
Write-Sha256File -Path $stableMsiPath -Algorithm 'SHA256'

$iexpressRoot = Join-Path $stagingRoot 'iexpress'
New-Item -ItemType Directory -Force -Path $iexpressRoot | Out-Null
$iexpressMsiName = 'package.msi'
$iexpressCmdName = 'install.cmd'
Copy-Item -LiteralPath $tempMsiPath -Destination (Join-Path $iexpressRoot $iexpressMsiName) -Force
(New-IExpressInstallCmd -MsiFileName $iexpressMsiName) | Set-Content -LiteralPath (Join-Path $iexpressRoot $iexpressCmdName) -Encoding ASCII
$tempExePath = Join-Path $stagingRoot 'georaeplan-installer.exe'
$sedPath = Join-Path $iexpressRoot 'package.sed'
(New-IExpressSedContent -TargetExePath $tempExePath -FriendlyName $PackageName -SourceDirectory $iexpressRoot -Files @($iexpressCmdName, $iexpressMsiName)) | Set-Content -LiteralPath $sedPath -Encoding ASCII

& iexpress.exe /N /Q $sedPath | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempExePath)) {
    throw 'Failed to build EXE installer.'
}

$versionedExePath = Join-Path $OutputRoot ("{0}-v{1}.exe" -f $PackageName, $Version)
$stableExePath = Join-Path $OutputRoot ($PackageName + '.exe')
Copy-Item -LiteralPath $tempExePath -Destination $versionedExePath -Force
Copy-Item -LiteralPath $tempExePath -Destination $stableExePath -Force
Write-Sha256File -Path $versionedExePath -Algorithm 'SHA256'
Write-Sha256File -Path $stableExePath -Algorithm 'SHA256'

Write-Host "installer_msi=$stableMsiPath"
Write-Host "installer_exe=$stableExePath"
Write-Host "installer_msi_versioned=$versionedMsiPath"
Write-Host "installer_exe_versioned=$versionedExePath"

