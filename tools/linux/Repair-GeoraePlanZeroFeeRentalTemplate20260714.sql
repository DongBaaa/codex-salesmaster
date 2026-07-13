\set ON_ERROR_STOP on

BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ops_repair_20260714_zero_fee_template') THEN
        RAISE EXCEPTION 'zero-fee rental template backup schema already exists';
    END IF;

    IF (SELECT count(*) FROM "ActiveEditSessions" WHERE "ExpiresAtUtc" > now()) <> 0 THEN
        RAISE EXCEPTION 'active rental edit session exists';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = '2be91cba-bf92-449d-9ddb-7d16cfb36c2e'
          AND NOT "IsDeleted"
          AND "ManagementNumber" = '1911-003'
          AND "MachineNumber" = '0A7WB8GMBA000CB'
          AND "MonthlyFee" = 0
          AND "BillingProfileId" = '5f5bf941-42e1-8504-c43b-57413c442d1f'
    ) THEN
        RAISE EXCEPTION 'zero-fee rental asset no longer matches the approved snapshot';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalBillingProfiles"
        WHERE "Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f'
          AND NOT "IsDeleted"
          AND "MonthlyAmount" = 1463000
          AND COALESCE("BillingTemplateJson", '') NOT LIKE '%2be91cba-bf92-449d-9ddb-7d16cfb36c2e%'
    ) THEN
        RAISE EXCEPTION 'middle-water billing profile no longer matches the approved snapshot';
    END IF;
END $$;

SELECT 1 FROM "RentalAssets"
WHERE "Id" = '2be91cba-bf92-449d-9ddb-7d16cfb36c2e'
FOR UPDATE;

SELECT 1 FROM "RentalBillingProfiles"
WHERE "Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f'
FOR UPDATE;

CREATE SCHEMA "ops_repair_20260714_zero_fee_template";

CREATE TABLE "ops_repair_20260714_zero_fee_template"."profile_before" AS
SELECT to_jsonb(p) AS payload
FROM "RentalBillingProfiles" p
WHERE p."Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f';

CREATE TABLE "ops_repair_20260714_zero_fee_template"."invoices_before" AS
SELECT to_jsonb(i) AS payload
FROM "Invoices" i
WHERE i."LinkedRentalBillingProfileId" = '5f5bf941-42e1-8504-c43b-57413c442d1f';

DO $$
DECLARE
    start_revision bigint;
BEGIN
    SELECT GREATEST(
        floor(extract(epoch FROM clock_timestamp()) * 1000)::bigint,
        COALESCE((SELECT max("Revision") + 1 FROM "RentalBillingProfiles"), 1)
    ) INTO start_revision;
    EXECUTE format('CREATE TEMP SEQUENCE zero_fee_template_revision_seq START WITH %s', start_revision);
END $$;

UPDATE "RentalBillingProfiles"
SET
    "BillingTemplateJson" = (
        COALESCE(NULLIF("BillingTemplateJson", '')::jsonb, '[]'::jsonb)
        || jsonb_build_array(jsonb_build_object(
            'ItemId', '8b4fa4a7-557b-4ca2-9d7b-7ca211f21602',
            'DisplayItemName', 'SL-M3820ND (임대료 추가명분)',
            'BillingLineMode', '개별',
            'IndividualGroupingMode', '사용자묶음',
            'Specification', 'SL-M3820ND / 실기기 미설치',
            'Unit', '',
            'MaterialNumber', '1911-003',
            'RepresentativeAssetId', NULL,
            'Quantity', 1,
            'UnitPrice', 0,
            'Amount', 0,
            'Note', '임대료 추가명분(실기기 설치안돼있음)',
            'IncludedAssetIds', jsonb_build_array('2be91cba-bf92-449d-9ddb-7d16cfb36c2e')
        ))
    )::text,
    "UpdatedAtUtc" = clock_timestamp(),
    "Revision" = nextval('zero_fee_template_revision_seq')
WHERE "Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f';

INSERT INTO "AuditLogs" (
    "Id", "UserId", "Username", "EntityName", "EntityId", "Action",
    "BeforeJson", "AfterJson", "CreatedAtUtc"
)
SELECT
    gen_random_uuid(), NULL, 'codex-maintenance', 'RentalBillingProfile', current_row."Id"::text,
    'AddZeroFeeRentalTemplateItem20260714', before_row.payload::text, to_jsonb(current_row)::text, clock_timestamp()
FROM "RentalBillingProfiles" current_row
CROSS JOIN "ops_repair_20260714_zero_fee_template"."profile_before" before_row
WHERE current_row."Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f';

DO $$
DECLARE
    referenced_line_count integer;
    template_amount numeric;
BEGIN
    SELECT count(*)
    INTO referenced_line_count
    FROM "RentalBillingProfiles" p
    CROSS JOIN LATERAL jsonb_array_elements(p."BillingTemplateJson"::jsonb) line
    WHERE p."Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f'
      AND COALESCE(line -> 'IncludedAssetIds', '[]'::jsonb) @> '["2be91cba-bf92-449d-9ddb-7d16cfb36c2e"]'::jsonb
      AND line ->> 'IndividualGroupingMode' = '사용자묶음'
      AND COALESCE((line ->> 'Amount')::numeric, 0) = 0;

    IF referenced_line_count <> 1 THEN
        RAISE EXCEPTION 'zero-fee asset template reference assertion failed';
    END IF;

    SELECT COALESCE(sum(
        CASE
            WHEN COALESCE((line ->> 'Quantity')::numeric, 0) * COALESCE((line ->> 'UnitPrice')::numeric, 0) > 0
                THEN COALESCE((line ->> 'Quantity')::numeric, 0) * COALESCE((line ->> 'UnitPrice')::numeric, 0)
            ELSE COALESCE((line ->> 'Amount')::numeric, 0)
        END
    ), 0)
    INTO template_amount
    FROM "RentalBillingProfiles" p
    CROSS JOIN LATERAL jsonb_array_elements(p."BillingTemplateJson"::jsonb) line
    WHERE p."Id" = '5f5bf941-42e1-8504-c43b-57413c442d1f';

    IF template_amount <> 1463000 THEN
        RAISE EXCEPTION 'billing template amount changed unexpectedly: %', template_amount;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "ops_repair_20260714_zero_fee_template"."profile_before" before_row
        JOIN "RentalBillingProfiles" current_row ON current_row."Id" = (before_row.payload ->> 'Id')::uuid
        WHERE (before_row.payload - 'BillingTemplateJson' - 'Revision' - 'UpdatedAtUtc')
              <> (to_jsonb(current_row) - 'BillingTemplateJson' - 'Revision' - 'UpdatedAtUtc')
    ) THEN
        RAISE EXCEPTION 'billing profile amount or terms changed unexpectedly';
    END IF;

    IF EXISTS (
        (SELECT payload FROM "ops_repair_20260714_zero_fee_template"."invoices_before"
         EXCEPT
         SELECT to_jsonb(i) FROM "Invoices" i WHERE i."LinkedRentalBillingProfileId" = '5f5bf941-42e1-8504-c43b-57413c442d1f')
        UNION ALL
        (SELECT to_jsonb(i) FROM "Invoices" i WHERE i."LinkedRentalBillingProfileId" = '5f5bf941-42e1-8504-c43b-57413c442d1f'
         EXCEPT
         SELECT payload FROM "ops_repair_20260714_zero_fee_template"."invoices_before")
    ) THEN
        RAISE EXCEPTION 'invoice rows changed unexpectedly';
    END IF;
END $$;

\if :apply
COMMIT;
\else
ROLLBACK;
\endif
