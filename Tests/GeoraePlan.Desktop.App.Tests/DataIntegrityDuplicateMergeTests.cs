using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityDuplicateMergeTests
{
    [Fact]
    public async Task ScanAsync_DuplicateCustomerIssueIncludesDecisionInfoAndMergeMovesReferences()
    {
        PrepareAppRoot("georaeplan-integrity-customer-merge");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerA = CreateCustomer("11111111-1111-1111-1111-111111111111", "중복거래처");
            var customerB = CreateCustomer("22222222-2222-2222-2222-222222222222", "중복거래처");
            var invoiceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var profileId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var assetId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            db.Customers.AddRange(customerA, customerB);
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerA.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceDate = new DateOnly(2026, 6, 12),
                InvoiceNumber = "S-1",
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|TEST|CUSTOMER",
                CustomerId = customerB.Id,
                CustomerName = customerB.NameOriginal,
                CurrentCustomerName = customerB.NameOriginal,
                ItemName = "복합기",
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerB.Id,
                CustomerName = customerB.NameOriginal,
                ItemName = "복합기",
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var dispatcher = new SyncRequestDispatcher();
            var syncRequested = false;
            dispatcher.SyncRequested += _ => syncRequested = true;
            var service = CreateLegacyLocalItemMergeService(db, dispatcher);
            var session = CreateAdminSession();

            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate);
            Assert.True(issue.CanMergeDuplicates);
            Assert.Contains(customerA.Id, issue.RelatedEntityIds);
            Assert.Contains(customerB.Id, issue.RelatedEntityIds);
            Assert.Contains("참조 합계", issue.ReviewInfoDisplay);

            var result = await service.MergeDuplicateIssueAsync(issue, session);

            Assert.True(result.Success, result.Message);
            Assert.True(syncRequested);
            var canonicalId = result.EntityId;
            var deletedCustomerId = customerA.Id == canonicalId ? customerB.Id : customerA.Id;
            var deletedCustomer = await db.Customers.IgnoreQueryFilters().FirstAsync(customer => customer.Id == deletedCustomerId);
            Assert.NotNull(deletedCustomer);
            Assert.True(deletedCustomer.IsDeleted);
            var invoice = await db.Invoices.IgnoreQueryFilters().FirstAsync(invoice => invoice.Id == invoiceId);
            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().FirstAsync(profile => profile.Id == profileId);
            Assert.Equal(canonicalId, invoice.CustomerId);
            Assert.Equal(canonicalId, profile.CustomerId);
            Assert.True(invoice.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateIssueAsync_RequiresInvoiceEditWhenCustomerMergeMovesInvoices()
    {
        PrepareAppRoot("georaeplan-integrity-customer-merge-invoice-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateCustomer("01111111-1111-1111-1111-111111111111", "권한병합거래처");
            var duplicate = CreateCustomer("02222222-2222-2222-2222-222222222222", "권한병합거래처");
            var duplicateInvoiceId = Guid.Parse("03333333-3333-3333-3333-333333333333");
            db.Customers.AddRange(canonical, duplicate);
            db.Invoices.AddRange(
                CreateInitializerInvoice("04444444-4444-4444-4444-444444444444", canonical.Id, "MERGE-PERM-CANONICAL-1"),
                CreateInitializerInvoice("05555555-5555-5555-5555-555555555555", canonical.Id, "MERGE-PERM-CANONICAL-2"),
                CreateInitializerInvoice(duplicateInvoiceId.ToString("D"), duplicate.Id, "MERGE-PERM-DUPLICATE-1"));
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate);
            var customerOnlySession = CreateUserSession(AppPermissionNames.CustomerEdit);

            var result = await service.MergeDuplicateIssueAsync(issue, customerOnlySession);

            Assert.False(result.Success);
            Assert.Contains("전표", result.Message);
            var storedDuplicate = await db.Customers.IgnoreQueryFilters().SingleAsync(customer => customer.Id == duplicate.Id);
            var storedDuplicateInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == duplicateInvoiceId);
            Assert.False(storedDuplicate.IsDeleted);
            Assert.False(storedDuplicate.IsDirty);
            Assert.Equal(duplicate.Id, storedDuplicateInvoice.CustomerId);
            Assert.False(storedDuplicateInvoice.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_MergeBusinessDuplicateCustomers_RepointsRentalAssignmentHistoryCustomerReferences()
    {
        PrepareAppRoot("georaeplan-initializer-business-customer-merge-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var source = CreateCustomer("15111111-1111-1111-1111-111111111111", "AUTO MERGE CUSTOMER", "123-45-67890");
            var target = CreateCustomer("15222222-2222-2222-2222-222222222222", "AUTO MERGE CUSTOMER", "123-45-67890");
            var historyId = Guid.Parse("15333333-3333-3333-3333-333333333333");
            db.Customers.AddRange(source, target);
            db.Invoices.AddRange(
                CreateInitializerInvoice("15444444-4444-4444-4444-444444444444", target.Id, "LOCAL-INIT-BIZ-1"),
                CreateInitializerInvoice("15555555-5555-5555-5555-555555555555", target.Id, "LOCAL-INIT-BIZ-2"));
            db.RentalAssetAssignmentHistories.Add(CreateInitializerAssignmentHistory(historyId, source.Id));
            await db.SaveChangesAsync();

            var method = typeof(LocalDbInitializer).GetMethod(
                "MergeBusinessDuplicateCustomersAsync",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var task = method!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(task);
            await task!;
            await db.SaveChangesAsync();

            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.Id == source.Id));
            var remaining = Assert.Single(await db.Customers.IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted && customer.NameOriginal == source.NameOriginal)
                .ToListAsync());
            Assert.Equal(target.Id, remaining.Id);

            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == historyId);
            Assert.Equal(target.Id, history.CustomerId);
            Assert.Equal("AUTO MERGE CUSTOMER", history.CustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_MergeDuplicateCustomers_RepointsRentalAssignmentHistoryCustomerReferences()
    {
        PrepareAppRoot("georaeplan-initializer-customer-merge-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var source = CreateCustomer("16111111-1111-1111-1111-111111111111", "AUTO MERGE CUSTOMER");
            var target = CreateCustomer("16222222-2222-2222-2222-222222222222", "AUTO MERGE CUSTOMER");
            var historyId = Guid.Parse("16333333-3333-3333-3333-333333333333");
            db.Customers.AddRange(source, target);
            db.Invoices.AddRange(
                CreateInitializerInvoice("16444444-4444-4444-4444-444444444444", target.Id, "LOCAL-INIT-GENERIC-1"),
                CreateInitializerInvoice("16555555-5555-5555-5555-555555555555", target.Id, "LOCAL-INIT-GENERIC-2"));
            db.RentalAssetAssignmentHistories.Add(CreateInitializerAssignmentHistory(historyId, source.Id));
            await db.SaveChangesAsync();

            var method = typeof(LocalDbInitializer).GetMethod(
                "MergeDuplicateCustomersAsync",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var task = method!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(task);
            await task!;
            await db.SaveChangesAsync();

            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.Id == source.Id));
            var remaining = Assert.Single(await db.Customers.IgnoreQueryFilters()
                .Where(customer => !customer.IsDeleted && customer.NameOriginal == source.NameOriginal)
                .ToListAsync());
            Assert.Equal(target.Id, remaining.Id);

            var history = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == historyId);
            Assert.Equal(target.Id, history.CustomerId);
            Assert.Equal("AUTO MERGE CUSTOMER", history.CustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_DuplicateCustomerCandidateRequiresExactSameName_NotSameBusinessNumberOnly()
    {
        PrepareAppRoot("georaeplan-integrity-customer-exact-name");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Customers.AddRange(
                CreateCustomer("61111111-1111-1111-1111-111111111111", "미추홀구 경제지원과", "121-83-00724"),
                CreateCustomer("62222222-2222-2222-2222-222222222222", "미추홀구 도시정비과", "121-83-00724"),
                CreateCustomer("63333333-3333-3333-3333-333333333333", "미추홀구 노인장애인복지과", "121-83-00724"),
                CreateCustomer("64444444-4444-4444-4444-444444444444", "중복거래처", "111-11-11111"),
                CreateCustomer("65555555-5555-5555-5555-555555555555", "중복거래처", "222-22-22222"));
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());

            var customerIssues = scan.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate)
                .ToList();
            var issue = Assert.Single(customerIssues);
            Assert.Equal("중복거래처", issue.CustomerName);
            Assert.DoesNotContain(customerIssues, current => current.RelatedEntityIds.Any(id =>
                id == Guid.Parse("61111111-1111-1111-1111-111111111111") ||
                id == Guid.Parse("62222222-2222-2222-2222-222222222222") ||
                id == Guid.Parse("63333333-3333-3333-3333-333333333333")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_ItemMergeMovesReferencesWithZeroStock()
    {
        PrepareAppRoot("georaeplan-integrity-item-merge");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "거래처");
            var itemA = CreateItem("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "중복품목", "A4", currentStock: 0m);
            var itemB = CreateItem("cccccccc-cccc-cccc-cccc-cccccccccccc", "중복품목", "A4", currentStock: 0m);
            var invoiceId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
            var lineId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var assetId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            var transferId = Guid.Parse("12121212-1212-1212-1212-121212121212");
            db.Customers.Add(customer);
            db.Items.AddRange(itemA, itemB);
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceDate = new DateOnly(2026, 6, 12),
                InvoiceNumber = "S-2",
                IsDirty = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        Id = lineId,
                        InvoiceId = invoiceId,
                        ItemId = itemB.Id,
                        ItemNameOriginal = itemB.NameOriginal,
                        SpecificationOriginal = itemB.SpecificationOriginal,
                        Quantity = 1m
                    }
                }
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|TEST|ASSET",
                ItemId = itemB.Id,
                ItemName = itemB.NameOriginal,
                IsDirty = false
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                IsDirty = false,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        TransferId = transferId,
                        ItemId = itemB.Id,
                        ItemNameOriginal = itemB.NameOriginal,
                        SpecificationOriginal = itemB.SpecificationOriginal,
                        Quantity = 1m
                    }
                }
            });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = itemA.Id,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 0m
                },
                new LocalItemWarehouseStock
                {
                    ItemId = itemB.Id,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 0m
                });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            Assert.True(issue.CanMergeDuplicates);
            Assert.Contains("창고별 재고 합계", issue.ReviewInfoDisplay);

            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                comparison.RecommendedCanonicalId!.Value,
                comparison.SnapshotToken,
                session);

            Assert.True(result.Success, result.Message);
            var canonicalId = result.EntityId;
            var deletedItemId = itemA.Id == canonicalId ? itemB.Id : itemA.Id;
            var deletedItem = await db.Items.IgnoreQueryFilters().FirstAsync(item => item.Id == deletedItemId);
            Assert.NotNull(deletedItem);
            Assert.True(deletedItem.IsDeleted);
            Assert.Equal(canonicalId, (await db.InvoiceLines.IgnoreQueryFilters().FirstAsync(line => line.Id == lineId)).ItemId);
            Assert.Equal(canonicalId, (await db.RentalAssets.IgnoreQueryFilters().FirstAsync(asset => asset.Id == assetId)).ItemId);
            Assert.All(await db.InventoryTransferLines.IgnoreQueryFilters().ToListAsync(), line => Assert.Equal(canonicalId, line.ItemId));
            var canonicalStocks = await db.ItemWarehouseStocks.Where(stock => stock.ItemId == canonicalId).ToListAsync();
            Assert.Single(canonicalStocks);
            Assert.Equal(0m, canonicalStocks[0].Quantity);
            Assert.Empty(await db.ItemWarehouseStocks.Where(stock => stock.ItemId == deletedItem.Id).ToListAsync());
            Assert.Equal(0m, (await db.Items.IgnoreQueryFilters().FirstAsync(item => item.Id == canonicalId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateIssueAsync_ItemMergeRepointsRentalBillingTemplateWithoutUnlinkingAssets()
    {
        PrepareAppRoot("georaeplan-integrity-item-merge-rental-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("91aaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Rental Template Customer");
            var canonical = CreateItem("91bbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Rental Duplicate Item", "A4", currentStock: 0m);
            var duplicate = CreateItem("91cccccc-cccc-cccc-cccc-cccccccccccc", "Rental Duplicate Item", "A4", currentStock: 0m);
            var invoiceId = Guid.Parse("91dddddd-dddd-dddd-dddd-dddddddddddd");
            var profileId = Guid.Parse("91eeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var assetId = Guid.Parse("91ffffff-ffff-ffff-ffff-ffffffffffff");
            var templateRowId = Guid.Parse("91000000-0000-0000-0000-000000000091");
            var unrelatedCatalogItemId = Guid.Parse("91000000-0000-0000-0000-000000000092");
            var otherOnlyProfileId = Guid.Parse("91000000-0000-0000-0000-000000000093");
            var malformedProfileId = Guid.Parse("91000000-0000-0000-0000-000000000094");
            var unknownRootProfileId = Guid.Parse("91000000-0000-0000-0000-000000000095");
            var emptyProfileId = Guid.Parse("91000000-0000-0000-0000-000000000096");
            var deletedProfileId = Guid.Parse("91000000-0000-0000-0000-000000000097");
            var deletedMalformedProfileId = Guid.Parse("91000000-0000-0000-0000-000000000098");
            var exactTemplateJson =
                "[{\"catalogitemid\":\"" + duplicate.Id.ToString("D") +
                "\",\"DisplayItemName\":\"Legacy duplicate display\",\"Specification\":\"Legacy duplicate spec\",\"BillingLineMode\":\"묶음\",\"Quantity\":1,\"UnitPrice\":12000,\"Amount\":12000,\"FutureTemplateProperty\":{\"Version\":2,\"Mode\":\"future\"}},{\"CatalogItemId\":\"" +
                unrelatedCatalogItemId.ToString("D") +
                "\",\"DisplayItemName\":\" rental duplicate item \",\"Specification\":\" a4 \",\"FutureRow\":\"preserve\"}]";
            var otherOnlyTemplateJson =
                " [ { \"CatalogItemId\" : \"" + unrelatedCatalogItemId.ToString("D") +
                "\", \"DisplayItemName\" : \" rental duplicate item \", \"Specification\" : \" a4 \", \"FutureRow\" : { \"Keep\" : true } } ] ";
            var malformedTemplateJson = "[{\"CatalogItemId\":\"" + unrelatedCatalogItemId.ToString("D") + "\"";
            var unknownRootTemplateJson = "{\"CatalogItemId\":\"" + unrelatedCatalogItemId.ToString("D") + "\",\"FutureRoot\":true}";
            var deletedTemplateJson = "[{\"CATALOGITEMID\":\"" + duplicate.Id.ToString("D") + "\",\"DisplayItemName\":\"Deleted legacy display\",\"FutureDeletedProperty\":\"keep\"}]";
            var deletedMalformedTemplateJson = "[{\"CatalogItemId\":\"" + unrelatedCatalogItemId.ToString("D") + "\",\"Broken\":]";

            db.Customers.Add(customer);
            db.Items.AddRange(canonical, duplicate);
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceDate = new DateOnly(2026, 6, 24),
                InvoiceNumber = "ITEM-MERGE-RENTAL-TEMPLATE-CANONICAL",
                IsDirty = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        InvoiceId = invoiceId,
                        ItemId = canonical.Id,
                        ItemNameOriginal = canonical.NameOriginal,
                        SpecificationOriginal = canonical.SpecificationOriginal,
                        Quantity = 1m
                    },
                    new LocalInvoiceLine
                    {
                        InvoiceId = invoiceId,
                        ItemId = canonical.Id,
                        ItemNameOriginal = canonical.NameOriginal,
                        SpecificationOriginal = canonical.SpecificationOriginal,
                        Quantity = 1m
                    },
                    new LocalInvoiceLine
                    {
                        InvoiceId = invoiceId,
                        ItemId = canonical.Id,
                        ItemNameOriginal = canonical.NameOriginal,
                        SpecificationOriginal = canonical.SpecificationOriginal,
                        Quantity = 1m
                    }
                }
            });
            db.RentalBillingProfiles.AddRange(
                CreateRentalBillingProfile(profileId, "template-exact", customer, duplicate, exactTemplateJson),
                CreateRentalBillingProfile(otherOnlyProfileId, "template-other-only", customer, duplicate, otherOnlyTemplateJson),
                CreateRentalBillingProfile(malformedProfileId, "template-malformed", customer, duplicate, malformedTemplateJson),
                CreateRentalBillingProfile(unknownRootProfileId, "template-unknown-root", customer, duplicate, unknownRootTemplateJson),
                CreateRentalBillingProfile(emptyProfileId, "template-empty", customer, duplicate, string.Empty),
                CreateRentalBillingProfile(deletedProfileId, "template-deleted", customer, duplicate, deletedTemplateJson, isDeleted: true),
                CreateRentalBillingProfile(deletedMalformedProfileId, "template-deleted-malformed", customer, duplicate, deletedMalformedTemplateJson, isDeleted: true));
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|TEST|ITEM-MERGE-RENTAL-TEMPLATE",
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                CurrentCustomerName = customer.NameOriginal,
                BillingProfileId = profileId,
                ItemId = duplicate.Id,
                ItemName = duplicate.NameOriginal,
                MonthlyFee = 12000m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);

            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var duplicateCandidate = comparison.Candidates.Single(candidate => candidate.ItemId == duplicate.Id);
            Assert.Equal(2, duplicateCandidate.RentalBillingTemplateCount);
            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(canonical.Id, result.EntityId);

            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.False(storedAsset.IsDeleted);
            Assert.Equal(profileId, storedAsset.BillingProfileId);
            Assert.Equal(canonical.Id, storedAsset.ItemId);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
            Assert.True(storedProfile.IsDirty);
            using var storedTemplate = System.Text.Json.JsonDocument.Parse(storedProfile.BillingTemplateJson);
            var templateItems = storedTemplate.RootElement.EnumerateArray().ToArray();
            Assert.Equal(2, templateItems.Length);
            var remappedTemplateItem = templateItems[0];
            Assert.Equal(canonical.Id.ToString("D"), remappedTemplateItem.GetProperty("catalogitemid").GetString());
            Assert.Equal(canonical.NameOriginal, remappedTemplateItem.GetProperty("DisplayItemName").GetString());
            Assert.Equal(canonical.SpecificationOriginal, remappedTemplateItem.GetProperty("Specification").GetString());
            Assert.Equal(2, remappedTemplateItem.GetProperty("FutureTemplateProperty").GetProperty("Version").GetInt32());
            Assert.Equal("future", remappedTemplateItem.GetProperty("FutureTemplateProperty").GetProperty("Mode").GetString());
            Assert.False(remappedTemplateItem.TryGetProperty("ItemId", out _));
            Assert.False(remappedTemplateItem.TryGetProperty("RepresentativeAssetId", out _));
            Assert.False(remappedTemplateItem.TryGetProperty("IncludedAssetIds", out _));
            Assert.Equal(
                "{\"CatalogItemId\":\"" + unrelatedCatalogItemId.ToString("D") + "\",\"DisplayItemName\":\" rental duplicate item \",\"Specification\":\" a4 \",\"FutureRow\":\"preserve\"}",
                templateItems[1].GetRawText());

            var unchangedProfiles = new[]
            {
                (otherOnlyProfileId, otherOnlyTemplateJson),
                (malformedProfileId, malformedTemplateJson),
                (unknownRootProfileId, unknownRootTemplateJson),
                (emptyProfileId, string.Empty),
                (deletedMalformedProfileId, deletedMalformedTemplateJson)
            };
            foreach (var (unchangedProfileId, originalTemplateJson) in unchangedProfiles)
            {
                var unchangedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                    .SingleAsync(profile => profile.Id == unchangedProfileId);
                Assert.Equal(originalTemplateJson, unchangedProfile.BillingTemplateJson);
                Assert.False(unchangedProfile.IsDirty);
            }

            var deletedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(profile => profile.Id == deletedProfileId);
            Assert.True(deletedProfile.IsDeleted);
            Assert.True(deletedProfile.IsDirty);
            using var storedDeletedTemplate = System.Text.Json.JsonDocument.Parse(deletedProfile.BillingTemplateJson);
            var deletedTemplateItem = Assert.Single(storedDeletedTemplate.RootElement.EnumerateArray().ToArray());
            Assert.Equal(canonical.Id.ToString("D"), deletedTemplateItem.GetProperty("CATALOGITEMID").GetString());
            Assert.Equal(canonical.NameOriginal, deletedTemplateItem.GetProperty("DisplayItemName").GetString());
            Assert.Equal("keep", deletedTemplateItem.GetProperty("FutureDeletedProperty").GetString());
            Assert.False(deletedTemplateItem.TryGetProperty("ItemId", out _));
            Assert.False(deletedTemplateItem.TryGetProperty("Specification", out _));
            Assert.False(deletedTemplateItem.TryGetProperty("RepresentativeAssetId", out _));
            Assert.False(deletedTemplateItem.TryGetProperty("IncludedAssetIds", out _));
            Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == canonical.Id)).IsDeleted);
            Assert.True((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, "MalformedD")]
    [InlineData(true, "MalformedD")]
    [InlineData(false, "NonArrayN")]
    [InlineData(true, "NonArrayN")]
    [InlineData(false, "MalformedUnicodeD")]
    [InlineData(true, "MalformedUnicodeD")]
    [InlineData(false, "NonArrayUnicodeN")]
    [InlineData(true, "NonArrayUnicodeN")]
    [InlineData(false, "MalformedX")]
    [InlineData(true, "MalformedX")]
    [InlineData(false, "NonArrayUnicodeX")]
    [InlineData(true, "NonArrayUnicodeX")]
    [InlineData(false, "AmbiguousD")]
    [InlineData(true, "AmbiguousD")]
    public async Task MergeDuplicateItemIssueAsync_RejectsUnparseableCandidateTemplateReferenceWithoutWrites(
        bool isDeleted,
        string templateShape)
    {
        PrepareAppRoot($"georaeplan-integrity-item-template-fail-closed-{templateShape}-{isDeleted}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("91a10000-0000-0000-0000-000000000001", "Template fail-closed customer");
            var canonical = CreateItem("91a10000-0000-0000-0000-000000000002", "Template fail-closed item", "A4", currentStock: 0m);
            var duplicate = CreateItem("91a10000-0000-0000-0000-000000000003", "Template fail-closed item", "A4", currentStock: 0m);
            var unicodeEscapedDuplicateD = string.Concat(
                duplicate.Id.ToString("D").Select(character => $"\\u{(int)character:x4}"));
            var unicodeEscapedDuplicateN = string.Concat(
                duplicate.Id.ToString("N").Select(character => $"\\u{(int)character:x4}"));
            var whitespaceDuplicateX = duplicate.Id.ToString("X")
                .Replace("{", "{ ", StringComparison.Ordinal)
                .Replace("}", " }", StringComparison.Ordinal)
                .Replace(",", " , ", StringComparison.Ordinal);
            var unicodeEscapedDuplicateX = string.Concat(
                whitespaceDuplicateX.Select(character => $"\\u{(int)character:x4}"));
            var templateJson = templateShape switch
            {
                "MalformedD" => $"[{{\"CatalogItemId\":\"{duplicate.Id:D}\"",
                "NonArrayN" => $"{{\"CatalogItemId\":\"{duplicate.Id:N}\",\"FutureRoot\":true}}",
                "MalformedUnicodeD" => $"[{{\"CatalogItemId\":\"{unicodeEscapedDuplicateD}\"",
                "NonArrayUnicodeN" => $"{{\"CatalogItemId\":\"{unicodeEscapedDuplicateN}\",\"FutureRoot\":true}}",
                "MalformedX" => $"[{{\"CatalogItemId\":\"{whitespaceDuplicateX}\"",
                "NonArrayUnicodeX" => $"{{\"CatalogItemId\":\"{unicodeEscapedDuplicateX}\",\"FutureRoot\":true}}",
                "AmbiguousD" => $"[{{\"CatalogItemId\":null,\"FutureItemReference\":\"{duplicate.Id:D}\"}}]",
                _ => throw new ArgumentOutOfRangeException(nameof(templateShape), templateShape, null)
            };
            var profileId = Guid.Parse("91a10000-0000-0000-0000-000000000004");

            db.Customers.Add(customer);
            db.Items.AddRange(canonical, duplicate);
            db.RentalBillingProfiles.Add(CreateRentalBillingProfile(
                profileId,
                $"template-fail-closed-{templateShape}-{isDeleted}",
                customer,
                duplicate,
                templateJson,
                isDeleted));
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Contains("해석할 수 없거나 모호한", result.Message, StringComparison.Ordinal);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == canonical.Id)).IsDeleted);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(profile => profile.Id == profileId);
            Assert.Equal(isDeleted, storedProfile.IsDeleted);
            Assert.Equal(templateJson, storedProfile.BillingTemplateJson);
            Assert.False(storedProfile.IsDirty);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RemapRentalBillingTemplateCatalogItemReference_NullInputIsPreserved()
    {
        var method = typeof(DataIntegrityIssueService).GetMethod(
            "RemapRentalBillingTemplateCatalogItemReference",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var duplicateId = Guid.Parse("91000000-0000-0000-0000-000000000099");
        var result = method!.Invoke(null, new object?[]
        {
            null,
            Guid.Parse("91000000-0000-0000-0000-000000000100"),
            new HashSet<Guid> { duplicateId },
            "Canonical name",
            "Canonical spec"
        });

        Assert.Null(result);
    }

    [Fact]
    public void RentalStateService_NormalizeTemplateAssetCoverage_PreservesExistingIncludedAssetIds()
    {
        var method = typeof(RentalStateService).GetMethod(
            "NormalizeTemplateAssetCoverage",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var existingAssetId = Guid.Parse("92000000-0000-0000-0000-000000000001");
        var linkedAssetId = Guid.Parse("92000000-0000-0000-0000-000000000002");
        var templateItems = new List<RentalBillingTemplateItemModel>
        {
            new()
            {
                DisplayItemName = "Bundle",
                IncludedAssetIds = [existingAssetId]
            }
        };

        var changed = Assert.IsType<bool>(method!.Invoke(null, new object?[] { templateItems, new List<Guid> { linkedAssetId } }));

        Assert.True(changed);
        Assert.Contains(existingAssetId, templateItems[0].IncludedAssetIds);
        Assert.Contains(linkedAssetId, templateItems[0].IncludedAssetIds);
    }

    [Fact]
    public async Task MergeDuplicateIssueAsync_RequiresInvoiceEditWhenItemMergeMovesInvoiceLines()
    {
        PrepareAppRoot("georaeplan-integrity-item-merge-invoice-permission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("0aaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "품목권한거래처");
            var canonical = CreateItem("0bbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "권한병합품목", "동일규격", currentStock: 0m);
            var duplicate = CreateItem("0ccccccc-cccc-cccc-cccc-cccccccccccc", "권한병합품목", "동일규격", currentStock: 0m);
            var canonicalInvoiceId = Guid.Parse("0ddddddd-dddd-dddd-dddd-dddddddddddd");
            var duplicateInvoiceId = Guid.Parse("0eeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
            var duplicateLineId = Guid.Parse("0fffffff-ffff-ffff-ffff-ffffffffffff");
            db.Customers.Add(customer);
            db.Items.AddRange(canonical, duplicate);
            db.Invoices.AddRange(
                new LocalInvoice
                {
                    Id = canonicalInvoiceId,
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    InvoiceDate = new DateOnly(2026, 6, 23),
                    InvoiceNumber = "ITEM-MERGE-PERM-CANONICAL",
                    IsDirty = false,
                    Lines =
                    {
                        new LocalInvoiceLine
                        {
                            InvoiceId = canonicalInvoiceId,
                            ItemId = canonical.Id,
                            ItemNameOriginal = canonical.NameOriginal,
                            SpecificationOriginal = canonical.SpecificationOriginal,
                            Quantity = 1m
                        },
                        new LocalInvoiceLine
                        {
                            InvoiceId = canonicalInvoiceId,
                            ItemId = canonical.Id,
                            ItemNameOriginal = canonical.NameOriginal,
                            SpecificationOriginal = canonical.SpecificationOriginal,
                            Quantity = 2m
                        }
                    }
                },
                new LocalInvoice
                {
                    Id = duplicateInvoiceId,
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    InvoiceDate = new DateOnly(2026, 6, 23),
                    InvoiceNumber = "ITEM-MERGE-PERM-DUPLICATE",
                    IsDirty = false,
                    Lines =
                    {
                        new LocalInvoiceLine
                        {
                            Id = duplicateLineId,
                            InvoiceId = duplicateInvoiceId,
                            ItemId = duplicate.Id,
                            ItemNameOriginal = duplicate.NameOriginal,
                            SpecificationOriginal = duplicate.SpecificationOriginal,
                            Quantity = 1m
                        }
                    }
                });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var itemOnlySession = CreateUserSession(AppPermissionNames.ItemEdit);

            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                itemOnlySession);

            Assert.False(result.Success);
            Assert.Contains("전표", result.Message);
            var storedDuplicate = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id);
            var storedDuplicateLine = await db.InvoiceLines.IgnoreQueryFilters().SingleAsync(line => line.Id == duplicateLineId);
            var storedDuplicateInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == duplicateInvoiceId);
            Assert.False(storedDuplicate.IsDeleted);
            Assert.False(storedDuplicate.IsDirty);
            Assert.Equal(duplicate.Id, storedDuplicateLine.ItemId);
            Assert.False(storedDuplicateInvoice.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_MergeDuplicateItems_PreservesItemsAndRentalReferencesForExplicitReview()
    {
        PrepareAppRoot("georaeplan-initializer-item-merge-rental-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("17111111-1111-1111-1111-111111111111", "Initializer Item Customer");
            var canonical = CreateItem("17222222-2222-2222-2222-222222222222", "Initializer Duplicate Item", "A4", currentStock: 0m);
            var duplicate = CreateItem("17333333-3333-3333-3333-333333333333", "Initializer Duplicate Item", "A4", currentStock: 0m);
            var profileId = Guid.Parse("17444444-4444-4444-4444-444444444444");
            var assetId = Guid.Parse("17555555-5555-5555-5555-555555555555");
            var templateRowId = Guid.Parse("17000000-0000-0000-0000-000000000017");
            db.Customers.Add(customer);
            db.Items.AddRange(canonical, duplicate);
            db.Invoices.AddRange(
                CreateInitializerInvoice("17666666-6666-6666-6666-666666666666", customer.Id, "LOCAL-INIT-ITEM-MERGE-1"),
                CreateInitializerInvoice("17777777-7777-7777-7777-777777777777", customer.Id, "LOCAL-INIT-ITEM-MERGE-2"));
            db.InvoiceLines.AddRange(
                new LocalInvoiceLine
                {
                    InvoiceId = Guid.Parse("17666666-6666-6666-6666-666666666666"),
                    ItemId = canonical.Id,
                    ItemNameOriginal = canonical.NameOriginal,
                    SpecificationOriginal = canonical.SpecificationOriginal,
                    Quantity = 1m
                },
                new LocalInvoiceLine
                {
                    InvoiceId = Guid.Parse("17777777-7777-7777-7777-777777777777"),
                    ItemId = canonical.Id,
                    ItemNameOriginal = canonical.NameOriginal,
                    SpecificationOriginal = canonical.SpecificationOriginal,
                    Quantity = 1m
                },
                new LocalInvoiceLine
                {
                    InvoiceId = Guid.Parse("17777777-7777-7777-7777-777777777777"),
                    ItemId = canonical.Id,
                    ItemNameOriginal = canonical.NameOriginal,
                    SpecificationOriginal = canonical.SpecificationOriginal,
                    Quantity = 1m
                });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                ItemName = duplicate.NameOriginal,
                BillingTemplateJson = System.Text.Json.JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        ItemId = templateRowId,
                        CatalogItemId = duplicate.Id,
                        DisplayItemName = duplicate.NameOriginal,
                        BillingLineMode = "묶음",
                        RepresentativeAssetId = assetId,
                        Quantity = 1m,
                        UnitPrice = 15000m,
                        Amount = 15000m,
                        IncludedAssetIds = [assetId]
                    }
                }),
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|TEST|INIT-ITEM-MERGE-RENTAL-TEMPLATE",
                CustomerId = customer.Id,
                CustomerName = customer.NameOriginal,
                CurrentCustomerName = customer.NameOriginal,
                BillingProfileId = profileId,
                ItemId = duplicate.Id,
                ItemName = duplicate.NameOriginal,
                MonthlyFee = 15000m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var method = typeof(LocalDbInitializer).GetMethod(
                "MergeDuplicateItemsAsync",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);

            var task = method!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(task);
            await task!;
            await db.SaveChangesAsync();

            var storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id == canonical.Id || item.Id == duplicate.Id)
                .ToListAsync();
            Assert.Equal(2, storedItems.Count);
            Assert.All(storedItems, item => Assert.False(item.IsDeleted));
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(duplicate.Id, storedAsset.ItemId);
            Assert.False(storedAsset.IsDirty);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
            var templateItems = System.Text.Json.JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(storedProfile.BillingTemplateJson) ?? [];
            var templateItem = Assert.Single(templateItems);
            Assert.Equal(templateRowId, templateItem.ItemId);
            Assert.Equal(duplicate.Id, templateItem.CatalogItemId);
            Assert.Contains(assetId, templateItem.IncludedAssetIds);
            Assert.False(storedProfile.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_ItemDuplicateComparisonExposesReferenceBreakdownAndSafetyBlocks()
    {
        PrepareAppRoot("georaeplan-integrity-item-comparison-blocks");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("61111111-1111-1111-1111-111111111111", "비교 거래처");
            var itemA = CreateItem("62222222-2222-2222-2222-222222222222", "비교 품목", "동일 규격", currentStock: 1m);
            var itemB = CreateItem("63333333-3333-3333-3333-333333333333", "비교 품목", "동일 규격", currentStock: 0m);
            itemA.CategoryName = "분류 A";
            itemB.CategoryName = "분류 B";
            itemB.IsDirty = true;
            var invoiceId = Guid.Parse("64444444-4444-4444-4444-444444444444");
            db.Customers.Add(customer);
            db.Items.AddRange(itemA, itemB);
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceDate = new DateOnly(2026, 8, 5),
                InvoiceNumber = "COMPARE-ITEM-1",
                IsDirty = false,
                Lines =
                {
                    new LocalInvoiceLine
                    {
                        InvoiceId = invoiceId,
                        ItemId = itemB.Id,
                        ItemNameOriginal = itemB.NameOriginal,
                        SpecificationOriginal = itemB.SpecificationOriginal,
                        Quantity = 1m
                    }
                }
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.Parse("65555555-5555-5555-5555-555555555555"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|COMPARE|ITEM",
                ItemId = itemA.Id,
                ItemName = itemA.NameOriginal,
                IsDirty = false
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                EntityName = nameof(LocalItem),
                EntityId = itemB.Id,
                MutationId = Guid.NewGuid().ToString("N"),
                Status = "Prepared"
            });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());
            var issue = Assert.Single(scan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            Assert.False(comparison.CanMerge);
            Assert.True(issue.CanReviewDuplicateCandidates);
            Assert.False(issue.CanMergeDuplicates);
            Assert.Equal("후보 비교", issue.DuplicateReviewActionText);
            Assert.Equal(64, comparison.SnapshotToken.Length);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.CategoryName), comparison.BlockingConflictFields);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.CurrentStock), comparison.BlockingConflictFields);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.IsDirty), comparison.BlockingConflictFields);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.UnresolvedOutboxCount), comparison.BlockingConflictFields);
            Assert.Equal(2, comparison.Candidates.Count);
            Assert.Equal(1, comparison.Candidates.Single(candidate => candidate.ItemId == itemA.Id).RentalAssetCount);
            var itemBCandidate = comparison.Candidates.Single(candidate => candidate.ItemId == itemB.Id);
            Assert.Equal(1, itemBCandidate.InvoiceLineCount);
            Assert.Equal(1, itemBCandidate.UnresolvedOutboxCount);
            Assert.Contains("전표 1", itemBCandidate.ReferenceSummary);
            Assert.False(string.IsNullOrWhiteSpace(itemBCandidate.MasterDataSummary));
            Assert.False(string.IsNullOrWhiteSpace(itemBCandidate.AssetDataSummary));
            Assert.False(string.IsNullOrWhiteSpace(itemBCandidate.SyncStateText));

            var blockedResult = await service.MergeDuplicateItemIssueAsync(
                issue,
                comparison.RecommendedCanonicalId!.Value,
                comparison.SnapshotToken,
                CreateAdminSession());
            Assert.False(blockedResult.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemA.Id)).IsDeleted);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_ItemDuplicateWithCustomPriceGrade_BlocksMergeWithoutOrphaningGrade()
    {
        PrepareAppRoot("georaeplan-integrity-item-price-grade-block");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("65111111-1111-1111-1111-111111111111", "등급가 품목", "A4", currentStock: 0m);
            var itemB = CreateItem("65222222-2222-2222-2222-222222222222", "등급가 품목", "A4", currentStock: 0m);
            var gradeId = Guid.Parse("65333333-3333-3333-3333-333333333333");
            db.Items.AddRange(itemA, itemB);
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = gradeId,
                ItemId = itemB.Id,
                PriceGradeOptionId = Guid.Parse("65444444-4444-4444-4444-444444444444"),
                PriceGradeName = "VIP",
                UnitPrice = 1234m,
                IsActive = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            Assert.False(comparison.CanMerge);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.ItemPriceGradeCount), comparison.BlockingConflictFields);
            var gradeCandidate = comparison.Candidates.Single(candidate => candidate.ItemId == itemB.Id);
            Assert.Equal(1, gradeCandidate.ItemPriceGradeCount);
            Assert.Contains("사용자등급가 1", gradeCandidate.ReferenceSummary);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemA.Id)).IsDeleted);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);
            var storedGrade = await db.ItemPriceGrades.IgnoreQueryFilters().AsNoTracking().SingleAsync(grade => grade.Id == gradeId);
            Assert.Equal(itemB.Id, storedGrade.ItemId);
            Assert.False(storedGrade.IsDeleted);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Canonical")]
    [InlineData("Duplicate")]
    [InlineData("Collision")]
    public async Task ScanAsync_ItemDuplicateWithDeletedPriceGrade_BlocksMergeAndTracksSnapshot(
        string gradePlacement)
    {
        PrepareAppRoot($"georaeplan-integrity-item-deleted-price-grade-{gradePlacement}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("65511111-1111-1111-1111-111111111111", "삭제 등급가 품목", "A4", currentStock: 0m);
            var itemB = CreateItem("65622222-2222-2222-2222-222222222222", "삭제 등급가 품목", "A4", currentStock: 0m);
            var optionId = Guid.Parse("65733333-3333-3333-3333-333333333333");
            var grades = new List<LocalItemPriceGrade>();

            if (gradePlacement is "Canonical" or "Collision")
            {
                grades.Add(new LocalItemPriceGrade
                {
                    Id = Guid.Parse("65844444-4444-4444-4444-444444444444"),
                    ItemId = itemA.Id,
                    PriceGradeOptionId = optionId,
                    PriceGradeName = "Deleted VIP A",
                    UnitPrice = 1234m,
                    IsActive = true,
                    IsDeleted = true,
                    Revision = 7,
                    UpdatedAtUtc = new DateTime(2026, 8, 8, 1, 0, 0, DateTimeKind.Utc),
                    IsDirty = false
                });
            }

            if (gradePlacement is "Duplicate" or "Collision")
            {
                grades.Add(new LocalItemPriceGrade
                {
                    Id = Guid.Parse("65955555-5555-5555-5555-555555555555"),
                    ItemId = itemB.Id,
                    PriceGradeOptionId = optionId,
                    PriceGradeName = "Deleted VIP B",
                    UnitPrice = 2345m,
                    IsActive = true,
                    IsDeleted = true,
                    Revision = 9,
                    UpdatedAtUtc = new DateTime(2026, 8, 8, 2, 0, 0, DateTimeKind.Utc),
                    IsDirty = false
                });
            }

            db.Items.AddRange(itemA, itemB);
            db.ItemPriceGrades.AddRange(grades);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var firstScan = await service.ScanAsync(session);
            var firstIssue = Assert.Single(firstScan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var firstComparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(firstIssue.ItemDuplicateComparison);

            Assert.False(firstComparison.CanMerge);
            Assert.Contains(nameof(DataIntegrityItemDuplicateCandidate.ItemPriceGradeCount), firstComparison.BlockingConflictFields);
            Assert.Equal(gradePlacement is "Canonical" or "Collision" ? 1 : 0,
                firstComparison.Candidates.Single(candidate => candidate.ItemId == itemA.Id).ItemPriceGradeCount);
            Assert.Equal(gradePlacement is "Duplicate" or "Collision" ? 1 : 0,
                firstComparison.Candidates.Single(candidate => candidate.ItemId == itemB.Id).ItemPriceGradeCount);

            grades[0].Revision++;
            grades[0].UpdatedAtUtc = grades[0].UpdatedAtUtc.AddMinutes(1);
            await db.SaveChangesAsync();

            var refreshedScan = await service.ScanAsync(session);
            var refreshedIssue = Assert.Single(refreshedScan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var refreshedComparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(refreshedIssue.ItemDuplicateComparison);
            Assert.NotEqual(firstComparison.SnapshotToken, refreshedComparison.SnapshotToken);

            var result = await service.MergeDuplicateItemIssueAsync(
                refreshedIssue,
                itemA.Id,
                refreshedComparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemA.Id)).IsDeleted);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);
            var storedGrades = await db.ItemPriceGrades.IgnoreQueryFilters().AsNoTracking().OrderBy(grade => grade.Id).ToListAsync();
            Assert.Equal(grades.Count, storedGrades.Count);
            Assert.All(storedGrades, grade => Assert.True(grade.IsDeleted));
            Assert.Contains(storedGrades, grade => grade.ItemId == grades[0].ItemId);
            if (gradePlacement == "Collision")
                Assert.Equal(2, storedGrades.Select(grade => grade.ItemId).Distinct().Count());
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_RespectsExplicitNonRecommendedCanonicalAndAuditsIt()
    {
        PrepareAppRoot("georaeplan-integrity-item-explicit-canonical");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("66111111-1111-1111-1111-111111111111", "대표 선택 품목", "A4", currentStock: 0m);
            var itemB = CreateItem("66222222-2222-2222-2222-222222222222", "대표 선택 품목", "A4", currentStock: 0m);
            itemA.NameMatchKey = string.Empty;
            itemA.SpecificationMatchKey = "STALE-SPEC-KEY";
            itemB.CategoryName = "복사될 분류";
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);
            Assert.Equal(itemB.Id, comparison.RecommendedCanonicalId);

            var result = await service.MergeDuplicateItemIssueAsync(issue, itemA.Id, comparison.SnapshotToken, session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(itemA.Id, result.EntityId);
            var storedA = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemA.Id);
            var storedB = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id);
            Assert.False(storedA.IsDeleted);
            Assert.Equal("복사될 분류", storedA.CategoryName);
            Assert.Equal(RentalCatalogValueNormalizer.NormalizeLooseKey(storedA.NameOriginal), storedA.NameMatchKey);
            Assert.Equal(RentalCatalogValueNormalizer.NormalizeLooseKey(storedA.SpecificationOriginal), storedA.SpecificationMatchKey);
            Assert.True(storedB.IsDeleted);
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(log => log.Action == "DataIntegrityDuplicateMerge" && log.EntityName == "Item");
            Assert.Equal(itemA.Id.ToString("D"), audit.EntityId);
            Assert.Contains(itemA.Id.ToString("D"), audit.BeforeJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_RejectsNonMemberAndStaleSnapshotWithoutMergeWrites()
    {
        PrepareAppRoot("georaeplan-integrity-item-stale-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("67111111-1111-1111-1111-111111111111", "스냅샷 품목", "S", currentStock: 0m);
            var itemB = CreateItem("67222222-2222-2222-2222-222222222222", "스냅샷 품목", "S", currentStock: 0m);
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            var nonMemberResult = await service.MergeDuplicateItemIssueAsync(
                issue,
                Guid.Parse("67333333-3333-3333-3333-333333333333"),
                comparison.SnapshotToken,
                session);
            Assert.False(nonMemberResult.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemA.Id)).IsDeleted);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);

            var changedItemB = await db.Items
                .IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemB.Id);
            changedItemB.Revision++;
            changedItemB.UpdatedAtUtc = changedItemB.UpdatedAtUtc.AddMinutes(1);
            await db.SaveChangesAsync();
            var staleResult = await service.MergeDuplicateItemIssueAsync(issue, itemA.Id, comparison.SnapshotToken, session);

            Assert.False(staleResult.Success);
            Assert.Contains("스냅샷", staleResult.Message);
            var items = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(items[itemA.Id].IsDeleted);
            Assert.False(items[itemB.Id].IsDeleted);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_RejectsEqualCountReferenceIdentitySwap()
    {
        PrepareAppRoot("georaeplan-integrity-item-reference-identity-swap");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("67411111-1111-1111-1111-111111111111", "참조 교환 품목", "S", currentStock: 0m);
            var itemB = CreateItem("67422222-2222-2222-2222-222222222222", "참조 교환 품목", "S", currentStock: 0m);
            var invoiceA = CreateInitializerInvoice("67433333-3333-3333-3333-333333333333", Guid.NewGuid(), "REF-SWAP-A");
            var invoiceB = CreateInitializerInvoice("67444444-4444-4444-4444-444444444444", Guid.NewGuid(), "REF-SWAP-B");
            var lineA = new LocalInvoiceLine
            {
                Id = Guid.Parse("67455555-5555-5555-5555-555555555555"),
                InvoiceId = invoiceA.Id,
                ItemId = itemA.Id,
                ItemNameOriginal = itemA.NameOriginal,
                SpecificationOriginal = itemA.SpecificationOriginal
            };
            var lineB = new LocalInvoiceLine
            {
                Id = Guid.Parse("67466666-6666-6666-6666-666666666666"),
                InvoiceId = invoiceB.Id,
                ItemId = itemB.Id,
                ItemNameOriginal = itemB.NameOriginal,
                SpecificationOriginal = itemB.SpecificationOriginal
            };
            db.Items.AddRange(itemA, itemB);
            db.Invoices.AddRange(invoiceA, invoiceB);
            db.InvoiceLines.AddRange(lineA, lineB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var review = await service.PrepareItemDuplicateReviewAsync(issue, session);
            Assert.True(review.CanMerge, review.BlockingReasonText);

            lineA.ItemId = itemB.Id;
            lineB.ItemId = itemA.Id;
            await db.SaveChangesAsync();

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                review.Comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Contains("스냅샷", result.Message);
            var storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(storedItems[itemA.Id].IsDeleted);
            Assert.False(storedItems[itemB.Id].IsDeleted);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_DeletedRentalTemplateReferenceParticipatesInSnapshot()
    {
        PrepareAppRoot("georaeplan-integrity-item-deleted-template-snapshot");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem(Guid.NewGuid().ToString("D"), "Deleted template snapshot item", "A4", 0m);
            var itemB = CreateItem(Guid.NewGuid().ToString("D"), "Deleted template snapshot item", "A4", 0m);
            var profileId = Guid.NewGuid();
            db.Items.AddRange(itemA, itemB);
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"deleted-template-snapshot-{profileId:N}",
                BillingTemplateJson = $"[{{\"CatalogItemId\":\"{itemB.Id:D}\"}}]",
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var issue = Assert.Single((await service.ScanAsync(session)).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.Equal(1, comparison.Candidates.Single(candidate => candidate.ItemId == itemB.Id).RentalBillingTemplateCount);

            var profile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            profile.Revision++;
            profile.UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1);
            await db.SaveChangesAsync();

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Contains("스냅샷", result.Message, StringComparison.Ordinal);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_WaitsForRunningSyncOperationBeforeMutation()
    {
        PrepareAppRoot("georaeplan-integrity-item-sync-lock");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("68111111-1111-1111-1111-111111111111", "동기화 직렬화 품목", "S", currentStock: 0m);
            var itemB = CreateItem("68222222-2222-2222-2222-222222222222", "동기화 직렬화 품목", "S", currentStock: 0m);
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            var syncLockEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSyncLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runningSync = SyncService.ExecuteWithGlobalSyncOperationLockAsync(
                async () =>
                {
                    syncLockEntered.TrySetResult();
                    await releaseSyncLock.Task;
                    return true;
                },
                CancellationToken.None);
            try
            {
                await syncLockEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                var mergeTask = service.MergeDuplicateItemIssueAsync(
                    issue,
                    itemA.Id,
                    comparison.SnapshotToken,
                    session);
                await Task.Delay(100);
                Assert.False(mergeTask.IsCompleted);

                releaseSyncLock.TrySetResult();
                Assert.True(await runningSync.WaitAsync(TimeSpan.FromSeconds(2)));
                var result = await mergeTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(result.Success, result.Message);
                Assert.Equal(itemA.Id, result.EntityId);
            }
            finally
            {
                releaseSyncLock.TrySetResult();
                await runningSync.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_FaultBeforeSaveRollsBackAndClearsStagedChanges()
    {
        PrepareAppRoot("georaeplan-integrity-item-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("69111111-1111-1111-1111-111111111111", "롤백 품목", "S", currentStock: 0m);
            var itemB = CreateItem("69222222-2222-2222-2222-222222222222", "롤백 품목", "S", currentStock: 0m);
            var assetId = Guid.Parse("69333333-3333-3333-3333-333333333333");
            db.Items.AddRange(itemA, itemB);
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|ROLLBACK|ITEM",
                ItemId = itemB.Id,
                ItemName = itemB.NameOriginal,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);
            service.TestOnlyBeforeDuplicateMergeSaveAsync = _ =>
                Task.FromException(new InvalidOperationException("duplicate-merge-fault"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.MergeDuplicateItemIssueAsync(
                    issue,
                    itemA.Id,
                    comparison.SnapshotToken,
                    session));
            Assert.Equal("duplicate-merge-fault", exception.Message);
            Assert.False(db.ChangeTracker.HasChanges());

            var storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(storedItems[itemA.Id].IsDeleted);
            Assert.False(storedItems[itemB.Id].IsDeleted);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(itemB.Id, storedAsset.ItemId);
            Assert.False(storedAsset.IsDirty);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());

            db.AuditLogs.Add(new LocalAuditLog
            {
                EntityName = "RollbackProof",
                EntityId = Guid.NewGuid().ToString("D"),
                Action = "UnrelatedSaveAfterRollback",
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(storedItems[itemB.Id].IsDeleted);
            storedAsset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(itemB.Id, storedAsset.ItemId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_ActiveLocalEditorBlocksPreflightAndFinalSave()
    {
        PrepareAppRoot("georaeplan-integrity-item-active-local-editor");

        IDisposable? lateRegistration = null;
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("6a111111-1111-1111-1111-111111111111", "Active editor item", "A4", 0m);
            var itemB = CreateItem("6a222222-2222-2222-2222-222222222222", "Active editor item", "A4", 0m);
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);

            using (EntityEditSessionMonitor.TestOnlyRegisterLocalSubject(
                       new EditSessionSubject("Item", itemB.Id.ToString("D"), itemB.NameOriginal)))
            {
                var blockedReview = await service.PrepareItemDuplicateReviewAsync(issue, session);
                Assert.False(blockedReview.CanMerge);
                Assert.Contains("편집 중", blockedReview.BlockingReasonText, StringComparison.Ordinal);
            }

            var review = await service.PrepareItemDuplicateReviewAsync(issue, session);
            Assert.True(review.CanMerge, review.BlockingReasonText);

            service.TestOnlyBeforeDuplicateMergeSaveAsync = _ =>
            {
                lateRegistration = EntityEditSessionMonitor.TestOnlyRegisterLocalSubject(
                    new EditSessionSubject("Item", itemA.Id.ToString("D"), itemA.NameOriginal));
                return Task.CompletedTask;
            };

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                review.Comparison.SnapshotToken,
                session);

            Assert.False(result.Success);
            Assert.Contains("편집 중", result.Message, StringComparison.Ordinal);
            var stored = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(stored[itemA.Id].IsDeleted);
            Assert.False(stored[itemB.Id].IsDeleted);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            lateRegistration?.Dispose();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertItemAsync_StaleDeletedDuplicateCannotResurrectAfterMerge()
    {
        PrepareAppRoot("georaeplan-integrity-item-stale-resurrection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem("6b111111-1111-1111-1111-111111111111", "Stale editor item", "A4", 0m);
            var itemB = CreateItem("6b222222-2222-2222-2222-222222222222", "Stale editor item", "A4", 0m);
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();
            var staleEditorCopy = (LocalItem)db.Entry(itemB).CurrentValues.ToObject();

            var dispatcher = new SyncRequestDispatcher();
            var session = CreateAdminSession();
            var integrity = CreateLegacyLocalItemMergeService(db, dispatcher);
            var issue = Assert.Single(
                (await integrity.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var review = await integrity.PrepareItemDuplicateReviewAsync(issue, session);
            Assert.True(review.CanMerge, review.BlockingReasonText);

            var merge = await integrity.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                review.Comparison.SnapshotToken,
                session);
            Assert.True(merge.Success, merge.Message);
            Assert.True((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);

            var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => local.UpsertItemAsync(staleEditorCopy, session));

            Assert.Contains("휴지통", error.Message, StringComparison.Ordinal);
            var deletedDuplicate = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id);
            Assert.True(deletedDuplicate.IsDeleted);
            Assert.Equal(itemA.Id, merge.EntityId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ScanAsync_ItemDuplicateCandidateRequiresExactSameNameAndSpecification()
    {
        PrepareAppRoot("georaeplan-integrity-item-exact-name-spec");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.Items.AddRange(
                CreateItem("71111111-1111-1111-1111-111111111111", "복합기", "A4", currentStock: 0m),
                CreateItem("72222222-2222-2222-2222-222222222222", "복합기", "A3", currentStock: 0m),
                CreateItem("73333333-3333-3333-3333-333333333333", "복 합기", "A4", currentStock: 0m),
                CreateItem("74444444-4444-4444-4444-444444444444", "중복품목", "동일규격", currentStock: 0m),
                CreateItem("75555555-5555-5555-5555-555555555555", "중복품목", "동일규격", currentStock: 0m));
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());

            var itemIssues = scan.Issues
                .Where(issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate)
                .ToList();
            var issue = Assert.Single(itemIssues);
            Assert.Equal("중복품목", issue.ItemName);
            Assert.DoesNotContain(itemIssues, current => current.RelatedEntityIds.Any(id =>
                id == Guid.Parse("71111111-1111-1111-1111-111111111111") ||
                id == Guid.Parse("72222222-2222-2222-2222-222222222222") ||
                id == Guid.Parse("73333333-3333-3333-3333-333333333333")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("TenantOnly")]
    [InlineData("Shared")]
    [InlineData("Invalid")]
    [InlineData("Conflict")]
    [InlineData("SingleOffice")]
    public async Task MergeDuplicateItemIssueAsync_CandidateStoredScopeEvidenceIsFailClosed(string scopeVariant)
    {
        PrepareAppRoot($"georaeplan-integrity-item-candidate-scope-{scopeVariant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemA = CreateItem(Guid.NewGuid().ToString("D"), "Scope candidate item", "A4", 0m);
            var itemB = CreateItem(Guid.NewGuid().ToString("D"), "Scope candidate item", "A4", 0m);
            ApplyItemScopeVariant(itemA, scopeVariant);
            ApplyItemScopeVariant(itemB, scopeVariant);
            db.Items.AddRange(itemA, itemB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scanSession = string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal)
                ? CreateItworldGlobalAdminSession()
                : CreateAdminSession();
            var issue = Assert.Single((await service.ScanAsync(scanSession)).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            if (!string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal))
            {
                var denied = await service.MergeDuplicateItemIssueAsync(
                    issue,
                    itemA.Id,
                    comparison.SnapshotToken,
                    CreateUsenetOfficeAdminSession());
                Assert.False(denied.Success);
                Assert.All(await db.Items.IgnoreQueryFilters().AsNoTracking().ToListAsync(), item => Assert.False(item.IsDeleted));
                Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
            }

            var allowedSession = scopeVariant switch
            {
                "TenantOnly" or "Shared" => CreateUsenetTenantAllAdminSession(),
                "SingleOffice" => CreateItworldOfficeAdminSession(),
                _ => CreateAdminSession()
            };
            var allowed = await service.MergeDuplicateItemIssueAsync(
                issue,
                itemA.Id,
                comparison.SnapshotToken,
                allowedSession);

            Assert.True(allowed.Success, allowed.Message);
            Assert.True((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemB.Id)).IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("TenantOnly")]
    [InlineData("Shared")]
    [InlineData("Invalid")]
    [InlineData("Conflict")]
    [InlineData("SingleOffice")]
    public async Task MergeDuplicateIssueAsync_CustomerCandidateStoredScopeEvidenceIsFailClosed(string scopeVariant)
    {
        PrepareAppRoot($"georaeplan-integrity-customer-candidate-scope-{scopeVariant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerA = CreateCustomer(Guid.NewGuid().ToString("D"), "Scope candidate customer");
            var customerB = CreateCustomer(Guid.NewGuid().ToString("D"), "Scope candidate customer");
            ApplyCustomerScopeVariant(customerA, scopeVariant);
            ApplyCustomerScopeVariant(customerB, scopeVariant);
            db.Customers.AddRange(customerA, customerB);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scanSession = string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal)
                ? CreateItworldGlobalAdminSession()
                : CreateAdminSession();
            var issue = Assert.Single((await service.ScanAsync(scanSession)).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate);

            if (!string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal))
            {
                var denied = await service.MergeDuplicateIssueAsync(issue, CreateUsenetOfficeAdminSession());
                Assert.False(denied.Success);
                Assert.All(await db.Customers.IgnoreQueryFilters().AsNoTracking().ToListAsync(), customer => Assert.False(customer.IsDeleted));
                Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
            }

            var allowedSession = scopeVariant switch
            {
                "TenantOnly" or "Shared" => CreateUsenetTenantAllAdminSession(),
                "SingleOffice" => CreateItworldOfficeAdminSession(),
                _ => CreateAdminSession()
            };
            var allowed = await service.MergeDuplicateIssueAsync(issue, allowedSession);

            Assert.True(allowed.Success, allowed.Message);
            Assert.Single(await db.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => customer.IsDeleted).ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Invoice", "TenantOnly")]
    [InlineData("Transaction", "TenantOnly")]
    [InlineData("ProfileActive", "TenantOnly")]
    [InlineData("ProfileDeleted", "TenantOnly")]
    [InlineData("Asset", "TenantOnly")]
    [InlineData("History", "TenantOnly")]
    [InlineData("Invoice", "Invalid")]
    [InlineData("Transaction", "Invalid")]
    [InlineData("ProfileActive", "Invalid")]
    [InlineData("ProfileDeleted", "Invalid")]
    [InlineData("Asset", "Invalid")]
    [InlineData("History", "Invalid")]
    [InlineData("Invoice", "Shared")]
    [InlineData("ProfileActive", "Shared")]
    [InlineData("Invoice", "Conflict")]
    [InlineData("ProfileActive", "Conflict")]
    [InlineData("ProfileActive", "SingleOffice")]
    [InlineData("ProfileDeleted", "SingleOffice")]
    [InlineData("ProfileActive", "CustomManagement")]
    [InlineData("Asset", "CustomManagement")]
    public async Task MergeDuplicateIssueAsync_CustomerSideEffectStoredScopeIsFailClosed(
        string sideEffectKind,
        string scopeVariant)
    {
        PrepareAppRoot($"georaeplan-integrity-customer-side-scope-{sideEffectKind}-{scopeVariant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateCustomer(Guid.NewGuid().ToString("D"), "Scoped side-effect customer");
            var duplicate = CreateCustomer(Guid.NewGuid().ToString("D"), "Scoped side-effect customer");
            if (string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal))
            {
                SetConcreteCustomerScope(canonical, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
                SetConcreteCustomerScope(duplicate, TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld);
            }

            db.Customers.AddRange(canonical, duplicate);
            db.Invoices.AddRange(
                CreateScopedInvoice(Guid.NewGuid(), canonical.Id, "SCOPE-CANONICAL-1", canonical.TenantCode, canonical.OfficeCode, canonical.ResponsibleOfficeCode),
                CreateScopedInvoice(Guid.NewGuid(), canonical.Id, "SCOPE-CANONICAL-2", canonical.TenantCode, canonical.OfficeCode, canonical.ResponsibleOfficeCode));
            var linkedId = AddCustomerMergeSideEffect(db, sideEffectKind, scopeVariant, duplicate);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scanSession = string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal)
                ? CreateItworldGlobalAdminSession()
                : CreateAdminSession();
            var issue = Assert.Single((await service.ScanAsync(scanSession)).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate);

            if (!string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal) &&
                !string.Equals(scopeVariant, "CustomManagement", StringComparison.Ordinal))
            {
                var denied = await service.MergeDuplicateIssueAsync(issue, CreateUsenetOfficeAdminSession());
                Assert.False(denied.Success);
                Assert.False((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == duplicate.Id)).IsDeleted);
                Assert.Equal(duplicate.Id, await ReadCustomerMergeSideEffectCustomerIdAsync(db, sideEffectKind, linkedId));
                Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
            }

            var allowedSession = scopeVariant switch
            {
                "TenantOnly" or "Shared" => CreateUsenetTenantAllAdminSession(),
                "SingleOffice" => CreateItworldOfficeAdminSession(),
                "CustomManagement" => CreateUsenetOfficeAdminSession(),
                _ => CreateAdminSession()
            };
            var allowed = await service.MergeDuplicateIssueAsync(issue, allowedSession);

            Assert.True(allowed.Success, allowed.Message);
            Assert.Equal(canonical.Id, allowed.EntityId);
            Assert.True((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == duplicate.Id)).IsDeleted);
            Assert.Equal(canonical.Id, await ReadCustomerMergeSideEffectCustomerIdAsync(db, sideEffectKind, linkedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Deleted")]
    [InlineData("OutOfScope")]
    [InlineData("Shared")]
    public async Task MergeDuplicateIssueAsync_CustomerMasterReferenceIsValidatedBeforeCopy(string masterVariant)
    {
        PrepareAppRoot($"georaeplan-integrity-customer-master-scope-{masterVariant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateCustomer(Guid.NewGuid().ToString("D"), "Customer master guarded merge");
            var duplicate = CreateCustomer(Guid.NewGuid().ToString("D"), "Customer master guarded merge");
            var masterId = Guid.NewGuid();
            duplicate.CustomerMasterId = masterId;
            db.Customers.AddRange(canonical, duplicate);
            db.Invoices.AddRange(
                CreateInitializerInvoice(Guid.NewGuid().ToString("D"), canonical.Id, "MASTER-CANONICAL-1"),
                CreateInitializerInvoice(Guid.NewGuid().ToString("D"), canonical.Id, "MASTER-CANONICAL-2"));
            if (!string.Equals(masterVariant, "Missing", StringComparison.Ordinal))
            {
                db.CustomerMasters.Add(new LocalCustomerMaster
                {
                    Id = masterId,
                    TenantCode = string.Equals(masterVariant, "OutOfScope", StringComparison.Ordinal)
                        ? TenantScopeCatalog.Itworld
                        : TenantScopeCatalog.UsenetGroup,
                    OfficeCode = masterVariant switch
                    {
                        "OutOfScope" => OfficeCodeCatalog.Itworld,
                        "Shared" => OfficeCodeCatalog.Shared,
                        _ => OfficeCodeCatalog.Usenet
                    },
                    NameOriginal = "Guarded customer master",
                    NameMatchKey = "GUARDEDCUSTOMERMASTER",
                    IsDeleted = string.Equals(masterVariant, "Deleted", StringComparison.Ordinal),
                    IsDirty = false
                });
            }

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var issue = Assert.Single((await service.ScanAsync(CreateAdminSession())).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.CustomerDuplicateCandidate);
            var deniedSession = masterVariant switch
            {
                "Missing" or "Deleted" => CreateAdminSession(),
                _ => CreateUsenetOfficeAdminSession()
            };
            var denied = await service.MergeDuplicateIssueAsync(issue, deniedSession);

            Assert.False(denied.Success);
            Assert.False((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == duplicate.Id)).IsDeleted);
            Assert.Null((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == canonical.Id)).CustomerMasterId);
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());

            if (masterVariant is "Missing" or "Deleted")
                return;

            var allowedSession = string.Equals(masterVariant, "Shared", StringComparison.Ordinal)
                ? CreateUsenetTenantAllAdminSession()
                : CreateAdminSession();
            var allowed = await service.MergeDuplicateIssueAsync(issue, allowedSession);
            Assert.True(allowed.Success, allowed.Message);
            Assert.Equal(masterId, (await db.Customers.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(customer => customer.Id == canonical.Id)).CustomerMasterId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Invoice", "TenantOnly")]
    [InlineData("Asset", "TenantOnly")]
    [InlineData("History", "TenantOnly")]
    [InlineData("ProfileActive", "TenantOnly")]
    [InlineData("ProfileDeleted", "TenantOnly")]
    [InlineData("Invoice", "Invalid")]
    [InlineData("Asset", "Invalid")]
    [InlineData("History", "Invalid")]
    [InlineData("ProfileActive", "Invalid")]
    [InlineData("ProfileDeleted", "Invalid")]
    [InlineData("Invoice", "Shared")]
    [InlineData("ProfileActive", "Shared")]
    [InlineData("Invoice", "Conflict")]
    [InlineData("ProfileActive", "Conflict")]
    [InlineData("ProfileActive", "SingleOffice")]
    [InlineData("ProfileDeleted", "SingleOffice")]
    [InlineData("ProfileActive", "CustomManagement")]
    [InlineData("Asset", "CustomManagement")]
    public async Task MergeDuplicateItemIssueAsync_SideEffectStoredScopeIsFailClosed(
        string sideEffectKind,
        string scopeVariant)
    {
        PrepareAppRoot($"georaeplan-integrity-item-side-scope-{sideEffectKind}-{scopeVariant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var isItworld = string.Equals(scopeVariant, "SingleOffice", StringComparison.Ordinal);
            var tenantCode = isItworld ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
            var officeCode = isItworld ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet;
            var customer = CreateCustomer(Guid.NewGuid().ToString("D"), "Scoped item side-effect customer");
            SetConcreteCustomerScope(customer, tenantCode, officeCode);
            var canonical = CreateItem(Guid.NewGuid().ToString("D"), "Scoped side-effect item", "A4", 0m);
            var duplicate = CreateItem(Guid.NewGuid().ToString("D"), "Scoped side-effect item", "A4", 0m);
            canonical.TenantCode = tenantCode;
            canonical.OfficeCode = officeCode;
            duplicate.TenantCode = tenantCode;
            duplicate.OfficeCode = officeCode;
            var canonicalInvoice = CreateScopedInvoice(
                Guid.NewGuid(),
                customer.Id,
                "ITEM-SCOPE-CANONICAL",
                tenantCode,
                officeCode,
                officeCode);
            canonicalInvoice.Lines.Add(new LocalInvoiceLine
            {
                InvoiceId = canonicalInvoice.Id,
                ItemId = canonical.Id,
                ItemNameOriginal = canonical.NameOriginal,
                SpecificationOriginal = canonical.SpecificationOriginal,
                Quantity = 1m
            });
            canonicalInvoice.Lines.Add(new LocalInvoiceLine
            {
                InvoiceId = canonicalInvoice.Id,
                ItemId = canonical.Id,
                ItemNameOriginal = canonical.NameOriginal,
                SpecificationOriginal = canonical.SpecificationOriginal,
                Quantity = 1m
            });
            db.Customers.Add(customer);
            db.Items.AddRange(canonical, duplicate);
            db.Invoices.Add(canonicalInvoice);
            var linkedId = AddItemMergeSideEffect(db, sideEffectKind, scopeVariant, customer, duplicate, officeCode, tenantCode);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var scanSession = isItworld ? CreateItworldGlobalAdminSession() : CreateAdminSession();
            var issue = Assert.Single((await service.ScanAsync(scanSession)).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            if (!isItworld && !string.Equals(scopeVariant, "CustomManagement", StringComparison.Ordinal))
            {
                var denied = await service.MergeDuplicateItemIssueAsync(
                    issue,
                    canonical.Id,
                    comparison.SnapshotToken,
                    CreateUsenetOfficeAdminSession());
                Assert.False(denied.Success);
                Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
                Assert.Equal(duplicate.Id, await ReadItemMergeSideEffectItemIdAsync(db, sideEffectKind, linkedId));
                Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
            }

            var allowedSession = scopeVariant switch
            {
                "TenantOnly" or "Shared" => CreateUsenetTenantAllAdminSession(),
                "SingleOffice" => CreateItworldOfficeAdminSession(),
                "CustomManagement" => CreateUsenetOfficeAdminSession(),
                _ => CreateAdminSession()
            };
            var allowed = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                allowedSession);

            Assert.True(allowed.Success, allowed.Message);
            Assert.True((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
            Assert.Equal(canonical.Id, await ReadItemMergeSideEffectItemIdAsync(db, sideEffectKind, linkedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("InvoiceLine")]
    [InlineData("InvoiceLineSerial")]
    [InlineData("TransferLine")]
    public async Task MergeDuplicateItemIssueAsync_RejectsEmptyParentReferencesWithoutWrites(string referenceKind)
    {
        PrepareAppRoot($"georaeplan-integrity-item-empty-parent-{referenceKind}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateItem(Guid.NewGuid().ToString("D"), "Empty parent item", "A4", 0m);
            var duplicate = CreateItem(Guid.NewGuid().ToString("D"), "Empty parent item", "A4", 0m);
            var referenceId = Guid.NewGuid();
            db.Items.AddRange(canonical, duplicate);
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            switch (referenceKind)
            {
                case "InvoiceLine":
                    db.InvoiceLines.Add(new LocalInvoiceLine
                    {
                        Id = referenceId,
                        InvoiceId = Guid.Empty,
                        ItemId = duplicate.Id,
                        ItemNameOriginal = duplicate.NameOriginal,
                        SpecificationOriginal = duplicate.SpecificationOriginal,
                        Quantity = 1m
                    });
                    break;
                case "InvoiceLineSerial":
                    db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
                    {
                        Id = referenceId,
                        InvoiceId = Guid.Empty,
                        InvoiceLineId = Guid.NewGuid(),
                        ItemId = duplicate.Id,
                        SerialNumber = $"EMPTY-{referenceId:N}"
                    });
                    break;
                case "TransferLine":
                    db.InventoryTransferLines.Add(new LocalInventoryTransferLine
                    {
                        Id = referenceId,
                        TransferId = Guid.Empty,
                        ItemId = duplicate.Id,
                        ItemNameOriginal = duplicate.NameOriginal,
                        Quantity = 1m
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(referenceKind), referenceKind, null);
            }

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var issue = Assert.Single((await service.ScanAsync(CreateAdminSession())).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
            Assert.Equal(duplicate.Id, await ReadEmptyParentReferenceItemIdAsync(db, referenceKind, referenceId));
            Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Movement")]
    [InlineData("StockLayer")]
    [InlineData("SerialLedger")]
    [InlineData("Stock")]
    [InlineData("Transfer")]
    public async Task MergeDuplicateItemIssueAsync_InvalidWarehouseEvidenceIsGlobalOnly(string referenceKind)
    {
        PrepareAppRoot($"georaeplan-integrity-item-invalid-warehouse-{referenceKind}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateItem(Guid.NewGuid().ToString("D"), "Invalid warehouse item", "A4", 0m);
            var duplicate = CreateItem(Guid.NewGuid().ToString("D"), "Invalid warehouse item", "A4", 0m);
            var referenceId = Guid.NewGuid();
            db.Items.AddRange(canonical, duplicate);
            AddInvalidWarehouseItemReference(db, referenceKind, referenceId, duplicate);
            await db.SaveChangesAsync();

            var service = CreateLegacyLocalItemMergeService(db, new SyncRequestDispatcher());
            var issue = Assert.Single((await service.ScanAsync(CreateAdminSession())).Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            foreach (var deniedSession in new[] { CreateUsenetOfficeAdminSession(), CreateUsenetTenantAllAdminSession() })
            {
                var denied = await service.MergeDuplicateItemIssueAsync(
                    issue,
                    canonical.Id,
                    comparison.SnapshotToken,
                    deniedSession);
                Assert.False(denied.Success);
                Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
                Assert.Equal(duplicate.Id, await ReadInvalidWarehouseReferenceItemIdAsync(db, referenceKind, referenceId));
                Assert.Empty(await db.AuditLogs.AsNoTracking().Where(log => log.Action == "DataIntegrityDuplicateMerge").ToListAsync());
            }

            var allowed = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                CreateAdminSession());
            Assert.True(allowed.Success, allowed.Message);
            Assert.Equal(canonical.Id, await ReadInvalidWarehouseReferenceItemIdAsync(db, referenceKind, referenceId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void DataIntegrityIssueWindow_ProvidesHorizontalScrollDecisionInfoAndMergeAction()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityIssueWindow.xaml"));

        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("판단/참조", xaml, StringComparison.Ordinal);
        Assert.Contains("삭제/병합 판단 정보", xaml, StringComparison.Ordinal);
        Assert.Contains("MergeSelectedButton_Click", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DataIntegrityInfoSeverity_IsDisplayedAndFilteredAsReferenceNotWarning()
    {
        var detail = new DataIntegrityIssueDetail { Severity = "Info" };
        var summary = new DataIntegrityIssueSummary { Severity = "Info" };
        Assert.Equal("참고", detail.SeverityDisplay);
        Assert.Equal("참고", summary.SeverityDisplay);

        var viewModelSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "DataIntegrityViewModels.cs"));
        var alertXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml"));
        var detailXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityIssueWindow.xaml"));

        Assert.Contains("\"참고\"", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("issue.Severity, \"Info\"", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("확인 항목", alertXaml, StringComparison.Ordinal);
        Assert.Contains("확인 항목과 참고 정보", detailXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettingsDataIntegrityWindow_WiresFixAndMergeHandlers()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.Sync.cs"));

        Assert.Contains("window.FixRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("window.MergeRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("MergeDataIntegrityDuplicateAsync(args.Issue, viewModel, window)", source, StringComparison.Ordinal);
        Assert.Contains("OpenDataIntegrityFixTargetAsync(args.Issue, window)", source, StringComparison.Ordinal);
    }

    private static LocalCustomer CreateCustomer(string id, string name, string businessNumber = "")
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
            BusinessNumber = businessNumber,
            IsDirty = false
        };

    private static LocalInvoice CreateInitializerInvoice(string id, Guid customerId, string invoiceNumber)
        => new()
        {
            Id = Guid.Parse(id),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceDate = new DateOnly(2026, 6, 22),
            InvoiceNumber = invoiceNumber,
            IsDirty = false
        };

    private static LocalRentalAssetAssignmentHistory CreateInitializerAssignmentHistory(Guid id, Guid customerId)
        => new()
        {
            Id = id,
            AssetId = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            CustomerName = "AUTO MERGE CUSTOMER",
            InstallLocation = "Initializer history site",
            ItemName = "Initializer history item",
            ManagementNumber = "LOCAL-INIT-HISTORY-001",
            IsCurrent = false,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalItem CreateItem(string id, string name, string spec, decimal currentStock)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
            SpecificationOriginal = spec,
            SpecificationMatchKey = spec,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = currentStock,
            IsDirty = false
        };

    private static void ApplyItemScopeVariant(LocalItem item, string scopeVariant)
    {
        switch (scopeVariant)
        {
            case "TenantOnly":
                item.TenantCode = TenantScopeCatalog.UsenetGroup;
                item.OfficeCode = string.Empty;
                break;
            case "Shared":
                item.TenantCode = TenantScopeCatalog.UsenetGroup;
                item.OfficeCode = OfficeCodeCatalog.Shared;
                break;
            case "Invalid":
                item.TenantCode = "INVALID-TENANT";
                item.OfficeCode = "INVALID-OFFICE";
                break;
            case "Conflict":
                item.TenantCode = TenantScopeCatalog.Itworld;
                item.OfficeCode = OfficeCodeCatalog.Usenet;
                break;
            case "SingleOffice":
                item.TenantCode = TenantScopeCatalog.Itworld;
                item.OfficeCode = string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scopeVariant), scopeVariant, null);
        }
    }

    private static void ApplyCustomerScopeVariant(LocalCustomer customer, string scopeVariant)
    {
        switch (scopeVariant)
        {
            case "TenantOnly":
                customer.TenantCode = TenantScopeCatalog.UsenetGroup;
                customer.OfficeCode = string.Empty;
                customer.ResponsibleOfficeCode = string.Empty;
                break;
            case "Shared":
                customer.TenantCode = TenantScopeCatalog.UsenetGroup;
                customer.OfficeCode = OfficeCodeCatalog.Usenet;
                customer.ResponsibleOfficeCode = OfficeCodeCatalog.Shared;
                break;
            case "Invalid":
                customer.TenantCode = "INVALID-TENANT";
                customer.OfficeCode = "INVALID-OFFICE";
                customer.ResponsibleOfficeCode = "INVALID-OFFICE";
                break;
            case "Conflict":
                customer.TenantCode = TenantScopeCatalog.Itworld;
                customer.OfficeCode = OfficeCodeCatalog.Usenet;
                customer.ResponsibleOfficeCode = OfficeCodeCatalog.Usenet;
                break;
            case "SingleOffice":
                customer.TenantCode = TenantScopeCatalog.Itworld;
                customer.OfficeCode = string.Empty;
                customer.ResponsibleOfficeCode = string.Empty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scopeVariant), scopeVariant, null);
        }
    }

    private static void SetConcreteCustomerScope(LocalCustomer customer, string tenantCode, string officeCode)
    {
        customer.TenantCode = tenantCode;
        customer.OfficeCode = officeCode;
        customer.ResponsibleOfficeCode = officeCode;
    }

    private static (string TenantCode, string OfficeCode, string ResponsibleOfficeCode, string ManagementCompanyCode)
        GetStoredScopeVariant(string scopeVariant)
        => scopeVariant switch
        {
            "TenantOnly" => (TenantScopeCatalog.UsenetGroup, string.Empty, string.Empty, string.Empty),
            "Shared" => (TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Shared, OfficeCodeCatalog.Usenet),
            "Invalid" => ("INVALID-TENANT", "INVALID-OFFICE", "INVALID-OFFICE", "INVALID-OFFICE"),
            "Conflict" => (TenantScopeCatalog.Itworld, OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Usenet),
            "SingleOffice" => (TenantScopeCatalog.Itworld, string.Empty, string.Empty, string.Empty),
            "CustomManagement" => (TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Usenet, "CUSTOM-MANAGEMENT-COMPANY"),
            _ => throw new ArgumentOutOfRangeException(nameof(scopeVariant), scopeVariant, null)
        };

    private static LocalInvoice CreateScopedInvoice(
        Guid id,
        Guid customerId,
        string invoiceNumber,
        string tenantCode,
        string officeCode,
        string responsibleOfficeCode)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            InvoiceDate = new DateOnly(2026, 8, 8),
            InvoiceNumber = invoiceNumber,
            IsDirty = false
        };

    private static Guid AddCustomerMergeSideEffect(
        LocalDbContext db,
        string sideEffectKind,
        string scopeVariant,
        LocalCustomer duplicate)
    {
        var id = Guid.NewGuid();
        var scope = GetStoredScopeVariant(scopeVariant);
        switch (sideEffectKind)
        {
            case "Invoice":
                db.Invoices.Add(CreateScopedInvoice(
                    id,
                    duplicate.Id,
                    $"SCOPE-SIDE-{id:N}",
                    scope.TenantCode,
                    scope.OfficeCode,
                    scope.ResponsibleOfficeCode));
                break;
            case "Transaction":
                db.Transactions.Add(new LocalTransaction
                {
                    Id = id,
                    CustomerId = duplicate.Id,
                    TenantCode = scope.TenantCode,
                    OfficeCode = scope.OfficeCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    TransactionDate = new DateOnly(2026, 8, 8),
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                    SettlementAmount = 1_000m,
                    BankReceipt = 1_000m,
                    ReceiptTotal = 1_000m,
                    IsDirty = false
                });
                break;
            case "ProfileActive":
            case "ProfileDeleted":
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = id,
                    TenantCode = scope.TenantCode,
                    OfficeCode = scope.OfficeCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    ManagementCompanyCode = scope.ManagementCompanyCode,
                    ProfileKey = $"customer-side-{id:N}",
                    CustomerId = duplicate.Id,
                    CustomerName = duplicate.NameOriginal,
                    BillingTemplateJson = "[]",
                    IsDeleted = string.Equals(sideEffectKind, "ProfileDeleted", StringComparison.Ordinal),
                    IsDirty = false
                });
                break;
            case "Asset":
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = id,
                    TenantCode = scope.TenantCode,
                    OfficeCode = scope.OfficeCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    ManagementCompanyCode = scope.ManagementCompanyCode,
                    AssetKey = $"CUSTOMER-SIDE|{id:N}",
                    CustomerId = duplicate.Id,
                    CustomerName = duplicate.NameOriginal,
                    CurrentCustomerName = duplicate.NameOriginal,
                    IsDirty = false
                });
                break;
            case "History":
                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = id,
                    AssetId = Guid.NewGuid(),
                    CustomerId = duplicate.Id,
                    TenantCode = scope.TenantCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    CustomerName = duplicate.NameOriginal,
                    IsCurrent = false,
                    IsDirty = false
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sideEffectKind), sideEffectKind, null);
        }

        return id;
    }

    private static async Task<Guid?> ReadCustomerMergeSideEffectCustomerIdAsync(
        LocalDbContext db,
        string sideEffectKind,
        Guid entityId)
        => sideEffectKind switch
        {
            "Invoice" => await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(invoice => invoice.Id == entityId)
                .Select(invoice => (Guid?)invoice.CustomerId)
                .SingleAsync(),
            "Transaction" => await db.Transactions.IgnoreQueryFilters().AsNoTracking()
                .Where(transaction => transaction.Id == entityId)
                .Select(transaction => (Guid?)transaction.CustomerId)
                .SingleAsync(),
            "ProfileActive" or "ProfileDeleted" => await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .Where(profile => profile.Id == entityId)
                .Select(profile => profile.CustomerId)
                .SingleAsync(),
            "Asset" => await db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(asset => asset.Id == entityId)
                .Select(asset => asset.CustomerId)
                .SingleAsync(),
            "History" => await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking()
                .Where(history => history.Id == entityId)
                .Select(history => history.CustomerId)
                .SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(sideEffectKind), sideEffectKind, null)
        };

    private static Guid AddItemMergeSideEffect(
        LocalDbContext db,
        string sideEffectKind,
        string scopeVariant,
        LocalCustomer customer,
        LocalItem duplicate,
        string concreteOfficeCode,
        string concreteTenantCode)
    {
        var id = Guid.NewGuid();
        var scope = GetStoredScopeVariant(scopeVariant);
        switch (sideEffectKind)
        {
            case "Invoice":
            {
                var invoice = CreateScopedInvoice(
                    id,
                    customer.Id,
                    $"ITEM-SIDE-{id:N}",
                    scope.TenantCode,
                    scope.OfficeCode,
                    scope.ResponsibleOfficeCode);
                invoice.Lines.Add(new LocalInvoiceLine
                {
                    InvoiceId = invoice.Id,
                    ItemId = duplicate.Id,
                    ItemNameOriginal = duplicate.NameOriginal,
                    SpecificationOriginal = duplicate.SpecificationOriginal,
                    Quantity = 1m
                });
                db.Invoices.Add(invoice);
                break;
            }
            case "Asset":
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = id,
                    TenantCode = scope.TenantCode,
                    OfficeCode = scope.OfficeCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    ManagementCompanyCode = scope.ManagementCompanyCode,
                    AssetKey = $"ITEM-SIDE|{id:N}",
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    CurrentCustomerName = customer.NameOriginal,
                    ItemId = duplicate.Id,
                    ItemName = duplicate.NameOriginal,
                    IsDirty = false
                });
                break;
            case "History":
            {
                var assetId = id;
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = assetId,
                    TenantCode = concreteTenantCode,
                    OfficeCode = concreteOfficeCode,
                    ResponsibleOfficeCode = concreteOfficeCode,
                    ManagementCompanyCode = concreteOfficeCode,
                    AssetKey = $"ITEM-HISTORY-SIDE|{assetId:N}",
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    CurrentCustomerName = customer.NameOriginal,
                    ItemId = duplicate.Id,
                    ItemName = duplicate.NameOriginal,
                    IsDirty = false
                });
                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    CustomerId = customer.Id,
                    TenantCode = scope.TenantCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    CustomerName = customer.NameOriginal,
                    ItemName = duplicate.NameOriginal,
                    IsCurrent = false,
                    IsDirty = false
                });
                break;
            }
            case "ProfileActive":
            case "ProfileDeleted":
                db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                {
                    Id = id,
                    TenantCode = scope.TenantCode,
                    OfficeCode = scope.OfficeCode,
                    ResponsibleOfficeCode = scope.ResponsibleOfficeCode,
                    ManagementCompanyCode = scope.ManagementCompanyCode,
                    ProfileKey = $"item-side-{id:N}",
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    BillingTemplateJson = $"[{{\"CatalogItemId\":\"{duplicate.Id:D}\",\"DisplayItemName\":\"{duplicate.NameOriginal}\"}}]",
                    IsDeleted = string.Equals(sideEffectKind, "ProfileDeleted", StringComparison.Ordinal),
                    IsDirty = false
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sideEffectKind), sideEffectKind, null);
        }

        return id;
    }

    private static async Task<Guid?> ReadItemMergeSideEffectItemIdAsync(
        LocalDbContext db,
        string sideEffectKind,
        Guid entityId)
    {
        switch (sideEffectKind)
        {
            case "Invoice":
                return await db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    .Where(line => line.InvoiceId == entityId)
                    .Select(line => line.ItemId)
                    .SingleAsync();
            case "Asset":
            case "History":
                return await db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                    .Where(asset => asset.Id == entityId)
                    .Select(asset => asset.ItemId)
                    .SingleAsync();
            case "ProfileActive":
            case "ProfileDeleted":
            {
                var json = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                    .Where(profile => profile.Id == entityId)
                    .Select(profile => profile.BillingTemplateJson)
                    .SingleAsync();
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return Guid.Parse(document.RootElement[0].GetProperty("CatalogItemId").GetString()!);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(sideEffectKind), sideEffectKind, null);
        }
    }

    private static async Task<Guid?> ReadEmptyParentReferenceItemIdAsync(
        LocalDbContext db,
        string referenceKind,
        Guid referenceId)
        => referenceKind switch
        {
            "InvoiceLine" => await db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                .Where(line => line.Id == referenceId)
                .Select(line => line.ItemId)
                .SingleAsync(),
            "InvoiceLineSerial" => await db.InvoiceLineSerials.AsNoTracking()
                .Where(serial => serial.Id == referenceId)
                .Select(serial => serial.ItemId)
                .SingleAsync(),
            "TransferLine" => await db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
                .Where(line => line.Id == referenceId)
                .Select(line => line.ItemId)
                .SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(referenceKind), referenceKind, null)
        };

    private static void AddInvalidWarehouseItemReference(
        LocalDbContext db,
        string referenceKind,
        Guid referenceId,
        LocalItem duplicate)
    {
        const string invalidWarehouseCode = "NOT_ITWORLD_VALID";
        switch (referenceKind)
        {
            case "Movement":
                db.InventoryMovements.Add(new LocalInventoryMovement
                {
                    Id = referenceId,
                    ItemId = duplicate.Id,
                    WarehouseCode = invalidWarehouseCode,
                    MovementType = "InvalidWarehouse",
                    QuantityDelta = 0m,
                    OccurredDate = new DateOnly(2026, 8, 8),
                    IsActive = true
                });
                break;
            case "StockLayer":
                db.StockLayers.Add(new LocalStockLayer
                {
                    Id = referenceId,
                    ItemId = duplicate.Id,
                    WarehouseCode = invalidWarehouseCode,
                    ReceiptDate = new DateOnly(2026, 8, 8),
                    OriginalQuantity = 0m,
                    RemainingQuantity = 0m
                });
                break;
            case "SerialLedger":
                db.SerialLedgers.Add(new LocalSerialLedger
                {
                    Id = referenceId,
                    ItemId = duplicate.Id,
                    WarehouseCode = invalidWarehouseCode,
                    SerialNumber = $"INVALID-{referenceId:N}",
                    Status = "Available"
                });
                break;
            case "Stock":
                db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
                {
                    ItemId = duplicate.Id,
                    WarehouseCode = invalidWarehouseCode,
                    Quantity = 0m
                });
                break;
            case "Transfer":
                db.InventoryTransfers.Add(new LocalInventoryTransfer
                {
                    Id = referenceId,
                    FromWarehouseCode = "USENET_BROKEN",
                    ToWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Lines =
                    {
                        new LocalInventoryTransferLine
                        {
                            TransferId = referenceId,
                            ItemId = duplicate.Id,
                            ItemNameOriginal = duplicate.NameOriginal,
                            Quantity = 1m
                        }
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(referenceKind), referenceKind, null);
        }
    }

    private static async Task<Guid?> ReadInvalidWarehouseReferenceItemIdAsync(
        LocalDbContext db,
        string referenceKind,
        Guid referenceId)
        => referenceKind switch
        {
            "Movement" => await db.InventoryMovements.AsNoTracking()
                .Where(movement => movement.Id == referenceId)
                .Select(movement => movement.ItemId)
                .SingleAsync(),
            "StockLayer" => await db.StockLayers.AsNoTracking()
                .Where(layer => layer.Id == referenceId)
                .Select(layer => layer.ItemId)
                .SingleAsync(),
            "SerialLedger" => await db.SerialLedgers.AsNoTracking()
                .Where(ledger => ledger.Id == referenceId)
                .Select(ledger => ledger.ItemId)
                .SingleAsync(),
            "Stock" => await db.ItemWarehouseStocks.AsNoTracking()
                .Where(stock => stock.ItemId != Guid.Empty && stock.WarehouseCode == "NOT_ITWORLD_VALID")
                .Select(stock => (Guid?)stock.ItemId)
                .SingleAsync(),
            "Transfer" => await db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
                .Where(line => line.TransferId == referenceId)
                .Select(line => line.ItemId)
                .SingleAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(referenceKind), referenceKind, null)
        };

    private static LocalRentalBillingProfile CreateRentalBillingProfile(
        Guid id,
        string profileKey,
        LocalCustomer customer,
        LocalItem item,
        string billingTemplateJson,
        bool isDeleted = false)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = profileKey,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            ItemName = item.NameOriginal,
            BillingTemplateJson = billingTemplateJson,
            IsDeleted = isDeleted,
            IsDirty = false
        };

    private static DataIntegrityIssueService CreateLegacyLocalItemMergeService(
        LocalDbContext db,
        SyncRequestDispatcher dispatcher)
    {
        var service = new DataIntegrityIssueService(db, dispatcher);
#if DEBUG
        service.TestOnlyUseLegacyLocalItemDuplicateMerge = true;
#endif
        return service;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateUserSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static SessionState CreateAdminScopeSession(
        string username,
        string tenantCode,
        string officeCode,
        string scopeType)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = username,
            Role = DomainConstants.RoleAdmin,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType
        });
        return session;
    }

    private static SessionState CreateUsenetOfficeAdminSession()
        => CreateAdminScopeSession(
            "usenet-office-admin",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);

    private static SessionState CreateUsenetTenantAllAdminSession()
        => CreateAdminScopeSession(
            "usenet-tenant-admin",
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);

    private static SessionState CreateItworldOfficeAdminSession()
        => CreateAdminScopeSession(
            "itworld-office-admin",
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.ScopeOfficeOnly);

    private static SessionState CreateItworldGlobalAdminSession()
        => CreateAdminScopeSession(
            "itworld-global-admin",
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.ScopeAdmin);

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
