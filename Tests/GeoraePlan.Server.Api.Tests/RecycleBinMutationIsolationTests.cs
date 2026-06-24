using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RecycleBinMutationIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RecycleBinMutationIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var dbContext = CreateDbContext(CreateAdminUser());
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task Restore_ClearsFailedMutationTrackerBeforeNextSuccessfulItem()
    {
        var user = CreateAdminUser();
        var customerId = Guid.Parse("a8111111-1111-1111-1111-111111111111");
        var linkedInvoiceId = Guid.Parse("a8222222-2222-2222-2222-222222222222");
        var mismatchedInvoiceId = Guid.Parse("a8333333-3333-3333-3333-333333333333");
        var transactionAndPaymentId = Guid.Parse("a8444444-4444-4444-4444-444444444444");
        var itemId = Guid.Parse("a8555555-5555-5555-5555-555555555555");

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Recycle batch failed restore customer",
                NameMatchKey = "recyclebatchfailedrestorecustomer",
                IsDeleted = true
            });

            seedDb.Invoices.AddRange(
                new Invoice
                {
                    Id = linkedInvoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    InvoiceNumber = "RB-FAIL-001",
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 6, 24),
                    IsDeleted = false
                },
                new Invoice
                {
                    Id = mismatchedInvoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    InvoiceNumber = "RB-MISMATCH-001",
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 6, 24),
                    IsDeleted = false
                });

            seedDb.Transactions.Add(new TransactionRecord
            {
                Id = transactionAndPaymentId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 24),
                TransactionKind = "전표수금",
                LinkedInvoiceId = linkedInvoiceId,
                LinkedInvoiceNumber = "RB-FAIL-001",
                SettlementAmount = 10_000m,
                ReceiptTotal = 10_000m,
                IsDeleted = true
            });

            seedDb.Payments.Add(new Payment
            {
                Id = transactionAndPaymentId,
                InvoiceId = mismatchedInvoiceId,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 10_000m,
                Note = "Mismatched linked payment should make transaction restore fail",
                IsDeleted = true
            });

            seedDb.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Recycle batch successful item",
                NameMatchKey = "recyclebatchsuccessfulitem",
                Unit = "EA",
                IsDeleted = true
            });

            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var controller = CreateRecycleBinController(scopedDb, user);

        var response = await controller.Restore(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = transactionAndPaymentId,
                    Kind = "transaction"
                },
                new RecycleBinMutationTargetDto
                {
                    EntityId = itemId,
                    Kind = "item"
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        Assert.Equal(2, payload.RequestedCount);
        Assert.Equal(1, payload.SucceededCount);
        Assert.False(payload.Results[0].Success);
        Assert.Contains("일치하지 않아 복원할 수 없습니다", payload.Results[0].Message, StringComparison.Ordinal);
        Assert.True(payload.Results[1].Success);

        scopedDb.ChangeTracker.Clear();
        Assert.True(await scopedDb.Customers.IgnoreQueryFilters()
            .Where(customer => customer.Id == customerId)
            .Select(customer => customer.IsDeleted)
            .SingleAsync());
        Assert.True(await scopedDb.Transactions.IgnoreQueryFilters()
            .Where(transaction => transaction.Id == transactionAndPaymentId)
            .Select(transaction => transaction.IsDeleted)
            .SingleAsync());
        Assert.True(await scopedDb.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == transactionAndPaymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        Assert.False(await scopedDb.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task Purge_ClearsFailedMutationTrackerBeforeNextSuccessfulItem()
    {
        var user = CreateOfficeUser();
        var profileId = Guid.Parse("b8111111-1111-1111-1111-111111111111");
        var linkedAssetId = Guid.Parse("b8222222-2222-2222-2222-222222222222");
        var outOfScopeLogId = Guid.Parse("b8333333-3333-3333-3333-333333333333");
        var customerId = Guid.Parse("b8444444-4444-4444-4444-444444444444");

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.RentalBillingProfiles.Add(new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "purge-isolation-profile",
                CustomerName = "Purge isolation rental profile",
                BillingType = "묶음",
                IsDeleted = true
            });

            seedDb.RentalAssets.Add(new RentalAsset
            {
                Id = linkedAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "purge-isolation-asset",
                BillingProfileId = profileId,
                ManagementNumber = "PI-0001",
                AssetStatus = RentalAssetStatusNormalizer.Active,
                BillingEligibilityStatus = "청구대상"
            });

            seedDb.RentalBillingLogs.Add(new RentalBillingLog
            {
                Id = outOfScopeLogId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingProfileId = profileId,
                BillingYearMonth = "2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                Status = "예정",
                BilledAmount = 10_000m,
                IsDeleted = true
            });

            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Recycle purge success customer",
                NameMatchKey = "recyclepurgesuccesscustomer",
                IsDeleted = true
            });

            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var controller = CreateRecycleBinController(scopedDb, user);

        var response = await controller.Purge(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = profileId,
                    Kind = "rental-billing-profile"
                },
                new RecycleBinMutationTargetDto
                {
                    EntityId = customerId,
                    Kind = "customer"
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        Assert.Equal(2, payload.RequestedCount);
        Assert.Equal(1, payload.SucceededCount);
        Assert.False(payload.Results[0].Success);
        Assert.Contains("렌탈 청구로그", payload.Results[0].Message, StringComparison.Ordinal);
        Assert.True(payload.Results[1].Success);

        scopedDb.ChangeTracker.Clear();
        Assert.True(await scopedDb.RentalBillingProfiles.IgnoreQueryFilters()
            .AnyAsync(profile => profile.Id == profileId));
        Assert.Equal(profileId, await scopedDb.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id == linkedAssetId)
            .Select(asset => asset.BillingProfileId)
            .SingleAsync());
        Assert.True(await scopedDb.RentalBillingLogs.IgnoreQueryFilters()
            .AnyAsync(log => log.Id == outOfScopeLogId));
        Assert.False(await scopedDb.Customers.IgnoreQueryFilters()
            .AnyAsync(customer => customer.Id == customerId));
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new AppDbContext(options, currentUser, revisionClock);
    }

    private static RecycleBinController CreateRecycleBinController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new RecycleBinController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static TestCurrentUserContext CreateAdminUser() => new()
    {
        Username = "recycle-batch-admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin,
        IsAdmin = true,
        Permissions = [PermissionNames.DataBackupRestore]
    };

    private static TestCurrentUserContext CreateOfficeUser() => new()
    {
        Username = "recycle-batch-office",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = [PermissionNames.DataBackupRestore]
    };

    public void Dispose() => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
