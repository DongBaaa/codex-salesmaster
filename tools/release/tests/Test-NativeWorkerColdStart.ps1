param(
    [Parameter(Mandatory=$true)][string]$RuntimePath,
    [Parameter(Mandatory=$true)][ValidateSet('MissingType','CacheReady')][string]$ExpectedOutcome
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$guard=Get-Content C:\AuditInput\guard.json -Raw | ConvertFrom-Json
$sha=[Security.Cryptography.SHA256]::Create()
try {$hostHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
if($hostHash -eq $guard.hostComputerHash -or $guard.purpose -ne 'app-update-native-cache-fixed-1720' -or
    (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine' -or
    @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count){throw 'Dedicated isolated guest required.'}
if('GeoraePlanInstaller.NativeMsiRuntime' -as [type]){throw 'Fresh PowerShell process required.'}
$output='C:\AuditOutput\cold-start-'+$ExpectedOutcome+'.json'
if(Test-Path -LiteralPath $output){throw 'Preserve existing evidence.'}
$package='C:\PackageSource\거래플랜-PC-설치패키지'
$native=Get-Content "$package\Native\installer.json" -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest=Get-Content "$package\desktop-payload.json" -Raw -Encoding UTF8 | ConvertFrom-Json
# Narrow test adapters bind to the real read-only package. The MSI barrier is
# deliberately stopped after the real worker creates and leases its cache.
function Test-SameSupervisorPath { param($Left,$Right) return [IO.Path]::GetFullPath($Left).Equals([IO.Path]::GetFullPath($Right),[StringComparison]::OrdinalIgnoreCase) }
function Assert-NoReparsePoints {
    param($Path)
    if([IO.Path]::GetFullPath($Path) -ne "$package\Native\installer.msi"){throw 'Unexpected fixture source.'}
    $source=Get-Item -LiteralPath $Path
    if($source.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point denied.'}
    for($item=$source.Directory; $null -ne $item; $item=$item.Parent){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point denied.'}
    }
}
function Write-InstallLog {param($Message)}
. $RuntimePath
function Enter-NativeEngineBarrier {throw 'COLD_START_CACHE_READY_NO_MSI_EXECUTED'}
$journal=[pscustomobject]@{
    FormatVersion=3;InstallRoot='C:\Program Files (x86)\tradeplan'
    ShortcutRepair=[pscustomobject]@{LegacyBridgeCopy=$false;RemoveLegacyApplicationShortcuts=$false}
    NativeInstall=[pscustomobject]@{
        SchemaVersion=1;UpgradeCode='{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}'
        OldProductCode='{EF741B65-0690-4EE6-95A1-96F8C2846FFF}';NewProductCode=$native.ProductCode
        InstallRoot='C:\Program Files (x86)\tradeplan';OldVersion='1.1.713';NewVersion='1.1.720'
        MsiSha256=$native.Sha256;MsiLength=(Get-Item "$package\Native\installer.msi").Length;Files=$manifest.Files
    }
}
$errorText=''
try {Invoke-NativeInstallWorker -Journal $journal -PackageRoot $package} catch {$errorText=$_.Exception.ToString()}
$cache=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) ('TradePlanInstallerCache\'+([guid]$native.ProductCode).ToString('N')+'\'+$native.Sha256.ToLowerInvariant()+'\installer.msi')
if($ExpectedOutcome -eq 'MissingType'){
    if($errorText -notlike '*Unable to find type*NativeMsiRuntime*' -or (Test-Path -LiteralPath $cache)){throw ('Original failure not reproduced: '+$errorText)}
} else {
    if($errorText -notlike '*COLD_START_CACHE_READY_NO_MSI_EXECUTED*'){throw ('Cold worker did not reach the MSI barrier: '+$errorText)}
    if((Get-FileHash -LiteralPath $cache).Hash -ne $native.Sha256){throw 'Cached MSI differs.'}
    $old=[GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($journal.NativeInstall.OldProductCode)
    if($null -eq $old -or $old.Version -ne '1.1.713' -or $null -ne [GeoraePlanInstaller.NativeMsiRuntime]::ReadProduct($native.ProductCode)){throw 'Test changed native registration.'}
}
[ordered]@{at=(Get-Date).ToUniversalTime().ToString('o');status='passed';expectedOutcome=$ExpectedOutcome;freshProcess=$true;typeLoaded=[bool]('GeoraePlanInstaller.NativeMsiRuntime' -as [type]);cacheCreated=(Test-Path -LiteralPath $cache);nativeInstallExecuted=$false;runtimeSha256=(Get-FileHash -LiteralPath $RuntimePath).Hash} | ConvertTo-Json | Set-Content -LiteralPath $output -Encoding UTF8
