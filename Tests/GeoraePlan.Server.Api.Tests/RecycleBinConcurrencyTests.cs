using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RecycleBinConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RecycleBinConcurrencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task Restore_ReturnsFailedItem_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "삭제 거래처",
            NameMatchKey = "삭제거래처",
            TradeType = "매출",
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = stored.Id,
                        Kind = "customer",
                        ExpectedRevision = stored.Revision + 1
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("새로고침 후 다시 시도", item.Message);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task Purge_ReturnsFailedItem_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "삭제 품목",
            NameMatchKey = "삭제품목",
            IsDeleted = true
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().FirstAsync(x => x.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = stored.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision + 1
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("Expected revision mismatch", result.Message);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task Restore_ContinuesBatch_WhenRentalAssetNaturalKeyConflicts()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-active",
            ManagementId = "MID-RESTORE-CONFLICT",
            ManagementNumber = "MN-ACTIVE",
            ItemName = "active asset",
            IsDeleted = false
        };
        var deletedAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-deleted",
            ManagementId = "MID-RESTORE-CONFLICT",
            ManagementNumber = "MN-DELETED",
            ItemName = "deleted asset",
            IsDeleted = true
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "batch restore customer",
            NameMatchKey = "batchrestorecustomer",
            TradeType = "sales",
            IsDeleted = true
        };
        dbContext.RentalAssets.AddRange(activeAsset, deletedAsset);
        dbContext.Customers.Add(deletedCustomer);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedAsset.Id,
                        Kind = "rental-asset"
                    },
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedCustomer.Id,
                        Kind = "customer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        Assert.Equal(2, payload.RequestedCount);
        Assert.Equal(1, payload.SucceededCount);

        var assetResult = Assert.Single(payload.Results, item => item.EntityId == deletedAsset.Id);
        var customerResult = Assert.Single(payload.Results, item => item.EntityId == deletedCustomer.Id);
        Assert.False(assetResult.Success);
        Assert.Contains("활성 자산", assetResult.Message);
        Assert.True(customerResult.Success);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id == deletedAsset.Id)
            .Select(asset => asset.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => customer.Id == deletedCustomer.Id)
            .Select(customer => customer.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task GetAll_IncludesRevisionForDeletedEntries()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "삭제 품목",
            NameMatchKey = "삭제품목",
            IsDeleted = true
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().FirstAsync(x => x.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetAll("item", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
        var deletedEntry = Assert.Single(payload);
        Assert.Equal(stored.Revision, deletedEntry.Revision);
    }

    private RecycleBinController CreateController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string category, string tenantKey, Guid fileId, string? fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, category, tenantKey, fileId.ToString("N"), fileName ?? "file.bin"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => fallbackContent ?? Array.Empty<byte>();

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
