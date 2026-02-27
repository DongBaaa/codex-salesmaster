using Microsoft.EntityFrameworkCore;

namespace SalesMaster.Desktop.App.Data;

public static class LocalDbInitializer
{
    public static async Task InitializeAsync(LocalDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // 기존 DB에 신규 컬럼을 안전하게 추가 (이미 있으면 무시)
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

    // ── SQLite ALTER TABLE 마이그레이션 ──────────────────────────────────────
    private static async Task MigrateColumnsAsync(LocalDbContext db)
    {
        // LocalCustomer 신규 컬럼 (EF Core 테이블명 = DbSet 속성명 "Customers")
        var customerCols = new (string col, string def)[]
        {
            ("DetailAddress",  "TEXT NOT NULL DEFAULT ''"),
            ("MobilePhone",    "TEXT NOT NULL DEFAULT ''"),
            ("FaxNumber",      "TEXT NOT NULL DEFAULT ''"),
            ("Representative", "TEXT NOT NULL DEFAULT ''"),
            ("BusinessType",   "TEXT NOT NULL DEFAULT ''"),
            ("BusinessItem",   "TEXT NOT NULL DEFAULT ''"),
            ("Recipient",      "TEXT NOT NULL DEFAULT ''"),
            ("HomePage",       "TEXT NOT NULL DEFAULT ''"),
            ("PriceGrade",     "TEXT NOT NULL DEFAULT '매출단가'"),
        };
        foreach (var (col, def) in customerCols)
            await TryAddColumnAsync(db, "Customers", col, def);

        // LocalItem 신규 컬럼 (EF Core 테이블명 = DbSet 속성명 "Items")
        var itemCols = new (string col, string def)[]
        {
            ("CategoryName",    "TEXT NOT NULL DEFAULT ''"),
            ("BoxQuantity",     "REAL NOT NULL DEFAULT 0"),
            ("StorageLocation", "TEXT NOT NULL DEFAULT ''"),
            ("CurrentStock",    "REAL NOT NULL DEFAULT 0"),
            ("SafetyStock",     "REAL NOT NULL DEFAULT 0"),
            ("PurchasePrice",   "REAL NOT NULL DEFAULT 0"),
            ("SalePrice",       "REAL NOT NULL DEFAULT 0"),
            ("RetailPrice",     "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeA",     "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeB",     "REAL NOT NULL DEFAULT 0"),
            ("PriceGradeC",     "REAL NOT NULL DEFAULT 0"),
            ("LastPurchaseDate","TEXT"),
            ("LastSaleDate",    "TEXT"),
            ("SimpleMemo",      "TEXT NOT NULL DEFAULT ''"),
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
            // 이미 존재하는 컬럼이면 무시
        }
    }
}
