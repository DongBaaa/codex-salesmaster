[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile,
    [string]$DotNetPath,
    [string]$JavaSdkDirectory,
    [string]$AndroidSdkDirectory,
    [string]$SigningConfigPath,
    [string]$KeystorePath,
    [string]$KeyAlias,
    [string]$StorePass,
    [string]$KeyPass,
    [string]$Configuration = 'Release',
    [string]$Framework = 'net8.0-android',
    [string]$OutputRoot,
    [string]$VersionName,
    [int]$VersionCode,
    [int]$KeepArtifactDirectoryCount = 2,
    [switch]$AllowDebugSigning,
    [switch]$SkipEnvironmentCheck,
    [switch]$SkipArtifactPrune,
    [switch]$NoRestore
)

$scriptPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Build-GeoraePlanAndroidApk.ps1'

& $scriptPath `
    -ProjectRoot $ProjectRoot `
    -ProjectFile $ProjectFile `
    -DotNetPath $DotNetPath `
    -JavaSdkDirectory $JavaSdkDirectory `
    -AndroidSdkDirectory $AndroidSdkDirectory `
    -SigningConfigPath $SigningConfigPath `
    -KeystorePath $KeystorePath `
    -KeyAlias $KeyAlias `
    -StorePass $StorePass `
    -KeyPass $KeyPass `
    -Configuration $Configuration `
    -Framework $Framework `
    -OutputRoot $OutputRoot `
    -VersionName $VersionName `
    -VersionCode $VersionCode `
    -KeepArtifactDirectoryCount $KeepArtifactDirectoryCount `
    -PackageFormat aab `
    -AllowDebugSigning:$AllowDebugSigning `
    -SkipEnvironmentCheck:$SkipEnvironmentCheck `
    -SkipArtifactPrune:$SkipArtifactPrune `
    -NoRestore:$NoRestore

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
