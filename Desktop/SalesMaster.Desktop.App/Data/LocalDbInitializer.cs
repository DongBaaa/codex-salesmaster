using Microsoft.EntityFrameworkCore;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;
using System.Text.RegularExpressions;

namespace SalesMaster.Desktop.App.Data;

public static class LocalDbInitializer
{
    private const string FallbackUtcText = "1970-01-01T00:00:00Z";
    private const string YeonsuOfficeIdSettingKey = "SystemOffice.YeonsuOfficeId";
    private static readonly Regex SqlIdentifierPattern = new(
        "^[A-Za-z0-9_]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
                    IsSystemDefault = true
                }));
        }

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

        if (!db.Settings.Any())
        {
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "0" });
            db.Settings.Add(new LocalSetting { Key = "Theme", Value = "Dark" });
        }

        await CustomerCategoryMaintenance.NormalizeAsync(db);
        await SeedOfficeAndWarehouseAsync(db);
        await db.SaveChangesAsync();
    }

    private static async Task MigrateColumnsAsync(LocalDbContext db)
    {
        await TryCreateAttachmentSelectionsTableAsync(db);
        await TryCreateTransactionsTableAsync(db);
        await TryCreateOfficeTableAsync(db);
        await TryCreateWarehouseTableAsync(db);
        await TryCreateInvoiceLineSerialsTableAsync(db);
        await TryCreateInventoryMovementsTableAsync(db);
        await TryCreateStockLayersTableAsync(db);
        await TryCreateCostAllocationsTableAsync(db);
        await TryCreateItemWarehouseStocksTableAsync(db);
        await TryCreateSerialLedgersTableAsync(db);
        await TryCreateInventoryTransfersTableAsync(db);
        await TryCreateAuditLogsTableAsync(db);

        var customerCols = new (string col, string def)[]
        {
            ("DetailAddress", "TEXT NOT NULL DEFAULT ''"),
            ("MobilePhone", "TEXT NOT NULL DEFAULT ''"),
            ("FaxNumber", "TEXT NOT NULL DEFAULT ''"),
            ("Representative", "TEXT NOT NULL DEFAULT ''"),
            ("BusinessType", "TEXT NOT NULL DEFAULT ''"),
            ("BusinessItem", "TEXT NOT NULL DEFAULT ''"),
            ("Recipient", "TEXT NOT NULL DEFAULT ''"),
            ("HomePage", "TEXT NOT NULL DEFAULT ''"),
            ("PriceGrade", "TEXT NOT NULL DEFAULT ''"),
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'UZNET'"),
            ("TradeType", "TEXT NOT NULL DEFAULT '매출'"),
        };
        foreach (var (col, def) in customerCols)
            await TryAddColumnAsync(db, "Customers", col, def);

        var itemCols = new (string col, string def)[]
        {
            ("CategoryName", "TEXT NOT NULL DEFAULT ''"),
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
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'UZNET'"),
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

        var invoiceCols = new (string col, string def)[]
        {
            ("ResponsibleOfficeCode", "TEXT NOT NULL DEFAULT 'UZNET'"),
            ("SourceWarehouseCode", "TEXT NOT NULL DEFAULT 'UZNET_MAIN'"),
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
            ("CostStatus", "TEXT NOT NULL DEFAULT 'Pending'"),
        };
        foreach (var (col, def) in invoiceCols)
            await TryAddColumnAsync(db, "Invoices", col, def);

        await NormalizeLegacyDateTimeColumnsAsync(db);

        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_VersionGroupId\" ON \"Invoices\" (\"VersionGroupId\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_IsLatestVersion\" ON \"Invoices\" (\"IsLatestVersion\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_SourceWarehouseCode\" ON \"Invoices\" (\"SourceWarehouseCode\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Invoices_ResponsibleOfficeCode\" ON \"Invoices\" (\"ResponsibleOfficeCode\");");
        await TryCreateIndexAsync(db, "CREATE INDEX IF NOT EXISTS \"IX_Transactions_ResponsibleOfficeCode\" ON \"Transactions\" (\"ResponsibleOfficeCode\");");
        await BackfillTransactionResponsibleOfficeCodeAsync(db);
        await NormalizeCustomerTradeTypeAsync(db);
    }

    private static async Task SeedOfficeAndWarehouseAsync(LocalDbContext db)
    {
        if (!db.Offices.Any())
        {
            db.Offices.AddRange(
                new LocalOffice
                {
                    Code = DomainConstants.DefaultOfficeUznet,
                    Name = "유즈넷",
                    IsHeadOffice = true,
                    IsDirty = false
                },
                new LocalOffice
                {
                    Code = DomainConstants.DefaultOfficeYeonsu,
                    Name = "연수구 사무실",
                    IsHeadOffice = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();
        }

        var offices = await db.Offices.AsNoTracking().ToListAsync();
        var uznetOffice = ResolveHeadOffice(offices);
        var yeonsuOffice = await ResolveYeonsuOfficeAsync(db, offices);

        DomainConstants.ConfigureSystemOffices(
            uznetOffice?.Code,
            yeonsuOffice?.Code,
            DomainConstants.DefaultWarehouseUznetMain,
            DomainConstants.DefaultWarehouseYeonsuMain);

        if (uznetOffice is not null && !db.Warehouses.Any(w => w.Code == DomainConstants.DefaultWarehouseUznetMain))
        {
            db.Warehouses.Add(new LocalWarehouse
            {
                OfficeId = uznetOffice.Id,
                OfficeCode = uznetOffice.Code,
                Code = DomainConstants.DefaultWarehouseUznetMain,
                Name = "유즈넷 창고",
                IsActive = true,
                IsDirty = false
            });
        }

        if (yeonsuOffice is not null && !db.Warehouses.Any(w => w.Code == DomainConstants.DefaultWarehouseYeonsuMain))
        {
            db.Warehouses.Add(new LocalWarehouse
            {
                OfficeId = yeonsuOffice.Id,
                OfficeCode = yeonsuOffice.Code,
                Code = DomainConstants.DefaultWarehouseYeonsuMain,
                Name = "연수구 창고",
                IsActive = true,
                IsDirty = false
            });
        }
    }

    private static LocalOffice? ResolveHeadOffice(IReadOnlyCollection<LocalOffice> offices)
        => offices.FirstOrDefault(o => o.IsHeadOffice)
           ?? offices.FirstOrDefault(o => string.Equals(o.Code, DomainConstants.DefaultOfficeUznet, StringComparison.OrdinalIgnoreCase))
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
        var setting = await db.Settings.FirstOrDefaultAsync(current => current.Key == key);
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
                ELSE '매출'
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
                'UZNET')
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

    private static async Task TryCreateTransactionsTableAsync(LocalDbContext db)
    {
        try
        {
            const string createTableSql = """
                                          CREATE TABLE IF NOT EXISTS "Transactions" (
                                              "Id" TEXT NOT NULL CONSTRAINT "PK_Transactions" PRIMARY KEY,
                                              "CustomerId" TEXT NOT NULL,
                                              "ResponsibleOfficeCode" TEXT NOT NULL DEFAULT 'UZNET',
                                              "TransactionDate" TEXT NOT NULL,
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
        catch { }
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
}
