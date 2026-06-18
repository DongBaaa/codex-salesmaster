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
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = Guid.NewGuid(),
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
    }

    [Fact]
    public async Task PurgeInventoryTransfer_DeletesEvidenceStorageOnlyAfterDbCommit()
    {
        var currentUser = CreateOfficeOnlyUser();
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
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
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
        var deletedPayment = new Payment
        {
            Id = transactionId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 1000m,
            IsDeleted = true
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(transaction);
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
