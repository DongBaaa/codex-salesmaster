[CmdletBinding()]
param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GEORAEPLAN_ARTIFACT_RETENTION_MANIFEST_APPLY -cne '1') {
    throw 'Manifest regeneration requires GEORAEPLAN_ARTIFACT_RETENTION_MANIFEST_APPLY=1.'
}

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$releaseRoot = Join-Path $root 'Tests\GeoraePlan.Desktop.App.Tests\bin\Release\net8.0-windows'
$manifestPath = Join-Path $root 'tools\maintenance\GeoraePlanArtifactRetentionTestClosureManifest.json'
$sourceEntries = @(
    [pscustomobject]@{
        RelativePath = 'source/ArtifactRetentionSafetyTests.cs'
        FullPath = Join-Path $root 'Tests\GeoraePlan.Desktop.App.Tests\ArtifactRetentionSafetyTests.cs'
    }
    [pscustomobject]@{
        RelativePath = 'source/Invoke-GeoraePlanArtifactRetention.ps1'
        FullPath = Join-Path $root 'tools\maintenance\Invoke-GeoraePlanArtifactRetention.ps1'
    }
)

if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw "Release test closure was not found: $releaseRoot"
}
foreach ($sourceEntry in $sourceEntries) {
    if (-not (Test-Path -LiteralPath $sourceEntry.FullPath -PathType Leaf)) {
        throw "Closure source was not found: $($sourceEntry.FullPath)"
    }
}

$entries = [Collections.Generic.List[object]]::new()
$releaseItems = Get-ChildItem -LiteralPath $releaseRoot -Recurse -Force |
    Sort-Object { $_.FullName.Substring($releaseRoot.Length).Replace('\', '/') }
foreach ($item in $releaseItems) {
    $relative = $item.FullName.Substring($releaseRoot.Length).TrimStart('\', '/').Replace('\', '/')
    if ($item.PSIsContainer) {
        $entries.Add([ordered]@{
            relativePath = "release/$relative"
            kind = 'directory'
            length = $null
            sha256 = $null
        })
        continue
    }

    $entries.Add([ordered]@{
        relativePath = "release/$relative"
        kind = 'file'
        length = [int64]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    })
}

foreach ($sourceEntry in $sourceEntries) {
    $item = Get-Item -LiteralPath $sourceEntry.FullPath
    $entries.Add([ordered]@{
        relativePath = $sourceEntry.RelativePath
        kind = 'file'
        length = [int64]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    })
}

$entries.Sort([Comparison[object]]{
    param($left, $right)
    return [StringComparer]::Ordinal.Compare(
        [string]$left.relativePath,
        [string]$right.relativePath)
})

$releaseFileCount = @($entries | Where-Object { $_.relativePath -like 'release/*' -and $_.kind -eq 'file' }).Count
$releaseDirectoryCount = @($entries | Where-Object { $_.relativePath -like 'release/*' -and $_.kind -eq 'directory' }).Count
if ($releaseFileCount -ne 209 -or $releaseDirectoryCount -ne 65 -or $entries.Count -ne 276) {
    throw "Unexpected closure inventory: files=$releaseFileCount directories=$releaseDirectoryCount entries=$($entries.Count)"
}

$payload = [ordered]@{
    schemaVersion = 1
    kind = 'georaeplan-artifact-retention-test-closure-v1'
    outputFileCount = $releaseFileCount
    outputDirectoryCount = $releaseDirectoryCount
    entries = $entries
}
$json = $payload | ConvertTo-Json -Depth 8 -Compress
$temporaryPath = "$manifestPath.tmp-$([Guid]::NewGuid().ToString('N'))"
try {
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $manifestPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
Write-Host "manifest_path=$manifestPath"
Write-Host "manifest_sha256=$manifestHash"
Write-Host "output_file_count=$releaseFileCount"
Write-Host "output_directory_count=$releaseDirectoryCount"
Write-Host "entry_count=$($entries.Count)"
