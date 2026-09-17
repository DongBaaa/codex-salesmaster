using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegritySharedItemDuplicateScopeTests
{
    [Theory]
    [InlineData("ALL", "USENET", false)]
    [InlineData("공용", "USENET", false)]
    [InlineData("shared", "USENET", false)]
    [InlineData("전체", "USENET", false)]
    [InlineData("ALL", "ALL", true)]
    [InlineData("ALL", "공용", true)]
    [InlineData("USENET", "USENET", true)]
    public async Task Scan_OnlyGroupsSameItemScope(string firstOffice, string secondOffice, bool expectedDuplicate)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Items.AddRange(Item(firstOffice), Item(secondOffice));
        await db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(await db.Items.AsNoTracking().OrderBy(i => i.Id).ToListAsync());
        var report = await new DataIntegrityIssueService(db).ScanAsync(Session());
        var duplicates = report.Issues.Where(i => i.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate).ToList();
        Assert.Equal(expectedDuplicate ? 1 : 0, duplicates.Count);
        if (expectedDuplicate) Assert.Equal(2, duplicates[0].RelatedEntityIds.Count);
        Assert.Equal(before, JsonSerializer.Serialize(await db.Items.IgnoreQueryFilters().AsNoTracking().OrderBy(i => i.Id).ToListAsync()));
    }

    [Fact]
    public async Task Review_RejectsStaleCandidateCombiningSharedAndOfficeItems()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var shared = Item("ALL");
        var office = Item("USENET");
        db.Items.AddRange(shared, office);
        await db.SaveChangesAsync();
        var issue = new DataIntegrityIssueDetail
        {
            Code = DataIntegrityIssueCodes.ItemDuplicateCandidate,
            EntityId = shared.Id,
            RelatedEntityIds = [shared.Id, office.Id]
        };
        var review = await new DataIntegrityIssueService(db).PrepareItemDuplicateReviewAsync(issue, Session());
        Assert.Contains(review.PermissionBlockingReasons, reason => reason.Contains("범위", StringComparison.Ordinal));
        Assert.Equal(2, await db.Items.CountAsync());
        Assert.All(await db.Items.ToListAsync(), item => Assert.False(item.IsDirty));
    }

    private static LocalItem Item(string office) => new()
    {
        Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = office,
        NameOriginal = "SCOPE-DUPLICATE-DEVICE", NameMatchKey = "SCOPEDUPLICATEDEVICE",
        SpecificationOriginal = "A4", SpecificationMatchKey = "A4",
        ItemKind = ItemKinds.Asset, TrackingType = ItemTrackingTypes.Asset, IsRental = true,
        IsDirty = false
    };

    private static SessionState Session()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "duplicate-scope-test",
            Role = DomainConstants.RoleAdmin, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        return session;
    }
}
