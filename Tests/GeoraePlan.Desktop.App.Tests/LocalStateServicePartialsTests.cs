using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalStateServicePartialsTests
{
    [Fact]
    public void PendingSyncSummary_BuildWaitingMessage_UsesPrimaryBucketAndTotal()
    {
        var summary = new PendingSyncSummary(
            5,
            [
                new PendingSyncBucket("OFFICE:ITWORLD", "ITWORLD", "거래처 변경", 3),
                new PendingSyncBucket("OFFICE:USENET", "USENET", "품목 변경", 2)
            ]);

        var message = summary.BuildWaitingMessage("안내:");

        Assert.Equal("안내: ITWORLD 거래처 변경 3건 포함 총 5건이 서버 반영 대기 중입니다.", message);
        Assert.Equal("ITWORLD", summary.PrimaryBucket?.ScopeDisplayName);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1.1.171", true)]
    [InlineData("1.1.172", false)]
    [InlineData("1.1.173", false)]
    [InlineData("invalid", true)]
    public void VersionChangeMaintenance_FullMirrorRefresh_RunsOnlyBeforeBaselineVersion(
        string? lastProcessedVersion,
        bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(VersionChangeMaintenanceService),
            "RequiresFullMirrorRefreshAfterVersionChange",
            new object?[] { lastProcessedVersion });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SalesViewModel_CalculateTaxInclusiveTotals_DoesNotAddVatOnTop()
    {
        var result = InvokePrivateStatic<(decimal SupplyAmount, decimal VatAmount, decimal TotalAmount)>(
            typeof(SalesViewModel),
            "CalculateTaxInclusiveTotals",
            new[] { 2_500m, 192_000m });

        Assert.Equal(194_500m, result.TotalAmount);
        Assert.Equal(176_818m, result.SupplyAmount);
        Assert.Equal(17_682m, result.VatAmount);
    }

    [Fact]
    public void LocalStateService_CustomerFinancialSummaryInvoiceFilter_UsesOnlyActiveConfirmedLatestInvoices()
    {
        Assert.True(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: true, isDeleted: false));
        Assert.True(IsFinancialSummaryInvoice(VoucherType.Purchase, isLatestVersion: true, isConfirmed: true, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: false, isConfirmed: true, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: false, isDeleted: false));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Sales, isLatestVersion: true, isConfirmed: true, isDeleted: true));
        Assert.False(IsFinancialSummaryInvoice(VoucherType.Procurement, isLatestVersion: true, isConfirmed: true, isDeleted: false));
    }

    [Fact]
    public void PaymentViewModel_ResolveInvoiceDefaultTransactionKind_UsesInvoiceSettlementKinds()
    {
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoiceReceipt,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Sales));
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoicePayment,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Purchase));
        Assert.Equal(
            PaymentFlowConstants.TransactionKindInvoicePayment,
            ResolveInvoiceDefaultTransactionKind(VoucherType.Procurement));
    }

    [Fact]
    public void RentalAssetViewModel_BuildEditableAssetOfficeCodes_PreservesReadableSelectedOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<string>>(
            typeof(RentalAssetViewModel),
            "BuildEditableAssetOfficeCodes",
            new object?[]
            {
                new[] { OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu },
                OfficeCodeCatalog.All.ToArray(),
                new string?[] { OfficeCodeCatalog.Itworld }
            });

        Assert.Contains(OfficeCodeCatalog.Itworld, result);
    }

    [Fact]
    public void RentalAssetViewModel_BuildOfficeDisplayOptions_AddsCatalogFallbackOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<DisplayOption>>(
            typeof(RentalAssetViewModel),
            "BuildOfficeDisplayOptions",
            new object?[]
            {
                Array.Empty<LocalOffice>(),
                new[] { OfficeCodeCatalog.Itworld }
            });

        var option = Assert.Single(result);
        Assert.Equal(OfficeCodeCatalog.Itworld, option.Value);
        Assert.Equal(OfficeCodeCatalog.Itworld, option.DisplayName);
    }

    [Fact]
    public void RentalBillingViewModel_BuildEditableBillingOfficeCodes_PreservesReadableSelectedOffice()
    {
        var result = InvokePrivateStatic<IReadOnlyList<string>>(
            typeof(RentalBillingViewModel),
            "BuildEditableBillingOfficeCodes",
            new object?[]
            {
                new[] { OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu },
                OfficeCodeCatalog.All.ToArray(),
                new string?[] { OfficeCodeCatalog.Itworld }
            });

        Assert.Contains(OfficeCodeCatalog.Itworld, result);
    }

    [Fact]
    public void RentalBillingViewModel_ResolveProfileOfficeCode_CanonicalizesMixedItworldScope()
    {
        var profile = new LocalRentalBillingProfile
        {
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
        };

        var officeCode = InvokePrivateStatic<string>(
            typeof(RentalBillingViewModel),
            "ResolveProfileOfficeCode",
            profile,
            OfficeCodeCatalog.Usenet);

        Assert.Equal(OfficeCodeCatalog.Itworld, officeCode);
    }

    [Fact]
    public async Task RentalStateService_SaveAsset_AdminCanSaveItworldAssetScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-asset-itworld-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            });

            var service = new RentalStateService(db);
            var assetId = Guid.NewGuid();
            var result = await service.SaveAssetAsync(new LocalRentalAsset
            {
                Id = assetId,
                ManagementId = "IT-001",
                ManagementNumber = "2604-001",
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                CurrentLocation = "ITWORLD warehouse",
                ItemName = "Rental asset",
                CustomerName = "ITWORLD customer",
                CurrentCustomerName = "ITWORLD customer",
                InstallSiteName = "ITWORLD customer",
                InstallLocation = "ITWORLD customer",
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            }, session);

            Assert.True(result.Success, result.Message);

            var persisted = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persisted.OfficeCode);
            Assert.Equal(TenantScopeCatalog.Itworld, persisted.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_GetAssetLinkCandidates_ExpandedScopeIncludesSameTenantOwnerAssets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-candidates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetOwnedAssetId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var yeonsuAssetId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var itworldAssetId = Guid.Parse("82333333-3333-3333-3333-333333333333");
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = usenetOwnedAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "TEST:USENET-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementNumber = "U-001",
                    MachineNumber = "USENET-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                },
                new LocalRentalAsset
                {
                    Id = yeonsuAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    AssetKey = "TEST:YEONSU-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementNumber = "Y-001",
                    MachineNumber = "YEONSU-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                },
                new LocalRentalAsset
                {
                    Id = itworldAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    AssetKey = "TEST:ITWORLD-SN",
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementNumber = "IT-001",
                    MachineNumber = "ITWORLD-SN",
                    ItemName = "SL-M3820ND",
                    CustomerName = "연수구 보건소[보건행정과]",
                    CurrentCustomerName = "연수구 보건소[보건행정과]",
                    AssetStatus = "임대진행중",
                    BillingEligibilityStatus = "미확인"
                });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var service = new RentalStateService(db);

            var currentOfficeOnly = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Yeonsu,
                session,
                includeOtherOfficeAssets: false);
            var expanded = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Yeonsu,
                session,
                includeOtherOfficeAssets: true);
            var itworldExpanded = await service.GetAssetLinkCandidatesAsync(
                null,
                null,
                "연수구 보건소[보건행정과]",
                OfficeCodeCatalog.Itworld,
                session,
                includeOtherOfficeAssets: true);

            Assert.DoesNotContain(currentOfficeOnly, candidate => candidate.Source.Id == usenetOwnedAssetId);
            Assert.Contains(currentOfficeOnly, candidate => candidate.Source.Id == yeonsuAssetId);
            Assert.DoesNotContain(expanded, candidate => candidate.Source.Id == itworldAssetId);
            Assert.Contains(itworldExpanded, candidate => candidate.Source.Id == itworldAssetId);

            var expandedUsenetAsset = Assert.Single(expanded, candidate => candidate.Source.Id == usenetOwnedAssetId);
            Assert.True(expandedUsenetAsset.IsOutsideCurrentOffice);
            Assert.Equal("USENET", expandedUsenetAsset.ManagementCompanyName);
            Assert.Equal("USENET", expandedUsenetAsset.AssetScopeDisplay);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveBillingProfile_CanTransferUsenetOwnedAssetToYeonsuBillingWithoutChangingOwner()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var assetId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var customerId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:USENET-TRANSFER-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "U-TRANSFER-001",
                MachineNumber = "USENET-TRANSFER-SN",
                ItemName = "SL-M3820ND",
                CustomerName = "기존 거래처",
                CurrentCustomerName = "기존 거래처",
                InstallSiteName = "기존 거래처",
                InstallLocation = "기존 위치",
                MonthlyFee = 30_000m,
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            });
            await db.SaveChangesAsync();

            var templateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = Guid.Parse("86666666-6666-6666-6666-666666666666"),
                    DisplayItemName = "SL-M3820ND",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 30_000m,
                    Amount = 30_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "연수구 보건소[보건행정과]",
                InstallSiteName = "연수구 보건소[보건행정과]",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = templateJson,
                MonthlyAmount = 30_000m
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerId = customerId,
                        CustomerName = "연수구 보건소[보건행정과]",
                        InstallLocation = "보건행정과",
                        InstallSiteName = "연수구 보건소[보건행정과]",
                        MonthlyFee = 30_000m,
                        Notes = "link test"
                    }
                ]);

            Assert.True(result.Success, result.Message);

            var persistedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(current => current.Id == profileId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, persistedProfile.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedProfile.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, persistedProfile.TenantCode);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Equal(profileId, persistedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, persistedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, persistedAsset.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, persistedAsset.TenantCode);

            var history = Assert.Single(await db.RentalAssetAssignmentHistories.Where(current => current.AssetId == assetId).ToListAsync());
            Assert.True(history.IsCurrent);
            Assert.Equal(profileId, history.BillingProfileId);
            Assert.Equal(customerId, history.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, history.ResponsibleOfficeCode);
            Assert.Equal("청구대상", persistedAsset.BillingEligibilityStatus);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RentalStateService_SaveBillingProfile_RemovingIncludedAssetClosesAssignmentHistoryWithoutDeletingAsset()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-history-unlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("8a111111-1111-1111-1111-111111111111");
            var assetId = Guid.Parse("8a222222-2222-2222-2222-222222222222");
            var customerId = Guid.Parse("8a333333-3333-3333-3333-333333333333");
            var itemId = Guid.Parse("8a444444-4444-4444-4444-444444444444");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "TEST:HISTORY-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "H-001",
                MachineNumber = "HISTORY-SN",
                ItemName = "Printer",
                CustomerName = "Old Customer",
                CurrentCustomerName = "Old Customer",
                InstallLocation = "Old Location",
                AssetStatus = "\uC784\uB300\uC9C4\uD589\uC911",
                BillingEligibilityStatus = "\uBBF8\uD655\uC778"
            });
            await db.SaveChangesAsync();

            var linkedTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = itemId,
                    DisplayItemName = "Printer",
                    BillingLineMode = "\uBB36\uC74C",
                    Quantity = 1m,
                    UnitPrice = 40_000m,
                    Amount = 40_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallSiteName = "Office 1",
                BillingType = "\uBB36\uC74C",
                BillingAdvanceMode = "\uD6C4\uBD88",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = linkedTemplateJson,
                MonthlyAmount = 40_000m
            };

            var service = new RentalStateService(db);
            var linkResult = await service.SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerId = customerId,
                        CustomerName = "Customer A",
                        InstallLocation = "Office 1",
                        MonthlyFee = 40_000m
                    }
                ]);
            Assert.True(linkResult.Success, linkResult.Message);

            db.ChangeTracker.Clear();
            var unlinkedTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = itemId,
                    DisplayItemName = "Printer",
                    BillingLineMode = "\uBB36\uC74C",
                    Quantity = 1m,
                    UnitPrice = 40_000m,
                    Amount = 40_000m
                }
            });
            var unlinkProfile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerId = customerId,
                CustomerName = "Customer A",
                InstallSiteName = "Office 1",
                BillingType = "\uBB36\uC74C",
                BillingAdvanceMode = "\uD6C4\uBD88",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = unlinkedTemplateJson,
                MonthlyAmount = 40_000m
            };

            var unlinkResult = await new RentalStateService(db).SaveBillingProfileAsync(
                unlinkProfile,
                CreateAdminSession(),
                Array.Empty<RentalBillingAssetLinkEdit>());
            Assert.True(unlinkResult.Success, unlinkResult.Message);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(persistedAsset.BillingProfileId);
            Assert.Null(persistedAsset.CustomerId);
            Assert.Equal("Customer A", persistedAsset.CurrentCustomerName);
            Assert.Equal("Customer A", persistedAsset.LastCustomerName);
            Assert.NotNull(persistedAsset.LastAssignmentClearedAtUtc);

            var history = Assert.Single(await db.RentalAssetAssignmentHistories.Where(current => current.AssetId == assetId).ToListAsync());
            Assert.False(history.IsCurrent);
            Assert.Equal(profileId, history.BillingProfileId);
            Assert.NotNull(history.UnlinkedAtUtc);

            var rows = await new RentalStateService(db).GetAssetAssignmentHistoriesAsync(assetId);
            var row = Assert.Single(rows);
            Assert.False(row.IsCurrent);
            Assert.Equal("Customer A", row.CustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalStateService_SaveBillingProfile_DeniesCrossTenantAssetTransfer()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-link-cross-tenant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            var assetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                AssetKey = "TEST:ITWORLD-TRANSFER-SN",
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementNumber = "IT-TRANSFER-001",
                MachineNumber = "ITWORLD-TRANSFER-SN",
                ItemName = "SL-M3820ND",
                CustomerName = "아이티월드 거래처",
                CurrentCustomerName = "아이티월드 거래처",
                AssetStatus = "임대진행중",
                BillingEligibilityStatus = "미확인"
            });
            await db.SaveChangesAsync();

            var templateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = Guid.Parse("89999999-9999-9999-9999-999999999999"),
                    DisplayItemName = "SL-M3820ND",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 30_000m,
                    Amount = 30_000m,
                    IncludedAssetIds = [assetId]
                }
            });
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                CustomerName = "연수구 보건소[보건행정과]",
                InstallSiteName = "연수구 보건소[보건행정과]",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingTemplateJson = templateJson,
                MonthlyAmount = 30_000m
            };

            var result = await new RentalStateService(db).SaveBillingProfileAsync(
                profile,
                CreateAdminSession(),
                [
                    new RentalBillingAssetLinkEdit
                    {
                        AssetId = assetId,
                        CustomerName = "연수구 보건소[보건행정과]",
                        MonthlyFee = 30_000m
                    }
                ]);

            Assert.False(result.Success);
            Assert.Contains("다른 업체/담당지점", result.Message, StringComparison.Ordinal);

            var persistedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == assetId);
            Assert.Null(persistedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Itworld, persistedAsset.ResponsibleOfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, persistedAsset.ManagementCompanyCode);
            Assert.Equal(TenantScopeCatalog.Itworld, persistedAsset.TenantCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void LocalIntegrityReport_BuildSummaryText_AndToMarkdown_IncludeKeySignals()
    {
        var report = new LocalIntegrityReport(
            new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc),
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.UsenetGroup,
            2,
            pendingServerMirrorRefresh: true,
            [
                new LocalIntegrityIssue("sync_outbox_failed_pending", "Error", 3, "실패 상태의 sync outbox가 남아 있습니다."),
                new LocalIntegrityIssue("out_of_scope_items", "Warning", 1, "현재 계정 범위 밖 품목 캐시가 남아 있습니다.")
            ]);

        var summary = report.BuildSummaryText(maxIssues: 1);
        var markdown = report.ToMarkdown();

        Assert.Contains("버전 변경 후 중앙 서버 기준 전체 재동기화가 대기 중입니다.", summary, StringComparison.Ordinal);
        Assert.Contains("실패 상태의 sync outbox가 남아 있습니다. (3건)", summary, StringComparison.Ordinal);
        Assert.Contains("그 외 1개 항목은 무결성 리포트에서 확인하세요.", summary, StringComparison.Ordinal);
        Assert.Contains("현재 미동기화 변경 2건이 있어", summary, StringComparison.Ordinal);

        Assert.Contains("# 무결성 점검 리포트", markdown, StringComparison.Ordinal);
        Assert.Contains("sync_outbox_failed_pending", markdown, StringComparison.Ordinal);
        Assert.Contains("out_of_scope_items", markdown, StringComparison.Ordinal);
    }

    private static bool IsFinancialSummaryInvoice(
        VoucherType voucherType,
        bool isLatestVersion,
        bool isConfirmed,
        bool isDeleted)
        => InvokePrivateStatic<bool>(
            typeof(LocalStateService),
            "IsCustomerFinancialSummaryInvoice",
            new LocalInvoice
            {
                VoucherType = voucherType,
                IsLatestVersion = isLatestVersion,
                IsConfirmed = isConfirmed,
                IsDeleted = isDeleted
            });

    private static string ResolveInvoiceDefaultTransactionKind(VoucherType voucherType)
        => InvokePrivateStatic<string>(
            typeof(PaymentViewModel),
            "ResolveInvoiceDefaultTransactionKind",
            new LocalInvoice { VoucherType = voucherType });

    [Fact]
    public void ResolveScope_PrefersOfficeScopeOverTenantScope()
    {
        var result = InvokePrivateStatic<(string ScopeKey, string ScopeDisplayName)>(
            "ResolveScope",
            OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal("OFFICE:ITWORLD", result.ScopeKey);
        Assert.Equal("ITWORLD", result.ScopeDisplayName);
    }

    [Fact]
    public void ResolveOfficeCodeFromTenant_ReturnsRepresentativeOffice()
    {
        var officeCode = InvokePrivateStatic<string>(
            "ResolveOfficeCodeFromTenant",
            TenantScopeCatalog.UsenetGroup);

        Assert.Equal(OfficeCodeCatalog.Usenet, officeCode);
    }

    [Fact]
    public void NormalizeOutboxErrorMessage_TruncatesLongMessage_AndSuppliesDefault()
    {
        var longMessage = new string('x', 600);

        var truncated = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", longMessage);
        var defaultMessage = InvokePrivateStatic<string>("NormalizeOutboxErrorMessage", new object?[] { null });

        Assert.Equal(500, truncated.Length);
        Assert.Equal("동기화 중 알 수 없는 오류가 발생했습니다.", defaultMessage);
    }

    [Fact]
    public void GetOutboxStatusWeight_UsesExpectedPriorityOrder()
    {
        var failed = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Failed");
        var prepared = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Prepared");
        var sent = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Sent");
        var acknowledged = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Acknowledged");
        var unknown = InvokePrivateStatic<int>("GetOutboxStatusWeight", "Unknown");

        Assert.True(failed < prepared);
        Assert.True(prepared < sent);
        Assert.True(sent < acknowledged);
        Assert.True(acknowledged < unknown);
    }

    [Fact]
    public void SyncOutboxListItem_ComputedProperties_WorkAsExpected()
    {
        var item = new SyncOutboxListItem
        {
            EntityId = Guid.Empty,
            MutationId = new string('a', 40),
            Status = "Failed"
        };

        Assert.Equal("-", item.EntityIdText);
        Assert.Equal(39, item.ShortMutationId.Length);
        Assert.EndsWith("...", item.ShortMutationId, StringComparison.Ordinal);
        Assert.True(item.IsFailed);
        Assert.False(item.IsAcknowledged);
    }

    [Fact]
    public void RecycleBinEntry_AndDependencyModels_ComputedProperties_WorkAsExpected()
    {
        var localDeletedAt = new DateTime(2026, 4, 20, 13, 45, 0, DateTimeKind.Local);
        var entry = new RecycleBinEntry
        {
            EntityId = Guid.NewGuid(),
            Kind = RecycleBinEntityKind.InventoryTransfer,
            DeletedAtUtc = localDeletedAt.ToUniversalTime()
        };

        var dependency = new RecycleBinDependencyItem
        {
            Label = "전표",
            Count = 3
        };

        var candidate = new RecycleBinCustomerMergeCandidate
        {
            CustomerId = Guid.NewGuid(),
            Name = "거래처A",
            BusinessNumber = "",
            Phone = "010-1234-5678",
            ResponsibleOfficeCode = "ITWORLD"
        };

        Assert.Equal("재고이동", entry.KindText);
        Assert.Equal(localDeletedAt.ToString("yyyy-MM-dd HH:mm"), entry.DeletedAtLocalText);
        Assert.Equal("전표 3건", dependency.DisplayText);
        Assert.Equal("거래처A / 010-1234-5678 / ITWORLD", candidate.DisplayText);
    }

    [Fact]
    public void RecycleBinHelpers_NormalizeAndFormatAsExpected()
    {
        var joined = InvokePrivateStatic<string>(
            "JoinSegments",
            new object?[] { new string?[] { "  거래처A  ", null, " 010-1234-5678 ", " " } });
        var digits = InvokePrivateStatic<string>("NormalizeDigits", "사업자 123-45-67890 / 연락처 010-1111-2222");
        var voucher = InvokePrivateStatic<string>("GetVoucherTypeLabel", VoucherType.Sales);
        var fallbackKind = InvokePrivateStatic<string>("GetTransactionKindLabel", "  임의구분  ");
        var emptyKind = InvokePrivateStatic<string>("GetTransactionKindLabel", new object?[] { null });

        Assert.Equal("거래처A / 010-1234-5678", joined);
        Assert.Equal("123456789001011112222", digits);
        Assert.Equal("매출", voucher);
        Assert.Equal("임의구분", fallbackKind);
        Assert.Equal("거래내역", emptyKind);
    }

    [Fact]
    public void SyncEquivalentRevisionConflict_IgnoresFileContent_WhenMetadataMatches()
    {
        var id = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var now = new DateTime(2026, 4, 21, 2, 0, 0, DateTimeKind.Utc);
        var client = new CustomerContractDto
        {
            Id = id,
            CustomerId = customerId,
            ContractType = "RentalContract",
            FileName = "contract.pdf",
            MimeType = "application/pdf",
            FileSize = 3,
            FileHash = "ABC123",
            UploadedByUsername = "tester",
            UploadedAtUtc = now,
            FileContent = [1, 2, 3],
            CreatedAtUtc = now.AddMinutes(-5),
            UpdatedAtUtc = now,
            Revision = 10,
            ExpectedRevision = 10
        };
        var server = new CustomerContractDto
        {
            Id = id,
            CustomerId = customerId,
            ContractType = client.ContractType,
            FileName = client.FileName,
            MimeType = client.MimeType,
            FileSize = client.FileSize,
            FileHash = client.FileHash,
            UploadedByUsername = client.UploadedByUsername,
            UploadedAtUtc = client.UploadedAtUtc,
            FileContent = [],
            CreatedAtUtc = client.CreatedAtUtc,
            UpdatedAtUtc = now.AddSeconds(30),
            Revision = 11
        };

        var conflict = new ConflictLogDto
        {
            EntityName = "CustomerContract",
            EntityId = id.ToString("D"),
            Reason = "Expected revision mismatch. client=10, server=11",
            ClientJson = JsonSerializer.Serialize(client),
            ServerJson = JsonSerializer.Serialize(server)
        };

        var isEquivalent = InvokePrivateStatic<bool>(
            typeof(SyncService),
            "IsEquivalentRevisionConflict",
            conflict);

        Assert.True(isEquivalent);
    }

    [Fact]
    public async Task SyncService_PrepareRentalBillingProfileRevisionRetry_RebasesRevisionAndRequeuesOutbox()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-profile-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("71111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("72222222-2222-2222-2222-222222222222");
            var localAssetAId = Guid.Parse("73333333-3333-3333-3333-333333333333");
            var localAssetBId = Guid.Parse("74444444-4444-4444-4444-444444444444");
            var staleServerAssetId = Guid.Parse("75555555-5555-5555-5555-555555555555");
            var templateItemId = Guid.Parse("76666666-6666-6666-6666-666666666666");
            var localRevision = 200L;
            var serverRevision = 150L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 11, 22, 27, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:sync-test";

            var canonicalTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = templateItemId,
                    DisplayItemName = "IMC2000",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 600000m,
                    Amount = 600000m,
                    IncludedAssetIds = [localAssetAId, localAssetBId]
                }
            });
            var staleServerTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    ItemId = templateItemId,
                    DisplayItemName = "IMC2000",
                    BillingLineMode = "묶음",
                    Quantity = 1m,
                    UnitPrice = 600000m,
                    Amount = 600000m,
                    IncludedAssetIds = [staleServerAssetId, localAssetAId]
                }
            });

            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "미추홀구 주안2동행정복지센터",
                BusinessNumber = "131-83-00632",
                ItemName = "IMC2000",
                BillingType = "묶음",
                InstallSiteName = "미추홀구 주안2동행정복지센터",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingDayMode = "고정일",
                BillingCycleMonths = 36,
                BillingAnchorMonth = 3,
                DocumentIssueMode = "결제일과 동일",
                MonthlyAmount = 600000m,
                OutstandingAmount = 600000m,
                BillingStatus = "보류",
                CompletionStatus = "미완료",
                SettlementStatus = "미입금",
                RequiresFollowUp = true,
                ProfileKey = "USENET|CUSTOMER:test|묶음|후불|25|36||",
                BillingTemplateJson = canonicalTemplateJson,
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = localAssetAId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = profileId,
                    AssetKey = "USENET|A-001|IMC2000",
                    CustomerId = customerId,
                    CustomerName = profile.CustomerName,
                    CurrentCustomerName = profile.CustomerName,
                    InstallSiteName = profile.InstallSiteName,
                    InstallLocation = profile.InstallSiteName,
                    ItemName = "IMC2000",
                    ManagementNumber = "A-001",
                    AssetStatus = "임대진행중",
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = localAssetBId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = profileId,
                    AssetKey = "USENET|A-002|IMC2000",
                    CustomerId = customerId,
                    CustomerName = profile.CustomerName,
                    CurrentCustomerName = profile.CustomerName,
                    InstallSiteName = profile.InstallSiteName,
                    InstallLocation = profile.InstallSiteName,
                    ItemName = "IMC2000",
                    ManagementNumber = "A-002",
                    AssetStatus = "임대진행중",
                    IsDirty = false
                });

            var clientSnapshot = LocalMappings.ToDto(profile);
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalBillingProfile),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = profileId,
                ExpectedRevision = localRevision,
                TenantCode = profile.TenantCode,
                OfficeCode = profile.OfficeCode,
                ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(profile);
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.BillingTemplateJson = staleServerTemplateJson;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalBillingProfile",
                EntityId = profileId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var repaired = await InvokePrivateInstanceAsync<bool>(
                sync,
                "TryPrepareRentalBillingProfileRevisionRetryAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(repaired);

            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == profileId);
            var outboxRows = await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry => entry.EntityName == nameof(LocalRentalBillingProfile) && entry.EntityId == profileId)
                .ToListAsync();

            Assert.Equal(serverRevision, storedProfile.Revision);
            Assert.True(storedProfile.IsDirty);
            Assert.Equal(canonicalTemplateJson, storedProfile.BillingTemplateJson);

            var rebasedDto = LocalMappings.ToDto(storedProfile);
            rebasedDto.ExpectedRevision = serverRevision;
            var expectedMutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalBillingProfile),
                rebasedDto);

            var outboxRow = Assert.Single(outboxRows);
            Assert.Equal(expectedMutationId, outboxRow.MutationId);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.True(string.IsNullOrWhiteSpace(outboxRow.ErrorMessage));
            Assert.Null(outboxRow.SentAtUtc);
            Assert.Null(outboxRow.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_TryRepairRentalAssetRevisionConflictAsync_ResolvesWhenServerCanReplaceInvalidItemReference()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-asset-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var profileId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var serverItemId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var missingLocalItemId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            const long localRevision = 200L;
            const long serverRevision = 350L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 14, 6, 50, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:asset-resolve";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Misu Center",
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
                CustomerName = "Misu Center",
                InstallSiteName = "Social Welfare",
                ItemName = "MFC-L5700D",
                MonthlyAmount = 0m,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = serverItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "MFC-L5700D",
                NameMatchKey = "MFCL5700D",
                SpecificationOriginal = string.Empty,
                SpecificationMatchKey = string.Empty,
                CategoryName = "A4",
                ItemKind = "Rental",
                TrackingType = "Stock",
                Unit = "EA",
                BoxQuantity = 1m,
                StorageLocation = string.Empty,
                CurrentStock = 0m,
                SafetyStock = 0m,
                PurchasePrice = 297000m,
                SalePrice = 0m,
                RetailPrice = 0m,
                PriceGradeA = 0m,
                PriceGradeB = 0m,
                PriceGradeC = 0m,
                IsRental = true,
                IsSale = false,
                IsDirty = false
            });

            var asset = new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Misu Center",
                CurrentCustomerName = "Misu Center",
                BillingProfileId = profileId,
                ItemId = missingLocalItemId,
                AssetKey = "USENET|A-001|MFC-L5700D",
                ManagementId = "570",
                ManagementNumber = "A-001",
                ItemName = "MFC-L5700D",
                InstallLocation = "Social Welfare",
                InstallSiteName = "Social Welfare",
                Notes = "원본 관리ID: 570\n원본 관리번호: A-001",
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var clientSnapshot = LocalMappings.ToDto(asset);
            clientSnapshot.ItemId = null;
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalAsset),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalAsset),
                EntityId = assetId,
                ExpectedRevision = localRevision,
                TenantCode = asset.TenantCode,
                OfficeCode = asset.OfficeCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(asset);
            serverSnapshot.ItemId = serverItemId;
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.ExpectedRevision = 0;
            serverSnapshot.MutationId = string.Empty;
            serverSnapshot.MutationCreatedAtUtc = null;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalAsset",
                EntityId = assetId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var outcome = (ValueTuple<bool, bool>?)await InvokePrivateInstanceTaskResultAsync(
                sync,
                "TryRepairRentalAssetRevisionConflictAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(outcome.HasValue);
            Assert.True(outcome.Value.Item1);
            Assert.False(outcome.Value.Item2);

            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            var outboxRows = await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry => entry.EntityName == nameof(LocalRentalAsset) && entry.EntityId == assetId)
                .ToListAsync();

            Assert.Equal(serverItemId, storedAsset.ItemId);
            Assert.Equal(serverRevision, storedAsset.Revision);
            Assert.False(storedAsset.IsDirty);
            Assert.Empty(outboxRows);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_TryRepairRentalAssetRevisionConflictAsync_PreparesRetryWhenLocalStateMovedWithinAllowedFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rental-asset-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.Parse("86111111-1111-1111-1111-111111111111");
            var customerId = Guid.Parse("86222222-2222-2222-2222-222222222222");
            var profileId = Guid.Parse("86333333-3333-3333-3333-333333333333");
            var itemId = Guid.Parse("86444444-4444-4444-4444-444444444444");
            var staleCustomerId = Guid.Parse("86555555-5555-5555-5555-555555555555");
            var staleProfileId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            const long localRevision = 900L;
            const long serverRevision = 1200L;
            var updatedAtUtc = new DateTime(2026, 4, 23, 14, 6, 50, DateTimeKind.Utc);
            const string deviceId = "DESKTOP-VGCK877:asset-retry";

            db.Customers.AddRange(
                new LocalCustomer
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "연수구 함박비류 도서관",
                    IsDirty = false
                },
                new LocalCustomer
                {
                    Id = staleCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "[연수구]함박비류도서관",
                    IsDirty = false
                });
            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerId = customerId,
                    CustomerName = "연수구 함박비류 도서관",
                    ProfileKey = "PROFILE|ACTIVE|HAMBAK",
                    InstallSiteName = "2층 컴퓨터실",
                    ItemName = "SL-M2670FN",
                    MonthlyAmount = 0m,
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = staleProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    CustomerId = staleCustomerId,
                    CustomerName = "[연수구]함박비류도서관",
                    ProfileKey = "PROFILE|STALE|HAMBAK",
                    InstallSiteName = "2층 컴퓨터실",
                    ItemName = "SL-M2670FN",
                    MonthlyAmount = 0m,
                    IsDirty = false
                });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "SL-M2670FN",
                NameMatchKey = "SLM2670FN",
                SpecificationOriginal = string.Empty,
                SpecificationMatchKey = string.Empty,
                CategoryName = "A4",
                ItemKind = "Rental",
                TrackingType = "Stock",
                Unit = "EA",
                BoxQuantity = 1m,
                StorageLocation = string.Empty,
                CurrentStock = 0m,
                SafetyStock = 0m,
                PurchasePrice = 0m,
                SalePrice = 0m,
                RetailPrice = 0m,
                PriceGradeA = 0m,
                PriceGradeB = 0m,
                PriceGradeC = 0m,
                IsRental = true,
                IsSale = false,
                IsDirty = false
            });

            var asset = new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "연수구 함박비류 도서관",
                CurrentCustomerName = "연수구 함박비류 도서관",
                BillingProfileId = profileId,
                ItemId = itemId,
                AssetKey = "USENET|A-002|SL-M2670FN",
                ManagementId = "438",
                ManagementNumber = "A-002",
                ItemName = "SL-M2670FN",
                InstallLocation = "2층 컴퓨터실",
                InstallSiteName = "2층 컴퓨터실",
                Revision = localRevision,
                UpdatedAtUtc = updatedAtUtc,
                IsDirty = true
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var clientSnapshot = LocalMappings.ToDto(asset);
            clientSnapshot.CustomerId = staleCustomerId;
            clientSnapshot.CustomerName = "[연수구]함박비류도서관";
            clientSnapshot.CurrentCustomerName = "[연수구]함박비류도서관";
            clientSnapshot.BillingProfileId = staleProfileId;
            clientSnapshot.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            clientSnapshot.ExpectedRevision = localRevision;
            clientSnapshot.MutationCreatedAtUtc = updatedAtUtc;
            clientSnapshot.MutationId = InvokePrivateStatic<string>(
                typeof(SyncService),
                "BuildMutationId",
                deviceId,
                nameof(LocalRentalAsset),
                clientSnapshot);

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = clientSnapshot.MutationId,
                DeviceId = deviceId,
                EntityName = nameof(LocalRentalAsset),
                EntityId = assetId,
                ExpectedRevision = localRevision,
                TenantCode = asset.TenantCode,
                OfficeCode = asset.OfficeCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                Status = "Sent",
                PreparedAtUtc = updatedAtUtc,
                SentAtUtc = updatedAtUtc.AddMinutes(1)
            });
            await db.SaveChangesAsync();

            var serverSnapshot = LocalMappings.ToDto(asset);
            serverSnapshot.CustomerId = null;
            serverSnapshot.CustomerName = "[연수구]함박비류도서관";
            serverSnapshot.CurrentCustomerName = "[연수구]함박비류도서관";
            serverSnapshot.Revision = serverRevision;
            serverSnapshot.UpdatedAtUtc = updatedAtUtc.AddMinutes(-5);
            serverSnapshot.ExpectedRevision = 0;
            serverSnapshot.MutationId = string.Empty;
            serverSnapshot.MutationCreatedAtUtc = null;

            var conflict = new ConflictLogDto
            {
                EntityName = "RentalAsset",
                EntityId = assetId.ToString("D"),
                Reason = $"Expected revision mismatch. client={localRevision}, server={serverRevision}",
                ClientJson = JsonSerializer.Serialize(clientSnapshot),
                ServerJson = JsonSerializer.Serialize(serverSnapshot)
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var officeAccess = new OfficeAccessService();
            var dispatcher = new SyncRequestDispatcher();
            var diagnostics = new SyncDiagnosticsService(session);
            var localState = new LocalStateService(db, officeAccess, dispatcher, session);
            var rental = new RentalStateService(db);
            var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
            using var sync = new SyncService(db, localState, rental, api, session, dispatcher, diagnostics);

            var outcome = (ValueTuple<bool, bool>?)await InvokePrivateInstanceTaskResultAsync(
                sync,
                "TryRepairRentalAssetRevisionConflictAsync",
                conflict,
                deviceId,
                session,
                CancellationToken.None);

            Assert.True(outcome.HasValue);
            Assert.False(outcome.Value.Item1);
            Assert.True(outcome.Value.Item2);

            var storedAsset = await db.RentalAssets.IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            var outboxRow = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.EntityName == nameof(LocalRentalAsset) && entry.EntityId == assetId);

            Assert.Equal(serverRevision, storedAsset.Revision);
            Assert.True(storedAsset.IsDirty);
            Assert.Equal(customerId, storedAsset.CustomerId);
            Assert.Equal(profileId, storedAsset.BillingProfileId);
            Assert.Equal(OfficeCodeCatalog.Usenet, storedAsset.ResponsibleOfficeCode);
            Assert.Equal("Prepared", outboxRow.Status);
            Assert.Equal(serverRevision, outboxRow.ExpectedRevision);
            Assert.True(string.IsNullOrWhiteSpace(outboxRow.ErrorMessage));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void RentalBillingProfileKey_IncludesLinkedCustomerDisplayName()
    {
        var customerId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var officialKey = RentalDuplicateNormalizer.BuildProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");
        var aliasKey = RentalDuplicateNormalizer.BuildProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks[Quality]",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");
        var legacyKey = RentalDuplicateNormalizer.BuildLegacyProfileKey(
            "USENET",
            customerId,
            "123-45-67890",
            "Waterworks[Quality]",
            "Bundle",
            "Postpaid",
            25,
            1,
            "TaxInvoice");

        Assert.Contains("NAME:WATERWORKS", officialKey, StringComparison.Ordinal);
        Assert.Contains("NAME:WATERWORKSQUALITY", aliasKey, StringComparison.Ordinal);
        Assert.NotEqual(officialKey, aliasKey);
        Assert.DoesNotContain("NAME:", legacyKey, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingProfileDisplay_DerivesLegacyAliasFromProfileKey()
    {
        var alias = InvokePrivateStatic<string>(
            typeof(RentalStateService),
            "TryResolveBillingProfileAliasFromProfileKey",
            "USENET||WaterworksQuality||IMC2000",
            "Waterworks");

        Assert.Equal("Waterworks[Quality]", alias);
    }

    [Fact]
    public async Task ResolveCustomerIdAsync_RespectsPreferredTenant()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-tenant-resolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var itworldCustomerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            db.Customers.Add(new LocalCustomer
            {
                Id = usenetCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "아이티월드",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("아이티월드"),
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var rental = new RentalStateService(db);
            var wrongTenantOnly = await InvokePrivateInstanceAsync<Guid?>(
                rental,
                "ResolveCustomerIdAsync",
                "아이티월드",
                null,
                CancellationToken.None,
                true,
                null,
                TenantScopeCatalog.Itworld);

            Assert.Null(wrongTenantOnly);

            db.Customers.Add(new LocalCustomer
            {
                Id = itworldCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "아이티월드",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("아이티월드"),
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var preferredTenantMatch = await InvokePrivateInstanceAsync<Guid?>(
                rental,
                "ResolveCustomerIdAsync",
                "아이티월드",
                null,
                CancellationToken.None,
                true,
                null,
                TenantScopeCatalog.Itworld);

            Assert.Equal(itworldCustomerId, preferredTenantMatch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
    }

    [Fact]
    public async Task SaveBillingProfileAsync_RejectsCrossTenantIncludedAsset()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-profile-asset-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var crossTenantAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = crossTenantAssetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = "ITWORLD|2604-001|Waterworks|IMC2000",
                CustomerName = "Waterworks",
                CurrentCustomerName = "Waterworks",
                ItemName = "IMC2000",
                ManagementNumber = "2604-001",
                AssetStatus = "임대진행중"
            });
            await db.SaveChangesAsync();

            var profile = new LocalRentalBillingProfile
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Waterworks[Quality]",
                ItemName = "IMC2000",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "IMC2000",
                        BillingLineMode = "묶음",
                        Quantity = 1,
                        UnitPrice = 90000m,
                        Amount = 90000m,
                        IncludedAssetIds = [crossTenantAssetId]
                    }
                })
            };
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var rental = new RentalStateService(db);
            var result = await rental.SaveBillingProfileAsync(profile, session);

            Assert.False(result.Success);
            Assert.True(result.PermissionDenied);
            Assert.Contains("다른 업체/담당지점", result.Message, StringComparison.Ordinal);
            Assert.Empty(await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
    }

    [Fact]
    public void RentalBillingTemplateEditorItem_RecalculatesAmountFromQuantityAndUnitPrice()
    {
        var item = new RentalBillingTemplateEditorItem
        {
            Quantity = 2m,
            UnitPrice = 1000m
        };

        Assert.Equal(2000m, item.Amount);
        Assert.Equal(2000m, item.EffectiveAmount);

        item.UnitPrice = 1500m;
        Assert.Equal(3000m, item.Amount);

        item.Quantity = 3m;
        Assert.Equal(4500m, item.Amount);

        item.Amount = 9999m;
        item.NormalizeCalculatedAmount();
        Assert.Equal(4500m, item.Amount);
    }

    [Fact]
    public void RentalBillingViewModel_UpdateTemplateDerivedValues_DoesNotReenterFromCalculatedAmountNotification()
    {
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "렌탈료",
            BillingLineMode = "묶음",
            Quantity = 1m,
            UnitPrice = 1000m,
            Amount = 1000m
        };

        vm.TemplateItems.Add(item);
        InvokePrivateInstance(vm, "WireTemplateItem", item);
        InvokePrivateInstance(vm, "UpdateTemplateDerivedValues");

        Assert.Equal(1000m, vm.EditMonthlyAmount);
        Assert.Equal(1000m, item.Amount);
        Assert.Equal("렌탈료", vm.EditItemName);
    }

    [Fact]
    public async Task SaveAssetAsync_RefreshesLinkedBillingProfileMonthlyAmount()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-asset-monthly-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var assetId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                ItemName = "Printer",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 100000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Printer",
                        BillingLineMode = "묶음",
                        Quantity = 1m,
                        UnitPrice = 100000m,
                        Amount = 100000m,
                        IncludedAssetIds = [assetId]
                    }
                })
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                AssetKey = "USENET|A-001|SN-001|Monthly Sync Customer|Printer",
                CustomerName = "Monthly Sync Customer",
                CurrentCustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                InstallLocation = "Main Office",
                ItemName = "Printer",
                ManagementNumber = "A-001",
                MachineNumber = "SN-001",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 100000m
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var rental = new RentalStateService(db);
            var result = await rental.SaveAssetAsync(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = profileId,
                CustomerName = "Monthly Sync Customer",
                CurrentCustomerName = "Monthly Sync Customer",
                InstallSiteName = "Main Office",
                InstallLocation = "Main Office",
                ItemName = "Printer",
                ManagementNumber = "A-001",
                MachineNumber = "SN-001",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 150000m
            }, session);

            Assert.True(result.Success, result.Message);
            var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstAsync(profile => profile.Id == profileId);
            var storedTemplateItems = rental.GetBillingTemplateItems(storedProfile);

            Assert.Equal(150000m, storedProfile.MonthlyAmount);
            Assert.Single(storedTemplateItems);
            Assert.Equal(150000m, storedTemplateItems[0].UnitPrice);
            Assert.Equal(150000m, storedTemplateItems[0].Amount);
            Assert.True(storedProfile.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbInitializer_RepairRentalCustomerLinkage_NormalizesItworldScope_AndKeepsYeonsuScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-scope-repair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var brokenProfileId = Guid.Parse("81111111-1111-1111-1111-111111111111");
            var brokenAssetId = Guid.Parse("82222222-2222-2222-2222-222222222222");
            var brokenLogId = Guid.Parse("83333333-3333-3333-3333-333333333333");
            var yeonsuProfileId = Guid.Parse("84444444-4444-4444-4444-444444444444");
            var yeonsuAssetId = Guid.Parse("85555555-5555-5555-5555-555555555555");
            var wrongUsenetCustomerId = Guid.Parse("86666666-6666-6666-6666-666666666666");

            db.Customers.Add(new LocalCustomer
            {
                Id = wrongUsenetCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Wrong USENET Customer",
                NameMatchKey = "WRONGUSENETCUSTOMER",
                IsDirty = false
            });

            db.RentalBillingProfiles.AddRange(
                new LocalRentalBillingProfile
                {
                    Id = brokenProfileId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerId = wrongUsenetCustomerId,
                    ProfileKey = "ITWORLD|BROKEN-ITWORLD-CUSTOMER|ITWORLD-SITE|PRINTER",
                    CustomerName = "Broken ITWORLD Customer",
                    InstallSiteName = "ITWORLD Site",
                    ItemName = "Printer",
                    MonthlyAmount = 120000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Printer",
                            Quantity = 1m,
                            UnitPrice = 120000m,
                            Amount = 120000m,
                            IncludedAssetIds = [brokenAssetId]
                        }
                    }),
                    IsDirty = false
                },
                new LocalRentalBillingProfile
                {
                    Id = yeonsuProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ProfileKey = "USENET|YEONSU-CUSTOMER|YEONSU-SITE|COPIER",
                    CustomerName = "YEONSU Customer",
                    InstallSiteName = "YEONSU Site",
                    ItemName = "Copier",
                    MonthlyAmount = 90000m,
                    BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                    {
                        new()
                        {
                            DisplayItemName = "Copier",
                            Quantity = 1m,
                            UnitPrice = 90000m,
                            Amount = 90000m,
                            IncludedAssetIds = [yeonsuAssetId]
                        }
                    }),
                    IsDirty = false
                });

            db.RentalAssets.AddRange(
                new LocalRentalAsset
                {
                    Id = brokenAssetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerId = wrongUsenetCustomerId,
                    BillingProfileId = brokenProfileId,
                    AssetKey = "ITWORLD|BROKEN-001|SN-BROKEN",
                    CustomerName = "Broken ITWORLD Customer",
                    CurrentCustomerName = "Broken ITWORLD Customer",
                    InstallSiteName = "ITWORLD Site",
                    InstallLocation = "ITWORLD Site",
                    ItemName = "Printer",
                    ManagementNumber = "BROKEN-001",
                    MachineNumber = "SN-BROKEN",
                    AssetStatus = "ACTIVE",
                    BillingEligibilityStatus = string.Empty,
                    MonthlyFee = 120000m,
                    IsDirty = false
                },
                new LocalRentalAsset
                {
                    Id = yeonsuAssetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    BillingProfileId = yeonsuProfileId,
                    AssetKey = "USENET|YEONSU-001|SN-YEONSU",
                    CustomerName = "YEONSU Customer",
                    CurrentCustomerName = "YEONSU Customer",
                    InstallSiteName = "YEONSU Site",
                    InstallLocation = "YEONSU Site",
                    ItemName = "Copier",
                    ManagementNumber = "YEONSU-001",
                    MachineNumber = "SN-YEONSU",
                    AssetStatus = "ACTIVE",
                    BillingEligibilityStatus = string.Empty,
                    MonthlyFee = 90000m,
                    IsDirty = false
                });

            db.RentalBillingLogs.Add(new LocalRentalBillingLog
            {
                Id = brokenLogId,
                BillingProfileId = brokenProfileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "202604",
                Status = "PENDING",
                BilledAmount = 120000m,
                IsDirty = false
            });

            await db.SaveChangesAsync();
            var repairMethod = typeof(LocalDbInitializer).GetMethod(
                "RepairRentalCustomerLinkageAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(repairMethod);

            var repairTask = repairMethod!.Invoke(null, new object?[] { db }) as Task;
            Assert.NotNull(repairTask);
            await repairTask!;
            await db.SaveChangesAsync();

            var fixedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == brokenProfileId);
            var fixedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == brokenAssetId);
            var fixedLog = await db.RentalBillingLogs.IgnoreQueryFilters().SingleAsync(log => log.Id == brokenLogId);
            var yeonsuProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == yeonsuProfileId);
            var yeonsuAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAssetId);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedProfile.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedProfile.ResponsibleOfficeCode);
            Assert.True(fixedProfile.IsDirty);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedAsset.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedAsset.ResponsibleOfficeCode);
            Assert.True(fixedAsset.IsDirty);

            Assert.Equal(TenantScopeCatalog.Itworld, fixedLog.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Itworld, fixedLog.ResponsibleOfficeCode);
            Assert.True(fixedLog.IsDirty);

            Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuProfile.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuProfile.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuProfile.ResponsibleOfficeCode);

            Assert.Equal(TenantScopeCatalog.UsenetGroup, yeonsuAsset.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, yeonsuAsset.ManagementCompanyCode);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, yeonsuAsset.ResponsibleOfficeCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_FindsRentalRiskSignals()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var missingAssetId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var zeroFeeAssetId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Integrity Customer",
                InstallSiteName = "Main Office",
                ItemName = "Printer",
                MonthlyAmount = 50_000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "Printer",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        Amount = 100_000m,
                        IncludedAssetIds = [missingAssetId]
                    }
                }),
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = zeroFeeAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|ZERO|SN-ZERO",
                CurrentCustomerName = "Integrity Customer",
                ItemName = "Printer",
                ManagementNumber = "ZERO",
                AssetStatus = "임대",
                BillingEligibilityStatus = "청구대상",
                MonthlyFee = 0m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(session);

            Assert.True(result.HasIssues);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalTemplateMissingAsset);
            Assert.Contains(result.Summaries, summary => summary.Code == DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DataIntegrityIssueService_ScanAsync_UsesCanonicalScopeForMixedItworldProfile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-data-integrity-itworld-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("86666666-6666-6666-6666-666666666666");
            var assetId = Guid.Parse("87777777-7777-7777-7777-777777777777");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                CustomerName = "ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                ItemName = "MP2555",
                MonthlyAmount = 300000m,
                BillingTemplateJson = JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
                {
                    new()
                    {
                        DisplayItemName = "MP2555",
                        Quantity = 1m,
                        UnitPrice = 300000m,
                        Amount = 300000m,
                        IncludedAssetIds = [assetId]
                    }
                }),
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                BillingProfileId = profileId,
                AssetKey = "ITWORLD|1405-003|MP2555",
                CurrentCustomerName = "ITWORLD Customer",
                CustomerName = "ITWORLD Customer",
                InstallSiteName = "ITWORLD Site",
                InstallLocation = "ITWORLD Site",
                ItemName = "MP2555",
                ManagementNumber = "1405-003",
                AssetStatus = "ACTIVE",
                BillingEligibilityStatus = string.Empty,
                MonthlyFee = 300000m,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var service = new DataIntegrityIssueService(db);
            var result = await service.ScanAsync(session);

            Assert.DoesNotContain(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch &&
                issue.ProfileId == profileId &&
                issue.AssetId == assetId);

            var scopeIssue = Assert.Single(result.Issues, issue =>
                issue.Code == DataIntegrityIssueCodes.RentalOperationalScopeMismatch &&
                issue.ProfileId == profileId);
            Assert.Contains("ITWORLD / ITWORLD / USENET", scopeIssue.CurrentValue);
            Assert.Contains("ITWORLD / ITWORLD / ITWORLD", scopeIssue.ExpectedValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncDiagnosticsSummary_UsesOnlyOpenIssuesForLastFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-diagnostics-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var resolvedAt = new DateTime(2026, 4, 21, 11, 34, 41, DateTimeKind.Utc);
            var openAt = resolvedAt.AddMinutes(5);
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.LastSuccessAt",
                Value = resolvedAt.AddSeconds(15).ToString("O")
            });
            db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                OccurredAtUtc = resolvedAt,
                LastOccurredAtUtc = resolvedAt,
                Severity = "Warning",
                Category = "integrity",
                Subcategory = "runtime-periodic-integrity",
                SyncPhase = "runtime-periodic-integrity",
                RawMessage = "resolved integrity warning",
                NormalizedMessage = "resolved integrity warning",
                RecoveryAttempted = true,
                RecoverySucceeded = true,
                ResolvedAtUtc = resolvedAt.AddSeconds(2),
                Status = "Resolved"
            });
            await db.SaveChangesAsync();

            var session = new SessionState();
            var diagnostics = new SyncDiagnosticsService(session);
            var resolvedOnlySummary = await diagnostics.GetSummaryAsync();

            Assert.Equal(0, resolvedOnlySummary.OpenIssueCount);
            Assert.Null(resolvedOnlySummary.LastFailureAtUtc);

            db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                OccurredAtUtc = openAt,
                LastOccurredAtUtc = openAt,
                Severity = "Error",
                Category = "sync",
                Subcategory = "general_sync_failure",
                SyncPhase = "manual-sync",
                RawMessage = "open sync failure",
                NormalizedMessage = "open sync failure",
                Status = "Open"
            });
            await db.SaveChangesAsync();

            var openSummary = await diagnostics.GetSummaryAsync();

            Assert.Equal(1, openSummary.OpenIssueCount);
            Assert.Equal(openAt, openSummary.LastFailureAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            // AppPaths is static for the test process; keep the temp root available
            // until process exit so parallel xUnit tests cannot delete the active DB path.
        }
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

    private static T InvokePrivateStatic<T>(string methodName, params object?[]? args)
    {
        var method = typeof(LocalStateService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[]? args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, args);
        Assert.NotNull(result);
        return (T)result!;
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        method!.Invoke(target, args);
    }

    private static async Task<T?> InvokePrivateInstanceAsync<T>(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        return await (Task<T?>)result!;
    }

    private static async Task<object?> InvokePrivateInstanceTaskResultAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = result as Task;
        Assert.NotNull(task);
        await task!;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
}
