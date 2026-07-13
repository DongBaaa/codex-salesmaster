\set ON_ERROR_STOP on

BEGIN;

DO $$
DECLARE
    assignments text;
    backup_row record;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ops_repair_20260714'
    ) THEN
        RAISE EXCEPTION 'georaeplan repair backup schema is missing';
    END IF;

    SELECT string_agg(format('%1$I = restored.%1$I', column_name), ', ' ORDER BY ordinal_position)
    INTO assignments
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'RentalAssets'
      AND column_name <> 'Id';

    DELETE FROM "RentalAssetAssignmentHistories"
    WHERE "AssetId" IN (
        SELECT (payload ->> 'Id')::uuid FROM "ops_repair_20260714"."rental_assets_before"
        UNION ALL SELECT 'd4a92236-45fd-4bca-b113-8354171a5103'::uuid
    );

    DELETE FROM "RentalAssets" WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103';

    FOR backup_row IN SELECT payload FROM "ops_repair_20260714"."rental_assets_before"
    LOOP
        EXECUTE format(
            'UPDATE "RentalAssets" target SET %s FROM (SELECT (jsonb_populate_record(NULL::"RentalAssets", $1)).*) restored WHERE target."Id" = restored."Id"',
            assignments
        ) USING backup_row.payload;
    END LOOP;

    INSERT INTO "RentalAssetAssignmentHistories"
    SELECT (jsonb_populate_record(NULL::"RentalAssetAssignmentHistories", payload)).*
    FROM "ops_repair_20260714"."assignment_histories_before";

    SELECT string_agg(format('%1$I = restored.%1$I', column_name), ', ' ORDER BY ordinal_position)
    INTO assignments
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'RentalBillingProfiles'
      AND column_name <> 'Id';

    FOR backup_row IN SELECT payload FROM "ops_repair_20260714"."billing_profiles_before"
    LOOP
        EXECUTE format(
            'UPDATE "RentalBillingProfiles" target SET %s FROM (SELECT (jsonb_populate_record(NULL::"RentalBillingProfiles", $1)).*) restored WHERE target."Id" = restored."Id"',
            assignments
        ) USING backup_row.payload;
    END LOOP;
END $$;

DELETE FROM "AuditLogs"
WHERE "Username" = 'codex-maintenance'
  AND "Action" = 'ConfirmedRentalAssetRepair20260714';

DO $$
BEGIN
    IF EXISTS (
        (SELECT payload FROM "ops_repair_20260714"."rental_assets_before"
         EXCEPT
         SELECT to_jsonb(a) FROM "RentalAssets" a
         WHERE a."Id" IN (SELECT (payload ->> 'Id')::uuid FROM "ops_repair_20260714"."rental_assets_before"))
        UNION ALL
        (SELECT to_jsonb(a) FROM "RentalAssets" a
         WHERE a."Id" IN (SELECT (payload ->> 'Id')::uuid FROM "ops_repair_20260714"."rental_assets_before")
         EXCEPT
         SELECT payload FROM "ops_repair_20260714"."rental_assets_before")
    ) THEN
        RAISE EXCEPTION 'georaeplan rental asset rollback verification failed';
    END IF;

    IF EXISTS (SELECT 1 FROM "RentalAssets" WHERE "Id" = 'd4a92236-45fd-4bca-b113-8354171a5103') THEN
        RAISE EXCEPTION 'inserted MMC asset remains after rollback';
    END IF;
END $$;

COMMIT;
