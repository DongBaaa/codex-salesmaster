using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class RecycleBinAmountPrivacyTests
{
    public static IEnumerable<object[]> RentalUnknownCases()
    {
        foreach (var role in new[] { "none", "purchase", "sales", "both", "admin", "god" })
        foreach (var mask in new[] { 0, 1, 2, 3 })
        foreach (var amount in new[] { 0, 123456 }) yield return [role, mask, amount];
    }

    [Theory, MemberData(nameof(RentalUnknownCases))]
    public async Task RentalEntriesHonorStoredUnknownFlagsAndKeepKnownZero(string role, int mask, int amount)
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        var user = User(Guid.NewGuid(), role is "sales" or "both", role is "purchase" or "both");
        if (role == "admin") user.Role = DomainConstants.RoleAdmin;
        if (role == "god") session.SetSession("e30.eyJnb2QiOnRydWV9.test", user);
        else session.SetOfflineSession(user);
        var hidden = (mask & 1) != 0;
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), CustomerName = "보존 고객",
            ProfileKey = "UNKNOWN", TenantCode = user.TenantCode, OfficeCode = user.OfficeCode,
            ResponsibleOfficeCode = user.OfficeCode, ManagementCompanyCode = user.OfficeCode,
            MonthlyAmount = amount, AmountsHidden = hidden, IsDeleted = true, IsDirty = false };
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), ManagementNumber = "UNKNOWN-ASSET",
            TenantCode = user.TenantCode, OfficeCode = user.OfficeCode, ResponsibleOfficeCode = user.OfficeCode,
            ManagementCompanyCode = user.OfficeCode, MonthlyFee = amount, SalesAmountsHidden = hidden,
            PurchaseAmountsHidden = (mask & 2) != 0, IsDeleted = true, IsDirty = false };
        var log = new LocalRentalBillingLog { Id = Guid.NewGuid(), BillingProfileId = profile.Id,
            TenantCode = user.TenantCode, OfficeCode = user.OfficeCode, ResponsibleOfficeCode = user.OfficeCode,
            BillingYearMonth = "2026-09", BilledAmount = amount, AmountsHidden = hidden,
            Note = "보존 비고", IsDeleted = true, IsDirty = false };
        db.AddRange(profile, asset, log); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var entries = await local.GetRecycleBinEntriesAsync(session);
        Assert.Equal(3, entries.Count);
        var visible = session.HasPermission(AppPermissionNames.AmountViewSales) && !hidden;
        foreach (var entry in entries)
        {
            Assert.Equal(!visible, entry.Detail.Contains("비공개", StringComparison.Ordinal));
            if (visible) Assert.Contains($"{amount:N0}원", entry.Detail);
            else Assert.DoesNotContain($"{amount:N0}원", entry.Detail);
            Assert.Equal(!visible, JsonSerializer.SerializeToElement(entry).GetProperty("Detail").GetString()!.Contains("비공개", StringComparison.Ordinal));
        }
        Assert.Contains("보존 비고", entries.Single(e => e.EntityId == log.Id).Detail);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        Assert.Equal(amount, (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync()).MonthlyAmount);
        Assert.Equal(amount, (await db.RentalAssets.IgnoreQueryFilters().SingleAsync()).MonthlyFee);
        Assert.Equal(amount, (await db.RentalBillingLogs.IgnoreQueryFilters().SingleAsync()).BilledAmount);
        Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        session.RefreshSession("revoked", User(user.UserId, false, false));
        Assert.All(entries, entry => Assert.Contains("비공개", entry.Detail));
        session.RefreshSession("regranted", User(user.UserId, true, true));
        // Cached strings stay invalid until a fresh scoped read after any access change.
        Assert.All(entries, entry => Assert.Contains("비공개", entry.Detail));
        var refreshed = await local.GetRecycleBinEntriesAsync(session);
        Assert.All(refreshed, entry => Assert.Equal(hidden, entry.Detail.Contains("비공개", StringComparison.Ordinal)));
    }
}
