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
        decimal monthlyFee = 100_000m)
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
            ItemName = "Rental Copier",
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
