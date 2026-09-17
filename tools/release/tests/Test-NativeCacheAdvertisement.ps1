param([Parameter(Mandatory=$true)][ValidateSet('Advertise','ObserveUser','ObserveSystem')][string]$Mode,
    [ValidatePattern('^$|^[a-z0-9-]+$')][string]$RunId='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$inputRoot='C:\CacheAdInput';$out='C:\CacheAdOutput';$package='C:\CacheAdPackage\거래플랜-PC-설치패키지'
$guard=Get-Content "$inputRoot\guard.json" -Raw | ConvertFrom-Json
$sha=[Security.Cryptography.SHA256]::Create()
try{$hostHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
if($hostHash -eq $guard.hostComputerHash -or $guard.purpose -cne 'cache-advertisement-20260912' -or
    (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine' -or
    @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count){throw 'Dedicated network-disabled guest required.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator guest required.'}
if(($Mode -eq 'ObserveSystem') -ne ($identity.User.Value -eq 'S-1-5-18')){throw 'Unexpected test identity.'}
$suffix=if($RunId){'-'+$RunId}else{''}
$resultFile="$out\$Mode$suffix.json"
if(Test-Path -LiteralPath $resultFile){throw 'Preserve previous evidence.'}
$result=[ordered]@{state='running';mode=$Mode;sid=$identity.User.Value;pid=$PID;startedAt=(Get-Date).ToString('o')}
function Save {$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultFile -Encoding UTF8}
$lease=$null
try {
    Save
    Add-Type -Path "$inputRoot\NativeMsiRuntime.cs"
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CacheAdvertisementProbe {
    [DllImport("msi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    public static extern uint MsiAdvertiseProductExW(string path,IntPtr script,string transforms,ushort language,uint platform,uint options);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    public static extern int MsiQueryProductStateW(string code);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern uint MsiEnumProductsExW(string code,string sid,uint context,uint index,StringBuilder product,out uint installedContext,IntPtr userSid,IntPtr sidLength);
    [DllImport("msi.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern uint MsiGetProductInfoExW(string code,string sid,uint context,string property,StringBuilder value,ref uint length);
    public static string[] Enumerate(string code,string sid,uint context) {
        // PowerShell binds $null to an empty string for this managed signature.
        // Empty SID is invalid to MSI; this probe uses it to request current user.
        if (String.IsNullOrEmpty(sid)) sid=null;
        StringBuilder product=new StringBuilder(39);uint installedContext;
        uint rc=MsiEnumProductsExW(code,sid,context,0,product,out installedContext,IntPtr.Zero,IntPtr.Zero);
        return new string[]{rc.ToString(),product.ToString(),installedContext.ToString()};
    }
    public static string[] Info(string code,string sid,uint context) {
        if (String.IsNullOrEmpty(sid)) sid=null;
        StringBuilder value=new StringBuilder(256);uint length=256;
        uint rc=MsiGetProductInfoExW(code,sid,context,"AssignmentType",value,ref length);
        return new string[]{rc.ToString(),value.ToString()};
    }
}
'@
    $native=Get-Content "$package\Native\installer.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    if($native.Sha256 -ne $guard.packageMsiHash -or [guid]$native.ProductCode -ne [guid]$guard.productCode){throw 'Unbound package.'}
    $result.productCode=$native.ProductCode
    if($Mode -eq 'Advertise') {
        $result.beforeState=[CacheAdvertisementProbe]::MsiQueryProductStateW($native.ProductCode)
        if($result.beforeState -ne -1){throw 'Fresh guest product identity required.'}
        if(Test-Path 'C:\Program Files (x86)\tradeplan'){throw 'Fresh guest install root required.'}
        $lease=[GeoraePlanInstaller.NativeMsiRuntime]::AcquireCachedPackage("$package\Native\installer.msi",$native.ProductCode,$native.Sha256,[long]$native.Length)
        $result.cache=$lease.Path;$result.cacheHash=(Get-FileHash -LiteralPath $lease.Path).Hash
        $installer=New-Object -ComObject WindowsInstaller.Installer
        try {
            $database=$installer.OpenDatabase($lease.Path,0)
            try {
                $view=$database.OpenView('SELECT `Value` FROM `Property` WHERE `Property` = ''ALLUSERS''')
                try {$view.Execute();$record=$view.Fetch();if($null -eq $record){throw 'ALLUSERS absent.'};try{$result.packageAllUsers=$record.StringData(1)}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)}}
                finally{$view.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)}
            }finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)}
        }finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)}
        # Advertise this exact package to the current user; do not install it.
        $result.advertiseExit=[CacheAdvertisementProbe]::MsiAdvertiseProductExW($lease.Path,[IntPtr]::new(1),$null,0,0,0)
        $result.installRootExists=Test-Path 'C:\Program Files (x86)\tradeplan'
    } else {
        $advertisement=Get-Content "$out\Advertise.json" -Raw | ConvertFrom-Json
        if($advertisement.state -ne 'observed' -or (($Mode -eq 'ObserveSystem') -eq ($advertisement.sid -eq $identity.User.Value))){throw 'Unexpected advertising user context.'}
        $result.advertisingUserSid=$advertisement.sid
        $result.otherUserEnumeration=[CacheAdvertisementProbe]::Enumerate($native.ProductCode,$advertisement.sid,2)
        $result.otherUserAssignment=[CacheAdvertisementProbe]::Info($native.ProductCode,$advertisement.sid,2)
        $result.cacheHash=(Get-FileHash -LiteralPath $advertisement.cache).Hash
    }
    $result.currentState=[CacheAdvertisementProbe]::MsiQueryProductStateW($native.ProductCode)
    $result.currentUserEnumeration=[CacheAdvertisementProbe]::Enumerate($native.ProductCode,$null,7)
    $result.allUsersEnumeration=[CacheAdvertisementProbe]::Enumerate($native.ProductCode,'S-1-1-0',7)
    $result.machineEnumeration=[CacheAdvertisementProbe]::Enumerate($native.ProductCode,$null,4)
    $result.currentUserAssignment=[CacheAdvertisementProbe]::Info($native.ProductCode,$null,2)
    $result.machineAssignment=[CacheAdvertisementProbe]::Info($native.ProductCode,$null,4)
    $result.state='observed'
}catch{$result.state='failed';$result.error=$_.Exception.ToString();throw}
finally{if($null -ne $lease){$lease.Dispose()};$result.finishedAt=(Get-Date).ToString('o');Save}
