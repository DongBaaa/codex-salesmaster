using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerCategoryReadOnlyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CategoryLookup_PreservesSyncedDefaultsDuplicatesAndCustomerLinks(bool dirty)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saves = new SaveCounter();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).AddInterceptors(saves).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var stamp = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        db.CustomerCategories.AddRange(
            new LocalCustomerCategory { Id = firstId, Name = "관공서", IsSystemDefault = true,
                IsDirty = dirty, Revision = 7, CreatedAtUtc = stamp, UpdatedAtUtc = stamp },
            new LocalCustomerCategory { Id = secondId, Name = " 관공서 ", IsSystemDefault = true,
                IsDirty = dirty, Revision = 8, CreatedAtUtc = stamp.AddDays(1), UpdatedAtUtc = stamp },
            new LocalCustomerCategory { Id = Guid.NewGuid(), Name = "삭제 분류", IsDeleted = true,
                IsDirty = dirty, Revision = 9, CreatedAtUtc = stamp, UpdatedAtUtc = stamp });
        db.Customers.Add(new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "분류 연결 거래처",
            CategoryId = secondId, IsDirty = dirty, Revision = 11, CreatedAtUtc = stamp, UpdatedAtUtc = stamp });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CaptureAsync(db);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        var saveCount = saves.Count;

        var rows = await local.GetCategoriesAsync();
        await local.GetCategoriesAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Id == firstId);
        Assert.Contains(rows, row => row.Id == secondId);
        Assert.Equal(before, await CaptureAsync(db));
        Assert.Equal(saveCount, saves.Count);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task CategoryLookup_DoesNotCommitAnotherPendingEdit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saves = new SaveCounter();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).AddInterceptors(saves).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "저장 전 이름" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        customer.NameOriginal = "아직 저장하지 않은 편집";
        var saveCount = saves.Count;
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());

        Assert.Empty(await local.GetCategoriesAsync());

        Assert.Equal(saveCount, saves.Count);
        Assert.True(db.ChangeTracker.HasChanges());
        await using var reader = new LocalDbContext(options);
        Assert.Equal("저장 전 이름", (await reader.Customers.AsNoTracking().SingleAsync()).NameOriginal);
    }

    private static async Task<string> CaptureAsync(LocalDbContext db)
        => JsonSerializer.Serialize(new
        {
            Categories = await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Name, row.IsSystemDefault, row.IsDeleted, row.IsDirty,
                    row.Revision, row.CreatedAtUtc, row.UpdatedAtUtc }).ToListAsync(),
            Customers = await db.Customers.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.CategoryId, row.IsDirty, row.Revision, row.UpdatedAtUtc }).ToListAsync()
        });

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }
}
