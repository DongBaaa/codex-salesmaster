using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Data;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Services;
using \uac70\ub798\ud50c\ub79c.Shared.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RecycleBinScopeAndSyncTests
{
    [Fact]
    public async Task LocalStateService_GetCustomersAsync_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-fallback-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var customers = await service.GetCustomersAsync(session);

            Assert.Contains(customers, customer => customer.Id == customerId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetDirtyCustomerContractsForSyncAsync_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-contract-fallback-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.CustomerContracts.Add(new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = customerId,
                ContractType = "Fallback contract",
                FileName = "fallback-contract.pdf",
                FileSize = 12,
                FileHash = "fallback-hash",
                FileContent = [1, 2, 3],
                IsDirty = true,
                IsDeleted = false,
                Revision = 3
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                AppPermissionNames.CustomerEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);

            Assert.Contains(dirtyContracts, contract => contract.Id == contractId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreInvoice_UsesOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-invoice-fallback-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = string.Empty,
                InvoiceNumber = "FB-RESTORE-001",
                LocalTempNumber = "L202606-0001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 22),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = true,
                IsDirty = false,
                Revision = 7
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Invoice, invoiceId, session);

            Assert.True(restore.Success, restore.Message);
            Assert.False(await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.IsDeleted)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_UpdatePaymentLedgerMemo_UsesInvoiceOwnerOfficeFallbackWhenResponsibleOfficeMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-payment-memo-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(customerId, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld));
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = string.Empty,
                InvoiceNumber = "FB-MEMO-001",
                LocalTempNumber = "L202606-0002",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 22),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = invoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                IsDirty = false,
                Revision = 8
            });
            db.Payments.Add(new LocalPayment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 6, 22),
                Amount = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 9
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await service.UpdatePaymentLedgerMemoAsync(paymentId, "fallback memo", session);

            Assert.True(result.Success, result.Message);
            var payment = await db.Payments.IgnoreQueryFilters().SingleAsync(current => current.Id == paymentId);
            Assert.Equal("fallback memo", payment.Note);
            Assert.True(payment.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreCustomer_RestoresContractsDeletedWithCustomer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-customer-contract-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var primaryContractId = Guid.NewGuid();
            var secondaryContractId = Guid.NewGuid();
            var oldDeletedContractId = Guid.NewGuid();
            var originalUpdatedAt = new DateTime(2026, 6, 25, 1, 0, 0, DateTimeKind.Utc);
            var oldDeletedAt = originalUpdatedAt.AddHours(-2);

            db.Customers.Add(CreateScopedCustomer(customerId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, isDeleted: false));
            db.CustomerContracts.AddRange(
                new LocalCustomerContract
                {
                    Id = primaryContractId,
                    CustomerId = customerId,
                    ContractType = "Primary",
                    FileName = "primary.pdf",
                    IsPrimary = true,
                    IsDeleted = false,
                    IsDirty = false,
                    CreatedAtUtc = originalUpdatedAt,
                    UpdatedAtUtc = originalUpdatedAt,
                    Revision = 20
                },
                new LocalCustomerContract
                {
                    Id = secondaryContractId,
                    CustomerId = customerId,
                    ContractType = "Secondary",
                    FileName = "secondary.pdf",
                    IsPrimary = false,
                    IsDeleted = false,
                    IsDirty = false,
                    CreatedAtUtc = originalUpdatedAt,
                    UpdatedAtUtc = originalUpdatedAt,
                    Revision = 21
                },
                new LocalCustomerContract
                {
                    Id = oldDeletedContractId,
                    CustomerId = customerId,
                    ContractType = "OldDeleted",
                    FileName = "old-deleted.pdf",
                    IsPrimary = false,
                    IsDeleted = true,
                    IsDirty = false,
                    CreatedAtUtc = oldDeletedAt,
                    UpdatedAtUtc = oldDeletedAt,
                    Revision = 22
                });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var delete = await service.DeleteCustomerAsync(customerId, session);

            Assert.True(delete.Success, delete.Message);
            db.ChangeTracker.Clear();

            var deletedCustomer = await db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            var deletedContracts = await db.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(contract => contract.CustomerId == customerId)
                .ToDictionaryAsync(contract => contract.Id);

            Assert.True(deletedCustomer.IsDeleted);
            Assert.True(deletedContracts[primaryContractId].IsDeleted);
            Assert.True(deletedContracts[primaryContractId].IsPrimary);
            Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedContracts[primaryContractId].UpdatedAtUtc);
            Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedContracts[secondaryContractId].UpdatedAtUtc);
            Assert.True(deletedContracts[oldDeletedContractId].IsDeleted);
            Assert.Equal(oldDeletedAt, deletedContracts[oldDeletedContractId].UpdatedAtUtc);

            var restore = await service.RestoreCustomerAsync(customerId, session);

            Assert.True(restore.Success, restore.Message);
            db.ChangeTracker.Clear();

            var restoredCustomer = await db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            var restoredContracts = await db.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(contract => contract.CustomerId == customerId)
                .ToDictionaryAsync(contract => contract.Id);

            Assert.False(restoredCustomer.IsDeleted);
            Assert.True(restoredCustomer.IsDirty);
            Assert.False(restoredContracts[primaryContractId].IsDeleted);
            Assert.True(restoredContracts[primaryContractId].IsDirty);
            Assert.True(restoredContracts[primaryContractId].IsPrimary);
            Assert.False(restoredContracts[secondaryContractId].IsDeleted);
            Assert.True(restoredContracts[secondaryContractId].IsDirty);
            Assert.True(restoredContracts[oldDeletedContractId].IsDeleted);
            Assert.False(restoredContracts[oldDeletedContractId].IsDirty);

            var dirtyContracts = await service.GetDirtyCustomerContractsForSyncAsync(session);
            Assert.Contains(dirtyContracts, contract => contract.Id == primaryContractId);
            Assert.Contains(dirtyContracts, contract => contract.Id == secondaryContractId);
            Assert.DoesNotContain(dirtyContracts, contract => contract.Id == oldDeletedContractId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetRecycleBinEntriesAsync_FiltersRentalAssetsByBusinessDatabase()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var usenetAssetId = Guid.NewGuid();
            var itworldAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = usenetAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "USENET|TEST|001",
                    ManagementId = "803",
                    ManagementNumber = "2407-007",
                    ItemName = "IMC2010",
                    CustomerName = "USENET Customer",
                    InstallLocation = "USENET Office",
                    IsDeleted = true,
                    IsDirty = false,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                    Revision = 10
                },
                new LocalRentalAsset
                {
                    Id = itworldAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "ITWORLD|TEST|803",
                    ManagementId = "803",
                    ManagementNumber = "2603-803",
                    ItemName = "JT-7270SC",
                    CustomerName = "ITWORLD Customer",
                    InstallLocation = "Seoul HQ",
                    IsDeleted = true,
                    IsDirty = false,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    Revision = 20
                });
            await db.SaveChangesAsync();

            var usenetSession = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var itworldSession = CreateSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);

            var usenetEntries = await service.GetRecycleBinEntriesAsync(usenetSession);
            var itworldEntries = await service.GetRecycleBinEntriesAsync(itworldSession);

            var usenetEntry = Assert.Single(usenetEntries, entry => entry.EntityId == usenetAssetId);
            Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup), usenetEntry.BusinessDatabaseName);
            Assert.DoesNotContain(usenetEntries, entry => entry.EntityId == itworldAssetId);

            var itworldEntry = Assert.Single(itworldEntries, entry => entry.EntityId == itworldAssetId);
            Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), itworldEntry.BusinessDatabaseName);
            Assert.DoesNotContain(itworldEntries, entry => entry.EntityId == usenetAssetId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreActiveOutOfScopeInvoice_IsDeniedBeforeAlreadyActive()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-invoice-restore-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            db.Customers.Add(CreateFallbackOfficeCustomer(hiddenCustomerId, TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu));
            db.Invoices.Add(new LocalInvoice
            {
                Id = hiddenInvoiceId,
                CustomerId = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                InvoiceNumber = "HIDDEN-ACTIVE-RESTORE-001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 24),
                TotalAmount = 1000m,
                SupplyAmount = 909m,
                VatAmount = 91m,
                VersionGroupId = hiddenInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                IsConfirmed = true,
                IsDeleted = false,
                IsDirty = false,
                Revision = 30
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Invoice, hiddenInvoiceId, session);

            Assert.False(restore.Success);
            Assert.Contains("권한", restore.Message);
            Assert.DoesNotContain("이미 활성", restore.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeCustomer_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-customer-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.CustomerEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                hiddenCustomerId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeInvoice_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-invoice-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Invoice,
                hiddenInvoiceId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopePayment_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-payment-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenInvoiceId = Guid.NewGuid();
            var hiddenPaymentId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Invoices.Add(CreateInvoice(
                hiddenInvoiceId,
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Payments.Add(new LocalPayment
            {
                Id = hiddenPaymentId,
                InvoiceId = hiddenInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 31
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Payment,
                hiddenPaymentId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeTransaction_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-transaction-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            var hiddenTransactionId = Guid.NewGuid();
            db.Customers.Add(CreateScopedCustomer(
                hiddenCustomerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                isDeleted: false));
            db.Transactions.Add(new LocalTransaction
            {
                Id = hiddenTransactionId,
                CustomerId = hiddenCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionDate = new DateOnly(2026, 6, 24),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 1000m,
                IsDeleted = false,
                IsDirty = false,
                Revision = 32
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                hiddenTransactionId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeRentalAsset_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-rental-asset-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenAssetId = Guid.NewGuid();
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = hiddenAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                AssetKey = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ManagementId = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ManagementNumber = $"HIDDEN-ACTIVE-ASSET-{hiddenAssetId:N}",
                ItemName = "권한 외 활성 자산",
                AssetStatus = "설치",
                IsDeleted = false,
                IsDirty = false,
                Revision = 33
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                hiddenAssetId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("권한", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteActiveOutOfScopeInventoryTransfer_IsDeniedBeforeActiveState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-active-transfer-purge-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenTransferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = hiddenTransferId,
                TransferNumber = "HIDDEN-ACTIVE-TRANSFER-001",
                TransferDate = new DateOnly(2026, 6, 24),
                FromWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                ToWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                TransferStatus = "수령대기",
                IsDeleted = false,
                IsDirty = false,
                Revision = 34
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.DeliveryEdit);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var purge = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                hiddenTransferId,
                session);

            Assert.False(purge.Success);
            Assert.Contains("출발지", purge.Message);
            Assert.DoesNotContain("활성 상태", purge.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_GetRecycleBinEntries_RentalBillingLogUsesLogScopeAndDoesNotLeakHiddenProfile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-log-hidden-profile-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var hiddenProfileId = Guid.NewGuid();
            var logId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = hiddenProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                ProfileKey = "LOCAL-HIDDEN-PROFILE-001",
                CustomerName = "숨김 렌탈 거래처",
                InstallSiteName = "숨김 설치처",
                ItemName = "숨김 품목",
                IsDeleted = true,
                IsDirty = false,
                Revision = 40
            });
            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = logId,
                BillingProfileId = hiddenProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                Status = "예정",
                BilledAmount = 12000m,
                IsDeleted = true,
                IsDirty = false,
                Revision = 41
            });
            await db.SaveChangesAsync();

            var session = CreateOfficeUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var entries = await service.GetRecycleBinEntriesAsync(session);

            var entry = Assert.Single(entries, current => current.Kind == RecycleBinEntityKind.RentalBillingLog);
            Assert.Equal(logId, entry.EntityId);
            Assert.Equal("청구로그 2026-06", entry.Title);
            Assert.Equal(OfficeCodeCatalog.Usenet, entry.ResponsibleOfficeCode);
            Assert.DoesNotContain("숨김", entry.Title);
            Assert.DoesNotContain("숨김", entry.Subtitle);
            Assert.DoesNotContain("숨김", entry.Detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_RestoreRentalAsset_IgnoresActiveNaturalKeyConflictInOtherBusinessDatabase()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-restore-cross-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var targetAssetId = Guid.NewGuid();
            var hiddenAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = targetAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "USENET|RESTORE-CROSS-DB|001",
                    ManagementId = "RESTORE-CROSS-DB-ID",
                    ManagementNumber = "RESTORE-CROSS-DB-MN",
                    ItemName = "USENET restore target",
                    IsDeleted = true,
                    IsDirty = false,
                    Revision = 31
                },
                new LocalRentalAsset
                {
                    Id = hiddenAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "ITWORLD|RESTORE-CROSS-DB|001",
                    ManagementId = "RESTORE-CROSS-DB-ID",
                    ManagementNumber = "RESTORE-CROSS-DB-MN",
                    ItemName = "ITWORLD active asset",
                    IsDeleted = false,
                    IsDirty = false,
                    Revision = 32
                });
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var restore = await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.RentalAsset, targetAssetId, session);

            Assert.True(restore.Success, restore.Message);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == targetAssetId)
                .Select(asset => asset.IsDeleted)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SyncService_GetMatchingIncomingRentalAssetIds_DoesNotMatchAcrossBusinessDatabase()
    {
        var incomingId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var incomingByManagementId = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld)}|803"] = [incomingId]
        };
        var empty = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var candidate = new LocalRentalAsset
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementId = "803"
        };

        var result = InvokePrivateStatic<HashSet<Guid>>(
            typeof(SyncService),
            "GetMatchingIncomingRentalAssetIds",
            candidate,
            empty,
            incomingByManagementId,
            empty);

        Assert.Empty(result);
    }

    [Fact]
    public void SyncService_DeduplicatePulledRentalAssets_KeepsSameManagementIdAcrossBusinessDatabases()
    {
        var now = DateTime.UtcNow;
        var usenetAssetId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var itworldAssetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var incoming = new List<RentalAssetDto>
        {
            new()
            {
                Id = usenetAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementId = "803",
                AssetKey = "USENET|2407-007|IMC2010",
                CreatedAtUtc = now.AddMinutes(-10),
                UpdatedAtUtc = now.AddMinutes(-5),
                Revision = 100
            },
            new()
            {
                Id = itworldAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementId = "803",
                AssetKey = "ITWORLD|2603-803|JT-7270SC",
                CreatedAtUtc = now.AddMinutes(-9),
                UpdatedAtUtc = now.AddMinutes(-4),
                Revision = 101
            }
        };

        var result = InvokePrivateStatic<IReadOnlyList<RentalAssetDto>>(
            typeof(SyncService),
            "DeduplicatePulledRentalAssets",
            incoming);

        Assert.Contains(result, asset => asset.Id == usenetAssetId);
        Assert.Contains(result, asset => asset.Id == itworldAssetId);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task LocalDbContext_RentalAssetNaturalKeyIndexes_AreScopedByTenant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-index-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    AssetKey = "SHARED-ASSET-KEY-001",
                    ManagementId = "803",
                    ManagementNumber = "MN-803",
                    ItemName = "USENET Asset",
                    IsDeleted = false
                },
                new LocalRentalAsset
                {
                    Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "SHARED-ASSET-KEY-001",
                    ManagementId = "803",
                    ManagementNumber = "MN-803",
                    ItemName = "ITWORLD Asset",
                    IsDeleted = false
                });

            await db.SaveChangesAsync();

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET-OTHER-ASSET-KEY",
                ManagementId = "803",
                ManagementNumber = "USENET-OTHER-MN",
                ItemName = "Same tenant duplicate",
                IsDeleted = false
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteRentalAsset_RemovesAssignmentHistories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-asset-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "LOCAL-PURGE-HISTORY-001"));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
            Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedRentalAsset_RemovesAssignmentHistories()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-asset-server-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "SERVER-PURGE-HISTORY-001"));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalAsset,
                assetId);

            Assert.True(result.Success);
            Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
            Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteRentalBillingProfile_ClearsAssignmentHistoryProfileReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-profile-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateDeletedRentalBillingProfile(profileId));
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "LOCAL-PURGE-PROFILE-HISTORY-001", isDeleted: false, billingProfileId: profileId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false, billingProfileId: profileId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, billingProfileId: profileId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
            Assert.Null(await db.RentalAssets.IgnoreQueryFilters()
                .Where(current => current.Id == assetId)
                .Select(current => current.BillingProfileId)
                .SingleAsync());
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.BillingProfileId == profileId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedRentalBillingProfile_ClearsAssignmentHistoryProfileReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-rental-profile-server-purge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(CreateDeletedRentalBillingProfile(profileId));
            db.RentalAssets.Add(CreateDeletedRentalAsset(assetId, "SERVER-PURGE-PROFILE-HISTORY-001", isDeleted: false, billingProfileId: profileId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: true, isDeleted: false, billingProfileId: profileId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, billingProfileId: profileId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.RentalBillingProfile,
                profileId);

            Assert.True(result.Success);
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
            Assert.Null(await db.RentalAssets.IgnoreQueryFilters()
                .Where(current => current.Id == assetId)
                .Select(current => current.BillingProfileId)
                .SingleAsync());
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.BillingProfileId == profileId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_PermanentlyDeleteCustomer_ClearsAssignmentHistoryCustomerReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-customer-purge-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.Customers.Add(CreateDeletedCustomer(customerId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: false, customerId: customerId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, customerId: customerId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                session);

            Assert.True(result.Success);
            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.CustomerId == customerId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId && current.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalStateService_ApplyServerPurgedCustomer_ClearsAssignmentHistoryCustomerReferences()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-recycle-bin-customer-server-purge-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.Customers.Add(CreateDeletedCustomer(customerId));
            db.RentalAssetAssignmentHistories.AddRange(
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: false, customerId: customerId),
                CreateRentalAssetAssignmentHistory(assetId, isCurrent: false, isDeleted: true, customerId: customerId));
            await db.SaveChangesAsync();

            var session = CreateSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId);

            Assert.True(result.Success);
            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.CustomerId == customerId));
            Assert.Equal(
                2,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId));
            Assert.Equal(
                0,
                await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .CountAsync(current => current.AssetId == assetId && current.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateOfficeUserSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "office-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalCustomer CreateFallbackOfficeCustomer(Guid customerId, string tenantCode, string officeCode)
        => new()
        {
            Id = customerId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = $"Fallback customer {customerId:N}",
            NameMatchKey = $"fallbackcustomer{customerId:N}",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = false,
            IsDirty = false,
            Revision = 5
        };

    private static LocalCustomer CreateScopedCustomer(Guid customerId, string tenantCode, string officeCode, bool isDeleted)
        => new()
        {
            Id = customerId,
            TenantCode = tenantCode,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = $"Scoped customer {customerId:N}",
            NameMatchKey = $"scopedcustomer{customerId:N}",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 6
        };

    private static LocalInvoice CreateInvoice(
        Guid invoiceId,
        Guid customerId,
        string tenantCode,
        string responsibleOfficeCode,
        bool isDeleted)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = tenantCode,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = responsibleOfficeCode,
            InvoiceNumber = $"INV-{invoiceId:N}",
            LocalTempNumber = $"L{invoiceId:N}"[..12],
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            IsConfirmed = true,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 7
        };

    private static LocalCustomer CreateDeletedCustomer(Guid customerId)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "영구삭제 이력 거래처",
            NameMatchKey = "영구삭제이력거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true,
            IsDirty = false,
            Revision = 14
        };

    private static LocalRentalBillingProfile CreateDeletedRentalBillingProfile(Guid profileId)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-HISTORY-001",
            CustomerName = "영구삭제 이력 청구프로필",
            InstallSiteName = "테스트 설치처",
            IsDeleted = true,
            IsDirty = false,
            IsActive = false,
            Revision = 12
        };

    private static LocalRentalAsset CreateDeletedRentalAsset(
        Guid assetId,
        string key,
        bool isDeleted = true,
        Guid? billingProfileId = null)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = key,
            ManagementId = key,
            ManagementNumber = key,
            ItemName = "영구삭제 이력 자산",
            BillingProfileId = billingProfileId,
            AssetStatus = "설치",
            BillingEligibilityStatus = billingProfileId.HasValue ? "청구가능" : string.Empty,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 10
        };

    private static LocalRentalAssetAssignmentHistory CreateRentalAssetAssignmentHistory(
        Guid assetId,
        bool isCurrent,
        bool isDeleted,
        Guid? billingProfileId = null,
        Guid? customerId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            BillingProfileId = billingProfileId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = customerId.HasValue ? "영구삭제 이력 거래처" : string.Empty,
            InstallLocation = customerId.HasValue ? "과거 설치처" : string.Empty,
            BillingProfileDisplay = billingProfileId.HasValue ? "영구삭제 이력 청구프로필" : string.Empty,
            ItemName = "영구삭제 이력 자산",
            ManagementNumber = "HISTORY-001",
            IsCurrent = isCurrent,
            IsDeleted = isDeleted,
            IsDirty = false,
            Revision = 11
        };

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }
}
