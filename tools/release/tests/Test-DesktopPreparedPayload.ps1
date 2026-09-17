[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releaseRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent (Split-Path -Parent $releaseRoot)
. (Join-Path $releaseRoot 'DesktopPayloadManifest.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $releaseRoot 'Build-GeoraePlanDesktopNativeInstallers.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Native builder parse failed.' }
foreach ($name in @('Prepare-InstallerSourceFolder', 'Invoke-RobocopyMirror', 'Assert-PreparedPayloadSignatures')) {
    $function = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false) | Where-Object Name -eq $name
    . ([scriptblock]::Create($function.Extent.Text))
}
function Publish-DesktopApplication { throw 'Prepared payload must never be republished.' }
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Preserve existing test evidence.' }
[void](New-Item -ItemType Directory -Path $EvidenceRoot)
$checks = New-Object System.Collections.Generic.List[string]
function Expect-Rejection([string]$Name, [scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch {
        if ($_.Exception.Message -notlike ('*' + $Message + '*')) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Unexpected acceptance: $Name" }
    $checks.Add($Name)
}
$Version = '2.0.0'
$source = Join-Path $EvidenceRoot 'source'
$stage = Join-Path $EvidenceRoot 'stage'
[void](New-Item -ItemType Directory -Path $source, $stage, (Join-Path $source 'Updater'), (Join-Path $source 'data'))
foreach ($path in @('거래플랜.exe','거래플랜.Desktop.App.exe','Updater\거래플랜.Updater.exe','tradeplan-windows.ico','data\설정 [1].json')) {
    [IO.File]::WriteAllText((Join-Path $source $path), ('canonical bytes: ' + $path), [Text.UTF8Encoding]::new($false))
}
$hidden = Join-Path $source 'data\hidden.dat'
[IO.File]::WriteAllText($hidden, 'hidden canonical bytes')
(Get-Item -LiteralPath $hidden).Attributes = [IO.FileAttributes]::Hidden
$manifest = Get-DesktopPayloadManifest -SourceRoot $source -Version $Version -IconFileName 'tradeplan-windows.ico'
Assert-DesktopPayloadManifest -SourceRoot $source -Manifest $manifest -Version $Version
$checks.Add('manifest includes nested and hidden files')
$prepared = Prepare-InstallerSourceFolder -ProjectRoot $projectRoot -OriginalSourceFolder $source -StagingRoot $stage -LaunchExeName '거래플랜.exe' -AppDisplayName '거래플랜' -ShortcutIconPath 'must-not-overwrite.ico' -DotnetExe 'must-not-publish.exe' -PreparedPayloadManifest $manifest
Assert-DesktopPayloadManifest -SourceRoot $prepared.SourceRoot -Manifest $manifest -Version $Version
$checks.Add('prepared native source retains exact bytes without publish or icon overwrite')

$changed = Join-Path $prepared.SourceRoot '거래플랜.exe'
$before = [IO.File]::ReadAllBytes($changed)
[IO.File]::WriteAllText($changed, 'altered exe')
Expect-Rejection 'modified payload rejected' { Assert-DesktopPayloadManifest -SourceRoot $prepared.SourceRoot -Manifest $manifest -Version $Version } 'hash mismatch'
[IO.File]::WriteAllBytes($changed, $before)
$extra = Join-Path $prepared.SourceRoot 'extra.txt'
[IO.File]::WriteAllText($extra, 'extra')
Expect-Rejection 'extra payload rejected' { Assert-DesktopPayloadManifest -SourceRoot $prepared.SourceRoot -Manifest $manifest -Version $Version } 'count mismatch'
[IO.File]::Delete($extra)
[IO.File]::Move($changed, (Join-Path $EvidenceRoot 'preserved.exe'))
Expect-Rejection 'missing payload rejected' { Assert-DesktopPayloadManifest -SourceRoot $prepared.SourceRoot -Manifest $manifest -Version $Version } 'count mismatch'
[IO.File]::Move((Join-Path $EvidenceRoot 'preserved.exe'), $changed)
Expect-Rejection 'wrong version rejected' { Assert-DesktopPayloadManifest -SourceRoot $source -Manifest $manifest -Version '1.0.0' } 'version mismatch'
foreach ($badPath in @('../escape','/absolute','data\bad','data/../escape','C:/outside')) {
    $bad = $manifest | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    $bad.Files[0].Path = $badPath
    Expect-Rejection ('invalid path: ' + $badPath) { Assert-DesktopPayloadManifest -SourceRoot $source -Manifest $bad -Version $Version } 'invalid or duplicate'
}
$bad = $manifest | ConvertTo-Json -Depth 5 | ConvertFrom-Json
$bad.Files[1].Path = $bad.Files[0].Path.ToUpperInvariant()
Expect-Rejection 'case duplicate rejected' { Assert-DesktopPayloadManifest -SourceRoot $source -Manifest $bad -Version $Version } 'invalid or duplicate'
$outside = Join-Path $EvidenceRoot 'outside'
[void](New-Item -ItemType Directory -Path $outside)
[IO.File]::WriteAllText((Join-Path $outside 'preserve.txt'), 'must survive')
$link = Join-Path $source 'linked'
[void](New-Item -ItemType Junction -Path $link -Target $outside)
try {
    Expect-Rejection 'linked directory rejected' { Get-DesktopPayloadManifest -SourceRoot $source -Version $Version -IconFileName 'tradeplan-windows.ico' } 'reparse point'
} finally { [IO.Directory]::Delete($link) }
if ([IO.File]::ReadAllText((Join-Path $outside 'preserve.txt')) -ne 'must survive') { throw 'Outside data changed.' }

$zipRoot = Join-Path $EvidenceRoot 'package'
[void](New-Item -ItemType Directory -Path $zipRoot)
Invoke-RobocopyMirror -Source $source -Destination (Join-Path $zipRoot 'App')
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $zipRoot 'desktop-payload.json') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archivePath = Join-Path $EvidenceRoot 'valid.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($zipRoot, $archivePath)
Assert-DesktopPayloadArchive -ArchivePath $archivePath -Manifest $manifest
$checks.Add('real ZIP payload and manifest match')
$archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'App/거래플랜.exe' }; $entry.Delete()
    $writer = New-Object IO.StreamWriter ($archive.CreateEntry('App/거래플랜.exe').Open())
    try { $writer.Write('tampered archive') } finally { $writer.Dispose() }
} finally { $archive.Dispose() }
Expect-Rejection 'altered ZIP rejected' { Assert-DesktopPayloadArchive -ArchivePath $archivePath -Manifest $manifest } 'hash mismatch'
Expect-Rejection 'unsigned prepared payload rejected when required' { Assert-PreparedPayloadSignatures -ProjectRoot $projectRoot -Paths @((Join-Path $source '거래플랜.exe')) -ConfigPath '' -RequireSigning } 'signature verification failed'
Assert-DesktopPayloadManifest -SourceRoot $source -Manifest $manifest -Version $Version
[ordered]@{ passed = $true; at = (Get-Date).ToString('o'); count = $checks.Count; checks = @($checks.ToArray()); installedOnHost = $false } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
Write-Output ("prepared_payload_tests=PASS count=" + $checks.Count)
