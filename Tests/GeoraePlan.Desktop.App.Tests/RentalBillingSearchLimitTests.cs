using Microsoft.Data.Sqlite;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSearchLimitTests
{
    [Fact]
    public async Task GetBillingRowsAsync_DefersSupplementalUnlinkedAssetsWhenProfilesFillSearchLimit()
    {
        PrepareAppRoot("georaeplan-rental-billing-search-profile-first");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var limit = RentalStateService.BillingProfileSearchResultLimit;
            for (var index = 0; index < limit; index++)
                db.RentalBillingProfiles.Add(CreateBillingProfile(index, $"Billing Search Customer {index:D4}"));

            var unlinkedAssetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateUnlinkedBillingAsset(unlinkedAssetId, "Billing Search Unlinked Customer"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Billing Search",
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Equal(limit, rows.Count);
            Assert.All(rows, row => Assert.True(row.HasPersistedProfile));
            Assert.DoesNotContain(rows, row => row.SelectionId == unlinkedAssetId);
            Assert.Equal(0, rows.Sum(row => row.GroupedUnlinkedAssetCount));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_LoadsUnlinkedAssetsWhenUnlinkedStatusIsFocusedEvenWithSearchText()
    {
        PrepareAppRoot("georaeplan-rental-billing-search-focused-unlinked");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var limit = RentalStateService.BillingProfileSearchResultLimit;
            for (var index = 0; index < limit; index++)
                db.RentalBillingProfiles.Add(CreateBillingProfile(index, $"Billing Search Customer {index:D4}"));

            var unlinkedAssetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateUnlinkedBillingAsset(unlinkedAssetId, "Billing Search Unlinked Customer"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Billing Search Unlinked",
                    Status = "\uCCAD\uAD6C\uC124\uC815 \uD544\uC694",
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.False(row.HasPersistedProfile);
            Assert.True(row.RequiresBillingProfileCreation);
            Assert.Equal(unlinkedAssetId, row.SelectionId);
            Assert.Equal("Billing Search Unlinked Customer", row.CustomerDisplayName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public void ResolveBillingRunsForRowBuild_ListModeKeepsOnlyPastRunsButHistoryModeKeepsAllRuns()
    {
        var duplicatePastRunId = Guid.NewGuid();
        var pastRun = CreateBillingRun(duplicatePastRunId, new DateOnly(2026, 5, 25));
        var duplicatePastRun = CreateBillingRun(duplicatePastRunId, new DateOnly(2026, 5, 30));
        var currentRun = CreateBillingRun(Guid.NewGuid(), new DateOnly(2026, 6, 25));
        var futureRun = CreateBillingRun(Guid.NewGuid(), new DateOnly(2026, 7, 25));
        var emptyIdRun = CreateBillingRun(Guid.Empty, new DateOnly(2026, 4, 25));
        var runs = new[] { pastRun, duplicatePastRun, currentRun, futureRun, emptyIdRun };

        var listModeRuns = InvokeResolveBillingRunsForRowBuild(
            runs,
            new DateOnly(2026, 6, 11),
            includeHistoryRows: false);

        var keptPastRun = Assert.Single(listModeRuns);
        Assert.Equal(duplicatePastRunId, keptPastRun.RunId);
        Assert.Equal(new DateOnly(2026, 5, 25), keptPastRun.ScheduledDate);

        var historyModeRuns = InvokeResolveBillingRunsForRowBuild(
            runs,
            new DateOnly(2026, 6, 11),
            includeHistoryRows: true);

        Assert.Equal(3, historyModeRuns.Count);
        Assert.Contains(historyModeRuns, run => run.RunId == duplicatePastRunId);
        Assert.Contains(historyModeRuns, run => run.RunId == currentRun.RunId);
        Assert.Contains(historyModeRuns, run => run.RunId == futureRun.RunId);
        Assert.DoesNotContain(historyModeRuns, run => run.RunId == Guid.Empty);
    }


    [Fact]
    public async Task GetBillingRowsAsync_PreservesAssetSummaryInstallLocationAndDataIssues()
    {
        PrepareAppRoot("georaeplan-rental-billing-asset-summary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var firstAssetId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            var profile = CreateBillingProfile(1, "Asset Summary Customer");
            profile.Id = profileId;
            profile.InstallSiteName = string.Empty;
            profile.BillingTemplateJson = System.Text.Json.JsonSerializer.Serialize(new List<RentalBillingTemplateItemModel>
            {
                new()
                {
                    DisplayItemName = "\uB80C\uD0C8\uB8CC",
                    BillingLineMode = "\uBB36\uC74C",
                    Quantity = 1m,
                    UnitPrice = 80_000m,
                    Amount = 80_000m,
                    IncludedAssetIds = new List<Guid> { firstAssetId }
                }
            });

            db.RentalBillingProfiles.Add(profile);
            db.RentalAssets.AddRange(
                CreateLinkedBillingAsset(firstAssetId, profileId, "Asset Summary Customer", "1\uCE35", 100_000m, "\uCCAD\uAD6C\uB300\uC0C1"),
                CreateLinkedBillingAsset(secondAssetId, profileId, "Asset Summary Customer", "2\uCE35", 0m, "\uBBF8\uD655\uC778"));
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Asset Summary",
                    ExpandCustomerSummaryRows = true,
                    IncludeHistoryRows = false,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.Equal(profileId, row.Source.Id);
            Assert.Equal(2, row.AssetCount);
            Assert.Equal(1, row.TemplateItemCount);
            Assert.Equal(1, row.IncludedAssetCount);
            Assert.Contains(row.InstallLocationDisplay, new[] { "1\uCE35 \uC678 1\uACF3", "2\uCE35 \uC678 1\uACF3" });
            Assert.True(row.HasDataIssue);
            Assert.Contains("\uC124\uCE58\uC704\uCE58 \uBBF8\uC124\uC815", row.DataIssueSummary);
            Assert.Contains("\uC7A5\uBE44 \uC6D4\uC694\uAE08 \uC5C6\uC74C", row.DataIssueSummary);
            Assert.Contains("\uC790\uC0B0/\uCCAD\uAD6C \uC6D4\uC694\uAE08 \uBD88\uC77C\uCE58", row.DataIssueSummary);
            Assert.Contains("\uCCAD\uAD6C\uB300\uC0C1 \uAC80\uD1A0 \uD544\uC694", row.DataIssueSummary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task SelectBillingLinkedAssetRowProjection_LoadsOnlyRowSummaryFields()
    {
        PrepareAppRoot("georaeplan-rental-billing-linked-asset-light-projection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var asset = CreateLinkedBillingAsset(
                assetId,
                profileId,
                "Projection Customer",
                "3\uCE35",
                123_000m,
                "\uCCAD\uAD6C\uB300\uC0C1");
            asset.CustomerId = Guid.NewGuid();
            asset.CustomerName = "Projection Customer";
            asset.CurrentCustomerName = "Projection Current Customer";
            asset.MachineNumber = "PROJECTION-SN-001";
            asset.ContractDate = new DateOnly(2026, 1, 1);
            asset.Notes = "projection-notes";
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var projected = await InvokeSelectBillingLinkedAssetRowProjection(db.RentalAssets.AsNoTracking())
                .SingleAsync();

            Assert.Equal(assetId, projected.Id);
            Assert.Equal(profileId, projected.BillingProfileId);
            Assert.Equal("3\uCE35", projected.InstallLocation);
            Assert.Equal("3\uCE35", projected.InstallSiteName);
            Assert.Equal(123_000m, projected.MonthlyFee);
            Assert.Equal("\uCCAD\uAD6C\uB300\uC0C1", projected.BillingEligibilityStatus);
            Assert.Equal("\uC784\uB300\uC911", projected.AssetStatus);
            Assert.Null(projected.CustomerId);
            Assert.Equal(string.Empty, projected.CustomerName);
            Assert.Equal(string.Empty, projected.CurrentCustomerName);
            Assert.Equal(string.Empty, projected.MachineNumber);
            Assert.Null(projected.ContractDate);
            Assert.Equal(string.Empty, projected.Notes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalRentalBillingProfile CreateBillingProfile(int index, string customerName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"BILLING-SEARCH-PROFILE-{index:D4}",
            CustomerName = customerName,
            ItemName = $"\uBCF5\uD569\uAE30 {index:D4}",
            BillingType = "\uC815\uAE30\uCCAD\uAD6C",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            MonthlyAmount = 100_000m + index,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateUnlinkedBillingAsset(Guid assetId, string customerName)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"UNLINKED-BILLING-SEARCH-{assetId:N}",
            ManagementId = $"UBS-{assetId:N}",
            ManagementNumber = "UBS-001",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            ItemName = "\uAC80\uC0C9\uC6A9 \uBBF8\uC5F0\uACB0 \uBCF5\uD569\uAE30",
            MachineNumber = "UBS-SN-001",
            InstallSiteName = "\uBCF8\uC810",
            InstallLocation = "\uBCF8\uC810",
            MonthlyFee = 120_000m,
            AssetStatus = "\uC784\uB300\uC911",
            BillingEligibilityStatus = "\uCCAD\uAD6C\uB300\uC0C1",
            BillingProfileId = null,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };



    private static LocalRentalAsset CreateLinkedBillingAsset(
        Guid assetId,
        Guid profileId,
        string customerName,
        string installLocation,
        decimal monthlyFee,
        string billingEligibilityStatus)
        => new()
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"LINKED-BILLING-SUMMARY-{assetId:N}",
            ManagementId = $"LBS-{assetId:N}",
            ManagementNumber = $"LBS-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            ItemName = "\uC694\uC57D \uAC80\uC99D \uBCF5\uD569\uAE30",
            MachineNumber = $"LBS-SN-{assetId:N}",
            InstallSiteName = installLocation,
            InstallLocation = installLocation,
            MonthlyFee = monthlyFee,
            AssetStatus = "\uC784\uB300\uC911",
            BillingEligibilityStatus = billingEligibilityStatus,
            BillingProfileId = profileId,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };


    private static IQueryable<LocalRentalAsset> InvokeSelectBillingLinkedAssetRowProjection(IQueryable<LocalRentalAsset> query)
    {
        var method = typeof(RentalStateService).GetMethod(
            "SelectBillingLinkedAssetRowProjection",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { query });
        Assert.NotNull(result);
        return Assert.IsAssignableFrom<IQueryable<LocalRentalAsset>>(result);
    }

    private static List<RentalBillingRunModel> InvokeResolveBillingRunsForRowBuild(
        IEnumerable<RentalBillingRunModel> runs,
        DateOnly referenceDate,
        bool includeHistoryRows)
    {
        var method = typeof(RentalStateService).GetMethod(
            "ResolveBillingRunsForRowBuild",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, new object?[] { runs, referenceDate, includeHistoryRows });
        Assert.NotNull(result);
        return Assert.IsType<List<RentalBillingRunModel>>(result);
    }

    private static RentalBillingRunModel CreateBillingRun(Guid runId, DateOnly scheduledDate)
        => new()
        {
            RunId = runId,
            ScheduledDate = scheduledDate,
            PeriodStartDate = new DateOnly(scheduledDate.Year, scheduledDate.Month, 1),
            PeriodEndDate = new DateOnly(scheduledDate.Year, scheduledDate.Month, DateTime.DaysInMonth(scheduledDate.Year, scheduledDate.Month)),
            PeriodLabel = $"{scheduledDate:yyyy-MM}",
            BilledAmount = 100_000m,
            SettledAmount = 0m,
            Status = PaymentFlowConstants.BillingStatusPlanned
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
}
