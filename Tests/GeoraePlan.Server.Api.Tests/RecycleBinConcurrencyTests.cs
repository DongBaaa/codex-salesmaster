using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
    public async Task PurgeCustomer_RemovesDeletedContractsAndStorageFiles()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        const string storagePath = "contracts/usenet/customer-contract.pdf";
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "계약서 포함 삭제 거래처",
            NameMatchKey = "계약서포함삭제거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        });
        dbContext.CustomerContracts.Add(new CustomerContract
        {
            Id = contractId,
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "customer-contract.pdf",
            MimeType = "application/pdf",
            FileSize = 128,
            StoragePath = storagePath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = customerId,
                        Kind = "customer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
        Assert.False(await dbContext.CustomerContracts.IgnoreQueryFilters().AnyAsync(current => current.Id == contractId));
        Assert.Contains(storagePath, storage.DeletedPaths);
    }

    [Fact]
    public async Task PurgeCustomer_RecordsOwnerOffice_WhenCustomerResponsibleOfficeMissing()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = "담당지점 누락 삭제 거래처",
            NameMatchKey = "담당지점누락삭제거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = customerId,
                        Kind = "customer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);

        var purgeRecord = await dbContext.RecycleBinPurgeRecords
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Kind == "customer" && current.EntityId == customerId);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, purgeRecord.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, purgeRecord.OfficeCode);
    }

    [Fact]
    public async Task PurgeCustomer_ClearsHistoricalAssignmentHistoryCustomerReferences()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "설치이력 포함 삭제 거래처",
            NameMatchKey = "설치이력포함삭제거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = historyId,
            AssetId = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "설치이력 포함 삭제 거래처",
            InstallLocation = "과거 설치처",
            ItemName = "과거 설치 품목",
            ManagementNumber = "HISTORY-CUSTOMER-PURGE-001",
            IsCurrent = false,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = customerId,
                        Kind = "customer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
        var history = await dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == historyId);
        Assert.Null(history.CustomerId);
        Assert.Equal("설치이력 포함 삭제 거래처", history.CustomerName);
        Assert.Equal("과거 설치처", history.InstallLocation);
    }

    [Fact]
    public async Task PurgeCustomer_RejectsWhenHistoricalAssignmentHistoryOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "권한 밖 이력 연결 삭제 거래처",
            NameMatchKey = "권한밖이력연결삭제거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = historyId,
            AssetId = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            CustomerName = "권한 밖 이력 연결 삭제 거래처",
            InstallLocation = "권한 밖 설치처",
            ItemName = "권한 밖 설치 품목",
            ManagementNumber = "HISTORY-CUSTOMER-PURGE-HIDDEN-001",
            IsCurrent = false,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = customerId,
                        Kind = "customer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("임대이력", result.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Customers.IgnoreQueryFilters().AnyAsync(current => current.Id == customerId));
        Assert.Equal(
            customerId,
            await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .Where(current => current.Id == historyId)
                .Select(current => current.CustomerId)
                .SingleAsync());
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("purge")]
    public async Task CustomerMutation_DirectOutOfOfficeId_DoesNotRestoreOrPurgeRow(string action)
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateDeletedCustomerOutsideCurrentOffice();
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var storedBefore = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        var controller = CreateController(dbContext, currentUser);
        var request = new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = customer.Id,
                    Kind = "customer",
                    ExpectedRevision = storedBefore.Revision
                }
            ]
        };

        var response = string.Equals(action, "restore", StringComparison.Ordinal)
            ? await controller.Restore(request, CancellationToken.None)
            : await controller.Purge(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        var storedAfter = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        Assert.True(storedAfter.IsDeleted);
        Assert.Equal(storedBefore.Revision, storedAfter.Revision);
        Assert.False(await dbContext.RecycleBinPurgeRecords
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Kind == "customer" && current.EntityId == customer.Id));
    }

    [Fact]
    public async Task PurgeItem_RejectsRemainingInvoiceLineReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "전표 참조 삭제 품목",
            NameMatchKey = "전표참조삭제품목",
            IsDeleted = true
        };
        var customer = CreateScopedCustomer("품목 영구삭제 전표 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PURGE-ITEM-REF");
        invoice.Lines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m,
            OrderIndex = 1,
            IsDeleted = false
        });
        dbContext.Items.Add(item);
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = item.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("전표 라인", result.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.Items.IgnoreQueryFilters().AnyAsync(current => current.Id == item.Id));
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters().AnyAsync(line => line.ItemId == item.Id));
    }

    [Fact]
    public async Task PurgeItem_RejectsRemainingInventoryTransferLineReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "재고이동 참조 삭제 품목",
            NameMatchKey = "재고이동참조삭제품목",
            IsDeleted = true
        };
        var transfer = new InventoryTransfer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = "TR-PURGE-ITEM-REF",
            TransferDate = new DateOnly(2026, 6, 19),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = false,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    Unit = "EA",
                    Quantity = 1m,
                    IsDeleted = false
                }
            ]
        };
        dbContext.Items.Add(item);
        dbContext.InventoryTransfers.Add(transfer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = item.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("재고이동 라인", result.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.Items.IgnoreQueryFilters().AnyAsync(current => current.Id == item.Id));
        Assert.True(await dbContext.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(line => line.ItemId == item.Id));
    }

    [Fact]
    public async Task PurgeItem_RejectsRemainingRentalAssetReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 자산 참조 삭제 품목",
            NameMatchKey = "렌탈자산참조삭제품목",
            IsDeleted = true
        };
        var asset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "asset-purge-item-ref",
            ItemId = item.Id,
            ItemName = item.NameOriginal,
            ManagementNumber = "MN-PURGE-ITEM-REF",
            IsDeleted = false
        };
        dbContext.Items.Add(item);
        dbContext.RentalAssets.Add(asset);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = item.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("렌탈 자산", result.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.Items.IgnoreQueryFilters().AnyAsync(current => current.Id == item.Id));
        Assert.True(await dbContext.RentalAssets.IgnoreQueryFilters().AnyAsync(assetRow => assetRow.ItemId == item.Id));
    }

    [Fact]
    public async Task PurgeItem_AllowsRentalBillingTemplateRowIdMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 청구 템플릿 참조 삭제 품목",
            NameMatchKey = "렌탈청구템플릿참조삭제품목",
            IsDeleted = true
        };
        var profileId = Guid.NewGuid();
        var profile = new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"purge-item-template-ref-{profileId:N}",
            CustomerName = "품목 영구삭제 렌탈 청구 고객",
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    RowId = item.Id,
                    DisplayItemName = item.NameOriginal,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m
                }
            }),
            IsDeleted = false
        };
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = item.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);
        Assert.False(await dbContext.Items.IgnoreQueryFilters().AnyAsync(current => current.Id == item.Id));
        Assert.True(await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profile.Id));
    }

    [Fact]
    public async Task PurgeItem_RejectsRemainingRentalBillingTemplateReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 청구 템플릿 참조 영구삭제 차단 품목",
            NameMatchKey = "렌탈청구템플릿참조영구삭제차단품목",
            IsDeleted = true
        };
        var profileId = Guid.NewGuid();
        var profile = new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"purge-item-template-block-{profileId:N}",
            CustomerName = "품목 영구삭제 렌탈 청구 차단 고객",
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = item.Id,
                    DisplayItemName = item.NameOriginal,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m
                }
            }),
            IsDeleted = false
        };
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == item.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = item.Id,
                        Kind = "item",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("렌탈 청구프로필", result.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.Items.IgnoreQueryFilters().AnyAsync(current => current.Id == item.Id));
        Assert.True(await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profile.Id));
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
    public async Task RestoreCustomer_RestoresContractsDeletedBySameCustomerDeleteOnly()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("거래처 복구 계약서 거래처", OfficeCodeCatalog.Usenet);
        var cascadePrimaryContractId = Guid.NewGuid();
        var cascadeSecondaryContractId = Guid.NewGuid();
        var previouslyDeletedContractId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.AddRange(
            new CustomerContract
            {
                Id = cascadePrimaryContractId,
                CustomerId = customer.Id,
                ContractType = "대표 계약서",
                FileName = "primary-contract.pdf",
                MimeType = "application/pdf",
                FileHash = "RESTORE-CUSTOMER-PRIMARY",
                FileSize = 1,
                IsPrimary = true,
                IsDeleted = false
            },
            new CustomerContract
            {
                Id = cascadeSecondaryContractId,
                CustomerId = customer.Id,
                ContractType = "부속 계약서",
                FileName = "secondary-contract.pdf",
                MimeType = "application/pdf",
                FileHash = "RESTORE-CUSTOMER-SECONDARY",
                FileSize = 1,
                IsPrimary = false,
                IsDeleted = false
            },
            new CustomerContract
            {
                Id = previouslyDeletedContractId,
                CustomerId = customer.Id,
                ContractType = "이전 삭제 계약서",
                FileName = "previously-deleted-contract.pdf",
                MimeType = "application/pdf",
                FileHash = "RESTORE-CUSTOMER-PREVIOUS",
                FileSize = 1,
                IsPrimary = false,
                IsDeleted = false
            });
        await dbContext.SaveChangesAsync();

        var previousDeleted = await dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .SingleAsync(contract => contract.Id == previouslyDeletedContractId);
        previousDeleted.IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var deleteCustomer = await dbContext.Customers.IgnoreQueryFilters().SingleAsync(current => current.Id == customer.Id);
        var deleteController = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var deleteResponse = await deleteController.Delete(deleteCustomer.Id, deleteCustomer.Revision, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResponse);

        dbContext.ChangeTracker.Clear();
        var deletedCustomer = await dbContext.Customers.IgnoreQueryFilters().SingleAsync(current => current.Id == customer.Id);
        var deletedCascadePrimary = await dbContext.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == cascadePrimaryContractId);
        var deletedCascadeSecondary = await dbContext.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == cascadeSecondaryContractId);
        var stillPreviouslyDeleted = await dbContext.CustomerContracts.IgnoreQueryFilters().SingleAsync(contract => contract.Id == previouslyDeletedContractId);
        Assert.True(deletedCustomer.IsDeleted);
        Assert.True(deletedCascadePrimary.IsDeleted);
        Assert.True(deletedCascadeSecondary.IsDeleted);
        Assert.True(stillPreviouslyDeleted.IsDeleted);
        Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedCascadePrimary.UpdatedAtUtc);
        Assert.Equal(deletedCustomer.UpdatedAtUtc, deletedCascadeSecondary.UpdatedAtUtc);
        Assert.NotEqual(deletedCustomer.UpdatedAtUtc, stillPreviouslyDeleted.UpdatedAtUtc);

        var restoreController = CreateController(dbContext, currentUser);
        var restoreResponse = await restoreController.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = customer.Id,
                        Kind = "customer",
                        ExpectedRevision = deletedCustomer.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(restoreResponse.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.CustomerContracts.IgnoreQueryFilters()
            .Where(contract => contract.Id == cascadePrimaryContractId)
            .Select(contract => contract.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.CustomerContracts.IgnoreQueryFilters()
            .Where(contract => contract.Id == cascadeSecondaryContractId)
            .Select(contract => contract.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.CustomerContracts.IgnoreQueryFilters()
            .Where(contract => contract.Id == previouslyDeletedContractId)
            .Select(contract => contract.IsDeleted)
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

    [Fact]
    public async Task GetAll_DoesNotReturnDeletedPayment_WhenParentInvoiceIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = true,
                ShareItems = false,
                ShareInvoices = false,
                SharePayments = true,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "RECYCLE-HIDDEN-INVOICE-PAYMENT-CUSTOMER",
                NameMatchKey = "RECYCLEHIDDENINVOICEPAYMENTCUSTOMER",
                TradeType = "매출"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "RECYCLE-HIDDEN-INVOICE",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 24),
                TotalAmount = 40000m
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 20000m,
                Note = "hidden invoice deleted payment",
                IsDeleted = true
            };
            seedDb.Customers.Add(customer);
            seedDb.Invoices.Add(invoice);
            seedDb.Payments.Add(payment);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-recycle-payment-only-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreateController(scopedDb, scopedUser);

            var response = await controller.GetAll("payment", null, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);

            Assert.Empty(payload);
        }
    }

    [Fact]
    public async Task GetAll_Transaction_DoesNotExposeHiddenCustomerName_WhenCustomerIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = false,
                ShareItems = false,
                ShareInvoices = false,
                SharePayments = true,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "RECYCLE-HIDDEN-TRANSACTION-CUSTOMER",
                NameMatchKey = "RECYCLEHIDDENTRANSACTIONCUSTOMER",
                TradeType = "매출"
            };
            var transaction = new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 24),
                TransactionKind = "수금",
                ReceiptTotal = 12345m,
                BankReceipt = 12345m,
                Note = "hidden customer deleted transaction",
                IsDeleted = true
            };
            seedDb.Customers.Add(customer);
            seedDb.Transactions.Add(transaction);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-recycle-transaction-only-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreateController(scopedDb, scopedUser);

            var response = await controller.GetAll("transaction", null, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
            var entry = Assert.Single(payload);

            Assert.Equal(transaction.Id, entry.EntityId);
            Assert.DoesNotContain(customer.NameOriginal, entry.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Subtitle, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAll_Invoice_DoesNotExposeHiddenCustomerName_WhenCustomerIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = false,
                ShareItems = false,
                ShareInvoices = true,
                SharePayments = false,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "RECYCLE-HIDDEN-INVOICE-CUSTOMER",
                NameMatchKey = "RECYCLEHIDDENINVOICECUSTOMER",
                TradeType = "매출"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "RECYCLE-HIDDEN-CUSTOMER-INVOICE",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 24),
                TotalAmount = 44000m,
                IsDeleted = true
            };
            seedDb.Customers.Add(customer);
            seedDb.Invoices.Add(invoice);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-recycle-invoice-only-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreateController(scopedDb, scopedUser);

            var response = await controller.GetAll("invoice", null, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
            var entry = Assert.Single(payload);

            Assert.Equal(invoice.Id, entry.EntityId);
            Assert.DoesNotContain(customer.NameOriginal, entry.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Subtitle, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAll_Payment_DoesNotExposeHiddenCustomerName_WhenCustomerIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = false,
                ShareItems = false,
                ShareInvoices = true,
                SharePayments = true,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "RECYCLE-HIDDEN-PAYMENT-CUSTOMER",
                NameMatchKey = "RECYCLEHIDDENPAYMENTCUSTOMER",
                TradeType = "매출"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "RECYCLE-HIDDEN-PAYMENT-INVOICE",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 24),
                TotalAmount = 66000m
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 33000m,
                Note = "hidden customer deleted payment",
                IsDeleted = true
            };
            seedDb.Customers.Add(customer);
            seedDb.Invoices.Add(invoice);
            seedDb.Payments.Add(payment);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-recycle-invoice-payment-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreateController(scopedDb, scopedUser);

            var response = await controller.GetAll("payment", null, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
            var entry = Assert.Single(payload);

            Assert.Equal(payment.Id, entry.EntityId);
            Assert.DoesNotContain(customer.NameOriginal, entry.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Subtitle, StringComparison.Ordinal);
            Assert.DoesNotContain(customer.NameOriginal, entry.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAll_IncludesDeletedCustomerCategoryForAdmin()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var category = new CustomerCategory
        {
            Id = Guid.NewGuid(),
            Name = "삭제 고객분류",
            IsDeleted = true
        };
        dbContext.CustomerCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CustomerCategories.IgnoreQueryFilters().FirstAsync(current => current.Id == category.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetAll("customer-category", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
        var entry = Assert.Single(payload);
        Assert.Equal(stored.Id, entry.EntityId);
        Assert.Equal("customer-category", entry.Kind);
        Assert.Equal("고객분류", entry.KindText);
        Assert.Equal(stored.Revision, entry.Revision);
    }

    [Fact]
    public async Task GetAll_RentalBillingLog_UsesLogScopeAndDoesNotLeakHiddenProfile()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenProfileId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = hiddenProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "HIDDEN-PROFILE-001",
            CustomerName = "숨김 렌탈 거래처",
            InstallSiteName = "숨김 설치처",
            ItemName = "숨김 품목",
            IsDeleted = true
        });
        dbContext.RentalBillingLogs.Add(new RentalBillingLog
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
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.GetAll("rental-billing-log", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
        var entry = Assert.Single(payload);
        Assert.Equal(logId, entry.EntityId);
        Assert.Equal("청구로그 2026-06", entry.Title);
        Assert.DoesNotContain("숨김", entry.Title);
        Assert.DoesNotContain("숨김", entry.Subtitle);
        Assert.DoesNotContain("숨김", entry.Detail);
    }

    [Fact]
    public async Task RestoreContract_RejectsActiveOutOfScopeContractInsteadOfReportingAlreadyActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenCustomer = CreateScopedCustomer("hidden contract customer", OfficeCodeCatalog.Yeonsu);
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            ContractType = "scope guard contract",
            IsDeleted = false
        };
        dbContext.Customers.Add(hiddenCustomer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = contract.Id,
                        Kind = "contract"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task RestoreInvoice_RejectsActiveOutOfScopeInvoiceInsteadOfReportingAlreadyActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenCustomer = CreateScopedCustomer("hidden invoice customer", OfficeCodeCatalog.Yeonsu);
        var invoice = CreateScopedInvoice(hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu, "INV-ACTIVE-HIDDEN-SCOPE");
        dbContext.Customers.Add(hiddenCustomer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoice.Id,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task RestoreInvoice_RejectsLinkedDeletedCustomerOutsideCustomerWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateDeletedCustomerOutsideCurrentOffice();
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-SCOPE-RESTORE-001",
            VoucherType = VoucherType.Sales,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoice.Id,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 거래처", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(current => current.Id == invoice.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreInvoice_RejectsVersionGroupOutsideInvoiceWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var visibleCustomer = CreateScopedCustomer("전표 묶음 복원 거래처", OfficeCodeCatalog.Usenet);
        var hiddenCustomer = CreateScopedCustomer("권한 외 전표 묶음 거래처", OfficeCodeCatalog.Yeonsu);
        var versionGroupId = Guid.NewGuid();
        var visibleInvoice = CreateScopedInvoice(visibleCustomer.Id, OfficeCodeCatalog.Usenet, "INV-GROUP-SCOPE-RESTORE");
        visibleInvoice.IsDeleted = true;
        visibleInvoice.VersionGroupId = versionGroupId;
        visibleInvoice.VersionNumber = 1;
        visibleInvoice.IsLatestVersion = false;
        var hiddenInvoice = CreateScopedInvoice(hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu, "INV-GROUP-SCOPE-HIDDEN");
        hiddenInvoice.IsDeleted = true;
        hiddenInvoice.VersionGroupId = versionGroupId;
        hiddenInvoice.VersionNumber = 2;
        hiddenInvoice.IsLatestVersion = true;
        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = visibleInvoice.Id,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("전표 묶음", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        var invoiceStates = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(current => current.Id == visibleInvoice.Id || current.Id == hiddenInvoice.Id)
            .ToDictionaryAsync(current => current.Id, current => current.IsDeleted);
        Assert.True(invoiceStates[visibleInvoice.Id]);
        Assert.True(invoiceStates[hiddenInvoice.Id]);
    }

    [Fact]
    public async Task RestoreInvoice_RejectsOutOfScopeItemLine()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Restore hidden item customer",
            NameMatchKey = "RESTOREHIDDENITEMCUSTOMER",
            TradeType = "Sales"
        };
        var hiddenItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Hidden restore invoice item",
            NameMatchKey = "HIDDENRESTOREINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = "RESTORE-HIDDEN-ITEM-INVOICE",
            InvoiceDate = new DateOnly(2026, 6, 19),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            IsDeleted = true,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = hiddenItem.Id,
                    ItemNameOriginal = hiddenItem.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    OrderIndex = 1,
                    IsDeleted = true
                }
            ]
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(hiddenItem);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == invoice.Id);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoice.Id,
                        Kind = "invoice",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Contains("Referenced invoice line item is outside the readable office scope", result.Message, StringComparison.Ordinal);
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(current => current.Id == invoice.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(current => current.InvoiceId == invoice.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task PurgeInvoice_RejectsVersionGroupOutsideInvoiceWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var visibleCustomer = CreateScopedCustomer("전표 묶음 삭제 거래처", OfficeCodeCatalog.Usenet);
        var hiddenCustomer = CreateScopedCustomer("권한 외 전표 묶음 삭제 거래처", OfficeCodeCatalog.Yeonsu);
        var versionGroupId = Guid.NewGuid();
        var visibleInvoice = CreateScopedInvoice(visibleCustomer.Id, OfficeCodeCatalog.Usenet, "INV-GROUP-SCOPE-PURGE");
        visibleInvoice.IsDeleted = true;
        visibleInvoice.VersionGroupId = versionGroupId;
        visibleInvoice.VersionNumber = 1;
        visibleInvoice.IsLatestVersion = false;
        var hiddenInvoice = CreateScopedInvoice(hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu, "INV-GROUP-SCOPE-PURGE-HIDDEN");
        hiddenInvoice.IsDeleted = true;
        hiddenInvoice.VersionGroupId = versionGroupId;
        hiddenInvoice.VersionNumber = 2;
        hiddenInvoice.IsLatestVersion = true;
        dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = visibleInvoice.Id,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("전표 묶음", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == visibleInvoice.Id));
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenInvoice.Id));
    }

    [Fact]
    public async Task PurgeInvoice_RemovesDeletedLinkedPaymentsAndAttachmentStorage()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        const string paymentAttachmentPath = "payments/test/purge-invoice-linked-payment.bin";
        var customer = CreateScopedCustomer("전표 영구삭제 연결 수금 거래처", OfficeCodeCatalog.Usenet);
        customer.Id = customerId;
        var invoice = CreateScopedInvoice(customerId, OfficeCodeCatalog.Usenet, "INV-PURGE-DELETED-PAYMENT");
        invoice.Id = invoiceId;
        invoice.VersionGroupId = invoiceId;
        invoice.VersionNumber = 1;
        invoice.IsLatestVersion = true;
        invoice.IsDeleted = true;
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 1000m,
            IsDeleted = true
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = paymentId,
            FileName = "purge-invoice-linked-payment.bin",
            StoragePath = paymentAttachmentPath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var fileStorage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, fileStorage);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoiceId,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == invoiceId));
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentId));
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters().AnyAsync(current => current.PaymentId == paymentId));
        Assert.Contains(paymentAttachmentPath, fileStorage.DeletedPaths);
        Assert.True(await dbContext.RecycleBinPurgeRecords.AnyAsync(current => current.Kind == "invoice" && current.EntityId == invoiceId));
        Assert.True(await dbContext.RecycleBinPurgeRecords.AnyAsync(current => current.Kind == "payment" && current.EntityId == paymentId));
    }

    [Fact]
    public async Task PurgeInvoice_RemovesStaleLedgerEntries()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var customer = CreateScopedCustomer("전표 ledger purge 거래처", OfficeCodeCatalog.Usenet);
        customer.Id = customerId;
        var invoice = CreateScopedInvoice(customerId, OfficeCodeCatalog.Usenet, "INV-PURGE-LEDGER-CLEANUP");
        invoice.Id = invoiceId;
        invoice.VersionGroupId = invoiceId;
        invoice.VersionNumber = 1;
        invoice.IsLatestVersion = true;
        invoice.IsDeleted = true;
        invoice.SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
        invoice.VoucherType = VoucherType.Sales;
        invoice.Lines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemId = itemId,
            ItemNameOriginal = "전표 ledger purge 품목",
            Unit = "개",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m,
            ItemTrackingType = ItemTrackingTypes.Stock,
            OrderIndex = 1,
            IsDeleted = true
        });
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "전표 ledger purge 품목",
            NameMatchKey = "전표ledgerpurge품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        dbContext.Invoices.Add(invoice);
        dbContext.InventoryLedgerEntries.Add(new InventoryLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            SourceType = "Invoice:Sales",
            SourceDocumentId = invoiceId,
            SourceLineId = lineId,
            QuantityDelta = -1m,
            OccurredDate = invoice.InvoiceDate,
            Note = "stale invoice ledger"
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoiceId,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(current => current.Id == invoiceId));
        Assert.False(await dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == invoiceId));
    }

    [Fact]
    public async Task RestorePayment_RejectsLinkedDeletedCustomerOutsideCustomerWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateDeletedCustomerOutsideCurrentOffice();
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-SCOPE-RESTORE-002",
            VoucherType = VoucherType.Sales,
            IsDeleted = false
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            Amount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 거래처", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Payments.IgnoreQueryFilters()
            .Where(current => current.Id == payment.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestorePayment_RejectsActiveOutOfScopePaymentInsteadOfReportingAlreadyActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenCustomer = CreateScopedCustomer("hidden payment customer", OfficeCodeCatalog.Yeonsu);
        var hiddenInvoice = CreateScopedInvoice(hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu, "INV-PAYMENT-ACTIVE-HIDDEN");
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = hiddenInvoice.Id,
            Amount = 1000m,
            IsDeleted = false
        };
        dbContext.Customers.Add(hiddenCustomer);
        dbContext.Invoices.Add(hiddenInvoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task RestoreTransaction_RejectsLinkedDeletedCustomerOutsideCustomerWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateDeletedCustomerOutsideCurrentOffice();
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 17),
            TransactionKind = "수금",
            ReceiptTotal = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transaction.Id,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 거래처", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(current => current.Id == transaction.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreTransaction_RejectsActiveOutOfScopeTransactionInsteadOfReportingAlreadyActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenCustomer = CreateScopedCustomer("hidden transaction customer", OfficeCodeCatalog.Yeonsu);
        var transaction = CreateDeletedTransaction(Guid.NewGuid(), hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu);
        transaction.IsDeleted = false;
        dbContext.Customers.Add(hiddenCustomer);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transaction.Id,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task RestoreTransaction_RestoresLinkedInvoiceCustomerAndRelinksCustomerId()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var staleCustomer = CreateScopedCustomer("restore transaction stale customer", OfficeCodeCatalog.Usenet);
        var invoiceCustomer = CreateScopedCustomer("restore transaction invoice customer", OfficeCodeCatalog.Usenet);
        invoiceCustomer.IsDeleted = true;
        var invoice = CreateScopedInvoice(invoiceCustomer.Id, OfficeCodeCatalog.Usenet, "INV-TX-RESTORE-CUSTOMER-RELINK");
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = staleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.AddRange(staleCustomer, invoiceCustomer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transaction.Id,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == invoiceCustomer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        var restoredTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(current => current.Id == transaction.Id);
        Assert.False(restoredTransaction.IsDeleted);
        Assert.Equal(invoiceCustomer.Id, restoredTransaction.CustomerId);
        Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
    }

    [Fact]
    public async Task RestorePayment_RelinksLinkedTransactionCustomerToInvoiceCustomer()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var staleCustomer = CreateScopedCustomer("restore payment stale transaction customer", OfficeCodeCatalog.Usenet);
        var invoiceCustomer = CreateScopedCustomer("restore payment invoice customer", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(invoiceCustomer.Id, OfficeCodeCatalog.Usenet, "INV-PAY-RESTORE-CUSTOMER-RELINK");
        var paymentId = Guid.NewGuid();
        var payment = new Payment
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 1000m,
            IsDeleted = true
        };
        var linkedTransaction = new TransactionRecord
        {
            Id = paymentId,
            CustomerId = staleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.AddRange(staleCustomer, invoiceCustomer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.Transactions.Add(linkedTransaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        var restoredPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(current => current.Id == payment.Id);
        var restoredTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(current => current.Id == linkedTransaction.Id);
        Assert.False(restoredPayment.IsDeleted);
        Assert.False(restoredTransaction.IsDeleted);
        Assert.Equal(invoiceCustomer.Id, restoredTransaction.CustomerId);
        Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
    }

    [Fact]
    public async Task RestoreTransaction_AlignsRentalLinkToLinkedInvoice()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("restore transaction rental customer", OfficeCodeCatalog.Usenet);
        var invoiceProfileId = Guid.NewGuid();
        var wrongProfileId = Guid.NewGuid();
        var invoiceRunId = Guid.NewGuid();
        var wrongRunId = Guid.NewGuid();
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-RESTORE-RENTAL-RELINK");
        invoice.LinkedRentalBillingProfileId = invoiceProfileId;
        invoice.LinkedRentalBillingRunId = invoiceRunId;
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 23),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            LinkedRentalBillingProfileId = wrongProfileId,
            LinkedRentalBillingRunId = wrongRunId,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(CreateRentalProfile(invoiceProfileId, customer.Id, "restore transaction invoice profile", invoiceRunId));
        dbContext.RentalBillingProfiles.Add(CreateRentalProfile(wrongProfileId, customer.Id, "restore transaction wrong profile", wrongRunId));
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transaction.Id,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        var restoredTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == transaction.Id);
        Assert.False(restoredTransaction.IsDeleted);
        Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
        Assert.Equal(invoiceProfileId, restoredTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(invoiceRunId, restoredTransaction.LinkedRentalBillingRunId);
    }

    [Fact]
    public async Task RestoreTransaction_RejectsWhenLinkedInvoiceRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("restore transaction hidden rental profile customer", OfficeCodeCatalog.Usenet);
        var hiddenProfileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-RESTORE-HIDDEN-RENTAL");
        invoice.LinkedRentalBillingProfileId = hiddenProfileId;
        invoice.LinkedRentalBillingRunId = runId;
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 23),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            IsDeleted = true
        };
        var hiddenProfile = CreateRentalProfile(hiddenProfileId, customer.Id, "restore transaction hidden profile", runId);
        hiddenProfile.OfficeCode = OfficeCodeCatalog.Shared;
        hiddenProfile.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        hiddenProfile.SettledAmount = 0m;
        hiddenProfile.OutstandingAmount = 100_000m;
        hiddenProfile.SettlementStatus = "보존";
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(hiddenProfile);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transaction.Id,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("렌탈 청구프로필", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        var storedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == transaction.Id);
        Assert.True(storedTransaction.IsDeleted);
        Assert.Null(storedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(storedTransaction.LinkedRentalBillingRunId);
        var storedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == hiddenProfileId);
        Assert.Equal("보존", storedProfile.SettlementStatus);
        Assert.Equal(0m, storedProfile.SettledAmount);
    }

    [Fact]
    public async Task RestorePayment_AlignsLinkedTransactionRentalLinkToInvoice()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("restore payment rental customer", OfficeCodeCatalog.Usenet);
        var invoiceProfileId = Guid.NewGuid();
        var wrongProfileId = Guid.NewGuid();
        var invoiceRunId = Guid.NewGuid();
        var wrongRunId = Guid.NewGuid();
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAY-RESTORE-RENTAL-RELINK");
        invoice.LinkedRentalBillingProfileId = invoiceProfileId;
        invoice.LinkedRentalBillingRunId = invoiceRunId;
        var paymentId = Guid.NewGuid();
        var payment = new Payment
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 23),
            Amount = 40_000m,
            IsDeleted = true
        };
        var linkedTransaction = new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 23),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            LinkedRentalBillingProfileId = wrongProfileId,
            LinkedRentalBillingRunId = wrongRunId,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(CreateRentalProfile(invoiceProfileId, customer.Id, "restore payment invoice profile", invoiceRunId));
        dbContext.RentalBillingProfiles.Add(CreateRentalProfile(wrongProfileId, customer.Id, "restore payment wrong profile", wrongRunId));
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.Transactions.Add(linkedTransaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        var restoredPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == payment.Id);
        var restoredTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == linkedTransaction.Id);
        Assert.False(restoredPayment.IsDeleted);
        Assert.False(restoredTransaction.IsDeleted);
        Assert.Equal(invoice.Id, restoredTransaction.LinkedInvoiceId);
        Assert.Equal(invoiceProfileId, restoredTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(invoiceRunId, restoredTransaction.LinkedRentalBillingRunId);
    }

    [Fact]
    public async Task RestorePayment_RejectsWhenInvoiceRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("restore payment hidden rental profile customer", OfficeCodeCatalog.Usenet);
        var hiddenProfileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAY-RESTORE-HIDDEN-RENTAL");
        invoice.LinkedRentalBillingProfileId = hiddenProfileId;
        invoice.LinkedRentalBillingRunId = runId;
        var paymentId = Guid.NewGuid();
        var payment = new Payment
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 23),
            Amount = 40_000m,
            IsDeleted = true
        };
        var linkedTransaction = new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 23),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            IsDeleted = true
        };
        var hiddenProfile = CreateRentalProfile(hiddenProfileId, customer.Id, "restore payment hidden profile", runId);
        hiddenProfile.OfficeCode = OfficeCodeCatalog.Shared;
        hiddenProfile.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        hiddenProfile.SettledAmount = 0m;
        hiddenProfile.OutstandingAmount = 100_000m;
        hiddenProfile.SettlementStatus = "보존";
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(hiddenProfile);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.Transactions.Add(linkedTransaction);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("렌탈 청구프로필", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Payments.IgnoreQueryFilters()
            .Where(current => current.Id == paymentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        var storedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == linkedTransaction.Id);
        Assert.True(storedTransaction.IsDeleted);
        Assert.Null(storedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(storedTransaction.LinkedRentalBillingRunId);
        var storedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == hiddenProfileId);
        Assert.Equal("보존", storedProfile.SettlementStatus);
        Assert.Equal(0m, storedProfile.SettledAmount);
    }

    [Fact]
    public async Task RestoreInvoice_RejectsWhenRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("restore invoice hidden rental profile customer", OfficeCodeCatalog.Usenet);
        var hiddenProfileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-RESTORE-HIDDEN-RENTAL");
        invoice.LinkedRentalBillingProfileId = hiddenProfileId;
        invoice.LinkedRentalBillingRunId = runId;
        invoice.IsDeleted = true;
        var hiddenProfile = CreateRentalProfile(hiddenProfileId, customer.Id, "restore invoice hidden profile", runId);
        hiddenProfile.OfficeCode = OfficeCodeCatalog.Shared;
        hiddenProfile.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        hiddenProfile.SettledAmount = 0m;
        hiddenProfile.OutstandingAmount = 100_000m;
        hiddenProfile.SettlementStatus = "보존";
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(hiddenProfile);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoice.Id,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("렌탈 청구프로필", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(current => current.Id == invoice.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        var storedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == hiddenProfileId);
        Assert.Equal("보존", storedProfile.SettlementStatus);
        Assert.Equal(0m, storedProfile.SettledAmount);
    }

    [Fact]
    public async Task RestoreTransaction_RestoresDeletedTransactionAttachments()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("첨부 거래내역 복원 거래처", OfficeCodeCatalog.Usenet);
        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet);
        var deletedAttachment = new TransactionAttachment
        {
            Id = attachmentId,
            TransactionId = transactionId,
            AttachmentType = "증빙",
            FileName = "restore-transaction-attachment.pdf",
            MimeType = "application/pdf",
            FileSize = 16,
            FileHash = "restore-transaction-attachment-hash",
            StoragePath = "storage/restore-transaction-attachment.pdf",
            FileContent = [0x25, 0x50, 0x44, 0x46],
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Transactions.Add(transaction);
        dbContext.TransactionAttachments.Add(deletedAttachment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(current => current.Id == transactionId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(current => current.Id == attachmentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task PurgeCompanyProfile_RecordsProfileOfficeScope()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profileId = Guid.NewGuid();
        dbContext.CompanyProfiles.Add(new CompanyProfile
        {
            Id = profileId,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ProfileName = "ITWORLD 삭제 회사설정",
            TradeName = "ITWORLD",
            IsDeleted = true,
            IsActive = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = profileId,
                        Kind = "company-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);

        var purgeRecord = await dbContext.RecycleBinPurgeRecords
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Kind == "company-profile" && current.EntityId == profileId);
        Assert.Equal(TenantScopeCatalog.Itworld, purgeRecord.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Itworld, purgeRecord.OfficeCode);
    }

    [Fact]
    public async Task GetAllCompanyProfile_ForOfficeAdmin_DoesNotExposeOutOfScopeDeletedProfiles()
    {
        var currentUser = CreateOfficeOnlyAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var visibleProfileId = Guid.NewGuid();
        var hiddenProfileId = Guid.NewGuid();
        dbContext.CompanyProfiles.AddRange(
            new CompanyProfile
            {
                Id = visibleProfileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "USENET 삭제 회사설정",
                TradeName = "USENET",
                IsDeleted = true,
                IsActive = false
            },
            new CompanyProfile
            {
                Id = hiddenProfileId,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ProfileName = "ITWORLD 삭제 회사설정",
                TradeName = "ITWORLD",
                IsDeleted = true,
                IsActive = false
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.GetAll("company-profile", null, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<List<RecycleBinEntryDto>>(ok.Value);
        var entry = Assert.Single(payload);
        Assert.Equal(visibleProfileId, entry.EntityId);
        Assert.DoesNotContain(payload, current => current.EntityId == hiddenProfileId);
    }

    [Fact]
    public async Task RestoreCompanyProfile_ForOfficeAdmin_RejectsOutOfScopeProfile()
    {
        var currentUser = CreateOfficeOnlyAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenProfileId = Guid.NewGuid();
        dbContext.CompanyProfiles.Add(new CompanyProfile
        {
            Id = hiddenProfileId,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ProfileName = "ITWORLD 삭제 회사설정",
            TradeName = "ITWORLD",
            IsDeleted = true,
            IsActive = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = hiddenProfileId,
                        Kind = "company-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(current => current.Id == hiddenProfileId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task PurgeCompanyProfile_ForOfficeAdmin_RejectsOutOfScopeProfile()
    {
        var currentUser = CreateOfficeOnlyAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var hiddenProfileId = Guid.NewGuid();
        dbContext.CompanyProfiles.Add(new CompanyProfile
        {
            Id = hiddenProfileId,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ProfileName = "ITWORLD 삭제 회사설정",
            TradeName = "ITWORLD",
            IsDeleted = true,
            IsActive = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = hiddenProfileId,
                        Kind = "company-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == hiddenProfileId && current.IsDeleted));
        Assert.False(await dbContext.RecycleBinPurgeRecords
            .AnyAsync(current => current.Kind == "company-profile" && current.EntityId == hiddenProfileId));
    }

    [Fact]
    public async Task RestoreRentalAsset_RejectsHiddenActiveIdentifierConflictWithoutLeakingDetails()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var targetAssetId = Guid.NewGuid();
        var hiddenAssetId = Guid.NewGuid();
        dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = targetAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "RESTORE-ASSET-TARGET-001",
                ManagementId = "RESTORE-HIDDEN-CONFLICT-ID",
                ManagementNumber = "RESTORE-HIDDEN-CONFLICT-MN",
                ItemName = "복원 대상 자산",
                IsDeleted = true
            },
            new RentalAsset
            {
                Id = hiddenAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
                AssetKey = "SECRET-HIDDEN-ASSET-KEY",
                ManagementId = "RESTORE-HIDDEN-CONFLICT-ID",
                ManagementNumber = "RESTORE-HIDDEN-CONFLICT-MN",
                ItemName = "권한 외 비밀 자산",
                IsDeleted = false
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = targetAssetId,
                        Kind = "rental-asset"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("같은 렌탈 자산 식별값", item.Message);
        Assert.DoesNotContain("권한 외 비밀 자산", item.Message);
        Assert.DoesNotContain("SECRET-HIDDEN-ASSET-KEY", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.Id == targetAssetId)
            .Select(asset => asset.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task PurgeRentalBillingProfile_RejectsWhenLinkedAssetOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profileId = Guid.NewGuid();
        var profile = new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-SCOPE-001",
            CustomerName = "영구삭제 범위 프로필",
            IsDeleted = true,
            IsActive = false
        };
        var outOfScopeAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            AssetKey = "PURGE-PROFILE-SCOPE-ASSET-001",
            BillingProfileId = profileId,
            ManagementId = "PURGE-PROFILE-SCOPE-ASSET-001",
            ManagementNumber = "PURGE-PROFILE-SCOPE-ASSET-001",
            ItemName = "권한 외 연결 자산",
            AssetStatus = "설치",
            BillingEligibilityStatus = "청구가능",
            BillingExclusionReason = "보존",
            IsDeleted = false
        };
        dbContext.RentalBillingProfiles.Add(profile);
        dbContext.RentalAssets.Add(outOfScopeAsset);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = profileId,
                        Kind = "rental-billing-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 렌탈 자산", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == profileId));
        var storedAsset = await dbContext.RentalAssets.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == outOfScopeAsset.Id);
        Assert.Equal(profileId, storedAsset.BillingProfileId);
        Assert.Equal("청구가능", storedAsset.BillingEligibilityStatus);
        Assert.Equal("보존", storedAsset.BillingExclusionReason);
    }

    [Fact]
    public async Task PurgeRentalBillingProfile_RejectsWhenLinkedBillingLogOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profileId = Guid.NewGuid();
        var hiddenLogId = Guid.NewGuid();
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-HIDDEN-LOG-001",
            CustomerName = "영구삭제 숨김 로그 프로필",
            IsDeleted = true,
            IsActive = false
        });
        dbContext.RentalBillingLogs.Add(new RentalBillingLog
        {
            Id = hiddenLogId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            BillingProfileId = profileId,
            BillingYearMonth = "2026-06",
            BilledAmount = 100m,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = profileId,
                        Kind = "rental-billing-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("렌탈 청구로그", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
        Assert.True(await dbContext.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenLogId));
    }

    [Fact]
    public async Task PurgeRentalBillingProfile_RejectsWhenAssignmentHistoryOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hiddenHistoryId = Guid.NewGuid();
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-HIDDEN-HISTORY-001",
            CustomerName = "영구삭제 숨김 이력 프로필",
            IsDeleted = true,
            IsActive = false
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            AssetKey = "PURGE-PROFILE-HIDDEN-HISTORY-ASSET-001",
            ManagementId = "PURGE-PROFILE-HIDDEN-HISTORY-ASSET-001",
            ManagementNumber = "PURGE-PROFILE-HIDDEN-HISTORY-ASSET-001",
            ItemName = "권한 외 이력 자산",
            IsDeleted = false
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = hiddenHistoryId,
            AssetId = assetId,
            BillingProfileId = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            BillingProfileDisplay = "권한 외 이력 프로필",
            ItemName = "권한 외 이력 자산",
            ManagementNumber = "PURGE-PROFILE-HIDDEN-HISTORY-ASSET-001",
            IsCurrent = false,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = profileId,
                        Kind = "rental-billing-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("임대이력", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
        Assert.Equal(
            profileId,
            await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .Where(current => current.Id == hiddenHistoryId)
                .Select(current => current.BillingProfileId)
                .SingleAsync());
    }

    [Fact]
    public async Task PurgeRentalAsset_RejectsWhenReferencedProfileOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var assetId = Guid.NewGuid();
        var asset = new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "PURGE-ASSET-SCOPE-001",
            ManagementId = "PURGE-ASSET-SCOPE-001",
            ManagementNumber = "PURGE-ASSET-SCOPE-001",
            ItemName = "영구삭제 범위 자산",
            IsDeleted = true
        };
        var outOfScopeProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "PURGE-ASSET-SCOPE-PROFILE-001",
            CustomerName = "권한 외 참조 프로필",
            BillingTemplateJson = BuildBillingTemplateJson(assetId),
            IsDeleted = false,
            IsActive = true
        };
        dbContext.RentalAssets.Add(asset);
        dbContext.RentalBillingProfiles.Add(outOfScopeProfile);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = assetId,
                        Kind = "rental-asset"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 렌탈 청구프로필", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalAssets.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == assetId));
        Assert.Equal(
            BuildBillingTemplateJson(assetId),
            await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(current => current.Id == outOfScopeProfile.Id)
                .Select(current => current.BillingTemplateJson)
                .SingleAsync());
    }

    [Fact]
    public async Task PurgeRentalAsset_RejectsWhenAssignmentHistoryOutsideRentalWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var assetId = Guid.NewGuid();
        var hiddenHistoryId = Guid.NewGuid();
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "PURGE-ASSET-HIDDEN-HISTORY-001",
            ManagementId = "PURGE-ASSET-HIDDEN-HISTORY-001",
            ManagementNumber = "PURGE-ASSET-HIDDEN-HISTORY-001",
            ItemName = "영구삭제 숨김 이력 자산",
            IsDeleted = true
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = hiddenHistoryId,
            AssetId = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ItemName = "권한 외 임대이력",
            ManagementNumber = "PURGE-ASSET-HIDDEN-HISTORY-001",
            IsCurrent = false,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = assetId,
                        Kind = "rental-asset"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("임대이력", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
        Assert.True(await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.Id == hiddenHistoryId));
    }

    [Fact]
    public async Task PurgeRentalBillingProfile_ClearsAssignmentHistoryProfileReferences()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PURGE-PROFILE-HISTORY-001",
            CustomerName = "영구삭제 이력 청구프로필",
            InstallSiteName = "테스트 설치처",
            IsDeleted = true,
            IsActive = false
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "PURGE-PROFILE-HISTORY-ASSET-001",
            ManagementId = "PURGE-PROFILE-HISTORY-ASSET-001",
            ManagementNumber = "PURGE-PROFILE-HISTORY-ASSET-001",
            ItemName = "프로필 영구삭제 이력 자산",
            AssetStatus = "설치",
            BillingProfileId = profileId,
            BillingEligibilityStatus = "청구가능",
            IsDeleted = false
        });
        dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileDisplay = "영구삭제 이력 청구프로필",
                ItemName = "프로필 영구삭제 이력 자산",
                ManagementNumber = "PURGE-PROFILE-HISTORY-ASSET-001",
                IsCurrent = true,
                IsDeleted = false
            },
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileDisplay = "영구삭제 과거 청구프로필",
                ItemName = "프로필 영구삭제 과거 이력 자산",
                ManagementNumber = "PURGE-PROFILE-HISTORY-ASSET-001",
                IsCurrent = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = profileId,
                        Kind = "rental-billing-profile"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(current => current.Id == profileId));
        Assert.Null(await dbContext.RentalAssets.IgnoreQueryFilters()
            .Where(current => current.Id == assetId)
            .Select(current => current.BillingProfileId)
            .SingleAsync());
        Assert.Equal(
            0,
            await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .CountAsync(current => current.BillingProfileId == profileId));
        Assert.Equal(
            2,
            await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .CountAsync(current => current.AssetId == assetId));
    }

    [Fact]
    public async Task PurgeRentalAsset_RemovesAssignmentHistories()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var assetId = Guid.NewGuid();
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "PURGE-ASSET-HISTORY-001",
            ManagementId = "PURGE-ASSET-HISTORY-001",
            ManagementNumber = "PURGE-ASSET-HISTORY-001",
            ItemName = "영구삭제 이력 자산",
            IsDeleted = true
        });
        dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ItemName = "영구삭제 이력 자산",
                ManagementNumber = "PURGE-ASSET-HISTORY-001",
                IsCurrent = true,
                IsDeleted = false
            },
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ItemName = "영구삭제 과거 이력 자산",
                ManagementNumber = "PURGE-ASSET-HISTORY-001",
                IsCurrent = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = assetId,
                        Kind = "rental-asset"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.RentalAssets.IgnoreQueryFilters().AnyAsync(current => current.Id == assetId));
        Assert.False(await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(current => current.AssetId == assetId));
        Assert.True(await dbContext.RecycleBinPurgeRecords.AnyAsync(current => current.Kind == "rental-asset" && current.EntityId == assetId));
    }

    [Fact]
    public async Task PurgeTransaction_RejectsWhenLinkedPaymentIsActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("활성 연동 수금 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-PURGE-ACTIVE-PAYMENT");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
        var transactionAttachmentPath = "storage/blocked-active-transaction-evidence.bin";
        var activePayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = false
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        dbContext.Payments.Add(activePayment);
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "blocked-active-transaction-evidence.bin",
            StoragePath = transactionAttachmentPath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("활성", item.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.DoesNotContain(transactionAttachmentPath, storage.DeletedPaths);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId && !current.IsDeleted));
        Assert.True(await dbContext.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.TransactionId == transactionId));
    }

    [Fact]
    public async Task PurgePayment_RejectsWhenLinkedTransactionStillExists()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("연동 거래내역 잔존 수금 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAY-PURGE-LINKED-TX");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        dbContext.Payments.Add(deletedPayment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("거래내역", item.Message);
        Assert.Equal(0, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
    }

    [Fact]
    public async Task PurgeTransaction_RejectsWhenLinkedPaymentInvoiceOutsidePaymentWriteScope()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var transactionCustomer = CreateScopedCustomer("거래내역 거래처", OfficeCodeCatalog.Usenet);
        var hiddenCustomer = CreateScopedCustomer("권한 외 수금 거래처", OfficeCodeCatalog.Yeonsu);
        var hiddenInvoice = CreateScopedInvoice(hiddenCustomer.Id, OfficeCodeCatalog.Yeonsu, "INV-TX-PURGE-HIDDEN-PAYMENT");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, transactionCustomer.Id, OfficeCodeCatalog.Usenet, hiddenInvoice.Id);
        var transactionAttachmentPath = "storage/blocked-hidden-payment-transaction-evidence.bin";
        var hiddenPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = hiddenInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.AddRange(transactionCustomer, hiddenCustomer);
        dbContext.Invoices.Add(hiddenInvoice);
        dbContext.Transactions.Add(transaction);
        dbContext.Payments.Add(hiddenPayment);
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "blocked-hidden-payment-transaction-evidence.bin",
            StoragePath = transactionAttachmentPath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연동 수금/지급", item.Message);
        Assert.Equal(0, payload.SucceededCount);
        Assert.DoesNotContain(transactionAttachmentPath, storage.DeletedPaths);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.TransactionId == transactionId));
    }

    [Fact]
    public async Task PurgeContract_DeletesStorageOnlyAfterDbCommit()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("계약서 파일 삭제 순서 거래처", OfficeCodeCatalog.Usenet);
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "삭제 순서 계약서",
            StoragePath = "storage/contract-delete-order.bin",
            FileName = "contract-delete-order.bin",
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var deletedBeforeCommit = false;
        var storage = new StubCentralFileStorage
        {
            OnDelete = _ =>
            {
                deletedBeforeCommit = dbContext.CustomerContracts
                    .IgnoreQueryFilters()
                    .Any(current => current.Id == contract.Id);
            }
        };
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = contract.Id,
                        Kind = "contract"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.False(deletedBeforeCommit);
        Assert.Contains(contract.StoragePath, storage.DeletedPaths);
        Assert.False(await dbContext.CustomerContracts.IgnoreQueryFilters().AnyAsync(current => current.Id == contract.Id));
    }

    [Fact]
    public async Task PurgeContract_DoesNotDeleteSharedStoragePathStillReferencedByAnotherContract()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("공유 계약서 파일 거래처", OfficeCodeCatalog.Usenet);
        var sharedStoragePath = "storage/shared-contract-file.bin";
        var purgedContract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "삭제 계약서",
            StoragePath = sharedStoragePath,
            FileName = "shared-contract-file.bin",
            IsDeleted = true
        };
        var remainingContract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "남은 계약서",
            StoragePath = sharedStoragePath,
            FileName = "shared-contract-file.bin",
            IsDeleted = false
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.AddRange(purgedContract, remainingContract);
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = purgedContract.Id,
                        Kind = "contract"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.DoesNotContain(sharedStoragePath, storage.DeletedPaths);
        Assert.False(await dbContext.CustomerContracts.IgnoreQueryFilters().AnyAsync(current => current.Id == purgedContract.Id));
        Assert.Equal(sharedStoragePath, await dbContext.CustomerContracts.IgnoreQueryFilters()
            .Where(current => current.Id == remainingContract.Id)
            .Select(current => current.StoragePath)
            .SingleAsync());
    }

    [Fact]
    public async Task PurgePayment_DeletesAttachmentStorageOnlyAfterDbCommit()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("수금 파일 삭제 순서 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAYMENT-PURGE-DELETE-ORDER");
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 1000m,
            IsDeleted = true
        };
        var paymentAttachmentPath = "storage/payment-delete-order.bin";
        var paymentAttachmentId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = paymentAttachmentId,
            PaymentId = payment.Id,
            FileName = "payment-delete-order.bin",
            StoragePath = paymentAttachmentPath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var deletedBeforeCommit = false;
        var storage = new StubCentralFileStorage
        {
            OnDelete = _ =>
            {
                deletedBeforeCommit = dbContext.Payments
                    .IgnoreQueryFilters()
                    .Any(current => current.Id == payment.Id);
            }
        };
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = payment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.False(deletedBeforeCommit);
        Assert.Contains(paymentAttachmentPath, storage.DeletedPaths);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == payment.Id));
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters().AnyAsync(current => current.Id == paymentAttachmentId));
    }

    [Fact]
    public async Task PurgePayment_DoesNotDeleteSharedAttachmentStoragePathStillReferencedByAnotherPaymentAttachment()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("공유 수금 파일 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAYMENT-PURGE-SHARED-FILE");
        var purgedPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 1000m,
            IsDeleted = true
        };
        var remainingPayment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 2000m,
            IsDeleted = true
        };
        var sharedStoragePath = "storage/shared-payment-attachment.bin";
        var purgedAttachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = purgedPayment.Id,
            FileName = "shared-payment-attachment.bin",
            StoragePath = sharedStoragePath,
            IsDeleted = true
        };
        var remainingAttachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = remainingPayment.Id,
            FileName = "shared-payment-attachment.bin",
            StoragePath = sharedStoragePath,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.AddRange(purgedPayment, remainingPayment);
        dbContext.PaymentAttachments.AddRange(purgedAttachment, remainingAttachment);
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = purgedPayment.Id,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.DoesNotContain(sharedStoragePath, storage.DeletedPaths);
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters().AnyAsync(current => current.Id == purgedAttachment.Id));
        Assert.Equal(sharedStoragePath, await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(current => current.Id == remainingAttachment.Id)
            .Select(current => current.StoragePath)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreInventoryTransfer_RejectsCrossTenantRoute_WhenOneEndpointWritable()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var transferId = Guid.NewGuid();
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            TransferNumber = "RESTORE-CROSS-TENANT-TRANSFER",
            TransferDate = new DateOnly(2026, 6, 23),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.InventoryTransfers.IgnoreQueryFilters()
            .Where(transfer => transfer.Id == transferId)
            .Select(transfer => transfer.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreInventoryTransfer_RejectsActiveOutOfScopeTransferInsteadOfReportingAlreadyActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var transferId = Guid.NewGuid();
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            FromWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            TransferNumber = "RESTORE-ACTIVE-HIDDEN-TRANSFER",
            TransferDate = new DateOnly(2026, 6, 23),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task PurgeInventoryTransfer_RejectsCrossTenantRoute_WhenOneEndpointWritable()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var transferId = Guid.NewGuid();
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Itworld,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            TransferNumber = "PURGE-CROSS-TENANT-TRANSFER",
            TransferDate = new DateOnly(2026, 6, 23),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);
        Assert.True(await dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
    }

    [Fact]
    public async Task PurgeInventoryTransfer_DeletesEvidenceStorageOnlyAfterDbCommit()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "재고이동 삭제 순서 품목",
            NameMatchKey = "재고이동삭제순서품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m
        });
        var transferId = Guid.NewGuid();
        var evidencePath = "storage/inventory-transfer-delete-order.bin";
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PURGE-DELETE-ORDER",
            TransferDate = new DateOnly(2026, 6, 18),
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            ReceiveEvidencePath = evidencePath,
            IsDeleted = true,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "재고이동 삭제 순서 품목",
                    Unit = "개",
                    Quantity = 1m
                }
            ]
        });
        await dbContext.SaveChangesAsync();

        var deletedBeforeCommit = false;
        var storage = new StubCentralFileStorage
        {
            OnDelete = _ =>
            {
                deletedBeforeCommit = dbContext.InventoryTransfers
                    .IgnoreQueryFilters()
                    .Any(current => current.Id == transferId);
            }
        };
        var controller = CreateController(dbContext, currentUser, storage);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.False(deletedBeforeCommit);
        Assert.Contains(evidencePath, storage.DeletedPaths);
        Assert.False(await dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(current => current.Id == transferId));
    }

    [Fact]
    public async Task PurgeInventoryTransfer_RemovesStaleLedgerEntries()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "재고이동 ledger purge 품목",
            NameMatchKey = "재고이동ledgerpurge품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PURGE-LEDGER-CLEANUP",
            TransferDate = new DateOnly(2026, 6, 23),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = true,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "재고이동 ledger purge 품목",
                    Unit = "개",
                    Quantity = 1m
                }
            ]
        });
        dbContext.InventoryLedgerEntries.Add(new InventoryLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            SourceType = "InventoryTransfer:Out",
            SourceDocumentId = transferId,
            SourceLineId = lineId,
            QuantityDelta = -1m,
            OccurredDate = new DateOnly(2026, 6, 23),
            Note = "stale transfer ledger"
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.False(await dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(current => current.Id == transferId));
        Assert.False(await dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task PurgeTransaction_DeletesLinkedPaymentAttachmentStorage()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("연동 수금 첨부 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-PURGE-PAYMENT-ATTACHMENT");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        var transactionAttachmentPath = "storage/transaction-evidence.bin";
        var paymentAttachmentPath = "storage/payment-evidence.bin";
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        dbContext.Payments.Add(deletedPayment);
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "transaction-evidence.bin",
            StoragePath = transactionAttachmentPath,
            IsDeleted = true
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = transactionId,
            FileName = "payment-evidence.bin",
            StoragePath = paymentAttachmentPath,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var storage = new StubCentralFileStorage();
        var controller = CreateController(dbContext, currentUser, storage);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.Contains(transactionAttachmentPath, storage.DeletedPaths);
        Assert.Contains(paymentAttachmentPath, storage.DeletedPaths);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters().AnyAsync(current => current.PaymentId == transactionId));
    }

    [Fact]
    public async Task RestorePayment_RestoresLinkedDeletedTransaction()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("연동 수금 복원 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAYMENT-RESTORE-LINKED-TX");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
        var transactionAttachmentId = Guid.NewGuid();
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        var deletedTransactionAttachment = new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = transactionId,
            AttachmentType = "증빙",
            FileName = "restore-linked-transaction-attachment.pdf",
            MimeType = "application/pdf",
            FileSize = 16,
            FileHash = "restore-linked-transaction-attachment-hash",
            StoragePath = "storage/restore-linked-transaction-attachment.pdf",
            FileContent = [0x25, 0x50, 0x44, 0x46],
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        dbContext.TransactionAttachments.Add(deletedTransactionAttachment);
        dbContext.Payments.Add(deletedPayment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(current => current.Id == transactionId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(current => current.Id == transactionId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(current => current.Id == transactionAttachmentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestorePayment_RestoresDeletedPaymentAttachments()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("첨부 수금 복원 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-PAYMENT-RESTORE-ATTACHMENT");
        var paymentId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var deletedPayment = new Payment
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 1000m,
            IsDeleted = true
        };
        var deletedAttachment = new PaymentAttachment
        {
            Id = attachmentId,
            PaymentId = paymentId,
            FileName = "restore-payment-attachment.pdf",
            MimeType = "application/pdf",
            FileSize = 16,
            FileHash = "restore-payment-attachment-hash",
            StoragePath = "storage/restore-payment-attachment.pdf",
            FileContent = [0x25, 0x50, 0x44, 0x46],
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(deletedPayment);
        dbContext.PaymentAttachments.Add(deletedAttachment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = paymentId,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(current => current.Id == paymentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(current => current.Id == attachmentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreInvoice_AfterRentalInvoiceDelete_RestoresLinkedPaymentTransactionAndSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var invoiceLineId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceNumber = "RECYCLE-RENTAL-RESTORE-INVOICE-001";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Recycle rental invoice restore customer",
            NameMatchKey = "RECYCLERENTALINVOICERESTORECUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Recycle rental invoice restore customer",
            BillingStatus = "완료",
            SettlementStatus = "입금확인",
            CompletionStatus = "완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 100_000m,
            OutstandingAmount = 0m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 5, 26)
                }
            })
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = invoiceLineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "Restore invoice line item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100_000m,
            LineAmount = 100_000m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 5, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = invoiceNumber,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = transactionId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 100_000m,
            Note = "linked rental payment"
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var invoiceController = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        Assert.IsType<NoContentResult>(await invoiceController.Delete(invoiceId, storedInvoice.Revision, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == invoiceLineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == transactionId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        var detached = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(detached.LinkedInvoiceId);
        Assert.Equal(0m, detached.SettlementAmount);

        var recycleController = CreateController(dbContext, currentUser);
        var response = await recycleController.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoiceId,
                        Kind = "invoice"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.Equal(1, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == invoiceLineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
        var restoredPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == transactionId);
        Assert.False(restoredPayment.IsDeleted);
        Assert.Equal(invoiceId, restoredPayment.InvoiceId);
        var relinkedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.False(relinkedTransaction.IsDeleted);
        Assert.Equal(invoiceId, relinkedTransaction.LinkedInvoiceId);
        Assert.Equal(invoiceNumber, relinkedTransaction.LinkedInvoiceNumber);
        Assert.Equal(100_000m, relinkedTransaction.SettlementAmount);
        Assert.Equal(profileId, relinkedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(runId, relinkedTransaction.LinkedRentalBillingRunId);

        var restoredProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(100_000m, restoredProfile.SettledAmount);
        Assert.Equal(0m, restoredProfile.OutstandingAmount);
        var restoredRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(restoredProfile.BillingRunsJson) ?? []);
        Assert.Equal(100_000m, restoredRun.SettledAmount);
        Assert.Equal(new DateOnly(2026, 5, 26), restoredRun.SettledDate);
    }

    [Fact]
    public async Task RestorePayment_AfterRentalInvoiceDelete_RelinksActiveTransactionAndRecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var invoiceLineId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceNumber = "RECYCLE-RENTAL-RESTORE-001";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Recycle rental restore customer",
            NameMatchKey = "RECYCLERENTALRESTORECUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Recycle rental restore customer",
            BillingStatus = "완료",
            SettlementStatus = "입금확인",
            CompletionStatus = "완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 100_000m,
            OutstandingAmount = 0m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 5, 26)
                }
            })
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = invoiceLineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "Restore invoice line by payment item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100_000m,
            LineAmount = 100_000m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 5, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = invoiceNumber,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = transactionId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 100_000m,
            Note = "linked rental payment"
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var invoiceController = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        Assert.IsType<NoContentResult>(await invoiceController.Delete(invoiceId, storedInvoice.Revision, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == transactionId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        var detached = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(detached.LinkedInvoiceId);
        Assert.Equal(0m, detached.SettlementAmount);

        var recycleController = CreateController(dbContext, currentUser);
        var response = await recycleController.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "payment"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);
        Assert.Equal(1, payload.SucceededCount);

        dbContext.ChangeTracker.Clear();
        var restoredInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.False(restoredInvoice.IsDeleted);
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == invoiceLineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
        var restoredPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == transactionId);
        Assert.False(restoredPayment.IsDeleted);
        var relinkedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.False(relinkedTransaction.IsDeleted);
        Assert.Equal(invoiceId, relinkedTransaction.LinkedInvoiceId);
        Assert.Equal(invoiceNumber, relinkedTransaction.LinkedInvoiceNumber);
        Assert.Equal(100_000m, relinkedTransaction.SettlementAmount);

        var restoredProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(100_000m, restoredProfile.SettledAmount);
        Assert.Equal(0m, restoredProfile.OutstandingAmount);
        Assert.Equal("완료", restoredProfile.CompletionStatus);
        var restoredRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(restoredProfile.BillingRunsJson) ?? []);
        Assert.Equal(100_000m, restoredRun.SettledAmount);
        Assert.Equal("완료", restoredRun.Status);
        Assert.Equal("입금확인", restoredRun.SettlementStatus);
        Assert.Equal(new DateOnly(2026, 5, 26), restoredRun.SettledDate);
    }

    [Fact]
    public async Task RestoreTransaction_RestoresLinkedDeletedPayment()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("연동 거래내역 복원 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-RESTORE-LINKED-PAYMENT");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
        var transactionAttachmentId = Guid.NewGuid();
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        var deletedTransactionAttachment = new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = transactionId,
            AttachmentType = "증빙",
            FileName = "restore-transaction-with-linked-payment-attachment.pdf",
            MimeType = "application/pdf",
            FileSize = 16,
            FileHash = "restore-transaction-with-linked-payment-attachment-hash",
            StoragePath = "storage/restore-transaction-with-linked-payment-attachment.pdf",
            FileContent = [0x25, 0x50, 0x44, 0x46],
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
        dbContext.TransactionAttachments.Add(deletedTransactionAttachment);
        dbContext.Payments.Add(deletedPayment);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transactionId,
                        Kind = "transaction"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(current => current.Id == transactionId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(current => current.Id == transactionAttachmentId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(current => current.Id == transactionId)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreCustomerCategory_RejectsActiveDuplicateAndKeepsDeletedRow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        dbContext.CustomerCategories.AddRange(
            new CustomerCategory
            {
                Id = activeId,
                Name = "공공기관",
                IsDeleted = false
            },
            new CustomerCategory
            {
                Id = deletedId,
                Name = " 공공기관 ",
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedId,
                        Kind = "customer-category"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);

        dbContext.ChangeTracker.Clear();
        var rows = await dbContext.CustomerCategories.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.False(rows[activeId].IsDeleted);
        Assert.True(rows[deletedId].IsDeleted);
        Assert.Equal(0, payload.SucceededCount);
    }

    [Fact]
    public async Task PurgeCustomerCategory_RejectsReferencedCategory()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var categoryId = Guid.NewGuid();
        dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = categoryId,
            Name = "참조 고객분류",
            IsDeleted = true
        });
        dbContext.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "참조 거래처",
            NameMatchKey = "참조거래처",
            CategoryId = categoryId,
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = categoryId,
                        Kind = "customer-category"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 거래처", item.Message);
        Assert.True(await dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(current => current.Id == categoryId));
    }

    [Fact]
    public async Task RestorePriceGradeOption_RejectsActiveDuplicateAndKeepsDeletedRow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        dbContext.PriceGradeOptions.AddRange(
            new PriceGradeOption
            {
                Id = activeId,
                Name = "VIP",
                PriceSource = "Sales",
                IsActive = true,
                IsDeleted = false
            },
            new PriceGradeOption
            {
                Id = deletedId,
                Name = " VIP ",
                PriceSource = "A",
                IsActive = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedId,
                        Kind = "price-grade-option"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);

        dbContext.ChangeTracker.Clear();
        var rows = await dbContext.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.False(rows[activeId].IsDeleted);
        Assert.True(rows[activeId].IsActive);
        Assert.True(rows[deletedId].IsDeleted);
        Assert.False(rows[deletedId].IsActive);
    }

    [Fact]
    public async Task RestoreTradeTypeOption_RejectsNonCanonicalAliasAndKeepsDeletedRow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        dbContext.TradeTypeOptions.AddRange(
            new TradeTypeOption
            {
                Id = activeId,
                Name = CustomerClassificationNormalizer.Sales,
                AllowsSales = true,
                AllowsPurchase = false,
                IsActive = true,
                IsDeleted = false
            },
            new TradeTypeOption
            {
                Id = deletedId,
                Name = "판매",
                AllowsSales = true,
                AllowsPurchase = false,
                IsActive = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedId,
                        Kind = "trade-type-option"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);

        dbContext.ChangeTracker.Clear();
        var rows = await dbContext.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.False(rows[activeId].IsDeleted);
        Assert.True(rows[activeId].IsActive);
        Assert.True(rows[deletedId].IsDeleted);
        Assert.False(rows[deletedId].IsActive);
    }

    [Fact]
    public async Task RestoreItemCategoryOption_RejectsLooseKeyDuplicateAndKeepsDeletedRow()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        dbContext.ItemCategoryOptions.AddRange(
            new ItemCategoryOption
            {
                Id = activeId,
                Name = "A3 Copier",
                IsActive = true,
                IsDeleted = false
            },
            new ItemCategoryOption
            {
                Id = deletedId,
                Name = "A3Copier",
                IsActive = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = deletedId,
                        Kind = "item-category-option"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);

        dbContext.ChangeTracker.Clear();
        var rows = await dbContext.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.False(rows[activeId].IsDeleted);
        Assert.True(rows[activeId].IsActive);
        Assert.True(rows[deletedId].IsDeleted);
        Assert.False(rows[deletedId].IsActive);
    }

    [Fact]
    public async Task PurgePriceGradeOption_RejectsCustomerReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var optionId = Guid.NewGuid();
        dbContext.PriceGradeOptions.Add(new PriceGradeOption
        {
            Id = optionId,
            Name = "VIP",
            PriceSource = "Sales",
            IsActive = false,
            IsDeleted = true
        });
        dbContext.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "VIP 거래처",
            NameMatchKey = "VIP거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            PriceGrade = "VIP",
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Purge(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = optionId,
                        Kind = "price-grade-option"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("연결된 거래처", item.Message);
        Assert.True(await dbContext.PriceGradeOptions.IgnoreQueryFilters().AnyAsync(current => current.Id == optionId));
    }

    [Fact]
    public async Task RestoreInvoice_AppliesStockSnapshotsAndLedgerEntries()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var customer = CreateScopedCustomer("전표 복원 재고 거래처", OfficeCodeCatalog.Usenet);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "복원 전표 재고 품목",
            NameMatchKey = "복원전표재고품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Revision = 10
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-RESTORE-STOCK-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 2_000m,
            SupplyAmount = 1_818m,
            VatAmount = 182m,
            IsDeleted = true,
            Lines =
            [
                new InvoiceLine
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = "복원 전표 재고 품목",
                    Unit = "개",
                    Quantity = 2m,
                    UnitPrice = 1_000m,
                    LineAmount = 2_000m,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    OrderIndex = 1,
                    IsDeleted = true
                }
            ]
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = invoiceId,
                        Kind = "invoice",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == lineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.True(await dbContext.InventoryLedgerEntries.AnyAsync(entry =>
            entry.SourceDocumentId == invoiceId &&
            entry.SourceLineId == lineId &&
            entry.SourceType == "Invoice:Sales" &&
            entry.QuantityDelta == -2m));
    }

    [Fact]
    public async Task RestorePayment_WhenInvoiceWasDeleted_RestoresInvoiceStockAndLedgerEntries()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var customer = CreateScopedCustomer("수금 복원 재고 거래처", OfficeCodeCatalog.Usenet);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "수금 복원 전표 재고 품목",
            NameMatchKey = "수금복원전표재고품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Revision = 10
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-RESTORE-PAYMENT-STOCK-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 2_000m,
            SupplyAmount = 1_818m,
            VatAmount = 182m,
            IsDeleted = true,
            Lines =
            [
                new InvoiceLine
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = "수금 복원 전표 재고 품목",
                    Unit = "개",
                    Quantity = 2m,
                    UnitPrice = 1_000m,
                    LineAmount = 2_000m,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    OrderIndex = 1,
                    IsDeleted = true
                }
            ]
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 23),
            Amount = 2_000m,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments.IgnoreQueryFilters().SingleAsync(payment => payment.Id == paymentId);
        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = paymentId,
                        Kind = "payment",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success, item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.Id == lineId)
            .Select(line => line.IsDeleted)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.True(await dbContext.InventoryLedgerEntries.AnyAsync(entry =>
            entry.SourceDocumentId == invoiceId &&
            entry.SourceLineId == lineId &&
            entry.SourceType == "Invoice:Sales" &&
            entry.QuantityDelta == -2m));
    }

    [Fact]
    public async Task RestoreInventoryTransfer_AppliesStockSnapshotsAndLedgerEntries()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "복구 재고이동 품목",
            NameMatchKey = "복구재고이동품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Revision = 10
        });
        var transferId = Guid.NewGuid();
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-RESTORE-STOCK-001",
            TransferDate = new DateOnly(2026, 6, 17),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = true,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "복구 재고이동 품목",
                    Unit = "개",
                    Quantity = 2m
                }
            ]
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.InventoryTransfers.IgnoreQueryFilters().FirstAsync(transfer => transfer.Id == transferId);
        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer",
                        ExpectedRevision = stored.Revision
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.True(item.Success);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.InventoryTransfers.IgnoreQueryFilters()
            .Where(transfer => transfer.Id == transferId)
            .Select(transfer => transfer.IsDeleted)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.True(await dbContext.InventoryLedgerEntries.AnyAsync(entry =>
            entry.SourceDocumentId == transferId &&
            entry.SourceType == "InventoryTransfer:Out" &&
            entry.QuantityDelta == -2m));
    }

    [Fact]
    public async Task RestoreInventoryTransfer_RejectsWhenSourceStockWouldBecomeNegative()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "복구 부족 재고이동 품목",
            NameMatchKey = "복구부족재고이동품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 1m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 1m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Revision = 10
        });
        var transferId = Guid.NewGuid();
        dbContext.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-RESTORE-SHORTAGE-001",
            TransferDate = new DateOnly(2026, 6, 17),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDeleted = true,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "복구 부족 재고이동 품목",
                    Unit = "개",
                    Quantity = 2m
                }
            ]
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var response = await controller.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer"
                    }
                ]
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("재고", item.Message);

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.InventoryTransfers.IgnoreQueryFilters()
            .Where(transfer => transfer.Id == transferId)
            .Select(transfer => transfer.IsDeleted)
            .SingleAsync());
        Assert.Equal(1m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    private RecycleBinController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser,
        StubCentralFileStorage? fileStorage = null)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            fileStorage ?? new StubCentralFileStorage(),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

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

    private static TestCurrentUserContext CreateOfficeOnlyUser()
        => new()
        {
            Username = "office-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = false
        };

    private static TestCurrentUserContext CreateOfficeOnlyAdminUser()
        => new()
        {
            Username = "office-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = true
        };

    private sealed class ServerRentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"TEST-{invoiceDate:yyyyMMdd}-0001");
    }

    private static Customer CreateDeletedCustomerOutsideCurrentOffice()
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "권한 외 삭제 거래처",
            NameMatchKey = "권한외삭제거래처",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        };

    private static Customer CreateScopedCustomer(string name, string officeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name.Replace(" ", string.Empty, StringComparison.Ordinal),
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = false
        };

    private static Invoice CreateScopedInvoice(Guid customerId, string officeCode, string invoiceNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = officeCode,
            InvoiceNumber = invoiceNumber,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m,
            SupplyAmount = 1000m,
            IsDeleted = false
        };

    private static TransactionRecord CreateDeletedTransaction(Guid transactionId, Guid customerId, string officeCode, Guid? linkedInvoiceId = null)
        => new()
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = officeCode,
            TransactionDate = new DateOnly(2026, 6, 17),
            TransactionKind = "전표수금",
            LinkedInvoiceId = linkedInvoiceId,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            IsDeleted = true
        };

    private static RentalBillingProfile CreateRentalProfile(Guid profileId, Guid customerId, string customerName, Guid runId)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"recycle-profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = customerName,
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 23),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettlementStatus = "미입금"
                }
            })
        };

    private static string BuildBillingTemplateJson(Guid assetId)
        => "[{\"IncludedAssetIds\":[\"" + assetId + "\"]}]";

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
        public List<string> DeletedPaths { get; } = new();
        public Action<string?>? OnDelete { get; init; }

        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string category, string tenantKey, Guid fileId, string? fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, category, tenantKey, fileId.ToString("N"), fileName ?? "file.bin"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => fallbackContent ?? Array.Empty<byte>();

        public void DeleteIfExists(string? storedPath)
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
            {
                OnDelete?.Invoke(storedPath);
                DeletedPaths.Add(storedPath);
            }
        }
    }
}
