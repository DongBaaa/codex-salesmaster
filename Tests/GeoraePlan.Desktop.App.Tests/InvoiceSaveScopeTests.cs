using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
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

    [Fact]
    public async Task GetLatestAndVersions_UseCustomerAwareCompositeScopeAndStableIdTieBreak()
    {
        PrepareAppRoot("georaeplan-invoice-version-composite-read");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itworldCustomerId = Guid.Parse("10000000-0000-0000-0000-000000000001");
            var foreignCustomerId = Guid.Parse("10000000-0000-0000-0000-000000000002");
            var lowerInvoiceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var higherInvoiceId = Guid.Parse("20000000-0000-0000-0000-000000000002");
            var foreignCustomerInvoiceId = Guid.Parse("20000000-0000-0000-0000-000000000003");
            var explicitMismatchInvoiceId = Guid.Parse("20000000-0000-0000-0000-000000000004");
            var versionGroupId = Guid.Parse("30000000-0000-0000-0000-000000000001");

            db.Customers.AddRange(
                CreateCustomer(itworldCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld),
                CreateCustomer(foreignCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));

            var legacyBlank = CreateScopedStoredInvoice(
                lowerInvoiceId,
                itworldCustomerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            legacyBlank.TenantCode = string.Empty;
            legacyBlank.OfficeCode = string.Empty;
            legacyBlank.ResponsibleOfficeCode = "legacy-invalid";
            legacyBlank.UpdatedAtUtc = new DateTime(2026, 7, 30, 23, 59, 0, DateTimeKind.Utc);

            var stableWinner = CreateScopedStoredInvoice(
                higherInvoiceId,
                itworldCustomerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            stableWinner.UpdatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            var foreignCustomer = CreateScopedStoredInvoice(
                foreignCustomerInvoiceId,
                foreignCustomerId,
                versionGroupId,
                versionNumber: 99,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            var explicitMismatch = CreateScopedStoredInvoice(
                explicitMismatchInvoiceId,
                itworldCustomerId,
                versionGroupId,
                versionNumber: 100,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);

            db.Invoices.AddRange(legacyBlank, stableWinner, foreignCustomer, explicitMismatch);
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var latest = await service.GetLatestInvoiceVersionAsync(lowerInvoiceId, session);
            var scopedVersions = await service.GetInvoiceVersionsAsync(lowerInvoiceId, session);
            var unscopedVersions = await service.GetInvoiceVersionsAsync(lowerInvoiceId);

            Assert.NotNull(latest);
            Assert.Equal(higherInvoiceId, latest!.Id);
            Assert.Equal(
                new[] { higherInvoiceId, lowerInvoiceId },
                scopedVersions.Select(current => current.Id).ToArray());
            Assert.Equal(
                new[] { higherInvoiceId, lowerInvoiceId },
                unscopedVersions.Select(current => current.Id).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_DemotesOnlyExactCompositeVersionChain()
    {
        PrepareAppRoot("georaeplan-invoice-version-composite-save");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var selectedCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var selectedInvoiceId = Guid.NewGuid();
            var foreignInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld),
                CreateCustomer(foreignCustomerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            db.Invoices.AddRange(
                CreateScopedStoredInvoice(
                    selectedInvoiceId,
                    selectedCustomerId,
                    versionGroupId,
                    versionNumber: 1,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld),
                CreateScopedStoredInvoice(
                    foreignInvoiceId,
                    foreignCustomerId,
                    versionGroupId,
                    versionNumber: 99,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var edit = CreateInvoice(selectedCustomerId, OfficeCodeCatalog.Itworld);
            edit.Id = selectedInvoiceId;
            edit.VersionGroupId = versionGroupId;
            edit.TenantCode = TenantScopeCatalog.Itworld;

            var result = await service.SaveInvoiceAsync(
                edit,
                CreateSaveContext("itworld-editor", OfficeCodeCatalog.Itworld),
                session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var selectedVersions = await db.Invoices.IgnoreQueryFilters()
                .Where(current => current.CustomerId == selectedCustomerId)
                .ToListAsync();
            Assert.Equal(2, selectedVersions.Count);
            Assert.Equal(result.SavedInvoiceId, Assert.Single(selectedVersions, current => current.IsLatestVersion).Id);

            var foreign = await db.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == foreignInvoiceId);
            Assert.True(foreign.IsLatestVersion);
            Assert.False(foreign.IsDirty);
            Assert.Equal(99, foreign.VersionNumber);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteInvoiceAsync_DeletesOnlyExactCompositeVersionChain()
    {
        PrepareAppRoot("georaeplan-invoice-version-composite-delete");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var selectedCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var selectedInvoiceId = Guid.NewGuid();
            var selectedPreviousId = Guid.NewGuid();
            var foreignInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld),
                CreateCustomer(foreignCustomerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            db.Invoices.AddRange(
                CreateScopedStoredInvoice(
                    selectedPreviousId,
                    selectedCustomerId,
                    versionGroupId,
                    versionNumber: 1,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    isLatest: false),
                CreateScopedStoredInvoice(
                    selectedInvoiceId,
                    selectedCustomerId,
                    versionGroupId,
                    versionNumber: 2,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld),
                CreateScopedStoredInvoice(
                    foreignInvoiceId,
                    foreignCustomerId,
                    versionGroupId,
                    versionNumber: 50,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.DeleteInvoiceAsync(selectedInvoiceId, session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var selectedVersions = await db.Invoices.IgnoreQueryFilters()
                .Where(current => current.CustomerId == selectedCustomerId)
                .ToListAsync();
            Assert.All(selectedVersions, current => Assert.True(current.IsDeleted));
            Assert.All(selectedVersions, current => Assert.True(current.IsDirty));

            var foreign = await db.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == foreignInvoiceId);
            Assert.False(foreign.IsDeleted);
            Assert.True(foreign.IsLatestVersion);
            Assert.False(foreign.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task NormalizeLatestInvoiceVersionGroupsAsync_SeparatesRawCollisionAndUsesStableIdTieBreak()
    {
        PrepareAppRoot("georaeplan-invoice-version-composite-normalize");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var selectedCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var lowerInvoiceId = Guid.Parse("40000000-0000-0000-0000-000000000001");
            var higherInvoiceId = Guid.Parse("40000000-0000-0000-0000-000000000002");
            var foreignInvoiceId = Guid.Parse("40000000-0000-0000-0000-000000000003");
            var versionGroupId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld),
                CreateCustomer(foreignCustomerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));

            var lower = CreateScopedStoredInvoice(
                lowerInvoiceId,
                selectedCustomerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            lower.UpdatedAtUtc = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);
            var higher = CreateScopedStoredInvoice(
                higherInvoiceId,
                selectedCustomerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            higher.UpdatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
            var foreign = CreateScopedStoredInvoice(
                foreignInvoiceId,
                foreignCustomerId,
                versionGroupId,
                versionNumber: 100,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            db.Invoices.AddRange(lower, higher, foreign);
            await db.SaveChangesAsync();

            var session = CreateAdminSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var changed = await service.NormalizeLatestInvoiceVersionGroupsAsync([versionGroupId]);

            Assert.Equal(1, changed);
            db.ChangeTracker.Clear();
            Assert.False((await db.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == lowerInvoiceId)).IsLatestVersion);
            Assert.True((await db.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == higherInvoiceId)).IsLatestVersion);
            var foreignAfter = await db.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == foreignInvoiceId);
            Assert.True(foreignAfter.IsLatestVersion);
            Assert.False(foreignAfter.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairDirtyInvoicesForSyncAsync_DoesNotRelinkCustomerAcrossRawVersionGroupCollision()
    {
        PrepareAppRoot("georaeplan-invoice-version-composite-dirty-repair");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var missingCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var selectedInvoiceId = Guid.NewGuid();
            var foreignInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(
                foreignCustomerId,
                OfficeCodeCatalog.Itworld,
                TenantScopeCatalog.Itworld));
            var selected = CreateScopedStoredInvoice(
                selectedInvoiceId,
                missingCustomerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            selected.IsDirty = true;
            var foreign = CreateScopedStoredInvoice(
                foreignInvoiceId,
                foreignCustomerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            foreign.IsDirty = true;
            db.Invoices.AddRange(selected, foreign);
            await db.SaveChangesAsync();

            var session = CreateAdminSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.RepairDirtyInvoicesForSyncAsync(session);

            Assert.Equal(0, result.ResolvedMissingCustomerCount);
            db.ChangeTracker.Clear();
            var selectedAfter = await db.Invoices.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == selectedInvoiceId);
            Assert.Equal(missingCustomerId, selectedAfter.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Itworld, selectedAfter.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveInvoiceAsync_RejectsChangingPersistedCompositeChainCustomer()
    {
        PrepareAppRoot("georaeplan-invoice-save-composite-immutable");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var originalCustomerId = Guid.NewGuid();
            var replacementCustomerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(originalCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld),
                CreateCustomer(replacementCustomerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));
            db.Invoices.Add(CreateScopedStoredInvoice(
                invoiceId,
                originalCustomerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var edit = CreateInvoice(replacementCustomerId, OfficeCodeCatalog.Itworld);
            edit.Id = invoiceId;
            edit.VersionGroupId = versionGroupId;
            edit.TenantCode = TenantScopeCatalog.Itworld;

            var result = await service.SaveInvoiceAsync(
                edit,
                CreateSaveContext("itworld-editor", OfficeCodeCatalog.Itworld),
                session);

            Assert.False(result.Success);
            Assert.Contains("버전", result.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            var stored = Assert.Single(await db.Invoices.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(originalCustomerId, stored.CustomerId);
            Assert.True(stored.IsLatestVersion);
            Assert.False(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetInvoiceAsync_LegacyInvalidScopeUsesLinkedCustomerAndDeniesForeignTenant()
    {
        PrepareAppRoot("georaeplan-invoice-direct-read-customer-aware");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                invoiceId,
                versionNumber: 1,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld);
            invoice.TenantCode = string.Empty;
            invoice.OfficeCode = string.Empty;
            invoice.ResponsibleOfficeCode = "legacy-invalid";
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var usenetSession = CreateInvoiceEditorSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                usenetSession);

            Assert.Null(await service.GetInvoiceAsync(invoiceId, usenetSession));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_PreservesExactChainWithNewerActiveDirtyOutboxVersion()
    {
        PrepareAppRoot("georaeplan-invoice-server-purge-chain-preflight");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var purgedInvoiceId = Guid.NewGuid();
            var newerInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(
                customerId,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup));
            var purged = CreateScopedStoredInvoice(
                purgedInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isLatest: false);
            purged.IsDeleted = true;
            purged.Revision = 5;
            var newer = CreateScopedStoredInvoice(
                newerInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            newer.Revision = 6;
            newer.IsDirty = true;
            db.Invoices.AddRange(purged, newer);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                MutationId = $"invoice:{newerInvoiceId:N}:6",
                EntityName = nameof(LocalInvoice),
                EntityId = newerInvoiceId,
                ExpectedRevision = 5,
                Status = "Prepared"
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                purgedInvoiceId);

            Assert.False(result.Success);
            Assert.Contains("보류", result.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            Assert.True(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == purgedInvoiceId));
            Assert.True(await db.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == newerInvoiceId));
            Assert.True(await db.SyncOutboxEntries.AnyAsync(current => current.EntityId == newerInvoiceId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_RemovesCleanActiveSiblingCoveredByAuthoritativeReceipt()
    {
        PrepareAppRoot(
            "georaeplan-invoice-server-purge-clean-active-sibling");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var purgedInvoiceId = Guid.NewGuid();
            var activeSiblingId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup));
            var purged = CreateScopedStoredInvoice(
                purgedInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isLatest: false);
            purged.IsDeleted = true;
            purged.IsDirty = false;
            purged.Revision = 4;
            var activeSibling =
                CreateScopedStoredInvoice(
                    activeSiblingId,
                    customerId,
                    versionGroupId,
                    versionNumber: 2,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet);
            activeSibling.PreviousVersionId =
                purgedInvoiceId;
            activeSibling.IsDeleted = false;
            activeSibling.IsDirty = false;
            activeSibling.Revision = 5;
            db.Invoices.AddRange(
                purged,
                activeSibling);
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));

            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        purgedInvoiceId,
                        purgeRevision: 5);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == purgedInvoiceId ||
                    current.Id == activeSiblingId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_ThrowingPostCommitSubscriberDoesNotChangeCommittedResult()
    {
        PrepareAppRoot(
            "georaeplan-invoice-purge-post-commit-subscriber");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                invoiceId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            invoice.IsDeleted = true;
            invoice.IsDirty = false;
            invoice.Revision = 4;
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();

            var notifier =
                new DesktopDataChangeNotifier();
            var historyEventCount = 0;
            var inventoryEventCount = 0;
            notifier.ItemInvoiceHistoryChanged +=
                (_, _) =>
                {
                    historyEventCount++;
                    throw new InvalidOperationException(
                        "simulated direct purge subscriber failure");
                };
            notifier.InventoryStateChanged +=
                (_, _) => inventoryEventCount++;
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet),
                notifier);

            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        purgeRevision: 4);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, historyEventCount);
            Assert.Equal(1, inventoryEventCount);
            await using var verificationDb =
                new LocalDbContext();
            Assert.False(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyConfirmedServerPurgeInvoice_AcknowledgesOnlyExactDeletionPlanInBusinessDatabase()
    {
        PrepareAppRoot(
            "georaeplan-confirmed-invoice-purge-plan");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var firstInvoiceId = Guid.NewGuid();
            var secondInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var unrelatedEntityId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(
                customerId,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup));

            var firstInvoice = CreateScopedStoredInvoice(
                firstInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isLatest: false);
            firstInvoice.IsDeleted = true;
            firstInvoice.IsDirty = true;
            firstInvoice.Revision = 4;
            var secondInvoice = CreateScopedStoredInvoice(
                secondInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            secondInvoice.PreviousVersionId = firstInvoiceId;
            secondInvoice.IsDeleted = true;
            secondInvoice.IsDirty = true;
            secondInvoice.Revision = 5;
            db.Invoices.AddRange(firstInvoice, secondInvoice);
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = secondInvoiceId,
                IsDeleted = true,
                IsDirty = true,
                Revision = 5
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Usenet,
                LinkedInvoiceId = secondInvoiceId,
                IsDeleted = true,
                IsDirty = true,
                Revision = 5
            });
            db.TransactionAttachments.Add(
                new LocalTransactionAttachment
                {
                    Id = attachmentId,
                    TransactionId = transactionId,
                    IsDeleted = true,
                    IsDirty = true,
                    Revision = 5
                });

            var plannedOutboxIds = new[]
            {
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid()
            };
            db.SyncOutboxEntries.AddRange(
                CreatePurgeOutbox(
                    plannedOutboxIds[0],
                    nameof(LocalInvoice),
                    firstInvoiceId,
                    "USENET"),
                CreatePurgeOutbox(
                    plannedOutboxIds[1],
                    "Invoice",
                    secondInvoiceId,
                    "USENET"),
                CreatePurgeOutbox(
                    plannedOutboxIds[2],
                    nameof(LocalPayment),
                    paymentId,
                    "USENET"),
                CreatePurgeOutbox(
                    plannedOutboxIds[3],
                    "TransactionRecord",
                    transactionId,
                    "USENET"),
                CreatePurgeOutbox(
                    plannedOutboxIds[4],
                    "TransactionAttachment",
                    attachmentId,
                    "USENET"),
                CreatePurgeOutbox(
                    Guid.NewGuid(),
                    nameof(LocalInvoice),
                    firstInvoiceId,
                    "ITWORLD"),
                CreatePurgeOutbox(
                    Guid.NewGuid(),
                    nameof(LocalInvoice),
                    unrelatedEntityId,
                    "USENET"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var confirmationFence =
                await service
                    .CaptureServerPurgeConfirmationFenceAsync(
                        RecycleBinEntityKind.Invoice,
                        firstInvoiceId,
                        businessDatabaseName: "USENET");
            Assert.NotNull(confirmationFence);

            var result =
                await service
                    .ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        firstInvoiceId,
                        acceptedRevision:
                            firstInvoice.Revision,
                        businessDatabaseName: "USENET",
                        confirmationFence);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == firstInvoiceId ||
                    current.Id == secondInvoiceId));
            Assert.False(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == paymentId));
            Assert.False(await db.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == transactionId));
            Assert.False(await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == attachmentId));

            var plannedOutboxStates = await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(current =>
                    plannedOutboxIds.Contains(current.Id))
                .Select(current => new
                {
                    current.Status,
                    current.ExpectedRevision,
                    current.AcceptedRevision
                })
                .ToListAsync();
            Assert.Equal(
                plannedOutboxIds.Length,
                plannedOutboxStates.Count);
            Assert.All(
                plannedOutboxStates,
                state =>
                {
                    Assert.Equal(
                        "Acknowledged",
                        state.Status);
                    Assert.True(
                        state.AcceptedRevision >=
                        state.ExpectedRevision);
                });
            Assert.Equal(
                2,
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .CountAsync(current =>
                        current.Status == "Prepared"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyConfirmedServerPurgeInvoice_PreRequestFenceRejectsConcurrentLocalMutation()
    {
        PrepareAppRoot(
            "georaeplan-confirmed-invoice-purge-fence");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var concurrentPaymentId = Guid.NewGuid();
            db.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                Guid.NewGuid(),
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            invoice.IsDeleted = true;
            invoice.IsDirty = true;
            invoice.Revision = 4;
            db.Invoices.Add(invoice);
            var originalOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalInvoice),
                invoiceId,
                "USENET");
            originalOutbox.ExpectedRevision = 4;
            db.SyncOutboxEntries.Add(originalOutbox);
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));
            var confirmationFence =
                await service
                    .CaptureServerPurgeConfirmationFenceAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        "USENET");
            Assert.NotNull(confirmationFence);

            await using (var concurrentDb =
                         new LocalDbContext())
            {
                var concurrentInvoice =
                    await concurrentDb.Invoices
                        .IgnoreQueryFilters()
                        .SingleAsync(current =>
                            current.Id == invoiceId);
                concurrentInvoice.Memo =
                    "서버 요청 대기 중 변경";
                concurrentInvoice.UpdatedAtUtc =
                    concurrentInvoice.UpdatedAtUtc
                        .AddSeconds(1);
                concurrentDb.Payments.Add(
                    new LocalPayment
                    {
                        Id = concurrentPaymentId,
                        InvoiceId = invoiceId,
                        IsDeleted = true,
                        IsDirty = true,
                        Revision = 4
                    });
                var concurrentOutbox =
                    CreatePurgeOutbox(
                        Guid.NewGuid(),
                        nameof(LocalInvoice),
                        invoiceId,
                        "USENET");
                concurrentOutbox.ExpectedRevision = 4;
                concurrentDb.SyncOutboxEntries.Add(
                    concurrentOutbox);
                await concurrentDb.SaveChangesAsync();
            }

            var result =
                await service
                    .ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        acceptedRevision: 4,
                        businessDatabaseName: "USENET",
                        confirmationFence);

            Assert.False(result.Success);
            Assert.Contains(
                "변경",
                result.Message,
                StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            Assert.True(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.True(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id ==
                    concurrentPaymentId));
            Assert.Equal(
                2,
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .CountAsync(current =>
                        current.EntityId == invoiceId &&
                        current.Status == "Prepared"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyConfirmedServerPurgeInvoice_PreRequestFenceRejectsLineOnlyConcurrentCommit()
    {
        PrepareAppRoot(
            "georaeplan-confirmed-invoice-purge-line-fence");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            db.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                Guid.NewGuid(),
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            invoice.IsDeleted = true;
            invoice.IsDirty = true;
            invoice.Revision = 4;
            invoice.Lines.Add(
                new LocalInvoiceLine
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "펜스 품목",
                    Quantity = 1m,
                    Remark = "요청 전"
                });
            db.Invoices.Add(invoice);
            var originalOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalInvoice),
                invoiceId,
                "USENET");
            originalOutbox.ExpectedRevision = 4;
            db.SyncOutboxEntries.Add(originalOutbox);
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));
            var confirmationFence =
                await service
                    .CaptureServerPurgeConfirmationFenceAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        "USENET");
            Assert.NotNull(confirmationFence);

            await using (var concurrentDb =
                         new LocalDbContext())
            {
                var concurrentLine =
                    await concurrentDb.InvoiceLines
                        .IgnoreQueryFilters()
                        .SingleAsync(current =>
                            current.Id == lineId);
                concurrentLine.Remark =
                    "서버 요청 중 라인만 변경";
                await concurrentDb.SaveChangesAsync();
            }

            var result =
                await service
                    .ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        acceptedRevision: 4,
                        businessDatabaseName: "USENET",
                        confirmationFence);

            Assert.False(result.Success);
            Assert.Contains(
                "하위",
                result.Message,
                StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            Assert.True(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.Equal(
                "서버 요청 중 라인만 변경",
                await db.InvoiceLines
                    .IgnoreQueryFilters()
                    .Where(current =>
                        current.Id == lineId)
                    .Select(current =>
                        current.Remark)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_IgnoresSameIdPendingOutboxFromAnotherBusinessDatabase()
    {
        PrepareAppRoot(
            "georaeplan-invoice-purge-foreign-outbox");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(
                customerId,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                invoiceId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            invoice.IsDeleted = true;
            invoice.IsDirty = false;
            invoice.Revision = 5;
            db.Invoices.Add(invoice);
            var foreignOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalInvoice),
                invoiceId,
                "ITWORLD");
            db.SyncOutboxEntries.Add(foreignOutbox);
            await db.SaveChangesAsync();

            var session = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result =
                await service.ApplyServerPurgeRecycleBinEntryAsync(
                    RecycleBinEntityKind.Invoice,
                    invoiceId,
                    purgeRevision: 5);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.Equal(
                "Prepared",
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .Where(current =>
                        current.Id == foreignOutbox.Id)
                    .Select(current => current.Status)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeInvoice_DefersWhenDeletedPaymentHasDirtyOrPendingMutation()
    {
        PrepareAppRoot(
            "georaeplan-invoice-purge-dependent-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            db.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Usenet,
                    TenantScopeCatalog.UsenetGroup));
            var invoice = CreateScopedStoredInvoice(
                invoiceId,
                customerId,
                invoiceId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            invoice.IsDeleted = true;
            invoice.IsDirty = false;
            invoice.Revision = 5;
            db.Invoices.Add(invoice);
            db.Payments.Add(
                new LocalPayment
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    IsDeleted = true,
                    IsDirty = true,
                    Revision = 5
                });
            var paymentOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalPayment),
                paymentId,
                "USENET");
            paymentOutbox.ExpectedRevision = 5;
            db.SyncOutboxEntries.Add(paymentOutbox);
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession(
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet));

            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        purgeRevision: 5);

            Assert.False(result.Success);
            Assert.Contains(
                "연결 데이터",
                result.Message,
                StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            Assert.True(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.True(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == paymentId));
            Assert.Equal(
                "Prepared",
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .Where(current =>
                        current.Id == paymentOutbox.Id)
                    .Select(current => current.Status)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyConfirmedServerPurgeInvoice_MissingLocalRowStillPersistsExactOutboxAcknowledgement()
    {
        PrepareAppRoot(
            "georaeplan-confirmed-missing-invoice-outbox");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var missingInvoiceId = Guid.NewGuid();
            var localOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalInvoice),
                missingInvoiceId,
                "USENET");
            var foreignOutbox = CreatePurgeOutbox(
                Guid.NewGuid(),
                nameof(LocalInvoice),
                missingInvoiceId,
                "ITWORLD");
            db.SyncOutboxEntries.AddRange(
                localOutbox,
                foreignOutbox);
            await db.SaveChangesAsync();

            var session = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            var result =
                await service
                    .ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Invoice,
                        missingInvoiceId,
                        acceptedRevision: 7,
                        businessDatabaseName: "USENET");

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var storedLocal = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == localOutbox.Id);
            Assert.Equal(
                "Acknowledged",
                storedLocal.Status);
            Assert.Equal(7, storedLocal.AcceptedRevision);
            Assert.NotNull(storedLocal.AcknowledgedAtUtc);
            Assert.Equal(
                "Prepared",
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .Where(current =>
                        current.Id == foreignOutbox.Id)
                    .Select(current => current.Status)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RecycleBinBatch_LocalApplyOrdersInvoiceRootBeforeSelectedLinkedPayment()
    {
        PrepareAppRoot(
            "georaeplan-recycle-bin-batch-invoice-root-first");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var customer = CreateCustomer(
                customerId,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup);
            customer.IsDirty = false;
            customer.Revision = 5;
            var invoice = CreateStoredInvoice(
                invoiceId,
                customerId,
                invoiceId,
                versionNumber: 1,
                isLatest: true,
                taxIssued: false);
            invoice.IsDeleted = true;
            invoice.Revision = 5;
            invoice.UpdatedAtUtc = now;
            var payment = new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate =
                    new DateOnly(2026, 7, 31),
                Amount = 30_000m,
                Revision = 5,
                IsDirty = false,
                IsDeleted = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            db.AddRange(customer, invoice, payment);
            await db.SaveChangesAsync();

            var session = CreateAdminSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var fence =
                await service
                    .CaptureServerPurgeConfirmationFenceAsync(
                        RecycleBinEntityKind.Invoice,
                        invoiceId,
                        "USENET");
            Assert.NotNull(fence);

            var paymentEntry = new RecycleBinEntry
            {
                EntityId = paymentId,
                Kind = RecycleBinEntityKind.Payment,
                TenantCode =
                    TenantScopeCatalog.UsenetGroup,
                OfficeCode =
                    OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                Title = "selected linked payment",
                DeletedAtUtc = now,
                Revision = 5
            };
            var invoiceEntry = new RecycleBinEntry
            {
                EntityId = invoiceId,
                Kind = RecycleBinEntityKind.Invoice,
                TenantCode =
                    TenantScopeCatalog.UsenetGroup,
                OfficeCode =
                    OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                Title = "selected invoice root",
                DeletedAtUtc = now,
                Revision = 5
            };
            var orderMethod =
                typeof(EnvironmentSettingsViewModel)
                    .GetMethod(
                        "OrderSuccessfulPurgeEntriesForLocalApply",
                        BindingFlags.Static |
                        BindingFlags.NonPublic);
            Assert.NotNull(orderMethod);
            var orderedEntries =
                Assert.IsAssignableFrom<
                    IReadOnlyList<RecycleBinEntry>>(
                    orderMethod.Invoke(
                        null,
                        [
                            new List<RecycleBinEntry>
                            {
                                paymentEntry,
                                invoiceEntry
                            }
                        ]));
            Assert.Equal(2, orderedEntries.Count);
            Assert.Equal(
                RecycleBinEntityKind.Invoice,
                orderedEntries[0].Kind);

            foreach (var entry in orderedEntries)
            {
                var result =
                    entry.Kind ==
                    RecycleBinEntityKind.Invoice
                        ? await service
                            .ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                                entry.Kind,
                                entry.EntityId,
                                entry.Revision,
                                "USENET",
                                fence!)
                        : await service
                            .ApplyServerPurgeRecycleBinEntryAsync(
                                entry.Kind,
                                entry.EntityId,
                                entry.Revision);
                Assert.True(result.Success, result.Message);
            }

            db.ChangeTracker.Clear();
            Assert.False(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.False(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == paymentId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RecycleBinPurgeReconciliation_TransactionCascadeCoversFailedSameIdPayment()
    {
        var sharedId = Guid.NewGuid();
        var paymentEntry = new RecycleBinEntry
        {
            EntityId = sharedId,
            Kind = RecycleBinEntityKind.Payment,
            Title = "failed payment action"
        };
        var transactionEntry = new RecycleBinEntry
        {
            EntityId = sharedId,
            Kind = RecycleBinEntityKind.Transaction,
            Title = "successful transaction cascade"
        };
        var paymentFailure =
            "수금/지급 · failed payment action: server payment purge failed";
        var result =
            EnvironmentSettingsViewModel
                .ReconcileSuccessfulPurgeCascades(
                    [paymentEntry, transactionEntry],
                    [
                        new EnvironmentSettingsViewModel
                            .RecycleBinSuccessfulLocalPurge(
                                transactionEntry,
                                ConfirmationFence: null)
                    ],
                    [paymentFailure],
                    new Dictionary<
                        (
                            RecycleBinEntityKind Kind,
                            Guid EntityId),
                        List<string>>
                    {
                        [(paymentEntry.Kind,
                            paymentEntry.EntityId)] =
                            [paymentFailure]
                    });

        Assert.Equal(2, result.SucceededCount);
        Assert.Contains(
            (paymentEntry.Kind,
                paymentEntry.EntityId),
            result.CoveredEntries);
        Assert.Contains(
            (transactionEntry.Kind,
                transactionEntry.EntityId),
            result.CoveredEntries);
        Assert.Empty(result.RemainingServerFailures);
        var statusMethod =
            typeof(EnvironmentSettingsViewModel)
                .GetMethod(
                    "BuildRecycleBinMutationStatusMessage",
                    BindingFlags.Static |
                    BindingFlags.NonPublic);
        Assert.NotNull(statusMethod);
        Assert.Equal(
            "휴지통 항목 2건을 영구삭제했습니다.",
            statusMethod!.Invoke(
                null,
                [
                    "영구삭제",
                    2,
                    result.SucceededCount,
                    result.RemainingServerFailures
                ]));
    }

    [Fact]
    public void RecycleBinPurgeReconciliation_InvoiceCascadeCoversFailedPaymentAndKeepsTrueFailures()
    {
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var unrelatedTransactionId =
            Guid.NewGuid();
        var invoiceEntry = new RecycleBinEntry
        {
            EntityId = invoiceId,
            Kind = RecycleBinEntityKind.Invoice,
            Title = "successful invoice cascade"
        };
        var paymentEntry = new RecycleBinEntry
        {
            EntityId = paymentId,
            Kind = RecycleBinEntityKind.Payment,
            Title = "covered revision failure"
        };
        var unrelatedEntry = new RecycleBinEntry
        {
            EntityId = unrelatedTransactionId,
            Kind = RecycleBinEntityKind.Transaction,
            Title = "true transaction failure"
        };
        var fence =
            new LocalStateService
                .ServerPurgeConfirmationFence(
                    RecycleBinEntityKind.Invoice,
                    invoiceId,
                    "USENET",
                    [
                        new LocalStateService
                            .ServerPurgeEntityFence(
                                nameof(LocalInvoice),
                                invoiceId,
                                "invoice-state"),
                        new LocalStateService
                            .ServerPurgeEntityFence(
                                nameof(LocalPayment),
                                paymentId,
                                "payment-state")
                    ],
                    [],
                    []);
        var paymentFailure =
            "수금/지급 · covered revision failure: revision conflict";
        var unrelatedFailure =
            "거래내역 · true transaction failure: server rejected";
        var generalFailure =
            "Linux PC 서버 영구삭제 결과 일부를 확인하지 못했습니다.";
        var result =
            EnvironmentSettingsViewModel
                .ReconcileSuccessfulPurgeCascades(
                    [
                        paymentEntry,
                        invoiceEntry,
                        unrelatedEntry
                    ],
                    [
                        new EnvironmentSettingsViewModel
                            .RecycleBinSuccessfulLocalPurge(
                                invoiceEntry,
                                fence)
                    ],
                    [
                        paymentFailure,
                        paymentFailure,
                        unrelatedFailure,
                        generalFailure
                    ],
                    new Dictionary<
                        (
                            RecycleBinEntityKind Kind,
                            Guid EntityId),
                        List<string>>
                    {
                        [(paymentEntry.Kind,
                            paymentEntry.EntityId)] =
                            [paymentFailure],
                        [(unrelatedEntry.Kind,
                            unrelatedEntry.EntityId)] =
                            [unrelatedFailure]
                    });

        Assert.Equal(2, result.SucceededCount);
        Assert.Contains(
            (paymentEntry.Kind,
                paymentEntry.EntityId),
            result.CoveredEntries);
        Assert.Contains(
            (invoiceEntry.Kind,
                invoiceEntry.EntityId),
            result.CoveredEntries);
        Assert.Single(
            result.RemainingServerFailures,
            current =>
                string.Equals(
                    current,
                    paymentFailure,
                    StringComparison.Ordinal));
        Assert.Contains(
            unrelatedFailure,
            result.RemainingServerFailures);
        Assert.Contains(
            generalFailure,
            result.RemainingServerFailures);
    }

    [Fact]
    public async Task SaveInvoiceAsync_UnpersistedItworldDraftDefaultsContinueExactChain()
    {
        PrepareAppRoot("georaeplan-invoice-unpersisted-draft-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var existingInvoiceId = Guid.NewGuid();
            var draftInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(customerId, OfficeCodeCatalog.Itworld, TenantScopeCatalog.Itworld));
            db.Invoices.Add(CreateScopedStoredInvoice(
                existingInvoiceId,
                customerId,
                versionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var draft = new LocalInvoice
            {
                Id = draftInvoiceId,
                CustomerId = customerId,
                VersionGroupId = versionGroupId,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        ItemNameOriginal = "ITWORLD draft default scope item",
                        ItemTrackingType = ItemTrackingTypes.NonStock,
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 1000m,
                        LineAmount = 1000m
                    }
                }
            };

            var result = await service.SaveInvoiceAsync(
                draft,
                CreateSaveContext("itworld-editor", OfficeCodeCatalog.Itworld),
                session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var saved = await db.Invoices.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == result.SavedInvoiceId);
            Assert.Equal(2, saved.VersionNumber);
            Assert.Equal(existingInvoiceId, saved.PreviousVersionId);
            Assert.Equal(versionGroupId, saved.VersionGroupId);
            Assert.Equal(TenantScopeCatalog.Itworld, saved.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, saved.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SalesViewModel_LoadInvoiceVersionsSeedsSelectedInvoiceIdWhenGroupIdCollides()
    {
        PrepareAppRoot("georaeplan-sales-version-selected-seed");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var selectedCustomerId = Guid.NewGuid();
            var foreignCustomerId = Guid.NewGuid();
            var collisionGroupId = Guid.NewGuid();
            var selectedPreviousId = Guid.NewGuid();
            var selectedLatestId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup),
                CreateCustomer(foreignCustomerId, OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
            var foreign = CreateScopedStoredInvoice(
                collisionGroupId,
                foreignCustomerId,
                Guid.NewGuid(),
                versionNumber: 50,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var selectedPrevious = CreateScopedStoredInvoice(
                selectedPreviousId,
                selectedCustomerId,
                collisionGroupId,
                versionNumber: 1,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                isLatest: false);
            var selectedLatest = CreateScopedStoredInvoice(
                selectedLatestId,
                selectedCustomerId,
                collisionGroupId,
                versionNumber: 2,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            db.Invoices.AddRange(foreign, selectedPrevious, selectedLatest);
            await db.SaveChangesAsync();

            var session = CreateInvoiceEditorSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            using var viewModel = new SalesViewModel(
                service,
                print: null!,
                invoicePrintService: null!,
                session);
            var loadVersions = typeof(SalesViewModel).GetMethod(
                "LoadInvoiceVersionsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(loadVersions);

            var loadTask = Assert.IsAssignableFrom<Task>(
                loadVersions!.Invoke(viewModel, [selectedLatest, 0]));
            await loadTask;

            Assert.Equal(
                new[] { selectedLatestId, selectedPreviousId },
                viewModel.InvoiceVersions.Select(current => current.Id).ToArray());
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
            LastSavedAtUtc = new DateTime(2026, 7, 6, 0, versionNumber % 60, 0, DateTimeKind.Utc),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 6, 0, versionNumber % 60, 0, DateTimeKind.Utc),
            IsDirty = false
        };

    private static LocalInvoice CreateScopedStoredInvoice(
        Guid id,
        Guid customerId,
        Guid versionGroupId,
        int versionNumber,
        string tenantCode,
        string officeCode,
        bool isLatest = true)
    {
        var invoice = CreateStoredInvoice(
            id,
            customerId,
            versionGroupId,
            versionNumber,
            isLatest,
            taxIssued: false);
        invoice.TenantCode = tenantCode;
        invoice.OfficeCode = officeCode;
        invoice.ResponsibleOfficeCode = officeCode;
        invoice.SourceWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(officeCode);
        return invoice;
    }

    private static LocalSyncOutboxEntry CreatePurgeOutbox(
        Guid id,
        string entityName,
        Guid entityId,
        string businessDatabaseName)
        => new()
        {
            Id = id,
            MutationId =
                $"confirmed-purge:{entityName}:{entityId:N}:{id:N}",
            DeviceId = "confirmed-purge-test-device",
            EntityName = entityName,
            EntityId = entityId,
            ExpectedRevision = 5,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode =
                OfficeCodeCatalog.Usenet,
            BusinessDatabaseName =
                businessDatabaseName,
            SessionId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Status = "Prepared",
            PreparedAtUtc = DateTime.UtcNow
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
