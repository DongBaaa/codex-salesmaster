[CmdletBinding()]
param(
    [string]$LinuxSshHost = '192.168.0.199',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshUser = 'itw',
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteOpsDirectory = '/srv/georaeplan/ops',
    [string[]]$Databases = @('georaeplan', 'georaeplan_itworld'),
    [string]$OutputDirectory = '',
    [switch]$IncludeSensitiveCandidateRows
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

function Invoke-RemoteBash {
    param([string]$Script)

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

function Get-CandidateSql {
    param([string]$Database)

    $sensitiveSelect = if ($IncludeSensitiveCandidateRows.IsPresent) {
        @'
    r."ProfileKey",
    r."CustomerName",
    r.display_item_name as "DisplayItemName",
    r.specification as "Specification",
    r.material_number as "MaterialNumber",
    r.item_id_text as "OriginalItemId",
'@
    }
    else {
        @'
    '' as "ProfileKey",
    '' as "CustomerName",
    '' as "DisplayItemName",
    '' as "Specification",
    '' as "MaterialNumber",
    '' as "OriginalItemId",
'@
    }

    return @"
copy (
with template_items as (
  select p."Id" profile_id,
         p."ProfileKey",
         p."CustomerName",
         elem,
         ord,
         coalesce(elem->>'CatalogItemId', elem->>'catalogItemId') item_id_text,
         coalesce(elem->>'DisplayItemName', elem->>'displayItemName', elem->>'ItemName', elem->>'itemName', '') display_item_name,
         coalesce(elem->>'Specification', elem->>'specification', '') specification,
         coalesce(elem->>'MaterialNumber', elem->>'materialNumber', '') material_number,
         case when jsonb_typeof(coalesce(elem->'IncludedAssetIds', elem->'includedAssetIds')) = 'array'
              then coalesce(elem->'IncludedAssetIds', elem->'includedAssetIds')
              else '[]'::jsonb
         end included_assets_json
  from "RentalBillingProfiles" p
  cross join lateral jsonb_array_elements(p."BillingTemplateJson"::jsonb) with ordinality as x(elem, ord)
  where not p."IsDeleted"
    and p."IsActive"
    and coalesce(trim(p."BillingTemplateJson"), '') <> ''
), normalized as (
  select *,
         regexp_replace(lower(coalesce(display_item_name,'')), '[[:space:][:punct:]]+', '', 'g') display_key,
         regexp_replace(lower(coalesce(specification,'')), '[[:space:][:punct:]]+', '', 'g') spec_key,
         regexp_replace(lower(coalesce(material_number,'')), '[[:space:][:punct:]]+', '', 'g') material_key,
         case when item_id_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              then item_id_text::uuid
              else null
         end item_id
  from template_items
), invalid_refs as (
  select n.*,
         case
           when n.item_id_text is null or btrim(n.item_id_text) = '' then 'ok'
           when n.item_id is null then 'invalid_item_id_text'
           when n.item_id = '00000000-0000-0000-0000-000000000000' then 'ok'
           when it."Id" is null then 'hard_missing_item'
           when it."IsDeleted" then 'deleted_item'
           else 'ok'
         end reason
  from normalized n
  left join "Items" it on it."Id" = n.item_id
), active_items as (
  select "Id",
         regexp_replace(lower(coalesce("NameOriginal",'')), '[[:space:][:punct:]]+', '', 'g') item_name_key,
         regexp_replace(lower(coalesce("SpecificationOriginal",'')), '[[:space:][:punct:]]+', '', 'g') item_spec_key,
         regexp_replace(lower(coalesce("MaterialNumber",'')), '[[:space:][:punct:]]+', '', 'g') item_material_key,
         regexp_replace(lower(coalesce("SerialNumber",'')), '[[:space:][:punct:]]+', '', 'g') item_serial_key
  from "Items"
  where not "IsDeleted"
), name_candidates as (
  select r.profile_id,
         r.ord,
         ai."Id" candidate_item_id,
         concat_ws(',',
           case when r.material_key <> '' and r.material_key = ai.item_material_key then 'material_exact' end,
           case when r.material_key <> '' and r.material_key = ai.item_serial_key then 'serial_exact' end,
           case when r.display_key <> '' and r.display_key = ai.item_name_key then 'display_name_exact' end,
           case when r.display_key <> '' and r.display_key = ai.item_spec_key then 'display_spec_exact' end,
           case when r.spec_key <> '' and r.spec_key = ai.item_spec_key then 'spec_exact' end,
           case when r.spec_key <> '' and r.spec_key = ai.item_name_key then 'spec_name_exact' end
         ) match_rules
  from invalid_refs r
  join active_items ai on (
       (r.material_key <> '' and (r.material_key = ai.item_material_key or r.material_key = ai.item_serial_key))
       or (r.display_key <> '' and (r.display_key = ai.item_name_key or r.display_key = ai.item_spec_key))
       or (r.spec_key <> '' and (r.spec_key = ai.item_spec_key or r.spec_key = ai.item_name_key))
  )
  where r.reason <> 'ok'
), name_candidate_counts as (
  select profile_id,
         ord,
         count(distinct candidate_item_id) candidate_count,
         min(candidate_item_id::text) as single_candidate_item_id,
         string_agg(distinct match_rules, ';' order by match_rules) match_rules
  from name_candidates
  group by profile_id, ord
), included_asset_ids as (
  select r.profile_id,
         r.ord,
         asset_id_text,
         case when asset_id_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              then asset_id_text::uuid
              else null
         end asset_id
  from invalid_refs r
  cross join lateral jsonb_array_elements_text(r.included_assets_json) asset_id_text
  where r.reason <> 'ok'
), asset_candidate_counts as (
  select r.profile_id,
         r.ord,
         count(distinct ia.asset_id) included_asset_count,
         count(distinct a."Id") found_asset_count,
          count(distinct a."ItemId") filter (
              where a."ItemId" is not null
                and not a."IsDeleted"
                and it."Id" is not null
                and not it."IsDeleted") active_asset_item_count,
          min(a."ItemId"::text) filter (
              where a."ItemId" is not null
                and not a."IsDeleted"
                and it."Id" is not null
                and not it."IsDeleted") active_asset_item_id
  from invalid_refs r
  left join included_asset_ids ia on ia.profile_id = r.profile_id and ia.ord = r.ord
  left join "RentalAssets" a on a."Id" = ia.asset_id
  left join "Items" it on it."Id" = a."ItemId"
  where r.reason <> 'ok'
  group by r.profile_id, r.ord
), classified as (
  select '$Database' as "Database",
         r.profile_id,
         r.ord,
         r.reason,
$sensitiveSelect
         coalesce(nc.candidate_count, 0) as name_candidate_count,
         coalesce(nc.single_candidate_item_id, '') as single_name_candidate_item_id,
         coalesce(nc.match_rules, '') as name_match_rules,
         case
           when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(material_exact|serial_exact)' then 'auto_match_strong_identifier'
           when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(display_name_exact|display_spec_exact|spec_exact|spec_name_exact)' then 'auto_match_unique_name_or_spec'
           when coalesce(nc.candidate_count, 0) > 1 then 'ambiguous_multiple_candidates'
           else 'no_active_candidate'
         end as name_match_class,
         coalesce(ac.included_asset_count, 0) as included_asset_count,
         coalesce(ac.found_asset_count, 0) as found_asset_count,
         coalesce(ac.active_asset_item_count, 0) as active_asset_item_count,
         coalesce(ac.active_asset_item_id, '') as single_active_asset_item_id,
         case
           when coalesce(ac.included_asset_count, 0) = 0 then 'no_included_assets'
           when coalesce(ac.found_asset_count, 0) = 0 then 'included_assets_not_found'
           when coalesce(ac.active_asset_item_count, 0) = 1 then 'single_active_item_from_included_assets'
            when coalesce(ac.active_asset_item_count, 0) > 1 then 'multiple_active_items_from_included_assets'
            else 'included_assets_without_active_item'
          end as asset_match_class,
          case
            when coalesce(ac.active_asset_item_count, 0) = 1 then coalesce(ac.active_asset_item_id, '')
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(material_exact|serial_exact)' then coalesce(nc.single_candidate_item_id, '')
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(display_name_exact|display_spec_exact|spec_exact|spec_name_exact)' then coalesce(nc.single_candidate_item_id, '')
            else ''
          end as proposed_item_id,
          case
            when coalesce(ac.active_asset_item_count, 0) = 1 then 'included_asset_single_active_item'
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(material_exact|serial_exact)' then 'strong_identifier_single_name_candidate'
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(display_name_exact|display_spec_exact|spec_exact|spec_name_exact)' then 'unique_name_or_spec_candidate'
            else 'none'
          end as proposed_source,
          case
            when coalesce(ac.active_asset_item_count, 0) = 1 then 'review_required_asset_based'
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(material_exact|serial_exact)' then 'review_required_identifier_based'
            when coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(display_name_exact|display_spec_exact|spec_exact|spec_name_exact)' then 'review_required_name_based'
            else 'manual_review_required'
          end as proposed_confidence
  from invalid_refs r
  left join name_candidate_counts nc on nc.profile_id = r.profile_id and nc.ord = r.ord
  left join asset_candidate_counts ac on ac.profile_id = r.profile_id and ac.ord = r.ord
  where r.reason <> 'ok'
)
select "Database",
       profile_id as "ProfileId",
       ord as "TemplateOrdinal",
       reason as "Reason",
       "ProfileKey",
       "CustomerName",
       "DisplayItemName",
       "Specification",
       "MaterialNumber",
       "OriginalItemId",
       name_candidate_count as "NameCandidateCount",
       name_match_rules as "NameMatchRules",
       name_match_class as "NameMatchClass",
       included_asset_count as "IncludedAssetCount",
       found_asset_count as "FoundAssetCount",
       active_asset_item_count as "ActiveAssetItemCount",
       asset_match_class as "AssetMatchClass",
       proposed_item_id as "ProposedItemId",
       proposed_source as "ProposedSource",
       proposed_confidence as "ProposedConfidence"
from classified
order by "Database", "Reason", "NameMatchClass", "AssetMatchClass", "ProposedSource", "ProfileId", "TemplateOrdinal"
) to stdout with csv header;
"@
}

function Get-ManualReviewDetailSql {
    param([string]$Database)

    $sensitiveDetailSelect = if ($IncludeSensitiveCandidateRows.IsPresent) {
        @'
       r."ProfileKey",
       r."CustomerName",
       r.display_item_name as "DisplayItemName",
       candidate_item_name as "CandidateItemName",
       candidate_specification as "CandidateSpecification",
       candidate_material_number as "CandidateMaterialNumber",
       candidate_serial_number as "CandidateSerialNumber"
'@
    }
    else {
        @'
       '' as "ProfileKey",
       '' as "CustomerName",
       '' as "DisplayItemName",
       '' as "CandidateItemName",
       '' as "CandidateSpecification",
       '' as "CandidateMaterialNumber",
       '' as "CandidateSerialNumber"
'@
    }

    return @"
copy (
with template_items as (
  select p."Id" profile_id,
         p."ProfileKey",
         p."CustomerName",
         elem,
         ord,
         coalesce(elem->>'CatalogItemId', elem->>'catalogItemId') item_id_text,
         coalesce(elem->>'DisplayItemName', elem->>'displayItemName', elem->>'ItemName', elem->>'itemName', '') display_item_name,
         coalesce(elem->>'Specification', elem->>'specification', '') specification,
         coalesce(elem->>'MaterialNumber', elem->>'materialNumber', '') material_number,
         case when jsonb_typeof(coalesce(elem->'IncludedAssetIds', elem->'includedAssetIds')) = 'array'
              then coalesce(elem->'IncludedAssetIds', elem->'includedAssetIds')
              else '[]'::jsonb
         end included_assets_json
  from "RentalBillingProfiles" p
  cross join lateral jsonb_array_elements(p."BillingTemplateJson"::jsonb) with ordinality as x(elem, ord)
  where not p."IsDeleted"
    and p."IsActive"
    and coalesce(trim(p."BillingTemplateJson"), '') <> ''
), normalized as (
  select *,
         regexp_replace(lower(coalesce(display_item_name,'')), '[[:space:][:punct:]]+', '', 'g') display_key,
         regexp_replace(lower(coalesce(specification,'')), '[[:space:][:punct:]]+', '', 'g') spec_key,
         regexp_replace(lower(coalesce(material_number,'')), '[[:space:][:punct:]]+', '', 'g') material_key,
         case when item_id_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              then item_id_text::uuid
              else null
         end item_id
  from template_items
), invalid_refs as (
  select n.*,
         case
           when n.item_id_text is null or btrim(n.item_id_text) = '' then 'ok'
           when n.item_id is null then 'invalid_item_id_text'
           when n.item_id = '00000000-0000-0000-0000-000000000000' then 'ok'
           when it."Id" is null then 'hard_missing_item'
           when it."IsDeleted" then 'deleted_item'
           else 'ok'
         end reason
  from normalized n
  left join "Items" it on it."Id" = n.item_id
), active_items as (
  select "Id",
         "NameOriginal",
         "SpecificationOriginal",
         "MaterialNumber",
         "SerialNumber",
         regexp_replace(lower(coalesce("NameOriginal",'')), '[[:space:][:punct:]]+', '', 'g') item_name_key,
         regexp_replace(lower(coalesce("SpecificationOriginal",'')), '[[:space:][:punct:]]+', '', 'g') item_spec_key,
         regexp_replace(lower(coalesce("MaterialNumber",'')), '[[:space:][:punct:]]+', '', 'g') item_material_key,
         regexp_replace(lower(coalesce("SerialNumber",'')), '[[:space:][:punct:]]+', '', 'g') item_serial_key
  from "Items"
  where not "IsDeleted"
), name_candidates as (
  select r.profile_id,
         r.ord,
         ai."Id" candidate_item_id,
         ai."NameOriginal" candidate_item_name,
         ai."SpecificationOriginal" candidate_specification,
         ai."MaterialNumber" candidate_material_number,
         ai."SerialNumber" candidate_serial_number,
         concat_ws(',',
           case when r.material_key <> '' and r.material_key = ai.item_material_key then 'material_exact' end,
           case when r.material_key <> '' and r.material_key = ai.item_serial_key then 'serial_exact' end,
           case when r.display_key <> '' and r.display_key = ai.item_name_key then 'display_name_exact' end,
           case when r.display_key <> '' and r.display_key = ai.item_spec_key then 'display_spec_exact' end,
           case when r.spec_key <> '' and r.spec_key = ai.item_spec_key then 'spec_exact' end,
           case when r.spec_key <> '' and r.spec_key = ai.item_name_key then 'spec_name_exact' end
         ) match_rules
  from invalid_refs r
  join active_items ai on (
       (r.material_key <> '' and (r.material_key = ai.item_material_key or r.material_key = ai.item_serial_key))
       or (r.display_key <> '' and (r.display_key = ai.item_name_key or r.display_key = ai.item_spec_key))
       or (r.spec_key <> '' and (r.spec_key = ai.item_spec_key or r.spec_key = ai.item_name_key))
  )
  where r.reason <> 'ok'
), name_candidate_counts as (
  select profile_id,
         ord,
         count(distinct candidate_item_id) candidate_count,
         string_agg(distinct match_rules, ';' order by match_rules) match_rules
  from name_candidates
  group by profile_id, ord
), included_asset_ids as (
  select r.profile_id,
         r.ord,
         asset_id_text,
         case when asset_id_text ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
              then asset_id_text::uuid
              else null
         end asset_id
  from invalid_refs r
  cross join lateral jsonb_array_elements_text(r.included_assets_json) asset_id_text
  where r.reason <> 'ok'
), asset_candidate_rows as (
  select r.profile_id,
         r.ord,
         ia.asset_id_text,
         ia.asset_id,
         a."Id" found_asset_id,
         a."ItemId" candidate_item_id,
         coalesce(a."IsDeleted", false) asset_deleted,
         it."Id" found_item_id,
         coalesce(it."IsDeleted", false) item_deleted,
         it."NameOriginal" candidate_item_name,
         it."SpecificationOriginal" candidate_specification,
         it."MaterialNumber" candidate_material_number,
         it."SerialNumber" candidate_serial_number,
         case
           when ia.asset_id is null then 'invalid_asset_id_text'
           when a."Id" is null then 'asset_not_found'
           when a."IsDeleted" then 'asset_deleted'
           when a."ItemId" is null then 'asset_without_item'
           when it."Id" is null then 'item_not_found'
           when it."IsDeleted" then 'item_deleted'
           else 'active_item'
         end candidate_status
  from invalid_refs r
  join included_asset_ids ia on ia.profile_id = r.profile_id and ia.ord = r.ord
  left join "RentalAssets" a on a."Id" = ia.asset_id
  left join "Items" it on it."Id" = a."ItemId"
  where r.reason <> 'ok'
), asset_candidate_counts as (
  select r.profile_id,
         r.ord,
         count(distinct ia.asset_id) included_asset_count,
         count(distinct a."Id") found_asset_count,
         count(distinct a."ItemId") filter (
             where a."ItemId" is not null
               and not a."IsDeleted"
               and it."Id" is not null
               and not it."IsDeleted") active_asset_item_count
  from invalid_refs r
  left join included_asset_ids ia on ia.profile_id = r.profile_id and ia.ord = r.ord
  left join "RentalAssets" a on a."Id" = ia.asset_id
  left join "Items" it on it."Id" = a."ItemId"
  where r.reason <> 'ok'
  group by r.profile_id, r.ord
), manual_refs as (
  select r.*,
         coalesce(nc.candidate_count, 0) name_candidate_count,
         coalesce(nc.match_rules, '') name_match_rules,
         coalesce(ac.active_asset_item_count, 0) active_asset_item_count
  from invalid_refs r
  left join name_candidate_counts nc on nc.profile_id = r.profile_id and nc.ord = r.ord
  left join asset_candidate_counts ac on ac.profile_id = r.profile_id and ac.ord = r.ord
  where r.reason <> 'ok'
    and not (
      coalesce(ac.active_asset_item_count, 0) = 1
      or (coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(material_exact|serial_exact)')
      or (coalesce(nc.candidate_count, 0) = 1 and nc.match_rules ~ '(display_name_exact|display_spec_exact|spec_exact|spec_name_exact)')
    )
), detail_rows as (
  select '$Database' as "Database",
         r.profile_id,
         r.ord,
         r.reason,
         'name_or_identifier_candidate' as detail_type,
         nc.candidate_item_id::text as candidate_item_id,
         nc.match_rules,
         '' as included_asset_id,
         'active_item' as candidate_status,
         nc.candidate_item_name,
         nc.candidate_specification,
         nc.candidate_material_number,
         nc.candidate_serial_number,
         r."ProfileKey",
         r."CustomerName",
         r.display_item_name
  from manual_refs r
  join name_candidates nc on nc.profile_id = r.profile_id and nc.ord = r.ord
  union all
  select '$Database' as "Database",
         r.profile_id,
         r.ord,
         r.reason,
         'included_asset_item_candidate' as detail_type,
         coalesce(acr.candidate_item_id::text, '') as candidate_item_id,
         '' as match_rules,
         coalesce(acr.asset_id::text, acr.asset_id_text, '') as included_asset_id,
         acr.candidate_status,
         acr.candidate_item_name,
         acr.candidate_specification,
         acr.candidate_material_number,
         acr.candidate_serial_number,
         r."ProfileKey",
         r."CustomerName",
         r.display_item_name
  from manual_refs r
  join asset_candidate_rows acr on acr.profile_id = r.profile_id and acr.ord = r.ord
)
select "Database",
       profile_id as "ProfileId",
       ord as "TemplateOrdinal",
       reason as "Reason",
       detail_type as "DetailType",
       candidate_item_id as "CandidateItemId",
       match_rules as "MatchRules",
       included_asset_id as "IncludedAssetId",
       candidate_status as "CandidateStatus",
$sensitiveDetailSelect
from detail_rows r
order by "Database", "Reason", "DetailType", "ProfileId", "TemplateOrdinal", "CandidateStatus", "CandidateItemId", "IncludedAssetId"
) to stdout with csv header;
"@
}

$script:SshExecutable = Resolve-SshExecutable
if (-not (Test-Path -LiteralPath $LinuxSshKeyPath)) {
    throw "Linux SSH key was not found: $LinuxSshKeyPath"
}

$Databases = @(
    $Databases |
        ForEach-Object { ([string]$_) -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
if ($Databases.Count -eq 0) {
    throw 'At least one database name is required.'
}

$projectRoot = Resolve-ProjectRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "artifacts\rental-template-item-reference-candidates\$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$allRows = New-Object System.Collections.Generic.List[object]
$candidatePath = Join-Path $OutputDirectory 'candidate-rows.csv'
$firstDatabase = $true
foreach ($database in $Databases) {
    $sql = Get-CandidateSql -Database $database
    $csvText = Invoke-RemotePsqlCsv -Database $database -Sql $sql
    $csvPath = Join-Path $OutputDirectory ("candidate-rows-$database.csv")
    $csvText | Set-Content -LiteralPath $csvPath -Encoding UTF8
    $csvLines = @($csvText -split "`r?`n" | Where-Object { $_ -ne '' })
    $rows = @($csvText | ConvertFrom-Csv)
    foreach ($row in $rows) {
        $allRows.Add($row) | Out-Null
    }

    if ($firstDatabase) {
        $csvLines | Set-Content -LiteralPath $candidatePath -Encoding UTF8
        $firstDatabase = $false
    }
    else {
        $csvLines |
            Select-Object -Skip 1 |
            Add-Content -LiteralPath $candidatePath -Encoding UTF8
    }
}

$allManualDetailRows = New-Object System.Collections.Generic.List[object]
$manualDetailPath = Join-Path $OutputDirectory 'manual-review-candidate-details.csv'
$firstManualDetailDatabase = $true
foreach ($database in $Databases) {
    $detailSql = Get-ManualReviewDetailSql -Database $database
    $detailCsvText = Invoke-RemotePsqlCsv -Database $database -Sql $detailSql
    $detailCsvPath = Join-Path $OutputDirectory ("manual-review-candidate-details-$database.csv")
    $detailCsvText | Set-Content -LiteralPath $detailCsvPath -Encoding UTF8
    $detailCsvLines = @($detailCsvText -split "`r?`n" | Where-Object { $_ -ne '' })
    $detailRows = @($detailCsvText | ConvertFrom-Csv)
    foreach ($row in $detailRows) {
        $allManualDetailRows.Add($row) | Out-Null
    }

    if ($firstManualDetailDatabase) {
        $detailCsvLines | Set-Content -LiteralPath $manualDetailPath -Encoding UTF8
        $firstManualDetailDatabase = $false
    }
    else {
        $detailCsvLines |
            Select-Object -Skip 1 |
            Add-Content -LiteralPath $manualDetailPath -Encoding UTF8
    }
}

$summaryByMatch = $allRows |
    Group-Object Database, Reason, NameMatchClass, AssetMatchClass |
    ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            Database = $first.Database
            Reason = $first.Reason
            NameMatchClass = $first.NameMatchClass
            AssetMatchClass = $first.AssetMatchClass
            Count = $_.Count
        }
    } |
    Sort-Object Database, Reason, NameMatchClass, AssetMatchClass

$summaryByDb = $allRows |
    Group-Object Database |
    ForEach-Object {
        [pscustomobject]@{
            Database = $_.Name
            CandidateCount = $_.Count
            SingleNameMatchCount = @($_.Group | Where-Object { $_.NameMatchClass -like 'auto_match_*' }).Count
            AmbiguousNameMatchCount = @($_.Group | Where-Object { $_.NameMatchClass -eq 'ambiguous_multiple_candidates' }).Count
            NoNameCandidateCount = @($_.Group | Where-Object { $_.NameMatchClass -eq 'no_active_candidate' }).Count
            SingleAssetItemCount = @($_.Group | Where-Object { $_.AssetMatchClass -eq 'single_active_item_from_included_assets' }).Count
            MultipleAssetItemCount = @($_.Group | Where-Object { $_.AssetMatchClass -eq 'multiple_active_items_from_included_assets' }).Count
            NoIncludedAssetsCount = @($_.Group | Where-Object { $_.AssetMatchClass -eq 'no_included_assets' }).Count
            ProposedItemCount = @($_.Group | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ProposedItemId) }).Count
            AssetBasedProposedItemCount = @($_.Group | Where-Object { $_.ProposedSource -eq 'included_asset_single_active_item' }).Count
            IdentifierBasedProposedItemCount = @($_.Group | Where-Object { $_.ProposedSource -eq 'strong_identifier_single_name_candidate' }).Count
            NameBasedProposedItemCount = @($_.Group | Where-Object { $_.ProposedSource -eq 'unique_name_or_spec_candidate' }).Count
        }
    } |
    Sort-Object Database

$manualDetailSummary = if ($allManualDetailRows.Count -gt 0) {
    $allManualDetailRows |
        Group-Object Database, DetailType, CandidateStatus |
        ForEach-Object {
            $first = $_.Group[0]
            [pscustomobject]@{
                Database = $first.Database
                DetailType = $first.DetailType
                CandidateStatus = $first.CandidateStatus
                Count = $_.Count
                DistinctProfileTemplateCount = @(
                    $_.Group |
                        ForEach-Object { "{0}|{1}" -f $_.ProfileId, $_.TemplateOrdinal } |
                        Sort-Object -Unique
                ).Count
                DistinctCandidateItemCount = @(
                    $_.Group |
                        Where-Object { -not [string]::IsNullOrWhiteSpace($_.CandidateItemId) } |
                        ForEach-Object { $_.CandidateItemId } |
                        Sort-Object -Unique
                ).Count
            }
        } |
        Sort-Object Database, DetailType, CandidateStatus
}
else {
    @([pscustomobject]@{
        Database = ''
        DetailType = 'none'
        CandidateStatus = 'no_manual_review_candidate_details'
        Count = 0
        DistinctProfileTemplateCount = 0
        DistinctCandidateItemCount = 0
    })
}

$summaryByMatchPath = Join-Path $OutputDirectory 'summary-by-match-class.csv'
$summaryByDbPath = Join-Path $OutputDirectory 'summary-by-database.csv'
$manualDetailSummaryPath = Join-Path $OutputDirectory 'manual-review-candidate-detail-summary.csv'
$summaryByMatch | Export-Csv -LiteralPath $summaryByMatchPath -Encoding UTF8 -NoTypeInformation
$summaryByDb | Export-Csv -LiteralPath $summaryByDbPath -Encoding UTF8 -NoTypeInformation
$manualDetailSummary | Export-Csv -LiteralPath $manualDetailSummaryPath -Encoding UTF8 -NoTypeInformation

$readmePath = Join-Path $OutputDirectory 'README.md'
@(
    '# 렌탈 청구 템플릿 품목 참조 후보 추출 결과',
    '',
    "- 생성 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss KST')",
    '- 실행 방식: Linux PC 운영 PostgreSQL SELECT-only',
    "- 상세 개인정보성 컬럼 포함: $($IncludeSensitiveCandidateRows.IsPresent)",
    '',
    '## 파일',
    "- `candidate-rows.csv`: 통합 후보 행. 기본 실행에서는 ProfileKey/CustomerName/표시품목명/원본 ItemId를 비워 둡니다.",
    "- `candidate-rows-<db>.csv`: DB별 후보 행.",
    "- `summary-by-database.csv`: DB별 후보 수 요약.",
    "- `summary-by-match-class.csv`: 매칭 분류별 후보 수 요약.",
    "- `manual-review-candidate-details.csv`: 자동 제안이 없는 수동 검토 대상의 후보 품목/포함 자산 품목 근거입니다.",
    "- `manual-review-candidate-detail-summary.csv`: 수동 검토 상세 근거 요약입니다.",
    "- `ProposedItemId`: 단일 활성 포함 자산 또는 단일 이름/식별자 후보가 있을 때만 채우는 내부 품목 GUID입니다. 자동 승인값이 아니며 담당자 검토가 필요합니다.",
    '',
    '## 주의',
    '- 이 스크립트는 운영 DB에 쓰기를 수행하지 않습니다.',
    '- `single_active_item_from_included_assets`도 운영 정책상 자동 보정 가능하다는 뜻은 아니며, 복제본 검증과 사용자 확인이 필요합니다.'
) | Set-Content -LiteralPath $readmePath -Encoding UTF8

Write-Host "rental_template_item_reference_candidate_output=$OutputDirectory"
Write-Host "candidate_rows=$candidatePath"
Write-Host "manual_review_candidate_details=$manualDetailPath"
Write-Host "summary_by_database=$summaryByDbPath"
Write-Host "summary_by_match_class=$summaryByMatchPath"
Write-Host "manual_review_candidate_detail_summary=$manualDetailSummaryPath"
$summaryByDb | Format-Table -AutoSize | Out-String | Write-Host
