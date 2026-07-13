\set ON_ERROR_STOP on

BEGIN;

DO $$
DECLARE
    assignments text;
    before_payload jsonb;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ops_repair_20260714_zero_fee_template') THEN
        RAISE EXCEPTION 'zero-fee rental template backup schema is missing';
    END IF;

    SELECT payload INTO before_payload
    FROM "ops_repair_20260714_zero_fee_template"."profile_before";

    SELECT string_agg(format('%1$I = restored.%1$I', column_name), ', ' ORDER BY ordinal_position)
    INTO assignments
    FROM information_schema.columns
    WHERE table_schema = 'public'
      AND table_name = 'RentalBillingProfiles'
      AND column_name <> 'Id';

    EXECUTE format(
        'UPDATE "RentalBillingProfiles" target SET %s FROM (SELECT (jsonb_populate_record(NULL::"RentalBillingProfiles", $1)).*) restored WHERE target."Id" = restored."Id"',
        assignments
    ) USING before_payload;
END $$;

DELETE FROM "AuditLogs"
WHERE "Username" = 'codex-maintenance'
  AND "Action" = 'AddZeroFeeRentalTemplateItem20260714';

COMMIT;
