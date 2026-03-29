using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using System.Text.RegularExpressions;

namespace 거래플랜.Desktop.App.Data;

public static class LocalDbInitializer
{
    private const string FallbackUtcText = "1970-01-01T00:00:00Z";
    private const string YeonsuOfficeIdSettingKey = "SystemOffice.YeonsuOfficeId";
    private const string LegacyLinkedGeneralSettlementCleanupKey = "Migration.CleanupLegacyLinkedGeneralSettlements.v1";
    private static readonly Regex SqlIdentifierPattern = new(
        "^[A-Za-z0-9_]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly CanonicalOfficeDefinition[] CanonicalOffices =
    [
        new(OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Usenet, true, OfficeCodeCatalog.UsenetMainWarehouse, "USENET 창고"),
        new(OfficeCodeCatalog.Itworld, OfficeCodeCatalog.Itworld, false, OfficeCodeCatalog.ItworldMainWarehouse, "ITWORLD 창고"),
        new(OfficeCodeCatalog.Yeonsu, OfficeCodeCatalog.Yeonsu, false, OfficeCodeCatalog.YeonsuMainWarehouse, "YEONSU 창고")
    ];

    public static async Task InitializeAsync(LocalDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await MigrateColumnsAsync(db);

        if (!db.CustomerCategories.Any())
        {
            db.CustomerCategories.AddRange(
                DefaultCustomerCategories.All.Select(definition => new LocalCustomerCategory
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsSystemDefault = false
                }));
        }

        SeedSelectionOptions(db);
        await NormalizeItemCategoryOptionDuplicatesAsync(db);

        if (!db.Units.Any())
        {
            db.Units.AddRange(
                new LocalUnit { Name = "EA" },
                new LocalUnit { Name = "SET" },
                new LocalUnit { Name = "대" },
                new LocalUnit { Name = "개" },
                new LocalUnit { Name = "박스" }
            );
        }

        await UpsertSettingAsync(db, "LastSyncRevision", "0");
        await UpsertSettingAsync(db, "Theme", "Dark");

        await CleanupLegacyLinkedGeneralSettlementsAsync(db);
        await CustomerCategoryMaintenance.NormalizeAsync(db);
        await NormalizeSelectionOptionSystemDefaultsAsync(db);
        await SeedOfficeAndWarehouseAsync(db);
        await SeedCompanyProfilesAsync(db);
        await SeedRentalDefaultsAsync(db);
        await NormalizeRentalOfficeDataAsync(db);
        await db.SaveChangesAsync();
        await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_ItemCategoryOptions_Name_Active\" ON \"ItemCategoryOptions\" (\"Name\") WHERE COALESCE(TRIM(\"Name\"), '') <> '' AND COALESCE(\"IsDeleted\", 0) = 0;");

        var rentalState = new RentalStateService(db);
        await rentalState.RepairRentalCatalogLinksAsync();
    }

    private static void LogSchemaStepFailure(string stepName, Exception ex)
    {
        AppLogger.Warn("LOCALDB", $"로컬 DB 보강 단계 '{stepName}' 실패: {ex.Message}");
    }

    private static async Task NormalizeSelectionOptionSystemDefaultsAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;

        var customerCategories = await db.CustomerCategories.IgnoreQueryFilters().Where(current => current.IsSystemDefault).ToListAsync();
        foreach (var current in customerCategories)
        {
            current.IsSystemDefault = false;
            current.IsDirty = true;
            current.UpdatedAtUtc = now;
        }

        var priceGradeOptions = await db.PriceGradeOptions.IgnoreQueryFilters().Where(current => current.IsSystemDefault).ToListAsync();
        foreach (var current in priceGradeOptions)
        {
            current.IsSystemDefault = false;
            current.IsDirty = true;
            current.UpdatedAtUtc = now;
        }

        var tradeTypeOptions = await db.TradeTypeOptions.IgnoreQueryFilters().Where(current => current.IsSystemDefault).ToListAsync();
        foreach (var current in tradeTypeOptions)
        {
            current.IsSystemDefault = false;
            current.IsDirty = true;
            current.UpdatedAtUtc = now;
        }

        var itemCategoryOptions = await db.ItemCategoryOptions.IgnoreQueryFilters().Where(current => current.IsSystemDefault).ToListAsync();
        foreach (var current in itemCategoryOptions)
        {
            current.IsSystemDefault = false;
            current.IsDirty = true;
            current.UpdatedAtUtc = now;
        }
    }

    private static async Task MigrateColumnsAsync(LocalDbContext db)
    {
        await TryCreateAttachmentSelectionsTableAsync(db);
        await TryCreateSyncDiagnosticEventsTableAsync(db);
        await TryCreateCustomerContractsTableAsync(db);
        await TryCreateTransactionsTableAsync(db);
        await TryCreateTransactionAttachmentsTableAsync(db);
        await TryCreateOfficeTableAsync(db);
        await TryCreateWarehouseTableAsync(db);
        await TryCreatePriceGradeOptionsTableAsync(db);
        await TryCreateTradeTypeOptionsTableAsync(db);
        await TryCreateItemCategoryOptionsTableAsync(db);
        await TryCreateInvoiceLineSerialsTableAsync(db);
        await TryCreateInventoryMovementsTableAsync(db);
        await TryCreateStockLayersTableAsync(db);
        await TryCreateCostAllocationsTableAsync(db);
        await TryCreateItemWarehouseStocksTableAsync(db);
        await TryCreateSerialLedgersTableAsync(db);
        await TryCreateInventoryTransfersTableAsync(db);
        await TryCreateAuditLogsTableAsync(db);
        await TryCreateRentalManagementCompaniesTableAsync(db);
        await TryCreateRentalBillingProfilesTableAsync(db);
        await TryCreateRentalAssetsTableAsync(db);
        await TryCreateRentalBillingLogsTableAsync(db);
        await EnsureLegacyRentalNamingColumnsAsync(db);

        var customerMasterCols = new (string col, string def)[]
        {
            ("TenantCode", $"TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}'"),
            ("OfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}'"),
        };
        foreach (var (col, def) in customerMasterCols)
            await TryAddColumnAsync(db, "CustomerMasters", col, def);

        var customerCols = new (string col, string def)[]
        {
            ("TenantCode", $"TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}'"),
            ("DetailAddress", "TEXT NOT NULL DEFAULT ''"),
            ("MobilePhone", "TEXT NOT NULL DEFAULT ''"),
            ("FaxNumber", "TEXT NOT NULL DEFAULT ''"),
            ("Representative", "TEXT NOT NULL DEFAULT ''"),
            ("BusinessType", "TEXT NOT NULL DEFAULT ''"),
            ("BusinessItem", "TEXT NOT NULL DEFAULT ''"),
            ("Recipient", "TEXT NOT NULL DEFAULT ''"),
            ("HomePage", "TEXT NOT NULL DEFAULT ''"),
            ("PriceGrade", "TEXT NOT NULL DEFAULT ''"),
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'USENET'"),
            ("TradeType", "TEXT NOT NULL DEFAULT '매출'"),
        };
        foreach (var (col, def) in customerCols)
            await TryAddColumnAsync(db, "Customers", col, def);

        var itemCols = new (string col, string def)[]
        {
            ("TenantCode", $"TEXT NOT NULL DEFAULT '{TenantScopeCatalog.UsenetGroup}'"),
            ("OfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Shared}'"),
            ("CategoryName", "TEXT NOT NULL DEFAULT ''"),
            ("ItemKind", "TEXT NOT NULL DEFAULT '일반상품'"),
            ("TrackingType", "TEXT NOT NULL DEFAULT '재고'"),
            ("BoxQuantity", "REAL NOT NULL DEFAULT 0"),
            ("StorageLocation", "TEXT NOT NULL DEFAULT ''"),
            ("CurrentStock", "REAL NOT NULL DEFAULT 0"),
            ("SafetyStock", "REAL NOT NULL DEFAULT 0"),
            ("PurchasePrice", "REAL NOT NULL DEFAULT 0"),
            ("SalePrice", "REAL NOT NULL DEFAULT 0"),
            ("RetailPrice", "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeA", "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeB", "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeC", "REAL NOT NULL DEFAULT 0"),
            ("LastPurchaseDate", "TEXT"),
            ("LastSaleDate", "TEXT"),
            ("SimpleMemo", "TEXT NOT NULL DEFAULT ''"),
        };
        foreach (var (col, def) in itemCols)
            await TryAddColumnAsync(db, "Items", col, def);

        var transactionCols = new (string col, string def)[]
        {
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'USENET'"),
            ("TransactionKind", "TEXT NOT NULL DEFAULT '일반수금'"),
            ("LinkedInvoiceId", "TEXT NULL"),
            ("LinkedInvoiceNumber", "TEXT NOT NULL DEFAULT ''"),
            ("LinkedRentalBillingProfileId", "TEXT NULL"),
            ("LinkedRentalBillingRunId", "TEXT NULL"),
            ("SettlementAmount", "REAL NOT NULL DEFAULT 0"),
            ("AdvanceDelta", "REAL NOT NULL DEFAULT 0"),
            ("PrepaidDelta", "REAL NOT NULL DEFAULT 0"),
            ("CashReceipt", "REAL NOT NULL DEFAULT 0"),
            ("CardReceipt", "REAL NOT NULL DEFAULT 0"),
            ("BankReceipt", "REAL NOT NULL DEFAULT 0"),
            ("DiscountApplied", "REAL NOT NULL DEFAULT 0"),
            ("ReceiptTotal", "REAL NOT NULL DEFAULT 0"),
            ("CashPayment", "REAL NOT NULL DEFAULT 0"),
            ("CardPayment", "REAL NOT NULL DEFAULT 0"),
            ("BankPayment", "REAL NOT NULL DEFAULT 0"),
            ("DiscountReceived", "REAL NOT NULL DEFAULT 0"),
            ("PaymentTotal", "REAL NOT NULL DEFAULT 0"),
            ("Note", "TEXT NOT NULL DEFAULT ''"),
            ("Memo", "TEXT NOT NULL DEFAULT ''"),
            ("IsDeleted", "INTEGER NOT NULL DEFAULT 0"),
            ("CreatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("UpdatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("Revision", "INTEGER NOT NULL DEFAULT 0"),
            ("IsDirty", "INTEGER NOT NULL DEFAULT 1"),
        };
        foreach (var (col, def) in transactionCols)
            await TryAddColumnAsync(db, "Transactions", col, def);

        var transactionAttachmentCols = new (string col, string def)[]
        {
            ("TransactionId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'"),
            ("AttachmentType", "TEXT NOT NULL DEFAULT '기타'"),
            ("FileName", "TEXT NOT NULL DEFAULT ''"),
            ("StoredFileName", "TEXT NOT NULL DEFAULT ''"),
            ("StoredPath", "TEXT NOT NULL DEFAULT ''"),
            ("MimeType", "TEXT NOT NULL DEFAULT ''"),
            ("FileSize", "INTEGER NOT NULL DEFAULT 0"),
            ("FileHash", "TEXT NOT NULL DEFAULT ''"),
            ("Description", "TEXT NOT NULL DEFAULT ''"),
            ("UploadedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("UploadedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("VerificationStatus", "TEXT NOT NULL DEFAULT '미확인'"),
            ("VerifiedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("VerifiedAtUtc", "TEXT NULL"),
            ("VerificationMemo", "TEXT NOT NULL DEFAULT ''"),
            ("SortOrder", "INTEGER NOT NULL DEFAULT 0"),
            ("IsDeleted", "INTEGER NOT NULL DEFAULT 0"),
            ("CreatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("UpdatedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("Revision", "INTEGER NOT NULL DEFAULT 0"),
            ("IsDirty", "INTEGER NOT NULL DEFAULT 1")
        };
        foreach (var (col, def) in transactionAttachmentCols)
            await TryAddColumnAsync(db, "TransactionAttachments", col, def);

        var invoiceCols = new (string col, string def)[]
        {
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'USENET'"),
            ("SourceWarehouseCode", "TEXT NOT NULL DEFAULT 'USENET_MAIN'"),
            ("DeliveryGroupId", "TEXT NULL"),
            ("ParentInvoiceId", "TEXT NULL"),
            ("VersionGroupId", "TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'"),
            ("VersionNumber", "INTEGER NOT NULL DEFAULT 1"),
            ("PreviousVersionId", "TEXT NULL"),
            ("IsLatestVersion", "INTEGER NOT NULL DEFAULT 1"),
            ("IsConfirmed", "INTEGER NOT NULL DEFAULT 1"),
            ("CreatedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("LastSavedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("LastSavedAtUtc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00Z'"),
            ("ConcurrencyStamp", "TEXT NOT NULL DEFAULT ''"),
            ("CostStatus", "TEXT NOT NULL DEFAULT '미확인'"),
            ("TaxInvoiceIssued", "INTEGER NOT NULL DEFAULT 0"),
        };
        foreach (var (col, def) in invoiceCols)
            await TryAddColumnAsync(db, "Invoices", col, def);

        var invoiceLineCols = new (string col, string def)[]
        {
            ("ItemTrackingType", "TEXT NOT NULL DEFAULT '재고'")
        };
        foreach (var (col, def) in invoiceLineCols)
            await TryAddColumnAsync(db, "InvoiceLines", col, def);

        var companyProfileCols = new (string col, string def)[]
        {
            ("ProfileName", "TEXT NOT NULL DEFAULT ''"),
            ("OfficeCode", "TEXT NOT NULL DEFAULT 'USENET'"),
            ("IsDefaultForOffice", "INTEGER NOT NULL DEFAULT 0"),
            ("IsActive", "INTEGER NOT NULL DEFAULT 1")
        };
        foreach (var (col, def) in companyProfileCols)
            await TryAddColumnAsync(db, "CompanyProfiles", col, def);

        var rentalAssetCols = new (string col, string def)[]
        {
            ("ItemId", "TEXT NULL"),
            ("CurrentCustomerName", "TEXT NOT NULL DEFAULT ''"),
            ("BillToCustomerName", "TEXT NOT NULL DEFAULT ''"),
            ("InstallSiteName", "TEXT NOT NULL DEFAULT ''"),
            ("BillingEligibilityStatus", "TEXT NOT NULL DEFAULT ''"),
            ("BillingExclusionReason", "TEXT NOT NULL DEFAULT ''")
        };
        foreach (var (col, def) in rentalAssetCols)
            await TryAddColumnAsync(db, "RentalAssets", col, def);

        var rentalBillingProfileCols = new (string col, string def)[]
        {
            ("BillingType", "TEXT NOT NULL DEFAULT '묶음'"),
            ("BillToCustomerName", "TEXT NOT NULL DEFAULT ''"),
            ("InstallSiteName", "TEXT NOT NULL DEFAULT ''"),
            ("BillingAdvanceMode", "TEXT NOT NULL DEFAULT '후불'"),
            ("BillingStartDate", "TEXT NULL"),
            ("SettlementStatus", "TEXT NOT NULL DEFAULT '미입금'"),
            ("CompletionStatus", "TEXT NOT NULL DEFAULT '미완료'"),
            ("SettledAmount", "REAL NOT NULL DEFAULT 0"),
            ("OutstandingAmount", "REAL NOT NULL DEFAULT 0"),
            ("RequiresFollowUp", "INTEGER NOT NULL DEFAULT 0"),
            ("FollowUpNote", "TEXT NOT NULL DEFAULT ''"),
            ("LastSettledDate", "TEXT NULL"),
            ("BillingTemplateJson", "TEXT NOT NULL DEFAULT '[]'"),
            ("BillingRunsJson", "TEXT NOT NULL DEFAULT '[]'")
        };
        foreach (var (col, def) in rentalBillingProfileCols)
            await TryAddColumnAsync(db, "RentalBillingProfiles", col, def);

        var inventoryTransferCols = new (string col, string def)[]
        {
            ("TransferStatus", "TEXT NOT NULL DEFAULT '수령대기'"),
            ("RequestedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("RequestedAtUtc", "TEXT NULL"),
            ("ReceivedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("ReceivedAtUtc", "TEXT NULL"),
            ("ReceiveMemo", "TEXT NOT NULL DEFAULT ''"),
            ("ReceiveEvidencePath", "TEXT NOT NULL DEFAULT ''"),
            ("RejectedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("RejectedAtUtc", "TEXT NULL"),
            ("RejectReason", "TEXT NOT NULL DEFAULT ''"),
            ("LastStatusChangedByUsername", "TEXT NOT NULL DEFAULT ''"),
            ("LastStatusChangedAtUtc", "TEXT NULL")
        };
        foreach (var (col, def) in inventoryTransferCols)
            await TryAddColumnAsync(db, "InventoryTransfers", col, def);

        var inventoryTransferLineCols = new (string col, string def)[]
        {
            ("ReceivedQuantity", "REAL NULL"),
            ("QuantityDifference", "REAL NULL"),
            ("ReceiptRemark", "TEXT NOT NULL DEFAULT ''")
        };
        foreach (var (col, def) in inventoryTransferLineCols)
            await TryAddColumnAsync(db, "InventoryTransferLines", col, def);

        await NormalizeLegacyDateTimeColumnsAsync(db);

        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_VersionGroupId\" ON \"Invoices\" (\"VersionGroupId\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_IsLatestVersion\" ON \"Invoices\" (\"IsLatestVersion\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_SourceWarehouseCode\" ON \"Invoices\" (\"SourceWarehouseCode\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_ResponsibleOfficeCode\" ON \"Invoices\" (\"ResponsibleOfficeCode\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CompanyProfiles_OfficeCode_ProfileName\" ON \"CompanyProfiles\" (\"OfficeCode\", \"ProfileName\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CompanyProfiles_OfficeCode_IsDefaultForOffice\" ON \"CompanyProfiles\" (\"OfficeCode\", \"IsDefaultForOffice\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Transactions_ResponsibleOfficeCode\" ON \"Transactions\" (\"ResponsibleOfficeCode\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_TransactionAttachments_TransactionId\" ON \"TransactionAttachments\" (\"TransactionId\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_TransactionAttachments_TransactionStatus\" ON \"TransactionAttachments\" (\"TransactionId\", \"VerificationStatus\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TransferStatus\" ON \"InventoryTransfers\" (\"TransferStatus\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Items_TenantCode_OfficeCode\" ON \"Items\" (\"TenantCode\", \"OfficeCode\");");
        await BackfillTransactionResponsibleOfficeCodeAsync(db);
        await NormalizeCompanyProfilesAsync(db);
        await NormalizeCustomerTradeTypeAsync(db);
        await RepairCustomerClassificationIntegrityAsync(db);
        await NormalizeTradeTypeOptionCatalogAsync(db);
        await BackfillCustomerScopeFieldsAsync(db);
        await BackfillCustomerMasterScopeFieldsAsync(db);
        await BackfillItemScopeFieldsAsync(db);
        await BackfillItemOperationalFieldsAsync(db);
        await BackfillInvoiceLineTrackingTypesAsync(db);
    }

    private static void SeedSelectionOptions(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var itemCategoryOptions = db.ItemCategoryOptions.IgnoreQueryFilters().ToList();

        foreach (var definition in SelectionOptionDefaults.DefaultPriceGrades)
        {
            var option = db.PriceGradeOptions.IgnoreQueryFilters().FirstOrDefault(current => current.Id == definition.Id);
            if (option is null)
            {
                db.PriceGradeOptions.Add(new LocalPriceGradeOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    PriceSource = definition.PriceSource,
                    SortOrder = definition.SortOrder,
                    IsSystemDefault = definition.IsSystemDefault,
                    IsActive = true,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                continue;
            }

            option.Name = string.IsNullOrWhiteSpace(option.Name) ? definition.Name : option.Name.Trim();
            option.PriceSource = SelectionOptionDefaults.NormalizePriceSource(option.PriceSource);
            option.SortOrder = option.SortOrder == 0 ? definition.SortOrder : option.SortOrder;
            option.IsSystemDefault = false;
            option.IsActive = true;
            option.IsDeleted = false;
        }

        foreach (var definition in SelectionOptionDefaults.DefaultTradeTypes)
        {
            var option = db.TradeTypeOptions.IgnoreQueryFilters().FirstOrDefault(current => current.Id == definition.Id);
            if (option is null)
            {
                db.TradeTypeOptions.Add(new LocalTradeTypeOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    AllowsSales = definition.AllowsSales,
                    AllowsPurchase = definition.AllowsPurchase,
                    SortOrder = definition.SortOrder,
                    IsSystemDefault = definition.IsSystemDefault,
                    IsActive = true,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                continue;
            }

            option.Name = string.IsNullOrWhiteSpace(option.Name) ? definition.Name : option.Name.Trim();
            option.AllowsSales = option.AllowsSales || definition.AllowsSales;
            option.AllowsPurchase = option.AllowsPurchase || definition.AllowsPurchase;
            option.SortOrder = option.SortOrder == 0 ? definition.SortOrder : option.SortOrder;
            option.IsSystemDefault = false;
            option.IsActive = true;
            option.IsDeleted = false;
        }

        foreach (var definition in SelectionOptionDefaults.DefaultItemCategories)
        {
            var option = itemCategoryOptions.FirstOrDefault(current => current.Id == definition.Id);
            if (option is null)
            {
                option = new LocalItemCategoryOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    SortOrder = definition.SortOrder,
                    IsSystemDefault = definition.IsSystemDefault,
                    IsActive = true,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.ItemCategoryOptions.Add(option);
                itemCategoryOptions.Add(option);
                continue;
            }

            option.Name = string.IsNullOrWhiteSpace(option.Name) ? definition.Name : option.Name.Trim();
            option.SortOrder = option.SortOrder == 0 ? definition.SortOrder : option.SortOrder;
            option.IsSystemDefault = false;
            option.IsActive = true;
            option.IsDeleted = false;
        }

        var nextSortOrder = itemCategoryOptions
            .Where(option => !option.IsDeleted)
            .Select(option => option.SortOrder)
            .DefaultIfEmpty(0)
            .Max() + 10;

        foreach (var categoryName in db.Items
                     .IgnoreQueryFilters()
                     .Select(item => item.CategoryName)
                     .AsEnumerable()
                     .Select(SelectionOptionDefaults.NormalizeItemCategoryName)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var existing = itemCategoryOptions.FirstOrDefault(option =>
                !option.IsDeleted &&
                string.Equals(option.Name, categoryName, StringComparison.CurrentCultureIgnoreCase));

            if (existing is not null)
                continue;

            existing = new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = categoryName,
                SortOrder = nextSortOrder,
                IsSystemDefault = false,
                IsActive = true,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.ItemCategoryOptions.Add(existing);
            itemCategoryOptions.Add(existing);
            nextSortOrder += 10;
        }
    }

    private static async Task SeedOfficeAndWarehouseAsync(LocalDbContext db)
    {
        foreach (var definition in CanonicalOffices)
            await EnsureOfficeAsync(db, definition.Code, definition.Name, definition.IsHeadOffice);

        await db.SaveChangesAsync();

        await NormalizeLegacyOfficeCodesAsync(db);
        await NormalizeOfficeReferenceDataAsync(db);
        await NormalizeWarehouseDataAsync(db);
        await db.SaveChangesAsync();

        var offices = await db.Offices.AsNoTracking().Where(office => !office.IsDeleted).ToListAsync();
        var usenetOffice = offices.FirstOrDefault(office => office.Code == OfficeCodeCatalog.Usenet);
        var itworldOffice = offices.FirstOrDefault(office => office.Code == OfficeCodeCatalog.Itworld);
        var yeonsuOffice = offices.FirstOrDefault(office => office.Code == OfficeCodeCatalog.Yeonsu);

        DomainConstants.ConfigureSystemOffices(
            usenetOffice?.Code,
            yeonsuOffice?.Code,
            DomainConstants.DefaultWarehouseUsenetMain,
            DomainConstants.DefaultWarehouseYeonsuMain,
            itworldOffice?.Code,
            DomainConstants.DefaultWarehouseItworldMain);

        foreach (var definition in CanonicalOffices)
        {
            var office = offices.FirstOrDefault(current => current.Code == definition.Code);
            if (office is null)
                continue;

            await EnsureWarehouseAsync(db, office, definition.WarehouseCode, definition.WarehouseName);
        }
    }

    private static async Task SeedCompanyProfilesAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var defaults = new[]
        {
            new LocalCompanyProfile
            {
                ProfileName = "USENET 기본",
                OfficeCode = OfficeCodeCatalog.Usenet,
                TradeName = OfficeCodeCatalog.Usenet,
                Representative = "[REDACTED_NAME]",
                BusinessNumber = "[REDACTED_BUSINESS_NUMBER]",
                Address = "[REDACTED_ADDRESS]",
                ContactNumber = "[REDACTED_PHONE]",
                BankAccountText = string.Empty,
                IsDefaultForOffice = true,
                IsActive = true,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            new LocalCompanyProfile
            {
                ProfileName = "ITWORLD 기본",
                OfficeCode = OfficeCodeCatalog.Itworld,
                TradeName = OfficeCodeCatalog.Itworld,
                Representative = "[REDACTED_NAME]",
                BusinessNumber = "[REDACTED_BUSINESS_NUMBER]",
                Address = "[REDACTED_ADDRESS]",
                ContactNumber = "[REDACTED_PHONE]",
                BankAccountText = string.Empty,
                IsDefaultForOffice = true,
                IsActive = true,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            new LocalCompanyProfile
            {
                ProfileName = "YEONSU 기본",
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                TradeName = OfficeCodeCatalog.Yeonsu,
                Representative = string.Empty,
                BusinessNumber = string.Empty,
                Address = string.Empty,
                ContactNumber = string.Empty,
                BankAccountText = string.Empty,
                IsDefaultForOffice = true,
                IsActive = true,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }
        };

        foreach (var definition in defaults)
        {
            var current = await db.CompanyProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(profile =>
                    profile.OfficeCode == definition.OfficeCode &&
                    profile.IsDefaultForOffice);

            if (current is null)
            {
                db.CompanyProfiles.Add(definition);
                continue;
            }

            current.ProfileName = string.IsNullOrWhiteSpace(current.ProfileName)
                ? definition.ProfileName
                : current.ProfileName.Trim();
            current.TradeName = string.IsNullOrWhiteSpace(current.TradeName)
                ? definition.TradeName
                : current.TradeName.Trim();
            current.OfficeCode = NormalizeRentalOfficeCode(current.OfficeCode, definition.OfficeCode);
            current.IsDefaultForOffice = true;
            current.IsActive = true;
            current.IsDeleted = false;
            if (string.IsNullOrWhiteSpace(current.Representative))
                current.Representative = definition.Representative;
            if (string.IsNullOrWhiteSpace(current.BusinessNumber))
                current.BusinessNumber = definition.BusinessNumber;
            if (string.IsNullOrWhiteSpace(current.Address))
                current.Address = definition.Address;
            if (string.IsNullOrWhiteSpace(current.ContactNumber))
                current.ContactNumber = definition.ContactNumber;
            current.IsDirty = false;
            current.UpdatedAtUtc = now;
        }
    }

    private static async Task NormalizeCompanyProfilesAsync(LocalDbContext db)
    {
        var profiles = await db.CompanyProfiles.IgnoreQueryFilters().ToListAsync();
        foreach (var profile in profiles)
        {
            var changed = false;

            var normalizedOfficeCode = NormalizeRentalOfficeCode(profile.OfficeCode, null);
            if (!string.Equals(profile.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                profile.OfficeCode = normalizedOfficeCode;
                changed = true;
            }

            var normalizedProfileName = string.IsNullOrWhiteSpace(profile.ProfileName)
                ? (!string.IsNullOrWhiteSpace(profile.TradeName) ? $"{profile.TradeName.Trim()} 기본" : $"{normalizedOfficeCode} 기본")
                : profile.ProfileName.Trim();
            if (profile.IsDefaultForOffice &&
                (string.IsNullOrWhiteSpace(profile.ProfileName) ||
                 string.Equals(normalizedProfileName, "유즈넷 기본", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedProfileName, "아이티월드 기본", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedProfileName, "연수구 기본", StringComparison.OrdinalIgnoreCase)))
            {
                normalizedProfileName = $"{normalizedOfficeCode} 기본";
            }
            if (!string.Equals(profile.ProfileName, normalizedProfileName, StringComparison.CurrentCulture))
            {
                profile.ProfileName = normalizedProfileName;
                changed = true;
            }

            var normalizedTradeName = string.IsNullOrWhiteSpace(profile.TradeName)
                ? profile.ProfileName.Replace(" 기본", string.Empty, StringComparison.CurrentCulture)
                : profile.TradeName.Trim();
            if (profile.IsDefaultForOffice &&
                (string.IsNullOrWhiteSpace(profile.TradeName) ||
                 string.Equals(normalizedTradeName, "유즈넷", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedTradeName, "아이티월드", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedTradeName, "연수구", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedTradeName, "연수구 사무실", StringComparison.OrdinalIgnoreCase)))
            {
                normalizedTradeName = OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode);
            }
            if (!string.Equals(profile.TradeName, normalizedTradeName, StringComparison.CurrentCulture))
            {
                profile.TradeName = normalizedTradeName;
                changed = true;
            }

            if (!profile.IsActive)
            {
                profile.IsActive = true;
                changed = true;
            }

            if (changed)
            {
                profile.IsDirty = true;
                profile.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
    }

    private static async Task EnsureOfficeAsync(LocalDbContext db, string code, string name, bool isHeadOffice)
    {
        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(code, out var normalizedCode))
            return;

        var existing = await db.Offices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(office => office.Code == normalizedCode);

        if (existing is null)
        {
            db.Offices.Add(new LocalOffice
            {
                Code = normalizedCode,
                Name = OfficeCodeCatalog.GetOfficeDisplayName(normalizedCode),
                IsHeadOffice = isHeadOffice,
                IsDeleted = false,
                IsDirty = false
            });
            return;
        }

        existing.Code = normalizedCode;
        existing.Name = OfficeCodeCatalog.GetOfficeDisplayName(normalizedCode);
        existing.IsHeadOffice = normalizedCode == DomainConstants.DefaultOfficeUsenet;
        existing.IsDeleted = false;
        existing.IsDirty = false;
    }

    private static async Task NormalizeLegacyOfficeCodesAsync(LocalDbContext db)
    {
        var offices = await db.Offices.IgnoreQueryFilters().ToListAsync();
        var canonicalMap = CanonicalOffices.ToDictionary(
            definition => definition.Code,
            definition => offices.First(office => string.Equals(office.Code, definition.Code, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        var yeonsuSetting = await db.Settings.FirstOrDefaultAsync(setting => setting.Key == YeonsuOfficeIdSettingKey);

        foreach (var office in offices)
        {
            var canonicalCode = ResolveCanonicalOfficeCode(office.Code, office.Name, office.IsHeadOffice);
            var canonicalOffice = canonicalMap[canonicalCode];
            var isCanonicalRow = office.Id == canonicalOffice.Id;

            if (isCanonicalRow)
            {
                office.Code = canonicalCode;
                office.Name = OfficeCodeCatalog.GetOfficeDisplayName(canonicalCode);
                office.IsHeadOffice = string.Equals(canonicalCode, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase);
                office.IsDeleted = false;
                office.IsDirty = false;
                continue;
            }

            foreach (var warehouse in await db.Warehouses.IgnoreQueryFilters()
                         .Where(warehouse => warehouse.OfficeId == office.Id || warehouse.OfficeCode == office.Code)
                         .ToListAsync())
            {
                warehouse.OfficeId = canonicalOffice.Id;
                warehouse.OfficeCode = canonicalCode;
                warehouse.IsDirty = true;
                warehouse.UpdatedAtUtc = DateTime.UtcNow;
            }

            if (yeonsuSetting is not null &&
                Guid.TryParse(yeonsuSetting.Value, out var yeonsuOfficeId) &&
                yeonsuOfficeId == office.Id &&
                string.Equals(canonicalCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))
            {
                yeonsuSetting.Value = canonicalOffice.Id.ToString("D");
            }

            db.Offices.Remove(office);
        }

        await UpsertSettingAsync(db, YeonsuOfficeIdSettingKey, canonicalMap[OfficeCodeCatalog.Yeonsu].Id.ToString("D"));
    }

    private static LocalOffice? ResolveHeadOffice(IReadOnlyCollection<LocalOffice> offices)
        => offices.FirstOrDefault(o => o.IsHeadOffice)
           ?? offices.FirstOrDefault(o => string.Equals(o.Code, DomainConstants.DefaultOfficeUsenet, StringComparison.OrdinalIgnoreCase))
           ?? offices.FirstOrDefault();

    private static async Task<LocalOffice?> ResolveYeonsuOfficeAsync(LocalDbContext db, IReadOnlyCollection<LocalOffice> offices)
    {
        var setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == YeonsuOfficeIdSettingKey);
        if (setting is not null && Guid.TryParse(setting.Value, out var officeId))
        {
            var mappedOffice = offices.FirstOrDefault(o => o.Id == officeId);
            if (mappedOffice is not null)
                return mappedOffice;
        }

        var fallbackOffice = offices.FirstOrDefault(o => string.Equals(o.Code, DomainConstants.DefaultOfficeYeonsu, StringComparison.OrdinalIgnoreCase))
                             ?? offices.FirstOrDefault(o => !o.IsHeadOffice);

        if (fallbackOffice is not null)
        {
            await UpsertSettingAsync(db, YeonsuOfficeIdSettingKey, fallbackOffice.Id.ToString("D"));
            await db.SaveChangesAsync();
        }

        return fallbackOffice;
    }

    private static async Task UpsertSettingAsync(LocalDbContext db, string key, string value)
    {
        var setting = db.Settings.Local.FirstOrDefault(current => string.Equals(current.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Key == key);
        if (setting is null)
        {
            db.Settings.Add(new LocalSetting
            {
                Key = key,
                Value = value
            });
            return;
        }

        setting.Value = value;
    }

    private static async Task NormalizeOfficeReferenceDataAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;

        foreach (var profile in await db.CompanyProfiles.IgnoreQueryFilters().ToListAsync())
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(profile.OfficeCode, profile.ProfileName);
            if (!string.Equals(profile.OfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                profile.OfficeCode = canonicalOfficeCode;
                profile.IsDirty = true;
                profile.UpdatedAtUtc = now;
            }
        }

        foreach (var customer in await db.Customers.IgnoreQueryFilters().ToListAsync())
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(customer.ResponsibleOfficeCode, null);
            if (!string.Equals(customer.ResponsibleOfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                customer.ResponsibleOfficeCode = canonicalOfficeCode;
                customer.IsDirty = true;
                customer.UpdatedAtUtc = now;
            }
        }

        foreach (var invoice in await db.Invoices.IgnoreQueryFilters().ToListAsync())
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(invoice.ResponsibleOfficeCode, null);
            if (!string.Equals(invoice.ResponsibleOfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                invoice.ResponsibleOfficeCode = canonicalOfficeCode;
                invoice.IsDirty = true;
                invoice.UpdatedAtUtc = now;
            }
        }

        foreach (var transaction in await db.Transactions.IgnoreQueryFilters().ToListAsync())
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(transaction.ResponsibleOfficeCode, null);
            if (!string.Equals(transaction.ResponsibleOfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                transaction.ResponsibleOfficeCode = canonicalOfficeCode;
                transaction.IsDirty = true;
                transaction.UpdatedAtUtc = now;
            }
        }

        foreach (var audit in await db.AuditLogs.ToListAsync())
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(audit.OfficeCode, null);
            if (!string.Equals(audit.OfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                audit.OfficeCode = canonicalOfficeCode;
                audit.CreatedAtUtc = now;
            }
        }

        foreach (var setting in await db.Settings.ToListAsync())
        {
            if (!string.Equals(setting.Key, "CachedSession_OfficeCode", StringComparison.OrdinalIgnoreCase))
                continue;

            var canonicalOfficeCode = ResolveCanonicalOfficeCode(setting.Value, null);
            if (!string.Equals(setting.Value, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase))
                setting.Value = canonicalOfficeCode;
        }
    }

    private static async Task NormalizeWarehouseDataAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var offices = await db.Offices.IgnoreQueryFilters()
            .Where(office => !office.IsDeleted &&
                             (office.Code == OfficeCodeCatalog.Usenet ||
                              office.Code == OfficeCodeCatalog.Itworld ||
                              office.Code == OfficeCodeCatalog.Yeonsu))
            .ToListAsync();
        var officeMap = offices.ToDictionary(office => office.Code, StringComparer.OrdinalIgnoreCase);

        var warehouses = await db.Warehouses.IgnoreQueryFilters().ToListAsync();
        foreach (var warehouse in warehouses)
        {
            var canonicalOfficeCode = ResolveCanonicalOfficeCode(warehouse.OfficeCode, warehouse.Name);
            if (!officeMap.TryGetValue(canonicalOfficeCode, out var office))
                continue;

            var canonicalWarehouseCode = ResolveCanonicalWarehouseCode(warehouse.Code, canonicalOfficeCode, warehouse.Name);
            var existingCanonical = warehouses.FirstOrDefault(current =>
                current.Id != warehouse.Id &&
                string.Equals(current.Code, canonicalWarehouseCode, StringComparison.OrdinalIgnoreCase));

            if (existingCanonical is not null)
            {
                await RepointWarehouseReferencesAsync(db, warehouse.Code, existingCanonical.Code);
                db.Warehouses.Remove(warehouse);
                continue;
            }

            warehouse.OfficeId = office.Id;
            warehouse.OfficeCode = canonicalOfficeCode;
            warehouse.Code = canonicalWarehouseCode;
            warehouse.Name = ResolveWarehouseDisplayName(canonicalWarehouseCode);
            warehouse.IsActive = true;
            warehouse.IsDeleted = false;
            warehouse.IsDirty = false;
            warehouse.UpdatedAtUtc = now;
        }

        foreach (var definition in CanonicalOffices)
        {
            if (!officeMap.TryGetValue(definition.Code, out var office))
                continue;

            await EnsureWarehouseAsync(db, office, definition.WarehouseCode, definition.WarehouseName);
        }
    }

    private static async Task EnsureWarehouseAsync(
        LocalDbContext db,
        LocalOffice office,
        string warehouseCode,
        string warehouseName)
    {
        var existing = await db.Warehouses.IgnoreQueryFilters()
            .FirstOrDefaultAsync(warehouse => warehouse.Code == warehouseCode);

        if (existing is null)
        {
            db.Warehouses.Add(new LocalWarehouse
            {
                OfficeId = office.Id,
                OfficeCode = office.Code,
                Code = warehouseCode,
                Name = warehouseName,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            });
            return;
        }

        existing.OfficeId = office.Id;
        existing.OfficeCode = office.Code;
        existing.Name = warehouseName;
        existing.IsActive = true;
        existing.IsDeleted = false;
        existing.IsDirty = false;
        existing.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static async Task RepointWarehouseReferencesAsync(LocalDbContext db, string oldCode, string newCode)
    {
        if (string.IsNullOrWhiteSpace(oldCode) || string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var invoice in await db.Invoices.IgnoreQueryFilters()
                     .Where(current => current.SourceWarehouseCode == oldCode)
                     .ToListAsync())
        {
            invoice.SourceWarehouseCode = newCode;
            invoice.IsDirty = true;
            invoice.UpdatedAtUtc = DateTime.UtcNow;
        }

        foreach (var movement in await db.InventoryMovements
                     .Where(current => current.WarehouseCode == oldCode)
                     .ToListAsync())
        {
            movement.WarehouseCode = newCode;
            movement.CreatedAtUtc = DateTime.UtcNow;
        }

        foreach (var layer in await db.StockLayers
                     .Where(current => current.WarehouseCode == oldCode)
                     .ToListAsync())
        {
            layer.WarehouseCode = newCode;
            layer.CreatedAtUtc = DateTime.UtcNow;
        }

        foreach (var allocation in await db.CostAllocations
                     .Where(current => current.WarehouseCode == oldCode)
                     .ToListAsync())
        {
            allocation.WarehouseCode = newCode;
            allocation.CreatedAtUtc = DateTime.UtcNow;
        }

        var stocks = await db.ItemWarehouseStocks
            .Where(current => current.WarehouseCode == oldCode)
            .ToListAsync();
        foreach (var stock in stocks)
        {
            var target = await db.ItemWarehouseStocks.FindAsync(stock.ItemId, newCode);
            if (target is null)
            {
                db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
                {
                    ItemId = stock.ItemId,
                    WarehouseCode = newCode,
                    Quantity = stock.Quantity,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                db.ItemWarehouseStocks.Remove(stock);
            }
            else
            {
                target.Quantity += stock.Quantity;
                target.UpdatedAtUtc = DateTime.UtcNow;
                db.ItemWarehouseStocks.Remove(stock);
            }
        }

        foreach (var ledger in await db.SerialLedgers
                     .Where(current => current.WarehouseCode == oldCode)
                     .ToListAsync())
        {
            ledger.WarehouseCode = newCode;
            ledger.UpdatedAtUtc = DateTime.UtcNow;
        }

        foreach (var transfer in await db.InventoryTransfers.IgnoreQueryFilters()
                     .Where(current => current.FromWarehouseCode == oldCode || current.ToWarehouseCode == oldCode)
                     .ToListAsync())
        {
            if (string.Equals(transfer.FromWarehouseCode, oldCode, StringComparison.OrdinalIgnoreCase))
                transfer.FromWarehouseCode = newCode;
            if (string.Equals(transfer.ToWarehouseCode, oldCode, StringComparison.OrdinalIgnoreCase))
                transfer.ToWarehouseCode = newCode;
            transfer.IsDirty = true;
            transfer.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static string ResolveCanonicalOfficeCode(string? primary, string? secondary, bool isHeadOffice = false)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(primary, secondary, isHeadOffice ? OfficeCodeCatalog.Usenet : OfficeCodeCatalog.Yeonsu);

    private static async Task CleanupLegacyLinkedGeneralSettlementsAsync(LocalDbContext db)
    {
        var marker = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Key == LegacyLinkedGeneralSettlementCleanupKey);
        if (marker is not null && string.Equals(marker.Value, "1", StringComparison.Ordinal))
            return;

        var now = DateTime.UtcNow;
        var legacyTransactions = await db.Transactions.IgnoreQueryFilters()
            .Where(current =>
                !current.IsDeleted &&
                current.LinkedInvoiceId.HasValue &&
                (current.TransactionKind == PaymentFlowConstants.TransactionKindReceipt ||
                 current.TransactionKind == PaymentFlowConstants.TransactionKindPayment))
            .ToListAsync();

        if (legacyTransactions.Count > 0)
        {
            var legacyTransactionIds = legacyTransactions.Select(current => current.Id).ToHashSet();
            var linkedPayments = await db.Payments.IgnoreQueryFilters()
                .Where(current => legacyTransactionIds.Contains(current.Id))
                .ToListAsync();
            var linkedAttachments = await db.TransactionAttachments.IgnoreQueryFilters()
                .Where(current => legacyTransactionIds.Contains(current.TransactionId))
                .ToListAsync();

            foreach (var transaction in legacyTransactions)
            {
                transaction.LinkedInvoiceId = null;
                transaction.LinkedInvoiceNumber = string.Empty;
                transaction.SettlementAmount = 0m;
                transaction.AdvanceDelta = 0m;
                transaction.PrepaidDelta = 0m;
                transaction.IsDeleted = true;
                transaction.IsDirty = true;
                transaction.UpdatedAtUtc = now;
            }

            foreach (var payment in linkedPayments)
            {
                payment.IsDeleted = true;
                payment.IsDirty = true;
                payment.UpdatedAtUtc = now;
            }

            foreach (var attachment in linkedAttachments)
            {
                attachment.IsDeleted = true;
                attachment.IsDirty = true;
                attachment.UpdatedAtUtc = now;
            }
        }

        await UpsertSettingAsync(db, LegacyLinkedGeneralSettlementCleanupKey, "1");
    }

    private static string ResolveCanonicalWarehouseCode(string? warehouseCode, string? officeCode, string? warehouseName)
        => OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode, officeCode, ResolveCanonicalOfficeCode(officeCode, warehouseName));

    private static string ResolveWarehouseDisplayName(string warehouseCode)
        => warehouseCode switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => "ITWORLD 창고",
            OfficeCodeCatalog.YeonsuMainWarehouse => "YEONSU 창고",
            _ => "USENET 창고"
        };

    private static async Task SeedRentalDefaultsAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var defaults = new[]
        {
            new { Code = OfficeCodeCatalog.Itworld, Name = OfficeCodeCatalog.Itworld, IsSystemDefault = true },
            new { Code = OfficeCodeCatalog.Yeonsu, Name = OfficeCodeCatalog.Yeonsu, IsSystemDefault = true },
            new { Code = OfficeCodeCatalog.Usenet, Name = OfficeCodeCatalog.Usenet, IsSystemDefault = true }
        };

        foreach (var definition in defaults)
        {
            var current = await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(company => company.Code == definition.Code);

            if (current is null)
            {
                db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
                {
                    Code = definition.Code,
                    Name = definition.Name,
                    IsSystemDefault = definition.IsSystemDefault,
                    IsActive = true,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                continue;
            }

            current.Code = ResolveCanonicalOfficeCode(current.Code, current.Name);
            current.Name = definition.Name;
            current.IsSystemDefault = current.IsSystemDefault || definition.IsSystemDefault;
            current.IsActive = true;
            current.IsDeleted = false;
            current.IsDirty = false;
            current.UpdatedAtUtc = now;
        }

        await NormalizeRentalManagementCompaniesAsync(db);

        await UpsertSettingAsync(db, "Rental.AlertDaysBefore", "7,3,1,0");
        await UpsertSettingAsync(db, "Rental.ImportBillingWorkbookPath", string.Empty);
        await UpsertSettingAsync(db, "Rental.ImportAssetWorkbookPath", string.Empty);
    }

    private static async Task NormalizeRentalManagementCompaniesAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var companies = db.RentalManagementCompanies.Local
            .Concat(await db.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync())
            .GroupBy(company => company.Id)
            .Select(group => group.First())
            .ToList();
        foreach (var definition in CanonicalOffices)
        {
            var matches = companies
                .Where(company => string.Equals(ResolveCanonicalOfficeCode(company.Code, company.Name), definition.Code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
                continue;

            var keeper = matches.FirstOrDefault(company =>
                             string.Equals(company.Code, definition.Code, StringComparison.OrdinalIgnoreCase))
                         ?? matches.First();

            keeper.Code = definition.Code;
            keeper.Name = definition.Code;
            keeper.IsSystemDefault = true;
            keeper.IsActive = true;
            keeper.IsDeleted = false;
            keeper.IsDirty = false;
            keeper.UpdatedAtUtc = now;

            foreach (var duplicate in matches.Where(company => company.Id != keeper.Id))
                db.RentalManagementCompanies.Remove(duplicate);
        }
    }

    private static async Task NormalizeRentalOfficeDataAsync(LocalDbContext db)
    {
        var billingProfiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();
        foreach (var profile in billingProfiles)
        {
            var officeCode = NormalizeRentalOfficeCode(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode);
            var changed = false;

            if (!string.Equals(profile.ResponsibleOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                profile.ResponsibleOfficeCode = officeCode;
                changed = true;
            }

            if (!string.Equals(profile.ManagementCompanyCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                profile.ManagementCompanyCode = officeCode;
                changed = true;
            }

            if (changed)
            {
                profile.IsDirty = true;
                profile.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        foreach (var asset in assets)
        {
            var officeCode = NormalizeRentalOfficeCode(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode);
            var changed = false;

            if (!string.Equals(asset.ResponsibleOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                asset.ResponsibleOfficeCode = officeCode;
                changed = true;
            }

            if (!string.Equals(asset.ManagementCompanyCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                asset.ManagementCompanyCode = officeCode;
                changed = true;
            }

            if (asset.ItemId == Guid.Empty)
            {
                asset.ItemId = null;
                changed = true;
            }

            if (string.Equals((asset.AssetStatus ?? string.Empty).Trim(), "설치중", StringComparison.OrdinalIgnoreCase))
            {
                asset.AssetStatus = "임대진행중";
                changed = true;
            }

            if (changed)
            {
                asset.IsDirty = true;
                asset.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        var billingLogs = await db.RentalBillingLogs.IgnoreQueryFilters().ToListAsync();
        foreach (var log in billingLogs)
        {
            var officeCode = NormalizeRentalOfficeCode(log.ResponsibleOfficeCode, string.Empty);
            if (string.Equals(log.ResponsibleOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase))
                continue;

            log.ResponsibleOfficeCode = officeCode;
            log.IsDirty = true;
            log.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static async Task NormalizeItemCategoryOptionDuplicatesAsync(LocalDbContext db)
    {
        var options = await db.ItemCategoryOptions.IgnoreQueryFilters()
            .OrderBy(option => option.SortOrder)
            .ThenBy(option => option.CreatedAtUtc)
            .ThenBy(option => option.Name)
            .ToListAsync();
        if (options.Count == 0)
            return;

        var canonicalByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var group in options
                     .Where(option => !option.IsDeleted && !string.IsNullOrWhiteSpace(option.Name))
                     .GroupBy(option => RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name), StringComparer.OrdinalIgnoreCase)
                     .Where(group => !string.IsNullOrWhiteSpace(group.Key)))
        {
            var canonical = group
                .OrderBy(option => option.SortOrder)
                .ThenByDescending(option => option.IsSystemDefault)
                .ThenBy(option => option.CreatedAtUtc)
                .First();
            var canonicalName = SelectionOptionDefaults.NormalizeItemCategoryName(canonical.Name);
            canonicalByKey[group.Key] = canonicalName;

            if (!string.Equals(canonical.Name, canonicalName, StringComparison.Ordinal))
            {
                canonical.Name = canonicalName;
                canonical.IsDirty = true;
                canonical.UpdatedAtUtc = now;
            }

            foreach (var duplicate in group.Where(option => option.Id != canonical.Id))
            {
                db.ItemCategoryOptions.Remove(duplicate);
            }
        }

        foreach (var deletedOption in options.Where(option =>
                     option.IsDeleted &&
                     !string.IsNullOrWhiteSpace(option.Name) &&
                     canonicalByKey.ContainsKey(RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name))))
        {
            db.ItemCategoryOptions.Remove(deletedOption);
        }

        if (canonicalByKey.Count == 0)
            return;

        var items = await db.Items.IgnoreQueryFilters().ToListAsync();
        foreach (var item in items)
        {
            var key = RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName);
            if (string.IsNullOrWhiteSpace(key) || !canonicalByKey.TryGetValue(key, out var canonicalName))
                continue;

            if (string.Equals(item.CategoryName, canonicalName, StringComparison.Ordinal))
                continue;

            item.CategoryName = canonicalName;
            item.IsDirty = true;
            item.UpdatedAtUtc = now;
        }

        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        foreach (var asset in assets)
        {
            var key = RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName);
            if (string.IsNullOrWhiteSpace(key) || !canonicalByKey.TryGetValue(key, out var canonicalName))
                continue;

            if (string.Equals(asset.ItemCategoryName, canonicalName, StringComparison.Ordinal))
                continue;

            asset.ItemCategoryName = canonicalName;
            asset.IsDirty = true;
            asset.UpdatedAtUtc = now;
        }
    }

    private static string NormalizeRentalOfficeCode(string? primary, string? secondary)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(primary, secondary, DomainConstants.DefaultOfficeUsenet);

    private static async Task EnsureLegacyRentalNamingColumnsAsync(LocalDbContext db)
    {
        await EnsureRenamedTextColumnAsync(db, "RentalAssets", "ProductCategory", "ItemCategoryName", "TEXT NOT NULL DEFAULT ''");
        await EnsureRenamedTextColumnAsync(db, "RentalAssets", "ModelName", "ItemName", "TEXT NOT NULL DEFAULT ''");
        await EnsureRenamedTextColumnAsync(db, "RentalBillingProfiles", "ModelName", "ItemName", "TEXT NOT NULL DEFAULT ''");
    }

    private static async Task BackfillItemScopeFieldsAsync(LocalDbContext db)
    {
        var items = await db.Items.IgnoreQueryFilters().ToListAsync();
        if (items.Count == 0)
            return;

        var rentalOfficeMap = (await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue)
                .Select(asset => new
                {
                    ItemId = asset.ItemId!.Value,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode
                })
                .ToListAsync())
            .Select(asset => new
            {
                asset.ItemId,
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode,
                    DomainConstants.OfficeUsenet)
            })
            .ToList();

        var rentalOfficeLookup = rentalOfficeMap
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code))
                    ?? DomainConstants.OfficeUsenet);

        var warehouseOfficeMap = await db.ItemWarehouseStocks
            .AsNoTracking()
            .Select(stock => new
            {
                stock.ItemId,
                OfficeCode = ResolveOfficeCodeFromWarehouseCode(stock.WarehouseCode)
            })
            .ToListAsync();

        var warehouseOfficeLookup = warehouseOfficeMap
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OfficeCode))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var invoiceOfficeMap = (await (
                from line in db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in db.Invoices.IgnoreQueryFilters().AsNoTracking() on line.InvoiceId equals invoice.Id
                where !line.IsDeleted && !invoice.IsDeleted && line.ItemId.HasValue
                select new
                {
                    ItemId = line.ItemId!.Value,
                    invoice.ResponsibleOfficeCode
                })
            .ToListAsync())
            .Select(entry => new
            {
                entry.ItemId,
                OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entry.ResponsibleOfficeCode, OfficeCodeCatalog.Shared)
            })
            .ToList();

        var invoiceOfficeLookup = invoiceOfficeMap
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OfficeCode))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var changed = false;
        foreach (var item in items)
        {
            var desiredOfficeCode = ResolveDesiredItemOfficeCode(item, rentalOfficeLookup, warehouseOfficeLookup, invoiceOfficeLookup);
            var desiredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                item.TenantCode,
                desiredOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                desiredOfficeCode);

            if (!string.Equals(item.OfficeCode, desiredOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                item.OfficeCode = desiredOfficeCode;
                changed = true;
            }

            if (!string.Equals(item.TenantCode, desiredTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                item.TenantCode = desiredTenantCode;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static string ResolveDesiredItemOfficeCode(
        LocalItem item,
        IReadOnlyDictionary<Guid, string> rentalOfficeLookup,
        IReadOnlyDictionary<Guid, List<string>> warehouseOfficeLookup,
        IReadOnlyDictionary<Guid, List<string>> invoiceOfficeLookup)
    {
        if (rentalOfficeLookup.TryGetValue(item.Id, out var rentalOfficeCode) &&
            !string.IsNullOrWhiteSpace(rentalOfficeCode))
        {
            return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(rentalOfficeCode, DomainConstants.OfficeUsenet);
        }

        if (warehouseOfficeLookup.TryGetValue(item.Id, out var warehouseOffices) && warehouseOffices.Count > 0)
        {
            return warehouseOffices.Count == 1
                ? OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(warehouseOffices[0], OfficeCodeCatalog.Shared)
                : OfficeCodeCatalog.Shared;
        }

        if (invoiceOfficeLookup.TryGetValue(item.Id, out var invoiceOffices) && invoiceOffices.Count > 0)
        {
            return invoiceOffices.Count == 1
                ? OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(invoiceOffices[0], OfficeCodeCatalog.Shared)
                : OfficeCodeCatalog.Shared;
        }

        return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared);
    }

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        return normalizedWarehouseCode switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.YeonsuMainWarehouse => OfficeCodeCatalog.Yeonsu,
            _ => OfficeCodeCatalog.Usenet
        };
    }

    private static async Task BackfillItemOperationalFieldsAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var items = await db.Items.IgnoreQueryFilters().ToListAsync();

        foreach (var item in items)
        {
            var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental);
            var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(item.ItemKind, normalizedTrackingType, item.CategoryName, item.IsRental);
            var shouldRemoveInventoryStock = !ItemOperationalPolicy.SupportsInventory(normalizedTrackingType);
            var changed = false;

            if (!string.Equals(item.TrackingType, normalizedTrackingType, StringComparison.Ordinal))
            {
                item.TrackingType = normalizedTrackingType;
                changed = true;
            }

            if (!string.Equals(item.ItemKind, normalizedItemKind, StringComparison.Ordinal))
            {
                item.ItemKind = normalizedItemKind;
                changed = true;
            }

            var expectedIsRental = string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal);
            var expectedIsSale = !string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal);

            if (item.IsRental != expectedIsRental)
            {
                item.IsRental = expectedIsRental;
                changed = true;
            }

            if (item.IsSale != expectedIsSale)
            {
                item.IsSale = expectedIsSale;
                changed = true;
            }

            if (shouldRemoveInventoryStock && item.CurrentStock != 0m)
            {
                item.CurrentStock = 0m;
                changed = true;
            }

            if (changed)
            {
                item.IsDirty = true;
                item.UpdatedAtUtc = now;
            }
        }

        var nonStockOrAssetItemIds = items
            .Where(item => !item.IsDeleted && !ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            .Select(item => item.Id)
            .ToList();

        if (nonStockOrAssetItemIds.Count > 0)
        {
            var warehouseStocks = await db.ItemWarehouseStocks
                .Where(stock => nonStockOrAssetItemIds.Contains(stock.ItemId))
                .ToListAsync();
            if (warehouseStocks.Count > 0)
                db.ItemWarehouseStocks.RemoveRange(warehouseStocks);
        }

        await db.SaveChangesAsync();
    }

    private static async Task BackfillInvoiceLineTrackingTypesAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var itemTrackingMap = await db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => !item.IsDeleted)
            .ToDictionaryAsync(
                item => item.Id,
                item => ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental));

        var lines = await db.InvoiceLines
            .IgnoreQueryFilters()
            .ToListAsync();
        var changedInvoiceIds = new HashSet<Guid>();

        foreach (var line in lines)
        {
            var normalizedTrackingType = ResolveInvoiceLineTrackingType(line, itemTrackingMap);
            if (string.Equals(line.ItemTrackingType, normalizedTrackingType, StringComparison.Ordinal))
                continue;

            line.ItemTrackingType = normalizedTrackingType;
            if (line.InvoiceId != Guid.Empty)
                changedInvoiceIds.Add(line.InvoiceId);
        }

        if (changedInvoiceIds.Count > 0)
        {
            var invoices = await db.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => changedInvoiceIds.Contains(invoice.Id))
                .ToListAsync();
            foreach (var invoice in invoices)
            {
                invoice.IsDirty = true;
                invoice.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync();
    }

    private static string ResolveInvoiceLineTrackingType(
        LocalInvoiceLine line,
        IReadOnlyDictionary<Guid, string> itemTrackingMap)
    {
        if (!string.IsNullOrWhiteSpace(line.ItemTrackingType))
            return ItemTrackingTypes.Normalize(
                line.ItemTrackingType,
                line.ItemId.HasValue ? ItemTrackingTypes.Stock : ItemTrackingTypes.NonStock);

        if (line.ItemId.HasValue &&
            itemTrackingMap.TryGetValue(line.ItemId.Value, out var trackingType) &&
            !string.IsNullOrWhiteSpace(trackingType))
        {
            return ItemTrackingTypes.Normalize(trackingType);
        }

        return line.ItemId.HasValue
            ? ItemTrackingTypes.Stock
            : ItemTrackingTypes.NonStock;
    }

    private static async Task EnsureRenamedTextColumnAsync(
        LocalDbContext db,
        string tableName,
        string oldColumnName,
        string newColumnName,
        string definition)
    {
        if (!IsSafeSqlIdentifier(tableName) ||
            !IsSafeSqlIdentifier(oldColumnName) ||
            !IsSafeSqlIdentifier(newColumnName))
        {
            return;
        }

        var quotedTableName = QuoteSqlIdentifier(tableName);
        var quotedOldColumnName = QuoteSqlIdentifier(oldColumnName);
        var quotedNewColumnName = QuoteSqlIdentifier(newColumnName);

        var hasNewColumn = await HasColumnAsync(db, tableName, newColumnName);
        var hasOldColumn = await HasColumnAsync(db, tableName, oldColumnName);

        if (!hasNewColumn && hasOldColumn)
        {
            try
            {
                var renameSql = "ALTER TABLE " + quotedTableName + " RENAME COLUMN " + quotedOldColumnName + " TO " + quotedNewColumnName + ";";
                await db.Database.ExecuteSqlRawAsync(renameSql);
            }
            catch
            {
                // Fallback to copy migration below.
            }

            hasNewColumn = await HasColumnAsync(db, tableName, newColumnName);
            hasOldColumn = await HasColumnAsync(db, tableName, oldColumnName);
        }

        if (!hasNewColumn)
        {
            await TryAddColumnAsync(db, tableName, newColumnName, definition);
            hasNewColumn = await HasColumnAsync(db, tableName, newColumnName);
        }

        if (!hasNewColumn || !hasOldColumn)
            return;

        try
        {
            var copySql =
                "UPDATE " + quotedTableName + Environment.NewLine +
                "SET " + quotedNewColumnName + " = CASE" + Environment.NewLine +
                "    WHEN COALESCE(TRIM(" + quotedNewColumnName + "), '') = '' THEN COALESCE(" + quotedOldColumnName + ", '')" + Environment.NewLine +
                "    ELSE " + quotedNewColumnName + Environment.NewLine +
                "END" + Environment.NewLine +
                "WHERE COALESCE(TRIM(" + quotedOldColumnName + "), '') <> '';";
            await db.Database.ExecuteSqlRawAsync(copySql);
        }
        catch
        {
            // Ignore copy failures on partially migrated databases.
        }
    }

    private static async Task<bool> HasColumnAsync(LocalDbContext db, string tableName, string columnName)
    {
        await db.Database.OpenConnectionAsync();

        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task TryAddColumnAsync(LocalDbContext db, string table, string column, string definition)
    {
        if (!IsSafeSqlIdentifier(table) || !IsSafeSqlIdentifier(column) || string.IsNullOrWhiteSpace(definition))
        {
            return;
        }

        try
        {
            var sql = "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + definition;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // Column may already exist on existing databases.
        }
    }

    private static bool IsSafeSqlIdentifier(string value)
        => !string.IsNullOrWhiteSpace(value) && SqlIdentifierPattern.IsMatch(value);

    private static string QuoteSqlIdentifier(string value)
        => "\"" + value + "\"";

    private static async Task TryCreateIndexAsync(LocalDbContext db, string sql)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task NormalizeLegacyDateTimeColumnsAsync(LocalDbContext db)
    {
        await TryNormalizeDateTimeTextColumnAsync(db, "CompanyProfiles", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CompanyProfiles", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Units", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Units", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CustomerCategories", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CustomerCategories", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CustomerMasters", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CustomerMasters", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Customers", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Customers", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Items", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Items", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Invoices", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Invoices", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Invoices", "LastSavedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Payments", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Payments", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RecentSelections", "LastUsedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "AttachmentSelections", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Transactions", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Transactions", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Offices", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Offices", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Warehouses", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "Warehouses", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "InventoryMovements", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "StockLayers", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "CostAllocations", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "ItemWarehouseStocks", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "SerialLedgers", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalManagementCompanies", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalManagementCompanies", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalBillingProfiles", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalBillingProfiles", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalAssets", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalAssets", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalBillingLogs", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "RentalBillingLogs", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "InventoryTransfers", "CreatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "InventoryTransfers", "UpdatedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "InventoryTransfers", "LastSavedAtUtc");
        await TryNormalizeDateTimeTextColumnAsync(db, "AuditLogs", "CreatedAtUtc");
    }

    private static async Task NormalizeCustomerTradeTypeAsync(LocalDbContext db)
    {
        const string sql = """
            UPDATE "Customers"
            SET "TradeType" = CASE
                WHEN COALESCE(TRIM("TradeType"), '') IN ('', '판매', '매출처', '매출') THEN '매출'
                WHEN COALESCE(TRIM("TradeType"), '') IN ('매입처', '매입') THEN '매입'
                WHEN COALESCE(TRIM("TradeType"), '') IN ('판매/매입', '매출/매입', '매입/매출') THEN '매출/매입'
                ELSE TRIM("TradeType")
            END;
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task RepairCustomerClassificationIntegrityAsync(LocalDbContext db)
    {
        var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();
        if (customers.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var customer in customers)
        {
            var customerChanged = false;
            var rawTradeType = (customer.TradeType ?? string.Empty).Trim();
            if (CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(rawTradeType, out var category, out var normalizedCompositeTradeType))
            {
                if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
                {
                    customer.CategoryId = category.Id;
                    customerChanged = true;
                }

                if (!string.Equals(customer.TradeType, normalizedCompositeTradeType, StringComparison.CurrentCulture))
                {
                    customer.TradeType = normalizedCompositeTradeType;
                    customerChanged = true;
                }
            }
            else if (CustomerClassificationNormalizer.TryResolveCategory(rawTradeType, out var standaloneCategory))
            {
                if (!customer.CategoryId.HasValue || customer.CategoryId == Guid.Empty)
                {
                    customer.CategoryId = standaloneCategory.Id;
                    customerChanged = true;
                }

                if (!string.Equals(customer.TradeType, CustomerClassificationNormalizer.Sales, StringComparison.CurrentCulture))
                {
                    customer.TradeType = CustomerClassificationNormalizer.Sales;
                    customerChanged = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(rawTradeType) &&
                     !CustomerClassificationNormalizer.TryNormalizeTradeType(rawTradeType, out _) &&
                     !string.Equals(customer.TradeType, CustomerClassificationNormalizer.Sales, StringComparison.CurrentCulture))
            {
                customer.TradeType = CustomerClassificationNormalizer.Sales;
                customerChanged = true;
            }

            if (!customerChanged)
                continue;

            customer.IsDirty = true;
            customer.UpdatedAtUtc = now;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task NormalizeTradeTypeOptionCatalogAsync(LocalDbContext db)
    {
        var options = await db.TradeTypeOptions.IgnoreQueryFilters().ToListAsync();
        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var definition in SelectionOptionDefaults.DefaultTradeTypes)
        {
            var option = options.FirstOrDefault(current => current.Id == definition.Id)
                ?? options.FirstOrDefault(current => string.Equals(current.Name?.Trim(), definition.Name, StringComparison.CurrentCultureIgnoreCase));

            if (option is null)
            {
                db.TradeTypeOptions.Add(new LocalTradeTypeOption
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    AllowsSales = definition.AllowsSales,
                    AllowsPurchase = definition.AllowsPurchase,
                    SortOrder = definition.SortOrder,
                    IsSystemDefault = false,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                changed = true;
                continue;
            }

            var optionChanged = false;
            if (!string.Equals(option.Name, definition.Name, StringComparison.CurrentCulture))
            {
                option.Name = definition.Name;
                optionChanged = true;
            }

            if (option.AllowsSales != definition.AllowsSales)
            {
                option.AllowsSales = definition.AllowsSales;
                optionChanged = true;
            }

            if (option.AllowsPurchase != definition.AllowsPurchase)
            {
                option.AllowsPurchase = definition.AllowsPurchase;
                optionChanged = true;
            }

            if (option.SortOrder != definition.SortOrder)
            {
                option.SortOrder = definition.SortOrder;
                optionChanged = true;
            }

            if (!option.IsActive)
            {
                option.IsActive = true;
                optionChanged = true;
            }

            if (option.IsDeleted)
            {
                option.IsDeleted = false;
                optionChanged = true;
            }

            if (optionChanged)
            {
                option.IsDirty = true;
                option.UpdatedAtUtc = now;
                changed = true;
            }
        }

        foreach (var option in options.Where(option =>
                     !option.IsDeleted &&
                     CustomerClassificationNormalizer.TradeTypeDefinition.Find(option.Name) is null))
        {
            option.IsDeleted = true;
            option.IsActive = false;
            option.IsDirty = true;
            option.UpdatedAtUtc = now;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task BackfillCustomerScopeFieldsAsync(LocalDbContext db)
    {
        var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();
        if (customers.Count == 0)
            return;

        var changed = false;
        foreach (var customer in customers)
        {
            var desiredOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customer.ResponsibleOfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                customer.TenantCode,
                desiredOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                desiredOfficeCode);

            if (!string.Equals(customer.ResponsibleOfficeCode, desiredOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                customer.ResponsibleOfficeCode = desiredOfficeCode;
                changed = true;
            }

            if (!string.Equals(customer.TenantCode, desiredTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                customer.TenantCode = desiredTenantCode;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task BackfillCustomerMasterScopeFieldsAsync(LocalDbContext db)
    {
        var customerMasters = await db.CustomerMasters.IgnoreQueryFilters().ToListAsync();
        if (customerMasters.Count == 0)
            return;

        var customersByMasterId = await db.Customers.IgnoreQueryFilters()
            .Where(customer => customer.CustomerMasterId.HasValue)
            .Select(customer => new
            {
                CustomerMasterId = customer.CustomerMasterId!.Value,
                customer.ResponsibleOfficeCode,
                customer.TenantCode
            })
            .ToListAsync();

        var referenceLookup = customersByMasterId
            .GroupBy(entry => entry.CustomerMasterId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var changed = false;
        foreach (var customerMaster in customerMasters)
        {
            var desiredOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                customerMaster.OfficeCode,
                OfficeCodeCatalog.Shared);
            var desiredTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                customerMaster.TenantCode,
                desiredOfficeCode,
                TenantScopeCatalog.UsenetGroup,
                desiredOfficeCode);

            if (referenceLookup.TryGetValue(customerMaster.Id, out var references) && references.Count > 0)
            {
                var officeCodes = references
                    .Select(entry => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entry.ResponsibleOfficeCode, OfficeCodeCatalog.Shared))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                desiredOfficeCode = officeCodes.Count == 1
                    ? officeCodes[0]
                    : OfficeCodeCatalog.Shared;

                var tenantCodes = references
                    .Select(entry => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entry.TenantCode, entry.ResponsibleOfficeCode))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                desiredTenantCode = tenantCodes.Count == 1
                    ? tenantCodes[0]
                    : TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, desiredOfficeCode);
            }

            if (!string.Equals(customerMaster.OfficeCode, desiredOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                customerMaster.OfficeCode = desiredOfficeCode;
                changed = true;
            }

            if (!string.Equals(customerMaster.TenantCode, desiredTenantCode, StringComparison.OrdinalIgnoreCase))
            {
                customerMaster.TenantCode = desiredTenantCode;
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static async Task BackfillTransactionResponsibleOfficeCodeAsync(LocalDbContext db)
    {
        const string sql = """
            UPDATE "Transactions"
            SET "ResponsibleOfficeCode" = COALESCE(
                NULLIF((
                    SELECT "ResponsibleOfficeCode"
                    FROM "Customers"
                    WHERE "Customers"."Id" = "Transactions"."CustomerId"
                ), ''),
                'USENET')
            WHERE COALESCE("ResponsibleOfficeCode", '') = '';
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task TryNormalizeDateTimeTextColumnAsync(LocalDbContext db, string table, string column)
    {
        if (!IsSafeSqlIdentifier(table) || !IsSafeSqlIdentifier(column))
        {
            return;
        }

        try
        {
            var sql = "UPDATE \"" + table + "\" " +
                      "SET \"" + column + "\" = '" + FallbackUtcText + "' " +
                      "WHERE \"" + column + "\" IS NULL OR TRIM(\"" + column + "\") = ''";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch
        {
            // Table/column may not exist on old partial schemas.
        }
    }

    private static async Task TryCreateAttachmentSelectionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "AttachmentSelections" (
                                              "CustomerKey" TEXT NOT NULL,
                                              "DocCode" TEXT NOT NULL,
                                              "IsChecked" INTEGER NOT NULL DEFAULT 0,
                                              "OrderIndex" INTEGER NULL,
                                              "UpdatedAtUtc" TEXT NOT NULL,
                                              CONSTRAINT "PK_AttachmentSelections" PRIMARY KEY ("CustomerKey", "DocCode")
                                          );
                                          """;
            await db.Database.ExecuteSqlRawAsync(createTableSql);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_AttachmentSelections_CustomerKey\" ON \"AttachmentSelections\" (\"CustomerKey\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateSyncDiagnosticEventsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "SyncDiagnosticEvents" (
                                              "Id" TEXT NOT NULL CONSTRAINT "PK_SyncDiagnosticEvents" PRIMARY KEY,
                                              "OccurredAtUtc" TEXT NOT NULL,
                                              "LastOccurredAtUtc" TEXT NOT NULL,
                                              "OccurrenceCount" INTEGER NOT NULL DEFAULT 1,
                                              "Severity" TEXT NOT NULL DEFAULT 'Error',
                                              "Category" TEXT NOT NULL DEFAULT '',
                                              "Subcategory" TEXT NOT NULL DEFAULT '',
                                              "EntityName" TEXT NOT NULL DEFAULT '',
                                              "EntityId" TEXT NOT NULL DEFAULT '',
                                              "ReferenceEntityName" TEXT NOT NULL DEFAULT '',
                                              "ReferenceEntityId" TEXT NOT NULL DEFAULT '',
                                              "UserName" TEXT NOT NULL DEFAULT '',
                                              "OfficeCode" TEXT NOT NULL DEFAULT '',
                                              "TenantCode" TEXT NOT NULL DEFAULT '',
                                              "MachineName" TEXT NOT NULL DEFAULT '',
                                              "AppVersion" TEXT NOT NULL DEFAULT '',
                                              "SyncPhase" TEXT NOT NULL DEFAULT '',
                                              "RawMessage" TEXT NOT NULL DEFAULT '',
                                              "NormalizedMessage" TEXT NOT NULL DEFAULT '',
                                              "StackTrace" TEXT NOT NULL DEFAULT '',
                                              "IsRecoverable" INTEGER NOT NULL DEFAULT 0,
                                              "RecoveryAction" TEXT NOT NULL DEFAULT '',
                                              "RecoveryAttempted" INTEGER NOT NULL DEFAULT 0,
                                              "RecoverySucceeded" INTEGER NOT NULL DEFAULT 0,
                                              "ResolvedAtUtc" TEXT NULL,
                                              "Status" TEXT NOT NULL DEFAULT 'Open',
                                              "LastKnownSyncRevision" INTEGER NOT NULL DEFAULT 0,
                                              "LastKnownSyncError" TEXT NOT NULL DEFAULT '',
                                              "DirtyCustomerMasterCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyCustomerCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyInvoiceCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyTransactionCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyAttachmentCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyPaymentCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyRentalAssetCount" INTEGER NOT NULL DEFAULT 0,
                                              "DirtyInventoryTransferCount" INTEGER NOT NULL DEFAULT 0,
                                              "MissingCustomerReferenceCount" INTEGER NOT NULL DEFAULT 0,
                                              "MissingInvoiceReferenceCount" INTEGER NOT NULL DEFAULT 0,
                                              "MissingTransactionReferenceCount" INTEGER NOT NULL DEFAULT 0,
                                              "MissingRentalItemReferenceCount" INTEGER NOT NULL DEFAULT 0
                                          );
                                          """;
            await db.Database.ExecuteSqlRawAsync(createTableSql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncDiagnosticEvents_LastOccurredAtUtc\" ON \"SyncDiagnosticEvents\" (\"LastOccurredAtUtc\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncDiagnosticEvents_Status_LastOccurredAtUtc\" ON \"SyncDiagnosticEvents\" (\"Status\", \"LastOccurredAtUtc\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncDiagnosticEvents_Category_Subcategory\" ON \"SyncDiagnosticEvents\" (\"Category\", \"Subcategory\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_SyncDiagnosticEvents_SyncPhase_Status\" ON \"SyncDiagnosticEvents\" (\"SyncPhase\", \"Status\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateSyncDiagnosticEventsTableAsync), ex);
        }
    }

    private static async Task TryCreateTransactionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "Transactions" (
                                              "Id" TEXT NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY,
                                              "CustomerId" TEXT NOT NULL,
                                              "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                                              "TransactionDate" TEXT NOT NULL,
                                              "TransactionKind" TEXT NOT NULL DEFAULT '일반수금',
                                              "LinkedInvoiceId" TEXT NULL,
                                              "LinkedInvoiceNumber" TEXT NOT NULL DEFAULT '',
                                              "LinkedRentalBillingProfileId" TEXT NULL,
                                              "LinkedRentalBillingRunId" TEXT NULL,
                                              "SettlementAmount" REAL NOT NULL DEFAULT 0,
                                              "AdvanceDelta" REAL NOT NULL DEFAULT 0,
                                              "PrepaidDelta" REAL NOT NULL DEFAULT 0,
                                              "CashReceipt" REAL NOT NULL DEFAULT 0,
                                              "CardReceipt" REAL NOT NULL DEFAULT 0,
                                              "BankReceipt" REAL NOT NULL DEFAULT 0,
                                              "DiscountApplied" REAL NOT NULL DEFAULT 0,
                                              "ReceiptTotal" REAL NOT NULL DEFAULT 0,
                                              "CashPayment" REAL NOT NULL DEFAULT 0,
                                              "CardPayment" REAL NOT NULL DEFAULT 0,
                                              "BankPayment" REAL NOT NULL DEFAULT 0,
                                              "DiscountReceived" REAL NOT NULL DEFAULT 0,
                                              "PaymentTotal" REAL NOT NULL DEFAULT 0,
                                              "Note" TEXT NOT NULL DEFAULT '',
                                              "Memo" TEXT NOT NULL DEFAULT '',
                                              "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                              "CreatedAtUtc" TEXT NOT NULL,
                                              "UpdatedAtUtc" TEXT NOT NULL,
                                              "Revision" INTEGER NOT NULL DEFAULT 0,
                                              "IsDirty" INTEGER NOT NULL DEFAULT 1
                                          );
                                          """;
            await db.Database.ExecuteSqlRawAsync(createTableSql);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Transactions_CustomerId\" ON \"Transactions\" (\"CustomerId\");");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Transactions_TransactionDate\" ON \"Transactions\" (\"TransactionDate\");");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE INDEX IF NOT EXISTS \"IX_Transactions_ResponsibleOfficeCode\" ON \"Transactions\" (\"ResponsibleOfficeCode\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateTransactionAttachmentsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "TransactionAttachments" (
                                              "Id" TEXT NOT NULL CONSTRAINT "PK_TransactionAttachments" PRIMARY KEY,
                                              "TransactionId" TEXT NOT NULL,
                                              "AttachmentType" TEXT NOT NULL DEFAULT '기타',
                                              "FileName" TEXT NOT NULL DEFAULT '',
                                              "StoredFileName" TEXT NOT NULL DEFAULT '',
                                              "StoredPath" TEXT NOT NULL DEFAULT '',
                                              "MimeType" TEXT NOT NULL DEFAULT '',
                                              "FileSize" INTEGER NOT NULL DEFAULT 0,
                                              "FileHash" TEXT NOT NULL DEFAULT '',
                                              "Description" TEXT NOT NULL DEFAULT '',
                                              "UploadedByUsername" TEXT NOT NULL DEFAULT '',
                                              "UploadedAtUtc" TEXT NOT NULL,
                                              "VerificationStatus" TEXT NOT NULL DEFAULT '미확인',
                                              "VerifiedByUsername" TEXT NOT NULL DEFAULT '',
                                              "VerifiedAtUtc" TEXT NULL,
                                              "VerificationMemo" TEXT NOT NULL DEFAULT '',
                                              "SortOrder" INTEGER NOT NULL DEFAULT 0,
                                              "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                              "CreatedAtUtc" TEXT NOT NULL,
                                              "UpdatedAtUtc" TEXT NOT NULL,
                                              "Revision" INTEGER NOT NULL DEFAULT 0,
                                              "IsDirty" INTEGER NOT NULL DEFAULT 1
                                          );
                                          """;
            await db.Database.ExecuteSqlRawAsync(createTableSql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_TransactionAttachments_TransactionId\" ON \"TransactionAttachments\" (\"TransactionId\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_TransactionAttachments_TransactionStatus\" ON \"TransactionAttachments\" (\"TransactionId\", \"VerificationStatus\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateCustomerContractsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "CustomerContracts" (
                                              "Id" TEXT NOT NULL CONSTRAINT "PK_CustomerContracts" PRIMARY KEY,
                                              "CustomerId" TEXT NOT NULL,
                                              "ContractType" TEXT NOT NULL DEFAULT '거래계약서',
                                              "FileName" TEXT NOT NULL DEFAULT '',
                                              "MimeType" TEXT NOT NULL DEFAULT 'application/pdf',
                                              "FileSize" INTEGER NOT NULL DEFAULT 0,
                                              "FileHash" TEXT NOT NULL DEFAULT '',
                                              "Description" TEXT NOT NULL DEFAULT '',
                                              "SignedDate" TEXT NULL,
                                              "ExpireDate" TEXT NULL,
                                              "IsPrimary" INTEGER NOT NULL DEFAULT 0,
                                              "UploadedByUsername" TEXT NOT NULL DEFAULT '',
                                              "UploadedAtUtc" TEXT NOT NULL,
                                              "FileContent" BLOB NOT NULL,
                                              "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                              "CreatedAtUtc" TEXT NOT NULL,
                                              "UpdatedAtUtc" TEXT NOT NULL,
                                              "Revision" INTEGER NOT NULL DEFAULT 0,
                                              "IsDirty" INTEGER NOT NULL DEFAULT 1
                                          );
                                          """;
            await db.Database.ExecuteSqlRawAsync(createTableSql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId\" ON \"CustomerContracts\" (\"CustomerId\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CustomerContracts_CustomerId_IsPrimary\" ON \"CustomerContracts\" (\"CustomerId\", \"IsPrimary\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateOfficeTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "Offices" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_Offices" PRIMARY KEY,
                                   "Code" TEXT NOT NULL,
                                   "Name" TEXT NOT NULL,
                                   "IsHeadOffice" INTEGER NOT NULL DEFAULT 0,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 0
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Offices_Code\" ON \"Offices\" (\"Code\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateOfficeTableAsync), ex);
        }
    }

    private static async Task TryCreateWarehouseTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "Warehouses" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_Warehouses" PRIMARY KEY,
                                   "OfficeId" TEXT NOT NULL,
                                   "OfficeCode" TEXT NOT NULL,
                                   "Code" TEXT NOT NULL,
                                   "Name" TEXT NOT NULL,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 0
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Warehouses_Code\" ON \"Warehouses\" (\"Code\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Warehouses_OfficeCode\" ON \"Warehouses\" (\"OfficeCode\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateWarehouseTableAsync), ex);
        }
    }

    private static async Task TryCreateInvoiceLineSerialsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "InvoiceLineSerials" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_InvoiceLineSerials" PRIMARY KEY,
                                   "InvoiceId" TEXT NOT NULL,
                                   "InvoiceLineId" TEXT NOT NULL,
                                   "ItemId" TEXT NULL,
                                   "SerialNumber" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceLineSerials_InvoiceLine\" ON \"InvoiceLineSerials\" (\"InvoiceId\", \"InvoiceLineId\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InvoiceLineSerials_Serial\" ON \"InvoiceLineSerials\" (\"SerialNumber\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateInvoiceLineSerialsTableAsync), ex);
        }
    }

    private static async Task TryCreateInventoryMovementsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "InventoryMovements" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryMovements" PRIMARY KEY,
                                   "InvoiceId" TEXT NULL,
                                   "InvoiceLineId" TEXT NULL,
                                   "ItemId" TEXT NULL,
                                   "WarehouseCode" TEXT NOT NULL,
                                   "MovementType" TEXT NOT NULL,
                                   "QuantityDelta" REAL NOT NULL,
                                   "UnitCost" REAL NOT NULL,
                                   "Amount" REAL NOT NULL,
                                   "OccurredDate" TEXT NOT NULL,
                                   "IsSettledCost" INTEGER NOT NULL DEFAULT 1,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "Note" TEXT NOT NULL,
                                   "CreatedByUsername" TEXT NOT NULL,
                                   "CreatedAtUtc" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryMovements_ItemWhDate\" ON \"InventoryMovements\" (\"ItemId\", \"WarehouseCode\", \"OccurredDate\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryMovements_Invoice\" ON \"InventoryMovements\" (\"InvoiceId\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateInventoryMovementsTableAsync), ex);
        }
    }

    private static async Task TryCreateStockLayersTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "StockLayers" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_StockLayers" PRIMARY KEY,
                                   "ItemId" TEXT NULL,
                                   "WarehouseCode" TEXT NOT NULL,
                                   "SourceInvoiceId" TEXT NULL,
                                   "SourceInvoiceLineId" TEXT NULL,
                                   "ReceiptDate" TEXT NOT NULL,
                                   "UnitCost" REAL NOT NULL,
                                   "OriginalQuantity" REAL NOT NULL,
                                   "RemainingQuantity" REAL NOT NULL,
                                   "IsNegativePlaceholder" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_StockLayers_ItemWhDate\" ON \"StockLayers\" (\"ItemId\", \"WarehouseCode\", \"ReceiptDate\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateStockLayersTableAsync), ex);
        }
    }

    private static async Task TryCreateCostAllocationsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "CostAllocations" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_CostAllocations" PRIMARY KEY,
                                   "SalesInvoiceId" TEXT NOT NULL,
                                   "SalesInvoiceLineId" TEXT NOT NULL,
                                   "PurchaseInvoiceId" TEXT NULL,
                                   "PurchaseInvoiceLineId" TEXT NULL,
                                   "WarehouseCode" TEXT NOT NULL,
                                   "Quantity" REAL NOT NULL,
                                   "UnitCost" REAL NOT NULL,
                                   "CostAmount" REAL NOT NULL,
                                   "IsUnsettled" INTEGER NOT NULL DEFAULT 0,
                                   "Note" TEXT NOT NULL,
                                   "CreatedAtUtc" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_CostAllocations_SalesLine\" ON \"CostAllocations\" (\"SalesInvoiceId\", \"SalesInvoiceLineId\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateCostAllocationsTableAsync), ex);
        }
    }

    private static async Task TryCreateItemWarehouseStocksTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "ItemWarehouseStocks" (
                                   "ItemId" TEXT NOT NULL,
                                   "WarehouseCode" TEXT NOT NULL,
                                   "Quantity" REAL NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   CONSTRAINT "PK_ItemWarehouseStocks" PRIMARY KEY ("ItemId", "WarehouseCode")
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateItemWarehouseStocksTableAsync), ex);
        }
    }

    private static async Task TryCreateSerialLedgersTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "SerialLedgers" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_SerialLedgers" PRIMARY KEY,
                                   "SerialNumber" TEXT NOT NULL,
                                   "ItemId" TEXT NULL,
                                   "WarehouseCode" TEXT NOT NULL,
                                   "Status" TEXT NOT NULL,
                                   "SourcePurchaseInvoiceId" TEXT NULL,
                                   "SourceSalesInvoiceId" TEXT NULL,
                                   "LastInvoiceId" TEXT NULL,
                                   "LastMovementType" TEXT NOT NULL,
                                   "Memo" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_SerialLedgers_SerialNumber\" ON \"SerialLedgers\" (\"SerialNumber\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateSerialLedgersTableAsync), ex);
        }
    }

    private static async Task TryCreatePriceGradeOptionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "PriceGradeOptions" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_PriceGradeOptions" PRIMARY KEY,
                                   "Name" TEXT NOT NULL,
                                   "PriceSource" TEXT NOT NULL DEFAULT 'Sales',
                                   "SortOrder" INTEGER NOT NULL DEFAULT 0,
                                   "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_PriceGradeOptions_Name\" ON \"PriceGradeOptions\" (\"Name\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreatePriceGradeOptionsTableAsync), ex);
        }
    }

    private static async Task TryCreateTradeTypeOptionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "TradeTypeOptions" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_TradeTypeOptions" PRIMARY KEY,
                                   "Name" TEXT NOT NULL,
                                   "AllowsSales" INTEGER NOT NULL DEFAULT 1,
                                   "AllowsPurchase" INTEGER NOT NULL DEFAULT 0,
                                   "SortOrder" INTEGER NOT NULL DEFAULT 0,
                                   "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_TradeTypeOptions_Name\" ON \"TradeTypeOptions\" (\"Name\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateTradeTypeOptionsTableAsync), ex);
        }
    }

    private static async Task TryCreateItemCategoryOptionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "ItemCategoryOptions" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_ItemCategoryOptions" PRIMARY KEY,
                                   "Name" TEXT NOT NULL,
                                   "SortOrder" INTEGER NOT NULL DEFAULT 0,
                                   "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_ItemCategoryOptions_Name\" ON \"ItemCategoryOptions\" (\"Name\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateItemCategoryOptionsTableAsync), ex);
        }
    }

    private static async Task TryCreateAuditLogsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "AuditLogs" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_AuditLogs" PRIMARY KEY,
                                   "EntityName" TEXT NOT NULL,
                                   "EntityId" TEXT NOT NULL,
                                   "Action" TEXT NOT NULL,
                                   "Username" TEXT NOT NULL,
                                   "Role" TEXT NOT NULL,
                                   "OfficeCode" TEXT NOT NULL,
                                   "BeforeJson" TEXT NOT NULL,
                                   "AfterJson" TEXT NOT NULL,
                                   "CreatedAtUtc" TEXT NOT NULL
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_AuditLogs_EntityAt\" ON \"AuditLogs\" (\"EntityName\", \"EntityId\", \"CreatedAtUtc\");");
        }
        catch (Exception ex)
        {
            LogSchemaStepFailure(nameof(TryCreateAuditLogsTableAsync), ex);
        }
    }

    private static async Task TryCreateInventoryTransfersTableAsync(LocalDbContext db)
    {
        try
        {
            const string transferSql = """
                               CREATE TABLE IF NOT EXISTS "InventoryTransfers" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryTransfers" PRIMARY KEY,
                                   "TransferNumber" TEXT NOT NULL DEFAULT '',
                                   "TransferDate" TEXT NOT NULL,
                                   "FromWarehouseCode" TEXT NOT NULL,
                                   "ToWarehouseCode" TEXT NOT NULL,
                                    "Memo" TEXT NOT NULL DEFAULT '',
                                    "CreatedByUsername" TEXT NOT NULL DEFAULT '',
                                    "LastSavedByUsername" TEXT NOT NULL DEFAULT '',
                                    "LastSavedAtUtc" TEXT NOT NULL,
                                    "TransferStatus" TEXT NOT NULL DEFAULT '수령대기',
                                    "RequestedByUsername" TEXT NOT NULL DEFAULT '',
                                    "RequestedAtUtc" TEXT NULL,
                                    "ReceivedByUsername" TEXT NOT NULL DEFAULT '',
                                    "ReceivedAtUtc" TEXT NULL,
                                    "ReceiveMemo" TEXT NOT NULL DEFAULT '',
                                    "ReceiveEvidencePath" TEXT NOT NULL DEFAULT '',
                                    "RejectedByUsername" TEXT NOT NULL DEFAULT '',
                                    "RejectedAtUtc" TEXT NULL,
                                    "RejectReason" TEXT NOT NULL DEFAULT '',
                                    "LastStatusChangedByUsername" TEXT NOT NULL DEFAULT '',
                                    "LastStatusChangedAtUtc" TEXT NULL,
                                    "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                    "CreatedAtUtc" TEXT NOT NULL,
                                    "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(transferSql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TransferDate\" ON \"InventoryTransfers\" (\"TransferDate\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TransferNumber\" ON \"InventoryTransfers\" (\"TransferNumber\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_Warehouses\" ON \"InventoryTransfers\" (\"FromWarehouseCode\", \"ToWarehouseCode\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransfers_TransferStatus\" ON \"InventoryTransfers\" (\"TransferStatus\");");

            const string lineSql = """
                               CREATE TABLE IF NOT EXISTS "InventoryTransferLines" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_InventoryTransferLines" PRIMARY KEY,
                                   "TransferId" TEXT NOT NULL,
                                   "ItemId" TEXT NULL,
                                   "ItemNameOriginal" TEXT NOT NULL DEFAULT '',
                                    "SpecificationOriginal" TEXT NOT NULL DEFAULT '',
                                    "Unit" TEXT NOT NULL DEFAULT '',
                                    "Quantity" REAL NOT NULL DEFAULT 1,
                                    "Remark" TEXT NOT NULL DEFAULT '',
                                    "ReceivedQuantity" REAL NULL,
                                    "QuantityDifference" REAL NULL,
                                    "ReceiptRemark" TEXT NOT NULL DEFAULT '',
                                    "IsDeleted" INTEGER NOT NULL DEFAULT 0
                                );
                               """;
            await db.Database.ExecuteSqlRawAsync(lineSql);
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_InventoryTransferLines_TransferItem\" ON \"InventoryTransferLines\" (\"TransferId\", \"ItemId\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateRentalManagementCompaniesTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "RentalManagementCompanies" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_RentalManagementCompanies" PRIMARY KEY,
                                   "Code" TEXT NOT NULL,
                                   "Name" TEXT NOT NULL,
                                   "IsSystemDefault" INTEGER NOT NULL DEFAULT 0,
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalManagementCompanies_Code\" ON \"RentalManagementCompanies\" (\"Code\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateRentalBillingProfilesTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "RentalBillingProfiles" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_RentalBillingProfiles" PRIMARY KEY,
                                   "ProfileKey" TEXT NOT NULL DEFAULT '',
                                   "CustomerId" TEXT NULL,
                                   "CustomerName" TEXT NOT NULL DEFAULT '',
                                   "BusinessNumber" TEXT NOT NULL DEFAULT '',
                                   "RealCustomerName" TEXT NOT NULL DEFAULT '',
                                   "ItemName" TEXT NOT NULL DEFAULT '',
                                   "BillingType" TEXT NOT NULL DEFAULT '묶음',
                                   "BillToCustomerName" TEXT NOT NULL DEFAULT '',
                                   "InstallSiteName" TEXT NOT NULL DEFAULT '',
                                   "BillingAdvanceMode" TEXT NOT NULL DEFAULT '후불',
                                   "ManagementCompanyCode" TEXT NOT NULL DEFAULT '',
                                   "BillingMethod" TEXT NOT NULL DEFAULT '',
                                   "PaymentMethod" TEXT NOT NULL DEFAULT '',
                                   "BillingStatus" TEXT NOT NULL DEFAULT '',
                                   "Email" TEXT NOT NULL DEFAULT '',
                                   "BillingDay" INTEGER NOT NULL DEFAULT 25,
                                   "BillingCycleMonths" INTEGER NOT NULL DEFAULT 1,
                                   "MonthlyAmount" REAL NOT NULL DEFAULT 0,
                                   "DepositAmount" REAL NOT NULL DEFAULT 0,
                                   "SubmissionDocuments" TEXT NOT NULL DEFAULT '',
                                   "Notes" TEXT NOT NULL DEFAULT '',
                                   "BillingAnchorDate" TEXT NULL,
                                   "BillingStartDate" TEXT NULL,
                                   "ContractDate" TEXT NULL,
                                   "ContractStartDate" TEXT NULL,
                                   "ContractEndDate" TEXT NULL,
                                   "LastBilledDate" TEXT NULL,
                                   "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                                   "AssignedUsername" TEXT NOT NULL DEFAULT '',
                                   "BillingTemplateJson" TEXT NOT NULL DEFAULT '[]',
                                   "BillingRunsJson" TEXT NOT NULL DEFAULT '[]',
                                   "IsActive" INTEGER NOT NULL DEFAULT 1,
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalBillingProfiles_ProfileKey\" ON \"RentalBillingProfiles\" (\"ProfileKey\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingProfiles_Assignee\" ON \"RentalBillingProfiles\" (\"AssignedUsername\", \"ResponsibleOfficeCode\", \"ManagementCompanyCode\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateRentalAssetsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "RentalAssets" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_RentalAssets" PRIMARY KEY,
                                   "AssetKey" TEXT NOT NULL DEFAULT '',
                                   "CustomerId" TEXT NULL,
                                   "BillingProfileId" TEXT NULL,
                                   "ManagementId" TEXT NOT NULL DEFAULT '',
                                   "ManagementNumber" TEXT NOT NULL DEFAULT '',
                                   "ManagementCompanyCode" TEXT NOT NULL DEFAULT '',
                                   "CurrentLocation" TEXT NOT NULL DEFAULT '',
                                   "CurrentCustomerName" TEXT NOT NULL DEFAULT '',
                                   "BillToCustomerName" TEXT NOT NULL DEFAULT '',
                                   "InstallSiteName" TEXT NOT NULL DEFAULT '',
                                   "BillingEligibilityStatus" TEXT NOT NULL DEFAULT '',
                                   "BillingExclusionReason" TEXT NOT NULL DEFAULT '',
                                   "ItemCategoryName" TEXT NOT NULL DEFAULT '',
                                   "Manufacturer" TEXT NOT NULL DEFAULT '',
                                   "ItemName" TEXT NOT NULL DEFAULT '',
                                   "MachineNumber" TEXT NOT NULL DEFAULT '',
                                   "PurchaseVendor" TEXT NOT NULL DEFAULT '',
                                   "PurchaseDate" TEXT NULL,
                                   "DisposalDate" TEXT NULL,
                                   "PurchasePrice" REAL NOT NULL DEFAULT 0,
                                   "SalePrice" REAL NOT NULL DEFAULT 0,
                                   "CustomerName" TEXT NOT NULL DEFAULT '',
                                   "InstallLocation" TEXT NOT NULL DEFAULT '',
                                   "DepositText" TEXT NOT NULL DEFAULT '',
                                   "MonthlyFee" REAL NOT NULL DEFAULT 0,
                                   "ContractMonths" INTEGER NOT NULL DEFAULT 0,
                                   "ContractDate" TEXT NULL,
                                   "InstallDate" TEXT NULL,
                                   "ContractStartDate" TEXT NULL,
                                   "RentalEndDate" TEXT NULL,
                                   "FreeSupplyItems" TEXT NOT NULL DEFAULT '',
                                   "PaidSupplyItems" TEXT NOT NULL DEFAULT '',
                                   "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                                   "AssignedUsername" TEXT NOT NULL DEFAULT '',
                                   "AssetStatus" TEXT NOT NULL DEFAULT '',
                                   "Notes" TEXT NOT NULL DEFAULT '',
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_AssetKey\" ON \"RentalAssets\" (\"AssetKey\");");
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_ManagementId\" ON \"RentalAssets\" (\"ManagementId\") WHERE TRIM(\"ManagementId\") <> '';");
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalAssets_ManagementNumber\" ON \"RentalAssets\" (\"ManagementNumber\") WHERE TRIM(\"ManagementNumber\") <> '';");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_RentalAssets_Assignee\" ON \"RentalAssets\" (\"AssignedUsername\", \"ResponsibleOfficeCode\", \"ManagementCompanyCode\");");
        }
        catch
        {
        }
    }

    private static async Task TryCreateRentalBillingLogsTableAsync(LocalDbContext db)
    {
        try
        {
            const string sql = """
                               CREATE TABLE IF NOT EXISTS "RentalBillingLogs" (
                                   "Id" TEXT NOT NULL CONSTRAINT "PK_RentalBillingLogs" PRIMARY KEY,
                                   "BillingProfileId" TEXT NOT NULL,
                                   "BillingYearMonth" TEXT NOT NULL DEFAULT '',
                                   "ScheduledDate" TEXT NOT NULL,
                                   "ProcessedDate" TEXT NULL,
                                   "ProcessedByUsername" TEXT NOT NULL DEFAULT '',
                                   "Status" TEXT NOT NULL DEFAULT '예정',
                                   "BilledAmount" REAL NOT NULL DEFAULT 0,
                                   "Note" TEXT NOT NULL DEFAULT '',
                                   "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'USENET',
                                   "AssignedUsername" TEXT NOT NULL DEFAULT '',
                                   "IsDeleted" INTEGER NOT NULL DEFAULT 0,
                                   "CreatedAtUtc" TEXT NOT NULL,
                                   "UpdatedAtUtc" TEXT NOT NULL,
                                   "Revision" INTEGER NOT NULL DEFAULT 0,
                                   "IsDirty" INTEGER NOT NULL DEFAULT 1
                               );
                               """;
            await db.Database.ExecuteSqlRawAsync(sql);
            await TryCreateIndexAsync(db, "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_RentalBillingLogs_ProfileMonth\" ON \"RentalBillingLogs\" (\"BillingProfileId\", \"BillingYearMonth\");");
            await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingLogs_AssigneeDate\" ON \"RentalBillingLogs\" (\"AssignedUsername\", \"ResponsibleOfficeCode\", \"ScheduledDate\");");
        }
        catch
        {
        }
    }

    private sealed record CanonicalOfficeDefinition(
        string Code,
        string Name,
        bool IsHeadOffice,
        string WarehouseCode,
        string WarehouseName);
}
