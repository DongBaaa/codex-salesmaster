using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingTemplateCandidateTests
{
    [Theory]
    [InlineData("USENET", false)]
    [InlineData("USENET", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    public async Task ExplicitAssetLink_SuppressesCandidateAcrossOfficeFilter(string officeFilter, bool emptyCustomer)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(emptyCustomer);
        var profile = CreateProfile(asset.Id);
        db.AddRange(asset, profile);
        await db.SaveChangesAsync();

        var rows = await ReadRows(db, CreateSession(true), officeFilter);

        Assert.DoesNotContain(rows, row => row.SelectionId == asset.Id);
        var stored = await db.RentalAssets.AsNoTracking().SingleAsync();
        Assert.Null(stored.BillingProfileId);
        Assert.False(stored.IsDirty);
        Assert.Equal(asset.CustomerId, stored.CustomerId);
        Assert.Equal(asset.CustomerName, stored.CustomerName);
        Assert.Equal(profile.BillingTemplateJson, (await db.RentalBillingProfiles.AsNoTracking().SingleAsync()).BillingTemplateJson);
        Assert.Empty(await db.Invoices.ToListAsync());
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("deleted")]
    [InlineData("unrelated")]
    [InlineData("unreadable-office")]
    [InlineData("unreadable-tenant")]
    public async Task IneligibleTemplate_DoesNotSuppressCandidate(string condition)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(false);
        var profile = CreateProfile(condition == "unrelated" ? Guid.NewGuid() : asset.Id);
        profile.IsActive = condition != "inactive";
        profile.IsDeleted = condition == "deleted";
        if (condition == "unreadable-tenant")
        {
            profile.TenantCode = TenantScopeCatalog.Itworld;
            profile.ManagementCompanyCode = OfficeCodeCatalog.Usenet;
        }
        db.AddRange(asset, profile);
        await db.SaveChangesAsync();

        var rows = await ReadRows(db, CreateSession(!condition.StartsWith("unreadable")), OfficeCodeCatalog.Usenet);

        var row = Assert.Single(rows, row => !row.HasPersistedProfile);
        Assert.Equal(asset.Id, row.SelectionId);
        Assert.Null((await db.RentalAssets.AsNoTracking().SingleAsync()).BillingProfileId);
        Assert.False((await db.RentalAssets.AsNoTracking().SingleAsync()).IsDirty);
    }

    private static Task<IReadOnlyList<RentalBillingViewRow>> ReadRows(LocalDbContext db, SessionState session, string office)
        => new RentalStateService(db).GetBillingRowsAsync(new RentalBillingFilter
        {
            OfficeCode = office, ExpandCustomerSummaryRows = true, IncludeHistoryRows = false,
            ReferenceDate = new DateOnly(2026, 9, 7)
        }, session);

    private static LocalRentalAsset CreateAsset(bool emptyCustomer) => new()
    {
        Id = Guid.NewGuid(), AssetKey = "template-candidate", ManagementId = "candidate-management",
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
        CustomerId = emptyCustomer ? null : Guid.NewGuid(), CustomerName = emptyCustomer ? "" : "Installation customer",
        CurrentCustomerName = emptyCustomer ? "" : "Installation customer", ItemName = "Rental copier",
        MonthlyFee = 100000m, AssetStatus = "임대진행중", BillingEligibilityStatus = "청구대상",
        IsDirty = false, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
    };

    private static LocalRentalBillingProfile CreateProfile(Guid assetId) => new()
    {
        Id = Guid.NewGuid(), ProfileKey = "candidate-billing-profile", CustomerId = Guid.NewGuid(),
        CustomerName = "Billing customer", TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
        ManagementCompanyCode = OfficeCodeCatalog.Yeonsu, ItemName = "Rental charge", BillingType = "묶음",
        MonthlyAmount = 100000m, BillingDay = 25, BillingCycleMonths = 1,
        BillingStartDate = new DateOnly(2026, 9, 1), ContractStartDate = new DateOnly(2026, 9, 1),
        IsActive = true, IsDirty = false, CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        BillingTemplateJson = JsonSerializer.Serialize(new[] { new RentalBillingTemplateItemModel
        {
            DisplayItemName = "Rental charge", BillingLineMode = "묶음", Quantity = 1m,
            UnitPrice = 100000m, Amount = 100000m, IncludedAssetIds = [assetId]
        } })
    };

    private static SessionState CreateSession(bool admin)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "candidate-scope-test",
            Role = admin ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
            ScopeType = admin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            Permissions = []
        });
        return session;
    }

    private static LocalDbContext CreateDb(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
}
