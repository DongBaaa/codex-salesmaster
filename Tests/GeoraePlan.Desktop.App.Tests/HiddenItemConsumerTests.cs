using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class DataIntegrityDuplicateAmountPrivacyTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task HiddenItemPricesStayUnknownThroughScanReviewAndMergeGuard(bool purchase, bool sales, bool storedZero)
    {
        await using var f = await Fixture.CreateAsync(false);
        f.Session.SetSession("admin-fixture", new UserSessionDto { UserId = f.UserId, Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        foreach (var item in await f.Db.Items.ToListAsync())
        {
            item.PurchaseAmountsHidden = purchase; item.SalesAmountsHidden = sales;
            if (storedZero)
            {
                item.PurchasePrice = item.SalePrice = item.RetailPrice = 0;
                item.PriceGradeA = item.PriceGradeB = item.PriceGradeC = 0;
            }
        }
        await f.Db.SaveChangesAsync();
        var issue = await f.ScanAsync();
        var review = await f.Service.PrepareItemDuplicateReviewAsync(issue, f.Session);
        foreach (var comparison in new[] { issue.ItemDuplicateComparison!, review.Comparison })
        {
            Assert.False(comparison.CanMerge);
            Assert.Contains("비공개", string.Join(" ", comparison.BlockingReasons));
            foreach (var row in comparison.Candidates)
            {
                Assert.Equal(purchase ? (decimal?)null : storedZero ? 0m : 123456m, row.PurchasePrice);
                Assert.Equal(sales ? (decimal?)null : storedZero ? 0m : 234567m, row.SalePrice);
                Assert.Equal(sales ? (decimal?)null : storedZero ? 0m : 678901m, row.PriceGradeC);
                Assert.Contains("보존 비고", row.AssetDataSummary);
                Assert.Equal(12m, row.BoxQuantity);
                var json = JsonSerializer.Serialize(row);
                if (purchase) Assert.DoesNotContain("123456", json);
                if (sales) foreach (var amount in new[] { "234567", "345678", "456789", "567890", "678901" }) Assert.DoesNotContain(amount, json);
            }
        }
        f.Service.TestOnlyPreviewItemDuplicateMergeAsync = (_, _) => throw new Xunit.Sdk.XunitException("Unknown prices must stop before server preview.");
        f.Service.TestOnlyExecuteItemDuplicateMergeAsync = (_, _) => throw new Xunit.Sdk.XunitException("Unknown prices must stop before server writes.");
        f.Service.TestOnlyRefreshCurrentBusinessScopeAsync = _ => Task.FromResult(true);
        f.Service.TestOnlyCountDirtyAsync = (_, _) => Task.FromResult(0);
        var result = await f.Service.MergeDuplicateItemIssueAsync(issue, f.SecondId, review.Comparison.SnapshotToken, f.Session);
        Assert.False(result.Success);
        Assert.Contains("비공개", result.Message);
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
        Assert.Equal(2, await f.Db.Items.CountAsync());
        Assert.False(await f.Db.Items.AnyAsync(x => x.IsDirty));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HiddenItemAvailabilityChangesSnapshotEvenWhenAllStoredPricesAreZero(bool purchase, bool sales)
    {
        await using var f = await Fixture.CreateAsync(false);
        foreach (var item in await f.Db.Items.ToListAsync())
            item.PurchasePrice = item.SalePrice = item.RetailPrice = item.PriceGradeA = item.PriceGradeB = item.PriceGradeC = 0;
        await f.Db.SaveChangesAsync();
        var before = (await f.ScanAsync()).ItemDuplicateComparison!;
        Assert.True(before.CanMerge);
        var changed = await f.Db.Items.SingleAsync(x => x.Id == f.SecondId);
        changed.PurchaseAmountsHidden = purchase; changed.SalesAmountsHidden = sales;
        await f.Db.SaveChangesAsync();
        var hidden = (await f.ScanAsync()).ItemDuplicateComparison!;
        Assert.NotEqual(before.SnapshotToken, hidden.SnapshotToken);
        Assert.False(hidden.CanMerge);
        changed.PurchaseAmountsHidden = changed.SalesAmountsHidden = false;
        await f.Db.SaveChangesAsync();
        var restored = (await f.ScanAsync()).ItemDuplicateComparison!;
        Assert.Equal(before.SnapshotToken, restored.SnapshotToken);
        Assert.True(restored.CanMerge);
    }
}

public sealed partial class RecycleBinAmountPrivacyTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task HiddenItemListAndRestorePreserveNullMoneyAndNonFinancialFields(bool purchase, bool sales, bool storedZero)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState(); session.SetSession("fixture", User(Guid.NewGuid(), true, true));
        var item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "숨긴 품목 복원", NameMatchKey = "HIDDEN-ITEM",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            TrackingType = ItemTrackingTypes.NonStock, Unit = "EA", Notes = "원래 비고",
            PurchasePrice = storedZero ? 0 : 654321, SalePrice = storedZero ? 0 : 123456,
            PurchaseAmountsHidden = purchase, SalesAmountsHidden = sales, Revision = 11, IsDirty = false, IsDeleted = true };
        var untouched = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "다른 품목", OfficeCode = OfficeCodeCatalog.Usenet, IsDirty = false, SalePrice = 777 };
        db.AddRange(item, untouched); await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var entry = Assert.Single(await local.GetRecycleBinEntriesAsync(session));
        Assert.Contains("원래 비고", entry.Detail);
        Assert.Equal(sales, entry.Detail.Contains("비공개", StringComparison.Ordinal));
        if (sales) Assert.DoesNotContain("123,456", JsonSerializer.Serialize(entry));
        Assert.False(db.ChangeTracker.HasChanges());
        var result = await local.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.Item, item.Id, session);
        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        var restored = await db.Items.SingleAsync(x => x.Id == item.Id);
        Assert.False(restored.IsDeleted); Assert.True(restored.IsDirty);
        Assert.Equal(11, restored.Revision); Assert.Equal("원래 비고", restored.Notes);
        Assert.Equal(purchase, restored.PurchaseAmountsHidden); Assert.Equal(sales, restored.SalesAmountsHidden);
        var dto = LocalMappings.ToDto(restored);
        Assert.Equal(purchase ? (decimal?)null : storedZero ? 0m : 654321m, dto.PurchasePrice);
        Assert.Equal(sales ? (decimal?)null : storedZero ? 0m : 123456m, dto.SalePrice);
        Assert.False((await db.Items.SingleAsync(x => x.Id == untouched.Id)).IsDirty);
        Assert.Equal(777m, (await db.Items.SingleAsync(x => x.Id == untouched.Id)).SalePrice);
    }
}
