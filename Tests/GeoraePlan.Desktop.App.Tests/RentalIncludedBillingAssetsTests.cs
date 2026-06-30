using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalIncludedBillingAssetsTests
{
    [Fact]
    public async Task GetIncludedBillingAssetsAsync_ExplicitIncludedAssetOutsideProfileSortWindowIsStillIncluded()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-explicit-window");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2100000-1111-4444-8888-000000000001");
            for (var index = 0; index < 300; index++)
            {
                db.RentalAssets.Add(CreateRentalAsset(
                    $"A Profile Customer {index:D4}",
                    $"A-{index:D4}",
                    profileId));
            }

            var explicitAsset = CreateRentalAsset(
                "ZZZ Explicit Customer",
                "Z-9999",
                billingProfileId: null);
            db.RentalAssets.Add(explicitAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetIncludedBillingAssetsAsync(
                profileId,
                new[] { explicitAsset.Id },
                customerId: null,
                officeCode: OfficeCodeCatalog.Usenet,
                CreateAdminSession());

            Assert.Contains(rows, asset => asset.Id == explicitAsset.Id);
            Assert.Equal(301, rows.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_BatchesManyIncludedAssetReferencesForLinkSync()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-save-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var includedAssetIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var asset = CreateRentalAsset(
                    $"Batch Sync Customer {index:D4}",
                    $"B-{index:D4}",
                    billingProfileId: null);
                includedAssetIds.Add(asset.Id);
                db.RentalAssets.Add(asset);
            }

            await db.SaveChangesAsync();

            var profileId = Guid.Parse("f2300000-1111-4444-8888-000000000001");
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Batch Sync Customer",
                ItemName = "Rental Copier Bundle",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Rental Copier Bundle",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        Amount = 100_000m,
                        IncludedAssetIds = includedAssetIds
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);

            var linkedAssetIds = await db.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => asset.BillingProfileId == profileId)
                .Select(asset => asset.Id)
                .ToListAsync();
            Assert.Equal(includedAssetIds.Count, linkedAssetIds.Count);
            Assert.Equal(includedAssetIds.OrderBy(id => id), linkedAssetIds.OrderBy(id => id));

            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var persistedItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(
                persistedProfile.BillingTemplateJson) ?? [];
            Assert.Equal(includedAssetIds.Count, persistedItems.Single().IncludedAssetIds.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_DoesNotLinkUnselectedSameCustomerAssets()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-selected-only");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2310000-1111-4444-8888-000000000001");
            var otherProfileId = Guid.Parse("f2310000-1111-4444-8888-000000000099");
            var selectedAssetId = Guid.Parse("f2310000-1111-4444-8888-0000000000a1");
            var unselectedSameCustomerAssetId = Guid.Parse("f2310000-1111-4444-8888-0000000000b1");
            var otherProfileAssetId = Guid.Parse("f2310000-1111-4444-8888-0000000000c1");

            db.RentalAssets.AddRange(
                CreateRentalAsset(
                    "Selected Only Customer",
                    "SEL-001",
                    billingProfileId: null,
                    selectedAssetId,
                    monthlyFee: 80_000m),
                CreateRentalAsset(
                    "Selected Only Customer",
                    "SEL-002",
                    billingProfileId: null,
                    unselectedSameCustomerAssetId,
                    monthlyFee: 90_000m),
                CreateRentalAsset(
                    "Selected Only Customer",
                    "SEL-003",
                    billingProfileId: otherProfileId,
                    otherProfileAssetId,
                    monthlyFee: 110_000m));
            await db.SaveChangesAsync();

            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Selected Only Customer",
                ItemName = "Selected Only Rental",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Selected Only Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = [selectedAssetId]
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);

            var selectedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == selectedAssetId);
            var unselectedSameCustomerAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == unselectedSameCustomerAssetId);
            var otherProfileAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == otherProfileAssetId);

            Assert.Equal(profileId, selectedAsset.BillingProfileId);
            Assert.Null(unselectedSameCustomerAsset.BillingProfileId);
            Assert.Equal(otherProfileId, otherProfileAsset.BillingProfileId);

            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var persistedItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(
                persistedProfile.BillingTemplateJson) ?? [];
            var persistedAssetIds = persistedItems
                .SelectMany(item => item.IncludedAssetIds)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            Assert.Equal([selectedAssetId], persistedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_LinksAssetLinkEditWithoutAddingItToDisplayItems()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-edit-not-selection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2311000-1111-4444-8888-000000000001");
            var selectedAssetId = Guid.Parse("f2311000-1111-4444-8888-0000000000a1");
            var internalOnlyAssetId = Guid.Parse("f2311000-1111-4444-8888-0000000000b1");

            db.RentalAssets.AddRange(
                CreateRentalAsset(
                    "Edit Is Not Selection Customer",
                    "EDIT-SEL-001",
                    billingProfileId: null,
                    selectedAssetId,
                    monthlyFee: 80_000m),
                CreateRentalAsset(
                    "Edit Is Not Selection Customer",
                    "EDIT-INTERNAL-001",
                    billingProfileId: null,
                    internalOnlyAssetId,
                    monthlyFee: 90_000m));
            await db.SaveChangesAsync();

            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Edit Is Not Selection Customer",
                ItemName = "Selected Rental Only",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Selected Rental Only",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = [selectedAssetId]
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = internalOnlyAssetId,
                        CustomerName = "Edit Is Not Selection Customer",
                        InstallLocation = "Internal Pool",
                        ItemCategoryName = "A3컬러복합기",
                        Manufacturer = "삼성전자",
                        ItemName = "SL-X4220RX",
                        MachineNumber = "SER-EDIT-001",
                        PurchaseVendor = "삼성전자(직판)",
                        ContractMonths = 12,
                        ContractDate = new DateOnly(2026, 1, 15),
                        RentalEndDate = new DateOnly(2026, 12, 31),
                        PurchaseDate = new DateOnly(2025, 12, 20),
                        InstallDate = new DateOnly(2026, 1, 20),
                        FreeSupplyItems = "토너",
                        PaidSupplyItems = "용지",
                        MonthlyFee = 123_456m,
                        Notes = "internal only"
                    }
                ]);

            Assert.True(result.Success, result.Message);

            var selectedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == selectedAssetId);
            var internalOnlyAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == internalOnlyAssetId);

            Assert.Equal(profileId, selectedAsset.BillingProfileId);
            Assert.Equal(profileId, internalOnlyAsset.BillingProfileId);
            Assert.Equal("Internal Pool", internalOnlyAsset.InstallLocation);
            Assert.Equal("A3컬러복합기", internalOnlyAsset.ItemCategoryName);
            Assert.Equal("삼성전자", internalOnlyAsset.Manufacturer);
            Assert.Equal("SL-X4220RX", internalOnlyAsset.ItemName);
            Assert.Equal("SER-EDIT-001", internalOnlyAsset.MachineNumber);
            Assert.Equal("삼성전자(직판)", internalOnlyAsset.PurchaseVendor);
            Assert.Equal(12, internalOnlyAsset.ContractMonths);
            Assert.Equal(new DateOnly(2026, 1, 15), internalOnlyAsset.ContractDate);
            Assert.Equal(new DateOnly(2026, 12, 31), internalOnlyAsset.RentalEndDate);
            Assert.Equal(new DateOnly(2025, 12, 20), internalOnlyAsset.PurchaseDate);
            Assert.Equal(new DateOnly(2026, 1, 20), internalOnlyAsset.InstallDate);
            Assert.Equal("토너", internalOnlyAsset.FreeSupplyItems);
            Assert.Equal("용지", internalOnlyAsset.PaidSupplyItems);
            Assert.Equal(123_456m, internalOnlyAsset.MonthlyFee);

            var persistedProfile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var persistedItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(
                persistedProfile.BillingTemplateJson) ?? [];
            var persistedAssetIds = persistedItems
                .SelectMany(item => item.IncludedAssetIds)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            Assert.Equal([selectedAssetId], persistedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_UsesTemplateIncludedAssetsAsProfileAssets()
    {
        PrepareAppRoot("georaeplan-rental-billing-template-assets-row");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2400000-1111-4444-8888-000000000001");
            var assetId = Guid.Parse("f2400000-1111-4444-8888-0000000000a1");
            db.RentalAssets.Add(CreateRentalAsset(
                "Template Included Customer",
                "TEMPLATE-001",
                billingProfileId: null,
                assetId,
                monthlyFee: 100_000m));
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Template Included Customer",
                ItemName = "Template Rental",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Template Rental",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        Amount = 100_000m,
                        IncludedAssetIds = [assetId]
                    }
                }),
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Template Included Customer",
                    ReferenceDate = new DateOnly(2026, 6, 25),
                    ExpandCustomerSummaryRows = true
                },
                CreateAdminSession());

            var profileRow = Assert.Single(rows, row => row.HasPersistedProfile);
            Assert.Equal(profileId, profileRow.Source.Id);
            Assert.Equal(1, profileRow.AssetCount);
            Assert.Equal(1, profileRow.IncludedAssetCount);
            Assert.DoesNotContain("연결장비 없음", profileRow.DataIssueSummary);
            Assert.DoesNotContain(rows, row => !row.HasPersistedProfile && row.SelectionId == assetId);

            var unlinkedRows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Template Included Customer",
                    Status = "미연결",
                    ReferenceDate = new DateOnly(2026, 6, 25),
                    ExpandCustomerSummaryRows = true
                },
                CreateAdminSession());
            Assert.DoesNotContain(unlinkedRows, row => row.SelectionId == assetId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_DoesNotCreateUnlinkedBillingRowsForZeroFeeAssets()
    {
        PrepareAppRoot("georaeplan-rental-billing-zero-fee-unlinked");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var zeroFeeAssetId = Guid.Parse("f2500000-1111-4444-8888-0000000000a1");
            var billableAssetId = Guid.Parse("f2500000-1111-4444-8888-0000000000b2");
            db.RentalAssets.AddRange(
                CreateRentalAsset("Zero Fee Customer", "ZERO-001", null, zeroFeeAssetId, monthlyFee: 0m),
                CreateRentalAsset("Billable Customer", "BILL-001", null, billableAssetId, monthlyFee: 80_000m));
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    Status = "미연결",
                    ReferenceDate = new DateOnly(2026, 6, 25),
                    ExpandCustomerSummaryRows = true
                },
                CreateAdminSession());

            Assert.DoesNotContain(rows, row => row.SelectionId == zeroFeeAssetId);
            Assert.Contains(rows, row => row.SelectionId == billableAssetId && !row.HasPersistedProfile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_RelinksAssetsFromDeletedProfileReference()
    {
        PrepareAppRoot("georaeplan-rental-billing-relink-deleted-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var deletedProfileId = Guid.Parse("f2550000-1111-4444-8888-0000000000d1");
            var activeProfileId = Guid.Parse("f2550000-1111-4444-8888-000000000001");
            var assetId = Guid.Parse("f2550000-1111-4444-8888-0000000000a1");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = deletedProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Deleted Link Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                IsDeleted = true,
                IsActive = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.RentalAssets.Add(CreateRentalAsset(
                "Deleted Link Customer",
                "DELETED-LINK-001",
                deletedProfileId,
                assetId,
                monthlyFee: 70_000m));
            await db.SaveChangesAsync();

            var profile = new LocalRentalBillingProfile
            {
                Id = activeProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Deleted Link Customer",
                ItemName = "Relink Rental",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Relink Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 70_000m,
                        Amount = 70_000m,
                        IncludedAssetIds = [assetId]
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(activeProfileId, storedAsset.BillingProfileId);
            Assert.Equal("Deleted Link Customer", storedAsset.CustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ApplyAssetMonthlyFeesToBillingTemplate_DoesNotAppendLinkedAssetsWhenTemplateHasExplicitAssets()
    {
        var method = typeof(RentalStateService).GetMethod(
            "ApplyAssetMonthlyFeesToBillingTemplate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var profileId = Guid.Parse("f2600000-1111-4444-8888-000000000001");
        var explicitAssetId = Guid.Parse("f2600000-1111-4444-8888-0000000000a1");
        var staleLinkedAssetId = Guid.Parse("f2600000-1111-4444-8888-0000000000b2");
        var profile = new LocalRentalBillingProfile
        {
            Id = profileId,
            BillingType = "개별",
            CustomerName = "Explicit Template Customer"
        };
        var templateItems = new List<RentalBillingTemplateItemModel>
        {
            new()
            {
                DisplayItemName = "Explicit Rental",
                BillingLineMode = "개별",
                Quantity = 1m,
                UnitPrice = 100_000m,
                Amount = 100_000m,
                IncludedAssetIds = [explicitAssetId]
            }
        };
        var assets = new List<LocalRentalAsset>
        {
            CreateRentalAsset("Explicit Template Customer", "EXPLICIT-001", profileId, explicitAssetId, monthlyFee: 100_000m),
            CreateRentalAsset("Explicit Template Customer", "STALE-001", profileId, staleLinkedAssetId, monthlyFee: 120_000m)
        };

        method!.Invoke(null, new object?[] { profile, templateItems, assets });

        Assert.Contains(explicitAssetId, templateItems[0].IncludedAssetIds);
        Assert.DoesNotContain(staleLinkedAssetId, templateItems[0].IncludedAssetIds);
    }

    [Fact]
    public async Task LocalDbInitializer_RepairRentalCustomerLinkage_DoesNotAppendProfileOnlyAssetsToExplicitTemplate()
    {
        PrepareAppRoot("georaeplan-rental-initializer-explicit-template");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("f2650000-1111-4444-8888-000000000001");
            var profileId = Guid.Parse("f2650000-1111-4444-8888-000000000002");
            var selectedAssetId = Guid.Parse("f2650000-1111-4444-8888-0000000000a1");
            var profileOnlyAssetId = Guid.Parse("f2650000-1111-4444-8888-0000000000b2");

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Initializer Explicit Customer",
                NameMatchKey = "INITIALIZEREXPLICITCUSTOMER",
                TradeType = CustomerTradeTypes.Sales,
                IsDirty = false
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Initializer Explicit Customer",
                InstallSiteName = "HQ",
                ItemName = "Explicit Rental",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 1m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        ItemId = Guid.Parse("f2650000-1111-4444-8888-0000000000c3"),
                        DisplayItemName = "Explicit Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 1m,
                        Amount = 1m,
                        IncludedAssetIds = [selectedAssetId]
                    }
                }),
                IsActive = true,
                IsDirty = false
            });
            db.RentalAssets.AddRange(
                CreateRentalAsset("Initializer Explicit Customer", "INIT-SEL-001", profileId, selectedAssetId, monthlyFee: 80_000m),
                CreateRentalAsset("Initializer Explicit Customer", "INIT-PROFILE-ONLY-001", profileId, profileOnlyAssetId, monthlyFee: 120_000m));
            await db.SaveChangesAsync();

            var method = typeof(LocalDbInitializer).GetMethod(
                "RepairRentalCustomerLinkageAsync",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var task = method!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(task);
            await task!;
            await db.SaveChangesAsync();

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var storedTemplateItem = Assert.Single(JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(storedProfile.BillingTemplateJson) ?? []);

            Assert.Equal(80_000m, storedProfile.MonthlyAmount);
            Assert.Equal(1m, storedTemplateItem.Quantity);
            Assert.Equal(80_000m, storedTemplateItem.UnitPrice);
            Assert.Equal(80_000m, storedTemplateItem.Amount);
            Assert.Equal([selectedAssetId], storedTemplateItem.IncludedAssetIds);
            Assert.DoesNotContain(profileOnlyAssetId, storedTemplateItem.IncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void ApplyAssetMonthlyFeesToBillingTemplate_IndividualMixedFeesKeepsQuantityAndAverageUnitPrice()
    {
        var method = typeof(RentalStateService).GetMethod(
            "ApplyAssetMonthlyFeesToBillingTemplate",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var profileId = Guid.Parse("f2700000-1111-4444-8888-000000000001");
        var assetAId = Guid.Parse("f2700000-1111-4444-8888-0000000000a1");
        var assetBId = Guid.Parse("f2700000-1111-4444-8888-0000000000b2");
        var profile = new LocalRentalBillingProfile
        {
            Id = profileId,
            BillingType = "개별",
            CustomerName = "Mixed Fee Template Customer"
        };
        var templateItems = new List<RentalBillingTemplateItemModel>
        {
            new()
            {
                DisplayItemName = "IMC2010",
                BillingLineMode = "개별",
                Quantity = 1m,
                UnitPrice = 50_000m,
                Amount = 50_000m,
                IncludedAssetIds = [assetAId, assetBId]
            }
        };
        var assets = new List<LocalRentalAsset>
        {
            CreateRentalAsset("Mixed Fee Template Customer", "MIXED-001", profileId, assetAId, monthlyFee: 50_000m),
            CreateRentalAsset("Mixed Fee Template Customer", "MIXED-002", profileId, assetBId, monthlyFee: 70_000m)
        };

        var changed = Assert.IsType<bool>(method!.Invoke(null, new object?[] { profile, templateItems, assets }));

        Assert.True(changed);
        var item = Assert.Single(templateItems);
        Assert.Equal(2m, item.Quantity);
        Assert.Equal(60_000m, item.UnitPrice);
        Assert.Equal(120_000m, item.Amount);
        Assert.Equal(new[] { assetAId, assetBId }, item.IncludedAssetIds);
    }

    [Fact]
    public async Task SaveBillingProfile_IndividualMixedFeeAggregateDoesNotOverwriteAssetMonthlyFees()
    {
        PrepareAppRoot("georaeplan-rental-individual-mixed-fee-save");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2800000-1111-4444-8888-000000000001");
            var assetAId = Guid.Parse("f2800000-1111-4444-8888-0000000000a1");
            var assetBId = Guid.Parse("f2800000-1111-4444-8888-0000000000b2");
            db.RentalAssets.AddRange(
                CreateRentalAsset("Mixed Fee Save Customer", "MIXED-SAVE-001", null, assetAId, monthlyFee: 50_000m),
                CreateRentalAsset("Mixed Fee Save Customer", "MIXED-SAVE-002", null, assetBId, monthlyFee: 70_000m));
            await db.SaveChangesAsync();

            var templateItems = new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "Rental Copier",
                    BillingLineMode = "개별",
                    Quantity = 2m,
                    UnitPrice = 60_000m,
                    Amount = 120_000m,
                    IncludedAssetIds = [assetAId, assetBId]
                }
            };
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Mixed Fee Save Customer",
                InstallSiteName = "Mixed Fee Save Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 120_000m,
                BillingTemplateJson = JsonSerializer.Serialize(templateItems)
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var storedAssets = await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == assetAId || asset.Id == assetBId)
                .OrderBy(asset => asset.ManagementNumber)
                .ToListAsync();
            Assert.Equal(2, storedAssets.Count);
            Assert.All(storedAssets, asset => Assert.Equal(profileId, asset.BillingProfileId));
            Assert.Equal(50_000m, storedAssets.Single(asset => asset.Id == assetAId).MonthlyFee);
            Assert.Equal(70_000m, storedAssets.Single(asset => asset.Id == assetBId).MonthlyFee);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var storedTemplateItem = Assert.Single(new RentalStateService(db).GetBillingTemplateItems(storedProfile));
            Assert.Equal(2m, storedTemplateItem.Quantity);
            Assert.Equal(60_000m, storedTemplateItem.UnitPrice);
            Assert.Equal(120_000m, storedTemplateItem.Amount);
            Assert.Equal(new[] { assetAId, assetBId }, storedTemplateItem.IncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfile_IndividualSameFeeAggregateUpdatesAssetMonthlyFeesFromEditedUnitPrice()
    {
        PrepareAppRoot("georaeplan-rental-individual-same-fee-edit-save");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2810000-1111-4444-8888-000000000001");
            var assetAId = Guid.Parse("f2810000-1111-4444-8888-0000000000a1");
            var assetBId = Guid.Parse("f2810000-1111-4444-8888-0000000000b2");
            db.RentalAssets.AddRange(
                CreateRentalAsset("Same Fee Save Customer", "SAME-SAVE-001", null, assetAId, monthlyFee: 50_000m, itemName: "IMC2010"),
                CreateRentalAsset("Same Fee Save Customer", "SAME-SAVE-002", null, assetBId, monthlyFee: 50_000m, itemName: "IMC2010"));
            await db.SaveChangesAsync();

            var templateItems = new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "IMC2010",
                    BillingLineMode = "\uAC1C\uBCC4",
                    Quantity = 2m,
                    UnitPrice = 60_000m,
                    Amount = 120_000m,
                    IncludedAssetIds = [assetAId, assetBId]
                }
            };
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Same Fee Save Customer",
                InstallSiteName = "Same Fee Save Customer",
                BillingType = "\uAC1C\uBCC4",
                BillingAdvanceMode = "\uC120\uBD88",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 120_000m,
                BillingTemplateJson = JsonSerializer.Serialize(templateItems)
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(profile, CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var storedAssets = await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == assetAId || asset.Id == assetBId)
                .OrderBy(asset => asset.ManagementNumber)
                .ToListAsync();
            Assert.Equal(2, storedAssets.Count);
            Assert.All(storedAssets, asset =>
            {
                Assert.Equal(profileId, asset.BillingProfileId);
                Assert.Equal(60_000m, asset.MonthlyFee);
            });

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var storedTemplateItem = Assert.Single(new RentalStateService(db).GetBillingTemplateItems(storedProfile));
            Assert.Equal(2m, storedTemplateItem.Quantity);
            Assert.Equal(60_000m, storedTemplateItem.UnitPrice);
            Assert.Equal(120_000m, storedTemplateItem.Amount);
            Assert.Equal(new[] { assetAId, assetBId }, storedTemplateItem.IncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetIncludedBillingAssetsAsync_BatchesManyExplicitIncludedAssetIds()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-explicit-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2200000-1111-4444-8888-000000000001");
            for (var index = 0; index < 350; index++)
            {
                db.RentalAssets.Add(CreateRentalAsset(
                    $"A Profile Customer {index:D4}",
                    $"P-{index:D4}",
                    profileId));
            }

            var explicitAssetIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var explicitAsset = CreateRentalAsset(
                    $"Z Explicit Customer {index:D4}",
                    $"E-{index:D4}",
                    billingProfileId: null);
                explicitAssetIds.Add(explicitAsset.Id);
                db.RentalAssets.Add(explicitAsset);
            }

            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetIncludedBillingAssetsAsync(
                profileId,
                explicitAssetIds,
                customerId: null,
                officeCode: OfficeCodeCatalog.Usenet,
                CreateAdminSession());

            Assert.Equal(950, rows.Count);
            Assert.Contains(rows, asset => asset.Id == explicitAssetIds.First());
            Assert.Contains(rows, asset => asset.Id == explicitAssetIds.Last());
            Assert.Equal(650, rows.Count(asset => explicitAssetIds.Contains(asset.Id)));
            Assert.Equal(300, rows.Count(asset => asset.BillingProfileId == profileId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_LegacyProfileWithoutTemplateAssetIdsKeepsExistingLinkedAssetsWhenNoExplicitCoverage()
    {
        PrepareAppRoot("georaeplan-rental-legacy-linked-assets-preserve");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2900000-1111-4444-8888-000000000001");
            var linkedAssetId = Guid.Parse("f2900000-1111-4444-8888-0000000000a1");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Legacy Linked Customer",
                InstallSiteName = "Legacy Linked Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 80_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Legacy Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = []
                    }
                }),
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.RentalAssets.Add(CreateRentalAsset(
                "Legacy Linked Customer",
                "LEGACY-LINK-001",
                profileId,
                linkedAssetId,
                monthlyFee: 80_000m));
            await db.SaveChangesAsync();

            var profileSavePayload = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Legacy Linked Customer",
                InstallSiteName = "Legacy Linked Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 80_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Legacy Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = []
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profileSavePayload,
                CreateAdminSession(),
                Array.Empty<RentalBillingAssetLinkEdit>());

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(asset => asset.Id == linkedAssetId);
            Assert.Equal(profileId, storedAsset.BillingProfileId);
            Assert.Equal("청구대상", storedAsset.BillingEligibilityStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_RemovingPreviouslyExplicitTemplateAssetIdsUnlinksAssets()
    {
        PrepareAppRoot("georaeplan-rental-explicit-linked-assets-remove");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2910000-1111-4444-8888-000000000001");
            var linkedAssetId = Guid.Parse("f2910000-1111-4444-8888-0000000000a1");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Explicit Remove Customer",
                InstallSiteName = "Explicit Remove Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 80_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Explicit Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = [linkedAssetId]
                    }
                }),
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.RentalAssets.Add(CreateRentalAsset(
                "Explicit Remove Customer",
                "EXPLICIT-REMOVE-001",
                profileId,
                linkedAssetId,
                monthlyFee: 80_000m));
            await db.SaveChangesAsync();

            var profileSavePayload = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Explicit Remove Customer",
                InstallSiteName = "Explicit Remove Customer",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 80_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Explicit Rental",
                        BillingLineMode = "개별",
                        Quantity = 1m,
                        UnitPrice = 80_000m,
                        Amount = 80_000m,
                        IncludedAssetIds = []
                    }
                })
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profileSavePayload,
                CreateAdminSession(),
                Array.Empty<RentalBillingAssetLinkEdit>());

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(asset => asset.Id == linkedAssetId);
            Assert.Null(storedAsset.BillingProfileId);
            Assert.Equal("미확인", storedAsset.BillingEligibilityStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_DuplicateProfileKeyMergesIncomingExplicitAssetsWithoutDroppingExistingAssets()
    {
        PrepareAppRoot("georaeplan-rental-duplicate-profile-merge-assets");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var existingProfileId = Guid.Parse("f2920000-1111-4444-8888-000000000001");
            var incomingProfileId = Guid.Parse("f2920000-1111-4444-8888-000000000002");
            var existingAssetId = Guid.Parse("f2920000-1111-4444-8888-0000000000a1");
            var incomingAssetId = Guid.Parse("f2920000-1111-4444-8888-0000000000b2");
            db.RentalAssets.AddRange(
                CreateRentalAsset(
                    "Duplicate Merge Customer",
                    "DUP-MERGE-001",
                    null,
                    existingAssetId,
                    monthlyFee: 80_000m),
                CreateRentalAsset(
                    "Duplicate Merge Customer",
                    "DUP-MERGE-002",
                    null,
                    incomingAssetId,
                    monthlyFee: 90_000m));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var existingSave = await service.SaveBillingProfileAsync(
                new LocalRentalBillingProfile
                {
                    Id = existingProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "Duplicate Merge Customer",
                    InstallSiteName = "Duplicate Merge Customer",
                    BillingType = "개별",
                    BillingAdvanceMode = "후불",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 80_000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Duplicate Rental",
                            BillingLineMode = "개별",
                            Quantity = 1m,
                            UnitPrice = 80_000m,
                            Amount = 80_000m,
                            IncludedAssetIds = [existingAssetId]
                        }
                    })
                },
                CreateAdminSession());
            Assert.True(existingSave.Success, existingSave.Message);
            var existingStoredProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(profile => profile.Id == existingProfileId);
            var existingRunsJson = JsonSerializer.Serialize(new List<RentalBillingRunModel>
            {
                new()
                {
                    RunId = Guid.Parse("f2920000-1111-4444-8888-0000000000f1"),
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    CycleMonths = 1,
                    BilledAmount = 80_000m,
                    SettledAmount = 10_000m,
                    SettlementStatus = PaymentFlowConstants.SettlementStatusPartial
                }
            });
            existingStoredProfile.BillingRunsJson = existingRunsJson;
            existingStoredProfile.LastBilledDate = new DateOnly(2026, 6, 25);
            existingStoredProfile.LastSettledDate = new DateOnly(2026, 6, 26);
            existingStoredProfile.SettledAmount = 10_000m;
            existingStoredProfile.OutstandingAmount = 70_000m;
            existingStoredProfile.SettlementStatus = PaymentFlowConstants.SettlementStatusPartial;
            existingStoredProfile.CompletionStatus = PaymentFlowConstants.CompletionPending;
            await db.SaveChangesAsync();

            var incomingSave = await service.SaveBillingProfileAsync(
                new LocalRentalBillingProfile
                {
                    Id = incomingProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerName = "Duplicate Merge Customer",
                    InstallSiteName = "Duplicate Merge Customer",
                    BillingType = "개별",
                    BillingAdvanceMode = "후불",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 90_000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Duplicate Rental",
                            BillingLineMode = "개별",
                            Quantity = 1m,
                            UnitPrice = 90_000m,
                            Amount = 90_000m,
                            IncludedAssetIds = [incomingAssetId]
                        }
                    })
                },
                CreateAdminSession());

            Assert.True(incomingSave.Success, incomingSave.Message);
            Assert.Equal(existingProfileId, incomingSave.EntityId);

            var storedAssets = await db.RentalAssets.IgnoreQueryFilters()
                .Where(asset => asset.Id == existingAssetId || asset.Id == incomingAssetId)
                .OrderBy(asset => asset.ManagementNumber)
                .ToListAsync();
            Assert.Equal(2, storedAssets.Count);
            Assert.All(storedAssets, asset => Assert.Equal(existingProfileId, asset.BillingProfileId));

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(profile => profile.Id == existingProfileId);
            Assert.Equal(existingRunsJson, storedProfile.BillingRunsJson);
            Assert.Equal(new DateOnly(2026, 6, 25), storedProfile.LastBilledDate);
            Assert.Equal(new DateOnly(2026, 6, 26), storedProfile.LastSettledDate);
            Assert.Equal(10_000m, storedProfile.SettledAmount);
            Assert.Equal(70_000m, storedProfile.OutstandingAmount);
            Assert.Equal(PaymentFlowConstants.SettlementStatusPartial, storedProfile.SettlementStatus);
            var storedIncludedAssetIds = new RentalStateService(db)
                .GetBillingTemplateItems(storedProfile)
                .SelectMany(item => item.IncludedAssetIds)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            Assert.Equal(
                new[] { existingAssetId, incomingAssetId }.OrderBy(id => id),
                storedIncludedAssetIds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalRentalAsset CreateRentalAsset(
        string customerName,
        string managementNumber,
        Guid? billingProfileId,
        Guid? assetId = null,
        decimal monthlyFee = 100_000m,
        string itemName = "Rental Copier")
    {
        var resolvedAssetId = assetId ?? Guid.NewGuid();
        return new LocalRentalAsset
        {
            Id = resolvedAssetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"MID-{resolvedAssetId:N}",
            ManagementNumber = managementNumber,
            AssetKey = $"ASSET-{resolvedAssetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            BillingProfileId = billingProfileId,
            ItemCategoryName = "Copier",
            ItemName = itemName,
            MachineNumber = $"SN-{assetId:N}",
            InstallSiteName = "HQ",
            InstallLocation = "HQ",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = monthlyFee,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
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
}
