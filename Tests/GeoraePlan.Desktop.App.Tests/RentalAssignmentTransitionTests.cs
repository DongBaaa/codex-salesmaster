using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssignmentTransitionTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public async Task SaveBillingProfile_NewConnectionStartsWhenPreviousConnectionEnds(bool itworld, bool existingBilling, bool replaceCurrent)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var office = itworld ? "ITWORLD" : "USENET";
        var tenant = itworld ? "ITWORLD" : "USENET_GROUP";
        var customer = new LocalCustomer { NameOriginal = "이력 검증 고객", NameMatchKey = "이력검증고객", TradeType = "매출",
            TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office };
        var asset = new LocalRentalAsset { AssetKey = "HISTORY-BOUNDARY", ManagementNumber = "HISTORY-BOUNDARY", ManagementId = "HISTORY-BOUNDARY",
            TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office,
            CustomerId = customer.Id, CustomerName = customer.NameOriginal, CurrentCustomerName = customer.NameOriginal,
            InstallLocation = "5층", InstallSiteName = "5층", InstallDate = new DateOnly(2025, 8, 8),
            ItemName = "이력 검증 장비", AssetStatus = "임대진행중", CurrentLocation = "렌탈", MonthlyFee = 50000 };
        var oldStart = new DateTime(2025, 8, 8, 0, 0, 0, DateTimeKind.Utc);
        var previous = new LocalRentalAssetAssignmentHistory { AssetId = asset.Id, TenantCode = tenant, ResponsibleOfficeCode = office,
            CustomerId = customer.Id, CustomerName = customer.NameOriginal, InstallLocation = "5층", LinkedAtUtc = oldStart,
            BillingProfileId = existingBilling ? Guid.NewGuid() : null, MonthlyFee = 50000, IsCurrent = true };
        var ended = new LocalRentalAssetAssignmentHistory { AssetId = asset.Id, TenantCode = tenant, ResponsibleOfficeCode = office,
            CustomerName = "이전 고객", LinkedAtUtc = oldStart.AddYears(-1), UnlinkedAtUtc = oldStart.AddDays(-1), IsCurrent = false };
        var profile = new LocalRentalBillingProfile { CustomerId = customer.Id, CustomerName = customer.NameOriginal, TenantCode = tenant,
            OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office, MonthlyAmount = 55000,
            BillingDayMode = "지정일 없음", BillingDay = 0,
            BillingTemplateJson = JsonSerializer.Serialize(new[] { new RentalBillingTemplateItemModel {
                DisplayItemName = asset.ItemName, Quantity = 1, UnitPrice = 55000, Amount = 55000, IncludedAssetIds = [asset.Id] } }) };
        db.Customers.Add(customer); db.RentalAssets.Add(asset); db.RentalAssetAssignmentHistories.Add(ended);
        if (replaceCurrent) db.RentalAssetAssignmentHistories.Add(previous);
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var endedBefore = JsonSerializer.Serialize(await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync(x => x.Id == ended.Id));
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "history-boundary", Role = DomainConstants.RoleAdmin,
            TenantCode = tenant, OfficeCode = office, ScopeType = TenantScopeCatalog.ScopeTenantAll });
        var service = new RentalStateService(db);
        var before = DateTime.UtcNow;
        var saved = await service.SaveBillingProfileAsync(profile, session);
        var after = DateTime.UtcNow;
        Assert.True(saved.Success, saved.Message);
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
            Assert.NotNull(closed.UnlinkedAtUtc);
            Assert.InRange(current.LinkedAtUtc, before, after);
            Assert.Equal(closed.UnlinkedAtUtc.Value, current.LinkedAtUtc);
        }
        else
        {
            Assert.Equal(oldStart, current.LinkedAtUtc);
        }
        Assert.Equal(profile.Id, current.BillingProfileId);
        Assert.Equal(55000m, current.MonthlyFee);
        Assert.Equal(endedBefore, JsonSerializer.Serialize(histories.Single(x => x.Id == ended.Id)));
        var currentId = current.Id;
        var repeat = await service.SaveBillingProfileAsync(await db.RentalBillingProfiles.AsNoTracking().SingleAsync(), session);
        Assert.True(repeat.Success, repeat.Message);
        db.ChangeTracker.Clear();
        histories = await db.RentalAssetAssignmentHistories.AsNoTracking().ToListAsync();
        Assert.Equal(replaceCurrent ? 3 : 2, histories.Count);
        Assert.Equal(currentId, Assert.Single(histories, x => x.IsCurrent).Id);
        Assert.Equal(current.LinkedAtUtc, histories.Single(x => x.Id == currentId).LinkedAtUtc);
        Assert.Equal(endedBefore, JsonSerializer.Serialize(histories.Single(x => x.Id == ended.Id)));
        Assert.Empty(await db.Invoices.ToListAsync());
        Assert.Empty(await db.Payments.ToListAsync());
    }
}
