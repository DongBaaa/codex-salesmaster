$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$out='C:\AuditOutput';$work='C:\AuditWork'
if(-not (Test-Path "$out\NativeMsiRuntime.cs") -or -not (Test-Path C:\AuditInput\guard.json)){throw 'Dedicated test mappings required.'}
$guard=Get-Content C:\AuditInput\guard.json -Raw | ConvertFrom-Json
$sha=[Security.Cryptography.SHA256]::Create()
try{$hostHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($env:COMPUTERNAME)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
if($hostHash -eq $guard.hostComputerHash -or $guard.purpose -ne 'app-initiated-native-update-1720' -or
 (Get-CimInstance Win32_ComputerSystem).Model -ne 'Virtual Machine' -or
 @(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue).Count){throw 'Isolated guest required.'}
if(Test-Path "$out\cache-tests.json"){throw 'Fresh cache test output required.'}
Add-Type -Path "$out\NativeMsiRuntime.cs"
$checks=New-Object 'System.Collections.Generic.List[string]'
$result=[ordered]@{state='running';checks=@();startedAt=(Get-Date).ToString('o')}
function Expect-Failure([scriptblock]$Action,[string]$Pattern){
 $failed=$false
 try{& $Action | Out-Null}catch{if($_.Exception.ToString() -notmatch $Pattern){throw};$failed=$true}
 if(-not $failed){throw ('Expected failure: '+$Pattern)}
}
try {
 $source=Join-Path $work 'cache-fixture.bin'
 if(Test-Path $source){throw 'Fresh fixture required.'}
 [IO.File]::WriteAllBytes($source,[byte[]](0..255)*32)
 $hash=(Get-FileHash -LiteralPath $source).Hash;$length=(Get-Item $source).Length
 $code=[guid]::NewGuid().ToString('B')
 $cache=[GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($source,$code,$hash,$length)
 if((Get-FileHash $cache).Hash -ne $hash){throw 'Cache content mismatch.'}
 $checks.Add('exact byte copy into protected durable cache')
 $moved=Join-Path $work 'preserved-cache-fixture.bin'
 if([IO.Path]::GetFullPath($source) -ne 'C:\AuditWork\cache-fixture.bin' -or (Test-Path $moved)){throw 'Unexpected fixture move.'}
 Move-Item -LiteralPath $source -Destination $moved
 if([GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($source,$code,$hash,$length) -ne $cache){throw 'Existing cache not reused.'}
 $checks.Add('source deletion does not invalidate existing verified cache')
 Expect-Failure { [GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($moved,$code,'invalid',1) } 'Invalid native source descriptor'
 $checks.Add('invalid descriptor rejected')
 $corrupt=[GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($moved,[guid]::NewGuid().ToString('B'),$hash,$length)
 [IO.File]::WriteAllText($corrupt,'deliberately corrupted test cache')
 $corruptCode=(Split-Path (Split-Path (Split-Path $corrupt -Parent) -Parent) -Leaf)
 Expect-Failure { [GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($moved,$corruptCode,$hash,$length) } 'Existing MSI cache differs'
 $checks.Add('corrupted existing cache rejected without overwrite')
 $aclCode=[guid]::NewGuid().ToString('B')
 $aclCache=[GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($moved,$aclCode,$hash,$length)
 $acl=Get-Acl -LiteralPath $aclCache
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
 Set-Acl -LiteralPath $aclCache -AclObject $acl
 Expect-Failure { [GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($moved,$aclCode,$hash,$length) } 'Untrusted MSI cache write access'
 $checks.Add('nonadministrator writable cache rejected')
 $badParent=Join-Path $work 'bad-cache-parent'
 [void](New-Item -ItemType Directory $badParent)
 $parentAcl=Get-Acl -LiteralPath $badParent
 $parentAcl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 $parentAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),[Security.AccessControl.FileSystemRights]::Modify,[Security.AccessControl.AccessControlType]::Allow))
 Set-Acl -LiteralPath $badParent -AclObject $parentAcl
 $parentCheck=[GeoraePlanInstaller.NativeMsiRuntime].GetMethod('AssertCacheParentSecurity',[Reflection.BindingFlags]'NonPublic,Static')
 $parentDelegate=$parentCheck.CreateDelegate([Action[string]])
 Expect-Failure { $parentDelegate.Invoke([string]$badParent) } 'MSI cache parent permits untrusted writes'
 $checks.Add('nonadministrator writable parent rejected')
 $native=Get-Content 'C:\PackageSource\거래플랜-PC-설치패키지\Native\installer.json' -Raw -Encoding UTF8 | ConvertFrom-Json
 $realSource='C:\PackageSource\거래플랜-PC-설치패키지\Native\installer.msi'
 $realCache=[GeoraePlanInstaller.NativeMsiRuntime]::CachePackage($realSource,$native.ProductCode,$native.Sha256,[long]$native.Length)
 $result.nativeCache=$realCache;$result.nativeHash=(Get-FileHash $realCache).Hash
 $result.state='passed'
} catch {$result.state='failed';$result.error=$_.Exception.ToString();throw} finally {$result.checks=@($checks.ToArray());$result.finishedAt=(Get-Date).ToString('o');$result | ConvertTo-Json -Depth 5 | Set-Content "$out\cache-tests.json" -Encoding UTF8}
