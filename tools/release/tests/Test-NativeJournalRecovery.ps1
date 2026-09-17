[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Preserve existing test evidence.' }
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
# Deliberate engine double: this test cannot invoke host MSI transactions.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
namespace GeoraePlanInstaller {
 public sealed class Product { public string ProductCode; public string Version; public string InstallRoot; }
 public sealed class Barrier : IDisposable { public void Dispose() { NativeMsiRuntime.Disposed++; } }
 public static class NativeMsiRuntime {
  public static Dictionary<string,Product> Products = new Dictionary<string,Product>(StringComparer.OrdinalIgnoreCase);
  public static int Disposed;
  public static int Begun;
  public static Barrier TryBegin() { Begun++; return new Barrier(); }
  public static Product ReadProduct(string code) { Product p; return Products.TryGetValue(code,out p) ? p : null; }
  public static Product FindAtRoot(string upgrade,string root) {
   Product found=null;
   foreach(Product p in Products.Values) if(String.Equals(p.InstallRoot,root,StringComparison.OrdinalIgnoreCase)) {
    if(found!=null) throw new Exception("Duplicate native products at root."); found=p;
   }
   return found;
  }
 }
}
'@
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'NativeInstallRuntime.ps1')
$InstallRoot = Join-Path $EvidenceRoot 'installed'
$oldCode = '{A88F8FA5-005A-49DD-A8C7-82023174BAAB}'
$newCode = '{2F9BEB1D-BB66-4527-99FE-2ACB0D868F39}'
$events = New-Object 'System.Collections.Generic.List[string]'
$checks = New-Object 'System.Collections.Generic.List[string]'
$script:NativeRecoveryCommitted = $false
$script:invalidPayload = $false
$script:cleanupFailure = $false
$script:shortcutFailure = $false
function Test-SameSupervisorPath($Left,$Right) { return [string]::Equals($Left,$Right,[StringComparison]::OrdinalIgnoreCase) }
function Write-InstallLog($Message) {}
function Assert-NativeInstalledPayload($Files,$Root) {
    $events.Add('verify-new')
    if ($script:invalidPayload) { throw 'Injected new payload corruption.' }
}
function Write-SupervisorJournal($Journal,$JournalPath) { $events.Add('journal:'+$Journal.Phase) }
function Invoke-PendingShortcutRepair($Repair) {
    $events.Add('shortcuts')
    if ($script:shortcutFailure) { throw 'Injected shortcut repair failure.' }
}
function New-ShortcutRepairPendingException($Message,$InnerException) {
    $pendingException = [InvalidOperationException]::new($Message,$InnerException)
    $pendingException.Data['GeoraePlanShortcutRepairPending']=$true
    return $pendingException
}
function Restore-InstallRollbackSnapshot($Snapshot) { $events.Add('restore-old') }
function Assert-RestoredInstallRollbackSnapshot($Snapshot) { $events.Add('verify-old') }
function Remove-CompletedSupervisorState($Journal,$JournalPath) {
    $events.Add('cleanup')
    if ($script:cleanupFailure) { throw 'Injected interrupted cleanup.' }
}
function New-Journal {
    return [pscustomobject]@{
        FormatVersion=3; InstallRoot=$InstallRoot; Phase='WorkerRunning'; Snapshots=@([pscustomobject]@{Label='primary';Path=$InstallRoot})
        ShortcutRepair=@{Enabled=$true;LegacyBridgeCopy=$false;RemoveLegacyApplicationShortcuts=$false;LegacyInstallRoot=(Join-Path $EvidenceRoot 'legacy')}
        NativeInstall=[pscustomobject]@{
            SchemaVersion=1; UpgradeCode='0E5C8E78-44C0-4585-A2E9-5E74071A3A11'; InstallRoot=$InstallRoot
            OldProductCode=$oldCode; NewProductCode=$newCode; OldVersion='1.0.0'; NewVersion='2.0.0'
            MsiSha256=('a'*64); MsiLength=123
            Files=@([pscustomobject]@{Path='app.exe';Length=8;Sha256=('b'*64)})
        }
    }
}
function Set-Product([string]$Code,[string]$Version,[string]$Root=$InstallRoot) {
    $p=New-Object GeoraePlanInstaller.Product
    $p.ProductCode=$Code; $p.Version=$Version; $p.InstallRoot=$Root
    [GeoraePlanInstaller.NativeMsiRuntime]::Products[$Code]=$p
}
function Reset-Case {
    $events.Clear(); [GeoraePlanInstaller.NativeMsiRuntime]::Products.Clear()
    $script:NativeRecoveryCommitted=$false; $script:invalidPayload=$false; $script:cleanupFailure=$false; $script:shortcutFailure=$false
}
function Assert-Events([string]$Expected) {
    $actual=$events -join ','
    if ($actual -cne $Expected) { throw "Events mismatch: $actual; expected $Expected" }
}
function Reject([string]$Name,[scriptblock]$Action) {
    $failed=$false
    try { & $Action } catch { $failed=$true }
    if (-not $failed) { throw "Unexpected acceptance: $Name" }
    $checks.Add($Name)
}
Reset-Case; Set-Product $oldCode '1.0.0'
$j=New-Journal
Invoke-NativeJournalRecovery $j 'fixture-journal'
Assert-Events 'journal:Recovering,restore-old,verify-old,journal:RestoredCleanupPending,cleanup'
if ($script:NativeRecoveryCommitted) { throw 'Rollback reported commit.' }
$checks.Add('old registration restores and verifies snapshots before cleanup')

Reset-Case; Set-Product $newCode '2.0.0'
$j=New-Journal
Invoke-NativeJournalRecovery $j 'fixture-journal'
Assert-Events 'verify-new,journal:ShortcutRepairPending,shortcuts,journal:CommittedCleanupPending,cleanup'
if (-not $script:NativeRecoveryCommitted) { throw 'Post-commit worker death did not roll forward.' }
$checks.Add('new registration after worker death never restores old files')

Reset-Case; Set-Product $newCode '2.0.0'; $script:invalidPayload=$true
Reject 'corrupt committed payload preserves state without rollback' { Invoke-NativeJournalRecovery (New-Journal) 'fixture-journal' }
Assert-Events 'verify-new'

foreach ($case in @('both','neither','wrong-version','wrong-root','unexpected-product')) {
    Reset-Case
    switch ($case) {
        both { Set-Product $oldCode '1.0.0'; Set-Product $newCode '2.0.0' }
        wrong-version { Set-Product $newCode '3.0.0' }
        wrong-root { Set-Product $newCode '2.0.0' (Join-Path $EvidenceRoot 'elsewhere') }
        unexpected-product { Set-Product '{148521E8-FA0A-4A38-AE6B-98D76838ECF5}' '2.0.0' }
    }
    Reject ('indeterminate '+$case+' preserves files and journal') { Invoke-NativeJournalRecovery (New-Journal) 'fixture-journal' }
    Assert-Events ''
}
Reset-Case; Set-Product $oldCode '1.0.0'; $script:cleanupFailure=$true
$j=New-Journal
Reject 'rollback cleanup interruption remains resumable' { Invoke-NativeJournalRecovery $j 'fixture-journal' }
if ($j.Phase -ne 'RestoredCleanupPending') { throw 'Rollback did not persist completed restoration.' }
$events.Clear(); $script:cleanupFailure=$false
Invoke-NativeJournalRecovery $j 'fixture-journal'
Assert-Events 'verify-old,journal:RestoredCleanupPending,cleanup'
$checks.Add('partial snapshot cleanup resumes without re-reading deleted backups')

Reset-Case; Set-Product $newCode '2.0.0'; $script:cleanupFailure=$true
$j=New-Journal
Reject 'commit cleanup interruption remains resumable' { Invoke-NativeJournalRecovery $j 'fixture-journal' }
$events.Clear(); $script:cleanupFailure=$false
Invoke-NativeJournalRecovery $j 'fixture-journal'
Assert-Events 'verify-new,journal:ShortcutRepairPending,shortcuts,journal:CommittedCleanupPending,cleanup'
$checks.Add('commit cleanup retry never restores snapshots')

Reset-Case; Set-Product $oldCode '1.0.0'
$j=New-Journal; $j.Phase='CommittedCleanupPending'
Reject 'committed journal with reverted registration fails closed' { Invoke-NativeJournalRecovery $j 'fixture-journal' }
Assert-Events ''
$j=New-Journal; $j.FormatVersion=2
Reject 'native payload cannot enter legacy journal format' { Assert-NativeJournalBinding $j }
$j=New-Journal; $j.NativeInstall.Files[0].Path='../outside'
Reject 'native journal payload cannot escape install root' { Assert-NativeJournalBinding $j }
$j=New-Journal; $j.NativeInstall.NewProductCode=$oldCode
Reject 'ambiguous same-product repair cannot enter upgrade journal' { Assert-NativeJournalBinding $j }
Reset-Case; Set-Product $newCode '2.0.0'; $script:shortcutFailure=$true
$j=New-Journal
$typedPending=$false
try { Invoke-NativeJournalRecovery $j 'fixture-journal' }
catch { $typedPending=$_.Exception.Data.Contains('GeoraePlanShortcutRepairPending') }
if (-not $typedPending -or $j.Phase -ne 'ShortcutRepairPending') { throw 'Shortcut failure did not retain the committed pending contract.' }
Assert-Events 'verify-new,journal:ShortcutRepairPending,shortcuts'
$checks.Add('shortcut-only failure reports typed pending state without file rollback')
$events.Clear();$script:shortcutFailure=$false
Invoke-NativeJournalRecovery $j 'fixture-journal'
Assert-Events 'verify-new,journal:ShortcutRepairPending,shortcuts,journal:CommittedCleanupPending,cleanup'
$checks.Add('shortcut recovery retry completes the committed installation')

$j=New-Journal; $j.ShortcutRepair.LegacyBridgeCopy=$true
Reject 'legacy mutation requires its own snapshot' { Assert-NativeJournalBinding $j }
$j.Snapshots += [pscustomobject]@{Label='legacy';Path=$j.ShortcutRepair.LegacyInstallRoot}
Assert-NativeJournalBinding $j
$checks.Add('legacy path bound to independent snapshot is accepted')
$j.Snapshots[1].Path=$InstallRoot
Reject 'legacy snapshot at the primary root is rejected' { Assert-NativeJournalBinding $j }

if ([GeoraePlanInstaller.NativeMsiRuntime]::Disposed -ne [GeoraePlanInstaller.NativeMsiRuntime]::Begun) { throw 'An engine barrier was leaked.' }
[ordered]@{passed=$true;at=(Get-Date).ToString('o');count=$checks.Count;checks=@($checks.ToArray());engine='in-memory double';hostMsiActions=0} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
Write-Output ('native_journal_recovery=PASS count='+$checks.Count)
