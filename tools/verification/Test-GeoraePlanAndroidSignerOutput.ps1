[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$ApkSignerPath,
    [string]$JavaHome,
    [string]$KnownApkPath
)
$ErrorActionPreference = 'Stop'
$tokens=$null; $errors=$null
$source=Join-Path $ProjectRoot 'tools\mobile\Test-GeoraePlanAndroidSigningContinuity.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($source,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'Signing gate parse failed.' }
foreach ($f in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('ConvertFrom-AndroidSigningCertificateOutput','Get-ApkSigningCertificate')},$true)) {
    . ([scriptblock]::Create($f.Extent.Text))
}
$results=[Collections.Generic.List[object]]::new()
$hash='a'*64
$valid="Signer #1 certificate DN: CN=Fixture Release`nSigner #1 certificate SHA-256 digest: $hash"
function Check-Case {
    param([string]$Name,[string]$Text,[switch]$Reject,[bool]$Debug=$false)
    $caught=$false; $value=$null
    try { $value=ConvertFrom-AndroidSigningCertificateOutput -OutputText $Text }
    catch { $caught=$true }
    if ($caught -ne $Reject.IsPresent) { throw "Unexpected acceptance/rejection: $Name" }
    if (-not $Reject -and ($value.CertificateSha256 -cne $hash -or $value.IsDebugSigning -ne $Debug)) { throw "Unexpected certificate metadata: $Name" }
    $results.Add([pscustomobject]@{Case=$Name;Passed=$true;Rejected=$caught})
}
Check-Case 'single-release' $valid
Check-Case 'single-debug' ($valid.Replace('CN=Fixture Release','CN=Android Debug, O=Android, C=US')) -Debug $true
Check-Case 'uppercase-digest' ($valid.Replace($hash,$hash.ToUpperInvariant()))
Check-Case 'verbose-one-signer' ("Number of signers: 1`r`n"+$valid.Replace("`n","`r`n"))
Check-Case 'second-signer' ($valid+"`nSigner #2 certificate DN: CN=Other`nSigner #2 certificate SHA-256 digest: "+('b'*64)) -Reject
Check-Case 'duplicate-digest' ($valid+"`nSigner #1 certificate SHA-256 digest: $hash") -Reject
Check-Case 'short-digest' ($valid.Replace($hash,'a'*63)) -Reject
Check-Case 'nonhex-digest' ($valid.Replace($hash,('a'*63)+'x')) -Reject
Check-Case 'missing-subject' ("Signer #1 certificate SHA-256 digest: $hash") -Reject
Check-Case 'missing-digest' 'Signer #1 certificate DN: CN=Fixture' -Reject
Check-Case 'wrong-ordinal' ($valid.Replace('#1','#2')) -Reject
Check-Case 'declared-two-signers' ("Number of signers: 2`n"+$valid) -Reject
Check-Case 'extra-subject' ($valid+"`nSigner #2 certificate DN: CN=Other") -Reject
Check-Case 'duplicate-subject' ($valid+"`nSigner #1 certificate DN: CN=Other") -Reject
Check-Case 'empty-subject' ($valid.Replace('CN=Fixture Release','')) -Reject
if ($KnownApkPath) {
    if (-not $ApkSignerPath -or -not $JavaHome) { throw 'Real APK verification requires signer and Java paths.' }
    $cert=Get-ApkSigningCertificate -ApkPath $KnownApkPath -ApkSignerPath $ApkSignerPath -JavaHome $JavaHome
    if ($cert.CertificateSha256 -cne 'dfc2e3680116ebe4291c466ba7da9491a2ecdf8502323ffafefc155e0c45dc28' -or -not $cert.IsDebugSigning) { throw 'Known published APK certificate changed.' }
    $results.Add([pscustomobject]@{Case='real-published-apk';Passed=$true;Rejected=$false})
}
[pscustomobject]@{Passed=$results.Count;Failed=0;PowerShell=$PSVersionTable.PSVersion.ToString();Cases=$results.ToArray()} | ConvertTo-Json -Depth 5
