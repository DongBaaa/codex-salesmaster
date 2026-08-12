[CmdletBinding()]
param([string]$AllowedParent = 'D:\DevCaches\georaeplan-private-artifacts',[switch]$Apply)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GeoraePlanArtifactRetentionProducer.Common.ps1')

$parentPath=Get-GeoraePlanArtifactRetentionNormalizedPath $AllowedParent
$driveRoot=[IO.Path]::GetPathRoot($parentPath)
if($driveRoot -notmatch '^[A-Za-z]:\\$' -or (Test-GeoraePlanArtifactRetentionPathEquals $parentPath $driveRoot)){throw 'Artifact parent must be a non-root local drive directory.'}
if(Test-Path -LiteralPath $parentPath -PathType Container){try{$valid=Assert-GeoraePlanArtifactRetentionParent $parentPath;try{Write-Output "artifact_retention_parent=$(if($Apply){'APPLIED'}else{'DRY_RUN'}) action=already_valid path=$($valid.Path)"}finally{Close-GeoraePlanArtifactRetentionParent $valid};return}catch{if(-not$Apply){throw}}}

$ancestorPath=Split-Path -Parent $parentPath
if(-not(Test-Path -LiteralPath $ancestorPath -PathType Container)){throw 'Artifact parent ancestor must exist.'}
$ancestor=Open-GeoraePlanArtifactRetentionPinned $ancestorPath
$parent=$null;$bootstrapLease=$null
try{
 $leaf=Split-Path -Leaf $parentPath
 $bootstrapName='.georaeplan-parent-bootstrap-'+$leaf+'.json'
 $bootstrapPath=Join-Path $ancestor.Path $bootstrapName
 $ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 if(Test-Path -LiteralPath $parentPath){
  $parent=Open-GeoraePlanArtifactRetentionRelativeEntry $ancestor $leaf $true
  Assert-GeoraePlanArtifactRetentionPrivateAcl $parent.Path
  $tokenBytes=$parent.NativeHandle.ReadExtendedAttribute($script:GeoraePlanArtifactRetentionParentEaName)
  $tokenRaw=[Text.UTF8Encoding]::new($false,$true).GetString($tokenBytes)
  $token=$tokenRaw|ConvertFrom-Json -ErrorAction Stop
  if($tokenRaw -cne (ConvertTo-GeoraePlanArtifactRetentionStrictJson $token)){throw 'Parent provisioning token is not canonical.'}
   Assert-GeoraePlanArtifactRetentionParentProvisioningToken $token $parentPath $ancestor
 } else {
  if(-not $Apply){Write-Output "artifact_retention_parent=DRY_RUN action=would_create path=$parentPath";return}
  $token=[ordered]@{schemaVersion=1;kind='georaeplan-artifact-parent-token-v1';parentId=[Guid]::NewGuid().ToString('N');createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o');parentPath=$parentPath;ancestorPhysicalPath=$ancestor.PhysicalPath;ancestorVolumeSerialNumber=$ancestor.VolumeSerialNumber;ancestorFileId=$ancestor.FileId}
  $tokenBytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-GeoraePlanArtifactRetentionStrictJson $token))
  $parent=New-GeoraePlanArtifactRetentionProvisionedRelativeDirectory $ancestor $leaf $script:GeoraePlanArtifactRetentionParentEaName $tokenBytes
 }
 Assert-GeoraePlanArtifactRetentionProvisioningToken $parent $script:GeoraePlanArtifactRetentionParentEaName $tokenBytes|Out-Null
 $bootstrap=[ordered]@{schemaVersion=1;kind='georaeplan-artifact-parent-bootstrap-v1';parentPath=$parentPath;ownerSid=$ownerSid;parentId=[string]$token.parentId;createdAtUtc=[string]$token.createdAtUtc;ancestorPhysicalPath=$ancestor.PhysicalPath;ancestorVolumeSerialNumber=$ancestor.VolumeSerialNumber;ancestorFileId=$ancestor.FileId;parentPhysicalPath=$parent.PhysicalPath;parentVolumeSerialNumber=$parent.VolumeSerialNumber;parentFileId=$parent.FileId;rootTokenSha256=(Get-GeoraePlanArtifactRetentionSha256 $tokenBytes)}
  $bootstrapBytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-GeoraePlanArtifactRetentionStrictJson $bootstrap));$bootstrapLease=Write-GeoraePlanArtifactRetentionRelativeCreateNewBytes $ancestor $bootstrapName $bootstrapBytes;Assert-GeoraePlanArtifactRetentionExactJson (Read-GeoraePlanArtifactRetentionPinnedStrictJson $bootstrapLease 'parent bootstrap journal') $bootstrap 'parent bootstrap journal'
  $allowedPartial=@($script:GeoraePlanArtifactRetentionParentLeaseFileName,$script:GeoraePlanArtifactRetentionParentOwnerFileName)
 Get-ChildItem -LiteralPath $parent.Path -Force -ErrorAction Stop|ForEach-Object{if($_.Name -notin $allowedPartial){throw 'Partial artifact parent contains an unknown entry.'}}
 $leasePath=Join-Path $parent.Path $script:GeoraePlanArtifactRetentionParentLeaseFileName
  $leasePin=Write-GeoraePlanArtifactRetentionRelativeCreateNewBytes $parent $script:GeoraePlanArtifactRetentionParentLeaseFileName ([byte[]]@());try{if((Read-GeoraePlanArtifactRetentionPinnedBytes $leasePin).Length -ne 0){throw 'Existing parent coordinator is nonconforming.'}}finally{Close-GeoraePlanArtifactRetentionPinned $leasePin}
 $marker=[ordered]@{schemaVersion=1;owner=$script:GeoraePlanArtifactRetentionParentOwnerKind;parentId=[string]$token.parentId;parentPath=$parentPath;parentPhysicalPath=$parent.PhysicalPath;parentVolumeSerialNumber=$parent.VolumeSerialNumber;parentFileId=$parent.FileId}
 $markerPath=Join-Path $parent.Path $script:GeoraePlanArtifactRetentionParentOwnerFileName
  $markerPin=Write-GeoraePlanArtifactRetentionRelativeCreateNewBytes $parent $script:GeoraePlanArtifactRetentionParentOwnerFileName ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-GeoraePlanArtifactRetentionStrictJson $marker)));try{Assert-GeoraePlanArtifactRetentionExactJson (Read-GeoraePlanArtifactRetentionPinnedStrictJson $markerPin 'parent marker') $marker 'parent marker'}finally{Close-GeoraePlanArtifactRetentionPinned $markerPin}
 Assert-GeoraePlanArtifactRetentionProvisioningToken $parent $script:GeoraePlanArtifactRetentionParentEaName $tokenBytes|Out-Null
 Close-GeoraePlanArtifactRetentionPinned $bootstrapLease;$bootstrapLease=$null
 Close-GeoraePlanArtifactRetentionPinned $parent;$parent=$null
 $verified=Assert-GeoraePlanArtifactRetentionParent $parentPath
 try{Write-Output "artifact_retention_parent=APPLIED action=provisioned path=$parentPath parent_id=$($token.parentId)"}finally{Close-GeoraePlanArtifactRetentionParent $verified}
}finally{Close-GeoraePlanArtifactRetentionPinned $bootstrapLease;Close-GeoraePlanArtifactRetentionPinned $parent;Close-GeoraePlanArtifactRetentionPinned $ancestor}
