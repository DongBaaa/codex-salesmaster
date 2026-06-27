[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateCsvPath,

    [string]$OutputDirectory = '',
    [string]$LinuxSshHost = '192.168.0.199',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshUser = 'itw',
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteOpsDirectory = '/srv/georaeplan/ops',
    [ValidateSet('Rollback', 'Commit')]
    [string]$PatchMode = 'Rollback',
    [switch]$ValidateAgainstLinuxPc,
    [ValidateRange(0, 100000)]
    [int]$ExpectedApprovedMappingCount = 0,
    [ValidateRange(0, 100000)]
    [int]$ExpectedReadyMappingCount = 0
)

$ErrorActionPreference = 'Stop'

function Resolve-SshExecutable {
    $windowsSsh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (Test-Path -LiteralPath $windowsSsh) {
        return $windowsSsh
    }

    $ssh = Get-Command ssh -ErrorAction SilentlyContinue
    if ($null -ne $ssh) {
        return $ssh.Source
    }

    throw 'ssh executable was not found.'
}

function Resolve-ProjectRoot {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    }

    return (Get-Location).Path
}

function ConvertTo-Base64Utf8 {
    param([string]$Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Value -replace "`r`n", "`n")))
}

function ConvertTo-SqlLiteral {
    param([string]$Value)
    return "'" + ($Value -replace "'", "''") + "'"
}

function Get-CsvValue {
    param(
        [Parameter(Mandatory = $true)]$Row,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property) {
            return [string]$property.Value
        }
    }

    return ''
}

function Test-GuidText {
    param([string]$Value)
    $ignored = [Guid]::Empty
    return [Guid]::TryParse($Value, [ref]$ignored)
}

function Test-ApprovalDecision {
    param([string]$Value)
    $normalized = ([string]$Value).Trim()
    $koreanApprove = ([string][char]0xC2B9) + ([string][char]0xC778)
    return $normalized -eq 'Approve' -or $normalized -eq 'Approved' -or $normalized -eq $koreanApprove
}

function Assert-DatabaseName {
    param([string]$Database)
    if ([string]::IsNullOrWhiteSpace($Database) -or $Database -notmatch '^[A-Za-z0-9_]+$') {
        throw "Invalid database name: $Database"
    }
}

function New-ValuesClause {
    param([object[]]$Mappings)

    $valueLines = foreach ($mapping in $Mappings) {
        "    ({0}::uuid, {1}::bigint, {2}::uuid)" -f `
            (ConvertTo-SqlLiteral $mapping.ProfileId),
            ([int64]$mapping.TemplateOrdinal),
            (ConvertTo-SqlLiteral $mapping.ApprovedItemId)
    }

    return ($valueLines -join ",`n")
}

function Invoke-RemoteBash {
    param([string]$Script)

    if (-not (Test-Path -LiteralPath $LinuxSshKeyPath)) {
        throw "Linux SSH key was not found: $LinuxSshKeyPath"
    }

    $scriptBase64 = ConvertTo-Base64Utf8 $Script
    $remoteCommand = "echo $scriptBase64 | base64 -d | bash"
    $target = "$LinuxSshUser@$LinuxSshHost"
    $output = & $script:SshExecutable `
        -p $LinuxSshPort `
        -i $LinuxSshKeyPath `
        -o BatchMode=yes `
        -o StrictHostKeyChecking=accept-new `
        -o ConnectTimeout=10 `
        $target `
        $remoteCommand 2>&1
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
    if ($exitCode -ne 0) {
        throw "remote command failed exit=$exitCode; $text"
    }

    return $text
}

function Invoke-RemotePsqlCsv {
    param(
        [string]$Database,
        [string]$Sql
    )

    Assert-DatabaseName -Database $Database
    $sqlBase64 = ConvertTo-Base64Utf8 $Sql
    $remoteScript = @"
set -euo pipefail
cd '$RemoteOpsDirectory'
set -a
. ./.env
set +a
DBUSER=`${POSTGRES_USER:-georaeplan}
echo $sqlBase64 | base64 -d | docker compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "`$DBUSER" -d '$Database' -f -
"@

    return Invoke-RemoteBash -Script $remoteScript
}

function New-ValidationSql {
    param([object[]]$Mappings)

    $valuesClause = New-ValuesClause -Mappings $Mappings
    return @"
copy (
with approved(profile_id, template_ordinal, approved_item_id) as (
  values
$valuesClause
), profile_matches as (
  select a.profile_id,
         a.template_ordinal,
         a.approved_item_id,
         p."Id" is not null as profile_exists,
         coalesce(p."IsDeleted", false) as profile_deleted,
         coalesce(p."IsActive", false) as profile_active,
         x.elem,
         coalesce(x.elem->>'CatalogItemId', x.elem->>'catalogItemId') as current_item_id_text,
         case when coalesce(x.elem->>'CatalogItemId', x.elem->>'catalogItemId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              then coalesce(x.elem->>'CatalogItemId', x.elem->>'catalogItemId')::uuid
              else null
         end as current_item_id,
         current_item."Id" is not null as current_item_exists,
         coalesce(current_item."IsDeleted", false) as current_item_deleted,
         approved_item."Id" is not null as approved_item_exists,
         coalesce(approved_item."IsDeleted", false) as approved_item_deleted,
         case when jsonb_typeof(coalesce(x.elem->'IncludedAssetIds', x.elem->'includedAssetIds')) = 'array'
              then coalesce(x.elem->'IncludedAssetIds', x.elem->'includedAssetIds')
              else '[]'::jsonb
         end as included_assets_json
  from approved a
  left join "RentalBillingProfiles" p on p."Id" = a.profile_id
  left join lateral jsonb_array_elements(
    case when p."BillingTemplateJson" is not null and btrim(p."BillingTemplateJson") <> ''
         then p."BillingTemplateJson"::jsonb
         else '[]'::jsonb
    end
  ) with ordinality as x(elem, ord) on x.ord = a.template_ordinal
  left join "Items" current_item on current_item."Id" = case when coalesce(x.elem->>'CatalogItemId', x.elem->>'catalogItemId') ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                                            then coalesce(x.elem->>'CatalogItemId', x.elem->>'catalogItemId')::uuid
                                                            else null
                                                       end
  left join "Items" approved_item on approved_item."Id" = a.approved_item_id
), asset_counts as (
  select pm.profile_id,
         pm.template_ordinal,
         count(distinct asset_id_text) as included_asset_count,
         count(distinct asset."Id") as found_asset_count,
         count(distinct asset."ItemId") filter (
           where asset."ItemId" is not null
             and not asset."IsDeleted"
             and item."Id" is not null
             and not item."IsDeleted") as active_asset_item_count,
         bool_or(asset."ItemId" = pm.approved_item_id and not asset."IsDeleted" and item."Id" is not null and not item."IsDeleted") as approved_item_linked_to_included_asset
  from profile_matches pm
  left join lateral jsonb_array_elements_text(pm.included_assets_json) asset_id_text on true
  left join "RentalAssets" asset on asset."Id" = case when asset_id_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                                                      then asset_id_text::uuid
                                                      else null
                                                 end
  left join "Items" item on item."Id" = asset."ItemId"
  group by pm.profile_id, pm.template_ordinal
), classified as (
  select pm.profile_id,
         pm.template_ordinal,
         pm.approved_item_id,
         pm.current_item_id_text,
         coalesce(ac.included_asset_count, 0) as included_asset_count,
         coalesce(ac.found_asset_count, 0) as found_asset_count,
         coalesce(ac.active_asset_item_count, 0) as active_asset_item_count,
         coalesce(ac.approved_item_linked_to_included_asset, false) as approved_item_linked_to_included_asset,
         case
           when not pm.profile_exists then 'profile_not_found'
           when pm.profile_deleted then 'profile_deleted'
           when not pm.profile_active then 'profile_inactive'
           when pm.elem is null then 'template_ordinal_not_found'
           when not pm.approved_item_exists then 'approved_item_not_found'
           when pm.approved_item_deleted then 'approved_item_deleted'
           when pm.current_item_id = pm.approved_item_id then 'already_fixed'
           when pm.current_item_id is not null and pm.current_item_exists and not pm.current_item_deleted then 'current_reference_is_valid_now'
           else 'ready'
         end as validation_status
  from profile_matches pm
  left join asset_counts ac on ac.profile_id = pm.profile_id and ac.template_ordinal = pm.template_ordinal
)
select profile_id as "ProfileId",
       template_ordinal as "TemplateOrdinal",
       approved_item_id as "ApprovedItemId",
       current_item_id_text as "CurrentItemId",
       included_asset_count as "IncludedAssetCount",
       found_asset_count as "FoundAssetCount",
       active_asset_item_count as "ActiveAssetItemCount",
       approved_item_linked_to_included_asset as "ApprovedItemLinkedToIncludedAsset",
       validation_status as "ValidationStatus"
from classified
order by "ValidationStatus", "ProfileId", "TemplateOrdinal"
) to stdout with csv header;
"@
}

function New-PatchSql {
    param(
        [string]$Database,
        [object[]]$Mappings,
        [string]$PatchId,
        [string]$Mode
    )

    Assert-DatabaseName -Database $Database
    $valuesClause = New-ValuesClause -Mappings $Mappings
    $expectedApprovedMappingCount = @($Mappings).Count
    $expectedTargetProfileCount = @($Mappings | ForEach-Object { [string]$_.ProfileId } | Sort-Object -Unique).Count
    $terminalStatement = if ($Mode -eq 'Commit') { 'commit;' } else { 'rollback;' }
    $modeWarning = if ($Mode -eq 'Commit') {
        '-- PatchMode=Commit: clone/test DB verification is expected before production maintenance.'
    }
    else {
        '-- PatchMode=Rollback: this script verifies the update path but rolls back by default.'
    }

    return @"
-- Generated by tools/linux/New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1
-- Database: $Database
-- PatchId: $PatchId
$modeWarning
-- Run this SQL against a cloned/test database first. Do not run directly on live data without a fresh backup and approved mapping evidence.

begin;

create table if not exists "RentalBillingTemplateItemReferenceRepairBackups" (
    "PatchId" text not null,
    "DatabaseName" text not null,
    "ProfileId" uuid not null,
    "BillingTemplateJsonBefore" text not null,
    "CreatedAtUtc" timestamptz not null default timezone('utc', now()),
    primary key ("PatchId", "DatabaseName", "ProfileId")
);


create temporary table "RentalBillingTemplateItemReferenceRepairCounts" on commit drop as
with approved(profile_id, template_ordinal, approved_item_id) as (
  values
$valuesClause
), target_profiles as (
  select p."Id" as profile_id,
         p."BillingTemplateJson" as billing_template_json_before,
         jsonb_agg(
           case when a.profile_id is not null then
             case
               when x.elem ? 'CatalogItemId' then jsonb_set(x.elem, '{CatalogItemId}', to_jsonb(a.approved_item_id::text), true)
               when x.elem ? 'catalogItemId' then jsonb_set(x.elem, '{catalogItemId}', to_jsonb(a.approved_item_id::text), true)
               else jsonb_set(x.elem, '{CatalogItemId}', to_jsonb(a.approved_item_id::text), true)
             end
           else x.elem
           end
           order by x.ord
         )::text as billing_template_json_after
  from "RentalBillingProfiles" p
  join (select distinct profile_id from approved) selected_profiles on selected_profiles.profile_id = p."Id"
  cross join lateral jsonb_array_elements(p."BillingTemplateJson"::jsonb) with ordinality as x(elem, ord)
  left join approved a on a.profile_id = p."Id" and a.template_ordinal = x.ord
  where not p."IsDeleted"
    and p."IsActive"
  group by p."Id", p."BillingTemplateJson"
), backup_insert as (
  insert into "RentalBillingTemplateItemReferenceRepairBackups" (
      "PatchId", "DatabaseName", "ProfileId", "BillingTemplateJsonBefore")
  select '$PatchId', '$Database', profile_id, billing_template_json_before
  from target_profiles
  on conflict ("PatchId", "DatabaseName", "ProfileId") do nothing
  returning "ProfileId"
), updated as (
  update "RentalBillingProfiles" p
  set "BillingTemplateJson" = target_profiles.billing_template_json_after,
      "UpdatedAtUtc" = timezone('utc', now()),
      "Revision" = p."Revision" + 1
  from target_profiles
  where p."Id" = target_profiles.profile_id
    and p."BillingTemplateJson" = target_profiles.billing_template_json_before
  returning p."Id"
)
select
  (select count(*) from approved) as approved_mapping_count,
  (select count(*) from target_profiles) as target_profile_count,
  (select count(*) from backup_insert) as inserted_backup_count,
  (select count(*) from updated) as updated_profile_count;

do `$repair_assert`$
declare
  repair_counts record;
begin
  select * into repair_counts from "RentalBillingTemplateItemReferenceRepairCounts";
  if repair_counts.approved_mapping_count <> $expectedApprovedMappingCount then
    raise exception 'approved_mapping_count mismatch: expected %, actual %', $expectedApprovedMappingCount, repair_counts.approved_mapping_count;
  end if;
  if repair_counts.target_profile_count <> $expectedTargetProfileCount then
    raise exception 'target_profile_count mismatch: expected %, actual %', $expectedTargetProfileCount, repair_counts.target_profile_count;
  end if;
  if repair_counts.inserted_backup_count <> $expectedTargetProfileCount then
    raise exception 'inserted_backup_count mismatch: expected %, actual %', $expectedTargetProfileCount, repair_counts.inserted_backup_count;
  end if;
  if repair_counts.updated_profile_count <> $expectedTargetProfileCount then
    raise exception 'updated_profile_count mismatch: expected %, actual %', $expectedTargetProfileCount, repair_counts.updated_profile_count;
  end if;
end
`$repair_assert`$;

select * from "RentalBillingTemplateItemReferenceRepairCounts";

$terminalStatement
"@
}

$projectRoot = Resolve-ProjectRoot
$resolvedCandidateCsvPath = (Resolve-Path -LiteralPath $CandidateCsvPath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "artifacts\rental-template-item-reference-repair-plan\$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$inputRows = @(Import-Csv -LiteralPath $resolvedCandidateCsvPath)
if ($inputRows.Count -eq 0) {
    throw "Candidate CSV has no rows: $resolvedCandidateCsvPath"
}

$reviewTemplateRows = foreach ($row in $inputRows) {
    [pscustomobject]@{
        Database = Get-CsvValue -Row $row -Names @('Database')
        ProfileId = Get-CsvValue -Row $row -Names @('ProfileId')
        TemplateOrdinal = Get-CsvValue -Row $row -Names @('TemplateOrdinal')
        Reason = Get-CsvValue -Row $row -Names @('Reason')
        NameCandidateCount = Get-CsvValue -Row $row -Names @('NameCandidateCount')
        NameMatchClass = Get-CsvValue -Row $row -Names @('NameMatchClass')
        IncludedAssetCount = Get-CsvValue -Row $row -Names @('IncludedAssetCount')
        ActiveAssetItemCount = Get-CsvValue -Row $row -Names @('ActiveAssetItemCount')
        AssetMatchClass = Get-CsvValue -Row $row -Names @('AssetMatchClass')
        ProposedItemId = Get-CsvValue -Row $row -Names @('ProposedItemId')
        ProposedSource = Get-CsvValue -Row $row -Names @('ProposedSource')
        ProposedConfidence = Get-CsvValue -Row $row -Names @('ProposedConfidence')
        ReviewDecision = Get-CsvValue -Row $row -Names @('ReviewDecision', 'Decision')
        ApprovedItemId = Get-CsvValue -Row $row -Names @('ApprovedItemId', 'NewItemId', 'TargetItemId')
        Reviewer = Get-CsvValue -Row $row -Names @('Reviewer')
        ReviewNote = Get-CsvValue -Row $row -Names @('ReviewNote', 'Note')
    }
}

$reviewTemplatePath = Join-Path $OutputDirectory 'review-template.csv'
$reviewTemplateRows | Export-Csv -LiteralPath $reviewTemplatePath -Encoding UTF8 -NoTypeInformation

$errors = New-Object System.Collections.Generic.List[object]
$approvedMappings = New-Object System.Collections.Generic.List[object]
$seenKeys = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($row in $reviewTemplateRows) {
    $decision = ([string]$row.ReviewDecision).Trim()
    $approvedItemId = ([string]$row.ApprovedItemId).Trim()
    $hasApprovalIntent = (Test-ApprovalDecision $decision) -or -not [string]::IsNullOrWhiteSpace($approvedItemId)
    if (-not $hasApprovalIntent) {
        continue
    }

    $database = ([string]$row.Database).Trim()
    $profileId = ([string]$row.ProfileId).Trim()
    $templateOrdinalText = ([string]$row.TemplateOrdinal).Trim()
    $templateOrdinal = 0L
    $rowErrors = New-Object System.Collections.Generic.List[string]

    if (-not (Test-ApprovalDecision $decision)) {
        $rowErrors.Add('ReviewDecision must be Approve/Approved/Korean-approve when ApprovedItemId is present.') | Out-Null
    }
    if ($database -notmatch '^[A-Za-z0-9_]+$') {
        $rowErrors.Add('Database is missing or invalid.') | Out-Null
    }
    if (-not (Test-GuidText $profileId)) {
        $rowErrors.Add('ProfileId is missing or invalid.') | Out-Null
    }
    if (-not [Int64]::TryParse($templateOrdinalText, [ref]$templateOrdinal) -or $templateOrdinal -le 0) {
        $rowErrors.Add('TemplateOrdinal must be a positive integer.') | Out-Null
    }
    if (-not (Test-GuidText $approvedItemId)) {
        $rowErrors.Add('ApprovedItemId is missing or invalid.') | Out-Null
    }

    $key = "$database|$profileId|$templateOrdinal"
    if ($rowErrors.Count -eq 0 -and -not $seenKeys.Add($key)) {
        $rowErrors.Add('Duplicate approved mapping for the same Database/ProfileId/TemplateOrdinal.') | Out-Null
    }

    if ($rowErrors.Count -gt 0) {
        $errors.Add([pscustomobject]@{
            Database = $database
            ProfileId = $profileId
            TemplateOrdinal = $templateOrdinalText
            ApprovedItemId = $approvedItemId
            Error = ($rowErrors -join ' ')
        }) | Out-Null
        continue
    }

    $approvedMappings.Add([pscustomobject]@{
        Database = $database
        ProfileId = ([Guid]$profileId).ToString()
        TemplateOrdinal = $templateOrdinal
        ApprovedItemId = ([Guid]$approvedItemId).ToString()
        ReviewDecision = $decision
        Reviewer = [string]$row.Reviewer
        ReviewNote = [string]$row.ReviewNote
    }) | Out-Null
}

$errorPath = Join-Path $OutputDirectory 'mapping-errors.csv'
if ($errors.Count -gt 0) {
    $errors | Export-Csv -LiteralPath $errorPath -Encoding UTF8 -NoTypeInformation
    throw "Approved mapping validation failed. See $errorPath"
}

$approvedMappingsPath = Join-Path $OutputDirectory 'approved-mappings.normalized.csv'
$approvedMappings | Export-Csv -LiteralPath $approvedMappingsPath -Encoding UTF8 -NoTypeInformation

$validationRows = New-Object System.Collections.Generic.List[object]
$patchFiles = New-Object System.Collections.Generic.List[string]
$patchId = 'rental-template-item-reference-repair-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$repairPlanGateErrors = New-Object System.Collections.Generic.List[string]
$readyMappingCount = 0
if ($ExpectedApprovedMappingCount -gt 0 -and $approvedMappings.Count -ne $ExpectedApprovedMappingCount) {
    $repairPlanGateErrors.Add("Approved mapping count mismatch. expected=$ExpectedApprovedMappingCount actual=$($approvedMappings.Count)") | Out-Null
}
if ($ExpectedReadyMappingCount -gt 0 -and -not $ValidateAgainstLinuxPc.IsPresent) {
    $repairPlanGateErrors.Add('ExpectedReadyMappingCount requires -ValidateAgainstLinuxPc.') | Out-Null
}

if ($approvedMappings.Count -gt 0 -and $ValidateAgainstLinuxPc.IsPresent -and $repairPlanGateErrors.Count -eq 0) {
    $script:SshExecutable = Resolve-SshExecutable
    foreach ($databaseGroup in ($approvedMappings | Group-Object Database)) {
        $database = $databaseGroup.Name
        Assert-DatabaseName -Database $database
        $sql = New-ValidationSql -Mappings @($databaseGroup.Group)
        $csvText = Invoke-RemotePsqlCsv -Database $database -Sql $sql
        $validationPath = Join-Path $OutputDirectory ("validation-$database.csv")
        $csvText | Set-Content -LiteralPath $validationPath -Encoding UTF8
        foreach ($validationRow in @($csvText | ConvertFrom-Csv)) {
            $validationRows.Add([pscustomobject]@{
                Database = $database
                ProfileId = $validationRow.ProfileId
                TemplateOrdinal = $validationRow.TemplateOrdinal
                ApprovedItemId = $validationRow.ApprovedItemId
                CurrentItemId = $validationRow.CurrentItemId
                IncludedAssetCount = $validationRow.IncludedAssetCount
                FoundAssetCount = $validationRow.FoundAssetCount
                ActiveAssetItemCount = $validationRow.ActiveAssetItemCount
                ApprovedItemLinkedToIncludedAsset = $validationRow.ApprovedItemLinkedToIncludedAsset
                ValidationStatus = $validationRow.ValidationStatus
            }) | Out-Null
        }
    }

    $readyMappingCount = @($validationRows | Where-Object { $_.ValidationStatus -eq 'ready' }).Count
    if ($ExpectedReadyMappingCount -gt 0 -and $readyMappingCount -ne $ExpectedReadyMappingCount) {
        $repairPlanGateErrors.Add("Ready mapping count mismatch. expected=$ExpectedReadyMappingCount actual=$readyMappingCount") | Out-Null
    }

    if ($repairPlanGateErrors.Count -eq 0) {
        foreach ($databaseGroup in ($validationRows | Where-Object { $_.ValidationStatus -eq 'ready' } | Group-Object Database)) {
            $database = $databaseGroup.Name
            $readyMappings = foreach ($validationRow in $databaseGroup.Group) {
                [pscustomobject]@{
                    ProfileId = $validationRow.ProfileId
                    TemplateOrdinal = [int64]$validationRow.TemplateOrdinal
                    ApprovedItemId = $validationRow.ApprovedItemId
                }
            }

            $patchSql = New-PatchSql -Database $database -Mappings @($readyMappings) -PatchId $patchId -Mode $PatchMode
            $patchPath = Join-Path $OutputDirectory ("repair-$database-$($PatchMode.ToLowerInvariant()).sql")
            $patchSql | Set-Content -LiteralPath $patchPath -Encoding UTF8
            $patchFiles.Add($patchPath) | Out-Null
        }
    }
}

$validationSummaryPath = Join-Path $OutputDirectory 'validation-summary.csv'
if ($validationRows.Count -gt 0) {
    $validationRows |
        Group-Object Database, ValidationStatus |
        ForEach-Object {
            $first = $_.Group[0]
            [pscustomobject]@{
                Database = $first.Database
                ValidationStatus = $first.ValidationStatus
                Count = $_.Count
            }
        } |
        Sort-Object Database, ValidationStatus |
        Export-Csv -LiteralPath $validationSummaryPath -Encoding UTF8 -NoTypeInformation
}
else {
    @([pscustomobject]@{
        Database = ''
        ValidationStatus = if ($approvedMappings.Count -eq 0) { 'no_approved_mappings' } else { 'not_validated' }
        Count = $approvedMappings.Count
    }) | Export-Csv -LiteralPath $validationSummaryPath -Encoding UTF8 -NoTypeInformation
}

$repairPlanGateStatus = if ($repairPlanGateErrors.Count -gt 0) { 'FAIL' } else { 'PASS' }
$repairPlanGateReportPath = Join-Path $OutputDirectory 'repair-plan-gate.md'
$repairPlanGateLines = New-Object System.Collections.Generic.List[string]
$repairPlanGateLines.Add('# Rental template item reference repair plan gate') | Out-Null
$repairPlanGateLines.Add('') | Out-Null
$repairPlanGateLines.Add(('- Created: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
$repairPlanGateLines.Add(('- Gate status: {0}' -f $repairPlanGateStatus)) | Out-Null
$repairPlanGateLines.Add(('- Approved mapping count: {0}' -f $approvedMappings.Count)) | Out-Null
$repairPlanGateLines.Add(('- Expected approved mapping count: {0}' -f $ExpectedApprovedMappingCount)) | Out-Null
$repairPlanGateLines.Add(('- Ready mapping count: {0}' -f $readyMappingCount)) | Out-Null
$repairPlanGateLines.Add(('- Expected ready mapping count: {0}' -f $ExpectedReadyMappingCount)) | Out-Null
$repairPlanGateLines.Add(('- ValidateAgainstLinuxPc: {0}' -f $ValidateAgainstLinuxPc.IsPresent)) | Out-Null
$repairPlanGateLines.Add('') | Out-Null
$repairPlanGateLines.Add('## Decision') | Out-Null
if ($repairPlanGateErrors.Count -eq 0) {
    $repairPlanGateLines.Add('- Repair plan gate passed for the selected expectations.') | Out-Null
}
else {
    foreach ($gateError in $repairPlanGateErrors) {
        $repairPlanGateLines.Add(('- {0}' -f $gateError)) | Out-Null
    }
}
Set-Content -LiteralPath $repairPlanGateReportPath -Value $repairPlanGateLines -Encoding UTF8

$readmePath = Join-Path $OutputDirectory 'README.md'
@(
    '# Rental template item reference repair plan',
    '',
    "- Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "- Candidate CSV: $resolvedCandidateCsvPath",
    "- Approved mapping count: $($approvedMappings.Count)",
    "- Expected approved mapping count: $ExpectedApprovedMappingCount",
    "- Ready mapping count: $readyMappingCount",
    "- Expected ready mapping count: $ExpectedReadyMappingCount",
    "- Repair plan gate status: $repairPlanGateStatus",
    "- Linux PC SELECT validation: $($ValidateAgainstLinuxPc.IsPresent)",
    "- Patch mode: $PatchMode",
    '',
    '## Files',
    '- `review-template.csv`: review template generated from input candidates.',
    '- `approved-mappings.normalized.csv`: normalized approved mappings only.',
    '- `validation-<db>.csv`: SELECT-only validation results when -ValidateAgainstLinuxPc is used.',
    '- `validation-summary.csv`: validation status summary.',
    '- `repair-plan-gate.md`: expected approved/ready count gate result.',
    '- `repair-<db>-rollback.sql` or `repair-<db>-commit.sql`: SQL generated only for ready mappings when the gate passes.',
    '',
    '## Safety rules',
    '1. Generate review-template.csv first and collect business approval before repair.',
    '2. Use -ValidateAgainstLinuxPc for SELECT-only validation before any SQL patch is trusted.',
    '3. Use -ExpectedApprovedMappingCount and -ExpectedReadyMappingCount before production repair.',
    '4. Generated SQL includes transaction-time assertions for approved, target profile, backup, and updated profile counts.',
    '5. Run generated SQL against a cloned/test database first.',
    '6. Do not run live repair without fresh backup, user approval, integrity recheck, and rental billing/invoice E2E verification.'
) | Set-Content -LiteralPath $readmePath -Encoding UTF8

Write-Host "rental_template_item_reference_repair_plan_output=$OutputDirectory"
Write-Host "review_template=$reviewTemplatePath"
Write-Host "approved_mappings=$approvedMappingsPath"
Write-Host "validation_summary=$validationSummaryPath"
Write-Host "repair_plan_gate_status=$repairPlanGateStatus"
Write-Host "repair_plan_gate_report=$repairPlanGateReportPath"
Write-Host "approved_mapping_count=$($approvedMappings.Count)"
Write-Host "ready_mapping_count=$readyMappingCount"
if ($patchFiles.Count -gt 0) {
    foreach ($patchFile in $patchFiles) {
        Write-Host "patch_sql=$patchFile"
    }
}
else {
    Write-Host 'patch_sql=none'
}


if ($repairPlanGateErrors.Count -gt 0) {
    throw "Repair plan gate failed: $($repairPlanGateErrors -join '; '). Report: $repairPlanGateReportPath"
}
