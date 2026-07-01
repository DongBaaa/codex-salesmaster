using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MainLoadingPerformanceTests
{
    [Fact]
    public async Task GetInvoiceDashboardMetricsAsync_MatchesMainDashboardTotalsWithoutLineSummaryLoad()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"georaeplan-dashboard-metrics-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDbContext(dbPath);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var currentDate = new DateOnly(2026, 6, 15);
            var currentSalesOpenId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            var currentSalesPaidId = Guid.Parse("10000000-0000-0000-0000-000000000002");
            var previousSalesId = Guid.Parse("10000000-0000-0000-0000-000000000003");
            var currentPurchaseId = Guid.Parse("10000000-0000-0000-0000-000000000004");
            var currentExpenseId = Guid.Parse("10000000-0000-0000-0000-000000000005");

            db.Invoices.AddRange(
                CreateInvoice(currentSalesOpenId, VoucherType.Sales, new DateOnly(2026, 6, 1), 100m),
                CreateInvoice(currentSalesPaidId, VoucherType.Sales, new DateOnly(2026, 6, 2), 300m),
                CreateInvoice(previousSalesId, VoucherType.Sales, new DateOnly(2026, 5, 31), 50m),
                CreateInvoice(currentPurchaseId, VoucherType.Purchase, new DateOnly(2026, 6, 3), 200m),
                CreateInvoice(currentExpenseId, VoucherType.Expense, new DateOnly(2026, 6, 4), 999m));
            db.Payments.AddRange(
                CreatePayment(currentSalesOpenId, 40m),
                CreatePayment(currentSalesPaidId, 300m),
                CreatePayment(currentPurchaseId, 50m));
            db.Items.AddRange(
                CreateItem(Guid.Parse("20000000-0000-0000-0000-000000000001"), 2m, 5m),
                CreateItem(Guid.Parse("20000000-0000-0000-0000-000000000002"), 10m, 5m));
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());

            var metrics = await service.GetInvoiceDashboardMetricsAsync(CreateAdminSession(), currentDate);
            var safetyStockAlerts = await service.CountSafetyStockAlertsAsync(CreateAdminSession());

            Assert.Equal(400m, metrics.MonthlySales);
            Assert.Equal(50m, metrics.PreviousMonthlySales);
            Assert.Equal(4, metrics.MonthlyInvoiceCount);
            Assert.Equal(110m, metrics.Receivable);
            Assert.Equal(150m, metrics.Payable);
            Assert.Equal(1, safetyStockAlerts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task GetInvoiceListSummariesByIdsAsync_LoadsOnlyRequestedFavoriteInvoices()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"georaeplan-favorite-summaries-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = CreateDbContext(dbPath);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var favoriteId = Guid.Parse("30000000-0000-0000-0000-000000000001");
            var hiddenId = Guid.Parse("30000000-0000-0000-0000-000000000002");
            var favorite = CreateInvoice(favoriteId, VoucherType.Sales, new DateOnly(2026, 6, 10), 100m);
            favorite.Lines.Add(new LocalInvoiceLine
            {
                InvoiceId = favoriteId,
                ItemNameOriginal = "Favorite item A",
                OrderIndex = 1
            });
            favorite.Lines.Add(new LocalInvoiceLine
            {
                InvoiceId = favoriteId,
                ItemNameOriginal = "Favorite item B",
                OrderIndex = 2
            });
            db.Invoices.AddRange(
                favorite,
                CreateInvoice(hiddenId, VoucherType.Sales, new DateOnly(2026, 6, 11), 200m));
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());

            var summaries = await service.GetInvoiceListSummariesByIdsAsync([favoriteId], CreateAdminSession());

            var summary = Assert.Single(summaries);
            Assert.Equal(favoriteId, summary.Id);
            Assert.Equal("Favorite item A 외 1건", summary.FirstItemSummary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dbPath);
        }
    }

    [Fact]
    public void MainViewModel_LoadInvoiceFavoritesAsync_ShortCircuitsWhenFavoriteListIsEmpty()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Desktop",
                "\uAC70\uB798\uD50C\uB79C.Desktop.App",
                "ViewModels",
                "MainViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var idsIndex = source.IndexOf("var ids = await GetFavoriteInvoiceIdsAsync();", StringComparison.Ordinal);
        var emptyGuardIndex = source.IndexOf("if (ids.Count == 0)", idsIndex, StringComparison.Ordinal);
        var favoriteQueryIndex = source.IndexOf("GetInvoiceListSummariesByIdsAsync(ids, _session, ct)", idsIndex, StringComparison.Ordinal);
        var legacyAllQueryIndex = source.IndexOf("GetInvoiceListSummariesAsync(from: null, to: null, customerId: null, session: _session, ct)", idsIndex, StringComparison.Ordinal);

        Assert.True(idsIndex > 0);
        Assert.True(emptyGuardIndex > idsIndex);
        Assert.True(favoriteQueryIndex > emptyGuardIndex);
        Assert.Equal(-1, legacyAllQueryIndex);
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private static LocalInvoice CreateInvoice(Guid id, VoucherType voucherType, DateOnly invoiceDate, decimal totalAmount)
        => new()
        {
            Id = id,
            VersionGroupId = id,
            CustomerId = Guid.Parse("40000000-0000-0000-0000-000000000001"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = $"LOAD-{id.ToString("N")[..6]}",
            VoucherType = voucherType,
            InvoiceDate = invoiceDate,
            TotalAmount = totalAmount,
            SupplyAmount = totalAmount,
            IsLatestVersion = true,
            VersionNumber = 1,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalPayment CreatePayment(Guid invoiceId, decimal amount)
        => new()
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 15),
            Amount = amount,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItem CreateItem(Guid id, decimal currentStock, decimal safetyStock)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = $"Safety stock item {id.ToString("N")[..6]}",
            CurrentStock = currentStock,
            SafetyStock = safetyStock,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Desktop", "\uAC70\uB798\uD50C\uB79C.Desktop.App", "ViewModels", "MainViewModel.cs");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Test cleanup best effort.
        }
    }
}
