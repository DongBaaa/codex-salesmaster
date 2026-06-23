using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceSaveScopeTests
{
    [Fact]
    public async Task SaveInvoiceAsync_DerivesTenantAndOwnerOfficeFromLinkedCustomerScope()
    {
        PrepareAppRoot("georaeplan-invoice-save-derived-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateAdminSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.SaveInvoiceAsync(
                CreateInvoice(customerId, OfficeCodeCatalog.Itworld),
                CreateSaveContext("itworld-admin", OfficeCodeCatalog.Itworld),
                session);

            Assert.True(result.Success, result.Message);
            var stored = await db.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == result.SavedInvoiceId);
            Assert.Equal(TenantScopeCatalog.Itworld, stored.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, stored.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, stored.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_RejectsLineItemOutsideReadableTenantScope()
    {
        PrepareAppRoot("georaeplan-invoice-save-line-item-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var hiddenItemId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            db.Items.Add(CreateItem(hiddenItemId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var invoice = CreateInvoice(customerId, OfficeCodeCatalog.Usenet);
            invoice.Lines.Clear();
            invoice.Lines.Add(new LocalInvoiceLine
            {
                ItemId = hiddenItemId,
                ItemNameOriginal = "Hidden tenant item",
                ItemTrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                Quantity = 1m,
                UnitPrice = 1000m,
                LineAmount = 1000m
            });

            var result = await service.SaveInvoiceAsync(
                invoice,
                CreateSaveContext("usenet-admin", OfficeCodeCatalog.Usenet),
                session);

            Assert.False(result.Success);
            Assert.Contains("품목", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.Invoices.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.InvoiceLines.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalInvoice CreateInvoice(Guid customerId, string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = string.Empty,
            ResponsibleOfficeCode = officeCode,
            SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(officeCode),
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23),
            Memo = "invoice save scope regression",
            Lines =
            {
                new LocalInvoiceLine
                {
                    ItemNameOriginal = "Scope non-stock item",
                    ItemTrackingType = ItemTrackingTypes.NonStock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m
                }
            }
        };

    private static LocalCustomer CreateCustomer(Guid id, string officeCode, string tenantCode)
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = $"{officeCode} invoice customer",
            NameMatchKey = $"{officeCode}INVOICECUSTOMER",
            IsDeleted = false
        };

    private static LocalItem CreateItem(Guid id, string officeCode, string tenantCode)
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            NameOriginal = $"{officeCode} hidden item",
            NameMatchKey = $"{officeCode}HIDDENITEM",
            SpecificationOriginal = "scope",
            SpecificationMatchKey = "SCOPE",
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            IsDeleted = false
        };

    private static InvoiceSaveContext CreateSaveContext(string username, string officeCode)
        => new()
        {
            Username = username,
            Role = DomainConstants.RoleAdmin,
            OfficeCode = officeCode
        };

    private static SessionState CreateAdminSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode}-invoice-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = [AppPermissionNames.InvoiceEdit]
        });
        return session;
    }

    private static SessionState CreateInvoiceEditorSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode}-invoice-editor",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.InvoiceEdit]
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
