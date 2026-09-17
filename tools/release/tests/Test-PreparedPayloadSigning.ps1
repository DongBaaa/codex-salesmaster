[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$EvidenceRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Preserve existing signing evidence.'}
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
$releaseRoot=Split-Path -Parent $PSScriptRoot
$repo=Split-Path -Parent (Split-Path -Parent $releaseRoot)
$builder=Join-Path $releaseRoot 'Build-GeoraePlanDesktopNativeInstallers.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($builder,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Native builder syntax is invalid.'}
$fn=$ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-PreparedPayloadSignatures'},$true)
if($fn.Count -ne 1){throw 'Prepared signing function is ambiguous.'}
$unsigned=Join-Path $EvidenceRoot 'unsigned.exe'
Add-Type -TypeDefinition 'public static class PreparedSigningFixture { public static void Main() {} }' -OutputAssembly $unsigned -OutputType ConsoleApplication
$signed=Join-Path $EvidenceRoot 'signed.exe'
Copy-Item -LiteralPath 'C:\Program Files\dotnet\dotnet.exe' -Destination $signed
$sig=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $signed
if($sig.Status -ne 'Valid' -or $null -eq $sig.TimeStamperCertificate){throw 'A trusted timestamped host fixture is required.'}
$tampered=Join-Path $EvidenceRoot 'tampered.exe'
Copy-Item -LiteralPath $signed -Destination $tampered
$bytes=[IO.File]::ReadAllBytes($tampered)
$peOffset=[BitConverter]::ToInt32($bytes,0x3c)
$sectionOffset=$peOffset+24+[BitConverter]::ToUInt16($bytes,$peOffset+20)
$codeOffset=[BitConverter]::ToInt32($bytes,$sectionOffset+20)+64
if($codeOffset -le 0 -or $codeOffset -ge $bytes.Length){throw 'Invalid PE section offset.'}
$bytes[$codeOffset]=$bytes[$codeOffset] -bxor 1
[IO.File]::WriteAllBytes($tampered,$bytes)
$badSig=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $tampered
if($badSig.Status -in @('Valid','NotSigned')){throw 'Tamper fixture must retain a detectable invalid signature.'}
$config=Join-Path $EvidenceRoot 'optional.json'
[IO.File]::WriteAllText($config,'{"schemaVersion":1}',[Text.UTF8Encoding]::new($false))
$wrong=Join-Path $EvidenceRoot 'wrong-signer.json'
[IO.File]::WriteAllText($wrong,'{"certificateThumbprint":"FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF"}',[Text.UTF8Encoding]::new($false))
$cases=@(
    @{name='optional-unsigned';paths=@($unsigned);required=$false;config=$config;pass=$true;marker='prepared_payload_signing=OPTIONAL_UNSIGNED'},
    @{name='required-unsigned';paths=@($unsigned);required=$true;config=$config;pass=$false},
    @{name='optional-invalid-signature';paths=@($tampered);required=$false;config=$config;pass=$false},
    @{name='optional-valid-timestamped';paths=@($signed);required=$false;config=$config;pass=$true;marker='windows_authenticode=PASS'},
    @{name='required-valid-timestamped';paths=@($signed);required=$true;config=$config;pass=$true;marker='windows_authenticode=PASS'},
    @{name='optional-wrong-signer';paths=@($signed);required=$false;config=$wrong;pass=$false},
    @{name='optional-mixed-wrong-signer';paths=@($unsigned,$signed);required=$false;config=$wrong;pass=$false},
    @{name='optional-mixed-valid';paths=@($unsigned,$signed);required=$false;config=$config;pass=$true;marker='windows_authenticode=PASS'},
    @{name='optional-missing';paths=@((Join-Path $EvidenceRoot 'missing.exe'));required=$false;config=$config;pass=$false}
)
$results=@()
foreach($case in $cases){
    $data=Join-Path $EvidenceRoot ($case.name+'.json')
    @{repo=$repo;paths=$case.paths;required=$case.required;config=$case.config} | ConvertTo-Json | Set-Content $data -Encoding UTF8
    $driver=Join-Path $EvidenceRoot ($case.name+'.ps1')
    $body="param([string]`$CaseFile)`r`n`$ErrorActionPreference='Stop'`r`n"+$fn[0].Extent.Text+"`r`n"+@'
$case=Get-Content -LiteralPath $CaseFile -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-PreparedPayloadSignatures -ProjectRoot $case.repo -Paths $case.paths -ConfigPath $case.config -RequireSigning:$case.required
'helper_returned'
'@
    [IO.File]::WriteAllText($driver,$body,[Text.UTF8Encoding]::new($true))
    $stdout=Join-Path $EvidenceRoot ($case.name+'.stdout')
    $stderr=Join-Path $EvidenceRoot ($case.name+'.stderr')
    $p=Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "'+$driver+'" -CaseFile "'+$data+'"') -PassThru -Wait -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $passed=($p.ExitCode -eq 0) -eq $case.pass
    if($case.ContainsKey('marker')){$passed=$passed -and (Get-Content $stdout -Raw).Contains($case.marker)}
    $results+=@{name=$case.name;exitCode=$p.ExitCode;passed=$passed}
    if(-not $passed){throw ('Prepared signing regression failed: '+$case.name)}
}
@{at=(Get-Date).ToString('o');passed=$results.Count;tests=$results;tamperedSignatureStatus=[string]$badSig.Status;certificateStoreModified=$false;hostMsiCalls=0;functionSourceSha256=(Get-FileHash $builder).Hash} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
'prepared_signing_tests_passed='+$results.Count
