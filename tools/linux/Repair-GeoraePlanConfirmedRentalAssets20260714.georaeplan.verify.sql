\set ON_ERROR_STOP on

BEGIN READ ONLY;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.schemata WHERE schema_name = 'ops_repair_20260714'
    ) THEN
        RAISE EXCEPTION 'georaeplan repair backup schema is missing';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE NOT "IsDeleted" AND (
        ("Id" = 'd502bce8-934e-436e-bb9b-d002a1a3d90f' AND "MachineNumber" = '0A7WBJCH600005R' AND "CustomerId" = 'dfcbd044-d721-51d4-a86c-b2a91e548634' AND "BillingProfileId" IS NULL)
        OR ("Id" = 'a59a058a-49f3-4478-85ad-a89f179c8fd5' AND "MachineNumber" = '070LB8GK1A002FL' AND "CustomerId" = 'be4738c4-288c-4ef8-b8dd-868bd90575cb' AND "BillingProfileId" IS NULL)
        OR ("Id" = '463891fd-f8ac-4b94-a4e3-5ecc9c39302a' AND "MachineNumber" = '0A7WB8GM8A004YJ' AND "CustomerId" = 'be4738c4-288c-4ef8-b8dd-868bd90575cb' AND "BillingProfileId" IS NULL)
        OR ("Id" = '931fc10e-9fa4-429b-8d99-e4b6a97cbc86' AND "MachineNumber" = '9155RC30010' AND "CustomerId" = 'cfb8c450-a474-41bb-886d-9bf635970783' AND "BillingProfileId" IS NULL AND "MonthlyFee" = 330000.00)
        OR ("Id" = 'd4a92236-45fd-4bca-b113-8354171a5103' AND "MachineNumber" = '28S3BJMMC0000KA' AND "CustomerId" = 'a2befe59-259d-43a5-ab38-534ceba39b28' AND "BillingProfileId" = '484013b3-4acd-3182-775d-317e49fc68f9' AND "AssetStatus" = '임대진행중')
    )) <> 5 THEN
        RAISE EXCEPTION 'confirmed georaeplan rental asset verification failed';
    END IF;

    IF (SELECT count(*) FROM "RentalAssets" WHERE "Id" IN (
        '0f9947a6-756e-4596-a75a-0db3d14c6881',
        'd0faac89-935a-4988-a7bd-67aee7c8ccf5'
    ) AND "IsDeleted") <> 2 THEN
        RAISE EXCEPTION 'wrong-scope georaeplan source rows are still active';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM "RentalAssets"
        WHERE "Id" = '115d4c40-14f7-42e7-86f7-a8b3a9b93d98'
          AND NOT "IsDeleted"
          AND "MachineNumber" = 'VUT7300352'
          AND "CustomerId" = 'c78e15ab-5a27-4978-9b83-359db7857095'
          AND "BillingProfileId" = 'c6b8c820-c6f4-ac52-8cc3-666377b1d84d'
    ) THEN
        RAISE EXCEPTION 'VUT7300352 verified-correct relationship changed';
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

    IF (SELECT count(*) FROM "RentalAssets" WHERE NOT "IsDeleted" AND (
        ("Id" = '25a9f52c-b277-4d66-bed4-6785a667e586' AND "MachineNumber" = '9136R210205')
        OR ("Id" = '3c03a0de-65e8-4b84-a2ba-d64f035fd541' AND "MachineNumber" = '9136R210109')
        OR ("Id" IN ('e33ac8cd-8d69-4463-9ff5-5ab7b9791da9','9adfe62e-bc5a-4449-9437-bcfe42a5be43') AND "MachineNumber" = '')
    )) <> 4 THEN
        RAISE EXCEPTION 'georaeplan duplicate serial resolution values are incorrect';
    END IF;

    IF EXISTS (
        SELECT 1 FROM "RentalAssets" a
        WHERE a."Id" IN (
            'd502bce8-934e-436e-bb9b-d002a1a3d90f',
            'a59a058a-49f3-4478-85ad-a89f179c8fd5',
            '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
            '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
            'd4a92236-45fd-4bca-b113-8354171a5103'
        )
          AND (
              NOT EXISTS (SELECT 1 FROM "Items" i WHERE i."Id" = a."ItemId" AND NOT i."IsDeleted")
              OR NOT EXISTS (SELECT 1 FROM "Customers" c WHERE c."Id" = a."CustomerId" AND NOT c."IsDeleted")
              OR NOT EXISTS (
                  SELECT 1 FROM "RentalAssetAssignmentHistories" h
                  WHERE h."AssetId" = a."Id" AND h."IsCurrent" AND NOT h."IsDeleted"
                    AND h."CustomerId" = a."CustomerId"
                    AND h."BillingProfileId" IS NOT DISTINCT FROM a."BillingProfileId"
              )
          )
    ) THEN
        RAISE EXCEPTION 'asset/item/customer/history reference verification failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "RentalAssets" a
        JOIN "RentalBillingProfiles" p ON p."Id" = a."BillingProfileId" AND NOT p."IsDeleted"
        WHERE NOT a."IsDeleted"
          AND COALESCE(p."BillingTemplateJson", '') NOT LIKE '%' || a."Id"::text || '%'
    ) THEN
        RAISE EXCEPTION 'active billed asset is missing from its profile template';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "ops_repair_20260714"."billing_profiles_before" before_row
        JOIN "RentalBillingProfiles" current_row ON current_row."Id" = (before_row.payload ->> 'Id')::uuid
        WHERE (before_row.payload - 'BillingTemplateJson' - 'Revision' - 'UpdatedAtUtc')
              <> (to_jsonb(current_row) - 'BillingTemplateJson' - 'Revision' - 'UpdatedAtUtc')
    ) THEN
        RAISE EXCEPTION 'billing profile amounts or non-template terms changed';
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
        RAISE EXCEPTION 'invoice or tax invoice related rows changed';
    END IF;

    IF (SELECT count(DISTINCT up."UserId")
        FROM "UserPermissions" up
        JOIN "Users" u ON u."Id" = up."UserId" AND NOT u."IsDeleted"
        WHERE up."Permission" IN ('Rental.AssetEdit', 'Rental.ProfileEdit')) < 1 THEN
        RAISE EXCEPTION 'rental save permissions are unavailable';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM "ops_repair_20260714"."rental_assets_before" before_row
        JOIN "RentalAssets" current_row ON current_row."Id" = (before_row.payload ->> 'Id')::uuid
        WHERE current_row."Id" IN (
            'd502bce8-934e-436e-bb9b-d002a1a3d90f',
            'a59a058a-49f3-4478-85ad-a89f179c8fd5',
            '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
            '931fc10e-9fa4-429b-8d99-e4b6a97cbc86'
        )
          AND current_row."Revision" <= (before_row.payload ->> 'Revision')::bigint
    ) THEN
        RAISE EXCEPTION 'asset sync revision did not advance';
    END IF;
END $$;

SELECT
    "ManagementNumber",
    "MachineNumber",
    "AssetStatus",
    "CustomerName",
    "BillingEligibilityStatus",
    "MonthlyFee",
    "IsDeleted",
    "Revision"
FROM "RentalAssets"
WHERE "Id" IN (
    'd502bce8-934e-436e-bb9b-d002a1a3d90f',
    '0f9947a6-756e-4596-a75a-0db3d14c6881',
    'a59a058a-49f3-4478-85ad-a89f179c8fd5',
    '463891fd-f8ac-4b94-a4e3-5ecc9c39302a',
    '931fc10e-9fa4-429b-8d99-e4b6a97cbc86',
    'd0faac89-935a-4988-a7bd-67aee7c8ccf5',
    'd4a92236-45fd-4bca-b113-8354171a5103',
    '115d4c40-14f7-42e7-86f7-a8b3a9b93d98'
)
ORDER BY "ManagementNumber", "IsDeleted";

COMMIT;
