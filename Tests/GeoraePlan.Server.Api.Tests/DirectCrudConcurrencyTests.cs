using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DirectCrudConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DirectCrudConcurrencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task CustomersController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "기존 거래처",
            NameMatchKey = "기존거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "수정 거래처";
        dto.NameMatchKey = "수정거래처";
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Customer), payload.EntityName);
        Assert.Equal(stored.Id, payload.EntityId);
        Assert.Equal(stored.Revision, payload.CurrentRevision);
    }

    [Fact]
    public async Task CustomersController_UpdateAndDelete_RequireExpectedRevisionAndKeepRowUnchanged()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "revision-required-customer",
            NameMatchKey = "REVISIONREQUIREDCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "blind-overwrite-must-not-save";
        dto.NameMatchKey = "BLINDOVERWRITEMUSTNOTSAVE";
        dto.ExpectedRevision = 0;
        dto.Revision = 0;

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

        var updateResponse = await controller.Update(
            stored.Id,
            dto,
            CancellationToken.None);
        var updateRequired = Assert.IsType<ObjectResult>(updateResponse.Result);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, updateRequired.StatusCode);
        var updatePayload = JsonSerializer.SerializeToElement(updateRequired.Value);
        Assert.Equal(nameof(Customer), updatePayload.GetProperty("EntityName").GetString());
        Assert.Equal(stored.Id, updatePayload.GetProperty("EntityId").GetGuid());
        Assert.Equal(stored.Revision, updatePayload.GetProperty("CurrentRevision").GetInt64());

        var deleteResponse = await controller.Delete(
            stored.Id,
            expectedRevision: null,
            CancellationToken.None);
        var deleteRequired = Assert.IsType<ObjectResult>(deleteResponse);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, deleteRequired.StatusCode);
        var deletePayload = JsonSerializer.SerializeToElement(deleteRequired.Value);
        Assert.Equal(stored.Revision, deletePayload.GetProperty("CurrentRevision").GetInt64());

        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == stored.Id);
        Assert.Equal("revision-required-customer", unchanged.NameOriginal);
        Assert.False(unchanged.IsDeleted);
    }

    [Fact]
    public async Task MasterDeleteRetries_ReturnNoContentWithoutAnotherRevisionOrAudit()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "delete-retry-customer",
            NameMatchKey = "DELETERETRYCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "delete-retry-item",
            NameMatchKey = "DELETERETRYITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.NonStock
        };
        var unit = new Unit
        {
            Id = Guid.NewGuid(),
            Name = "DELETE-RETRY-UNIT",
            IsActive = true
        };
        var category = new CustomerCategory
        {
            Id = Guid.NewGuid(),
            Name = "DELETE-RETRY-CATEGORY"
        };
        dbContext.AddRange(customer, item, unit, category);
        await dbContext.SaveChangesAsync();

        var customerRevision = customer.Revision;
        var itemRevision = item.Revision;
        var unitRevision = unit.Revision;
        var categoryRevision = category.Revision;
        var customerController = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());
        var itemController = new ItemsController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var unitController = new UnitsController(dbContext);
        var categoryController = new CustomerCategoriesController(dbContext);

        Assert.IsType<NoContentResult>(
            await customerController.Delete(customer.Id, customerRevision, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await itemController.Delete(item.Id, itemRevision, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await unitController.Delete(unit.Id, unitRevision, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await categoryController.Delete(category.Id, categoryRevision, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var customerAfterFirstDelete = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        var itemAfterFirstDelete = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == item.Id);
        var unitAfterFirstDelete = await dbContext.Units.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == unit.Id);
        var categoryAfterFirstDelete = await dbContext.CustomerCategories.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == category.Id);
        var auditCountAfterFirstDelete = await dbContext.AuditLogs.CountAsync();

        Assert.IsType<NoContentResult>(
            await customerController.Delete(customer.Id, expectedRevision: null, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await itemController.Delete(item.Id, expectedRevision: null, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await unitController.Delete(unit.Id, expectedRevision: null, CancellationToken.None));
        Assert.IsType<NoContentResult>(
            await categoryController.Delete(category.Id, expectedRevision: null, CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        var customerAfterRetry = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        var itemAfterRetry = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == item.Id);
        var unitAfterRetry = await dbContext.Units.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == unit.Id);
        var categoryAfterRetry = await dbContext.CustomerCategories.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == category.Id);

        Assert.True(customerAfterRetry.IsDeleted);
        Assert.Equal(customerAfterFirstDelete.Revision, customerAfterRetry.Revision);
        Assert.Equal(customerAfterFirstDelete.UpdatedAtUtc, customerAfterRetry.UpdatedAtUtc);
        Assert.True(itemAfterRetry.IsDeleted);
        Assert.Equal(itemAfterFirstDelete.Revision, itemAfterRetry.Revision);
        Assert.Equal(itemAfterFirstDelete.UpdatedAtUtc, itemAfterRetry.UpdatedAtUtc);
        Assert.True(unitAfterRetry.IsDeleted);
        Assert.Equal(unitAfterFirstDelete.Revision, unitAfterRetry.Revision);
        Assert.Equal(unitAfterFirstDelete.UpdatedAtUtc, unitAfterRetry.UpdatedAtUtc);
        Assert.True(categoryAfterRetry.IsDeleted);
        Assert.Equal(categoryAfterFirstDelete.Revision, categoryAfterRetry.Revision);
        Assert.Equal(categoryAfterFirstDelete.UpdatedAtUtc, categoryAfterRetry.UpdatedAtUtc);
        Assert.Equal(auditCountAfterFirstDelete, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CustomersController_Update_AcceptsValidIfMatchWhenBodyRevisionIsMissing()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "if-match-customer",
            NameMatchKey = "IFMATCHCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == customer.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "if-match-updated";
        dto.NameMatchKey = "IFMATCHUPDATED";
        dto.ExpectedRevision = 0;
        dto.Revision = 0;

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Headers.IfMatch = $"\"{stored.Revision}\"";

        var response = await controller.Update(
            stored.Id,
            dto,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            "if-match-updated",
            await dbContext.Customers.IgnoreQueryFilters()
                .Where(current => current.Id == stored.Id)
                .Select(current => current.NameOriginal)
                .SingleAsync());
    }

    [Fact]
    public async Task CustomersController_UpdateAndDelete_ForbidTenantOfficeMismatchedExistingCustomer()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-customer-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.CustomerEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TENANT-MISMATCH-CUSTOMER",
            NameMatchKey = "TENANTMISMATCHCUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().SingleAsync(row => row.Id == customer.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "SHOULD-NOT-BE-SAVED";
        dto.ExpectedRevision = stored.Revision;

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

        var updateResponse = await controller.Update(stored.Id, dto, CancellationToken.None);
        var deleteResponse = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        Assert.IsType<ForbidResult>(deleteResponse);
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Customers.IgnoreQueryFilters().SingleAsync(row => row.Id == customer.Id);
        Assert.Equal(TenantScopeCatalog.Itworld, unchanged.TenantCode);
        Assert.Equal("TENANT-MISMATCH-CUSTOMER", unchanged.NameOriginal);
        Assert.False(unchanged.IsDeleted);
    }

    [Fact]
    public async Task CustomersController_Update_SynchronizesLinkedRentalCustomerSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var profileLinkedAssetId = Guid.NewGuid();
        var currentHistoryId = Guid.NewGuid();
        var profileLinkedHistoryId = Guid.NewGuid();
        var pastHistoryId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Old Server Customer",
            NameMatchKey = "OLDSERVERCUSTOMER",
            BusinessNumber = "OLD-BIZ",
            Email = "old-server@example.test",
            TradeType = CustomerClassificationNormalizer.Sales
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
            CustomerName = "Stale Server Billing",
            BusinessNumber = "STALE-BIZ",
            Email = "stale-profile@example.test",
            ItemName = "Server Rental Line"
        });
        dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = $"asset-{assetId:N}",
                CustomerId = customerId,
                CustomerName = "Stale Server Asset",
                CurrentCustomerName = "Stale Server Asset",
                ManagementNumber = "SERVER-ASSET-001",
                ItemName = "Server Rental Asset"
            },
            new RentalAsset
            {
                Id = profileLinkedAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = $"asset-{profileLinkedAssetId:N}",
                BillingProfileId = profileId,
                CustomerName = "Stale Profile Linked Asset",
                CurrentCustomerName = "Stale Profile Linked Asset",
                ManagementNumber = "SERVER-ASSET-002",
                ItemName = "Server Profile Linked Rental Asset"
            });
        dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = currentHistoryId,
                AssetId = assetId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Stale Server History",
                ItemName = "Server Rental Asset",
                ManagementNumber = "SERVER-ASSET-001",
                IsCurrent = true
            },
            new RentalAssetAssignmentHistory
            {
                Id = profileLinkedHistoryId,
                AssetId = profileLinkedAssetId,
                BillingProfileId = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Stale Profile Linked History",
                BillingProfileDisplay = "profile history",
                ItemName = "Server Profile Linked Rental Asset",
                ManagementNumber = "SERVER-ASSET-002",
                IsCurrent = true
            },
            new RentalAssetAssignmentHistory
            {
                Id = pastHistoryId,
                AssetId = assetId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Past Server Customer Snapshot",
                ItemName = "Past Server Asset",
                ManagementNumber = "SERVER-ASSET-PAST",
                IsCurrent = false
            });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == customerId);
        var dto = stored.ToDto();
        dto.NameOriginal = "New Server Customer";
        dto.NameMatchKey = "NEWSERVERCUSTOMER";
        dto.BusinessNumber = "NEW-BIZ";
        dto.Email = "new-server@example.test";
        dto.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        dto.OfficeCode = OfficeCodeCatalog.Usenet;
        dto.TenantCode = TenantScopeCatalog.UsenetGroup;
        dto.ExpectedRevision = stored.Revision;

        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var response = await controller.Update(customerId, dto, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var syncedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileId);
        Assert.Equal("New Server Customer", syncedProfile.CustomerName);
        Assert.Equal("NEW-BIZ", syncedProfile.BusinessNumber);
        Assert.Equal("new-server@example.test", syncedProfile.Email);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, syncedProfile.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfile.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfile.ManagementCompanyCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, syncedProfile.TenantCode);

        var syncedAsset = await dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == assetId);
        Assert.Equal("New Server Customer", syncedAsset.CustomerName);
        Assert.Equal("New Server Customer", syncedAsset.CurrentCustomerName);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, syncedAsset.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedAsset.ManagementCompanyCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, syncedAsset.TenantCode);

        var syncedProfileLinkedAsset = await dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileLinkedAssetId);
        Assert.Equal("New Server Customer", syncedProfileLinkedAsset.CustomerName);
        Assert.Equal("New Server Customer", syncedProfileLinkedAsset.CurrentCustomerName);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, syncedProfileLinkedAsset.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfileLinkedAsset.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, syncedProfileLinkedAsset.ManagementCompanyCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, syncedProfileLinkedAsset.TenantCode);

        var syncedCurrentHistory = await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == currentHistoryId);
        var syncedProfileLinkedHistory = await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == profileLinkedHistoryId);
        foreach (var history in new[] { syncedCurrentHistory, syncedProfileLinkedHistory })
        {
            Assert.Equal("New Server Customer", history.CustomerName);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, history.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, history.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, history.TenantCode);
        }

        var preservedPastHistory = await dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == pastHistoryId);
        Assert.Equal("Past Server Customer Snapshot", preservedPastHistory.CustomerName);
        Assert.Equal(OfficeCodeCatalog.Usenet, preservedPastHistory.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, preservedPastHistory.OfficeCode);
    }

    [Fact]
    public async Task CustomersController_Delete_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "삭제 대상 거래처",
            NameMatchKey = "삭제대상거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision + 1, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Customer), payload.EntityName);
        Assert.Equal(stored.Revision, payload.CurrentRevision);
    }

    [Fact]
    public async Task CustomersController_Delete_ReturnsConflict_WhenActiveBusinessReferencesRemain()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REFERENCE-BLOCK-CUSTOMER",
            NameMatchKey = "REFERENCEBLOCKCUSTOMER",
            TradeType = "매출"
        };
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = "CUSTOMER-DELETE-BLOCK-INVOICE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19)
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "수금"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"asset-{assetId:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            ManagementNumber = "A-001"
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            CustomerName = customer.NameOriginal,
            ManagementNumber = "A-001",
            IsCurrent = true
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        Assert.Equal(CustomerDeletionReferenceGuard.ConflictCode, payloadType.GetProperty("error")?.GetValue(payload));
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("전표 1건", message, StringComparison.Ordinal);
        Assert.Contains("거래내역 1건", message, StringComparison.Ordinal);
        Assert.Contains("렌탈 청구 1건", message, StringComparison.Ordinal);
        Assert.Contains("렌탈 자산 1건", message, StringComparison.Ordinal);
        Assert.Contains("현재 설치이력 1건", message, StringComparison.Ordinal);
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task CustomersController_Delete_CascadesContractsWithoutClearingPrimaryFlag()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DELETE-CONTRACT-PRIMARY-CUSTOMER",
            NameMatchKey = "DELETECONTRACTPRIMARYCUSTOMER",
            TradeType = "매출"
        };
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "대표 계약서",
            FileName = "primary-contract.pdf",
            MimeType = "application/pdf",
            FileHash = "PRIMARY-CONTRACT",
            FileSize = 1,
            IsPrimary = true,
            IsDeleted = false
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(current => current.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        var deletedContract = await dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == contract.Id);
        Assert.True(deletedContract.IsDeleted);
        Assert.True(deletedContract.IsPrimary);
    }

    [Fact]
    public async Task CustomersController_Update_RejectsSoftDeleteMutationViaPut()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PUT-SOFT-DELETE-CUSTOMER",
            NameMatchKey = "PUTSOFTDELETECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = "PUT-CUSTOMER-DELETE-BYPASS",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24)
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customer.Id);
        var dto = stored.ToDto();
        dto.IsDeleted = true;
        dto.ExpectedRevision = stored.Revision;
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        AssertSoftDeletePutRejected(badRequest);
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(row => row.Id == customer.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoice.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task DirectCreateEndpoints_RejectSoftDeletedPayloadsAndDoNotCreateHiddenRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var customerMasterId = Guid.NewGuid();

        var customersController = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var customerResponse = await customersController.Create(new CustomerDto
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CREATE-DELETED-CUSTOMER",
            NameMatchKey = "CREATEDELETEDCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales,
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(customerResponse.Result));

        var itemsController = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var itemResponse = await itemsController.Create(new ItemDto
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CREATE-DELETED-ITEM",
            NameMatchKey = "CREATEDELETEDITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(itemResponse.Result));

        var invoicesController = CreateInvoicesController(dbContext, currentUser);
        var invoiceResponse = await invoicesController.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = Guid.NewGuid(),
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(invoiceResponse.Result));

        var paymentsController = CreatePaymentsController(dbContext, currentUser);
        var paymentResponse = await paymentsController.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = Guid.NewGuid(),
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 1m,
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(paymentResponse.Result));

        var unitsController = new UnitsController(dbContext);
        var unitResponse = await unitsController.Create(new UnitDto
        {
            Id = unitId,
            Name = "CREATE-DELETED-UNIT",
            IsActive = true,
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(unitResponse.Result));

        var customerCategoriesController = new CustomerCategoriesController(dbContext);
        var categoryResponse = await customerCategoriesController.Create(new CustomerCategoryDto
        {
            Id = categoryId,
            Name = "생성삭제분류",
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(categoryResponse.Result));

        var customerMastersController = new CustomerMastersController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var customerMasterResponse = await customerMastersController.Create(new CustomerMasterDto
        {
            Id = customerMasterId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CREATE-DELETED-MASTER",
            NameMatchKey = "CREATEDELETEDMASTER",
            IsDeleted = true
        }, CancellationToken.None);
        AssertSoftDeleteCreateRejected(Assert.IsType<BadRequestObjectResult>(customerMasterResponse.Result));

        Assert.False(await dbContext.Customers.IgnoreQueryFilters().AnyAsync(row => row.Id == customerId));
        Assert.False(await dbContext.Items.IgnoreQueryFilters().AnyAsync(row => row.Id == itemId));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(row => row.Id == paymentId));
        Assert.False(await dbContext.Units.IgnoreQueryFilters().AnyAsync(row => row.Id == unitId));
        Assert.False(await dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(row => row.Id == categoryId));
        Assert.False(await dbContext.CustomerMasters.IgnoreQueryFilters().AnyAsync(row => row.Id == customerMasterId));
    }

    [Fact]
    public async Task ItemsController_Update_ReturnsConflict_WhenRevisionFallbackDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITEM-A",
            NameMatchKey = "ITEMA"
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().FirstAsync(x => x.Id == item.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "ITEM-B";
        dto.NameMatchKey = "ITEMB";
        dto.Revision = stored.Revision + 1;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Item), payload.EntityName);
    }

    [Theory]
    [InlineData(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Usenet)]
    [InlineData(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Itworld)]
    public async Task ItemsController_Update_RejectsExistingItemScopeChangeAndKeepsInventoryState(
        string requestedTenantCode,
        string requestedOfficeCode)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "IMMUTABLE-SCOPE-DIRECT-ITEM",
            NameMatchKey = "IMMUTABLESCOPEDIRECTITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m,
            Notes = "direct-before"
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m
        });
        await dbContext.SaveChangesAsync();

        var storedBefore = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        var stockBefore = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        var dto = storedBefore.ToDto();
        dto.TenantCode = requestedTenantCode;
        dto.OfficeCode = requestedOfficeCode;
        dto.Notes = "direct-must-not-write";
        dto.ExpectedRevision = storedBefore.Revision;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(storedBefore.Id, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(
            "Item tenant/office scope cannot be changed for an existing item.",
            badRequest.Value);

        dbContext.ChangeTracker.Clear();
        var storedAfter = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        Assert.Equal(storedBefore.TenantCode, storedAfter.TenantCode);
        Assert.Equal(storedBefore.OfficeCode, storedAfter.OfficeCode);
        Assert.Equal(storedBefore.Notes, storedAfter.Notes);
        Assert.Equal(storedBefore.Revision, storedAfter.Revision);

        var stockAfter = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        Assert.Equal(stockBefore.WarehouseCode, stockAfter.WarehouseCode);
        Assert.Equal(stockBefore.Quantity, stockAfter.Quantity);
        Assert.Equal(stockBefore.Revision, stockAfter.Revision);
    }

    [Theory]
    [InlineData("INVALID-TENANT", OfficeCodeCatalog.Usenet, "Item tenant scope is invalid.")]
    [InlineData(TenantScopeCatalog.UsenetGroup, "INVALID-OFFICE", "Item office scope is invalid.")]
    public async Task ItemsController_Update_RejectsInvalidExplicitItemScopeAndKeepsInventoryState(
        string requestedTenantCode,
        string requestedOfficeCode,
        string expectedError)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "INVALID-SCOPE-DIRECT-ITEM",
            NameMatchKey = "INVALIDSCOPEDIRECTITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m,
            Notes = "invalid-before"
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m
        });
        await dbContext.SaveChangesAsync();

        var storedBefore = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        var stockBefore = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        var dto = storedBefore.ToDto();
        dto.TenantCode = requestedTenantCode;
        dto.OfficeCode = requestedOfficeCode;
        dto.Notes = "invalid-must-not-write";
        dto.ExpectedRevision = storedBefore.Revision;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(storedBefore.Id, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(expectedError, badRequest.Value);

        dbContext.ChangeTracker.Clear();
        var storedAfter = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        Assert.Equal(storedBefore.TenantCode, storedAfter.TenantCode);
        Assert.Equal(storedBefore.OfficeCode, storedAfter.OfficeCode);
        Assert.Equal(storedBefore.Notes, storedAfter.Notes);
        Assert.Equal(storedBefore.Revision, storedAfter.Revision);

        var stockAfter = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        Assert.Equal(stockBefore.Quantity, stockAfter.Quantity);
        Assert.Equal(stockBefore.Revision, stockAfter.Revision);
    }

    [Fact]
    public async Task ItemsController_Update_AllowsTenantWideEditorToKeepSharedItemScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-tenant-item-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            Permissions = [PermissionNames.ItemEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "SHARED-SCOPE-DIRECT-ITEM",
            NameMatchKey = "SHAREDSCOPEDIRECTITEM",
            TrackingType = ItemTrackingTypes.Asset,
            Notes = "shared-before"
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var storedBefore = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        var dto = storedBefore.ToDto();
        dto.Notes = "shared-after";
        dto.ExpectedRevision = storedBefore.Revision;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(storedBefore.Id, dto, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var updated = Assert.IsType<ItemDto>(ok.Value);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, updated.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Shared, updated.OfficeCode);
        Assert.Equal("shared-after", updated.Notes);
    }

    [Fact]
    public async Task ItemsController_Update_HidesExistingItemScopeDetailsFromUnauthorizedEditor()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-office-item-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.ItemEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "READ-ONLY-SHARED-DIRECT-ITEM",
            NameMatchKey = "READONLYSHAREDDIRECTITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 2m,
            Notes = "unauthorized-before"
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 2m
        });
        await dbContext.SaveChangesAsync();

        var storedBefore = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        var stockBefore = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        var sameScopeDto = storedBefore.ToDto();
        sameScopeDto.Notes = "same-scope-must-not-write";
        sameScopeDto.ExpectedRevision = storedBefore.Revision;
        var changedScopeDto = storedBefore.ToDto();
        changedScopeDto.TenantCode = TenantScopeCatalog.Itworld;
        changedScopeDto.OfficeCode = OfficeCodeCatalog.Itworld;
        changedScopeDto.Notes = "changed-scope-must-not-write";
        changedScopeDto.ExpectedRevision = storedBefore.Revision;
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var sameScopeResponse = await controller.Update(
            storedBefore.Id,
            sameScopeDto,
            CancellationToken.None);
        var changedScopeResponse = await controller.Update(
            storedBefore.Id,
            changedScopeDto,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(sameScopeResponse.Result);
        Assert.IsType<ForbidResult>(changedScopeResponse.Result);

        dbContext.ChangeTracker.Clear();
        var storedAfter = await dbContext.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == item.Id);
        Assert.Equal(storedBefore.TenantCode, storedAfter.TenantCode);
        Assert.Equal(storedBefore.OfficeCode, storedAfter.OfficeCode);
        Assert.Equal(storedBefore.Notes, storedAfter.Notes);
        Assert.Equal(storedBefore.Revision, storedAfter.Revision);

        var stockAfter = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync(row => row.ItemId == item.Id);
        Assert.Equal(stockBefore.Quantity, stockAfter.Quantity);
        Assert.Equal(stockBefore.Revision, stockAfter.Revision);
    }

    [Fact]
    public async Task ItemsController_UpdateAndDelete_ForbidTenantOfficeMismatchedExistingItem()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-item-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.ItemEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TENANT-MISMATCH-ITEM",
            NameMatchKey = "TENANTMISMATCHITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "SHOULD-NOT-BE-SAVED";
        dto.ExpectedRevision = stored.Revision;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var updateResponse = await controller.Update(stored.Id, dto, CancellationToken.None);
        var deleteResponse = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        Assert.IsType<ForbidResult>(deleteResponse);
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        Assert.Equal(TenantScopeCatalog.Itworld, unchanged.TenantCode);
        Assert.Equal("TENANT-MISMATCH-ITEM", unchanged.NameOriginal);
        Assert.False(unchanged.IsDeleted);
    }

    [Fact]
    public async Task ItemsController_Update_RejectsSoftDeleteMutationViaPutAndKeepsWarehouseStocks()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PUT-SOFT-DELETE-ITEM",
            NameMatchKey = "PUTSOFTDELETEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 7m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 7m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == item.Id);
        var dto = stored.ToDto();
        dto.IsDeleted = true;
        dto.ExpectedRevision = stored.Revision;
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        AssertSoftDeletePutRejected(badRequest);
        Assert.False(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == item.Id));
    }

    [Fact]
    public async Task ItemsController_Delete_RemovesWarehouseStockRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Delete stock item",
            NameMatchKey = "DELETESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        Assert.True(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == item.Id));
    }

    [Fact]
    public async Task ItemsController_Delete_RejectsActiveInvoiceLineReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Active invoice item",
            NameMatchKey = "ACTIVEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 3m
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Item delete invoice customer",
            NameMatchKey = "ITEMDELETEINVOICECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-ITEM-DELETE-BLOCK",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19),
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    OrderIndex = 1
                }
            ]
        };
        dbContext.Items.Add(item);
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("전표 라인", message);
        Assert.False(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task ItemsController_Delete_AllowsRentalBillingTemplateRowIdMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Rental template referenced item",
            NameMatchKey = "RENTALTEMPLATEREFERENCEDITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        var profileId = Guid.NewGuid();
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"item-template-guard-{profileId:N}",
            CustomerName = "Item template guard customer",
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
            })
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        Assert.True(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task ItemsController_Delete_RejectsActiveRentalBillingTemplateCatalogItemReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Rental template blocked item",
            NameMatchKey = "RENTALTEMPLATEBLOCKEDITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        var profileId = Guid.NewGuid();
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"item-template-block-{profileId:N}",
            CustomerName = "Item template block customer",
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = Guid.NewGuid(),
                    CatalogItemId = item.Id,
                    DisplayItemName = item.NameOriginal,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m
                }
            })
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("렌탈 청구프로필", message);
        Assert.False(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task ItemsController_Create_EnsuresActiveItemCategoryOption()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Create(new ItemDto
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server direct item category",
            NameMatchKey = "SERVERDIRECTITEMCATEGORY",
            CategoryName = " A3 Copier ",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA"
        }, CancellationToken.None);

        var item = AssertOk<ItemDto>(response);

        Assert.Equal("A3 Copier", item.CategoryName);
        var option = await dbContext.ItemCategoryOptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("A3 Copier", option.Name);
        Assert.True(option.IsActive);
        Assert.False(option.IsDeleted);
    }

    [Fact]
    public async Task DbInitializer_RepairItemCurrentStockSnapshots_RecalculatesFromWarehouseTotals()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Repair stock item",
            NameMatchKey = "REPAIRSTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m,
                Revision = 1
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = "USENET_SUB",
                Quantity = 2m,
                Revision = 2
            });
        await dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairItemCurrentStockSnapshotsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var repaired = await Assert.IsType<Task<int>>(method.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, repaired);
        Assert.Equal(3m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task DbInitializer_PreservesNegativeWarehouseStockAndRecalculatesSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Negative stock item",
            NameMatchKey = "NEGATIVESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = -1m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = -1m,
                Revision = 1
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = "USENET_SUB",
                Quantity = 2m,
                Revision = 2
            });
        await dbContext.SaveChangesAsync();

        var repairNegativeMethod = typeof(DbInitializer).GetMethod(
            "RepairNegativeItemWarehouseStocksAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        var repairSnapshotMethod = typeof(DbInitializer).GetMethod(
            "RepairItemCurrentStockSnapshotsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(repairNegativeMethod);
        Assert.NotNull(repairSnapshotMethod);

        var repairedNegativeRows = await Assert.IsType<Task<int>>(repairNegativeMethod!.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();
        var repairedSnapshots = await Assert.IsType<Task<int>>(repairSnapshotMethod!.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, repairedNegativeRows);
        Assert.Equal(1, repairedSnapshots);
        Assert.Equal(-1m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Equal(1m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "청구 거래처",
            NameMatchKey = "청구거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-001",
            InvoiceDate = new DateOnly(2026, 4, 11)
        };
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .FirstAsync(x => x.Id == invoice.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
    }

    [Fact]
    public async Task InvoicesController_Update_RejectsSoftDeleteMutationViaPutAndKeepsLinkedRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PUT-SOFT-DELETE-INVOICE-CUSTOMER",
            NameMatchKey = "PUTSOFTDELETEINVOICECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PUT-INVOICE-DELETE-BYPASS",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "PUT soft delete line",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m,
            OrderIndex = 1
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 10m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 24),
            TransactionKind = "invoice-linked-receipt",
            LinkedInvoiceId = invoiceId,
            ReceiptTotal = 10m
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .FirstAsync(x => x.Id == invoiceId);
        var dto = stored.ToDto();
        dto.IsDeleted = true;
        dto.ExpectedRevision = stored.Revision;
        var controller = CreateInvoicesController(dbContext, currentUser);

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        AssertSoftDeletePutRejected(badRequest);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(row => row.Id == transactionId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_RejectsProtectedInvoiceSameIdLineMutation_WhenPaymentExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-INVOICE-CUSTOMER",
            NameMatchKey = "DIRECTPAIDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-INVOICE-ITEM",
            NameMatchKey = "DIRECTPAIDINVOICEITEM",
            TrackingType = ItemTrackingTypes.NonStock
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-PAID-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 100m,
            Note = "paid before direct API edit"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Lines.Single().UnitPrice = 200m;
        dto.Lines.Single().LineAmount = 200m;
        dto.TotalAmount = 200m;
        dto.SupplyAmount = 182m;
        dto.VatAmount = 18m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(invoiceId, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
        Assert.Equal(ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation, payload.Reason);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.InvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.Amount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_RejectsProtectedInvoiceSameIdLineMutation_WhenLinkedTransactionExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-TRANSACTION-INVOICE-CUSTOMER",
            NameMatchKey = "DIRECTTRANSACTIONINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-TRX-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "transaction line",
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 25),
            TransactionKind = "direct invoice receipt",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "DIRECT-TRX-001",
            BankReceipt = 100m,
            ReceiptTotal = 100m,
            SettlementAmount = 100m
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Lines.Single().UnitPrice = 200m;
        dto.Lines.Single().LineAmount = 200m;
        dto.TotalAmount = 200m;
        dto.SupplyAmount = 182m;
        dto.VatAmount = 18m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(invoiceId, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
        Assert.Equal(ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation, payload.Reason);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Transactions.IgnoreQueryFilters()
            .Where(row => row.LinkedInvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.SettlementAmount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_AllowsProtectedInvoiceMetadataOnlyChange_WhenPaymentExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-METADATA-CUSTOMER",
            NameMatchKey = "DIRECTPAIDMETADATACUSTOMER",
            TradeType = "Sales"
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-PAID-META-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Memo = "before"
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "metadata line",
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 100m,
            Note = "paid before metadata edit"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Memo = "memo-only direct API edit";

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var saved = AssertOk(await controller.Update(invoiceId, dto, CancellationToken.None));

        Assert.Equal("memo-only direct API edit", saved.Memo);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.InvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.Amount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_ForbidsTenantOfficeMismatchedExistingInvoice_ForOfficeScopedUser()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-invoice-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TENANT-MISMATCH-INVOICE-CUSTOMER",
            NameMatchKey = "TENANTMISMATCHINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "TENANT-MISMATCH-INV",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23),
            Memo = "original mismatch memo"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .FirstAsync(x => x.Id == invoice.Id);
        var dto = stored.ToDto();
        dto.Memo = "should not be saved";
        dto.TenantCode = TenantScopeCatalog.UsenetGroup;
        dto.ExpectedRevision = stored.Revision;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(TenantScopeCatalog.Itworld, unchanged.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, unchanged.OfficeCode);
        Assert.Equal("original mismatch memo", unchanged.Memo);
    }

    [Fact]
    public async Task InvoicesController_Create_RecordsMutationReceipt_ForMobileRetry()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MOBILE-MUTATION-CUSTOMER",
            NameMatchKey = "MOBILEMUTATIONCUSTOMER",
            TradeType = "Sales"
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var mutationId = $"mobile:invoice:{invoiceId:N}:{Guid.NewGuid():N}";
        var mutationCreatedAtUtc = new DateTime(2026, 6, 22, 1, 2, 3, DateTimeKind.Utc);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "MOBILE-MUTATION-CUSTOMER",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            MutationId = mutationId,
            MutationCreatedAtUtc = mutationCreatedAtUtc
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var receipt = await dbContext.ProcessedSyncMutations.SingleAsync(current => current.MutationId == mutationId);
        Assert.Equal(nameof(Invoice), receipt.EntityName);
        Assert.Equal(invoiceId.ToString("D"), receipt.EntityId);
        Assert.Equal(ProcessedSyncMutationRecorder.DirectApiDeviceId, receipt.DeviceId);
        Assert.Equal(mutationCreatedAtUtc, receipt.ProcessedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", receipt.PayloadHash);
    }

    [Fact]
    public async Task InvoicesController_Create_ReplaysSameMutationAndRejectsChangedPayload()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "INVOICE-IDEMPOTENCY-CUSTOMER",
            NameMatchKey = "INVOICEIDEMPOTENCYCUSTOMER",
            TradeType = "Sales"
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var mutationId = $"direct:invoice:{invoiceId:N}:{Guid.NewGuid():N}";
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        InvoiceDto CreateRequest(string memo) => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "INVOICE-IDEMPOTENCY-CUSTOMER",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 23),
            Memo = memo,
            MutationId = mutationId,
            MutationCreatedAtUtc = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc)
        };

        var first = await controller.Create(CreateRequest("original payload"), CancellationToken.None);
        var replayRequest = CreateRequest("original payload");
        replayRequest.MutationId = mutationId.ToUpperInvariant();
        var changedRequest = CreateRequest("changed payload");
        changedRequest.MutationId = mutationId.ToUpperInvariant();

        var replay = await controller.Create(replayRequest, CancellationToken.None);
        var changed = await controller.Create(changedRequest, CancellationToken.None);

        Assert.IsType<OkObjectResult>(first.Result);
        var replayResult = Assert.IsType<OkObjectResult>(replay.Result);
        Assert.Equal(invoiceId, Assert.IsType<InvoiceDto>(replayResult.Value).Id);
        var conflict = Assert.IsType<ConflictObjectResult>(changed.Result);
        Assert.Equal(
            "mutation_id_conflict",
            Assert.IsType<DirectMutationConflictResponse>(conflict.Value).Error);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await dbContext.Invoices.IgnoreQueryFilters().CountAsync(invoice => invoice.Id == invoiceId));
        Assert.Equal(
            "original payload",
            await dbContext.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Memo)
                .SingleAsync());
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt => receipt.MutationId == mutationId));
        Assert.Equal(
            ProcessedSyncMutationRecorder.NormalizeMutationId(mutationId),
            await dbContext.ProcessedSyncMutations
                .Select(receipt => receipt.MutationId)
                .SingleAsync());
    }

    [Fact]
    public async Task ProcessedMutationId_UsesCanonicalModelCollationAndRejectsCaseVariantRawInsert()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        await using var schemaCommand = dbContext.Database.GetDbConnection().CreateCommand();
        schemaCommand.CommandText =
            """SELECT "sql" FROM "sqlite_master" WHERE "type" = 'table' AND "name" = 'ProcessedSyncMutations';""";
        var tableSql = Assert.IsType<string>(await schemaCommand.ExecuteScalarAsync());
        Assert.Contains("\"MutationId\" TEXT COLLATE NOCASE", tableSql, StringComparison.OrdinalIgnoreCase);

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ProcessedSyncMutations"
                 ("Id", "MutationId", "DeviceId", "EntityName", "EntityId",
                  "ExpectedRevision", "PayloadHash", "ProcessedAtUtc")
             VALUES
                 ({firstId}, {"Case-Sensitive-Mutation"}, {"device-a"}, {"Invoice"}, {Guid.NewGuid().ToString("D")},
                  {0L}, {new string('a', 64)}, {DateTime.UtcNow});
             """);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO "ProcessedSyncMutations"
                     ("Id", "MutationId", "DeviceId", "EntityName", "EntityId",
                      "ExpectedRevision", "PayloadHash", "ProcessedAtUtc")
                 VALUES
                     ({secondId}, {"case-sensitive-mutation"}, {"device-b"}, {"Invoice"}, {Guid.NewGuid().ToString("D")},
                      {0L}, {new string('b', 64)}, {DateTime.UtcNow});
                 """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task PaymentsController_Create_RecordsMutationReceipt_ForMobileRetry()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MOBILE-PAYMENT-MUTATION-CUSTOMER",
            NameMatchKey = "MOBILEPAYMENTMUTATIONCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MOBILE-PAYMENT-MUTATION-INVOICE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == invoiceId);
        var paymentId = Guid.NewGuid();
        var mutationId = $"mobile:payment:{paymentId:N}:{Guid.NewGuid():N}";
        var mutationCreatedAtUtc = new DateTime(2026, 6, 22, 2, 3, 4, DateTimeKind.Utc);
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 22),
            Amount = 10_000m,
            Note = "mobile retry receipt",
            ExpectedRevision = storedInvoice.Revision,
            MutationId = mutationId,
            MutationCreatedAtUtc = mutationCreatedAtUtc
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var receipt = await dbContext.ProcessedSyncMutations.SingleAsync(current => current.MutationId == mutationId);
        Assert.Equal(nameof(Payment), receipt.EntityName);
        Assert.Equal(paymentId.ToString("D"), receipt.EntityId);
        Assert.Equal(storedInvoice.Revision, receipt.ExpectedRevision);
        Assert.Equal(ProcessedSyncMutationRecorder.DirectApiDeviceId, receipt.DeviceId);
        Assert.Equal(mutationCreatedAtUtc, receipt.ProcessedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", receipt.PayloadHash);
    }

    [Fact]
    public async Task PaymentsController_Create_ReplaysSameMutationAndRejectsChangedPayloadWithoutDuplicateMirror()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PAYMENT-IDEMPOTENCY-CUSTOMER",
            NameMatchKey = "PAYMENTIDEMPOTENCYCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-IDEMPOTENCY-INVOICE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 23),
            TotalAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var paymentId = Guid.NewGuid();
        var mutationId = $"direct:payment:{paymentId:N}:{Guid.NewGuid():N}";
        var controller = CreatePaymentsController(dbContext, currentUser);

        PaymentDto CreateRequest(decimal amount) => new()
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 23),
            Amount = amount,
            Note = "same retry payload",
            ExpectedRevision = storedInvoice.Revision,
            MutationId = mutationId,
            MutationCreatedAtUtc = new DateTime(2026, 7, 23, 2, 3, 4, DateTimeKind.Utc)
        };

        var first = await controller.Create(CreateRequest(10_000m), CancellationToken.None);
        var replay = await controller.Create(CreateRequest(10_000m), CancellationToken.None);
        var changed = await controller.Create(CreateRequest(20_000m), CancellationToken.None);

        Assert.IsType<OkObjectResult>(first.Result);
        var replayResult = Assert.IsType<OkObjectResult>(replay.Result);
        Assert.Equal(paymentId, Assert.IsType<PaymentDto>(replayResult.Value).Id);
        var conflict = Assert.IsType<ConflictObjectResult>(changed.Result);
        Assert.Equal(
            "mutation_id_conflict",
            Assert.IsType<DirectMutationConflictResponse>(conflict.Value).Error);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await dbContext.Payments.IgnoreQueryFilters().CountAsync(payment => payment.Id == paymentId));
        Assert.Equal(
            1,
            await dbContext.Transactions.IgnoreQueryFilters().CountAsync(transaction => transaction.Id == paymentId));
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt => receipt.MutationId == mutationId));
        Assert.Equal(
            10_000m,
            await dbContext.Payments.IgnoreQueryFilters()
                .Where(payment => payment.Id == paymentId)
                .Select(payment => payment.Amount)
                .SingleAsync());
    }

    [Fact]
    public async Task PaymentsController_Create_RejectsExistingPaymentWithoutMirrorOrMutationReceiptAndPreservesIt()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "LEGACY-PAYMENT-RETRY-CUSTOMER",
            NameMatchKey = "LEGACYPAYMENTRETRYCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "LEGACY-PAYMENT-RETRY-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 23),
            TotalAmount = 100_000m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 23),
            Amount = 12_000m,
            Note = "original legacy payment"
        });
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 24),
            Amount = 45_000m,
            Note = "retry must not overwrite"
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Contains(paymentId.ToString(), Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);

        dbContext.ChangeTracker.Clear();
        var preserved = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);
        Assert.Equal(invoiceId, preserved.InvoiceId);
        Assert.Equal(new DateOnly(2026, 7, 23), preserved.PaymentDate);
        Assert.Equal(12_000m, preserved.Amount);
        Assert.Equal("original legacy payment", preserved.Note);
        Assert.False(preserved.IsDeleted);
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == paymentId));
        Assert.False(await dbContext.ProcessedSyncMutations.AnyAsync());
    }

    [Fact]
    public async Task PaymentsController_Create_CreatesLinkedTransactionMirror()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAYMENT-MIRROR-CUSTOMER",
            NameMatchKey = "DIRECTPAYMENTMIRRORCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-PAYMENT-MIRROR-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 3),
            TotalAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var paymentId = Guid.NewGuid();
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 4),
            Amount = 45_000m,
            Note = "direct payment should create transaction"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var transaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == paymentId);
        Assert.False(transaction.IsDeleted);
        Assert.Equal(customerId, transaction.CustomerId);
        Assert.Equal(invoiceId, transaction.LinkedInvoiceId);
        Assert.Equal("DIRECT-PAYMENT-MIRROR-001", transaction.LinkedInvoiceNumber);
        Assert.Equal(new DateOnly(2026, 7, 4), transaction.TransactionDate);
        Assert.Equal(45_000m, transaction.SettlementAmount);
        Assert.Equal(45_000m, transaction.ReceiptTotal);
        Assert.Equal(45_000m, transaction.BankReceipt);
        Assert.Equal(0m, transaction.PaymentTotal);
        Assert.Equal("direct payment should create transaction", transaction.Note);
    }

    [Theory]
    [InlineData("standalone")]
    [InlineData("deleted")]
    [InlineData("cross-tenant")]
    public async Task PaymentsController_Create_RejectsAnyPreexistingTransactionWithPaymentIdWithoutOverwritingIt(
        string existingTransactionKind)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PAYMENT-TRANSACTION-COLLISION-CUSTOMER",
            NameMatchKey = "PAYMENTTRANSACTIONCOLLISIONCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-TRANSACTION-COLLISION-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 23),
            TotalAmount = 100_000m
        });

        var isDeleted = existingTransactionKind == "deleted";
        var isCrossTenant = existingTransactionKind == "cross-tenant";
        var existingTransactionCustomerId = customerId;
        if (isCrossTenant)
        {
            existingTransactionCustomerId = Guid.NewGuid();
            dbContext.Customers.Add(new Customer
            {
                Id = existingTransactionCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "CROSS-TENANT-TRANSACTION-CUSTOMER",
                NameMatchKey = "CROSSTENANTTRANSACTIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
        }

        var originalTransactionDate = new DateOnly(2025, 12, 31);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = existingTransactionCustomerId,
            TenantCode = isCrossTenant ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = isCrossTenant ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = isCrossTenant ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet,
            TransactionDate = originalTransactionDate,
            TransactionKind = $"existing-{existingTransactionKind}",
            LinkedInvoiceId = null,
            BankPayment = 77_000m,
            PaymentTotal = 77_000m,
            Note = "must remain unchanged",
            IsDeleted = isDeleted
        });
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 24),
            Amount = 45_000m,
            Note = "must not overwrite transaction"
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Contains(paymentId.ToString(), Assert.IsType<string>(conflict.Value), StringComparison.OrdinalIgnoreCase);

        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
        var preserved = await dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == paymentId);
        Assert.Equal(Guid.Empty, preserved.LinkedInvoiceId ?? Guid.Empty);
        Assert.Equal(originalTransactionDate, preserved.TransactionDate);
        Assert.Equal($"existing-{existingTransactionKind}", preserved.TransactionKind);
        Assert.Equal(77_000m, preserved.BankPayment);
        Assert.Equal(77_000m, preserved.PaymentTotal);
        Assert.Equal("must remain unchanged", preserved.Note);
        Assert.Equal(isDeleted, preserved.IsDeleted);
        Assert.Equal(
            isCrossTenant ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            preserved.TenantCode);
    }

    [Fact]
    public async Task DirectCrud_AllowsConsecutiveUpdates_WhenClientUsesReturnedRevision()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Consecutive customer",
            NameMatchKey = "CONSECUTIVECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Consecutive item",
            NameMatchKey = "CONSECUTIVEITEM",
            TrackingType = ItemTrackingTypes.NonStock
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-CONSECUTIVE-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 26),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m
        };
        var invoiceLine = new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 100m,
            Note = "initial"
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        dbContext.InvoiceLines.Add(invoiceLine);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var customerController = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var itemController = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var invoiceController = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var paymentController = CreatePaymentsController(dbContext, currentUser);

        var customerDto = (await dbContext.Customers.IgnoreQueryFilters().SingleAsync(row => row.Id == customer.Id)).ToDto();
        customerDto.ExpectedRevision = customerDto.Revision;
        customerDto.Notes = "first save";
        var savedCustomer = AssertOk(await customerController.Update(customer.Id, customerDto, CancellationToken.None));
        savedCustomer.ExpectedRevision = savedCustomer.Revision;
        savedCustomer.Notes = "second save";
        var savedCustomerAgain = AssertOk(await customerController.Update(customer.Id, savedCustomer, CancellationToken.None));
        Assert.Equal("second save", savedCustomerAgain.Notes);

        var itemDto = (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).ToDto();
        itemDto.ExpectedRevision = itemDto.Revision;
        itemDto.SimpleMemo = "first save";
        var savedItem = AssertOk(await itemController.Update(item.Id, itemDto, CancellationToken.None));
        savedItem.ExpectedRevision = savedItem.Revision;
        savedItem.SimpleMemo = "second save";
        var savedItemAgain = AssertOk(await itemController.Update(item.Id, savedItem, CancellationToken.None));
        Assert.Equal("second save", savedItemAgain.SimpleMemo);

        var invoiceDto = (await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .Include(row => row.Payments)
            .SingleAsync(row => row.Id == invoice.Id)).ToDto();
        invoiceDto.ExpectedRevision = invoiceDto.Revision;
        invoiceDto.Memo = "first save";
        var savedInvoice = AssertOk(await invoiceController.Update(invoice.Id, invoiceDto, CancellationToken.None));
        savedInvoice.ExpectedRevision = savedInvoice.Revision;
        savedInvoice.Memo = "second save";
        var savedInvoiceAgain = AssertOk(await invoiceController.Update(invoice.Id, savedInvoice, CancellationToken.None));
        Assert.Equal("second save", savedInvoiceAgain.Memo);

        var paymentDto = (await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(row => row.Invoice)
            .ThenInclude(invoiceRow => invoiceRow!.Customer)
            .Include(row => row.Attachments)
            .SingleAsync(row => row.Id == payment.Id)).ToDto();
        paymentDto.ExpectedRevision = paymentDto.Revision;
        paymentDto.Note = "first save";
        var savedPayment = AssertOk(await paymentController.Update(payment.Id, paymentDto, CancellationToken.None));
        savedPayment.ExpectedRevision = savedPayment.Revision;
        savedPayment.Note = "second save";
        var savedPaymentAgain = AssertOk(await paymentController.Update(payment.Id, savedPayment, CancellationToken.None));
        Assert.Equal("second save", savedPaymentAgain.Note);
    }

    [Fact]
    public async Task InvoicesController_Create_PreservesExtendedInvoiceLineFields()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 거래처",
            NameMatchKey = "렌탈거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 4, 13),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "A3 컬러복합기",
                    Unit = "대",
                    Quantity = 1m,
                    UnitPrice = 150000m,
                    LineAmount = 150000m,
                    SerialNumber = "SN-2603-001",
                    MaterialNumber = "2603-001",
                    InstallLocation = "2층 사무실",
                    RentalStartDate = new DateOnly(2026, 4, 1),
                    RentalEndDate = new DateOnly(2029, 3, 31),
                    ItemTrackingType = ItemTrackingTypes.Asset
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var created = Assert.IsType<InvoiceDto>(ok.Value);
        var line = Assert.Single(created.Lines);

        Assert.Equal("SN-2603-001", line.SerialNumber);
        Assert.Equal("2603-001", line.MaterialNumber);
        Assert.Equal("2층 사무실", line.InstallLocation);
        Assert.Equal(new DateOnly(2026, 4, 1), line.RentalStartDate);
        Assert.Equal(new DateOnly(2029, 3, 31), line.RentalEndDate);

        var storedLine = await dbContext.InvoiceLines.IgnoreQueryFilters().SingleAsync(x => x.Id == lineId);
        Assert.Equal("SN-2603-001", storedLine.SerialNumber);
        Assert.Equal("2603-001", storedLine.MaterialNumber);
        Assert.Equal("2층 사무실", storedLine.InstallLocation);
        Assert.Equal(new DateOnly(2026, 4, 1), storedLine.RentalStartDate);
        Assert.Equal(new DateOnly(2029, 3, 31), storedLine.RentalEndDate);
    }

    [Fact]
    public async Task InvoicesController_Create_IgnoresDeletedLinesWhenCalculatingTotals()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted Line Total Customer",
            NameMatchKey = "DELETEDLINETOTALCUSTOMER",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var activeLineId = Guid.NewGuid();
        var deletedLineId = Guid.NewGuid();
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 19),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = deletedLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "삭제 라인",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 90000m,
                    LineAmount = 90000m,
                    OrderIndex = 1,
                    IsDeleted = true
                },
                new InvoiceLineDto
                {
                    Id = activeLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "활성 라인",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 10000m,
                    LineAmount = 10000m,
                    OrderIndex = 2
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var created = Assert.IsType<InvoiceDto>(ok.Value);

        Assert.Equal(10000m, created.SupplyAmount);
        Assert.Equal(0m, created.VatAmount);
        Assert.Equal(10000m, created.TotalAmount);
        var line = Assert.Single(created.Lines);
        Assert.Equal(activeLineId, line.Id);

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(10000m, storedInvoice.SupplyAmount);
        Assert.Equal(0m, storedInvoice.VatAmount);
        Assert.Equal(10000m, storedInvoice.TotalAmount);
        Assert.DoesNotContain(storedInvoice.Lines, current => current.Id == deletedLineId);
        Assert.Equal(storedInvoice.TotalAmount, storedInvoice.Lines.Where(lineRow => !lineRow.IsDeleted).Sum(lineRow => lineRow.LineAmount) + storedInvoice.VatAmount);
    }

    [Fact]
    public async Task InvoicesController_Create_RenumbersActiveLinesByPayloadOrder()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Invoice Line Order Customer",
            NameMatchKey = "INVOICELINEORDERCUSTOMER",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var firstPayloadLineId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var secondPayloadLineId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 20),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = firstPayloadLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "payload first",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m,
                    OrderIndex = 50
                },
                new InvoiceLineDto
                {
                    Id = secondPayloadLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "payload second",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 2000m,
                    LineAmount = 2000m,
                    OrderIndex = 50
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var created = Assert.IsType<InvoiceDto>(ok.Value);

        Assert.Equal(new[] { "payload first", "payload second" }, created.Lines.Select(line => line.ItemNameOriginal).ToArray());
        Assert.Equal(new[] { 1, 2 }, created.Lines.Select(line => line.OrderIndex).ToArray());

        var storedLines = await dbContext.InvoiceLines
            .AsNoTracking()
            .Where(line => line.InvoiceId == invoiceId)
            .OrderBy(line => line.OrderIndex)
            .ToListAsync();
        Assert.Equal(new[] { "payload first", "payload second" }, storedLines.Select(line => line.ItemNameOriginal).ToArray());
        Assert.Equal(new[] { 1, 2 }, storedLines.Select(line => line.OrderIndex).ToArray());
    }

    [Fact]
    public async Task InvoicesController_Create_ForbidsOutOfScopeItemLine()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed invoice customer",
            NameMatchKey = "ALLOWEDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope invoice item",
            NameMatchKey = "OUTOFSCOPEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = outOfScopeItem.Id,
                    ItemNameOriginal = outOfScopeItem.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == dto.Id));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task InvoicesController_Create_ForbidsReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed invoice rental customer",
            NameMatchKey = "ALLOWEDINVOICERENTALCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-INVOICE-PROFILE",
            CustomerName = "Read shared invoice profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            LinkedRentalBillingProfileId = readSharedProfile.Id,
            LinkedRentalBillingRunId = Guid.NewGuid()
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == dto.Id));
    }

    [Fact]
    public async Task InvoicesController_Create_ForbidsReadSharedCustomerReference()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-INVOICE-CUSTOMER",
            NameMatchKey = "READSHAREDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = readSharedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 19),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == dto.Id));
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsOutOfScopeItemLine()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Legacy invoice hidden item customer",
            NameMatchKey = "LEGACYINVOICEHIDDENITEMCUSTOMER",
            TradeType = "Sales"
        };
        var hiddenItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Hidden delete invoice item",
            NameMatchKey = "HIDDENDELETEINVOICEITEM",
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
            InvoiceNumber = "LEGACY-HIDDEN-ITEM-INVOICE",
            InvoiceDate = new DateOnly(2026, 6, 19),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
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
                    OrderIndex = 1
                }
            ]
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(hiddenItem);
        dbContext.Invoices.Add(invoice);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = hiddenItem.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(row => row.Id == invoice.Id);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoice.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.Equal(4m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == hiddenItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(4m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == hiddenItem.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceLineWithOutOfScopeItem()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice customer",
            NameMatchKey = "ALLOWEDSYNCINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope sync invoice item",
            NameMatchKey = "OUTOFSCOPESYNCINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-out-of-scope-item-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        Lines =
                        [
                            new InvoiceLineDto
                            {
                                Id = Guid.NewGuid(),
                                InvoiceId = invoiceId,
                                ItemId = outOfScopeItem.Id,
                                ItemNameOriginal = outOfScopeItem.NameOriginal,
                                ItemTrackingType = ItemTrackingTypes.Stock,
                                Unit = "EA",
                                Quantity = 1m,
                                UnitPrice = 100m,
                                LineAmount = 100m
                            }
                        ]
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced item is outside the readable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceLineWithOutOfScopeItem_WhenCustomerRelinkedByName()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Relinked sync invoice customer",
            NameMatchKey = MatchKeyNormalizer.Normalize("Relinked sync invoice customer"),
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope relinked invoice item",
            NameMatchKey = "OUTOFSCOPERELINKEDINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-relinked-out-of-scope-item-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = Guid.NewGuid(),
                        CustomerName = customer.NameOriginal,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        Lines =
                        [
                            new InvoiceLineDto
                            {
                                Id = Guid.NewGuid(),
                                InvoiceId = invoiceId,
                                ItemId = outOfScopeItem.Id,
                                ItemNameOriginal = outOfScopeItem.NameOriginal,
                                ItemTrackingType = ItemTrackingTypes.Stock,
                                Unit = "EA",
                                Quantity = 1m,
                                UnitPrice = 100m,
                                LineAmount = 100m
                            }
                        ]
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced item is outside the readable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceWithReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice rental customer",
            NameMatchKey = "ALLOWEDSYNCINVOICERENTALCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-SYNC-INVOICE-PROFILE",
            CustomerName = "Read shared sync invoice profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-read-shared-rental-link-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        LinkedRentalBillingProfileId = readSharedProfile.Id,
                        LinkedRentalBillingRunId = Guid.NewGuid()
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceWithReadSharedCustomerReference()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-SYNC-INVOICE-CUSTOMER",
            NameMatchKey = "READSHAREDSYNCINVOICECUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-read-shared-customer-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = readSharedCustomer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 19),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced customer is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
    }

    [Fact]
    public async Task SyncPush_RejectsTransactionWithReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync rental payment customer",
            NameMatchKey = "ALLOWEDSYNCRENTALPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-SYNC-TRANSACTION-PROFILE",
            CustomerName = "Read shared sync transaction profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-rental-link-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 17),
                        TransactionKind = "렌탈수금",
                        LinkedRentalBillingProfileId = readSharedProfile.Id,
                        LinkedRentalBillingRunId = Guid.NewGuid(),
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(row => row.Id == transactionId));
    }

    [Fact]
    public async Task SyncPush_RejectsTransactionWithReadSharedCustomerReference()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-SYNC-TRANSACTION-CUSTOMER",
            NameMatchKey = "READSHAREDSYNCTRANSACTIONCUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-customer-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = readSharedCustomer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 19),
                        TransactionKind = "GeneralReceipt",
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced customer is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(row => row.Id == transactionId));
    }

    [Fact]
    public async Task SyncPush_RejectsTransactionWithReadSharedInvoiceLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice payment customer",
            NameMatchKey = "ALLOWEDSYNCINVOICEPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(readSharedInvoice);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareInvoices = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-invoice-link-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 17),
                        TransactionKind = "전표수금",
                        LinkedInvoiceId = readSharedInvoice.Id,
                        LinkedInvoiceNumber = "READ-SHARED-INV-001",
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced invoice is outside the writable payment office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(row => row.Id == transactionId));
    }

    [Fact]
    public async Task InvoicesController_SalesCreateUpdateDelete_AdjustsWarehouseStockSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Stock customer",
            NameMatchKey = "STOCKCUSTOMER",
            TradeType = "매출"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Stock item",
            NameMatchKey = "STOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var createDto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 21),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        };

        var createResponse = await controller.Create(createDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(3m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(3m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].Quantity = 1m;
        updateDto.Lines[0].LineAmount = 1000m;

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(updateResponse.Result);
        Assert.Equal(4m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(4m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var latestInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var deleteResponse = await controller.Delete(invoiceId, latestInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId));
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.InvoiceId == invoiceId)
            .AllAsync(line => line.IsDeleted));
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsLinkedPayments_WhenPaymentEditMissing()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-only-delete",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-INVOICE-DELETE-PAYMENT-PERM-CUSTOMER",
            NameMatchKey = "DIRECTINVOICEDELETEPAYMENTPERMCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-INVOICE-DELETE-PAYMENT-PERM",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 40_000m,
            Note = "direct delete linked payment"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(scenario.InvoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == scenario.InvoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == scenario.PaymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.InvoiceId, unchangedTransaction.LinkedInvoiceId);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task InvoicesController_Delete_RentalBillingInvoice_RevertsSettlementAndDeletesLinkedPayments()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var paymentAttachmentId = Guid.NewGuid();
        var invoiceNumber = "RENTAL-DEL-001";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental delete customer",
            NameMatchKey = "SERVERRENTALDELETECUSTOMER",
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
            CustomerName = "Server rental delete customer",
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
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Rental billing item",
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
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = paymentAttachmentId,
            PaymentId = transactionId,
            AttachmentType = "입금증빙",
            FileName = "rental-payment-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "test-hash",
            UploadedAtUtc = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc),
            FileContent = [0x25, 0x50, 0x44, 0x46]
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(invoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        var deletedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.True(deletedInvoice.IsDeleted);
        var deletedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == transactionId);
        Assert.True(deletedPayment.IsDeleted);
        var deletedAttachment = await dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(attachment => attachment.Id == paymentAttachmentId);
        Assert.True(deletedAttachment.IsDeleted);
        var detachedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(detachedTransaction.LinkedInvoiceId);
        Assert.Equal(0m, detachedTransaction.SettlementAmount);
        Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
        Assert.Equal("일반수금", detachedTransaction.TransactionKind);
        Assert.False(detachedTransaction.IsDeleted);
        var revertedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, revertedProfile.SettledAmount);
        Assert.Equal(0m, revertedProfile.OutstandingAmount);
        Assert.Equal("\uBBF8\uC644\uB8CC", revertedProfile.CompletionStatus);
        Assert.Null(revertedProfile.LastBilledDate);
        Assert.Empty(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(revertedProfile.BillingRunsJson) ?? []);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_PreservesCancelledRunWhenOutstandingRemains()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental cancelled customer",
            NameMatchKey = "SERVERRENTALCANCELLEDCUSTOMER",
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
            CustomerName = "Server rental cancelled customer",
            BillingStatus = "취소",
            SettlementStatus = "확인대기",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "취소",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "확인대기"
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
            InvoiceNumber = "RENTAL-CANCEL-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, recalculatedProfile.SettledAmount);
        Assert.Equal(100_000m, recalculatedProfile.OutstandingAmount);
        Assert.Equal("취소", recalculatedProfile.BillingStatus);
        var recalculatedRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? []);
        Assert.Equal("취소", recalculatedRun.Status);
        Assert.Equal(0m, recalculatedRun.SettledAmount);
        Assert.Equal("확인대기", recalculatedRun.SettlementStatus);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_PreservesUnknownRunMetadataWhileFinancialEvidenceWins()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        var transactionId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingStatus = "취소";
        profile.SettledAmount = 888_000m;
        profile.OutstandingAmount = 111_000m;
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                RunKey = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "취소",
                BilledAmount = 999_000m,
                SettledAmount = 888_000m,
                SettlementStatus = "fake-settlement",
                SettledDate = new DateOnly(2026, 7, 1),
                Note = "manual operator note must survive",
                Items = new[]
                {
                    new { ItemId = Guid.NewGuid(), Name = "preserved rental item", Quantity = 2 }
                }
            }
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(100_000m, recalculated.SettledAmount);
        Assert.Equal(0m, recalculated.OutstandingAmount);
        Assert.Equal("취소", recalculated.BillingStatus);
        using var runsDocument = JsonDocument.Parse(recalculated.BillingRunsJson);
        var run = Assert.Single(runsDocument.RootElement.EnumerateArray());
        Assert.Equal(100_000m, run.GetProperty("BilledAmount").GetDecimal());
        Assert.Equal(100_000m, run.GetProperty("SettledAmount").GetDecimal());
        Assert.Equal("완료", run.GetProperty("Status").GetString());
        Assert.Equal("manual operator note must survive", run.GetProperty("Note").GetString());
        var item = Assert.Single(run.GetProperty("Items").EnumerateArray());
        Assert.Equal("preserved rental item", item.GetProperty("Name").GetString());
        Assert.Equal(2, item.GetProperty("Quantity").GetInt32());
    }

    [Theory]
    [InlineData("보류", false, true)]
    [InlineData("취소", false, false)]
    [InlineData("취소", true, true)]
    public async Task InvoicesController_Delete_LastRentalEvidence_PreservesManualMetadataAndNeutralizesFinancials(
        string manualStatus,
        bool existingRequiresFollowUp,
        bool expectedRequiresFollowUp)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(
            dbContext,
            existingPaymentAmount: 55_000m,
            storedSettledAmount: 55_000m);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            BankReceipt = 55_000m,
            ReceiptTotal = 55_000m,
            SettlementAmount = 55_000m
        });
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingStatus = manualStatus;
        profile.SettlementStatus = "부분입금";
        profile.CompletionStatus = "미완료";
        profile.SettledAmount = 55_000m;
        profile.OutstandingAmount = 45_000m;
        profile.LastBilledDate = new DateOnly(2026, 7, 25);
        profile.LastSettledDate = new DateOnly(2026, 7, 26);
        profile.RequiresFollowUp = existingRequiresFollowUp;
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                RunKey = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = manualStatus,
                BilledAmount = 100_000m,
                SettledAmount = 55_000m,
                SettlementStatus = "부분입금",
                SettledDate = new DateOnly(2026, 7, 26),
                Note = "manual stop note must survive evidence deletion",
                Items = new[]
                {
                    new { Name = "preserved stopped item", Quantity = 3 }
                }
            }
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Delete(
            scenario.InvoiceId,
            storedInvoice.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(manualStatus, recalculated.BillingStatus);
        Assert.Equal("확인대기", recalculated.SettlementStatus);
        Assert.Equal("미완료", recalculated.CompletionStatus);
        Assert.Equal(0m, recalculated.SettledAmount);
        Assert.Equal(0m, recalculated.OutstandingAmount);
        Assert.Null(recalculated.LastBilledDate);
        Assert.Null(recalculated.LastSettledDate);
        Assert.Equal(expectedRequiresFollowUp, recalculated.RequiresFollowUp);
        using var runsDocument = JsonDocument.Parse(recalculated.BillingRunsJson);
        var preservedRun = Assert.Single(runsDocument.RootElement.EnumerateArray());
        Assert.Equal(scenario.RunId, preservedRun.GetProperty("RunId").GetGuid());
        Assert.Equal("2026-07", preservedRun.GetProperty("RunKey").GetString());
        Assert.Equal("2026-07-25", preservedRun.GetProperty("ScheduledDate").GetString());
        Assert.Equal("2026-07-01", preservedRun.GetProperty("PeriodStartDate").GetString());
        Assert.Equal("2026-07-31", preservedRun.GetProperty("PeriodEndDate").GetString());
        Assert.Equal(manualStatus, preservedRun.GetProperty("Status").GetString());
        Assert.Equal(0m, preservedRun.GetProperty("BilledAmount").GetDecimal());
        Assert.Equal(0m, preservedRun.GetProperty("SettledAmount").GetDecimal());
        Assert.Equal("확인대기", preservedRun.GetProperty("SettlementStatus").GetString());
        Assert.Equal(JsonValueKind.Null, preservedRun.GetProperty("SettledDate").ValueKind);
        Assert.Equal(
            "manual stop note must survive evidence deletion",
            preservedRun.GetProperty("Note").GetString());
        var preservedItem = Assert.Single(preservedRun.GetProperty("Items").EnumerateArray());
        Assert.Equal("preserved stopped item", preservedItem.GetProperty("Name").GetString());
        Assert.Equal(3, preservedItem.GetProperty("Quantity").GetInt32());

        var deletedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.True(deletedPayment.IsDeleted);
        var detachedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.Null(detachedTransaction.LinkedInvoiceId);
        Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(0m, detachedTransaction.SettlementAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_UnpaidInvoiceKeepsLaterActiveRunLastBilledDate()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var service = new RentalSettlementRecalculationService(dbContext);

        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(new DateOnly(2026, 7, 25), recalculated.LastBilledDate);

        var augustRunId = Guid.NewGuid();
        var augustInvoiceId = Guid.NewGuid();
        var runs = JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculated.BillingRunsJson) ?? [];
        runs.Add(new ServerRentalBillingRunSnapshot
        {
            RunId = augustRunId,
            RunKey = "2026-08",
            ScheduledDate = new DateOnly(2026, 8, 25),
            PeriodStartDate = new DateOnly(2026, 8, 1),
            PeriodEndDate = new DateOnly(2026, 8, 31),
            CycleMonths = 1,
            PeriodLabel = "2026-08",
            Status = "청구중",
            BilledAmount = 200_000m
        });
        recalculated.BillingRunsJson = JsonSerializer.Serialize(runs);
        dbContext.Invoices.Add(new Invoice
        {
            Id = augustInvoiceId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-LATER-ACTIVE-002",
            VersionGroupId = augustInvoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = 200_000m,
            SupplyAmount = 181_818m,
            VatAmount = 18_182m,
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = augustRunId
        });
        await dbContext.SaveChangesAsync();
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            new DateOnly(2026, 8, 25),
            await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(current => current.Id == scenario.ProfileId)
                .Select(current => current.LastBilledDate)
                .SingleAsync());
    }

    [Theory]
    [InlineData("min-value")]
    [InlineData("reversed-period")]
    public async Task RentalSettlementRecalculation_RepairsInvalidExistingRunScheduleWithoutLosingManualMetadata(
        string invalidShape)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingDay = 25;
        profile.BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
        profile.BillingCycleMonths = 3;
        profile.BillingAnchorMonth = 7;
        profile.BillingStatus = "보류";
        profile.RequiresFollowUp = true;
        profile.LastBilledDate = null;
        var usesMinValue = invalidShape == "min-value";
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = scenario.RunId,
                RunKey = usesMinValue ? string.Empty : "invalid-period",
                ScheduledDate = usesMinValue ? DateOnly.MinValue : new DateOnly(2026, 8, 25),
                PeriodStartDate = usesMinValue ? DateOnly.MinValue : new DateOnly(2026, 9, 30),
                PeriodEndDate = usesMinValue ? DateOnly.MinValue : new DateOnly(2026, 7, 1),
                CycleMonths = 0,
                PeriodLabel = usesMinValue ? string.Empty : "invalid",
                Status = "보류",
                BilledAmount = 777_000m,
                SettledAmount = 123_000m,
                SettlementStatus = "fake",
                SettledDate = new DateOnly(2026, 1, 1),
                Note = "invalid schedule repair must preserve note",
                Items = new[]
                {
                    new { Name = "preserved schedule item", Quantity = 4 }
                }
            }
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal("보류", recalculated.BillingStatus);
        Assert.Equal(100_000m, recalculated.OutstandingAmount);
        Assert.Equal(new DateOnly(2026, 9, 25), recalculated.LastBilledDate);
        using var runsDocument = JsonDocument.Parse(recalculated.BillingRunsJson);
        var run = Assert.Single(runsDocument.RootElement.EnumerateArray());
        Assert.Equal("20260701-20260930", run.GetProperty("RunKey").GetString());
        Assert.Equal("2026-09-25", run.GetProperty("ScheduledDate").GetString());
        Assert.Equal("2026-07-01", run.GetProperty("PeriodStartDate").GetString());
        Assert.Equal("2026-09-30", run.GetProperty("PeriodEndDate").GetString());
        Assert.Equal(3, run.GetProperty("CycleMonths").GetInt32());
        Assert.Equal("2026-07 ~ 2026-09", run.GetProperty("PeriodLabel").GetString());
        Assert.Equal("보류", run.GetProperty("Status").GetString());
        Assert.Equal(100_000m, run.GetProperty("BilledAmount").GetDecimal());
        Assert.Equal(0m, run.GetProperty("SettledAmount").GetDecimal());
        Assert.Equal("invalid schedule repair must preserve note", run.GetProperty("Note").GetString());
        var item = Assert.Single(run.GetProperty("Items").EnumerateArray());
        Assert.Equal("preserved schedule item", item.GetProperty("Name").GetString());
        Assert.Equal(4, item.GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RecalculatesEveryActiveRunForSameProfileInOneCall()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        var secondRunId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingDay = 25;
        profile.BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
        profile.BillingCycleMonths = 1;
        profile.BillingAnchorMonth = 7;
        profile.LastBilledDate = null;
        profile.BillingRunsJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                RunId = scenario.RunId,
                RunKey = string.Empty,
                ScheduledDate = DateOnly.MinValue,
                PeriodStartDate = DateOnly.MinValue,
                PeriodEndDate = DateOnly.MinValue,
                CycleMonths = 0,
                PeriodLabel = string.Empty,
                Status = "보류",
                BilledAmount = 777_000m,
                SettledAmount = 111_000m,
                SettlementStatus = "fake-a",
                SettledDate = new DateOnly(2026, 1, 1),
                Note = "run-a metadata",
                Items = new[] { new { Name = "run-a item", Quantity = 1 } }
            },
            new
            {
                RunId = secondRunId,
                RunKey = string.Empty,
                ScheduledDate = DateOnly.MinValue,
                PeriodStartDate = DateOnly.MinValue,
                PeriodEndDate = DateOnly.MinValue,
                CycleMonths = 0,
                PeriodLabel = string.Empty,
                Status = "취소",
                BilledAmount = 888_000m,
                SettledAmount = 222_000m,
                SettlementStatus = "fake-b",
                SettledDate = new DateOnly(2026, 2, 2),
                Note = "run-b metadata",
                Items = new[] { new { Name = "run-b item", Quantity = 2 } }
            }
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = secondInvoiceId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-MULTI-RUN-002",
            VersionGroupId = secondInvoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = 200_000m,
            SupplyAmount = 181_818m,
            VatAmount = 18_182m,
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = secondRunId
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId), (scenario.ProfileId, secondRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(0m, recalculated.SettledAmount);
        Assert.Equal(200_000m, recalculated.OutstandingAmount);
        Assert.Equal(new DateOnly(2026, 8, 25), recalculated.LastBilledDate);
        using var runsDocument = JsonDocument.Parse(recalculated.BillingRunsJson);
        var runs = runsDocument.RootElement.EnumerateArray()
            .ToDictionary(run => run.GetProperty("RunId").GetGuid());

        var firstRun = runs[scenario.RunId];
        Assert.Equal("2026-07-25", firstRun.GetProperty("ScheduledDate").GetString());
        Assert.Equal("2026-07-01", firstRun.GetProperty("PeriodStartDate").GetString());
        Assert.Equal("2026-07-31", firstRun.GetProperty("PeriodEndDate").GetString());
        Assert.Equal(100_000m, firstRun.GetProperty("BilledAmount").GetDecimal());
        Assert.Equal(0m, firstRun.GetProperty("SettledAmount").GetDecimal());
        Assert.Equal("보류", firstRun.GetProperty("Status").GetString());
        Assert.Equal("run-a metadata", firstRun.GetProperty("Note").GetString());
        Assert.Equal("run-a item", Assert.Single(firstRun.GetProperty("Items").EnumerateArray()).GetProperty("Name").GetString());

        var secondRun = runs[secondRunId];
        Assert.Equal("2026-08-25", secondRun.GetProperty("ScheduledDate").GetString());
        Assert.Equal("2026-08-01", secondRun.GetProperty("PeriodStartDate").GetString());
        Assert.Equal("2026-08-31", secondRun.GetProperty("PeriodEndDate").GetString());
        Assert.Equal(200_000m, secondRun.GetProperty("BilledAmount").GetDecimal());
        Assert.Equal(0m, secondRun.GetProperty("SettledAmount").GetDecimal());
        Assert.Equal("취소", secondRun.GetProperty("Status").GetString());
        Assert.Equal("run-b metadata", secondRun.GetProperty("Note").GetString());
        Assert.Equal("run-b item", Assert.Single(secondRun.GetProperty("Items").EnumerateArray()).GetProperty("Name").GetString());
    }

    [Fact]
    public async Task RentalSettlementRecalculation_JulyTargetKeepsTopSummaryOnLatestActiveAugustRun()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        var augustRunId = Guid.NewGuid();
        var augustInvoiceId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.RequiresFollowUp = false;
        profile.LastBilledDate = null;
        profile.LastSettledDate = null;
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new ServerRentalBillingRunSnapshot
            {
                RunId = scenario.RunId,
                RunKey = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "청구중",
                BilledAmount = 100_000m,
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = augustRunId,
                RunKey = "2026-08",
                ScheduledDate = new DateOnly(2026, 8, 25),
                PeriodStartDate = new DateOnly(2026, 8, 1),
                PeriodEndDate = new DateOnly(2026, 8, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-08",
                Status = "청구중",
                BilledAmount = 200_000m
            }
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = augustInvoiceId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-AUGUST-TOP-002",
            VersionGroupId = augustInvoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = 200_000m,
            SupplyAmount = 181_818m,
            VatAmount = 18_182m,
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = augustRunId
        });
        dbContext.Transactions.AddRange(
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 7, 26),
                TransactionKind = "렌탈수금",
                LinkedInvoiceId = scenario.InvoiceId,
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = scenario.RunId,
                BankReceipt = 40_000m,
                ReceiptTotal = 40_000m,
                SettlementAmount = 40_000m
            },
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 8, 26),
                TransactionKind = "렌탈수금",
                LinkedInvoiceId = augustInvoiceId,
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = augustRunId,
                BankReceipt = 200_000m,
                ReceiptTotal = 200_000m,
                SettlementAmount = 200_000m
            });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(200_000m, recalculated.SettledAmount);
        Assert.Equal(0m, recalculated.OutstandingAmount);
        Assert.Equal("입금확인", recalculated.SettlementStatus);
        Assert.Equal("완료", recalculated.CompletionStatus);
        Assert.Equal("완료", recalculated.BillingStatus);
        Assert.Equal(new DateOnly(2026, 8, 26), recalculated.LastSettledDate);
        Assert.Equal(new DateOnly(2026, 8, 25), recalculated.LastBilledDate);
        Assert.True(recalculated.RequiresFollowUp);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RemovingLatestInactiveRunRewindsLastBilledToRemainingActiveRun()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        var augustRunId = Guid.NewGuid();
        var deletedAugustInvoiceId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.LastBilledDate = new DateOnly(2026, 8, 25);
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new ServerRentalBillingRunSnapshot
            {
                RunId = scenario.RunId,
                RunKey = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "청구중",
                BilledAmount = 100_000m,
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = augustRunId,
                RunKey = "2026-08",
                ScheduledDate = new DateOnly(2026, 8, 25),
                PeriodStartDate = new DateOnly(2026, 8, 1),
                PeriodEndDate = new DateOnly(2026, 8, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-08",
                Status = "청구중",
                BilledAmount = 200_000m
            }
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = deletedAugustInvoiceId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-DELETED-AUGUST-002",
            VersionGroupId = deletedAugustInvoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            IsDeleted = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = 200_000m,
            SupplyAmount = 181_818m,
            VatAmount = 18_182m,
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = augustRunId
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, augustRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(new DateOnly(2026, 7, 25), recalculated.LastBilledDate);
        var remainingRun = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculated.BillingRunsJson) ?? []);
        Assert.Equal(scenario.RunId, remainingRun.RunId);
        Assert.Equal(100_000m, remainingRun.BilledAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RemovesLegacyRunKeyCompanionsWithInactiveRunIdentityGroup()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        var invoice = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.InvoiceId);
        invoice.IsDeleted = true;
        var unrelatedRunId = Guid.NewGuid();
        var tombstonedRunId = Guid.NewGuid();
        var tombstonedAtUtc = new DateTime(2026, 8, 4, 1, 2, 3, DateTimeKind.Utc);
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new ServerRentalBillingRunSnapshot
            {
                RunId = scenario.RunId,
                RunKey = "Billing-Cycle-2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                CycleMonths = 1,
                Status = "\uCCAD\uAD6C\uC911",
                BilledAmount = 100_000m,
                SettlementStatus = "\uD655\uC778\uB300\uAE30"
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = Guid.Empty,
                RunKey = " billing-[ Cycle ]-2026-07 ",
                ScheduledDate = new DateOnly(2026, 7, 25),
                CycleMonths = 1,
                Status = "\uCCAD\uAD6C\uC911",
                BilledAmount = 100_000m,
                SettlementStatus = "\uD655\uC778\uB300\uAE30"
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = unrelatedRunId,
                RunKey = "unrelated-2026-08",
                ScheduledDate = new DateOnly(2026, 8, 25),
                CycleMonths = 1,
                Status = "\uC608\uC815",
                SettlementStatus = "\uD655\uC778\uB300\uAE30"
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = tombstonedRunId,
                RunKey = "tombstoned-2026-06",
                ScheduledDate = new DateOnly(2026, 6, 25),
                CycleMonths = 1,
                Status = "\uCDE8\uC18C",
                SettlementStatus = "\uD655\uC778\uB300\uAE30",
                IsTombstoned = true,
                TombstonedAtUtc = tombstonedAtUtc,
                TombstonedByUsername = "operator"
            }
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        var remainingRuns = JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(
            recalculatedProfile.BillingRunsJson) ?? [];
        Assert.DoesNotContain(remainingRuns, current => current.RunId == scenario.RunId);
        Assert.DoesNotContain(remainingRuns, current =>
            current.RunId == Guid.Empty &&
            RentalDuplicateNormalizer.NormalizeProfileKeyPart(current.RunKey) ==
            RentalDuplicateNormalizer.NormalizeProfileKeyPart("Billing-Cycle-2026-07"));
        Assert.Contains(remainingRuns, current => current.RunId == unrelatedRunId);
        var tombstone = Assert.Single(remainingRuns, current => current.RunId == tombstonedRunId);
        Assert.True(tombstone.IsTombstoned);
        Assert.Equal(tombstonedAtUtc, tombstone.TombstonedAtUtc);
        Assert.Equal("operator", tombstone.TombstonedByUsername);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_LastEvidenceRemovalKeepsFutureZeroEvidenceRunPlanned()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        var augustRunId = Guid.NewGuid();
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingStatus = "청구중";
        profile.LastBilledDate = new DateOnly(2026, 7, 25);
        profile.BillingRunsJson = JsonSerializer.Serialize(new[]
        {
            new ServerRentalBillingRunSnapshot
            {
                RunId = scenario.RunId,
                RunKey = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "청구중",
                BilledAmount = 100_000m
            },
            new ServerRentalBillingRunSnapshot
            {
                RunId = augustRunId,
                RunKey = "2026-08",
                ScheduledDate = new DateOnly(2026, 8, 25),
                PeriodStartDate = new DateOnly(2026, 8, 1),
                PeriodEndDate = new DateOnly(2026, 8, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-08",
                Status = "예정",
                BilledAmount = 100_000m
            }
        });
        var julyInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        julyInvoice.IsDeleted = true;
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal("예정", recalculated.BillingStatus);
        Assert.Equal(0m, recalculated.SettledAmount);
        Assert.Equal(0m, recalculated.OutstandingAmount);
        Assert.Equal("미완료", recalculated.CompletionStatus);
        Assert.Null(recalculated.LastBilledDate);
        Assert.Null(recalculated.LastSettledDate);
        Assert.False(recalculated.RequiresFollowUp);
        var remainingRun = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculated.BillingRunsJson) ?? []);
        Assert.Equal(augustRunId, remainingRun.RunId);
        Assert.Equal("예정", remainingRun.Status);
        Assert.Equal(0m, remainingRun.BilledAmount);
        Assert.Equal(0m, remainingRun.SettledAmount);
        Assert.Null(remainingRun.SettledDate);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(100000, true, false)]
    public async Task RentalSettlementRecalculation_DerivesRequiresFollowUpFromAllActiveOutstanding(
        decimal settledAmount,
        bool existingRequiresFollowUp,
        bool expectedRequiresFollowUp)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        if (settledAmount > 0m)
        {
            dbContext.Transactions.Add(new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 7, 26),
                TransactionKind = "렌탈수금",
                LinkedInvoiceId = scenario.InvoiceId,
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = scenario.RunId,
                BankReceipt = settledAmount,
                ReceiptTotal = settledAmount,
                SettlementAmount = settledAmount
            });
        }
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.RequiresFollowUp = existingRequiresFollowUp;
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            expectedRequiresFollowUp,
            await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .Where(current => current.Id == scenario.ProfileId)
                .Select(current => current.RequiresFollowUp)
                .SingleAsync());
    }

    [Theory]
    [InlineData("보류", false, true)]
    [InlineData("취소", false, false)]
    [InlineData("취소", true, true)]
    public async Task RentalSettlementRecalculation_CompletedEvidencePreservesProfileManualStopPolicy(
        string manualStatus,
        bool existingRequiresFollowUp,
        bool expectedRequiresFollowUp)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        profile.BillingStatus = manualStatus;
        profile.RequiresFollowUp = existingRequiresFollowUp;
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(manualStatus, recalculated.BillingStatus);
        Assert.Equal(expectedRequiresFollowUp, recalculated.RequiresFollowUp);
        var run = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculated.BillingRunsJson) ?? []);
        Assert.Equal("완료", run.Status);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_MixedProfileAndSpecificTargetsKeepProfileAggregateSummary()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        dbContext.Transactions.AddRange(
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 7, 26),
                TransactionKind = "렌탈수금",
                LinkedInvoiceId = scenario.InvoiceId,
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = scenario.RunId,
                BankReceipt = 40_000m,
                ReceiptTotal = 40_000m,
                SettlementAmount = 40_000m
            },
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 8, 1),
                TransactionKind = "렌탈수금",
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = null,
                BankReceipt = 30_000m,
                ReceiptTotal = 30_000m,
                SettlementAmount = 30_000m
            });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, (Guid?)null), (scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculated = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(70_000m, recalculated.SettledAmount);
        Assert.Equal(30_000m, recalculated.OutstandingAmount);
        Assert.Equal("부분입금", recalculated.SettlementStatus);
        Assert.Equal("미완료", recalculated.CompletionStatus);
        Assert.Equal("청구중", recalculated.BillingStatus);
        Assert.Equal(new DateOnly(2026, 8, 1), recalculated.LastSettledDate);
        var run = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculated.BillingRunsJson) ?? []);
        Assert.Equal(40_000m, run.SettledAmount);
        Assert.Equal(60_000m, 100_000m - run.SettledAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RestoresConfiguredQuarterlyPeriodFromLinkedInvoice()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental missing run customer",
            NameMatchKey = "SERVERRENTALMISSINGRUNCUSTOMER",
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
            CustomerName = "Server rental missing run customer",
            BillingStatus = "청구중",
            SettlementStatus = "확인대기",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 3,
            BillingAnchorMonth = 7,
            SettledAmount = 0m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-MISSING-RUN-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 13),
            TotalAmount = 396_000m,
            SupplyAmount = 396_000m,
            VatAmount = 0m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 8, 26),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-MISSING-RUN-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(40_000m, recalculatedProfile.SettledAmount);
        Assert.Equal(356_000m, recalculatedProfile.OutstandingAmount);
        Assert.Equal("미완료", recalculatedProfile.CompletionStatus);

        var restoredRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? [], current => current.RunId == runId);
        Assert.Equal("20260701-20260930", restoredRun.RunKey);
        Assert.Equal(new DateOnly(2026, 9, 25), restoredRun.ScheduledDate);
        Assert.Equal(new DateOnly(2026, 7, 1), restoredRun.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 9, 30), restoredRun.PeriodEndDate);
        Assert.Equal(3, restoredRun.CycleMonths);
        Assert.Equal("2026-07 ~ 2026-09", restoredRun.PeriodLabel);
        Assert.Equal(396_000m, restoredRun.BilledAmount);
        Assert.Equal(40_000m, restoredRun.SettledAmount);
        Assert.Equal(new DateOnly(2026, 8, 26), restoredRun.SettledDate);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RestoresLegacyRunFromSingleBracketedBillingMonth()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var juneRunId = Guid.NewGuid();
        var legacyMayRunId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental legacy month customer",
            NameMatchKey = "SERVERRENTALLEGACYMONTHCUSTOMER",
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
            CustomerName = "Server rental legacy month customer",
            BillingStatus = "청구중",
            SettlementStatus = "확인대기",
            CompletionStatus = "미완료",
            MonthlyAmount = 363_000m,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = juneRunId,
                    RunKey = "20260601-20260630",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    CycleMonths = 1,
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 363_000m,
                    SettlementStatus = "확인대기"
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
            InvoiceNumber = "RENTAL-LEGACY-MAY-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 4),
            TotalAmount = 363_000m,
            SupplyAmount = 330_000m,
            VatAmount = 33_000m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = legacyMayRunId,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemNameOriginal = "사무기기 렌탈대금[5월]",
                    Unit = "식",
                    Quantity = 1m,
                    UnitPrice = 363_000m,
                    LineAmount = 363_000m,
                    OrderIndex = 1
                }
            ]
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, legacyMayRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, legacyMayRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        var restoredRuns = JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(
                               recalculatedProfile.BillingRunsJson) ?? [];
        Assert.Equal(2, restoredRuns.Count);
        Assert.Equal(2, restoredRuns.Select(run => run.RunKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(restoredRuns, run =>
            run.RunId == juneRunId &&
            run.RunKey == "20260601-20260630");
        var restoredMayRun = Assert.Single(restoredRuns, run => run.RunId == legacyMayRunId);
        Assert.Equal("20260501-20260531", restoredMayRun.RunKey);
        Assert.Equal(new DateOnly(2026, 5, 25), restoredMayRun.ScheduledDate);
        Assert.Equal(new DateOnly(2026, 5, 1), restoredMayRun.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 5, 31), restoredMayRun.PeriodEndDate);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_DuplicateActivePhysicalIdentityFailsClosedWithoutPartialRewrite()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();

        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        var firstRun = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(
                profile.BillingRunsJson) ?? []);
        var duplicateRun = JsonSerializer.Deserialize<ServerRentalBillingRunSnapshot>(
            JsonSerializer.Serialize(firstRun))!;
        duplicateRun.Status = "보류";
        duplicateRun.BilledAmount = 777_000m;
        profile.SettledAmount = 12_345m;
        profile.OutstandingAmount = 54_321m;
        profile.BillingRunsJson = JsonSerializer.Serialize(new[] { firstRun, duplicateRun });
        await dbContext.SaveChangesAsync();

        var originalJson = profile.BillingRunsJson;
        var originalRevision = profile.Revision;
        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        Assert.Equal(originalRevision, unchanged.Revision);
        Assert.Equal(originalJson, unchanged.BillingRunsJson);
        Assert.Equal(12_345m, unchanged.SettledAmount);
        Assert.Equal(54_321m, unchanged.OutstandingAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_CrossRunSameIdTransactionDoesNotHideDirectPayment()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(
            dbContext,
            existingPaymentAmount: 40_000m);
        var profile = dbContext.RentalBillingProfiles.Local.Single(current => current.Id == scenario.ProfileId);
        var runs = JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? [];
        var paymentRun = Assert.Single(runs);
        var transactionRunId = Guid.NewGuid();
        runs.Add(new ServerRentalBillingRunSnapshot
        {
            RunId = transactionRunId,
            RunKey = "2026-06",
            ScheduledDate = new DateOnly(2026, 6, 25),
            PeriodStartDate = new DateOnly(2026, 6, 1),
            PeriodEndDate = new DateOnly(2026, 6, 30),
            PeriodLabel = "2026-06",
            Status = "in-progress",
            BilledAmount = 50_000m,
            SettledAmount = 10_000m,
            SettlementStatus = "partial",
            SettledDate = new DateOnly(2026, 6, 26)
        });
        profile.BillingRunsJson = JsonSerializer.Serialize(runs);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "legacy cross-run settlement",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = transactionRunId,
            SettlementAmount = 10_000m
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == scenario.ProfileId);
        var recalculatedRuns = JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(
            recalculatedProfile.BillingRunsJson) ?? [];
        var recalculatedPaymentRun = Assert.Single(
            recalculatedRuns,
            current => current.RunId == paymentRun.RunId);
        Assert.Equal(40_000m, recalculatedPaymentRun.SettledAmount);
        Assert.Equal(new DateOnly(2026, 7, 26), recalculatedPaymentRun.SettledDate);
        var recalculatedTransactionRun = Assert.Single(
            recalculatedRuns,
            current => current.RunId == transactionRunId);
        Assert.Equal(10_000m, recalculatedTransactionRun.SettledAmount);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("transaction")]
    [InlineData("payment")]
    public async Task RentalSettlementRecalculation_ZeroAmountActiveEvidenceKeepsExistingBillingRun(
        string evidenceKind)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var scenario = SeedRentalDirectPaymentScenario(
            dbContext,
            existingPaymentAmount: evidenceKind == "payment" ? 0m : null);
        var invoice = dbContext.Invoices.Local.Single(current => current.Id == scenario.InvoiceId);
        invoice.TotalAmount = 0m;
        invoice.SupplyAmount = 0m;
        invoice.VatAmount = 0m;

        if (evidenceKind == "transaction")
        {
            invoice.IsDeleted = true;
            dbContext.Transactions.Add(new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = scenario.CustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 7, 26),
                TransactionKind = "zero rental settlement",
                LinkedInvoiceId = scenario.InvoiceId,
                LinkedInvoiceNumber = invoice.InvoiceNumber,
                LinkedRentalBillingProfileId = scenario.ProfileId,
                LinkedRentalBillingRunId = scenario.RunId,
                SettlementAmount = 0m
            });
        }

        await dbContext.SaveChangesAsync();
        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(scenario.ProfileId, scenario.RunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == scenario.ProfileId);
        var retainedRun = Assert.Single(
            JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(
                recalculatedProfile.BillingRunsJson) ?? [],
            current => current.RunId == scenario.RunId);
        Assert.Equal("2026-07", retainedRun.RunKey);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_IgnoresZeroAmountEvidenceWhenRestoringMissingBillingRunJson()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var zeroPaymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental zero evidence customer",
            NameMatchKey = "SERVERRENTALZEROEVIDENCECUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-zero-evidence-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Server rental zero evidence customer",
            BillingStatus = "in-progress",
            SettlementStatus = "pending",
            CompletionStatus = "incomplete",
            MonthlyAmount = 100_000m,
            BillingCycleMonths = 1,
            SettledAmount = 0m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-ZERO-EVIDENCE-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 25),
            TotalAmount = 0m,
            SupplyAmount = 0m,
            VatAmount = 0m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.Payments.Add(new Payment
        {
            Id = zeroPaymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 9, 26),
            Amount = 0m,
            Note = "zero amount direct rental payment must not restore billing run"
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 9, 26),
            TransactionKind = "rental payment",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-ZERO-EVIDENCE-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 0m,
            ReceiptTotal = 0m,
            SettlementAmount = 0m,
            Note = "zero amount rental transaction must not restore billing run"
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, recalculatedProfile.SettledAmount);
        Assert.Equal(0m, recalculatedProfile.OutstandingAmount);
        Assert.Equal("\uBBF8\uC644\uB8CC", recalculatedProfile.CompletionStatus);
        Assert.Null(recalculatedProfile.LastSettledDate);
        Assert.Empty(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? []);
    }

    [Fact]
    public async Task InvoicesController_Update_RentalBillingInvoice_RecalculatesBilledAndOutstandingAmounts()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental update customer",
            NameMatchKey = "SERVERRENTALUPDATECUSTOMER",
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
            CustomerName = "Server rental update customer",
            BillingStatus = "청구중",
            SettlementStatus = "부분입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 50_000m,
            OutstandingAmount = 50_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 50_000m,
                    SettlementStatus = "부분입금",
                    SettledDate = new DateOnly(2026, 6, 26)
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
            InvoiceNumber = "RENTAL-UPD-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Rental billing item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100_000m,
            LineAmount = 100_000m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-UPD-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 50_000m,
            ReceiptTotal = 50_000m,
            SettlementAmount = 50_000m
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].UnitPrice = 120_000m;
        updateDto.Lines[0].LineAmount = 120_000m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        var updatedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(120_000m, updatedInvoice.TotalAmount);
        var updatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(50_000m, updatedProfile.SettledAmount);
        Assert.Equal(70_000m, updatedProfile.OutstandingAmount);
        Assert.Equal("부분입금", updatedProfile.SettlementStatus);
        Assert.Equal("미완료", updatedProfile.CompletionStatus);
        var updatedRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(updatedProfile.BillingRunsJson) ?? []);
        Assert.Equal(120_000m, updatedRun.BilledAmount);
        Assert.Equal(50_000m, updatedRun.SettledAmount);
        Assert.Equal("부분입금", updatedRun.SettlementStatus);
        Assert.Equal("청구중", updatedRun.Status);
    }

    [Fact]
    public async Task PaymentsController_Create_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var createResponse = await controller.Create(new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 7, 26),
            Amount = 40_000m,
            Note = "direct rental payment"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(createResponse.Result);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 40_000m, expectedOutstanding: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Create_ForbidsInvoiceLinkedToRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var paymentId = Guid.NewGuid();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var createResponse = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 8, 26),
            Amount = 40_000m,
            Note = "must be rejected because rental profile is outside writable scope"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResponse.Result);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 0m, outstandingAmount: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Create_RejectsInvoiceWhoseCustomerIsDeleted()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-DELETED-CUSTOMER",
            NameMatchKey = "RESTPAYMENTDELETEDCUSTOMER",
            TradeType = "Sales",
            IsDeleted = true
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-DELETED-CUSTOMER-001",
            LocalTempNumber = "PAY-DELETED-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        dbContext.Customers.Add(deletedCustomer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var paymentId = Guid.NewGuid();
        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 10_000m,
            Note = "should be rejected"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("invoice_customer_not_found", badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
    }

    [Fact]
    public async Task PaymentsController_Update_RejectsSoftDeleteMutationViaPutAndKeepsLinkedRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PUT-SOFT-DELETE-PAYMENT-CUSTOMER",
            NameMatchKey = "PUTSOFTDELETEPAYMENTCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var transactionAttachmentId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PUT-PAYMENT-DELETE-BYPASS",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 40m,
            Note = "before PUT delete bypass"
        });
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = attachmentId,
            PaymentId = paymentId,
            AttachmentType = "receipt",
            FileName = "receipt.pdf",
            MimeType = "application/pdf",
            FileHash = "hash",
            FileSize = 1
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 24),
            TransactionKind = "linked-payment",
            LinkedInvoiceId = invoiceId,
            ReceiptTotal = 40m
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = paymentId,
            AttachmentType = "receipt",
            FileName = "receipt.pdf",
            MimeType = "application/pdf",
            FileHash = "hash",
            FileSize = 1
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(payment => payment.Attachments)
            .FirstAsync(payment => payment.Id == paymentId);
        var dto = stored.ToDto();
        dto.IsDeleted = true;
        dto.ExpectedRevision = stored.Revision;
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Update(paymentId, dto, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        AssertSoftDeletePutRejected(badRequest);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(row => row.Id == attachmentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(row => row.Id == transactionAttachmentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task PaymentsController_Update_RejectsRouteBodyIdentityMismatch_BeforeReceiptLookupOrMutation()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var routeId = Guid.NewGuid();
        var mutationId = $"route-body-mismatch:Payment:{routeId:N}";
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Update(routeId, new PaymentDto
        {
            Id = Guid.NewGuid(),
            MutationId = mutationId,
            MutationCreatedAtUtc = DateTime.UtcNow
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.False(await dbContext.ProcessedSyncMutations
            .AnyAsync(receipt => receipt.MutationId == mutationId));
    }

    [Fact]
    public async Task PaymentsController_Update_NormalizesRouteIdBeforeMutationHash_AndReplaysIdempotently()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PAYMENT-UPDATE-IDEMPOTENCY-CUSTOMER",
            NameMatchKey = "PAYMENTUPDATEIDEMPOTENCYCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-UPDATE-IDEMPOTENCY-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 27),
            TotalAmount = 100_000m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 27),
            Amount = 10_000m,
            Note = "before update"
        });
        await dbContext.SaveChangesAsync();

        var storedPayment = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);
        var mutationId = $"direct:update-payment:{paymentId:N}:{Guid.NewGuid():N}";
        var mutationCreatedAtUtc = new DateTime(2026, 7, 27, 3, 4, 5, DateTimeKind.Utc);
        var controller = CreatePaymentsController(dbContext, currentUser);

        PaymentDto UpdateRequest(Guid bodyId, decimal amount = 20_000m) => new()
        {
            Id = bodyId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 27),
            Amount = amount,
            Note = "idempotent update",
            ExpectedRevision = storedPayment.Revision,
            MutationId = mutationId,
            MutationCreatedAtUtc = mutationCreatedAtUtc
        };

        var first = await controller.Update(
            paymentId,
            UpdateRequest(Guid.Empty),
            CancellationToken.None);
        var replay = await controller.Update(
            paymentId,
            UpdateRequest(paymentId),
            CancellationToken.None);
        var changedPayload = await controller.Update(
            paymentId,
            UpdateRequest(paymentId, amount: 30_000m),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(first.Result);
        var replayResult = Assert.IsType<OkObjectResult>(replay.Result);
        Assert.Equal(paymentId, Assert.IsType<PaymentDto>(replayResult.Value).Id);
        var changedPayloadConflict =
            Assert.IsType<ConflictObjectResult>(changedPayload.Result);
        Assert.Equal(
            "mutation_id_conflict",
            Assert.IsType<DirectMutationConflictResponse>(
                changedPayloadConflict.Value).Error);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            20_000m,
            await dbContext.Payments
                .IgnoreQueryFilters()
                .Where(payment => payment.Id == paymentId)
                .Select(payment => payment.Amount)
                .SingleAsync());
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));
    }

    [Fact]
    public async Task PaymentsController_Update_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 70_000m,
            Note = "direct rental payment updated",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 70_000m, expectedOutstanding: 30_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_DerivedLinkedPayment_UpdatesSourceTransactionAndSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m
        });
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 7, 27),
            Amount = 70_000m,
            Note = "derived rental payment updated",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        dbContext.ChangeTracker.Clear();
        var linkedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.Equal(scenario.InvoiceId, linkedTransaction.LinkedInvoiceId);
        Assert.Equal("RENTAL-DIRECT-PAY-001", linkedTransaction.LinkedInvoiceNumber);
        Assert.Equal(scenario.CustomerId, linkedTransaction.CustomerId);
        Assert.Equal(scenario.ProfileId, linkedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, linkedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(new DateOnly(2026, 7, 27), linkedTransaction.TransactionDate);
        Assert.Equal(70_000m, linkedTransaction.SettlementAmount);
        Assert.Equal(70_000m, linkedTransaction.ReceiptTotal);
        Assert.Equal(70_000m, linkedTransaction.BankReceipt);
        await AssertRentalSettlementAsync(
            dbContext,
            scenario.ProfileId,
            scenario.RunId,
            expectedSettled: 70_000m,
            expectedOutstanding: 30_000m,
            expectedSettledDate: new DateOnly(2026, 7, 27));
    }

    [Fact]
    public async Task PaymentsController_Update_ForbidsExistingRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 70_000m,
            Note = "must not update outside rental profile",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.Equal(40_000m, unchangedPayment.Amount);
        Assert.Equal("seeded out-of-scope rental payment", unchangedPayment.Note);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 30_000m,
            Note = "must not recalculate hidden linked transaction rental profile",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.Equal(40_000m, unchangedPayment.Amount);
        Assert.Equal("seeded linked transaction payment", unchangedPayment.Note);
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_RejectsInvoiceWhoseCustomerIsDeleted()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-ACTIVE-CUSTOMER",
            NameMatchKey = "RESTPAYMENTACTIVECUSTOMER",
            TradeType = "Sales"
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-UPDATE-DELETED-CUSTOMER",
            NameMatchKey = "RESTPAYMENTUPDATEDELETEDCUSTOMER",
            TradeType = "Sales",
            IsDeleted = true
        };
        var activeInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = activeCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-ACTIVE-CUSTOMER-001",
            LocalTempNumber = "PAY-ACTIVE-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        var deletedCustomerInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-UPDATE-DELETED-CUSTOMER-001",
            LocalTempNumber = "PAY-UPDATE-DELETED-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = activeInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 10_000m,
            Note = "stored payment"
        };
        dbContext.Customers.AddRange(activeCustomer, deletedCustomer);
        dbContext.Invoices.AddRange(activeInvoice, deletedCustomerInvoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == payment.Id);
        var controller = CreatePaymentsController(dbContext, currentUser);
        var response = await controller.Update(payment.Id, new PaymentDto
        {
            Id = payment.Id,
            InvoiceId = deletedCustomerInvoice.Id,
            PaymentDate = payment.PaymentDate,
            Amount = 10_000m,
            Note = "should not relink",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("invoice_customer_not_found", badRequest.Value?.ToString(), StringComparison.Ordinal);
        var unchanged = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == payment.Id);
        Assert.Equal(activeInvoice.Id, unchanged.InvoiceId);
        Assert.Equal("stored payment", unchanged.Note);
    }

    [Fact]
    public async Task PaymentsController_Delete_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 0m, expectedOutstanding: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_DerivedLinkedPayment_DeletesSourceTransactionAndRevertsSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            Note = "mobile linked transaction"
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = scenario.PaymentId,
            FileName = "linked-transaction-evidence.pdf",
            StoragePath = "storage/linked-transaction-evidence.pdf"
        });
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        var deletedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.True(deletedPayment.IsDeleted);
        var deletedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.True(deletedTransaction.IsDeleted);
        var deletedAttachment = await dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(attachment => attachment.TransactionId == scenario.PaymentId);
        Assert.True(deletedAttachment.IsDeleted);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 0m, expectedOutstanding: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_ForbidsExistingRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.False(unchangedPayment.IsDeleted);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.False(unchangedPayment.IsDeleted);
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsRentalProfileOutsideWritableScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(scenario.InvoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == scenario.InvoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == scenario.PaymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task InvoicesController_SalesCreate_AllowsNegativeWarehouseStockWhenInventoryIsShort()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Shortage customer",
            NameMatchKey = "SHORTAGECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Shortage stock item",
            NameMatchKey = "SHORTAGESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 1m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 1m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var invoiceId = Guid.NewGuid();

        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 28),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var savedInvoice = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(invoiceId, savedInvoice.Id);
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
        Assert.Equal(-1m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(-1m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
    }

    [Fact]
    public async Task InvoicesController_PurchaseCreateUpdateDelete_AdjustsWarehouseStockSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase stock vendor",
            NameMatchKey = "PURCHASESTOCKVENDOR",
            TradeType = "Purchase"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase stock item",
            NameMatchKey = "PURCHASESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var createDto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Purchase,
            PurchaseReceivingRequired = true,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
            InvoiceDate = new DateOnly(2026, 5, 21),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        };

        var createResponse = await controller.Create(createDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(7m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(7m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].Quantity = 1m;
        updateDto.Lines[0].LineAmount = 1000m;

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(updateResponse.Result);
        Assert.Equal(6m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(6m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var latestInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var deleteResponse = await controller.Delete(invoiceId, latestInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId));
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.InvoiceId == invoiceId)
            .AllAsync(line => line.IsDeleted));
    }

    [Fact]
    public async Task InvoicesController_PurchasePending_DoesNotAdjustWarehouseStockOrLedger()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase pending vendor",
            NameMatchKey = "PURCHASEPENDINGVENDOR",
            TradeType = "Purchase"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase pending item",
            NameMatchKey = "PURCHASEPENDINGITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var createResponse = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Purchase,
            PurchaseReceivingRequired = true,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Pending,
            InvoiceDate = new DateOnly(2026, 5, 22),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.False(await dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == invoiceId));
    }

    [Fact]
    public async Task PaymentsController_Delete_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "수금 거래처",
            NameMatchKey = "수금거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-PAY-001",
            InvoiceDate = new DateOnly(2026, 4, 11)
        };
        dbContext.Invoices.Add(invoice);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 4, 11),
            Amount = 10000m,
            Note = "테스트 수금"
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstAsync(x => x.Id == payment.Id);
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Delete(stored.Id, stored.Revision + 1, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Payment), payload.EntityName);
    }

    [Fact]
    public async Task PaymentsController_Update_ForbidsRelinkingPaymentToOutOfScopeInvoice()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var allowedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed payment customer",
            NameMatchKey = "ALLOWEDPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope payment customer",
            NameMatchKey = "OUTOFSCOPEPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var allowedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = allowedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-PAY-SCOPE-ALLOWED",
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m
        };
        var outOfScopeInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = outOfScopeCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "INV-PAY-SCOPE-BLOCKED",
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = allowedInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 100m,
            Note = "original scope"
        };
        dbContext.Customers.AddRange(allowedCustomer, outOfScopeCustomer);
        dbContext.Invoices.AddRange(allowedInvoice, outOfScopeInvoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(row => row.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .Include(row => row.Attachments)
            .SingleAsync(row => row.Id == payment.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.InvoiceId = outOfScopeInvoice.Id;
        dto.Note = "attempted out-of-scope relink";

        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Update(payment.Id, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        var persisted = await dbContext.Payments
            .IgnoreQueryFilters()
            .SingleAsync(row => row.Id == payment.Id);
        Assert.Equal(allowedInvoice.Id, persisted.InvoiceId);
        Assert.Equal("original scope", persisted.Note);
    }

    [Fact]
    public async Task PaymentsController_UploadAttachment_IsIdempotentForClientAttachmentId()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Mobile attachment retry customer",
            NameMatchKey = "MOBILEATTACHMENTRETRYCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MOBILE-ATTACHMENT-IDEMPOTENT",
            InvoiceDate = new DateOnly(2026, 6, 18)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 5000m,
            Note = "mobile upload retry"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var fileStorage = new RecordingCentralFileStorage();
        var controller = CreatePaymentsController(dbContext, currentUser, fileStorage);
        var clientAttachmentId = Guid.NewGuid();

        var first = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("first upload")),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));
        var second = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("first upload")),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));

        Assert.Equal(clientAttachmentId, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FileHash, second.FileHash);
        Assert.Single(fileStorage.SavedFileIds);
        Assert.NotEqual(clientAttachmentId, fileStorage.SavedFileIds[0]);

        fileStorage.ClearSavedContent();
        var unavailableResponse = await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("first upload")),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None);
        var unavailable = Assert.IsType<ObjectResult>(unavailableResponse.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains(
            "client_attachment_content_unavailable",
            JsonSerializer.Serialize(unavailable.Value),
            StringComparison.Ordinal);

        Assert.Equal(1, await dbContext.PaymentAttachments.IgnoreQueryFilters().CountAsync(current => current.PaymentId == payment.Id));
        var storedAttachment = await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.PaymentId == payment.Id);
        Assert.False(string.IsNullOrWhiteSpace(storedAttachment.StoragePath));
        Assert.Empty(storedAttachment.FileContent);
    }

    [Fact]
    public async Task PaymentsController_UploadAttachment_RejectsDifferentPayloadOrMetadataForClientAttachmentId()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Mobile attachment collision customer",
            NameMatchKey = "MOBILEATTACHMENTCOLLISIONCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MOBILE-ATTACHMENT-COLLISION",
            InvoiceDate = new DateOnly(2026, 8, 3)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 8, 3),
            Amount = 5000m,
            Note = "mobile upload collision"
        };
        dbContext.AddRange(customer, invoice, payment);
        await dbContext.SaveChangesAsync();

        var fileStorage = new RecordingCentralFileStorage();
        var controller = CreatePaymentsController(dbContext, currentUser, fileStorage);
        var clientAttachmentId = Guid.NewGuid();
        var firstBytes = TestPdfBytes("first upload");

        var first = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", firstBytes),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));
        var conflicts = new[]
        {
            await controller.UploadAttachment(
                payment.Id,
                CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("different upload")),
                "내역첨부",
                "mobile retry",
                clientAttachmentId,
                CancellationToken.None),
            await controller.UploadAttachment(
                payment.Id,
                CreateFormFile("renamed-receipt.pdf", "application/pdf", firstBytes),
                "내역첨부",
                "mobile retry",
                clientAttachmentId,
                CancellationToken.None),
            await controller.UploadAttachment(
                payment.Id,
                CreateFormFile("retry-receipt.pdf", "application/pdf", firstBytes),
                "확인서",
                "mobile retry",
                clientAttachmentId,
                CancellationToken.None),
            await controller.UploadAttachment(
                payment.Id,
                CreateFormFile("retry-receipt.pdf", "application/pdf", firstBytes),
                "내역첨부",
                "changed description",
                clientAttachmentId,
                CancellationToken.None)
        };

        foreach (var response in conflicts)
        {
            var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
            Assert.Contains(
                "client_attachment_payload_conflict",
                JsonSerializer.Serialize(conflict.Value),
                StringComparison.Ordinal);
        }
        Assert.Single(fileStorage.SavedFileIds);
        var stored = await dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == clientAttachmentId);
        Assert.Equal(first.FileHash, stored.FileHash);
        Assert.Equal(firstBytes.LongLength, stored.FileSize);
    }

    [Fact]
    public async Task PaymentsController_UploadAttachment_RejectsFileContentThatDoesNotMatchFileType()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Payment attachment signature customer",
            NameMatchKey = "PAYMENTATTACHMENTSIGNATURECUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-ATTACHMENT-SIGNATURE",
            InvoiceDate = new DateOnly(2026, 6, 19)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 5000m,
            Note = "signature mismatch"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("fake-receipt.pdf", "application/pdf", [0x4D, 0x5A, 0x90, 0x00]),
            "내역첨부",
            "fake pdf content must not be stored",
            Guid.NewGuid(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("file_content_mismatch", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await dbContext.PaymentAttachments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PaymentsController_UploadAttachment_DoesNotPersistRow_WhenFileStorageFails()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Payment attachment storage failure customer",
            NameMatchKey = "PAYMENTATTACHMENTSTORAGEFAILURECUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-ATTACHMENT-STORAGE-FAIL",
            InvoiceDate = new DateOnly(2026, 6, 24)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 5000m,
            Note = "storage failure must not persist attachment row"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser, new ThrowingCentralFileStorage());

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.UploadAttachment(
            payment.Id,
            CreateFormFile("storage-failure-receipt.pdf", "application/pdf", TestPdfBytes("storage failure")),
            "내역첨부",
            "storage failure should not create db row",
            Guid.NewGuid(),
            CancellationToken.None));

        dbContext.ChangeTracker.Clear();
        Assert.Empty(await dbContext.PaymentAttachments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PaymentsController_DoesNotExposePaymentAttachments_WhenParentInvoiceIsNotReadable()
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
                NameOriginal = "Hidden invoice payment attachment customer",
                NameMatchKey = "HIDDENINVOICEPAYMENTATTACHMENTCUSTOMER",
                TradeType = "Sales"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "PAYMENT-ONLY-HIDDEN-INVOICE-DIRECT",
                InvoiceDate = new DateOnly(2026, 6, 24),
                VoucherType = VoucherType.Sales
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 20_000m,
                Note = "payment only hidden direct API"
            };
            var attachment = new PaymentAttachment
            {
                Id = Guid.NewGuid(),
                PaymentId = payment.Id,
                AttachmentType = "PDF",
                FileName = "hidden-direct-payment.pdf",
                MimeType = "application/pdf",
                FileSize = TestPdfBytes("hidden direct payment attachment").LongLength,
                FileHash = "hash",
                FileContent = TestPdfBytes("hidden direct payment attachment"),
                UploadedAtUtc = new DateTime(2026, 6, 24, 0, 1, 0, DateTimeKind.Utc)
            };

            seedDb.Customers.Add(customer);
            seedDb.Invoices.Add(invoice);
            seedDb.Payments.Add(payment);
            seedDb.PaymentAttachments.Add(attachment);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-payment-only-direct-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreatePaymentsController(scopedDb, scopedUser);

            var paymentsResponse = await controller.GetByInvoice(invoice.Id, CancellationToken.None);
            var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResponse.Result);
            var payments = Assert.IsType<List<PaymentDto>>(paymentsOk.Value);
            Assert.Empty(payments);

            var attachmentsResponse = await controller.GetAttachments(payment.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(attachmentsResponse.Result);

            var contentResponse = await controller.GetAttachmentContent(attachment.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contentResponse);
        }
    }

    [Fact]
    public async Task PaymentsController_GetAttachmentContent_ReturnsNotFound_WhenStoredContentIsMissing()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Missing payment attachment customer",
            NameMatchKey = "MISSINGPAYMENTATTACHMENTCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MISSING-PAYMENT-ATTACHMENT",
            InvoiceDate = new DateOnly(2026, 6, 18)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 5000m,
            Note = "missing attachment content"
        };
        var attachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttachmentType = "내역첨부",
            FileName = "missing-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 12,
            FileHash = "missing",
            StoragePath = Path.Combine(Path.GetTempPath(), "georaeplan-missing", Guid.NewGuid().ToString("N"), "missing-receipt.pdf"),
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(attachment);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);

        var result = await controller.GetAttachmentContent(attachment.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task PaymentsController_GetAttachmentContent_ReturnsNotFound_WhenStoredContentHashDiffers()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Hash drift payment attachment customer",
            NameMatchKey = "HASHDRIFTPAYMENTATTACHMENTCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "HASH-DRIFT-PAYMENT-ATTACHMENT",
            InvoiceDate = new DateOnly(2026, 6, 24)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 5000m,
            Note = "hash drift attachment content"
        };
        var expectedContent = TestPdfBytes("payment hash drift expected");
        var wrongSameLengthContent = MutateSameLength(expectedContent);
        var attachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttachmentType = "내역첨부",
            FileName = "hash-drift-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = expectedContent.LongLength,
            FileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedContent)),
            StoragePath = "payment-attachments/hash-drift-receipt.pdf",
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(attachment);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(
            dbContext,
            currentUser,
            new FixedReadCentralFileStorage(wrongSameLengthContent));

        var result = await controller.GetAttachmentContent(attachment.Id, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("attachment_content_unavailable", notFound.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransactionAttachment_ToDto_DoesNotExposeStoredContent_WhenStoredContentHashDiffers()
    {
        var expectedContent = TestPdfBytes("transaction hash drift expected");
        var wrongSameLengthContent = MutateSameLength(expectedContent);
        var tempRoot = Path.Combine(Path.GetTempPath(), "georaeplan-hash-drift-tests", Guid.NewGuid().ToString("N"));
        var storedPath = Path.Combine(tempRoot, "wrong-transaction-attachment.pdf");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllBytes(storedPath, wrongSameLengthContent);

        try
        {
            var attachment = new TransactionAttachment
            {
                Id = Guid.NewGuid(),
                TransactionId = Guid.NewGuid(),
                AttachmentType = "기타",
                FileName = "wrong-transaction-attachment.pdf",
                MimeType = "application/pdf",
                FileSize = expectedContent.LongLength,
                FileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedContent)),
                StoragePath = storedPath,
                FileContent = []
            };

            var dto = attachment.ToDto(includeContent: true);

            Assert.Empty(dto.FileContent);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CustomersController_DownloadContractContent_ReturnsNotFound_WhenStoredContentIsMissing()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Missing contract customer",
            NameMatchKey = "MISSINGCONTRACTCUSTOMER",
            TradeType = "Sales"
        };
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "거래계약서",
            FileName = "missing-contract.pdf",
            MimeType = "application/pdf",
            FileSize = 20,
            FileHash = "missing",
            StoragePath = Path.Combine(Path.GetTempPath(), "georaeplan-missing", Guid.NewGuid().ToString("N"), "missing-contract.pdf"),
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var result = await controller.DownloadContractContent(contract.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CustomersController_DownloadContractContent_ReturnsNotFound_WhenStoredContentHashDiffers()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Hash drift contract customer",
            NameMatchKey = "HASHDRIFTCONTRACTCUSTOMER",
            TradeType = "Sales"
        };
        var expectedContent = TestPdfBytes("contract hash drift expected");
        var wrongSameLengthContent = MutateSameLength(expectedContent);
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "거래계약서",
            FileName = "hash-drift-contract.pdf",
            MimeType = "application/pdf",
            FileSize = expectedContent.LongLength,
            FileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedContent)),
            StoragePath = "customer-contracts/hash-drift-contract.pdf",
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var controller = new CustomersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new FixedReadCentralFileStorage(wrongSameLengthContent));

        var result = await controller.DownloadContractContent(contract.Id, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("contract_content_unavailable", notFound.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomersController_DoesNotExposeContractContent_WhenParentCustomerIsNotReadable()
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
                SharePayments = false,
                ShareContracts = true,
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
                NameOriginal = "Hidden customer contract",
                NameMatchKey = "HIDDENCUSTOMERCONTRACT",
                TradeType = "Sales"
            };
            var content = TestPdfBytes("hidden customer contract content");
            var contract = new CustomerContract
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                ContractType = "거래계약서",
                FileName = "hidden-customer-contract.pdf",
                MimeType = "application/pdf",
                FileSize = content.LongLength,
                FileHash = "hidden-contract-hash",
                FileContent = content,
                UploadedAtUtc = new DateTime(2026, 6, 24, 1, 30, 0, DateTimeKind.Utc)
            };

            seedDb.Customers.Add(customer);
            seedDb.CustomerContracts.Add(contract);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-contract-only-direct-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = new CustomersController(
                scopedDb,
                new OfficeScopeService(scopedUser, scopedDb),
                new StubCentralFileStorage());

            var contractsResponse = await controller.GetContracts(customer.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contractsResponse.Result);

            var contentResponse = await controller.DownloadContractContent(contract.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contentResponse);
        }
    }

    [Fact]
    public async Task CompanyProfileController_Upsert_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "기본",
            TradeName = "유즈넷",
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().FirstAsync(x => x.Id == profile.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Upsert(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task CompanyProfileController_ForbidsOutOfScopeOfficeProfileReadAndWrite()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "company-profile-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.CompanyProfileEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var usenetProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET 기본",
            TradeName = "유즈넷",
            BankAccountText = "USENET-ACCOUNT",
            IsDefaultForOffice = true,
            IsActive = true
        };
        var yeonsuProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileName = "YEONSU 기본",
            TradeName = "연수",
            BankAccountText = "YEONSU-SECRET-ACCOUNT",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.AddRange(usenetProfile, yeonsuProfile);
        await dbContext.SaveChangesAsync();

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var readResponse = await controller.Get(OfficeCodeCatalog.Yeonsu, CancellationToken.None);
        Assert.IsType<ForbidResult>(readResponse.Result);

        var writeDto = yeonsuProfile.ToDto();
        writeDto.ExpectedRevision = yeonsuProfile.Revision;
        writeDto.TradeName = "연수 수정 시도";
        var writeResponse = await controller.Upsert(writeDto, CancellationToken.None);

        Assert.IsType<ForbidResult>(writeResponse.Result);
        var persistedYeonsu = await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == yeonsuProfile.Id);
        Assert.Equal("연수", persistedYeonsu.TradeName);

        var ownReadResponse = await controller.Get(OfficeCodeCatalog.Usenet, CancellationToken.None);
        var ownReadOk = Assert.IsType<OkObjectResult>(ownReadResponse.Result);
        var ownReadDto = Assert.IsType<CompanyProfileDto>(ownReadOk.Value);
        Assert.Equal(usenetProfile.Id, ownReadDto.Id);
    }

    [Fact]
    public async Task CompanyProfileController_Get_DoesNotFallbackToOtherOfficeProfile()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var usenetProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET default",
            TradeName = "USENET",
            BankAccountText = "USENET-ONLY-ACCOUNT",
            StampImage = [1, 2, 3],
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(usenetProfile);
        await dbContext.SaveChangesAsync();

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var missingOfficeResponse = await controller.Get(OfficeCodeCatalog.Yeonsu, CancellationToken.None);

        Assert.IsType<NotFoundResult>(missingOfficeResponse.Result);
    }

    [Fact]
    public async Task CompanyProfileController_Upsert_RejectsSoftDeleteMutationViaPutAndKeepsProfileActive()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET default",
            TradeName = "USENET",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().FirstAsync(row => row.Id == profile.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.IsDeleted = true;

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Upsert(dto, CancellationToken.None);

        AssertSoftDeletePutRejected(Assert.IsType<BadRequestObjectResult>(response.Result));
        Assert.False(await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(row => row.Id == profile.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());

        var readResponse = await controller.Get(OfficeCodeCatalog.Usenet, CancellationToken.None);
        Assert.IsType<OkObjectResult>(readResponse.Result);
    }

    [Fact]
    public async Task CompanyProfileController_Upsert_PreservesFaxNumber_WhenLegacyClientOmitsField()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET default",
            TradeName = "USENET",
            FaxNumber = "032-100-2000",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().FirstAsync(row => row.Id == profile.Id);
        var legacyDto = stored.ToDto();
        legacyDto.ExpectedRevision = stored.Revision;
        legacyDto.TradeName = "USENET 수정";
        legacyDto.FaxNumber = null;

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var legacyResponse = await controller.Upsert(legacyDto, CancellationToken.None);

        Assert.IsType<OkObjectResult>(legacyResponse.Result);
        var afterLegacySave = await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == profile.Id);
        Assert.Equal("USENET 수정", afterLegacySave.TradeName);
        Assert.Equal("032-100-2000", afterLegacySave.FaxNumber);

        var clearDto = afterLegacySave.ToDto();
        clearDto.ExpectedRevision = afterLegacySave.Revision;
        clearDto.FaxNumber = string.Empty;

        var clearResponse = await controller.Upsert(clearDto, CancellationToken.None);

        Assert.IsType<OkObjectResult>(clearResponse.Result);
        Assert.Equal(string.Empty, await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(row => row.Id == profile.Id)
            .Select(row => row.FaxNumber)
            .SingleAsync());
    }

    [Fact]
    public async Task SyncPush_ReturnsAcceptedRevision_ForConsecutiveCompanyProfileSaves()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET 기본",
            TradeName = "유즈넷",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().SingleAsync(row => row.Id == profile.Id);
        var baselineRevision = stored.Revision;
        var firstDto = stored.ToDto();
        firstDto.ExpectedRevision = stored.Revision;
        firstDto.TradeName = "유즈넷 1차 저장";

        var controller = CreateSyncController(dbContext, currentUser);
        var firstResult = AssertSyncOk(await controller.Push(new SyncPushRequest
        {
            DeviceId = "test-device",
            CompanyProfiles = [firstDto]
        }, CancellationToken.None));

        var accepted = Assert.Single(firstResult.AcceptedRevisions, revision => revision.EntityId == profile.Id);
        Assert.Equal(nameof(CompanyProfile), accepted.EntityName);
        Assert.True(accepted.Revision > baselineRevision);

        var secondDto = firstDto;
        secondDto.Revision = accepted.Revision;
        secondDto.ExpectedRevision = accepted.Revision;
        secondDto.UpdatedAtUtc = accepted.UpdatedAtUtc;
        secondDto.TradeName = "유즈넷 2차 저장";

        var secondResult = AssertSyncOk(await controller.Push(new SyncPushRequest
        {
            DeviceId = "test-device",
            CompanyProfiles = [secondDto]
        }, CancellationToken.None));

        Assert.Equal(0, secondResult.ConflictCount);
        Assert.Contains(secondResult.AcceptedRevisions, revision => revision.EntityId == profile.Id && revision.Revision > accepted.Revision);
        Assert.Equal("유즈넷 2차 저장", await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(row => row.Id == profile.Id)
            .Select(row => row.TradeName)
            .SingleAsync());
    }

    [Fact]
    public async Task UsersController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "user1",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Users.IgnoreQueryFilters().Include(x => x.Permissions).FirstAsync(x => x.Id == user.Id);
        var controller = new UsersController(dbContext, currentUser, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(
            stored.Id,
            new UpdateUserRequest
            {
                ExpectedRevision = stored.Revision + 1,
                Username = stored.Username,
                Role = stored.Role,
                TenantCode = stored.TenantCode,
                OfficeCode = stored.OfficeCode,
                ScopeType = stored.ScopeType,
                IsActive = stored.IsActive,
                Permissions = []
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task TenantSettingsController_UpdateTenant_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var tenant = new TenantDefinition
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            DisplayName = "유즈넷",
            StorageMode = TenantScopeCatalog.StorageSharedDatabase,
            IsActive = true
        };
        dbContext.TenantDefinitions.Add(tenant);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.TenantDefinitions.IgnoreQueryFilters().FirstAsync(x => x.TenantCode == tenant.TenantCode);
        var controller = new TenantSettingsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.UpdateTenant(
            stored.TenantCode,
            new UpdateTenantDefinitionRequest
            {
                ExpectedRevision = stored.Revision + 1,
                DisplayName = stored.DisplayName,
                StorageMode = stored.StorageMode,
                Description = stored.Description,
                IsActive = stored.IsActive
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task UnitsAndCustomerCategories_UseOptimisticConcurrencyGuard()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var unit = new Unit
        {
            Id = Guid.NewGuid(),
            Name = "대"
        };
        var category = new CustomerCategory
        {
            Id = Guid.NewGuid(),
            Name = "기업"
        };
        dbContext.Units.Add(unit);
        dbContext.CustomerCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var storedUnit = await dbContext.Units.IgnoreQueryFilters().FirstAsync(x => x.Id == unit.Id);
        var storedCategory = await dbContext.CustomerCategories.IgnoreQueryFilters().FirstAsync(x => x.Id == category.Id);

        var unitController = new UnitsController(dbContext);
        var categoryController = new CustomerCategoriesController(dbContext);

        var unitDto = storedUnit.ToDto();
        unitDto.ExpectedRevision = storedUnit.Revision + 1;
        var categoryDto = storedCategory.ToDto();
        categoryDto.ExpectedRevision = storedCategory.Revision + 1;

        var unitResponse = await unitController.Update(storedUnit.Id, unitDto, CancellationToken.None);
        var categoryResponse = await categoryController.Update(storedCategory.Id, categoryDto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(unitResponse.Result);
        Assert.IsType<ConflictObjectResult>(categoryResponse.Result);
    }

    [Fact]
    public async Task CustomerCategoriesController_Create_ReturnsConflict_WhenActiveNameAlreadyExistsAfterTrim()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var existingId = Guid.NewGuid();
        dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = existingId,
            Name = "관공서"
        });
        await dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var controller = new CustomerCategoriesController(dbContext);
        var response = await controller.Create(new CustomerCategoryDto
        {
            Id = incomingId,
            Name = " 관공서 "
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.False(await dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(category => category.Id == incomingId));
        Assert.Equal(1, await dbContext.CustomerCategories.IgnoreQueryFilters().CountAsync(category => !category.IsDeleted));
    }

    [Fact]
    public async Task CustomerCategoriesController_Update_ReturnsConflict_WhenActiveNameAlreadyExistsAfterTrim()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var existingId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        dbContext.CustomerCategories.AddRange(
            new CustomerCategory
            {
                Id = existingId,
                Name = "관공서"
            },
            new CustomerCategory
            {
                Id = targetId,
                Name = "학교"
            });
        await dbContext.SaveChangesAsync();

        var storedTarget = await dbContext.CustomerCategories.FirstAsync(category => category.Id == targetId);
        var controller = new CustomerCategoriesController(dbContext);
        var response = await controller.Update(targetId, new CustomerCategoryDto
        {
            Id = targetId,
            Name = " 관공서 ",
            ExpectedRevision = storedTarget.Revision
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Equal("학교", await dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .Where(category => category.Id == targetId)
            .Select(category => category.Name)
            .SingleAsync());
        Assert.Equal(2, await dbContext.CustomerCategories.IgnoreQueryFilters().CountAsync(category => !category.IsDeleted));
    }

    [Fact]
    public async Task UnitsController_Update_RejectsSoftDeleteMutationViaPut()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var unitId = Guid.NewGuid();
        dbContext.Units.Add(new Unit
        {
            Id = unitId,
            Name = "EA",
            IsActive = true
        });
        await dbContext.SaveChangesAsync();

        var storedUnit = await dbContext.Units.IgnoreQueryFilters().FirstAsync(unit => unit.Id == unitId);
        var controller = new UnitsController(dbContext);
        var response = await controller.Update(unitId, new UnitDto
        {
            Id = unitId,
            Name = "EA",
            IsActive = true,
            IsDeleted = true,
            ExpectedRevision = storedUnit.Revision
        }, CancellationToken.None);

        AssertSoftDeletePutRejected(Assert.IsType<BadRequestObjectResult>(response.Result));
        Assert.False(await dbContext.Units.IgnoreQueryFilters()
            .Where(unit => unit.Id == unitId)
            .Select(unit => unit.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task CustomerCategoriesController_Update_RejectsSoftDeleteMutationViaPutAndDeleteEndpointDeletes()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var categoryId = Guid.NewGuid();
        dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = categoryId,
            Name = "기업"
        });
        await dbContext.SaveChangesAsync();

        var storedCategory = await dbContext.CustomerCategories.IgnoreQueryFilters().FirstAsync(category => category.Id == categoryId);
        var controller = new CustomerCategoriesController(dbContext);
        var updateResponse = await controller.Update(categoryId, new CustomerCategoryDto
        {
            Id = categoryId,
            Name = "기업",
            IsDeleted = true,
            ExpectedRevision = storedCategory.Revision
        }, CancellationToken.None);

        AssertSoftDeletePutRejected(Assert.IsType<BadRequestObjectResult>(updateResponse.Result));
        Assert.False(await dbContext.CustomerCategories.IgnoreQueryFilters()
            .Where(category => category.Id == categoryId)
            .Select(category => category.IsDeleted)
            .SingleAsync());

        var deleteResult = await controller.Delete(categoryId, storedCategory.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResult);
        Assert.True(await dbContext.CustomerCategories.IgnoreQueryFilters()
            .Where(category => category.Id == categoryId)
            .Select(category => category.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task CustomerCategoriesController_Delete_RejectsReferencedCategoryAndKeepsActive()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var categoryId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = categoryId,
            Name = "참조분류"
        });
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CATEGORY-REFERENCE-CUSTOMER",
            NameMatchKey = "CATEGORYREFERENCECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales,
            CategoryId = categoryId
        });
        dbContext.CustomerMasters.Add(new CustomerMaster
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CATEGORY-REFERENCE-MASTER",
            NameMatchKey = "CATEGORYREFERENCEMASTER",
            CategoryId = categoryId
        });
        await dbContext.SaveChangesAsync();

        var storedCategory = await dbContext.CustomerCategories.IgnoreQueryFilters().FirstAsync(category => category.Id == categoryId);
        var controller = new CustomerCategoriesController(dbContext);

        var deleteResult = await controller.Delete(categoryId, storedCategory.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(deleteResult);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        Assert.Equal(CustomerCategoryDeletionReferenceGuard.ConflictCode, payloadType.GetProperty("error")?.GetValue(payload));
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("거래처", message, StringComparison.Ordinal);
        Assert.False(await dbContext.CustomerCategories.IgnoreQueryFilters()
            .Where(category => category.Id == categoryId)
            .Select(category => category.IsDeleted)
            .SingleAsync());
    }

    private static TDto AssertOk<TDto>(ActionResult<TDto> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<TDto>(ok.Value);
    }

    private static void AssertSoftDeletePutRejected(BadRequestObjectResult badRequest)
    {
        var payload = badRequest.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        Assert.Equal(SoftDeleteMutationGuard.ErrorCode, payloadType.GetProperty("error")?.GetValue(payload));
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("전용 삭제 API", message, StringComparison.Ordinal);
    }

    private static void AssertSoftDeleteCreateRejected(BadRequestObjectResult badRequest)
    {
        var payload = badRequest.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        Assert.Equal(SoftDeleteMutationGuard.CreateErrorCode, payloadType.GetProperty("error")?.GetValue(payload));
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("삭제 상태로 저장할 수 없습니다", message, StringComparison.Ordinal);
    }

    private static SyncPushResult AssertSyncOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private static SyncController CreateSyncController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            new RevisionClock(),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));

    private static InvoicesController CreateInvoicesController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

    private static PaymentsController CreatePaymentsController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            new RentalSettlementRecalculationService(dbContext));

    private static PaymentsController CreatePaymentsController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser,
        ICentralFileStorage fileStorage)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            fileStorage,
            new RentalSettlementRecalculationService(dbContext));

    private static RentalDirectPaymentScenario SeedRentalDirectPaymentScenario(
        AppDbContext dbContext,
        decimal? existingPaymentAmount = null,
        decimal storedSettledAmount = 0m)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var storedOutstandingAmount = Math.Max(0m, billedAmount - storedSettledAmount);
        var storedSettlementStatus = storedSettledAmount <= 0m
            ? "확인대기"
            : storedSettledAmount < billedAmount
                ? "부분입금"
                : "입금확인";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Direct rental payment customer",
            NameMatchKey = "DIRECTRENTALPAYMENTCUSTOMER",
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
            CustomerName = "Direct rental payment customer",
            BillingStatus = storedOutstandingAmount <= 0m ? "완료" : "청구중",
            SettlementStatus = storedSettlementStatus,
            CompletionStatus = storedOutstandingAmount <= 0m ? "완료" : "미완료",
            MonthlyAmount = billedAmount,
            SettledAmount = storedSettledAmount,
            OutstandingAmount = storedOutstandingAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-07",
                    ScheduledDate = new DateOnly(2026, 7, 25),
                    PeriodStartDate = new DateOnly(2026, 7, 1),
                    PeriodEndDate = new DateOnly(2026, 7, 31),
                    PeriodLabel = "2026-07",
                    Status = storedOutstandingAmount <= 0m ? "완료" : "청구중",
                    BilledAmount = billedAmount,
                    SettledAmount = storedSettledAmount,
                    SettlementStatus = storedSettlementStatus,
                    SettledDate = storedSettledAmount > 0m ? new DateOnly(2026, 7, 26) : null
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
            InvoiceNumber = "RENTAL-DIRECT-PAY-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Rental billing direct payment item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = billedAmount,
            LineAmount = billedAmount
        });

        if (existingPaymentAmount.HasValue)
        {
            dbContext.Payments.Add(new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 26),
                Amount = existingPaymentAmount.Value,
                Note = "seeded direct rental payment"
            });
        }

        return new RentalDirectPaymentScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static OutOfScopeRentalLinkedInvoiceScenario SeedOutOfScopeRentalLinkedInvoiceScenario(
        AppDbContext dbContext,
        decimal? existingPaymentAmount = null,
        decimal storedSettledAmount = 0m)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var storedOutstandingAmount = Math.Max(0m, billedAmount - storedSettledAmount);

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Direct out-of-scope rental customer",
            NameMatchKey = "DIRECTOUTOFSCOPERENTALCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"profile-out-of-scope-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Direct out-of-scope rental customer",
            BillingStatus = storedOutstandingAmount <= 0m ? "completed" : "in-progress",
            SettlementStatus = storedSettledAmount <= 0m ? "pending" : storedOutstandingAmount > 0m ? "partial" : "settled",
            CompletionStatus = storedOutstandingAmount <= 0m ? "completed" : "incomplete",
            MonthlyAmount = billedAmount,
            SettledAmount = storedSettledAmount,
            OutstandingAmount = storedOutstandingAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-08",
                    ScheduledDate = new DateOnly(2026, 8, 25),
                    PeriodStartDate = new DateOnly(2026, 8, 1),
                    PeriodEndDate = new DateOnly(2026, 8, 31),
                    PeriodLabel = "2026-08",
                    Status = storedOutstandingAmount <= 0m ? "completed" : "in-progress",
                    BilledAmount = billedAmount,
                    SettledAmount = storedSettledAmount,
                    SettlementStatus = storedSettledAmount <= 0m ? "pending" : storedOutstandingAmount > 0m ? "partial" : "settled",
                    SettledDate = storedSettledAmount > 0m ? new DateOnly(2026, 8, 26) : null
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
            InvoiceNumber = "RENTAL-OUT-OF-SCOPE-DIRECT-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Out-of-scope rental billing item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = billedAmount,
            LineAmount = billedAmount
        });

        if (existingPaymentAmount.HasValue)
        {
            dbContext.Payments.Add(new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 8, 26),
                Amount = existingPaymentAmount.Value,
                Note = "seeded out-of-scope rental payment"
            });
        }

        return new OutOfScopeRentalLinkedInvoiceScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static LinkedTransactionOutOfScopeRentalScenario SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(
        AppDbContext dbContext)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var settledAmount = 40_000m;

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Payment linked transaction hidden rental customer",
            NameMatchKey = "PAYMENTLINKEDTRANSACTIONHIDDENRENTALCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"linked-transaction-hidden-profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Payment linked transaction hidden rental customer",
            BillingStatus = "in-progress",
            SettlementStatus = "partial",
            CompletionStatus = "incomplete",
            MonthlyAmount = billedAmount,
            SettledAmount = settledAmount,
            OutstandingAmount = billedAmount - settledAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-09",
                    ScheduledDate = new DateOnly(2026, 9, 25),
                    PeriodStartDate = new DateOnly(2026, 9, 1),
                    PeriodEndDate = new DateOnly(2026, 9, 30),
                    PeriodLabel = "2026-09",
                    Status = "in-progress",
                    BilledAmount = billedAmount,
                    SettledAmount = settledAmount,
                    SettlementStatus = "partial",
                    SettledDate = new DateOnly(2026, 9, 26)
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
            InvoiceNumber = "PAY-LINKED-TX-HIDDEN-RENTAL",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 9, 26),
            Amount = settledAmount,
            Note = "seeded linked transaction payment"
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 9, 26),
            TransactionKind = "legacy linked rental receipt",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "PAY-LINKED-TX-HIDDEN-RENTAL",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = settledAmount,
            ReceiptTotal = settledAmount,
            SettlementAmount = settledAmount,
            Note = "legacy linked transaction with hidden rental profile"
        });

        return new LinkedTransactionOutOfScopeRentalScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static async Task AssertRentalSettlementAsync(
        AppDbContext dbContext,
        Guid profileId,
        Guid runId,
        decimal expectedSettled,
        decimal expectedOutstanding,
        DateOnly? expectedSettledDate = null)
    {
        var profile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        var expectedSettlementStatus = expectedSettled <= 0m
            ? "확인대기"
            : expectedOutstanding > 0m
                ? "부분입금"
                : "입금확인";

        Assert.Equal(expectedSettled, profile.SettledAmount);
        Assert.Equal(expectedOutstanding, profile.OutstandingAmount);
        Assert.Equal(expectedOutstanding <= 0m ? "완료" : "미완료", profile.CompletionStatus);
        Assert.Equal(expectedSettlementStatus, profile.SettlementStatus);

        var run = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? [], current => current.RunId == runId);
        Assert.Equal(100_000m, run.BilledAmount);
        Assert.Equal(expectedSettled, run.SettledAmount);
        Assert.Equal(expectedSettlementStatus, run.SettlementStatus);
        Assert.Equal(expectedOutstanding <= 0m ? "완료" : "청구중", run.Status);
        if (expectedSettled <= 0m)
            Assert.Null(run.SettledDate);
        else
            Assert.Equal(expectedSettledDate ?? new DateOnly(2026, 7, 26), run.SettledDate);
    }

    private static async Task AssertOutOfScopeRentalSettlementUnchangedAsync(
        AppDbContext dbContext,
        Guid profileId,
        decimal settledAmount,
        decimal outstandingAmount)
    {
        var profile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);

        Assert.Equal(settledAmount, profile.SettledAmount);
        Assert.Equal(outstandingAmount, profile.OutstandingAmount);
        var run = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? []);
        Assert.Equal(settledAmount, run.SettledAmount);
    }

    private sealed record RentalDirectPaymentScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private sealed record OutOfScopeRentalLinkedInvoiceScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private sealed record LinkedTransactionOutOfScopeRentalScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private static IFormFile CreateFormFile(string fileName, string contentType, string content)
        => CreateFormFile(fileName, contentType, System.Text.Encoding.UTF8.GetBytes(content));

    private static IFormFile CreateFormFile(string fileName, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] TestPdfBytes(string marker)
        => System.Text.Encoding.UTF8.GetBytes($"%PDF-1.4\n% {marker}\n1 0 obj\n<<>>\nendobj\n%%EOF\n");

    private static byte[] MutateSameLength(byte[] content)
    {
        var mutated = content.ToArray();
        mutated[^6] = mutated[^6] == (byte)'O' ? (byte)'X' : (byte)'O';
        return mutated;
    }

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

    private static TestCurrentUserContext CreateOfficePaymentEditor()
        => new()
        {
            Username = "payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };

    private sealed class ServerRentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public int CycleMonths { get; set; } = 1;
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
        public bool IsTombstoned { get; set; }
        public DateTime? TombstonedAtUtc { get; set; }
        public string TombstonedByUsername { get; set; } = string.Empty;
    }

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

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-0001");
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

    private sealed class FixedReadCentralFileStorage(byte[] bytes) : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string category, string tenantKey, Guid fileId, string? fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, category, tenantKey, fileId.ToString("N"), fileName ?? "file.bin"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => bytes;

        public void DeleteIfExists(string? storedPath)
        {
        }
    }

    private sealed class RecordingCentralFileStorage : ICentralFileStorage
    {
        private readonly Dictionary<string, byte[]> _savedContent = new(StringComparer.OrdinalIgnoreCase);

        public string RootPath => Path.GetTempPath();
        public List<Guid> SavedFileIds { get; } = [];

        public Task<string> SaveBytesAsync(
            string category,
            string tenantKey,
            Guid fileId,
            string? fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            SavedFileIds.Add(fileId);
            var storedPath = Path.Combine(
                RootPath,
                category,
                tenantKey,
                fileId.ToString("N"),
                fileName ?? "file.bin");
            _savedContent[storedPath] = content.ToArray();
            return Task.FromResult(storedPath);
        }

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => !string.IsNullOrWhiteSpace(storedPath) &&
               _savedContent.TryGetValue(storedPath, out var content)
                ? content.ToArray()
                : fallbackContent ?? [];

        public void ClearSavedContent()
            => _savedContent.Clear();

        public void DeleteIfExists(string? storedPath)
        {
        }
    }

    private sealed class ThrowingCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string category,
            string tenantKey,
            Guid fileId,
            string? fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("file storage failed"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => fallbackContent ?? Array.Empty<byte>();

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
