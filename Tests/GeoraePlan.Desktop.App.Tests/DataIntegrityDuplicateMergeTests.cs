using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    private static LocalCustomer CreateCustomer(string id, string name)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
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
