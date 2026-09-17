using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ItemCategorySelectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActiveCategoryWinsOverHistoricalMatch_WithoutReactivatingIt(bool deleted, bool import)
    {
        await using var db = await CreateDbAsync();
        var historical = new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(), Name = "렌탈 료", IsActive = false, IsDeleted = deleted, IsDirty = false
        };
        var active = new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(), Name = "렌탈료", IsActive = true, IsDeleted = false, IsDirty = false
        };
        // Track historical first, as a sync that retains old tombstones can do.
        db.ItemCategoryOptions.AddRange(historical, active);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        if (import)
        {
            Assert.Equal(active.Name, await local.EnsureItemCategoryOptionForImportAsync("렌탈료"));
        }
        else
        {
            var saved = await local.UpsertItemAsync(new LocalItem
            {
                Id = Guid.NewGuid(), NameOriginal = "활성 분류 저장 검증", CategoryName = "렌탈료",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ItemKind = ItemKinds.Billing, TrackingType = ItemTrackingTypes.NonStock
            });
            Assert.Equal(active.Name, saved.CategoryName);
            Assert.Equal(active.Name, (await db.Items.AsNoTracking().SingleAsync()).CategoryName);
        }
        Assert.False(historical.IsActive);
        Assert.Equal(deleted, historical.IsDeleted);
        Assert.False(historical.IsDirty);
        Assert.Equal(EntityState.Unchanged, db.Entry(historical).State);
        Assert.Equal(2, await db.ItemCategoryOptions.IgnoreQueryFilters().CountAsync());
        Assert.True(active.IsActive);
        Assert.False(active.IsDeleted);
        Assert.False(active.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoActiveCategory_StillRejectsOrdinarySave(bool deleted)
    {
        await using var db = await CreateDbAsync();
        db.ItemCategoryOptions.Add(new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(), Name = "렌탈료", IsActive = false, IsDeleted = deleted, IsDirty = false
        });
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        await Assert.ThrowsAsync<InvalidOperationException>(() => local.UpsertItemAsync(new LocalItem
        {
            Id = Guid.NewGuid(), NameOriginal = "차단 검증", CategoryName = "렌탈료",
            TrackingType = ItemTrackingTypes.NonStock
        }));
        Assert.Empty(await db.Items.ToListAsync());
        var stored = await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        Assert.False(stored.IsActive);
        Assert.Equal(deleted, stored.IsDeleted);
        Assert.False(stored.IsDirty);
    }

    private static async Task<LocalDbContext> CreateDbAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-category-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
        var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
