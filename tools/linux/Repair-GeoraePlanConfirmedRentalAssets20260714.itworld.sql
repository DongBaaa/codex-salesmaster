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

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103'
          AND NOT "IsDeleted"
          AND "MachineNumber" = '28S3BJMMC0000KA'
          AND "AssetStatus" = '창고'
          AND "CustomerId" IS NULL
          AND "BillingProfileId" IS NULL
    ) THEN
        RAISE EXCEPTION 'MMC source asset no longer matches the approved snapshot';
    END IF;

    IF EXISTS (SELECT 1 FROM "RentalAssets" WHERE "Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5') THEN
        RAISE EXCEPTION 'target MR asset id already exists in georaeplan_itworld';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "Items"
        WHERE "Id" = '3ec40e1c-7236-4daa-bd66-8e093d23b52c' AND NOT "IsDeleted"
    ) THEN
        RAISE EXCEPTION 'target itworld SL-X4220RX item is missing';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" IN (
        'd4a92236-45fd-4bca-b113-8354171a5103',
        '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
        'cbc94ddd-95ca-468f-aa9e-427c21246704',
        '93ff6058-fd27-462e-8e70-714fa6f510c5'
    )) <> 4 THEN
        RAISE EXCEPTION 'expected itworld rental asset set is incomplete';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = '93ff6058-fd27-462e-8e70-714fa6f510c5'
          AND NOT "IsDeleted"
          AND "MachineNumber" = 'NEKA015825'
          AND "AssetStatus" = '폐기'
          AND "CustomerId" IS NULL
          AND "BillingProfileId" IS NULL
    ) THEN
        RAISE EXCEPTION 'NEKA015825 source-of-truth asset no longer matches the approved snapshot';
    END IF;
END $$;

SELECT 1
FROM "RentalAssets"
WHERE "Id" IN (
    'd4a92236-45fd-4bca-b113-8354171a5103',
    '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
    'cbc94ddd-95ca-468f-aa9e-427c21246704',
    '93ff6058-fd27-462e-8e70-714fa6f510c5'
)
FOR UPDATE;

CREATE SCHEMA "ops_repair_20260714";

CREATE TABLE "ops_repair_20260714"."metadata" AS
SELECT
    'confirmed-rental-assets-20260714'::text AS "PatchId",
    'georaeplan_itworld'::text AS "DatabaseName",
    now() AS "CapturedAtUtc",
    current_user::text AS "DatabaseUser";

CREATE TABLE "ops_repair_20260714"."rental_assets_before" AS
SELECT to_jsonb(a) AS payload
FROM "RentalAssets" a
WHERE a."Id" IN (
    'd4a92236-45fd-4bca-b113-8354171a5103',
    '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
    'cbc94ddd-95ca-468f-aa9e-427c21246704',
    '93ff6058-fd27-462e-8e70-714fa6f510c5'
);

CREATE TABLE "ops_repair_20260714"."assignment_histories_before" AS
SELECT to_jsonb(h) AS payload
FROM "RentalAssetAssignmentHistories" h
WHERE h."AssetId" IN (
    SELECT (payload ->> 'Id')::uuid
    FROM "ops_repair_20260714"."rental_assets_before"
);

DO $$
DECLARE
    start_revision bigint;
BEGIN
    SELECT GREATEST(
        floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint,
        COALESCE((SELECT max("Revision") + 1 FROM "RentalAssets"), 1),
        COALESCE((SELECT max("Revision") + 1 FROM "RentalAssetAssignmentHistories"), 1)
    ) INTO start_revision;

    EXECUTE format('CREATE TEMP SEQUENCE repair_revision_seq START WITH %s', start_revision);
END $$;

INSERT INTO "RentalAssets"
SELECT (jsonb_populate_record(
    NULL::"RentalAssets",
    to_jsonb(source_asset)
    || jsonb_build_object(
        'Id', 'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
        'ManagementId', '577',
        'ManagementNumber', '2104-001',
        'MachineNumber', '28S3BJMR300002P',
        'AssetKey', 'ITWORLD|2104-001||SL-X4220RX',
        'ItemId', '3ec40e1c-7236-4daa-bd66-8e093d23b52c',
        'PurchaseDate', '2021-04-06',
        'PurchasePrice', 1773200.00,
        'SalePrice', 300000.00,
        'Notes', E'원본 관리ID: 577\n원본 관리번호: 2104-001\nK제한: 5000\nC제한: 500\nK추가: 15\nC추가: 130\n기타사항: 유즈넷 판매\n회수1: 45681\n렌탈1: 미추홀구[용현3동행정복지센터]',
        'CreatedAtUtc', '2026-04-07T11:59:16.378845Z',
        'UpdatedAtUtc', clock_timestamp(),
        'Revision', nextval('repair_revision_seq')
    )
)).*
FROM "RentalAssets" source_asset
WHERE source_asset."Id" = 'd4a92236-45fd-4bca-b113-8354171a5103';

UPDATE "RentalAssets"
SET
    "IsDeleted" = true,
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103';

UPDATE "RentalAssetAssignmentHistories"
SET
    "IsCurrent" = false,
    "UnlinkedAtUtc" = clock_timestamp(),
    "ChangeReason" = '확정 원장 폐기 상태 보정',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "AssetId" = '93ff6058-fd27-462e-8e70-714fa6f510c5'
  AND "IsCurrent"
  AND NOT "IsDeleted";

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
    "AssetKey" = "OfficeCode" || '|' || "ManagementNumber" || '||' || "ItemName",
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" = '93ff6058-fd27-462e-8e70-714fa6f510c5';

UPDATE "RentalAssets"
SET
    "MachineNumber" = '',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "Id" IN (
    '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
    'cbc94ddd-95ca-468f-aa9e-427c21246704'
);

UPDATE "RentalAssetAssignmentHistories"
SET
    "MachineNumber" = '',
    "ChangeReason" = '확정 원장 중복 시리얼 보정',
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('repair_revision_seq')
WHERE "IsCurrent"
  AND NOT "IsDeleted"
  AND "AssetId" IN (
      '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
      'cbc94ddd-95ca-468f-aa9e-427c21246704'
  );

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
    'd4a92236-45fd-4bca-b113-8354171a5103',
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
    'cbc94ddd-95ca-468f-aa9e-427c21246704',
    '93ff6058-fd27-462e-8e70-714fa6f510c5'
);

DO $$
BEGIN
    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
        AND NOT "IsDeleted" AND "MachineNumber" = '28S3BJMR300002P'
        AND "AssetStatus" = '창고' AND "CustomerId" IS NULL AND "BillingProfileId" IS NULL) <> 1 THEN
        RAISE EXCEPTION 'MR warehouse target assertion failed';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103' AND "IsDeleted"
    ) THEN
        RAISE EXCEPTION 'MMC itworld source deactivation assertion failed';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = '93ff6058-fd27-462e-8e70-714fa6f510c5'
          AND NOT "IsDeleted"
          AND "AssetStatus" = '폐기'
          AND "CustomerId" IS NULL
          AND "CustomerName" = ''
          AND "BillingProfileId" IS NULL
    ) THEN
        RAISE EXCEPTION 'NEKA015825 disposed cleanup assertion failed';
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
        RAISE EXCEPTION 'non-placeholder duplicate serial remains in georaeplan_itworld';
    END IF;

    IF EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" IN (
            '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
            'cbc94ddd-95ca-468f-aa9e-427c21246704'
        )
          AND "MachineNumber" <> ''
    ) THEN
        RAISE EXCEPTION 'itworld duplicate serial blanking assertion failed';
    END IF;
END $$;

\if :apply
COMMIT;
\else
ROLLBACK;
\endif
