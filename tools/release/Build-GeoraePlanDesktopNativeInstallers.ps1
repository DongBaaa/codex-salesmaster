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
    [string]$WixToolPath,
    [int]$KeepVersionedInstallerCount = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-DotnetCommand {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        catch {
            continue
        }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "Working dotnet executable not found. projectRoot=$ProjectRoot"
}

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
            ((Test-Path -LiteralPath (Join-Path $_.FullName '거래플랜.exe')) -or
             (Test-Path -LiteralPath (Join-Path $_.FullName '거래플랜.Desktop.App.exe')))
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

    return $DeploymentRoot
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
        [string]$PreferredToolPath,
        [Parameter(Mandatory = $true)][string]$DotnetExe
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
        (Join-Path $ProjectRoot '.wix\bin\wix.exe'),
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
    & $DotnetExe tool install wix --tool-path $toolPath --version 6.0.2 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to install WiX Toolset.'
    }

    return (Join-Path $toolPath 'wix.exe')
}

function Ensure-WixExtensions {
    param([Parameter(Mandatory = $true)][string]$WixExePath)

    foreach ($extension in @('WixToolset.UI.wixext/6.0.2', 'WixToolset.Util.wixext/6.0.2')) {
        & $WixExePath extension add -g $extension | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to add WiX extension: $extension"
        }
    }
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

function Get-AppIconsRoot {
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $iconsRoot = Join-Path $ProjectRoot 'AppIcons'
    if (-not (Test-Path -LiteralPath $iconsRoot)) {
        throw "AppIcons folder not found: $iconsRoot"
    }

    return (Resolve-Path -LiteralPath $iconsRoot).Path
}

function Get-WindowsIconAsset {
    param([Parameter(Mandatory = $true)][string]$AppIconsRoot)

    $preferred = Join-Path $AppIconsRoot 'tradeplan-windows.ico'
    if (Test-Path -LiteralPath $preferred) {
        return (Resolve-Path -LiteralPath $preferred).Path
    }

    $firstIco = Get-ChildItem -LiteralPath $AppIconsRoot -Recurse -File -Filter '*.ico' | Select-Object -First 1
    if ($null -ne $firstIco) {
        return $firstIco.FullName
    }

    throw "Windows icon asset (.ico) not found under $AppIconsRoot"
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & robocopy $Source $Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination"
    }
}

function Publish-DesktopApplication {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $desktopProject)) {
        throw "Desktop project not found: $desktopProject"
    }

    Remove-Item -LiteralPath $PublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

    & $DotnetExe publish $desktopProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $PublishRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to publish desktop application for installer packaging.'
    }

    return $PublishRoot
}

function Prepare-InstallerSourceFolder {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$OriginalSourceFolder,
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$LaunchExeName,
        [Parameter(Mandatory = $true)][string]$AppDisplayName,
        [Parameter(Mandatory = $true)][string]$ShortcutIconPath,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $installerSourceRoot = Join-Path $StagingRoot 'installer-source'
    Invoke-RobocopyMirror -Source $OriginalSourceFolder -Destination $installerSourceRoot

    $shortcutIconFileName = Split-Path -Leaf $ShortcutIconPath
    Copy-Item -LiteralPath $ShortcutIconPath -Destination (Join-Path $installerSourceRoot $shortcutIconFileName) -Force

    $publishRoot = Publish-DesktopApplication -ProjectRoot $ProjectRoot -PublishRoot (Join-Path $StagingRoot 'desktop-publish') -DotnetExe $DotnetExe

    $publishedExeCandidates = @(
        (Join-Path $publishRoot '거래플랜.Desktop.App.exe'),
        (Join-Path $publishRoot '거래플랜.exe')
    )
    $publishedExe = $publishedExeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($publishedExe)) {
        throw "Published desktop executable not found under $publishRoot"
    }

    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $installerSourceRoot '거래플랜.Desktop.App.exe') -Force
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $installerSourceRoot $LaunchExeName) -Force

    $publishedPdbCandidates = @(
        (Join-Path $publishRoot '거래플랜.Desktop.App.pdb'),
        (Join-Path $publishRoot '거래플랜.pdb')
    )
    $publishedPdb = $publishedPdbCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not [string]::IsNullOrWhiteSpace($publishedPdb)) {
        Copy-Item -LiteralPath $publishedPdb -Destination (Join-Path $installerSourceRoot '거래플랜.Desktop.App.pdb') -Force
    }

    return [pscustomobject]@{
        SourceRoot = $installerSourceRoot
        ShortcutIconFileName = $shortcutIconFileName
    }
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

    $script:__TradePlanComponentIds = New-Object System.Collections.Generic.List[string]
    $script:__TradePlanComponentIndex = 1
    $script:__TradePlanFileIndex = 1
    $script:__TradePlanDirMap = $dirMap

    function RenderDirectoryChildren {
        param(
            [Parameter(Mandatory = $true)][string]$DirectoryPath,
            [Parameter(Mandatory = $true)][int]$Depth
        )

        $indent = ('  ' * $Depth)
        $lines = New-Object System.Collections.Generic.List[string]

        $files = Get-ChildItem -LiteralPath $DirectoryPath -File | Sort-Object Name
        foreach ($file in $files) {
            $componentId = 'CMP{0:D4}' -f $script:__TradePlanComponentIndex
            $fileId = switch -Regex ($file.Name) {
                '^CreateDesktopShortcut\.vbs$' { 'DesktopShortcutScriptFile'; break }
                default { 'FIL{0:D4}' -f $script:__TradePlanFileIndex }
            }
            $script:__TradePlanComponentIndex++
            if ($fileId -like 'FIL*') {
                $script:__TradePlanFileIndex++
            }
            $script:__TradePlanComponentIds.Add($componentId)

            $sourcePath = '$(var.SourceDir)\' + (Get-RelativeWindowsPath -Root $SourceRoot -Path $file.FullName)
            $fileName = Convert-ToXmlAttribute $file.Name
            $sourceAttr = Convert-ToXmlAttribute $sourcePath

            $lines.Add("$indent<Component Id=`"$componentId`" Guid=`"*`">")
            $lines.Add("$indent  <File Id=`"$fileId`" KeyPath=`"yes`" Name=`"$fileName`" Source=`"$sourceAttr`" />")
            $lines.Add("$indent</Component>")
        }

        $subDirectories = Get-ChildItem -LiteralPath $DirectoryPath -Directory | Sort-Object Name
        foreach ($subDirectory in $subDirectories) {
            $dirId = $script:__TradePlanDirMap[$subDirectory.FullName]
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
    foreach ($componentId in $script:__TradePlanComponentIds) {
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
    $manufacturerName = Convert-ToXmlAttribute $Manufacturer
    $launchExe = Convert-ToXmlAttribute $LaunchExeName
    $upgradeCodeValue = Convert-ToXmlAttribute $UpgradeCode
    $downgradeMessage = Convert-ToXmlAttribute '이미 최신 버전의 거래플랜이 설치되어 있습니다.'
    $uninstallShortcutName = Convert-ToXmlAttribute '거래플랜 제거'
    $registryManufacturer = Convert-ToXmlAttribute $Manufacturer
    $registryProduct = Convert-ToXmlAttribute $AppDisplayName

    return @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui"
     xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">
  <Package Name="$productName"
           Manufacturer="$manufacturerName"
           Version="`$(var.ProductVersion)"
           UpgradeCode="$upgradeCodeValue"
           Language="1042"
           Scope="perMachine"
           InstallerVersion="500"
           Compressed="yes"
           Codepage="65001">
    <SummaryInformation Description="$productName" Manufacturer="$manufacturerName" />
    <MajorUpgrade DowngradeErrorMessage="$downgradeMessage" />
    <MediaTemplate EmbedCab="yes" CompressionLevel="high" />

    <Icon Id="AppPackageIcon" SourceFile="`$(var.AppIconPath)" />
    <Property Id="ARPPRODUCTICON" Value="AppPackageIcon" />

    <SetProperty Id="ARPINSTALLLOCATION"
                 Value="[INSTALLFOLDER]"
                 Before="RegisterProduct"
                 Sequence="execute" />
    <SetProperty Id="GEORAEPLANLOCALAPPDATAROOT"
                 Value="[LocalAppDataFolder]$productName"
                 Before="CostInitialize"
                 Sequence="execute" />

    <StandardDirectory Id="ProgramFilesFolder">
      <Directory Id="INSTALLFOLDER" Name="tradeplan" />
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ProgramMenuDir" Name="$productName" />
    </StandardDirectory>

    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />

    <Feature Id="MainFeature" Title="$productName" Level="1">
      <ComponentGroupRef Id="AppFiles" />
      <ComponentRef Id="ApplicationStartMenuComponent" />
      <ComponentRef Id="ApplicationDesktopShortcutComponent" />
    </Feature>
  </Package>

  <Fragment>
    <Component Id="ApplicationStartMenuComponent" Directory="ProgramMenuDir" Guid="*">
      <Shortcut Id="ApplicationStartMenuShortcut"
                Directory="ProgramMenuDir"
                Name="$productName"
                Target="[INSTALLFOLDER]$launchExe"
                WorkingDirectory="INSTALLFOLDER"
                Icon="AppPackageIcon"
                IconIndex="0"
                Advertise="no" />
      <Shortcut Id="ApplicationUninstallShortcut"
                Directory="ProgramMenuDir"
                Name="$uninstallShortcutName"
                Target="[SystemFolder]msiexec.exe"
                Arguments="/x [ProductCode]"
                WorkingDirectory="INSTALLFOLDER"
                Icon="AppPackageIcon"
                IconIndex="0"
                Advertise="no" />
      <RemoveFolder Id="CleanProgramMenuDir" Directory="ProgramMenuDir" On="uninstall" />
      <util:RemoveFolderEx Property="GEORAEPLANLOCALAPPDATAROOT" On="uninstall" />
      <RegistryValue Root="HKLM" Key="Software\$registryManufacturer\$registryProduct" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
    </Component>
  </Fragment>

  <Fragment>
    <Component Id="ApplicationDesktopShortcutComponent" Directory="DesktopFolder" Guid="*">
      <Shortcut Id="ApplicationDesktopShortcut"
                Directory="DesktopFolder"
                Name="$productName"
                Target="[INSTALLFOLDER]$launchExe"
                WorkingDirectory="INSTALLFOLDER"
                Icon="AppPackageIcon"
                IconIndex="0"
                Advertise="no" />
      <RegistryValue Root="HKLM" Key="Software\$registryManufacturer\$registryProduct" Name="DesktopShortcutInstalled" Type="integer" Value="1" KeyPath="yes" />
    </Component>
  </Fragment>
</Wix>
"@
}

function New-BootstrapperProjectFiles {
    param(
        [Parameter(Mandatory = $true)][string]$BootstrapperRoot,
        [Parameter(Mandatory = $true)][string]$MsiPath,
        [Parameter(Mandatory = $true)][string]$IconPath,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    New-Item -ItemType Directory -Force -Path $BootstrapperRoot | Out-Null
    $projectPath = Join-Path $BootstrapperRoot 'TradePlan.Installer.csproj'
    $programPath = Join-Path $BootstrapperRoot 'Program.cs'
    $manifestPath = Join-Path $BootstrapperRoot 'app.manifest'
    $localMsiPath = Join-Path $BootstrapperRoot 'package.msi'
    $localIconPath = Join-Path $BootstrapperRoot 'tradeplan-windows.ico'

    Copy-Item -LiteralPath $MsiPath -Destination $localMsiPath -Force
    Copy-Item -LiteralPath $IconPath -Destination $localIconPath -Force

    $csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RuntimeIdentifier>win-x86</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishTrimmed>false</PublishTrimmed>
    <AssemblyName>TradePlan.Installer</AssemblyName>
    <ApplicationIcon>tradeplan-windows.ico</ApplicationIcon>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Version>$Version</Version>
    <FileVersion>$Version.0</FileVersion>
    <Product>$AppDisplayName Installer</Product>
    <Company>TradePlan</Company>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="package.msi" LogicalName="package.msi" />
  </ItemGroup>
</Project>
"@

    $manifestContent = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="TradePlan.Installer.app" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
      <supportedOS Id="{4f476546-937f-47f9-a4f2-61c5f2fbe5a5}" />
      <supportedOS Id="{1f676c76-80e1-4239-95bb-83d0f6d0da78}" />
      <supportedOS Id="{35138b9a-5d96-4fbd-8e2d-a2440225f93a}" />
      <supportedOS Id="{e2011457-1546-43c5-a5fe-008deee3d3f0}" />
    </application>
  </compatibility>
</assembly>
'@

    $programContent = @'
using System.Diagnostics;
using System.Reflection;

internal static class Program
{
    private const string InstallFolderName = "tradeplan";
    private const string AppExeName = "거래플랜.exe";
    private const string AppShortcutName = "거래플랜";

    [STAThread]
    private static int Main(string[] args)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "TradePlanInstaller");
        Directory.CreateDirectory(tempRoot);
        var packagePath = Path.Combine(tempRoot, "tradeplan-installer.msi");

        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("package.msi"))
        {
            if (stream is null)
            {
                return 2;
            }

            using var fileStream = File.Create(packagePath);
            stream.CopyTo(fileStream);
        }

        var normalizedArgs = NormalizeArgs(args).ToArray();
        var startInfo = new ProcessStartInfo("msiexec.exe")
        {
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(packagePath);

        foreach (var arg in normalizedArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 3;
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
        {
            TryEnsureMachineShortcuts(ResolveInstallRoot(normalizedArgs));
        }

        return process.ExitCode;
    }

    private static string[] NormalizeArgs(IEnumerable<string> args)
    {
        var normalized = new List<string>();
        foreach (var arg in args)
        {
            if (string.Equals(arg, "/Q", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase))
            {
                normalized.Add("/qn");
                normalized.Add("/norestart");
                continue;
            }

            normalized.Add(arg);
        }

        return normalized.ToArray();
    }

    private static string ResolveInstallRoot(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("INSTALLFOLDER=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring("INSTALLFOLDER=".Length).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        return Path.Combine(programFilesX86, InstallFolderName);
    }

    private static void TryEnsureMachineShortcuts(string installRoot)
    {
        try
        {
            var exePath = Path.Combine(installRoot, AppExeName);
            if (!File.Exists(exePath))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var commonDesktopShortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                AppShortcutName + ".lnk");
            CreateShortcut(shell, commonDesktopShortcutPath, exePath, installRoot);

            var commonProgramsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                AppShortcutName);
            Directory.CreateDirectory(commonProgramsDir);
            var startMenuShortcutPath = Path.Combine(commonProgramsDir, AppShortcutName + ".lnk");
            CreateShortcut(shell, startMenuShortcutPath, exePath, installRoot);
        }
        catch
        {
            // 설치 자체는 성공했으므로 바로가기 생성 실패는 무시
        }
    }

    private static void CreateShortcut(dynamic shell, string shortcutPath, string exePath, string workingDirectory)
    {
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = exePath;
        shortcut.Save();
    }
}
'@

    $csprojContent | Set-Content -LiteralPath $projectPath -Encoding UTF8
    $programContent | Set-Content -LiteralPath $programPath -Encoding UTF8
    $manifestContent | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    return $projectPath
}

function Build-BootstrapperExe {
    param(
        [Parameter(Mandatory = $true)][string]$BootstrapperProjectPath,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    Remove-Item -LiteralPath $PublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

    & $DotnetExe publish $BootstrapperProjectPath -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -o $PublishRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to build installer bootstrapper executable.'
    }

    $bootstrapperExe = Join-Path $PublishRoot 'TradePlan.Installer.exe'
    if (-not (Test-Path -LiteralPath $bootstrapperExe)) {
        throw "Bootstrapper executable not found: $bootstrapperExe"
    }

    return $bootstrapperExe
}

function Write-Sha256File {
    param([Parameter(Mandatory = $true)][string]$Path)

    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
    ("{0} *{1}" -f $hash.Hash, (Split-Path -Leaf $Path)) | Set-Content -LiteralPath ($Path + '.sha256.txt') -Encoding UTF8
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
    $Manufacturer = 'TradePlan'
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
$adminOutputRoot = Join-Path $OutputRoot '관리자용'
$archiveOutputRoot = Join-Path $adminOutputRoot '버전보관'
New-Item -ItemType Directory -Force -Path $adminOutputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $archiveOutputRoot | Out-Null

Get-ChildItem -LiteralPath $OutputRoot -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like ($PackageName + '.msi') -or
        $_.Name -like ($PackageName + '.msi.sha256.txt') -or
        $_.Name -like ($PackageName + '-v*.exe') -or
        $_.Name -like ($PackageName + '-v*.exe.sha256.txt') -or
        $_.Name -like ($PackageName + '-v*.msi') -or
        $_.Name -like ($PackageName + '-v*.msi.sha256.txt') -or
        $_.Name -like ($PackageName + '.zip') -or
        $_.Name -like ($PackageName + '.zip.sha256.txt')
    } |
    Remove-Item -Force -ErrorAction SilentlyContinue

$legacyPackageRoot = Join-Path $OutputRoot $PackageName
if (Test-Path -LiteralPath $legacyPackageRoot) {
    Remove-Item -LiteralPath $legacyPackageRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$wixExe = Ensure-WixTool -ProjectRoot $ProjectRoot -PreferredToolPath $WixToolPath -DotnetExe $dotnetExe
Ensure-WixExtensions -WixExePath $wixExe
$appIconsRoot = Get-AppIconsRoot -ProjectRoot $ProjectRoot
$shortcutIconPath = Get-WindowsIconAsset -AppIconsRoot $appIconsRoot

$stagingRoot = Join-Path ([System.IO.Path]::GetPathRoot($ProjectRoot)) 'GeoraePlanInstallerBuild'
Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$preparedSource = Prepare-InstallerSourceFolder -ProjectRoot $ProjectRoot -OriginalSourceFolder $SourceFolder -StagingRoot $stagingRoot -LaunchExeName $LaunchExeName -AppDisplayName $AppDisplayName -ShortcutIconPath $shortcutIconPath -DotnetExe $dotnetExe
$sourceForPackaging = $preparedSource.SourceRoot
$appIconForPackage = Join-Path $sourceForPackaging $preparedSource.ShortcutIconFileName

$productWxsPath = Join-Path $stagingRoot 'Product.wxs'
$generatedWxsPath = Join-Path $stagingRoot 'GeneratedFiles.wxs'
$productWxs = New-ProductWxsContent -AppDisplayName $AppDisplayName -Manufacturer $Manufacturer -LaunchExeName $LaunchExeName -UpgradeCode '{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}'
$generatedWxs = New-GeneratedWxsContent -SourceRoot $sourceForPackaging
$productWxs | Set-Content -LiteralPath $productWxsPath -Encoding UTF8
$generatedWxs | Set-Content -LiteralPath $generatedWxsPath -Encoding UTF8

$tempMsiPath = Join-Path $stagingRoot 'tradeplan-installer.msi'
$wixIntermediateRoot = Join-Path $stagingRoot 'wix-intermediate'
New-Item -ItemType Directory -Force -Path $wixIntermediateRoot | Out-Null
if (Test-Path -LiteralPath $tempMsiPath) { Remove-Item -LiteralPath $tempMsiPath -Force }

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $wixIntermediateRoot
$env:TMP = $wixIntermediateRoot
try {
    & $wixExe build $productWxsPath $generatedWxsPath -arch x86 -intermediatefolder $wixIntermediateRoot -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -d SourceDir=$sourceForPackaging -d ProductVersion=$Version -d AppIconPath=$appIconForPackage -o $tempMsiPath
}
finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
}

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempMsiPath)) {
    throw 'Failed to build MSI installer.'
}

$versionedMsiPath = Join-Path $archiveOutputRoot ("{0}-v{1}.msi" -f $PackageName, $Version)
$stableMsiPath = Join-Path $adminOutputRoot ($PackageName + '.msi')
Copy-Item -LiteralPath $tempMsiPath -Destination $versionedMsiPath -Force
Copy-Item -LiteralPath $tempMsiPath -Destination $stableMsiPath -Force
Write-Sha256File -Path $versionedMsiPath
Write-Sha256File -Path $stableMsiPath

$bootstrapperRoot = Join-Path $stagingRoot 'bootstrapper'
$bootstrapperProject = New-BootstrapperProjectFiles -BootstrapperRoot $bootstrapperRoot -MsiPath $tempMsiPath -IconPath $shortcutIconPath -Version $Version -AppDisplayName $AppDisplayName
$tempExePath = Build-BootstrapperExe -BootstrapperProjectPath $bootstrapperProject -PublishRoot (Join-Path $bootstrapperRoot 'publish') -DotnetExe $dotnetExe

$versionedExePath = Join-Path $archiveOutputRoot ("{0}-v{1}.exe" -f $PackageName, $Version)
$stableExePath = Join-Path $OutputRoot ($PackageName + '.exe')
Copy-Item -LiteralPath $tempExePath -Destination $versionedExePath -Force
Copy-Item -LiteralPath $tempExePath -Destination $stableExePath -Force
Write-Sha256File -Path $versionedExePath
Write-Sha256File -Path $stableExePath

$removedVersionedInstallers = Remove-OldVersionedInstallerArchives -ArchiveRoot $archiveOutputRoot -PackageName $PackageName -KeepVersionCount $KeepVersionedInstallerCount
if ($removedVersionedInstallers.Count -gt 0) {
    Write-Host "installer_archives_pruned=$($removedVersionedInstallers.Count)"
}

$packageReadmePath = Join-Path $OutputRoot 'README.txt'
$packageReadme = @(
    '거래플랜 Windows 설치 안내',
    '',
    '일반 사용자/회사 배포용 설치 파일:',
    ' - 거래플랜-PC-설치패키지.exe',
    '',
    '권장 방식:',
    ' - EXE를 실행하고 관리자 권한(UAC)을 허용한 뒤 설치를 진행합니다.',
    ' - 설치 중 경로를 바꿀 수 있으며 기본 경로는 C:\Program Files (x86)\tradeplan 입니다.',
    '',
    '관리자용 보관 파일:',
    ' - 관리자용\거래플랜-PC-설치패키지.msi',
    ' - 관리자용\거래플랜-PC-설치패키지.zip',
    ' - 관리자용\버전보관\*',
    '',
    '참고:',
    ' - 프로그램은 하나이며, 관리자/일반 구분은 로그인 권한으로 처리됩니다.',
    ' - 설치파일 형식(EXE/MSI)은 설치 방식 차이입니다.'
) -join [Environment]::NewLine
$packageReadme | Set-Content -LiteralPath $packageReadmePath -Encoding UTF8

$adminReadmePath = Join-Path $adminOutputRoot 'README.txt'
$adminReadme = @(
    '거래플랜 관리자용 보관 폴더',
    '',
    '포함 항목:',
    ' - MSI 설치본',
    ' - ZIP 내부업데이트 원본',
    ' - 버전별 EXE/MSI 보관본',
    '',
    '일반 사용자 배포는 상위 폴더의 EXE를 사용하세요.'
) -join [Environment]::NewLine
$adminReadme | Set-Content -LiteralPath $adminReadmePath -Encoding UTF8

Write-Host "installer_msi=$stableMsiPath"
Write-Host "installer_exe=$stableExePath"
Write-Host "installer_msi_versioned=$versionedMsiPath"
Write-Host "installer_exe_versioned=$versionedExePath"
Write-Host "installer_admin_root=$adminOutputRoot"
