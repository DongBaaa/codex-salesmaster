\set ON_ERROR_STOP on

BEGIN;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.schemata
        WHERE schema_name = 'ops_repair_20260714'
    ) THEN
        RAISE EXCEPTION 'ops_repair_20260714 backup schema already exists';
    END IF;

    IF (SELECT count(*) FROM "ActiveEditSessions" WHERE "ExpiresAtUtc" > now()) <> 0 THEN
        RAISE EXCEPTION 'active rental edit session exists';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" IN (
        'd502bce8-934e-436e-bb9b-d002a1a3d90f',
        '0f9947a6-756e-4596-a75a-0db3d14c6881',
        'a59a058a-49f3-4478-85ad-a89f179c8fd5',
        '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
        '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
        'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
        'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
        '9adfe62e-bc5a-4449-9437-bcfe42a5be43',
        '25a9f52c-b277-4d66-bed4-6785a667e586',
        '3c03a0de-65e8-4b84-a2ba-d64f035fd541'
    )) <> 10 THEN
        RAISE EXCEPTION 'expected georaeplan rental asset set is incomplete';
    END IF;

    IF EXISTS (SELECT 1 FROM "RentalAssets" WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103') THEN
        RAISE EXCEPTION 'target MMC asset id already exists in georaeplan';
    END IF;

    IF (SELECT count(*) FROM "Customers" WHERE NOT "IsDeleted" AND "Id" IN (
        'dfcbd044-d721-51d4-a86c-b2a91e548634',
        'be4738c4-288c-4ef8-b8dd-868bd90575cb',
        'cfb8c450-a474-41bb-886d-9bf635970783',
        'a2befe59-259d-43a5-ab38-534ceba39b28'
    )) <> 4 THEN
        RAISE EXCEPTION 'one or more target customer masters are missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "Items"
        WHERE "Id" = '2d69a7ac-47d3-40a2-a757-d542e72bf11e' AND NOT "IsDeleted"
    ) THEN
        RAISE EXCEPTION 'target georaeplan SL-X4220RX item is missing';
    END IF;

    IF (SELECT count(*) FROM "RentalBillingProfiles" WHERE NOT "IsDeleted" AND "Id" IN (
        '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
        '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
        'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
        '484013b3-4acd-3182-775d-317e49fc68f9'
    )) <> 4 THEN
        RAISE EXCEPTION 'one or more affected billing profiles are missing';
    END IF;

    IF EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE NOT "IsDeleted"
          AND btrim(COALESCE("MachineNumber", '')) IN ('9136R210205', '9136R210109')
    ) THEN
        RAISE EXCEPTION 'replacement duplicate serial already exists in georaeplan';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
          AND NOT "IsDeleted"
          AND "MachineNumber" = '28S3BJMR300002P'
          AND "CustomerId" = 'a2befe59-259d-43a5-ab38-534ceba39b28'
          AND "BillingProfileId" = '484013b3-4acd-3182-775d-317e49fc68f9'
    ) THEN
        RAISE EXCEPTION 'Protech source asset no longer matches the approved snapshot';
    END IF;
END $$;

SELECT 1
FROM "RentalAssets"
WHERE "Id" IN (
    'd502bce8-934e-436e-bb9b-d002a1a3d90f',
    '0f9947a6-756e-4596-a75a-0db3d14c6881',
    'a59a058a-49f3-4478-85ad-a89f179c8fd5',
    '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
    '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
    '9adfe62e-bc5a-4449-9437-bcfe42a5be43',
    '25a9f52c-b277-4d66-bed4-6785a667e586',
    '3c03a0de-65e8-4b84-a2ba-d64f035fd541'
)
FOR UPDATE;

SELECT 1
FROM "RentalBillingProfiles"
WHERE "Id" IN (
    '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
    '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
    'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
    '484013b3-4acd-3182-775d-317e49fc68f9'
)
FOR UPDATE;

CREATE SCHEMA "ops_repair_20260714";

CREATE TABLE "ops_repair_20260714"."metadata" AS
SELECT
    'confirmed-rental-assets-20260714'::text AS "PatchId",
    'georaeplan'::text AS "DatabaseName",
    now() AS "CapturedAtUtc",
    current_user::text AS "DatabaseUser";

CREATE TABLE "ops_repair_20260714"."rental_assets_before" AS
SELECT to_jsonb(a) AS payload
FROM "RentalAssets" a
WHERE a."Id" IN (
    'd502bce8-934e-436e-bb9b-d002a1a3d90f',
    '0f9947a6-756e-4596-a75a-0db3d14c6881',
    'a59a058a-49f3-4478-85ad-a89f179c8fd5',
    '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
    '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
    '9adfe62e-bc5a-4449-9437-bcfe42a5be43',
    '25a9f52c-b277-4d66-bed4-6785a667e586',
    '3c03a0de-65e8-4b84-a2ba-d64f035fd541'
);

CREATE TABLE "ops_repair_20260714"."assignment_histories_before" AS
SELECT to_jsonb(h) AS payload
FROM "RentalAssetAssignmentHistories" h
WHERE h."AssetId" IN (
    SELECT (payload ->> 'Id')::uuid
    FROM "ops_repair_20260714"."rental_assets_before"
);

CREATE TABLE "ops_repair_20260714"."billing_profiles_before" AS
SELECT to_jsonb(p) AS payload
FROM "RentalBillingProfiles" p
WHERE p."Id" IN (
    '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
    '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
    'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
    '484013b3-4acd-3182-775d-317e49fc68f9'
);

CREATE TABLE "ops_repair_20260714"."invoices_before" AS
SELECT to_jsonb(i) AS payload
FROM "Invoices" i
WHERE i."LinkedRentalBillingProfileId" IN (
    '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
    '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
    'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
    '484013b3-4acd-3182-775d-317e49fc68f9',
    '5f5bf941-42e1-8504-c43b-57413c442d1f'
);

DO $$
DECLARE
    start_revision bigint;
BEGIN
    SELECT GREATEST(
        floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint,
        COALESCE((SELECT max("Revision") + 1 FROM "RentalAssets"), 1),
        COALESCE((SELECT max("Revision") + 1 FROM "RentalBillingProfiles"), 1),
        COALESCE((SELECT max("Revision") + 1 FROM "RentalAssetAssignmentHistories"), 1)
    ) INTO start_revision;

    EXECUTE format('CREATE TEMP SEQUENCE repair_revision_seq START WITH %s', start_revision);
END $$;

CREATE FUNCTION pg_temp.rewrite_rental_template(
    template_text text,
    old_asset_id text,
    new_asset_id text
)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$
    SELECT COALESCE(
        jsonb_agg(
            line
            || jsonb_build_object(
                'IncludedAssetIds',
                COALESCE((
                    SELECT jsonb_agg(
                        CASE
                            WHEN value = old_asset_id AND new_asset_id IS NOT NULL THEN to_jsonb(new_asset_id)
                            ELSE to_jsonb(value)
                        END
                    )
                    FROM jsonb_array_elements_text(COALESCE(line -> 'IncludedAssetIds', '[]'::jsonb)) AS ids(value)
                    WHERE value <> old_asset_id OR new_asset_id IS NOT NULL
                ), '[]'::jsonb)
            )
            || CASE
                WHEN line ->> 'RepresentativeAssetId' = old_asset_id
                    THEN jsonb_build_object('RepresentativeAssetId', new_asset_id)
                ELSE '{}'::jsonb
            END
        ),
        '[]'::jsonb
    )::text
    FROM jsonb_array_elements(COALESCE(NULLIF(template_text, '')::jsonb, '[]'::jsonb)) AS lines(line);
$$;

INSERT INTO "RentalAssets"
SELECT (jsonb_populate_record(
    NULL::"RentalAssets",
    to_jsonb(source_asset)
    || jsonb_build_object(
        'Id', 'd4a92236-45fd-4bca-b113-8354171a5103',
        'ManagementId', '451',
        'ManagementNumber', '2004-002',
        'MachineNumber', '28S3BJMMC0000KA',
        'AssetKey', 'USENET|2004-002|프로테크주식회사|SL-X4220RX',
        'ItemId', '2d69a7ac-47d3-40a2-a757-d542e72bf11e',
        'PurchaseDate', '2020-04-08',
        'PurchasePrice', 1645600.00,
        'SalePrice', 0.00,
        'Notes', E'원본 관리ID: 451\n원본 관리번호: 2004-002\n기타사항: 노후 교체 요청/ 아이티월드 사무실 사용중\n회수1: 45988\n렌탈1: [미추홀구]여성아동복지과드림스타트',
        'CreatedAtUtc', '2026-04-07T12:00:18.222846Z',
        'UpdatedAtUtc', clock_timestamp(),
        'Revision', nextval('repair_revision_seq')
    )
)).*
FROM "RentalAssets" source_asset
WHERE source_asset."Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5';

UPDATE "RentalAssetAssignmentHistories"
SET
    "IsCurrent" = false,
    "UnlinkedAtUtc" = clock_timestamp(),
    "ChangeReason" = '확정 원장 자산 연결 보정',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "IsCurrent"
  AND NOT "IsDeleted"
  AND "AssetId" IN (
      'd502bce8-934e-436e-bb9b-d002a1a3d90f',
      '0f9947a6-756e-4596-a75a-0db3d14c6881',
      'a59a058a-49f3-4478-85ad-a89f179c8fd5',
      '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
      '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
      'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
  );

UPDATE "RentalAssets"
SET
    "LastCustomerName" = "CustomerName",
    "LastInstallLocation" = "InstallLocation",
    "LastBillingProfileId" = "BillingProfileId",
    "LastBillingProfileDisplay" = CASE WHEN "BillingProfileId" IS NULL THEN '' ELSE "CustomerName" || ' · ' || "ItemName" END,
    "LastAssignmentClearedAtUtc" = clock_timestamp(),
    "CustomerId" = 'dfcbd044-d721-51d4-a86c-b2a91e548634',
    "CustomerName" = '연수구청[출산보육과]',
    "CurrentCustomerName" = '연수구청[출산보육과]',
    "BillToCustomerName" = '연수구청[출산보육과]',
    "BillingProfileId" = NULL,
    "ResponsibleOfficeCode" = 'YEONSU',
    "AssetKey" = 'USENET|1606-003|연수구청[출산보육과]|SL-M3820ND',
    "BillingEligibilityStatus" = '청구제외',
    "BillingExclusionReason" = '무상 임대 / 청구 프로필 추후 연결',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = 'd502bce8-934e-436e-bb9b-d002a1a3d90f';

UPDATE "RentalAssets"
SET
    "LastCustomerName" = "CustomerName",
    "LastInstallLocation" = "InstallLocation",
    "LastBillingProfileId" = "BillingProfileId",
    "LastBillingProfileDisplay" = CASE WHEN "BillingProfileId" IS NULL THEN '' ELSE "CustomerName" || ' · ' || "ItemName" END,
    "LastAssignmentClearedAtUtc" = clock_timestamp(),
    "CustomerId" = 'be4738c4-288c-4ef8-b8dd-868bd90575cb',
    "CustomerName" = '미추홀구 시설관리공단',
    "CurrentCustomerName" = '미추홀구 시설관리공단',
    "BillToCustomerName" = '미추홀구 시설관리공단',
    "BillingProfileId" = NULL,
    "ResponsibleOfficeCode" = 'USENET',
    "AssetKey" = "OfficeCode" || '|' || "ManagementNumber" || '|미추홀구 시설관리공단|' || "ItemName",
    "BillingEligibilityStatus" = '청구제외',
    "BillingExclusionReason" = '무상 임대 / 청구 프로필 추후 연결',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" IN (
    'a59a058a-49f3-4478-85ad-a89f179c8fd5',
    '463891fd-f8ac-4b94-a4e3-5ecc9c39302a'
);

UPDATE "RentalAssets"
SET
    "LastCustomerName" = "CustomerName",
    "LastInstallLocation" = "InstallLocation",
    "LastBillingProfileId" = "BillingProfileId",
    "LastBillingProfileDisplay" = CASE WHEN "BillingProfileId" IS NULL THEN '' ELSE "CustomerName" || ' · ' || "ItemName" END,
    "LastAssignmentClearedAtUtc" = clock_timestamp(),
    "CustomerId" = 'cfb8c450-a474-41bb-886d-9bf635970783',
    "CustomerName" = '연수구 보건소[위생정책과]',
    "CurrentCustomerName" = '연수구 보건소[위생정책과]',
    "BillToCustomerName" = '연수구 보건소[위생정책과]',
    "BillingProfileId" = NULL,
    "ResponsibleOfficeCode" = 'YEONSU',
    "AssetKey" = 'USENET|2601-007|연수구 보건소[위생정책과]|IMC3010',
    "BillingEligibilityStatus" = '미확인',
    "BillingExclusionReason" = '청구 프로필 추후 연결',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '931fc10e-9fa4-429b-8d99-e4b6a97cbc86';

UPDATE "RentalAssets"
SET
    "LastCustomerName" = "CustomerName",
    "LastInstallLocation" = "InstallLocation",
    "LastBillingProfileId" = "BillingProfileId",
    "LastBillingProfileDisplay" = CASE WHEN "BillingProfileId" IS NULL THEN '' ELSE "CustomerName" || ' · ' || "ItemName" END,
    "LastAssignmentClearedAtUtc" = clock_timestamp(),
    "CustomerId" = NULL,
    "CustomerName" = '',
    "CurrentCustomerName" = '',
    "BillToCustomerName" = '',
    "InstallLocation" = '',
    "InstallSiteName" = '',
    "BillingProfileId" = NULL,
    "AssetStatus" = '폐기',
    "CurrentLocation" = '폐기',
    "DisposalDate" = COALESCE("DisposalDate", DATE '2025-07-23'),
    "BillingEligibilityStatus" = '청구제외',
    "BillingExclusionReason" = '자산상태: 폐기',
    "IsDeleted" = true,
    "AssetKey" = "OfficeCode" || '|' || "ManagementNumber" || '||' || "ItemName",
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '0f9947a6-756e-4596-a75a-0db3d14c6881';

UPDATE "RentalAssets"
SET
    "LastCustomerName" = "CustomerName",
    "LastInstallLocation" = "InstallLocation",
    "LastBillingProfileId" = "BillingProfileId",
    "LastBillingProfileDisplay" = CASE WHEN "BillingProfileId" IS NULL THEN '' ELSE "CustomerName" || ' · ' || "ItemName" END,
    "LastAssignmentClearedAtUtc" = clock_timestamp(),
    "CustomerId" = NULL,
    "CustomerName" = '',
    "CurrentCustomerName" = '',
    "BillToCustomerName" = '',
    "InstallLocation" = '',
    "InstallSiteName" = '',
    "BillingProfileId" = NULL,
    "AssetStatus" = '창고',
    "CurrentLocation" = '창고',
    "BillingEligibilityStatus" = '청구제외',
    "BillingExclusionReason" = '자산상태: 창고',
    "IsDeleted" = true,
    "AssetKey" = "OfficeCode" || '|' || "ManagementNumber" || '||' || "ItemName",
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5';

UPDATE "RentalAssets"
SET
    "MachineNumber" = CASE "Id"
        WHEN '25a9f52c-b277-4d66-bed4-6785a667e586' THEN '9136R210205'
        WHEN '3c03a0de-65e8-4b84-a2ba-d64f035fd541' THEN '9136R210109'
        WHEN 'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9' THEN ''
        WHEN '9adfe62e-bc5a-4449-9437-bcfe42a5be43' THEN ''
    END,
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" IN (
    '25a9f52c-b277-4d66-bed4-6785a667e586',
    '3c03a0de-65e8-4b84-a2ba-d64f035fd541',
    'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
    '9adfe62e-bc5a-4449-9437-bcfe42a5be43'
);

UPDATE "RentalAssetAssignmentHistories"
SET
    "MachineNumber" = CASE "AssetId"
        WHEN 'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9' THEN ''
        WHEN '9adfe62e-bc5a-4449-9437-bcfe42a5be43' THEN ''
    END,
    "ChangeReason" = '확정 원장 중복 시리얼 보정',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "IsCurrent"
  AND NOT "IsDeleted"
  AND "AssetId" IN (
      'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
      '9adfe62e-bc5a-4449-9437-bcfe42a5be43'
  );

INSERT INTO "RentalAssetAssignmentHistories"
SELECT (jsonb_populate_record(
    NULL::"RentalAssetAssignmentHistories",
    history_backup.payload
    || jsonb_build_object(
        'Id', gen_random_uuid(),
        'AssetId', mapped.target_asset_id,
        'BillingProfileId', target_asset."BillingProfileId",
        'CustomerId', target_asset."CustomerId",
        'CustomerName', target_asset."CustomerName",
        'InstallLocation', target_asset."InstallLocation",
        'BillingProfileDisplay', CASE
            WHEN target_asset."BillingProfileId" IS NULL THEN ''
            ELSE target_asset."CustomerName" || ' · ' || target_asset."ItemName"
        END,
        'MachineNumber', target_asset."MachineNumber",
        'ManagementNumber', target_asset."ManagementNumber",
        'MonthlyFee', target_asset."MonthlyFee",
        'ResponsibleOfficeCode', target_asset."ResponsibleOfficeCode",
        'IsCurrent', true,
        'LinkedAtUtc', clock_timestamp(),
        'UnlinkedAtUtc', NULL,
        'ChangeReason', '확정 원장 자산 연결 보정',
        'IsDeleted', false,
        'CreatedAtUtc', clock_timestamp(),
        'UpdatedAtUtc', clock_timestamp(),
        'Revision', nextval('repair_revision_seq')
    )
)).*
FROM "ops_repair_20260714"."assignment_histories_before" history_backup
CROSS JOIN LATERAL (
    SELECT CASE history_backup.payload ->> 'AssetId'
        WHEN 'd0faac89-935a-4988-a7bd-67aee7c8ccf5' THEN 'd4a92236-45fd-4bca-b113-8354171a5103'::uuid
        ELSE (history_backup.payload ->> 'AssetId')::uuid
    END AS target_asset_id
) mapped
JOIN "RentalAssets" target_asset ON target_asset."Id" = mapped.target_asset_id
WHERE history_backup.payload ->> 'IsCurrent' = 'true'
  AND history_backup.payload ->> 'IsDeleted' = 'false'
  AND history_backup.payload ->> 'AssetId' IN (
      'd502bce8-934e-436e-bb9b-d002a1a3d90f',
      'a59a058a-49f3-4478-85ad-a89f179c8fd5',
      '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
      '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
      'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
  );

UPDATE "RentalBillingProfiles"
SET
    "BillingTemplateJson" = pg_temp.rewrite_rental_template(
        "BillingTemplateJson",
        '0f9947a6-756e-4596-a75a-0db3d14c6881',
        NULL
    ),
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '2e9351c8-361c-fd6e-49fd-aa5a5b794079';

UPDATE "RentalBillingProfiles"
SET
    "BillingTemplateJson" = pg_temp.rewrite_rental_template(
        "BillingTemplateJson",
        'd502bce8-934e-436e-bb9b-d002a1a3d90f',
        NULL
    ),
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf';

UPDATE "RentalBillingProfiles"
SET
    "BillingTemplateJson" = pg_temp.rewrite_rental_template(
        pg_temp.rewrite_rental_template(
            "BillingTemplateJson",
            'a59a058a-49f3-4478-85ad-a89f179c8fd5',
            NULL
        ),
        '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
        NULL
    ),
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = 'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6';

UPDATE "RentalBillingProfiles"
SET
    "BillingTemplateJson" = pg_temp.rewrite_rental_template(
        "BillingTemplateJson",
        'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
        'd4a92236-45fd-4bca-b113-8354171a5103'
    ),
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '484013b3-4acd-3182-775d-317e49fc68f9';

INSERT INTO "AuditLogs" (
    "Id", "UserId", "Username", "EntityName", "EntityId", "Action",
    "BeforeJson", "AfterJson", "CreatedAtUtc"
)
SELECT
    gen_random_uuid(),
    NULL,
    'codex-maintenance',
    'RentalAsset',
    changed."Id"::text,
    'ConfirmedRentalAssetRepair20260714',
    COALESCE(before_row.payload, '{}'::jsonb)::text,
    to_jsonb(changed)::text,
    clock_timestamp()
FROM "RentalAssets" changed
LEFT JOIN "ops_repair_20260714"."rental_assets_before" before_row
    ON before_row.payload ->> 'Id' = changed."Id"::text
WHERE changed."Id" IN (
    'd502bce8-934e-436e-bb9b-d002a1a3d90f',
    '0f9947a6-756e-4596-a75a-0db3d14c6881',
    'a59a058a-49f3-4478-85ad-a89f179c8fd5',
    '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
    '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    'd4a92236-45fd-4bca-b113-8354171a5103',
    'e33ac8cd-8d69-4463-9ff5-5ab7b9791da9',
    '9adfe62e-bc5a-4449-9437-bcfe42a5be43',
    '25a9f52c-b277-4d66-bed4-6785a667e586',
    '3c03a0de-65e8-4b84-a2ba-d64f035fd541'
);

INSERT INTO "AuditLogs" (
    "Id", "UserId", "Username", "EntityName", "EntityId", "Action",
    "BeforeJson", "AfterJson", "CreatedAtUtc"
)
SELECT
    gen_random_uuid(),
    NULL,
    'codex-maintenance',
    'RentalBillingProfile',
    changed."Id"::text,
    'ConfirmedRentalAssetRepair20260714',
    before_row.payload::text,
    to_jsonb(changed)::text,
    clock_timestamp()
FROM "RentalBillingProfiles" changed
JOIN "ops_repair_20260714"."billing_profiles_before" before_row
    ON before_row.payload ->> 'Id' = changed."Id"::text
WHERE changed."Id" IN (
    '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
    '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
    'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
    '484013b3-4acd-3182-775d-317e49fc68f9'
);

DO $$
BEGIN
    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103'
        AND NOT "IsDeleted" AND "MachineNumber" = '28S3BJMMC0000KA'
        AND "CustomerId" = 'a2befe59-259d-43a5-ab38-534ceba39b28'
        AND "BillingProfileId" = '484013b3-4acd-3182-775d-317e49fc68f9'
        AND "AssetStatus" = '임대진행중') <> 1 THEN
        RAISE EXCEPTION 'MMC Protech target assertion failed';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" IN (
        '0f9947a6-756e-4596-a75a-0db3d14c6881',
        'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
    ) AND "IsDeleted") <> 2 THEN
        RAISE EXCEPTION 'wrong-scope source deactivation assertion failed';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE NOT "IsDeleted" AND (
        ("Id" = 'd502bce8-934e-436e-bb9b-d002a1a3d90f' AND "CustomerId" = 'dfcbd044-d721-51d4-a86c-b2a91e548634' AND "BillingProfileId" IS NULL)
        OR ("Id" IN ('a59a058a-49f3-4478-85ad-a89f179c8fd5','463891fd-f8ac-4b94-a4e3-5ecc9c39302a') AND "CustomerId" = 'be4738c4-288c-4ef8-b8dd-868bd90575cb' AND "BillingProfileId" IS NULL)
        OR ("Id" = '931fc10e-9fa4-429b-8d99-e4b6a97cbc86' AND "CustomerId" = 'cfb8c450-a474-41bb-886d-9bf635970783' AND "BillingProfileId" IS NULL)
    )) <> 4 THEN
        RAISE EXCEPTION 'confirmed customer relink assertion failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "RentalAssets"
        WHERE NOT "IsDeleted"
          AND NULLIF(btrim("MachineNumber"), '') IS NOT NULL
          AND btrim("MachineNumber") <> '미상'
        GROUP BY btrim("MachineNumber")
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'non-placeholder duplicate serial remains in georaeplan';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "RentalAssets" a
        WHERE a."Id" IN (
            'd502bce8-934e-436e-bb9b-d002a1a3d90f',
            'a59a058a-49f3-4478-85ad-a89f179c8fd5',
            '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
            '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
            'd4a92236-45fd-4bca-b113-8354171a5103'
        )
          AND NOT EXISTS (
              SELECT 1
              FROM "RentalAssetAssignmentHistories" h
              WHERE h."AssetId" = a."Id"
                AND h."IsCurrent"
                AND NOT h."IsDeleted"
                AND h."CustomerId" = a."CustomerId"
                AND h."BillingProfileId" IS NOT DISTINCT FROM a."BillingProfileId"
          )
    ) THEN
        RAISE EXCEPTION 'current assignment history assertion failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "RentalAssetAssignmentHistories"
        WHERE "AssetId" IN (
            'd502bce8-934e-436e-bb9b-d002a1a3d90f',
            '0f9947a6-756e-4596-a75a-0db3d14c6881',
            'a59a058a-49f3-4478-85ad-a89f179c8fd5',
            '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
            '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
            'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
            'd4a92236-45fd-4bca-b113-8354171a5103'
        )
          AND "IsCurrent"
          AND NOT "IsDeleted"
        GROUP BY "AssetId"
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'multiple current assignment histories found';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "RentalBillingProfiles"
        WHERE "Id" IN (
            '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
            '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
            'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6'
        )
          AND "BillingTemplateJson" LIKE ANY (ARRAY[
              '%0f9947a6-756e-4596-a75a-0db3d14c6881%',
              '%d502bce8-934e-436e-bb9b-d002a1a3d90f%',
              '%a59a058a-49f3-4478-85ad-a89f179c8fd5%',
              '%463891fd-f8ac-4b94-a4e3-5ecc9c39302a%'
          ])
    ) THEN
        RAISE EXCEPTION 'removed asset remains in billing template';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalBillingProfiles"
        WHERE "Id" = '484013b3-4acd-3182-775d-317e49fc68f9'
          AND "BillingTemplateJson" LIKE '%d4a92236-45fd-4bca-b113-8354171a5103%'
          AND "BillingTemplateJson" NOT LIKE '%d0faac89-935a-4988-a7bd-67aee7c8ccf5%'
    ) THEN
        RAISE EXCEPTION 'Protech billing template replacement assertion failed';
    END IF;

    IF EXISTS (
        (SELECT payload FROM "ops_repair_20260714"."invoices_before"
         EXCEPT
         SELECT to_jsonb(i) FROM "Invoices" i WHERE i."LinkedRentalBillingProfileId" IN (
             '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
             '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
             'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
             '484013b3-4acd-3182-775d-317e49fc68f9',
             '5f5bf941-42e1-8504-c43b-57413c442d1f'
         ))
        UNION ALL
        (SELECT to_jsonb(i) FROM "Invoices" i WHERE i."LinkedRentalBillingProfileId" IN (
             '2e9351c8-361c-fd6e-49fd-aa5a5b794079',
             '3f7aa6e8-3d72-1bb2-bf53-df80a0f5bddf',
             'f0c57f6d-bc89-2f06-f5dd-ce808cb9d5b6',
             '484013b3-4acd-3182-775d-317e49fc68f9',
             '5f5bf941-42e1-8504-c43b-57413c442d1f'
         )
         EXCEPT
         SELECT payload FROM "ops_repair_20260714"."invoices_before")
    ) THEN
        RAISE EXCEPTION 'invoice rows changed unexpectedly';
    END IF;
END $$;

\if :apply
COMMIT;
\else
ROLLBACK;
\endif
