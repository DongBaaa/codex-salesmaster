[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$FixtureEvidenceRoot, [Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $releaseRoot 'DesktopNativePackage.ps1')
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Preserve existing test evidence.' }
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
$fixture = Get-Content -LiteralPath (Join-Path $FixtureEvidenceRoot 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $fixture.passed -or -not $fixture.bootstrapperPublishStubbed) { throw 'Expected isolated builder fixture.' }
$project = Join-Path $FixtureEvidenceRoot 'project'
$output = Join-Path $project 'output'
$package = Join-Path $output '관리자용\거래플랜-PC-설치패키지'
$zip = Join-Path $output '관리자용\거래플랜-PC-설치패키지.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$committedArchive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $manifestEntry = $committedArchive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq 'Native/installer.json' }
    $reader = New-Object IO.StreamReader ($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $committedArchive.Dispose() }
Assert-DesktopNativePackageArchive -ArchivePath $zip -Manifest $manifest
if ((Get-FileHash -LiteralPath $fixture.msi).Hash -ne $manifest.Sha256) { throw 'Published and embedded MSI differ.' }
$identity = Get-DesktopMsiIdentity -Path $fixture.msi
if ($identity.ProductCode -ne $manifest.ProductCode -or $identity.UpgradeCode -ne $manifest.UpgradeCode -or $identity.ProductVersion -ne $manifest.Version) { throw 'Embedded MSI identity mismatch.' }
$checks = New-Object System.Collections.Generic.List[string]
$checks.Add('embedded and public MSI bytes and identity match')
function Expect-Rejection([string]$Name, [scriptblock]$Action, [string]$Message) {
    $rejected=$false
    try { & $Action } catch { if ($_.Exception.Message -notlike ('*'+$Message+'*')) { throw }; $rejected=$true }
    if (-not $rejected) { throw "Unexpected acceptance: $Name" }
    $checks.Add($Name)
}
foreach ($case in @('msi-content','manifest-content','payload-content','duplicate-msi')) {
    $copy = Join-Path $EvidenceRoot ($case + '.zip')
    Copy-Item -LiteralPath $zip -Destination $copy
    $archive = [IO.Compression.ZipFile]::Open($copy, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entryName = switch ($case) { 'manifest-content' {'Native/installer.json'} 'payload-content' {'desktop-payload.json'} default {'Native/installer.msi'} }
        if ($case -ne 'duplicate-msi') { ($archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $entryName }).Delete() }
        $writer = New-Object IO.StreamWriter ($archive.CreateEntry($entryName).Open())
        try { if ($case -eq 'manifest-content') { $writer.Write('{"SchemaVersion":99}') } else { $writer.Write('altered content') } } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    Expect-Rejection $case { Assert-DesktopNativePackageArchive -ArchivePath $copy -Manifest $manifest } 'Embedded native'
}
Expect-Rejection 'wrong MSI version rejected before embedding' {
    Add-DesktopNativePackage -MsiPath $fixture.msi -PackageRoot $EvidenceRoot -Version '9.9.9' -PayloadManifestPath (Join-Path $package 'desktop-payload.json')
} 'identity mismatch'

# Observe only public artifacts. Rebuilding the unpacked working package is
# expected; committed ZIP/native files and SHA sidecars must survive failure.
$public = @(Get-ChildItem -LiteralPath $output -File)
$admin = Join-Path $output '관리자용'
$public += @(Get-ChildItem -LiteralPath $admin -File | Where-Object Name -notlike '*.lock')
$public += @(Get-ChildItem -LiteralPath (Join-Path $admin '버전보관') -File)
$hashes = @{}
foreach ($file in $public) { $hashes[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
if ($hashes.Count -lt 12) { throw 'Expected complete prior public build.' }
$stub = Join-Path $project 'dotnet-fixture.ps1'
$stubBytes = [IO.File]::ReadAllBytes($stub)
[IO.File]::WriteAllBytes((Join-Path $EvidenceRoot 'original-dotnet-fixture.ps1'), $stubBytes)
$oldDotnet = $env:DOTNET_EXE; $oldTemp=$env:TEMP; $oldTmp=$env:TMP; $oldPath=$env:PATH
try {
    [IO.File]::WriteAllText($stub, "if (`$args.Count -eq 1 -and `$args[0] -eq '--version') { '8.0.100'; `$global:LASTEXITCODE=0; return }; throw 'Injected native bootstrapper build failure'", [Text.UTF8Encoding]::new($true))
    $env:DOTNET_EXE=$stub; $env:TEMP=Join-Path $project 'temp'; $env:TMP=$env:TEMP
    $repo=Split-Path -Parent (Split-Path -Parent $releaseRoot)
    $env:PATH=(Join-Path $repo '.tooling\wix')+';'+$oldPath
    $ErrorActionPreference='Continue'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $releaseRoot 'Build-GeoraePlanDesktopInstaller.ps1') -ProjectRoot $project -SourceFolder (Join-Path $project 'source') -OutputRoot $output -ApiBaseUrl https://fixture.example.invalid *> (Join-Path $EvidenceRoot 'failed-build.log')
    $ErrorActionPreference='Stop'
    if ($LASTEXITCODE -eq 0) { throw 'Injected native build unexpectedly succeeded.' }
    $log=Get-Content -LiteralPath (Join-Path $EvidenceRoot 'failed-build.log') -Raw
    if ($log -notlike '*Injected native bootstrapper build failure*' -or $log -notlike '*before ZIP publication*') { throw 'Build failed for an unexpected reason.' }
} finally {
    [IO.File]::WriteAllBytes($stub,$stubBytes)
    $env:DOTNET_EXE=$oldDotnet; $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:PATH=$oldPath
}
foreach ($path in $hashes.Keys) { if ((Get-FileHash -LiteralPath $path).Hash -ne $hashes[$path]) { throw "Prior public artifact changed: $path" } }
$checks.Add('native build failure preserves all previous public artifacts')
Assert-DesktopNativePackageArchive -ArchivePath $zip -Manifest $manifest
$checks.Add('previous committed ZIP remains valid after native failure')
try {
    $env:DOTNET_EXE=$stub; $env:TEMP=Join-Path $project 'temp'; $env:TMP=$env:TEMP
    $env:PATH=(Join-Path $repo '.tooling\wix')+';'+$oldPath
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $releaseRoot 'Build-GeoraePlanDesktopInstaller.ps1') -ProjectRoot $project -SourceFolder (Join-Path $project 'source') -OutputRoot $output -ApiBaseUrl https://fixture.example.invalid *> (Join-Path $EvidenceRoot 'recovery-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Normal build after failure did not recover.' }
} finally { $env:DOTNET_EXE=$oldDotnet; $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:PATH=$oldPath }
$recoveredManifest=Get-Content -LiteralPath (Join-Path $package 'Native\installer.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-DesktopNativePackageArchive -ArchivePath $zip -Manifest $recoveredManifest
if ((Get-FileHash -LiteralPath $fixture.msi).Hash -ne $recoveredManifest.Sha256) { throw 'Recovered public and embedded MSI differ.' }
$checks.Add('normal rebuild after failure publishes matching ZIP and native MSI')
[ordered]@{passed=$true;at=(Get-Date).ToString('o');checks=@($checks.ToArray());count=$checks.Count;preservedPublicFiles=$hashes.Count;installedOnHost=$false;nativeManifest=$recoveredManifest} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
Write-Output ("embedded_native_checks=PASS count="+$checks.Count)
