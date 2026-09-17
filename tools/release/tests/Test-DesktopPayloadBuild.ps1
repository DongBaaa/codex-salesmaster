[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EvidenceRoot,
    [Parameter(Mandatory = $true)][string]$WixToolPath,
    [string]$AppDisplayName = '거래플랜',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [switch]$EnableTestHooks
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Preserve existing integration evidence.' }
$releaseRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'RuntimeBundleFixture.ps1')
$repo = Split-Path -Parent (Split-Path -Parent $releaseRoot)
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
$project = Join-Path $EvidenceRoot 'project'
$source = Join-Path $project 'source'
$output = Join-Path $project 'output'
$desktop = Join-Path $project 'Desktop\거래플랜.Desktop.App'
foreach ($path in @($project,$source,$desktop,(Join-Path $source 'Updater'),(Join-Path $project 'deploy'),(Join-Path $project 'AppIcons'),(Join-Path $project 'temp'))) {
    [void](New-Item -ItemType Directory -Path $path -Force)
}
[IO.File]::WriteAllText((Join-Path $project 'deploy\Set-ApiBaseUrl.ps1'), '# isolated test marker')
[IO.File]::WriteAllText((Join-Path $desktop '거래플랜.Desktop.App.csproj'), ('<Project><PropertyGroup><Version>'+ $Version +'</Version></PropertyGroup></Project>'))
[IO.File]::WriteAllText((Join-Path $source 'appsettings.json'), '{"Api":{"BaseUrl":"https://fixture.example.invalid"}}')
Add-Type -TypeDefinition @"
using System.Reflection;
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
public static class IsolatedFixture { public static void Main() {} }
"@ -OutputAssembly (Join-Path $source '거래플랜.Desktop.App.exe') -OutputType ConsoleApplication
[IO.File]::WriteAllText((Join-Path $source 'Updater\거래플랜.Updater.exe'), 'non-executable updater fixture')
Add-FixtureRuntimeMetadata -Path (Join-Path $source '거래플랜.Desktop.App.exe')
Add-FixtureRuntimeMetadata -Path (Join-Path $source 'Updater\거래플랜.Updater.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RuntimeBundleFixture.ps1') -Destination $project
Copy-Item -LiteralPath (Join-Path $repo 'AppIcons\tradeplan-windows.ico') -Destination (Join-Path $project 'AppIcons\tradeplan-windows.ico')
$dotnetStub = Join-Path $project 'dotnet-fixture.ps1'
@'
if ($args.Count -eq 1 -and $args[0] -eq '--version') { '8.0.100'; $global:LASTEXITCODE=0; return }
if ($args.Count -lt 2 -or $args[0] -ne 'publish' -or [IO.Path]::GetFileName([string]$args[1]) -ne 'TradePlan.Installer.csproj') {
    throw 'Canonical desktop payload was unexpectedly republished or tool installation attempted.'
}
$outputIndex = [Array]::IndexOf($args, '-o')
if ($outputIndex -lt 0) { throw 'Bootstrapper output argument missing.' }
$output = [string]$args[$outputIndex + 1]
[void](New-Item -ItemType Directory -Path $output -Force)
[IO.File]::WriteAllText((Join-Path $output 'TradePlan.Installer.exe'), 'non-executable bootstrapper fixture')
. (Join-Path $PSScriptRoot 'RuntimeBundleFixture.ps1')
Add-FixtureRuntimeMetadata -Path (Join-Path $output 'TradePlan.Installer.exe')
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'bootstrapper-only-publish.log') -Value 'bootstrapper fixture only'
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath $dotnetStub -Encoding UTF8
$oldPath = $env:PATH; $oldDotnet = $env:DOTNET_EXE; $oldTemp = $env:TEMP; $oldTmp = $env:TMP
try {
    $env:PATH = (Split-Path -Parent $WixToolPath) + ';' + $oldPath
    $env:DOTNET_EXE = $dotnetStub
    $env:TEMP = Join-Path $project 'temp'; $env:TMP = $env:TEMP
    $hookArguments = @()
    if ($EnableTestHooks) { $hookArguments += '-EnableTestHooks' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $releaseRoot 'Build-GeoraePlanDesktopInstaller.ps1') -ProjectRoot $project -SourceFolder $source -OutputRoot $output -AppDisplayName $AppDisplayName -ApiBaseUrl https://fixture.example.invalid @hookArguments *> (Join-Path $EvidenceRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Combined ZIP/native fixture build failed; inspect build.log.' }
}
finally { $env:PATH=$oldPath; $env:DOTNET_EXE=$oldDotnet; $env:TEMP=$oldTemp; $env:TMP=$oldTmp }
$package = Join-Path $output '관리자용\거래플랜-PC-설치패키지'
$manifest = Get-Content -LiteralPath (Join-Path $package 'desktop-payload.json') -Raw -Encoding UTF8 | ConvertFrom-Json
. (Join-Path $releaseRoot 'DesktopPayloadManifest.ps1')
Assert-DesktopPayloadManifest -SourceRoot (Join-Path $package 'App') -Manifest $manifest -Version $Version
$zip = Join-Path $output '관리자용\거래플랜-PC-설치패키지.zip'
Assert-DesktopPayloadArchive -ArchivePath $zip -Manifest $manifest
$buildLog = Get-Content -LiteralPath (Join-Path $EvidenceRoot 'build.log') -Raw
$matches = [regex]::Matches($buildLog, '(?m)^installer_staging_root=(.+)\r?$')
if ($matches.Count -ne 1) { throw 'Native staging evidence ambiguous.' }
$stage = $matches[0].Groups[1].Value.Trim()
if ([IO.Path]::GetDirectoryName($stage) -ne [IO.Path]::GetPathRoot($project) -or
    [IO.Path]::GetFileName($stage) -notmatch '^GeoraePlanInstallerBuild-[a-f0-9]{32}$') { throw 'Unexpected fixture staging path.' }
Assert-DesktopPayloadManifest -SourceRoot (Join-Path $stage 'installer-source') -Manifest $manifest -Version $Version
$msi = Join-Path $output '관리자용\거래플랜-PC-설치패키지.msi'
if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw 'Actual MSI missing.' }
$extractedRoot = Join-Path $EvidenceRoot 'extracted'
$decompiledPath = Join-Path $EvidenceRoot 'decompiled.wxs'
& $WixToolPath msi decompile $msi -x $extractedRoot -o $decompiledPath *> (Join-Path $EvidenceRoot 'decompile.log')
if ($LASTEXITCODE -ne 0) { throw 'Actual MSI extraction failed.' }
[xml]$decompiled = Get-Content -LiteralPath $decompiledPath -Raw -Encoding UTF8
$fileNodes = @($decompiled.SelectNodes('//*[local-name()="File"]'))
if ($fileNodes.Count -ne $manifest.Files.Count) { throw 'MSI embedded payload count mismatch.' }
$msiPayloadPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $fileNodes) {
    $relative = [string]$file.Name
    $parent = $file.ParentNode
    while ($null -ne $parent -and $parent.GetAttribute('Id') -ne 'INSTALLFOLDER') {
        if ($parent.LocalName -eq 'Directory') { $relative = $parent.GetAttribute('Name') + '/' + $relative }
        $parent = $parent.ParentNode
    }
    if ($null -eq $parent -or -not $msiPayloadPaths.Add($relative)) { throw 'MSI payload directory or duplicate path mismatch.' }
    $expected = @($manifest.Files | Where-Object Path -ceq $relative)
    $extracted = Join-Path (Join-Path $extractedRoot 'File') $file.GetAttribute('Id')
    if ($expected.Count -ne 1 -or (Get-Item -LiteralPath $extracted).Length -ne $expected[0].Length -or
        (Get-FileHash -LiteralPath $extracted -Algorithm SHA256).Hash -ne $expected[0].Sha256) { throw "MSI embedded payload differs: $relative" }
}
$publishCalls = @(Get-Content -LiteralPath (Join-Path $project 'bootstrapper-only-publish.log'))
if ($publishCalls.Count -ne 1) { throw 'Unexpected publish count.' }
[ordered]@{
    passed = $true; at = (Get-Date).ToString('o'); manifestFileCount = $manifest.Files.Count
    realCombinedBuilderExecuted = $true; realWixMsiBuilt = $true; zipAndNativeSourceMatch = $true
    actualMsiExtractedPayloadMatchesZip = $true
    appDisplayName = $AppDisplayName
    desktopRepublished = $false; bootstrapperPublishStubbed = $true; installedOnHost = $false
    stage = $stage; msi = $msi; msiSha256 = (Get-FileHash -LiteralPath $msi).Hash; zipSha256 = (Get-FileHash -LiteralPath $zip).Hash
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
Write-Output 'combined_payload_build=PASS'
