param([ValidateSet('Main','Acquire','Gate','Worker')][string]$Mode='Main', [string]$Id='main')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$inputRoot='C:\CacheLeaseInput';$out='C:\CacheLeaseOutput';$work='C:\CacheLeaseWork'
$guard=Get-Content "$inputRoot\guard.json" -Raw | ConvertFrom-Json
$sha=[Security.Cryptography.SHA256]::Create()
try {$hostHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
if($hostHash -eq $guard.hostComputerHash -or $guard.purpose -cne 'native-cache-lease-20260912' -or
    (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine' -or
    @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count -or
    -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Dedicated network-disabled administrator Sandbox required.'
}
if($Id -notmatch '^[a-z0-9-]+$'){throw 'Invalid child identifier.'}
$resultFile="$out\$Id.json"
if(Test-Path -LiteralPath $resultFile){throw 'Preserve previous evidence.'}
$result=[ordered]@{mode=$Mode;id=$Id;pid=$PID;startedAt=(Get-Date).ToString('o');state='running'}
function Save { $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultFile -Encoding UTF8 }
function Wait-File([string]$Path,[int]$Seconds=45) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath $Path)) {
        if($clock.Elapsed.TotalSeconds -gt $Seconds){throw ('Timed out waiting for '+$Path)}
        Start-Sleep -Milliseconds 100
    }
}
function Expect-Failure([scriptblock]$Action,[string]$Pattern) {
    $failed=$false
    try { & $Action | Out-Null } catch { if($_.Exception.ToString() -notmatch $Pattern){throw};$failed=$true }
    if(-not $failed){throw ('Expected failure: '+$Pattern)}
}
function Start-Child([string]$ChildMode,[string]$ChildId) {
    $p=Start-Process "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',"$inputRoot\Test-NativeCacheLease.ps1",'-Mode',$ChildMode,'-Id',$ChildId)
    [void]$p.Handle
    $children.Add($p)
    return $p
}
function Finish-Child($Process,[int]$ExpectedExit=0) {
    if(-not $Process.WaitForExit(45000)){throw 'Child did not finish.'}
    if($Process.ExitCode -ne $ExpectedExit){throw ('Unexpected child exit '+$Process.ExitCode)}
}
function Remove-FixtureCache([string]$Path) {
    $expected=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) ('TradePlanInstallerCache\'+([guid]$fixture.productCode).ToString('N')+'\'+$fixture.hash.ToLowerInvariant()+'\installer.msi')
    if([IO.Path]::GetFullPath($Path) -cne $expected -or (Get-FileHash -LiteralPath $Path).Hash -ne $fixture.hash){throw 'Unexpected synthetic deletion target.'}
    # Deliberately nonrecursive and limited to this generated fixture in a guest.
    [IO.File]::Delete($Path)
}
$lease=$null;$gate=$null
$children=New-Object 'System.Collections.Generic.List[System.Diagnostics.Process]'
try {
    Save
    if($Mode -eq 'Worker') {
        if('GeoraePlanInstaller.NativeMsiRuntime' -as [type]){throw 'Cold process required.'}
        . "$inputRoot\NativeInstallRuntime.embedded.ps1"
        function Test-SameSupervisorPath {param($Left,$Right) return [IO.Path]::GetFullPath($Left).Equals([IO.Path]::GetFullPath($Right),[StringComparison]::OrdinalIgnoreCase)}
        function Assert-NoReparsePoints {param($Path) for($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)){if([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse denied.'}}}
        function Write-InstallLog {param($Message) Add-Content -LiteralPath "$out\worker.log" -Value $Message}
        function Enter-NativeEngineBarrier {
            $script:barrierReached=$true
            $cached=$cacheLease.Path
            Expect-Failure { [IO.File]::Delete($cached) } 'being used|sharing violation|another process'
            $script:protectedDuringBarrier=$true
            throw 'CACHE_LEASE_VERIFIED_NO_MSI_EXECUTED'
        }
        $package='C:\CacheLeasePackage\거래플랜-PC-설치패키지'
        $native=Get-Content "$package\Native\installer.json" -Raw -Encoding UTF8 | ConvertFrom-Json
        $payload=Get-Content "$package\desktop-payload.json" -Raw -Encoding UTF8 | ConvertFrom-Json
        $journal=[pscustomobject]@{FormatVersion=3;InstallRoot='C:\Program Files (x86)\tradeplan';ShortcutRepair=[pscustomobject]@{LegacyBridgeCopy=$false;RemoveLegacyApplicationShortcuts=$false};NativeInstall=[pscustomobject]@{
            SchemaVersion=1;UpgradeCode='{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}';OldProductCode='{EF741B65-0690-4EE6-95A1-96F8C2846FFF}';NewProductCode=$native.ProductCode
            InstallRoot='C:\Program Files (x86)\tradeplan';OldVersion='1.1.713';NewVersion='1.1.720';MsiSha256=$native.Sha256;MsiLength=$native.Length;Files=$payload.Files}}
        $script:barrierReached=$false;$script:protectedDuringBarrier=$false
        Expect-Failure {Invoke-NativeInstallWorker -Journal $journal -PackageRoot $package} 'CACHE_LEASE_VERIFIED_NO_MSI_EXECUTED'
        if(-not $script:barrierReached -or -not $script:protectedDuringBarrier){throw 'Worker did not retain its cache lease.'}
        $cached=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) ('TradePlanInstallerCache\'+([guid]$native.ProductCode).ToString('N')+'\'+$native.Sha256.ToLowerInvariant()+'\installer.msi')
        # Exclusive reopening proves exception unwinding released the worker lease.
        $probe=[IO.File]::Open($cached,'Open','Read','None');$probe.Dispose()
        $result.cacheHash=(Get-FileHash -LiteralPath $cached).Hash
        $result.barrierLeaseVerified=$true;$result.exceptionReleasedLease=$true;$result.msiExecuted=$false
    } else {
        Add-Type -Path "$inputRoot\NativeMsiRuntime.cs"
        if($Mode -ne 'Main') {
            $fixture=Get-Content "$work\fixture.json" -Raw | ConvertFrom-Json
            [IO.File]::WriteAllText("$out\$Id.ready",'ready')
            if($Mode -eq 'Acquire') {
                $lease=[GeoraePlanInstaller.NativeMsiRuntime]::AcquireCachedPackage($fixture.source,$fixture.productCode,$fixture.hash,[long]$fixture.length)
                $result.cache=$lease.Path;$result.hash=(Get-FileHash -LiteralPath $lease.Path).Hash
            } else {
                $gate=[IO.File]::Open($fixture.gate,'Open','ReadWrite','None')
            }
            [IO.File]::WriteAllText("$out\$Id.acquired",'acquired')
            Wait-File "$out\$Id.release" 75
        } else {
            if(Test-Path -LiteralPath $work){throw 'Fresh guest required.'}
            [void](New-Item -ItemType Directory -Path $work)
            $source="$work\synthetic.bin"
            [IO.File]::WriteAllBytes($source,([byte[]](0..255))*32768)
            $fixture=[pscustomobject]@{source=$source;productCode=[guid]::NewGuid().ToString('B');hash=(Get-FileHash $source).Hash;length=(Get-Item $source).Length;gate=''}
            $checks=New-Object 'System.Collections.Generic.List[string]'
            $lease=[GeoraePlanInstaller.NativeMsiRuntime]::AcquireCachedPackage($source,$fixture.productCode,$fixture.hash,$fixture.length)
            $cache=$lease.Path
            if($lease.Length -ne $fixture.length -or (Get-FileHash $cache).Hash -ne $fixture.hash){throw 'Leased content mismatch.'}
            Expect-Failure {Remove-FixtureCache $cache} 'being used|sharing violation|another process'
            Expect-Failure {[IO.File]::Open($cache,'Open','Write','ReadWrite')} 'being used|sharing violation|another process'
            $lease.Dispose();$lease.Dispose();$lease=$null
            $checks.Add('verified source is readable, but cannot be deleted or written while leased; disposal is idempotent')
            $fixture.gate=Join-Path (Split-Path (Split-Path (Split-Path $cache -Parent) -Parent) -Parent) '.cache.lock'
            $fixture | ConvertTo-Json | Set-Content "$work\fixture.json" -Encoding UTF8
            Remove-FixtureCache $cache
            $gate=[IO.File]::Open($fixture.gate,'Open','ReadWrite','None')
            $a=Start-Child Acquire first;$b=Start-Child Acquire second
            Wait-File "$out\first.ready";Wait-File "$out\second.ready"
            Start-Sleep -Milliseconds 700
            if((Test-Path "$out\first.acquired") -or (Test-Path "$out\second.acquired") -or (Test-Path $cache)){throw 'Publication escaped the held gate.'}
            $gate.Dispose();$gate=$null
            Wait-File "$out\first.acquired";Wait-File "$out\second.acquired"
            if((Get-FileHash $cache).Hash -ne $fixture.hash -or @(Get-ChildItem (Split-Path $cache -Parent) -Filter '*.partial').Count){throw 'Concurrent publication was not exact.'}
            Expect-Failure {Remove-FixtureCache $cache} 'being used|sharing violation|another process'
            [IO.File]::WriteAllText("$out\first.release",'release');Finish-Child $a
            Expect-Failure {Remove-FixtureCache $cache} 'being used|sharing violation|another process'
            [IO.File]::WriteAllText("$out\second.release",'release');Finish-Child $b
            Remove-FixtureCache $cache
            $checks.Add('two real processes wait on publication gate, share exact cache, and independently pin it until both release')
            $gate=[IO.File]::Open($fixture.gate,'Open','ReadWrite','None')
            $timer=[Diagnostics.Stopwatch]::StartNew();$timeout=Start-Child Acquire timeout
            Finish-Child $timeout 1
            $timeoutResult=Get-Content "$out\timeout.json" -Raw | ConvertFrom-Json
            if($timer.Elapsed.TotalSeconds -lt 29 -or $timeoutResult.error -notmatch 'being used|sharing violation|another process'){throw 'Bounded gate timeout was not observed.'}
            if(Test-Path -LiteralPath $cache){throw 'Timed-out acquisition published a cache.'}
            $gate.Dispose();$gate=$null
            $checks.Add('held gate times out after 30 seconds without publishing a cache')
            $holder=Start-Child Gate abandoned
            Wait-File "$out\abandoned.acquired"
            $waiting=Start-Child Acquire survivor
            Wait-File "$out\survivor.ready"
            if($holder.HasExited){throw 'Expected live owned gate holder.'}
            $holder.Kill();$holder.WaitForExit()
            Wait-File "$out\survivor.acquired"
            [IO.File]::WriteAllText("$out\survivor.release",'release');Finish-Child $waiting
            $checks.Add('process termination releases OS gate and waiting worker resumes')
            $leaseOwner=Start-Child Acquire terminated-lease
            Wait-File "$out\terminated-lease.acquired"
            Expect-Failure {Remove-FixtureCache $cache} 'being used|sharing violation|another process'
            if($leaseOwner.HasExited){throw 'Expected live owned lease holder.'}
            $leaseOwner.Kill();$leaseOwner.WaitForExit()
            Remove-FixtureCache $cache
            $checks.Add('process termination also releases the source lease without leaving an undeletable cache')
            $before=Get-Acl -LiteralPath $fixture.gate
            $bad=Get-Acl -LiteralPath $fixture.gate
            $bad.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'Modify','Allow'))
            Set-Acl -LiteralPath $fixture.gate -AclObject $bad
            try {Expect-Failure {[GeoraePlanInstaller.NativeMsiRuntime]::AcquireCachedPackage($source,$fixture.productCode,$fixture.hash,$fixture.length)} 'Untrusted MSI cache write access'}
            finally {Set-Acl -LiteralPath $fixture.gate -AclObject $before}
            $checks.Add('untrusted writable gate rejected without acquiring a package')
            $worker=Start-Child Worker worker;Finish-Child $worker
            $checks.Add('fresh real worker embeds current CSharp, pins full MSI before barrier, and releases lease after injected barrier exception')
            $result.checks=@($checks.ToArray());$result.fixtureHash=$fixture.hash
        }
    }
    $result.state='passed'
} catch { $result.state='failed';$result.error=$_.Exception.ToString();throw }
finally {
    if($null -ne $gate){$gate.Dispose()}
    if($null -ne $lease){$lease.Dispose()}
    foreach($p in $children){if(-not $p.HasExited){$p.Kill();$p.WaitForExit()};$p.Dispose()}
    $result.finishedAt=(Get-Date).ToString('o');Save
}
