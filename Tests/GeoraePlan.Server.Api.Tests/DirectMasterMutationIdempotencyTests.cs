using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DirectMasterMutationIdempotencyTests : IDisposable
{
    private static readonly DateTime MutationCreatedAtUtc =
        new(2026, 7, 27, 4, 5, 6, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public DirectMasterMutationIdempotencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task Customers_Create_RecordsReceipt_ReplaysExactly_AndRejectsChangedPayload()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = CreateCustomersController(dbContext, currentUser);
        var customerId = Guid.NewGuid();
        var mutationId = $"direct:customer:create:{customerId:N}";

        var first = await controller.Create(
            CreateCustomerRequest(customerId, mutationId, notes: "original payload"),
            CancellationToken.None);

        var firstDto = AssertOk<CustomerDto>(first);
        var firstRevision = firstDto.Revision;
        Assert.True(firstRevision > 0);
        await AssertReceiptAsync<Customer>(
            dbContext,
            mutationId,
            customerId,
            expectedRevision: 0);

        var replay = await controller.Create(
            CreateCustomerRequest(customerId, mutationId, notes: "original payload"),
            CancellationToken.None);

        var replayDto = AssertOk<CustomerDto>(replay);
        Assert.Equal(firstRevision, replayDto.Revision);
        dbContext.ChangeTracker.Clear();
        var afterReplay = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        Assert.Equal(firstRevision, afterReplay.Revision);
        Assert.Equal("original payload", afterReplay.Notes);
        Assert.Equal(
            1,
            await dbContext.Customers
                .IgnoreQueryFilters()
                .CountAsync(customer => customer.Id == customerId));
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));

        var changed = await controller.Create(
            CreateCustomerRequest(customerId, mutationId, notes: "changed reuse"),
            CancellationToken.None);

        AssertMutationConflict(changed, mutationId, nameof(Customer), customerId);
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        Assert.Equal(firstRevision, unchanged.Revision);
        Assert.Equal("original payload", unchanged.Notes);
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));
    }

    [Fact]
    public async Task Customers_Update_RecordsReceipt_ReplaysWithoutRepeatingLinkedSideEffects_AndRejectsChangedPayload()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "customer before direct update",
            NameMatchKey = "CUSTOMERBEFOREDIRECTUPDATE",
            TradeType = CustomerClassificationNormalizer.Sales,
            BusinessNumber = "100-00-00000",
            Email = "before@example.test"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"direct-customer-update-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "customer before direct update",
            BusinessNumber = "100-00-00000",
            Email = "before@example.test"
        });
        await dbContext.SaveChangesAsync();
        var customerBefore = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var mutationId = $"direct:customer:update:{customerId:N}:{customerBefore.Revision}";
        var controller = CreateCustomersController(dbContext, currentUser);

        var first = await controller.Update(
            customerId,
            CreateCustomerUpdateRequest(
                customerBefore,
                mutationId,
                name: "customer after direct update"),
            CancellationToken.None);

        var firstDto = AssertOk<CustomerDto>(first);
        Assert.True(firstDto.Revision > customerBefore.Revision);
        await AssertReceiptAsync<Customer>(
            dbContext,
            mutationId,
            customerId,
            customerBefore.Revision);
        dbContext.ChangeTracker.Clear();
        var profileAfterFirst = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal("customer after direct update", profileAfterFirst.CustomerName);
        var sideEffectRevision = profileAfterFirst.Revision;

        var replay = await controller.Update(
            customerId,
            CreateCustomerUpdateRequest(
                customerBefore,
                mutationId,
                name: "customer after direct update"),
            CancellationToken.None);

        var replayDto = AssertOk<CustomerDto>(replay);
        Assert.Equal(firstDto.Revision, replayDto.Revision);
        dbContext.ChangeTracker.Clear();
        var customerAfterReplay = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var profileAfterReplay = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(firstDto.Revision, customerAfterReplay.Revision);
        Assert.Equal(sideEffectRevision, profileAfterReplay.Revision);
        Assert.Equal("customer after direct update", profileAfterReplay.CustomerName);

        var changed = await controller.Update(
            customerId,
            CreateCustomerUpdateRequest(
                customerBefore,
                mutationId,
                name: "changed mutation id reuse"),
            CancellationToken.None);

        AssertMutationConflict(changed, mutationId, nameof(Customer), customerId);
        dbContext.ChangeTracker.Clear();
        var unchangedCustomer = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var unchangedProfile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(firstDto.Revision, unchangedCustomer.Revision);
        Assert.Equal("customer after direct update", unchangedCustomer.NameOriginal);
        Assert.Equal(sideEffectRevision, unchangedProfile.Revision);
        Assert.Equal("customer after direct update", unchangedProfile.CustomerName);
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));
    }

    [Fact]
    public async Task CustomersAndItems_Update_RejectRouteBodyMismatch_BeforeReceiptOrMutation()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "route identity customer",
            NameMatchKey = "ROUTEIDENTITYCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "route identity item",
            NameMatchKey = "ROUTEIDENTITYITEM",
            ItemKind = ItemKinds.Billing,
            TrackingType = ItemTrackingTypes.NonStock,
            IsSale = true
        });
        await dbContext.SaveChangesAsync();
        var customerBefore = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var itemBefore = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        var customerMutationId = $"route-body-mismatch:customer:{customerId:N}";
        var itemMutationId = $"route-body-mismatch:item:{itemId:N}";

        var customerResponse = await CreateCustomersController(dbContext, currentUser).Update(
            customerId,
            new CustomerDto
            {
                Id = Guid.NewGuid(),
                ExpectedRevision = customerBefore.Revision,
                MutationId = customerMutationId,
                MutationCreatedAtUtc = MutationCreatedAtUtc,
                NameOriginal = "must not write"
            },
            CancellationToken.None);
        var itemResponse = await new ItemsController(
                dbContext,
                new OfficeScopeService(currentUser, dbContext))
            .Update(
                itemId,
                new ItemDto
                {
                    Id = Guid.NewGuid(),
                    ExpectedRevision = itemBefore.Revision,
                    MutationId = itemMutationId,
                    MutationCreatedAtUtc = MutationCreatedAtUtc,
                    NameOriginal = "must not write"
                },
                CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(customerResponse.Result);
        Assert.IsType<BadRequestObjectResult>(itemResponse.Result);
        Assert.False(await dbContext.ProcessedSyncMutations.AnyAsync(receipt =>
            receipt.MutationId == customerMutationId ||
            receipt.MutationId == itemMutationId));
        dbContext.ChangeTracker.Clear();
        var customerAfter = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var itemAfter = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(customerBefore.Revision, customerAfter.Revision);
        Assert.Equal(customerBefore.NameOriginal, customerAfter.NameOriginal);
        Assert.Equal(itemBefore.Revision, itemAfter.Revision);
        Assert.Equal(itemBefore.NameOriginal, itemAfter.NameOriginal);
    }

    [Fact]
    public async Task Items_CreateAndUpdate_RecordReceipts_ReplayExactly_AndRejectChangedPayloads()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new ItemsController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var itemId = Guid.NewGuid();
        var createMutationId = $"direct:item:create:{itemId:N}";

        var createFirst = await controller.Create(
            CreateItemRequest(itemId, createMutationId, memo: "created once"),
            CancellationToken.None);

        var createdDto = AssertOk<ItemDto>(createFirst);
        await AssertReceiptAsync<Item>(
            dbContext,
            createMutationId,
            itemId,
            expectedRevision: 0);
        var createReplay = await controller.Create(
            CreateItemRequest(itemId, createMutationId, memo: "created once"),
            CancellationToken.None);
        Assert.Equal(createdDto.Revision, AssertOk<ItemDto>(createReplay).Revision);
        var createChanged = await controller.Create(
            CreateItemRequest(itemId, createMutationId, memo: "changed reuse"),
            CancellationToken.None);
        AssertMutationConflict(createChanged, createMutationId, nameof(Item), itemId);
        dbContext.ChangeTracker.Clear();
        var storedAfterCreate = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(createdDto.Revision, storedAfterCreate.Revision);
        Assert.Equal("created once", storedAfterCreate.SimpleMemo);

        var updateMutationId =
            $"direct:item:update:{itemId:N}:{storedAfterCreate.Revision}";
        var updateFirst = await controller.Update(
            itemId,
            CreateItemUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                salePrice: 123_456m),
            CancellationToken.None);

        var updatedDto = AssertOk<ItemDto>(updateFirst);
        Assert.True(updatedDto.Revision > storedAfterCreate.Revision);
        await AssertReceiptAsync<Item>(
            dbContext,
            updateMutationId,
            itemId,
            storedAfterCreate.Revision);
        var updateReplay = await controller.Update(
            itemId,
            CreateItemUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                salePrice: 123_456m),
            CancellationToken.None);
        Assert.Equal(updatedDto.Revision, AssertOk<ItemDto>(updateReplay).Revision);
        var updateChanged = await controller.Update(
            itemId,
            CreateItemUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                salePrice: 999_999m),
            CancellationToken.None);
        AssertMutationConflict(updateChanged, updateMutationId, nameof(Item), itemId);

        dbContext.ChangeTracker.Clear();
        var finalItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(updatedDto.Revision, finalItem.Revision);
        Assert.Equal(123_456m, finalItem.SalePrice);
        Assert.Equal(
            1,
            await dbContext.Items
                .IgnoreQueryFilters()
                .CountAsync(item => item.Id == itemId));
        Assert.Equal(
            2,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt =>
                receipt.EntityName == nameof(Item) &&
                receipt.EntityId == itemId.ToString("D")));
    }

    [Fact]
    public async Task CustomerCategories_CreateAndUpdate_RecordReceipts_ReplayExactly_AndRejectChangedPayloads()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new CustomerCategoriesController(dbContext);
        var categoryId = Guid.NewGuid();
        var createMutationId = $"direct:customer-category:create:{categoryId:N}";

        var createFirst = await controller.Create(
            CreateCategoryRequest(categoryId, createMutationId, "멱등 생성 분류"),
            CancellationToken.None);

        var createdDto = AssertOk<CustomerCategoryDto>(createFirst);
        await AssertReceiptAsync<CustomerCategory>(
            dbContext,
            createMutationId,
            categoryId,
            expectedRevision: 0);
        var createReplay = await controller.Create(
            CreateCategoryRequest(categoryId, createMutationId, "멱등 생성 분류"),
            CancellationToken.None);
        Assert.Equal(
            createdDto.Revision,
            AssertOk<CustomerCategoryDto>(createReplay).Revision);
        var createChanged = await controller.Create(
            CreateCategoryRequest(categoryId, createMutationId, "재사용 충돌 분류"),
            CancellationToken.None);
        AssertMutationConflict(
            createChanged,
            createMutationId,
            nameof(CustomerCategory),
            categoryId);

        dbContext.ChangeTracker.Clear();
        var storedAfterCreate = await dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(category => category.Id == categoryId);
        Assert.Equal(createdDto.Revision, storedAfterCreate.Revision);
        var updateMutationId =
            $"direct:customer-category:update:{categoryId:N}:{storedAfterCreate.Revision}";

        var updateFirst = await controller.Update(
            categoryId,
            CreateCategoryUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "멱등 수정 분류"),
            CancellationToken.None);

        var updatedDto = AssertOk<CustomerCategoryDto>(updateFirst);
        await AssertReceiptAsync<CustomerCategory>(
            dbContext,
            updateMutationId,
            categoryId,
            storedAfterCreate.Revision);
        var updateReplay = await controller.Update(
            categoryId,
            CreateCategoryUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "멱등 수정 분류"),
            CancellationToken.None);
        Assert.Equal(
            updatedDto.Revision,
            AssertOk<CustomerCategoryDto>(updateReplay).Revision);
        var updateChanged = await controller.Update(
            categoryId,
            CreateCategoryUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "수정 재사용 충돌 분류"),
            CancellationToken.None);
        AssertMutationConflict(
            updateChanged,
            updateMutationId,
            nameof(CustomerCategory),
            categoryId);

        dbContext.ChangeTracker.Clear();
        var finalCategory = await dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(category => category.Id == categoryId);
        Assert.Equal(updatedDto.Revision, finalCategory.Revision);
        Assert.Equal("멱등 수정 분류", finalCategory.Name);
        Assert.Equal(
            1,
            await dbContext.CustomerCategories
                .IgnoreQueryFilters()
                .CountAsync(category => category.Id == categoryId));
        Assert.Equal(
            2,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt =>
                receipt.EntityName == nameof(CustomerCategory) &&
                receipt.EntityId == categoryId.ToString("D")));
    }

    [Fact]
    public async Task Units_CreateAndUpdate_RecordReceipts_ReplayExactly_AndRejectChangedPayloads()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new UnitsController(dbContext);
        var unitId = Guid.NewGuid();
        var createMutationId = $"direct:unit:create:{unitId:N}";

        var createFirst = await controller.Create(
            CreateUnitRequest(unitId, createMutationId, "IDEMPOTENT-EA"),
            CancellationToken.None);

        var createdDto = AssertOk<UnitDto>(createFirst);
        await AssertReceiptAsync<Unit>(
            dbContext,
            createMutationId,
            unitId,
            expectedRevision: 0);
        var createReplay = await controller.Create(
            CreateUnitRequest(unitId, createMutationId, "IDEMPOTENT-EA"),
            CancellationToken.None);
        Assert.Equal(createdDto.Revision, AssertOk<UnitDto>(createReplay).Revision);
        var createChanged = await controller.Create(
            CreateUnitRequest(unitId, createMutationId, "CONFLICT-EA"),
            CancellationToken.None);
        AssertMutationConflict(createChanged, createMutationId, nameof(Unit), unitId);

        dbContext.ChangeTracker.Clear();
        var storedAfterCreate = await dbContext.Units
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(unit => unit.Id == unitId);
        Assert.Equal(createdDto.Revision, storedAfterCreate.Revision);
        var updateMutationId =
            $"direct:unit:update:{unitId:N}:{storedAfterCreate.Revision}";

        var updateFirst = await controller.Update(
            unitId,
            CreateUnitUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "IDEMPOTENT-BOX"),
            CancellationToken.None);

        var updatedDto = AssertOk<UnitDto>(updateFirst);
        await AssertReceiptAsync<Unit>(
            dbContext,
            updateMutationId,
            unitId,
            storedAfterCreate.Revision);
        var updateReplay = await controller.Update(
            unitId,
            CreateUnitUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "IDEMPOTENT-BOX"),
            CancellationToken.None);
        Assert.Equal(updatedDto.Revision, AssertOk<UnitDto>(updateReplay).Revision);
        var updateChanged = await controller.Update(
            unitId,
            CreateUnitUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                "CONFLICT-BOX"),
            CancellationToken.None);
        AssertMutationConflict(updateChanged, updateMutationId, nameof(Unit), unitId);

        dbContext.ChangeTracker.Clear();
        var finalUnit = await dbContext.Units
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(unit => unit.Id == unitId);
        Assert.Equal(updatedDto.Revision, finalUnit.Revision);
        Assert.Equal("IDEMPOTENT-BOX", finalUnit.Name);
        Assert.Equal(
            1,
            await dbContext.Units
                .IgnoreQueryFilters()
                .CountAsync(unit => unit.Id == unitId));
        Assert.Equal(
            2,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt =>
                receipt.EntityName == nameof(Unit) &&
                receipt.EntityId == unitId.ToString("D")));
    }

    [Fact]
    public async Task CompanyProfile_Upsert_RecordsCreateAndUpdateReceipts_ReplaysExactly_AndRejectsChangedPayloads()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new CompanyProfileController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var profileId = Guid.NewGuid();
        var createMutationId = $"direct:company-profile:create:{profileId:N}";

        var createFirst = await controller.Upsert(
            CreateCompanyProfileRequest(
                profileId,
                createMutationId,
                tradeName: "멱등 회사"),
            CancellationToken.None);

        var createdDto = AssertOk<CompanyProfileDto>(createFirst);
        await AssertReceiptAsync<CompanyProfile>(
            dbContext,
            createMutationId,
            profileId,
            expectedRevision: 0);
        var createReplay = await controller.Upsert(
            CreateCompanyProfileRequest(
                profileId,
                createMutationId,
                tradeName: "멱등 회사"),
            CancellationToken.None);
        Assert.Equal(
            createdDto.Revision,
            AssertOk<CompanyProfileDto>(createReplay).Revision);
        var createChanged = await controller.Upsert(
            CreateCompanyProfileRequest(
                profileId,
                createMutationId,
                tradeName: "재사용 충돌 회사"),
            CancellationToken.None);
        AssertMutationConflict(
            createChanged,
            createMutationId,
            nameof(CompanyProfile),
            profileId);

        dbContext.ChangeTracker.Clear();
        var storedAfterCreate = await dbContext.CompanyProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(createdDto.Revision, storedAfterCreate.Revision);
        var updateMutationId =
            $"direct:company-profile:update:{profileId:N}:{storedAfterCreate.Revision}";

        var updateFirst = await controller.Upsert(
            CreateCompanyProfileUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                address: "인천광역시 멱등로 1"),
            CancellationToken.None);

        var updatedDto = AssertOk<CompanyProfileDto>(updateFirst);
        await AssertReceiptAsync<CompanyProfile>(
            dbContext,
            updateMutationId,
            profileId,
            storedAfterCreate.Revision);
        var updateReplay = await controller.Upsert(
            CreateCompanyProfileUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                address: "인천광역시 멱등로 1"),
            CancellationToken.None);
        Assert.Equal(
            updatedDto.Revision,
            AssertOk<CompanyProfileDto>(updateReplay).Revision);
        var updateChanged = await controller.Upsert(
            CreateCompanyProfileUpdateRequest(
                storedAfterCreate,
                updateMutationId,
                address: "인천광역시 충돌로 2"),
            CancellationToken.None);
        AssertMutationConflict(
            updateChanged,
            updateMutationId,
            nameof(CompanyProfile),
            profileId);

        dbContext.ChangeTracker.Clear();
        var finalProfile = await dbContext.CompanyProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(updatedDto.Revision, finalProfile.Revision);
        Assert.Equal("인천광역시 멱등로 1", finalProfile.Address);
        Assert.Equal(
            1,
            await dbContext.CompanyProfiles
                .IgnoreQueryFilters()
                .CountAsync(profile => profile.Id == profileId));
        Assert.Equal(
            2,
            await dbContext.ProcessedSyncMutations.CountAsync(receipt =>
                receipt.EntityName == nameof(CompanyProfile) &&
                receipt.EntityId == profileId.ToString("D")));
    }

    [Fact]
    public async Task CustomerMaster_Create_RecordsReceipt_ReplaysExactly_AndRejectsChangedPayload()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var controller = new CustomerMastersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var masterId = Guid.NewGuid();
        var mutationId = $"direct:customer-master:create:{masterId:N}";

        var first = await controller.Create(
            CreateCustomerMasterRequest(
                masterId,
                mutationId,
                name: "멱등 거래처 원장"),
            CancellationToken.None);

        var firstDto = AssertOk<CustomerMasterDto>(first);
        await AssertReceiptAsync<CustomerMaster>(
            dbContext,
            mutationId,
            masterId,
            expectedRevision: 0);
        var replay = await controller.Create(
            CreateCustomerMasterRequest(
                masterId,
                mutationId,
                name: "멱등 거래처 원장"),
            CancellationToken.None);
        Assert.Equal(
            firstDto.Revision,
            AssertOk<CustomerMasterDto>(replay).Revision);
        var changed = await controller.Create(
            CreateCustomerMasterRequest(
                masterId,
                mutationId,
                name: "재사용 충돌 거래처 원장"),
            CancellationToken.None);
        AssertMutationConflict(
            changed,
            mutationId,
            nameof(CustomerMaster),
            masterId);

        dbContext.ChangeTracker.Clear();
        var finalMaster = await dbContext.CustomerMasters
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(master => master.Id == masterId);
        Assert.Equal(firstDto.Revision, finalMaster.Revision);
        Assert.Equal("멱등 거래처 원장", finalMaster.NameOriginal);
        Assert.Equal(
            1,
            await dbContext.CustomerMasters
                .IgnoreQueryFilters()
                .CountAsync(master => master.Id == masterId));
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));
    }

    [Fact]
    public async Task Customer_DirectUpdateThenSameDtoSyncPush_IsCountedAsOneCrossPathDuplicate()
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
            NameOriginal = "cross path before",
            NameMatchKey = "CROSSPATHBEFORE",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        await dbContext.SaveChangesAsync();
        var before = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        var mutationId =
            $"cross-path:customer:{customerId:N}:{before.Revision}";
        var directRequest = CreateCustomerUpdateRequest(
            before,
            mutationId,
            name: "cross path after");

        var directResponse = await CreateCustomersController(dbContext, currentUser)
            .Update(customerId, directRequest, CancellationToken.None);

        var directDto = AssertOk<CustomerDto>(directResponse);
        var revisionAfterDirect = directDto.Revision;
        Assert.True(revisionAfterDirect > before.Revision);
        dbContext.ChangeTracker.Clear();

        var syncResponse = await CreateSyncController(dbContext, currentUser).Push(
            new SyncPushRequest
            {
                DeviceId = "mobile-cross-path-test",
                Customers = [directRequest]
            },
            CancellationToken.None);

        var syncResult = AssertSyncOk(syncResponse);
        Assert.Equal(1, syncResult.AcceptedCount);
        Assert.Equal(1, syncResult.DuplicateMutationCount);
        Assert.Equal(0, syncResult.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var afterSync = await dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == customerId);
        Assert.Equal(revisionAfterDirect, afterSync.Revision);
        Assert.Equal("cross path after", afterSync.NameOriginal);
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations
                .CountAsync(receipt => receipt.MutationId == mutationId));
    }

    private static CustomerDto CreateCustomerRequest(
        Guid id,
        string mutationId,
        string notes)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "direct idempotent customer",
            NameMatchKey = "DIRECTIDEMPOTENTCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales,
            Phone = "032-000-0000",
            Notes = notes,
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static CustomerDto CreateCustomerUpdateRequest(
        Customer before,
        string mutationId,
        string name)
    {
        var dto = before.ToDto();
        dto.NameOriginal = name;
        dto.NameMatchKey = MatchKeyNormalizer.Normalize(name);
        dto.BusinessNumber = "200-00-00000";
        dto.Email = "after@example.test";
        dto.ExpectedRevision = before.Revision;
        dto.MutationId = mutationId;
        dto.MutationCreatedAtUtc = MutationCreatedAtUtc;
        return dto;
    }

    private static ItemDto CreateItemRequest(
        Guid id,
        string mutationId,
        string memo)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "direct idempotent item",
            NameMatchKey = "DIRECTIDEMPOTENTITEM",
            SpecificationOriginal = "standard",
            SpecificationMatchKey = "STANDARD",
            ItemKind = ItemKinds.Billing,
            TrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            SimpleMemo = memo,
            SalePrice = 10_000m,
            IsSale = true,
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static ItemDto CreateItemUpdateRequest(
        Item before,
        string mutationId,
        decimal salePrice)
    {
        var dto = before.ToDto();
        dto.SalePrice = salePrice;
        dto.ExpectedRevision = before.Revision;
        dto.MutationId = mutationId;
        dto.MutationCreatedAtUtc = MutationCreatedAtUtc;
        return dto;
    }

    private static CustomerCategoryDto CreateCategoryRequest(
        Guid id,
        string mutationId,
        string name)
        => new()
        {
            Id = id,
            Name = name,
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static CustomerCategoryDto CreateCategoryUpdateRequest(
        CustomerCategory before,
        string mutationId,
        string name)
    {
        var dto = before.ToDto();
        dto.Name = name;
        dto.ExpectedRevision = before.Revision;
        dto.MutationId = mutationId;
        dto.MutationCreatedAtUtc = MutationCreatedAtUtc;
        return dto;
    }

    private static UnitDto CreateUnitRequest(
        Guid id,
        string mutationId,
        string name)
        => new()
        {
            Id = id,
            Name = name,
            IsActive = true,
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static UnitDto CreateUnitUpdateRequest(
        Unit before,
        string mutationId,
        string name)
    {
        var dto = before.ToDto();
        dto.Name = name;
        dto.ExpectedRevision = before.Revision;
        dto.MutationId = mutationId;
        dto.MutationCreatedAtUtc = MutationCreatedAtUtc;
        return dto;
    }

    private static CompanyProfileDto CreateCompanyProfileRequest(
        Guid id,
        string mutationId,
        string tradeName)
        => new()
        {
            Id = id,
            ProfileName = "멱등 회사 프로필",
            OfficeCode = OfficeCodeCatalog.Usenet,
            TradeName = tradeName,
            Representative = "테스트 대표",
            IsActive = true,
            IsDefaultForOffice = false,
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static CompanyProfileDto CreateCompanyProfileUpdateRequest(
        CompanyProfile before,
        string mutationId,
        string address)
    {
        var dto = before.ToDto();
        dto.Address = address;
        dto.ExpectedRevision = before.Revision;
        dto.MutationId = mutationId;
        dto.MutationCreatedAtUtc = MutationCreatedAtUtc;
        return dto;
    }

    private static CustomerMasterDto CreateCustomerMasterRequest(
        Guid id,
        string mutationId,
        string name)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = MatchKeyNormalizer.Normalize(name),
            MutationId = mutationId,
            MutationCreatedAtUtc = MutationCreatedAtUtc
        };

    private static TDto AssertOk<TDto>(ActionResult<TDto> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(200, ok.StatusCode);
        return Assert.IsType<TDto>(ok.Value);
    }

    private static SyncPushResult AssertSyncOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        Assert.Equal(200, ok.StatusCode);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private static void AssertMutationConflict<TDto>(
        ActionResult<TDto> response,
        string mutationId,
        string entityName,
        Guid entityId)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<DirectMutationConflictResponse>(conflict.Value);
        Assert.Equal("mutation_id_conflict", payload.Error);
        Assert.Equal(
            ProcessedSyncMutationRecorder.NormalizeMutationId(mutationId),
            payload.MutationId);
        Assert.Equal(entityName, payload.EntityName);
        Assert.Equal(entityId, payload.EntityId);
    }

    private static async Task AssertReceiptAsync<TEntity>(
        AppDbContext dbContext,
        string mutationId,
        Guid entityId,
        long expectedRevision)
    {
        dbContext.ChangeTracker.Clear();
        var receipt = await dbContext.ProcessedSyncMutations
            .AsNoTracking()
            .SingleAsync(current =>
                current.MutationId ==
                ProcessedSyncMutationRecorder.NormalizeMutationId(mutationId));
        Assert.Equal(typeof(TEntity).Name, receipt.EntityName);
        Assert.Equal(entityId.ToString("D"), receipt.EntityId);
        Assert.Equal(expectedRevision, receipt.ExpectedRevision);
        Assert.Equal(ProcessedSyncMutationRecorder.DirectApiDeviceId, receipt.DeviceId);
        Assert.Equal(MutationCreatedAtUtc, receipt.ProcessedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", receipt.PayloadHash);
    }

    private CustomersController CreateCustomersController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage());

    private static SyncController CreateSyncController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
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
            Username = "direct-master-idempotency-admin",
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
        public IReadOnlyCollection<string> Permissions { get; init; } =
            Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin ||
               IsGodMode ||
               Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"IDEMPOTENCY-{invoiceDate:yyyyMMdd}-0001");
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
            => Task.FromResult(Path.Combine(
                RootPath,
                area,
                ownerId,
                fileId.ToString("N"),
                fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
