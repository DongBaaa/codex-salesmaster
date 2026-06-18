using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceSelfConflictAutoRebaseTests
{
    [Fact]
    public async Task SaveInvoiceAsync_AutoRebasesStaleStamp_WhenLatestWasSavedBySameUser()
    {
        PrepareAppRoot("georaeplan-invoice-self-conflict-auto-rebase");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession("admin");
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = await SeedCustomerAsync(db);

            var first = await service.SaveInvoiceAsync(
                BuildInvoice(Guid.NewGuid(), customerId, new DateOnly(2026, 6, 15), "처음 저장"),
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet
                },
                session);
            Assert.True(first.Success, first.Message);

            var latestBeforeRetry = await db.Invoices.AsNoTracking().SingleAsync(invoice => invoice.Id == first.SavedInvoiceId);

            var retry = await service.SaveInvoiceAsync(
                BuildInvoice(first.SavedInvoiceId, customerId, new DateOnly(2026, 6, 12), "현재 창 수정"),
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ExpectedConcurrencyStamp = "stale-stamp-from-open-window",
                    AutoRebaseWhenLatestSavedBySameUser = true
                },
                session);

            Assert.True(retry.Success, retry.Message);
            Assert.NotEqual(first.SavedInvoiceId, retry.SavedInvoiceId);

            var latestAfterRetry = await db.Invoices
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == retry.SavedInvoiceId);
            Assert.True(latestAfterRetry.IsLatestVersion);
            Assert.Equal(new DateOnly(2026, 6, 12), latestAfterRetry.InvoiceDate);
            Assert.Equal("현재 창 수정", latestAfterRetry.Memo);
            Assert.Equal("admin", latestAfterRetry.LastSavedByUsername);

            var previous = await db.Invoices
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == latestBeforeRetry.Id);
            Assert.False(previous.IsLatestVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_DoesNotAutoRebaseStaleStamp_WhenLatestWasSavedByDifferentUser()
    {
        PrepareAppRoot("georaeplan-invoice-other-user-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession("admin");
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = await SeedCustomerAsync(db);

            var first = await service.SaveInvoiceAsync(
                BuildInvoice(Guid.NewGuid(), customerId, new DateOnly(2026, 6, 15), "다른 사용자 저장"),
                new InvoiceSaveContext
                {
                    Username = "other-user",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet
                },
                session);
            Assert.True(first.Success, first.Message);

            var retry = await service.SaveInvoiceAsync(
                BuildInvoice(first.SavedInvoiceId, customerId, new DateOnly(2026, 6, 12), "현재 창 수정"),
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ExpectedConcurrencyStamp = "stale-stamp-from-open-window",
                    AutoRebaseWhenLatestSavedBySameUser = true
                },
                session);

            Assert.False(retry.Success);
            Assert.True(retry.ConcurrencyConflict, retry.Message);
            Assert.Equal(1, await db.Invoices.CountAsync());
            Assert.Equal(new DateOnly(2026, 6, 15), await db.Invoices.Select(invoice => invoice.InvoiceDate).SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_RenumbersActiveLinesByCurrentListOrder()
    {
        PrepareAppRoot("georaeplan-invoice-line-order");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession("admin");
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var customerId = await SeedCustomerAsync(db);
            var invoiceId = Guid.NewGuid();

            var result = await service.SaveInvoiceAsync(
                new LocalInvoice
                {
                    Id = invoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 6, 20),
                    Lines =
                    {
                        new LocalInvoiceLine
                        {
                            ItemNameOriginal = "local payload first",
                            ItemTrackingType = ItemTrackingTypes.NonStock,
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 1000m,
                            LineAmount = 1000m,
                            OrderIndex = 50
                        },
                        new LocalInvoiceLine
                        {
                            ItemNameOriginal = "local payload second",
                            ItemTrackingType = ItemTrackingTypes.NonStock,
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 2000m,
                            LineAmount = 2000m,
                            OrderIndex = 50
                        }
                    }
                },
                new InvoiceSaveContext
                {
                    Username = "admin",
                    Role = DomainConstants.RoleAdmin,
                    OfficeCode = OfficeCodeCatalog.Usenet
                },
                session);

            Assert.True(result.Success, result.Message);

            var storedLines = await db.InvoiceLines
                .AsNoTracking()
                .Where(line => line.InvoiceId == result.SavedInvoiceId)
                .OrderBy(line => line.OrderIndex)
                .ToListAsync();
            Assert.Equal(new[] { "local payload first", "local payload second" }, storedLines.Select(line => line.ItemNameOriginal).ToArray());
            Assert.Equal(new[] { 1, 2 }, storedLines.Select(line => line.OrderIndex).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalInvoice BuildInvoice(Guid invoiceId, Guid customerId, DateOnly invoiceDate, string memo)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = invoiceDate,
            Memo = memo,
            Lines =
            {
                new LocalInvoiceLine
                {
                    ItemNameOriginal = "테스트 비재고 품목",
                    ItemTrackingType = ItemTrackingTypes.NonStock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m
                }
            }
        };

    private static async Task<Guid> SeedCustomerAsync(LocalDbContext db)
    {
        var customerId = Guid.NewGuid();
        db.Customers.Add(new LocalCustomer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "전표 충돌 거래처",
            NameMatchKey = "전표 충돌 거래처"
        });
        await db.SaveChangesAsync();
        return customerId;
    }

    private static SessionState CreateAdminSession(string username)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = username,
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
