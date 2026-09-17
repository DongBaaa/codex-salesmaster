using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalManagementCompanyStartupPreservationTests
{
    public static IEnumerable<object[]> SavedStates()
    {
        foreach (var code in new[] { "ITWORLD", "USENET", "YEONSU" })
        foreach (var dirty in new[] { false, true })
        foreach (var active in new[] { false, true })
        foreach (var deleted in new[] { false, true })
            yield return new object[] { code, dirty, active, deleted };
    }

    [Theory]
    [MemberData(nameof(SavedStates))]
    public async Task Startup_PreservesSavedCompanyAndPendingMutationAcrossRepeatedRuns(
        string code, bool dirty, bool active, bool deleted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var company = new LocalRentalManagementCompany
        {
            Code = code, Name = code + " 사용자 지정 업체명", IsSystemDefault = true,
            IsDirty = dirty, IsActive = active, IsDeleted = deleted,
            CreatedAtUtc = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 2, 4, 5, 6, DateTimeKind.Utc),
            Revision = 42
        };
        db.RentalManagementCompanies.Add(company);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await SeedAsync(db);
            db.ChangeTracker.Clear();
            var after = await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == company.Id);
            Assert.Equal(before, JsonSerializer.Serialize(after));
            Assert.Equal(3, await db.RentalManagementCompanies.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task Startup_CreatesMissingDefaultsAndFillsOnlyAnEmptyCleanName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
        {
            Code = "USENET", Name = " ", IsSystemDefault = true, IsDirty = false
        });
        await db.SaveChangesAsync();
        await SeedAsync(db);
        db.ChangeTracker.Clear();
        var companies = await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Code).ToListAsync();
        Assert.Equal(new[] { "ITWORLD", "USENET", "YEONSU" }, companies.Select(x => x.Code));
        Assert.All(companies, x => { Assert.Equal(x.Code, x.Name); Assert.True(x.IsSystemDefault); Assert.False(x.IsDirty); });
        var before = JsonSerializer.Serialize(companies);
        await SeedAsync(db);
        db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Code).ToListAsync()));
    }

    [Fact]
    public async Task Startup_DoesNotDeleteAnUnsentLegacyAliasDuringDeduplication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var pending = new LocalRentalManagementCompany { Code = "유즈넷", Name = "미전송 변경", IsDirty = true };
        db.RentalManagementCompanies.AddRange(
            pending,
            new LocalRentalManagementCompany { Code = "USENET", Name = "서버 업체명", IsSystemDefault = true, IsDirty = false });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == pending.Id));
        await SeedAsync(db);
        db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(await db.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == pending.Id)));
        Assert.Equal("서버 업체명", await db.RentalManagementCompanies.IgnoreQueryFilters().Where(x => x.Code == "USENET").Select(x => x.Name).SingleAsync());
    }

    private static LocalDbContext CreateDb(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);

    private static async Task SeedAsync(LocalDbContext db)
    {
        var method = typeof(LocalDbInitializer).GetMethod("SeedRentalDefaultsAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        await (Task)method.Invoke(null, new object[] { db })!;
        await db.SaveChangesAsync();
    }
}
