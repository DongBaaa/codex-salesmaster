using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingInvoiceAggregationTests
{
    [Fact]
    public async Task BuildRentalBillingInvoiceLinesAsync_GroupsSameModelAndUnitPriceAsQuantity()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-invoice-aggregate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a1000000-0000-0000-0000-000000000001");
            var assetAId = Guid.Parse("a1000000-0000-0000-0000-0000000000a1");
            var assetBId = Guid.Parse("a1000000-0000-0000-0000-0000000000b2");
            db.RentalAssets.AddRange(
                CreateBillableAsset(assetAId, profileId, "AGG-A", "SN-A", "IMC2010", 50_000m),
                CreateBillableAsset(assetBId, profileId, "AGG-B", "SN-B", "IMC2010", 50_000m));
            await db.SaveChangesAsync();

            var profile = CreateProfile(profileId, "\uAC1C\uBCC4");
            var run = CreateRun();
            var templateItems = new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "IMC2010",
                    BillingLineMode = "\uAC1C\uBCC4",
                    Unit = "\uB300",
                    IncludedAssetIds = [assetAId, assetBId]
                }
            };

            var result = await InvokeBuildRentalBillingInvoiceLinesAsync(
                new RentalStateService(db),
                profile,
                run,
                templateItems,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var line = Assert.Single(result.Lines);
            Assert.Equal("\uC0AC\uBB34\uAE30\uAE30 \uB80C\uD0C8\uB300\uAE08[6\uC6D4]", line.ItemNameOriginal);
            Assert.Equal(2m, line.Quantity);
            Assert.Equal(50_000m, line.UnitPrice);
            Assert.Equal(100_000m, line.LineAmount);
            Assert.Equal("IMC2010", line.SpecificationOriginal);
            Assert.Equal("AGG-A \uC678 1\uAC74", line.MaterialNumber);
            Assert.Equal("SN-A \uC678 1\uAC74", line.SerialNumber);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BuildRentalBillingInvoiceLinesAsync_DoesNotGroupSameModelWhenUnitPriceDiffers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-invoice-separate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a2000000-0000-0000-0000-000000000001");
            var assetAId = Guid.Parse("a2000000-0000-0000-0000-0000000000a1");
            var assetBId = Guid.Parse("a2000000-0000-0000-0000-0000000000b2");
            db.RentalAssets.AddRange(
                CreateBillableAsset(assetAId, profileId, "SEP-A", "SN-A", "IMC2010", 50_000m),
                CreateBillableAsset(assetBId, profileId, "SEP-B", "SN-B", "IMC2010", 70_000m));
            await db.SaveChangesAsync();

            var result = await InvokeBuildRentalBillingInvoiceLinesAsync(
                new RentalStateService(db),
                CreateProfile(profileId, "\uAC1C\uBCC4"),
                CreateRun(),
                [
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "IMC2010",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        IncludedAssetIds = [assetAId, assetBId]
                    }
                ],
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Lines.Count);
            Assert.All(result.Lines, line => Assert.Equal(1m, line.Quantity));
            Assert.Contains(result.Lines, line => line.UnitPrice == 50_000m);
            Assert.Contains(result.Lines, line => line.UnitPrice == 70_000m);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BuildRentalBillingInvoiceLinesAsync_DoesNotGroupDifferentModelsWithSameDisplayItemAndUnitPrice()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-invoice-model-separate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a4000000-0000-0000-0000-000000000001");
            var assetAId = Guid.Parse("a4000000-0000-0000-0000-0000000000a1");
            var assetBId = Guid.Parse("a4000000-0000-0000-0000-0000000000b2");
            db.RentalAssets.AddRange(
                CreateBillableAsset(assetAId, profileId, "MODEL-A", "SN-A", "IMC2010", 50_000m),
                CreateBillableAsset(assetBId, profileId, "MODEL-B", "SN-B", "MFC-L5700DN", 50_000m));
            await db.SaveChangesAsync();

            var result = await InvokeBuildRentalBillingInvoiceLinesAsync(
                new RentalStateService(db),
                CreateProfile(profileId, "\uAC1C\uBCC4"),
                CreateRun(),
                [
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "\uBCF5\uD569\uAE30 \uB80C\uD0C8\uB8CC",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        IncludedAssetIds = [assetAId, assetBId]
                    }
                ],
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            Assert.Equal(2, result.Lines.Count);
            Assert.All(result.Lines, line =>
            {
                Assert.Equal(1m, line.Quantity);
                Assert.Equal(50_000m, line.UnitPrice);
                Assert.Equal(50_000m, line.LineAmount);
            });
            Assert.Contains(result.Lines, line => line.SpecificationOriginal == "IMC2010");
            Assert.Contains(result.Lines, line => line.SpecificationOriginal == "MFC-L5700DN");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BuildRentalBillingInvoiceLinesAsync_GroupsSameModelAcrossDifferentDisplayItems()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-invoice-model-display-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a5000000-0000-0000-0000-000000000001");
            var assetAId = Guid.Parse("a5000000-0000-0000-0000-0000000000a1");
            var assetBId = Guid.Parse("a5000000-0000-0000-0000-0000000000b2");
            db.RentalAssets.AddRange(
                CreateBillableAsset(assetAId, profileId, "DISPLAY-A", "SN-A", "IMC2010", 50_000m),
                CreateBillableAsset(assetBId, profileId, "DISPLAY-B", "SN-B", "IMC2010", 50_000m));
            await db.SaveChangesAsync();

            var result = await InvokeBuildRentalBillingInvoiceLinesAsync(
                new RentalStateService(db),
                CreateProfile(profileId, "\uD63C\uD569"),
                CreateRun(),
                [
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "\uBCF5\uD569\uAE30 A",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        IncludedAssetIds = [assetAId]
                    },
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "\uBCF5\uD569\uAE30 B",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        IncludedAssetIds = [assetBId]
                    }
                ],
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var line = Assert.Single(result.Lines);
            Assert.Equal("IMC2010", line.SpecificationOriginal);
            Assert.Equal(2m, line.Quantity);
            Assert.Equal(50_000m, line.UnitPrice);
            Assert.Equal(100_000m, line.LineAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task BuildRentalBillingInvoiceLinesAsync_SkipsZeroFeeIndividualAssetsAndGroupsBillableModels()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-invoice-zero-skip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("a6000000-0000-0000-0000-000000000001");
            var billableAId = Guid.Parse("a6000000-0000-0000-0000-0000000000a1");
            var billableBId = Guid.Parse("a6000000-0000-0000-0000-0000000000b2");
            var zeroAId = Guid.Parse("a6000000-0000-0000-0000-0000000000c3");
            var zeroBId = Guid.Parse("a6000000-0000-0000-0000-0000000000d4");
            db.RentalAssets.AddRange(
                CreateBillableAsset(billableAId, profileId, "BILL-A", "SN-A", "IMC2010", 240_000m),
                CreateBillableAsset(billableBId, profileId, "BILL-B", "SN-B", "IMC2010", 240_000m),
                CreateBillableAsset(zeroAId, profileId, "ZERO-A", "SN-C", "SL-M3820ND", 0m),
                CreateBillableAsset(zeroBId, profileId, "ZERO-B", "SN-D", "SL-M3820ND", 0m));
            await db.SaveChangesAsync();

            var result = await InvokeBuildRentalBillingInvoiceLinesAsync(
                new RentalStateService(db),
                CreateProfile(profileId, "\uAC1C\uBCC4"),
                CreateRun(),
                [
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "SL-M3820ND",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        Quantity = 2m,
                        UnitPrice = 0m,
                        Amount = 0m,
                        IncludedAssetIds = [zeroAId, zeroBId]
                    },
                    new RentalBillingTemplateItemModel
                    {
                        DisplayItemName = "IMC2010",
                        BillingLineMode = "\uAC1C\uBCC4",
                        Unit = "\uB300",
                        Quantity = 2m,
                        UnitPrice = 240_000m,
                        Amount = 480_000m,
                        IncludedAssetIds = [billableAId, billableBId]
                    }
                ],
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var line = Assert.Single(result.Lines);
            Assert.Equal("IMC2010", line.SpecificationOriginal);
            Assert.Equal(2m, line.Quantity);
            Assert.Equal(240_000m, line.UnitPrice);
            Assert.Equal(480_000m, line.LineAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalAsset CreateBillableAsset(
        Guid assetId,
        Guid profileId,
        string managementNumber,
        string serialNumber,
        string itemName,
        decimal monthlyFee)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingProfileId = profileId,
            AssetKey = $"USENET|{managementNumber}|{serialNumber}",
            ManagementNumber = managementNumber,
            MachineNumber = serialNumber,
            ItemName = itemName,
            CustomerName = "\uC218\uB7C9\uD569\uC0B0 \uAC70\uB798\uCC98",
            CurrentCustomerName = "\uC218\uB7C9\uD569\uC0B0 \uAC70\uB798\uCC98",
            AssetStatus = "\uC784\uB300\uC9C4\uD589\uC911",
            BillingEligibilityStatus = "\uCCAD\uAD6C\uB300\uC0C1",
            MonthlyFee = monthlyFee
        };

    private static LocalRentalBillingProfile CreateProfile(Guid profileId, string billingType)
        => new()
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerName = "\uC218\uB7C9\uD569\uC0B0 \uAC70\uB798\uCC98",
            BillingType = billingType,
            BillingAdvanceMode = "\uD6C4\uBD88",
            BillingDay = 25,
            BillingCycleMonths = 1
        };

    private static RentalBillingRunModel CreateRun()
        => new()
        {
            RunId = Guid.Parse("a3000000-0000-0000-0000-000000000001"),
            ScheduledDate = new DateOnly(2026, 6, 25),
            PeriodStartDate = new DateOnly(2026, 6, 1),
            PeriodEndDate = new DateOnly(2026, 6, 30),
            CycleMonths = 1
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

    private static async Task<(bool Success, string Message, List<LocalInvoiceLine> Lines)> InvokeBuildRentalBillingInvoiceLinesAsync(
        RentalStateService service,
        LocalRentalBillingProfile profile,
        RentalBillingRunModel run,
        IReadOnlyList<RentalBillingTemplateItemModel> templateItems,
        SessionState session)
    {
        var method = typeof(RentalStateService).GetMethod(
            "BuildRentalBillingInvoiceLinesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(service, [profile, run, templateItems, session, CancellationToken.None]);
        Assert.NotNull(result);
        return await Assert.IsType<Task<(bool Success, string Message, List<LocalInvoiceLine> Lines)>>(result);
    }
}
