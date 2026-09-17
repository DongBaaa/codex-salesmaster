using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetDeletionHistoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1970)]
    [InlineData(2025)]
    public async Task DeleteAssetAsync_ClosesCurrentHistoryAtDeletionTime(int? previousClearYear)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var asset = new LocalRentalAsset
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = "DELETE-HISTORY", ManagementNumber = "DELETE-HISTORY", ManagementId = "DELETE-HISTORY",
            CustomerName = "현재 거래처", CurrentCustomerName = "현재 거래처",
            InstallLocation = "본관", InstallSiteName = "본관", ItemName = "회귀 검증 장비",
            AssetStatus = "임대진행중", BillingEligibilityStatus = "미확인",
            LastAssignmentClearedAtUtc = previousClearYear.HasValue
                ? new DateTime(previousClearYear.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            IsDirty = false, IsDeleted = false
        };
        var linkedAtUtc = DateTime.UtcNow.AddDays(-1);
        var current = new LocalRentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(), AssetId = asset.Id, CustomerName = asset.CustomerName,
            TenantCode = asset.TenantCode, ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            LinkedAtUtc = linkedAtUtc, IsCurrent = true, IsDirty = false
        };
        var ended = new LocalRentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(), AssetId = asset.Id, CustomerName = "이전 거래처",
            LinkedAtUtc = linkedAtUtc.AddDays(-20), UnlinkedAtUtc = linkedAtUtc.AddDays(-10),
            IsCurrent = false, IsDirty = false, ChangeReason = "기존 이력"
        };
        var unrelated = new LocalRentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), CustomerName = "다른 자산",
            LinkedAtUtc = linkedAtUtc, IsCurrent = true, IsDirty = false
        };
        db.RentalAssets.Add(asset);
        db.RentalAssetAssignmentHistories.AddRange(current, ended, unrelated);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var baseline = await db.RentalAssetAssignmentHistories.AsNoTracking().ToListAsync();
        var endedBefore = JsonSerializer.Serialize(baseline.Single(h => h.Id == ended.Id));
        var unrelatedBefore = JsonSerializer.Serialize(baseline.Single(h => h.Id == unrelated.Id));
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "delete-history-regression", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });

        var before = DateTime.UtcNow;
        var result = await new RentalStateService(db).DeleteAssetAsync(asset.Id, session, asset.Revision);
        var after = DateTime.UtcNow;
        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        var stored = await db.RentalAssets.IgnoreQueryFilters().SingleAsync();
        Assert.True(stored.IsDeleted);
        Assert.True(stored.IsDirty);
        Assert.Equal(asset.LastAssignmentClearedAtUtc, stored.LastAssignmentClearedAtUtc);
        var histories = await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(3, histories.Count);
        var closed = histories.Single(h => h.Id == current.Id);
        Assert.False(closed.IsCurrent);
        Assert.False(closed.IsDeleted);
        Assert.True(closed.IsDirty);
        Assert.Equal(linkedAtUtc, closed.LinkedAtUtc);
        Assert.Equal("자산 삭제", closed.ChangeReason);
        Assert.NotNull(closed.UnlinkedAtUtc);
        Assert.InRange(closed.UnlinkedAtUtc.Value, before, after);
        Assert.Equal(endedBefore, JsonSerializer.Serialize(histories.Single(h => h.Id == ended.Id)));
        Assert.Equal(unrelatedBefore, JsonSerializer.Serialize(histories.Single(h => h.Id == unrelated.Id)));
    }
}
