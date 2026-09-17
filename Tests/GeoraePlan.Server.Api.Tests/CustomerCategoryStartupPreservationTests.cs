using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class CustomerCategoryStartupPreservationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;

    public CustomerCategoryStartupPreservationTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options,
            new AdminContext(), new RevisionClock());
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Startup_ApiDeletedDefaultRemainsDeletedAcrossRepeatedStarts()
    {
        await StartAsync();
        var id = DefaultCustomerCategories.Government.Id;
        var category = await _db.CustomerCategories.SingleAsync(x => x.Id == id);
        var controller = new CustomerCategoriesController(_db);
        Assert.IsType<NoContentResult>(await controller.Delete(id, category.Revision, CancellationToken.None));
        var before = await SnapshotAsync();
        for (var run = 0; run < 2; run++)
        {
            await StartAsync();
            Assert.Equal(before, await SnapshotAsync());
            Assert.False(await _db.CustomerCategories.AnyAsync(x => x.Id == id));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_DoesNotResurrectDeletedDuplicatesOrMoveTheirHistoricalLinks(bool fixedId)
    {
        await StartAsync();
        var id = fixedId ? DefaultCustomerCategories.Government.Id : Guid.NewGuid();
        var name = fixedId ? "관공서" : "사용자 분류";
        var deleted = await _db.CustomerCategories.SingleOrDefaultAsync(x => x.Id == id);
        if (deleted is null)
        {
            deleted = new CustomerCategory { Id = id, Name = name };
            _db.CustomerCategories.Add(deleted);
        }
        deleted.IsDeleted = true;
        var active = new CustomerCategory { Id = Guid.NewGuid(), Name = name };
        _db.CustomerCategories.Add(active);
        var customer = new Customer { Id = Guid.NewGuid(), NameOriginal = "보존할 과거 거래처",
            NameMatchKey = "HISTORICAL", CategoryId = id, IsDeleted = true };
        var master = new CustomerMaster { Id = Guid.NewGuid(), NameOriginal = "보존할 과거 원장",
            NameMatchKey = "HISTORICALMASTER", CategoryId = id, IsDeleted = true };
        _db.Customers.Add(customer);
        _db.CustomerMasters.Add(master);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var before = await SnapshotAsync();
        await StartAsync();
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(id, (await _db.Customers.IgnoreQueryFilters().SingleAsync(x => x.Id == customer.Id)).CategoryId);
        Assert.Equal(id, (await _db.CustomerMasters.IgnoreQueryFilters().SingleAsync(x => x.Id == master.Id)).CategoryId);
        Assert.True(await _db.CustomerCategories.AnyAsync(x => x.Id == active.Id));
    }

    [Fact]
    public async Task Startup_PreservesEditedDefaultMetadataAndSeedsMissingDefinitions()
    {
        _db.CustomerCategories.Add(new CustomerCategory
        {
            Id = DefaultCustomerCategories.School.Id, Name = "사용자가 수정한 학교", IsSystemDefault = false
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var before = JsonSerializer.Serialize(await _db.CustomerCategories.SingleAsync());
        await StartAsync();
        Assert.Equal(4, await _db.CustomerCategories.CountAsync());
        Assert.Equal(before, JsonSerializer.Serialize(await _db.CustomerCategories.SingleAsync(x => x.Id == DefaultCustomerCategories.School.Id)));
        Assert.All(await _db.CustomerCategories.Where(x => x.Id != DefaultCustomerCategories.School.Id).ToListAsync(), x => Assert.True(x.IsSystemDefault));
    }

    [Fact]
    public async Task Startup_AllDeletedCustomDuplicatesStayDeleted()
    {
        await StartAsync();
        _db.CustomerCategories.AddRange(
            new CustomerCategory { Name = "보관 분류", IsDeleted = true },
            new CustomerCategory { Name = "보관 분류", IsDeleted = true });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var before = await SnapshotAsync();
        await StartAsync();
        Assert.Equal(before, await SnapshotAsync());
        Assert.False(await _db.CustomerCategories.AnyAsync(x => x.Name == "보관 분류"));
    }

    [Fact]
    public async Task Startup_StillConsolidatesActiveDuplicatesAndTheirCustomerLinks()
    {
        await StartAsync();
        var canonical = await _db.CustomerCategories.SingleAsync(x => x.Id == DefaultCustomerCategories.Government.Id);
        var duplicate = new CustomerCategory { Name = canonical.Name };
        _db.CustomerCategories.Add(duplicate);
        var customer = new Customer { NameOriginal = "활성 거래처", NameMatchKey = "ACTIVE",
            CategoryId = duplicate.Id };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await StartAsync();
        Assert.Equal(canonical.Id, (await _db.CustomerCategories.SingleAsync(x => x.Name == "관공서")).Id);
        Assert.True((await _db.CustomerCategories.IgnoreQueryFilters().SingleAsync(x => x.Id == duplicate.Id)).IsDeleted);
        Assert.Equal(canonical.Id, (await _db.Customers.SingleAsync(x => x.Id == customer.Id)).CategoryId);
        var before = await SnapshotAsync();
        await StartAsync();
        Assert.Equal(before, await SnapshotAsync());
    }

    private async Task StartAsync()
    {
        var method = typeof(DbInitializer).GetMethod("EnsureDefaultCustomerCategoriesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)method.Invoke(null, [_db, CancellationToken.None])!;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<string> SnapshotAsync()
        => JsonSerializer.Serialize(await _db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).ToListAsync());

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class AdminContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string Username => "admin";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public bool HasPermission(string permission) => true;
    }
}
