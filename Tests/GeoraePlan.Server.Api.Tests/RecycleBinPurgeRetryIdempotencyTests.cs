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

public sealed class RecycleBinPurgeRetryIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RevisionClock _revisionClock = new();

    public RecycleBinPurgeRetryIdempotencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var dbContext = CreateDbContext(CreateAdminUser());
        dbContext.Database.EnsureCreated();
    }

    public static IEnumerable<object[]> SupportedPurgeKinds()
    {
        yield return ["customer"];
        yield return ["contract"];
        yield return ["item"];
        yield return ["company-profile"];
        yield return ["customer-category"];
        yield return ["price-grade-option"];
        yield return ["trade-type-option"];
        yield return ["item-category-option"];
        yield return ["invoice"];
        yield return ["payment"];
        yield return ["transaction"];
        yield return ["inventory-transfer"];
        yield return ["rental-management-company"];
        yield return ["rental-billing-profile"];
        yield return ["rental-asset"];
        yield return ["rental-billing-log"];
    }

    [Fact]
    public async Task Purge_RetryAfterCommittedCustomerPurge_ReturnsSuccessWithoutChangingReceipt()
    {
        var user = CreateAdminUser();
        var customerId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(user);
        var customer = CreateDeletedCustomer(customerId, OfficeCodeCatalog.Usenet);
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();
        var expectedRevision = customer.Revision;
        var request = CreateRequest("customer", customerId, expectedRevision);
        var controller = CreateController(dbContext, user);

        var first = GetPayload(await controller.Purge(request, CancellationToken.None));
        var firstResult = Assert.Single(first.Results);
        Assert.True(firstResult.Success, firstResult.Message);

        dbContext.ChangeTracker.Clear();
        var receiptBeforeRetry = await dbContext.RecycleBinPurgeRecords
            .AsNoTracking()
            .SingleAsync(current => current.Kind == "customer" && current.EntityId == customerId);

        var retry = GetPayload(await controller.Purge(request, CancellationToken.None));
        var retryResult = Assert.Single(retry.Results);
        Assert.True(retryResult.Success, retryResult.Message);
        Assert.Contains("이미 영구삭제가 완료", retryResult.Message, StringComparison.Ordinal);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Customers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == customerId));
        var receiptAfterRetry = await dbContext.RecycleBinPurgeRecords
            .AsNoTracking()
            .SingleAsync(current => current.Kind == "customer" && current.EntityId == customerId);
        Assert.Equal(receiptBeforeRetry.Id, receiptAfterRetry.Id);
        Assert.Equal(receiptBeforeRetry.Revision, receiptAfterRetry.Revision);
        Assert.Equal(receiptBeforeRetry.PurgedAtUtc, receiptAfterRetry.PurgedAtUtc);
    }

    [Theory]
    [MemberData(nameof(SupportedPurgeKinds))]
    public async Task Purge_PriorReceiptForSupportedKind_IsAcceptedOnlyWhileTargetIsAbsent(string kind)
    {
        var user = CreateAdminUser();
        var entityId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(user);
        dbContext.RecycleBinPurgeRecords.Add(CreatePurgeRecord(kind, entityId, OfficeCodeCatalog.Usenet));
        await dbContext.SaveChangesAsync();
        var controller = CreateController(dbContext, user);

        var payload = GetPayload(await controller.Purge(
            CreateRequest(kind, entityId, expectedRevision: 1),
            CancellationToken.None));

        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Contains("이미 영구삭제가 완료", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, payload.SucceededCount);
    }

    [Fact]
    public async Task Purge_MissingTargetWithoutReceipt_RemainsFailure()
    {
        var user = CreateAdminUser();
        var customerId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(user);
        var controller = CreateController(dbContext, user);

        var payload = GetPayload(await controller.Purge(
            CreateRequest("customer", customerId, expectedRevision: 1),
            CancellationToken.None));

        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("찾을 수 없습니다", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task Purge_OutOfScopeReceipt_IsIndistinguishableFromMissingReceipt()
    {
        var user = CreateOfficeUser();
        var outOfScopeReceiptId = Guid.NewGuid();
        var missingReceiptId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(user);
        dbContext.RecycleBinPurgeRecords.Add(
            CreatePurgeRecord("customer", outOfScopeReceiptId, OfficeCodeCatalog.Yeonsu));
        await dbContext.SaveChangesAsync();
        var controller = CreateController(dbContext, user);

        var payload = GetPayload(await controller.Purge(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    Kind = "customer",
                    EntityId = outOfScopeReceiptId,
                    ExpectedRevision = 1
                },
                new RecycleBinMutationTargetDto
                {
                    Kind = "customer",
                    EntityId = missingReceiptId,
                    ExpectedRevision = 1
                }
            ]
        }, CancellationToken.None));

        Assert.Equal(2, payload.Results.Count);
        var outOfScopeResult = payload.Results.Single(current => current.EntityId == outOfScopeReceiptId);
        var missingResult = payload.Results.Single(current => current.EntityId == missingReceiptId);
        Assert.False(outOfScopeResult.Success);
        Assert.False(missingResult.Success);
        Assert.Equal(missingResult.Message, outOfScopeResult.Message);
        Assert.Contains("찾을 수 없습니다", outOfScopeResult.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purge_SameIdRecreatedAfterEarlierPurge_DoesNotAcceptOldReceipt()
    {
        var user = CreateAdminUser();
        var customerId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(user);
        var original = CreateDeletedCustomer(customerId, OfficeCodeCatalog.Usenet);
        dbContext.Customers.Add(original);
        await dbContext.SaveChangesAsync();
        var originalRevision = original.Revision;
        var originalRequest = CreateRequest("customer", customerId, originalRevision);
        var controller = CreateController(dbContext, user);

        var first = GetPayload(await controller.Purge(originalRequest, CancellationToken.None));
        Assert.True(Assert.Single(first.Results).Success);

        dbContext.ChangeTracker.Clear();
        var recreated = CreateDeletedCustomer(customerId, OfficeCodeCatalog.Usenet);
        recreated.NameOriginal = "Recreated customer with earlier purged id";
        recreated.NameMatchKey = $"recreatedcustomer{customerId:N}";
        dbContext.Customers.Add(recreated);
        await dbContext.SaveChangesAsync();
        Assert.True(recreated.Revision > originalRevision);

        var retry = GetPayload(await controller.Purge(originalRequest, CancellationToken.None));
        var retryResult = Assert.Single(retry.Results);
        Assert.False(retryResult.Success);
        Assert.Contains("다른 PC에서 변경", retryResult.Message, StringComparison.Ordinal);

        dbContext.ChangeTracker.Clear();
        var preserved = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customerId);
        Assert.True(preserved.IsDeleted);
        Assert.Equal(recreated.Revision, preserved.Revision);
        Assert.Single(await dbContext.RecycleBinPurgeRecords
            .AsNoTracking()
            .Where(current => current.Kind == "customer" && current.EntityId == customerId)
            .ToListAsync());
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options, currentUser, _revisionClock);
    }

    private RecycleBinController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, _revisionClock),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);

    private static RecycleBinMutationRequest CreateRequest(
        string kind,
        Guid entityId,
        long expectedRevision)
        => new()
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    Kind = kind,
                    EntityId = entityId,
                    ExpectedRevision = expectedRevision
                }
            ]
        };

    private static RecycleBinMutationResultDto GetPayload(
        ActionResult<RecycleBinMutationResultDto> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
    }

    private static RecycleBinPurgeRecord CreatePurgeRecord(
        string kind,
        Guid entityId,
        string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            EntityId = entityId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            PurgedAtUtc = DateTime.UtcNow,
            IsDeleted = false
        };

    private static Customer CreateDeletedCustomer(Guid id, string responsibleOfficeCode)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = responsibleOfficeCode,
            NameOriginal = $"Deleted customer {id:N}",
            NameMatchKey = $"deletedcustomer{id:N}",
            TradeType = "매출",
            IsDeleted = true
        };

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "purge-retry-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true,
            Permissions = [PermissionNames.DataBackupRestore]
        };

    private static TestCurrentUserContext CreateOfficeUser()
        => new()
        {
            Username = "purge-retry-office",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DataBackupRestore]
        };

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
            => IsAdmin ||
               IsGodMode ||
               Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
