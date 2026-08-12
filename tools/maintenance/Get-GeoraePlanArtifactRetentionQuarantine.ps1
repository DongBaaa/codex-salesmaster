[CmdletBinding()]
param(
    [string]$AllowedParent = 'D:\DevCaches\georaeplan-private-artifacts',
    [switch]$EmitRetryDescriptor
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GeoraePlanArtifactRetentionProducer.Common.ps1')

$parent = Assert-GeoraePlanArtifactRetentionParent -AllowedParent $AllowedParent
try {
    $quarantines = @(
        Get-ChildItem -LiteralPath $parent.Path -Force -Directory -ErrorAction Stop |
            Where-Object { $_.Name -match '^\.georaeplan-retention-[0-9A-Fa-f]{32}-[0-9A-Fa-f]{32}\.quarantine$' } |
            Sort-Object Name
    )
    $result = New-Object System.Collections.Generic.List[object]
    foreach ($quarantine in $quarantines) {
        $pinned = $null;$ownerLease=$null;$completionLease=$null
        try {
            $pinned = Open-GeoraePlanArtifactRetentionRelativeEntry $parent.Identity $quarantine.Name $true
            Assert-GeoraePlanArtifactRetentionPrivateAcl $pinned.Path
            if (-not $pinned.IsDirectory -or $pinned.VolumeSerialNumber -ine $parent.Identity.VolumeSerialNumber) {
                throw 'Artifact retention quarantine physical identity is invalid.'
            }
            $ownerPath = Join-Path $pinned.Path $script:GeoraePlanArtifactRetentionOwnerFileName
            $completionPath = Join-Path $pinned.Path $script:GeoraePlanArtifactRetentionCompletionFileName
            if (-not (Test-Path -LiteralPath $ownerPath -PathType Leaf) -or -not (Test-Path -LiteralPath $completionPath -PathType Leaf)) {
                throw 'Artifact retention quarantine metadata is incomplete; automatic purge is forbidden.'
            }
            $ownerLease=Open-GeoraePlanArtifactRetentionRelativeEntry $pinned $script:GeoraePlanArtifactRetentionOwnerFileName $false
            $completionLease=Open-GeoraePlanArtifactRetentionRelativeEntry $pinned $script:GeoraePlanArtifactRetentionCompletionFileName $false
            Assert-GeoraePlanArtifactRetentionPrivateFileAcl $ownerLease.Path
            Assert-GeoraePlanArtifactRetentionPrivateFileAcl $completionLease.Path
            $owner = Read-GeoraePlanArtifactRetentionPinnedStrictJson $ownerLease 'quarantine owner metadata'
            $completion = Read-GeoraePlanArtifactRetentionPinnedStrictJson $completionLease 'quarantine completion metadata'
            $artifactId = [string]$owner.artifactId
            if ($artifactId -notmatch '\A[0-9A-Fa-f]{32}\z' -or ([string]$completion.artifactId) -ine $artifactId) {
                throw 'Artifact retention quarantine identity metadata is invalid; automatic purge is forbidden.'
            }
            Assert-GeoraePlanArtifactRetentionPinned $pinned|Out-Null
            $record = [ordered]@{
                artifactId = $artifactId
                quarantinePath = $pinned.Path
                quarantinePhysicalPath = $pinned.PhysicalPath
                quarantineVolumeSerialNumber = $pinned.VolumeSerialNumber
                quarantineFileId = $pinned.FileId
                ownerMetadataSha256 = Get-GeoraePlanArtifactRetentionSha256 (Read-GeoraePlanArtifactRetentionPinnedBytes $ownerLease)
                completionMetadataSha256 = Get-GeoraePlanArtifactRetentionSha256 (Read-GeoraePlanArtifactRetentionPinnedBytes $completionLease)
                action = 'manual_inspect_then_explicit_retention_apply'
                automaticPurge = $false
            }
            $result.Add([pscustomobject]$record)
        } catch {
            $result.Add([pscustomobject][ordered]@{artifactId=$null;quarantinePath=$quarantine.FullName;quarantinePhysicalPath=$null;quarantineVolumeSerialNumber=$null;quarantineFileId=$null;ownerMetadataSha256=$null;completionMetadataSha256=$null;action='manual_inspect_invalid_quarantine';automaticPurge=$false;validationError=$_.Exception.Message})
        } finally { Close-GeoraePlanArtifactRetentionPinned $completionLease;Close-GeoraePlanArtifactRetentionPinned $ownerLease;Close-GeoraePlanArtifactRetentionPinned $pinned }
    }
    if ($EmitRetryDescriptor) {
        [pscustomobject]@{
            schemaVersion = 1
            kind = 'georaeplan-artifact-retention-retry-descriptor-v1'
            allowedParent = $parent.Path
            parentMarkerSha256 = $parent.MarkerSha256
            automaticPurge = $false
            retryCommand = $null
            quarantines = $result.ToArray()
        } | ConvertTo-Json -Depth 8 -Compress
        return
    }
    ConvertTo-Json -InputObject @($result.ToArray()) -Depth 6 -Compress
} finally { Close-GeoraePlanArtifactRetentionParent $parent }
