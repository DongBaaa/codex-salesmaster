using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Desktop.App.Data;

public static class LocalDbInitializer
{
    public static async Task InitializeAsync(LocalDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await MigrateColumnsAsync(db);

        if (!db.CustomerCategories.Any())
        {
            db.CustomerCategories.AddRange(
                new LocalCustomerCategory { Name = "관공서", IsSystemDefault = true },
                new LocalCustomerCategory { Name = "학교", IsSystemDefault = true },
                new LocalCustomerCategory { Name = "기업", IsSystemDefault = true },
                new LocalCustomerCategory { Name = "개인", IsSystemDefault = true }
            );
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

        await db.SaveChangesAsync();
    }

    private static async Task MigrateColumnsAsync(LocalDbContext db)
    {
        await TryCreateAttachmentSelectionsTableAsync(db);

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
            ("PriceGrade", "TEXT NOT NULL DEFAULT '매출단가'"),
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
    }

    private static async Task TryAddColumnAsync(LocalDbContext db, string table, string column, string definition)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
        }
        catch
        {
            // Column may already exist on existing databases.
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
            // Table creation should not block app startup.
        }
    }
}
