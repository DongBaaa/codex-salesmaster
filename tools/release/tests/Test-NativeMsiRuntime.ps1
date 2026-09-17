[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$releaseRoot=Split-Path -Parent $PSScriptRoot
Add-Type -Path (Join-Path $releaseRoot 'NativeMsiRuntime.cs')
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Preserve existing test evidence.'}
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
$payload=Join-Path $EvidenceRoot 'payload'
[void](New-Item -ItemType Directory -Path $payload,(Join-Path $payload 'nested'))
$file=Join-Path $payload 'nested\file [1].txt'
[IO.File]::WriteAllText($file,'original payload',[Text.UTF8Encoding]::new($false))
$length=[long](Get-Item -LiteralPath $file).Length
$hash=(Get-FileHash -LiteralPath $file).Hash
$checks=New-Object System.Collections.Generic.List[string]
function Reject([string]$Name,[scriptblock]$Action,[string]$Expected){
 $failed=$false
 try{& $Action}catch{if($_.Exception.ToString() -notlike ('*'+$Expected+'*')){throw};$failed=$true}
 if(-not $failed){throw "Unexpected acceptance: $Name"};$checks.Add($Name)
}
[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@('nested/file [1].txt'),@($length),@($hash))
$checks.Add('exact nested payload hash verified')
[IO.File]::WriteAllText($file,'modified payload')
Reject 'changed payload rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@('nested/file [1].txt'),@($length),@($hash))} 'differs'
[IO.File]::WriteAllText($file,'original payload',[Text.UTF8Encoding]::new($false))
Reject 'duplicate paths rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@('nested/file [1].txt','NESTED\FILE [1].TXT'),@($length,$length),@($hash,$hash))} 'duplicate'
foreach($path in @('../escape','nested/../escape','C:\outside','nested//file [1].txt')){
 Reject ('invalid path: '+$path) {[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@($path),@($length),@($hash))} 'path'
}
Reject 'length mismatch rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@('nested/file [1].txt'),@($length+1),@($hash))} 'differs'
$outside=Join-Path $EvidenceRoot 'outside'
[void](New-Item -ItemType Directory -Path $outside)
[IO.File]::WriteAllText((Join-Path $outside 'preserve.txt'),'preserved')
$link=Join-Path $payload 'linked'
[void](New-Item -ItemType Junction -Path $link -Target $outside)
try{Reject 'junction traversal rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::AssertPayload($payload,@('linked/preserve.txt'),@(9),@($hash))} 'reparse point'}finally{[IO.Directory]::Delete($link)}
if([IO.File]::ReadAllText((Join-Path $outside 'preserve.txt')) -ne 'preserved'){throw 'Outside data changed.'}
Reject 'relative install root rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::CanonicalDirectory('relative')} 'absolute'
Reject 'volume root rejected' {[GeoraePlanInstaller.NativeMsiRuntime]::CanonicalDirectory('C:\')} 'volume root'
$unknown=[GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot([guid]::NewGuid().ToString('B'),$payload)
if($null -ne $unknown){throw 'Unexpected unrelated product.'}
$checks.Add('absent UpgradeCode returns no product')
$real=[GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot('{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}','C:\Program Files (x86)\tradeplan')
if($null -eq $real -or $real.Version -ne '1.1.711'){throw 'Unexpected current host registration; inspect before proceeding.'}
$checks.Add('read-only host registration matches known baseline')
$other=[GeoraePlanInstaller.NativeMsiRuntime]::FindAtRoot('{0E5C8E78-44C0-4585-A2E9-5E74071A3A11}',$payload)
if($null -ne $other){throw 'Product matched the wrong installation directory.'}
$checks.Add('registered product at another directory is not selected')
[ordered]@{passed=$true;at=(Get-Date).ToString('o');count=$checks.Count;checks=@($checks.ToArray());hostMsiActions='read only';transactionsOpenedOnHost=0} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding utf8
Write-Output ("native_runtime_readonly_tests=PASS count="+$checks.Count)
