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

    [Fact]
    public async Task SaveInvoiceAsync_NormalizesDuplicateLatestVersionsInSameVersionGroup()
    {
        PrepareAppRoot("georaeplan-invoice-save-latest-version-normalize");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var firstInvoiceId = Guid.NewGuid();
            var secondInvoiceId = Guid.NewGuid();
            var versionGroupId = firstInvoiceId;
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            db.Invoices.AddRange(
                CreateStoredInvoice(firstInvoiceId, customerId, versionGroupId, 1, isLatest: true, taxIssued: false),
                CreateStoredInvoice(secondInvoiceId, customerId, versionGroupId, 2, isLatest: true, taxIssued: true, previousVersionId: firstInvoiceId));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var edit = CreateInvoice(customerId, OfficeCodeCatalog.Usenet);
            edit.Id = secondInvoiceId;
            edit.VersionGroupId = versionGroupId;
            edit.TaxInvoiceIssued = true;
            edit.TaxInvoiceNumber = "TAX-LOCAL-LATEST-ONLY";

            var result = await service.SaveInvoiceAsync(
                edit,
                new InvoiceSaveContext
                {
                    Username = "usenet-invoice-editor",
                    Role = DomainConstants.RoleUser,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ForceOverride = true
                },
                session);

            Assert.True(result.Success, result.Message);
            var versions = await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.VersionGroupId == versionGroupId || invoice.Id == versionGroupId)
                .ToListAsync();
            var latest = Assert.Single(versions, invoice => invoice.IsLatestVersion && !invoice.IsDeleted);
            Assert.Equal(result.SavedInvoiceId, latest.Id);
            Assert.True(latest.TaxInvoiceIssued);
            Assert.All(versions.Where(invoice => invoice.Id != latest.Id), invoice => Assert.False(invoice.IsLatestVersion));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairDuplicateLatestInvoiceVersionGroupsForSyncAsync_DemotesOlderLatestAndMarksDirty()
    {
        PrepareAppRoot("georaeplan-invoice-sync-latest-version-repair");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var firstInvoiceId = Guid.NewGuid();
            var secondInvoiceId = Guid.NewGuid();
            var versionGroupId = firstInvoiceId;
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            db.Invoices.AddRange(
                CreateStoredInvoice(firstInvoiceId, customerId, versionGroupId, 1, isLatest: true, taxIssued: false),
                CreateStoredInvoice(secondInvoiceId, customerId, versionGroupId, 2, isLatest: true, taxIssued: true, previousVersionId: firstInvoiceId));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var changed = await service.RepairDuplicateLatestInvoiceVersionGroupsForSyncAsync(session);

            Assert.True(changed > 0);
            var versions = await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.VersionGroupId == versionGroupId || invoice.Id == versionGroupId)
                .ToListAsync();
            var latest = Assert.Single(versions, invoice => invoice.IsLatestVersion && !invoice.IsDeleted);
            Assert.Equal(secondInvoiceId, latest.Id);
            Assert.False(latest.IsDirty);

            var demoted = Assert.Single(versions, invoice => invoice.Id == firstInvoiceId);
            Assert.False(demoted.IsLatestVersion);
            Assert.True(demoted.IsDirty);
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

    private static LocalInvoice CreateStoredInvoice(
        Guid id,
        Guid customerId,
        Guid versionGroupId,
        int versionNumber,
        bool isLatest,
        bool taxIssued,
        Guid? previousVersionId = null)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet),
            InvoiceNumber = $"LOCAL-LATEST-{versionNumber:00}",
            LocalTempNumber = $"L-LOCAL-LATEST-{versionNumber:00}",
            TaxInvoiceIssued = taxIssued,
            TaxInvoiceNumber = taxIssued ? $"TAX-LOCAL-{versionNumber:00}" : string.Empty,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 30),
            TotalAmount = 99_000m,
            SupplyAmount = 90_000m,
            VatAmount = 9_000m,
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            PreviousVersionId = previousVersionId,
            IsLatestVersion = isLatest,
            IsConfirmed = true,
            CreatedByUsername = "seed",
            LastSavedByUsername = "seed",
            LastSavedAtUtc = new DateTime(2026, 7, 6, 0, versionNumber, 0, DateTimeKind.Utc),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 6, 0, versionNumber, 0, DateTimeKind.Utc),
            IsDirty = false
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
