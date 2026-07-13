\set ON_ERROR_STOP on

BEGIN READ ONLY;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ops_repair_20260714'
    ) THEN
        RAISE EXCEPTION 'georaeplan_itworld repair backup schema is missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
          AND NOT "IsDeleted"
          AND "MachineNumber" = '28S3BJMR300002P'
          AND "AssetStatus" = '창고'
          AND "CustomerId" IS NULL
          AND "BillingProfileId" IS NULL
    ) THEN
        RAISE EXCEPTION 'MR warehouse asset verification failed';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103' AND "IsDeleted"
    ) THEN
        RAISE EXCEPTION 'MMC itworld source row is still active';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = '93ff6058-fd27-462e-8e70-714fa6f510c5'
          AND NOT "IsDeleted"
          AND "MachineNumber" = 'NEKA015825'
          AND "AssetStatus" = '폐기'
          AND "CustomerId" IS NULL
          AND "CustomerName" = ''
          AND "BillingProfileId" IS NULL
    ) THEN
        RAISE EXCEPTION 'NEKA015825 disposed source-of-truth asset verification failed';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE NOT "IsDeleted" AND "Id" IN (
        '5d06cfd5-ff14-480f-8a0f-145ac0e10336',
        'cbc94ddd-95ca-468f-aa9e-427c21246704'
    ) AND "MachineNumber" = '') <> 2 THEN
        RAISE EXCEPTION 'itworld duplicate serial blanking verification failed';
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
        SELECT 1 FROM "RentalAssets" a
        WHERE a."Id" = 'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
          AND NOT EXISTS (SELECT 1 FROM "Items" i WHERE i."Id" = a."ItemId" AND NOT i."IsDeleted")
    ) THEN
        RAISE EXCEPTION 'MR target item reference is invalid';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "ops_repair_20260714"."rental_assets_before" before_row
        JOIN "RentalAssets" current_row ON current_row."Id" = (before_row.payload ->> 'Id')::uuid
        WHERE current_row."Revision" <= (before_row.payload ->> 'Revision')::bigint
    ) THEN
        RAISE EXCEPTION 'itworld asset sync revision did not advance';
    END IF;
END $$;

SELECT
    "ManagementNumber",
    "MachineNumber",
    "AssetStatus",
    "CustomerName",
    "IsDeleted",
    "Revision"
FROM "RentalAssets"
WHERE "Id" IN (
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    'd4a92236-45fd-4bca-b113-8354171a5103',
    '93ff6058-fd27-462e-8e70-714fa6f510c5'
)
ORDER BY "ManagementNumber", "IsDeleted";

COMMIT;
