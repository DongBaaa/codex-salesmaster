using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Security;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RentalAssignmentTransitionTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task Refresh_ReplacedConnectionStartsAtClosureAndKeepsHistoricalPeriod(bool itworld, bool existingBilling, bool replaceCurrent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, new HistoryUser(), new RevisionClock());
        await db.Database.EnsureCreatedAsync();
        var office = itworld ? "ITWORLD" : "USENET";
        var tenant = itworld ? "ITWORLD" : "USENET_GROUP";
        var asset = new RentalAsset { AssetKey = "BOUNDARY", ManagementNumber = "BOUNDARY", ManagementId = "BOUNDARY",
            TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office,
            CustomerId = Guid.NewGuid(), CustomerName = "고객", CurrentCustomerName = "고객", InstallLocation = "5층",
            InstallDate = new DateOnly(2025, 8, 8), BillingProfileId = Guid.NewGuid(), MonthlyFee = 55000 };
        var oldStart = new DateTime(2025, 8, 8, 0, 0, 0, DateTimeKind.Utc);
        var previous = new RentalAssetAssignmentHistory { AssetId = asset.Id, TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office,
            CustomerId = asset.CustomerId, CustomerName = asset.CustomerName, InstallLocation = "5층", LinkedAtUtc = oldStart,
            BillingProfileId = existingBilling ? Guid.NewGuid() : null, MonthlyFee = 50000, IsCurrent = true };
        var ended = new RentalAssetAssignmentHistory { AssetId = asset.Id, TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office,
            CustomerName = "이전", LinkedAtUtc = oldStart.AddYears(-1), UnlinkedAtUtc = oldStart.AddDays(-1), IsCurrent = false };
        db.RentalAssets.Add(asset); db.RentalAssetAssignmentHistories.Add(ended);
        if (replaceCurrent) db.RentalAssetAssignmentHistories.Add(previous);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var baseline = JsonSerializer.Serialize(await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync(x => x.Id == ended.Id));
        var service = new RentalAssignmentHistoryService(db);
        var before = DateTime.UtcNow;
        await service.RefreshAsync();
        var after = DateTime.UtcNow;
        db.ChangeTracker.Clear();
        var histories = await db.RentalAssetAssignmentHistories.AsNoTracking().ToListAsync();
        Assert.Equal(replaceCurrent ? 3 : 2, histories.Count);
        var current = Assert.Single(histories, x => x.IsCurrent);
        if (replaceCurrent)
        {
            var closed = histories.Single(x => x.Id == previous.Id);
            Assert.Equal(oldStart, closed.LinkedAtUtc);
            Assert.Equal(previous.BillingProfileId, closed.BillingProfileId);
            Assert.Equal(50000m, closed.MonthlyFee);
            Assert.InRange(current.LinkedAtUtc, before, after);
            Assert.Equal(closed.UnlinkedAtUtc, current.LinkedAtUtc);
        }
        else
        {
            Assert.Equal(oldStart, current.LinkedAtUtc);
        }
        Assert.Equal(asset.BillingProfileId, current.BillingProfileId);
        Assert.Equal(baseline, JsonSerializer.Serialize(histories.Single(x => x.Id == ended.Id)));
        await service.RefreshAsync(); db.ChangeTracker.Clear();
        histories = await db.RentalAssetAssignmentHistories.AsNoTracking().ToListAsync();
        Assert.Equal(replaceCurrent ? 3 : 2, histories.Count);
        Assert.Equal(current.Id, Assert.Single(histories, x => x.IsCurrent).Id);
        Assert.Equal(current.LinkedAtUtc, histories.Single(x => x.Id == current.Id).LinkedAtUtc);
        Assert.Equal(baseline, JsonSerializer.Serialize(histories.Single(x => x.Id == ended.Id)));
    }

    private sealed class HistoryUser : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string Username => "history-regression";
        public string TenantCode => "USENET_GROUP";
        public string OfficeCode => "USENET";
        public string ScopeType => "ScopeAdmin";
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => true;
    }
}
