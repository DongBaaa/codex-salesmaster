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
    public async Task PurgeTransaction_RejectsWhenLinkedPaymentIsActive()
    {
        var currentUser = CreateOfficeOnlyUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = CreateScopedCustomer("활성 연동 수금 거래처", OfficeCodeCatalog.Usenet);
        var invoice = CreateScopedInvoice(customer.Id, OfficeCodeCatalog.Usenet, "INV-TX-PURGE-ACTIVE-PAYMENT");
        var transactionId = Guid.NewGuid();
        var transaction = CreateDeletedTransaction(transactionId, customer.Id, OfficeCodeCatalog.Usenet, invoice.Id);
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

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId && !current.IsDeleted));
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

        dbContext.ChangeTracker.Clear();
        Assert.True(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
        Assert.True(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));

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

        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string category, string tenantKey, Guid fileId, string? fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, category, tenantKey, fileId.ToString("N"), fileName ?? "file.bin"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => fallbackContent ?? Array.Empty<byte>();

        public void DeleteIfExists(string? storedPath)
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
                DeletedPaths.Add(storedPath);
        }
    }
}
