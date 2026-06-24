using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class UnitsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public UnitsControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task Create_ReturnsConflict_WhenActiveNameAlreadyExistsAfterNormalize()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Units.Add(new Unit
        {
            Id = Guid.NewGuid(),
            Name = "EA",
            IsActive = true,
            IsDeleted = false
        });
        await dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var controller = new UnitsController(dbContext);
        var response = await controller.Create(new UnitDto
        {
            Id = incomingId,
            Name = " ea ",
            IsActive = true
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Contains("unit_name_duplicate", conflict.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Units.IgnoreQueryFilters().AnyAsync(unit => unit.Id == incomingId));
        Assert.Equal(1, await dbContext.Units.IgnoreQueryFilters().CountAsync(unit => !unit.IsDeleted && unit.IsActive));
    }

    [Fact]
    public async Task Update_ReturnsConflict_WhenRenamingToExistingActiveNameAfterNormalize()
    {
        await using var dbContext = CreateDbContext();
        var existingId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        dbContext.Units.AddRange(
            new Unit
            {
                Id = existingId,
                Name = "EA",
                IsActive = true,
                IsDeleted = false
            },
            new Unit
            {
                Id = targetId,
                Name = "BOX",
                IsActive = true,
                IsDeleted = false
            });
        await dbContext.SaveChangesAsync();
        var storedTarget = await dbContext.Units.IgnoreQueryFilters().AsNoTracking().SingleAsync(unit => unit.Id == targetId);

        var controller = new UnitsController(dbContext);
        var response = await controller.Update(targetId, new UnitDto
        {
            Id = targetId,
            Name = " ea ",
            IsActive = true,
            ExpectedRevision = storedTarget.Revision
        }, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Contains("unit_name_duplicate", conflict.Value?.ToString(), StringComparison.Ordinal);
        var unchanged = await dbContext.Units.IgnoreQueryFilters().AsNoTracking().SingleAsync(unit => unit.Id == targetId);
        Assert.Equal("BOX", unchanged.Name);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenNameIsBlank()
    {
        await using var dbContext = CreateDbContext();
        var incomingId = Guid.NewGuid();
        var controller = new UnitsController(dbContext);

        var response = await controller.Create(new UnitDto
        {
            Id = incomingId,
            Name = "   ",
            IsActive = true
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("unit_name_required", badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Units.IgnoreQueryFilters().AnyAsync(unit => unit.Id == incomingId));
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenReferencedByItem()
    {
        await using var dbContext = CreateDbContext();
        var unitId = Guid.NewGuid();
        dbContext.Units.Add(new Unit
        {
            Id = unitId,
            Name = "EA",
            IsActive = true,
            IsDeleted = false
        });
        dbContext.Items.Add(new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "UNIT-REFERENCE-ITEM",
            NameMatchKey = "UNITREFERENCEITEM",
            CategoryName = "기타",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Units.IgnoreQueryFilters().AsNoTracking().SingleAsync(unit => unit.Id == unitId);
        var controller = new UnitsController(dbContext);
        var response = await controller.Delete(unitId, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        Assert.Contains("unit_delete_blocked_by_references", conflict.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Units.IgnoreQueryFilters()
            .Where(unit => unit.Id == unitId)
            .Select(unit => unit.IsDeleted)
            .SingleAsync());
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext()
    {
        var currentUser = new TestCurrentUserContext();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "settings-editor";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission)
            => true;
    }
}
