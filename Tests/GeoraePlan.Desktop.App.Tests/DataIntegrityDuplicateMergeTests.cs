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
            var service = new DataIntegrityIssueService(db, dispatcher);
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

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
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

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
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
    public async Task MergeDuplicateIssueAsync_ItemMergeMovesReferencesAndAggregatesWarehouseStock()
    {
        PrepareAppRoot("georaeplan-integrity-item-merge");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = CreateCustomer("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "거래처");
            var itemA = CreateItem("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "중복품목", "A4", currentStock: 1m);
            var itemB = CreateItem("cccccccc-cccc-cccc-cccc-cccccccccccc", "중복품목", "A4", currentStock: 2m);
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
                    Quantity = 1m
                },
                new LocalItemWarehouseStock
                {
                    ItemId = itemB.Id,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 2m
                });
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateAdminSession();
            var scan = await service.ScanAsync(session);
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            Assert.True(issue.CanMergeDuplicates);
            Assert.Contains("창고별 재고 합계", issue.ReviewInfoDisplay);

            var result = await service.MergeDuplicateIssueAsync(issue, session);

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
            Assert.Equal(3m, canonicalStocks[0].Quantity);
            Assert.Empty(await db.ItemWarehouseStocks.Where(stock => stock.ItemId == deletedItem.Id).ToListAsync());
            Assert.Equal(3m, (await db.Items.IgnoreQueryFilters().FirstAsync(item => item.Id == canonicalId)).CurrentStock);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
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

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var scan = await service.ScanAsync(CreateAdminSession());
            var issue = Assert.Single(scan.Issues, issue => issue.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var itemOnlySession = CreateUserSession(AppPermissionNames.ItemEdit);

            var result = await service.MergeDuplicateIssueAsync(issue, itemOnlySession);

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

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
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
