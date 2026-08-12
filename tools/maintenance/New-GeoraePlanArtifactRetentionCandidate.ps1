[CmdletBinding()]
param(
 [string]$AllowedParent='D:\DevCaches\georaeplan-private-artifacts',
 [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{32}$')][string]$ArtifactId,
 [Parameter(Mandatory=$true)][string]$StagePath,
 [ValidateSet('','BeforeHandleBoundPublish','MutateStageMarkerBeforeHandleBoundPublish','MutateBootstrapBeforeStageCreate')][string]$TestFaultInjection='',
 [switch]$Apply
)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GeoraePlanArtifactRetentionProducer.Common.ps1')

$parent=Assert-GeoraePlanArtifactRetentionParent $AllowedParent
try {
 $id=$ArtifactId.ToLowerInvariant()
 $stage=Get-GeoraePlanArtifactRetentionNormalizedPath $StagePath
 $expectedName='.georaeplan-producer-stage-'+$id
 if(-not(Test-GeoraePlanArtifactRetentionPathEquals (Split-Path -Parent $stage) $parent.Path) -or (Split-Path -Leaf $stage) -cne $expectedName){throw 'Stage must be the exact non-GUID direct child of the pinned parent.'}
 if(-not $Apply){
  if(Test-Path -LiteralPath $stage){
   $pin=Open-GeoraePlanArtifactRetentionRelativeEntry $parent.Identity $expectedName $true;$manifestLease=$null
   try{
    Assert-GeoraePlanArtifactRetentionPrivateAcl $pin.Path;$tokenBytes=$pin.NativeHandle.ReadExtendedAttribute($script:GeoraePlanArtifactRetentionStageEaName);$tokenRaw=[Text.UTF8Encoding]::new($false,$true).GetString($tokenBytes);$token=$tokenRaw|ConvertFrom-Json -ErrorAction Stop
    if($tokenRaw -cne (ConvertTo-GeoraePlanArtifactRetentionStrictJson $token)){throw 'Stage provisioning token is noncanonical.'};Assert-GeoraePlanArtifactRetentionStageProvisioningToken $token $id $parent.ParentId $stage
    $records=@($pin.NativeHandle.EnumerateAndOpenChildren($pin.Path,'source'));try{if($records.Count -ne 1 -or $records[0].Name -cne '.georaeplan-producer-stage.json' -or $records[0].Entry.EntryIdentity.IsDirectory){throw 'Stage dry-run child set is not exact.'};$manifestLease=ConvertFrom-GeoraePlanArtifactRetentionNativeChild $pin $records[0];$records[0].Entry=$null}finally{foreach($record in $records){if($null -ne $record.Entry){$record.Entry.Dispose()}}}
    Assert-GeoraePlanArtifactRetentionPrivateFileAcl $manifestLease.Path;$manifest=Read-GeoraePlanArtifactRetentionPinnedStrictJson $manifestLease 'stage manifest';$expected=[ordered]@{schemaVersion=1;kind='georaeplan-artifact-producer-stage-v1';artifactId=$id;stagePath=$pin.Path;stagePhysicalPath=$pin.PhysicalPath;stageVolumeSerialNumber=$pin.VolumeSerialNumber;stageFileId=$pin.FileId;createdAtUtc=[string]$token.createdAtUtc};Assert-GeoraePlanArtifactRetentionExactJson $manifest $expected 'stage manifest'
   }finally{Close-GeoraePlanArtifactRetentionPinned $manifestLease;Close-GeoraePlanArtifactRetentionPinned $pin}
  }
  Write-Output "artifact_retention_stage=DRY_RUN action=$(if(Test-Path -LiteralPath $stage){'would_resume'}else{'would_create'}) artifact_id=$id"
  return
 }
 $lease=Enter-GeoraePlanArtifactRetentionProducerLease $parent.LeasePath
 try {
   Assert-GeoraePlanArtifactRetentionParentUnchanged $parent $parent.MarkerSha256 $lease
  if($TestFaultInjection -eq 'MutateBootstrapBeforeStageCreate'){Add-Content -LiteralPath $parent.BootstrapLease.Path -Value ' ';throw 'Injected bootstrap mutation.'}
  $created=$false
   if(Test-Path -LiteralPath $stage){$pin=Open-GeoraePlanArtifactRetentionRelativeEntry $parent.Identity $expectedName $true;Assert-GeoraePlanArtifactRetentionPrivateAcl $pin.Path;$tokenBytes=$pin.NativeHandle.ReadExtendedAttribute($script:GeoraePlanArtifactRetentionStageEaName);$tokenRaw=[Text.UTF8Encoding]::new($false,$true).GetString($tokenBytes);$token=$tokenRaw|ConvertFrom-Json -ErrorAction Stop;if($tokenRaw -cne (ConvertTo-GeoraePlanArtifactRetentionStrictJson $token)){throw 'Stage provisioning token is noncanonical.'};Assert-GeoraePlanArtifactRetentionStageProvisioningToken $token $id $parent.ParentId $stage}
  else{$token=[ordered]@{schemaVersion=1;kind='georaeplan-artifact-stage-token-v1';artifactId=$id;parentId=$parent.ParentId;stagePath=$stage;createdAtUtc=[DateTimeOffset]::UtcNow.ToString('o')};$tokenBytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-GeoraePlanArtifactRetentionStrictJson $token));$pin=New-GeoraePlanArtifactRetentionProvisionedRelativeDirectory $parent.Identity $expectedName $script:GeoraePlanArtifactRetentionStageEaName $tokenBytes;$created=$true}
  try{
   Assert-GeoraePlanArtifactRetentionPinned $pin|Out-Null
    $manifestPath=Join-Path $pin.Path '.georaeplan-producer-stage.json'
    if(-not $created){$existingNames=@(Get-ChildItem -LiteralPath $pin.Path -Force|ForEach-Object{$_.Name});if($existingNames.Count -gt 1 -or ($existingNames.Count -eq 1 -and $existingNames[0] -cne '.georaeplan-producer-stage.json')){throw 'An unknown partial stage cannot be adopted.'}}
    $manifest=[ordered]@{schemaVersion=1;kind='georaeplan-artifact-producer-stage-v1';artifactId=$id;stagePath=$pin.Path;stagePhysicalPath=$pin.PhysicalPath;stageVolumeSerialNumber=$pin.VolumeSerialNumber;stageFileId=$pin.FileId;createdAtUtc=[string]$token.createdAtUtc}
    $manifestBytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-GeoraePlanArtifactRetentionStrictJson $manifest));$manifestLease=Write-GeoraePlanArtifactRetentionRelativeCreateNewBytes $pin '.georaeplan-producer-stage.json' $manifestBytes
    try{Assert-GeoraePlanArtifactRetentionExactJson (Read-GeoraePlanArtifactRetentionPinnedStrictJson $manifestLease 'stage manifest') $manifest 'stage manifest'}finally{Close-GeoraePlanArtifactRetentionPinned $manifestLease}
   Test-GeoraePlanArtifactRetentionFault 'BeforeHandleBoundPublish' $TestFaultInjection
   Assert-GeoraePlanArtifactRetentionPinned $pin|Out-Null
   Write-Output "artifact_retention_stage=APPLIED action=$(if($created){'created'}else{'already_valid'}) artifact_id=$id"
  }finally{Close-GeoraePlanArtifactRetentionPinned $pin}
 }finally{$lease.Dispose()}
} finally {Close-GeoraePlanArtifactRetentionParent $parent}
