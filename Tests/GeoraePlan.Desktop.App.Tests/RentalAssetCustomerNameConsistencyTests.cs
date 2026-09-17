using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetCustomerNameConsistencyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public async Task SaveAssetAsync_CustomerNameEdit_KeepsReferenceAndSyncNamesConsistent(bool registered, bool clearName, bool clearReference)
    {
        var previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        try
        {
            var databasePath = Path.Combine(root, "rental-name.db");
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={databasePath}").Options;
            await using var db = new LocalDbContext(options);
            Assert.Equal(databasePath, db.Database.GetDbConnection().DataSource);
            await db.Database.EnsureCreatedAsync();
            var oldCustomer = CreateCustomer("Previous Rental Customer");
            var newCustomer = CreateCustomer("New Rental Customer");
            db.Customers.Add(oldCustomer);
            if (registered) db.Customers.Add(newCustomer);
            var asset = new LocalRentalAsset
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = oldCustomer.Id,
                CustomerName = oldCustomer.NameOriginal,
                CurrentCustomerName = oldCustomer.NameOriginal,
                LastCustomerName = "Historical Rental Customer",
                ManagementId = "NAME-CHANGE-1", ManagementNumber = "NAME-CHANGE-1",
                AssetStatus = "임대진행중", CurrentLocation = "설치",
                ItemName = string.Empty, ItemCategoryName = "Printer",
                MonthlyFee = 33000, Notes = "Preserve this asset note"
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var edited = await db.RentalAssets.AsNoTracking().SingleAsync();
            // The editable name changes while its hidden current-name snapshot and ID still refer to the old customer.
            edited.CustomerName = clearName ? string.Empty : newCustomer.NameOriginal;
            if (clearReference) edited.CustomerId = null;
            // An empty name with a still-valid ID is recovered from that ID.
            var expectedName = clearName
                ? clearReference ? string.Empty : oldCustomer.NameOriginal
                : newCustomer.NameOriginal;
            var expectedCustomerId = clearName && !clearReference
                ? oldCustomer.Id
                : registered ? newCustomer.Id : (Guid?)null;
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "rental-name-test", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var result = await new RentalStateService(db).SaveAssetAsync(edited, session, allowCategoryRecovery: true);
            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var saved = await db.RentalAssets.AsNoTracking().SingleAsync();
            Assert.Equal(expectedName, saved.CustomerName);
            Assert.Equal(expectedName, saved.CurrentCustomerName);
            Assert.Equal("Historical Rental Customer", saved.LastCustomerName);
            Assert.Equal(expectedCustomerId, saved.CustomerId);
            Assert.Equal(33000, saved.MonthlyFee);
            Assert.Equal("Preserve this asset note", saved.Notes);
            Assert.True(saved.IsDirty);
            var payload = LocalMappings.ToDto(saved);
            Assert.Equal(saved.CustomerId, payload.CustomerId);
            Assert.Equal(expectedName, payload.CustomerName);
            Assert.Equal(expectedName, payload.CurrentCustomerName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task SaveAssetAsync_LinkedBillingCustomer_RequiresConsistentAssignment(bool registered, bool changeCustomer, bool switchProfile)
    {
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-rental-linked-name-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "linked-name.db")}").Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var oldCustomer = CreateCustomer("Previous Linked Customer");
        var newCustomer = CreateCustomer("New Linked Customer");
        db.Customers.Add(oldCustomer);
        if (registered) db.Customers.Add(newCustomer);
        var profile = new LocalRentalBillingProfile
        {
            ProfileKey = "LINKED-NAME-PROFILE", CustomerId = oldCustomer.Id,
            CustomerName = oldCustomer.NameOriginal, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, MonthlyAmount = 33000,
            BillingTemplateJson = "[]", Notes = "Preserve billing agreement"
        };
        var asset = new LocalRentalAsset
        {
            AssetKey = "LINKED-NAME-ASSET", ManagementId = "LINKED-NAME", ManagementNumber = "LINKED-NAME",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerId = oldCustomer.Id, CustomerName = oldCustomer.NameOriginal,
            CurrentCustomerName = oldCustomer.NameOriginal, BillingProfileId = profile.Id,
            AssetStatus = "임대진행중", CurrentLocation = "설치", ItemCategoryName = "Printer",
            MonthlyFee = 33000, Notes = "Preserve asset agreement"
        };
        db.RentalBillingProfiles.Add(profile);
        var newProfile = new LocalRentalBillingProfile
        {
            ProfileKey = "NEW-LINKED-NAME-PROFILE", CustomerId = newCustomer.Id,
            CustomerName = newCustomer.NameOriginal, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, MonthlyAmount = 33000
        };
        if (switchProfile) db.RentalBillingProfiles.Add(newProfile);
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var beforeAsset = await db.RentalAssets.AsNoTracking().SingleAsync();
        var beforeProfile = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(p => p.Id == profile.Id);
        var edited = await db.RentalAssets.AsNoTracking().SingleAsync();
        if (changeCustomer) edited.CustomerName = newCustomer.NameOriginal;
        if (switchProfile) edited.BillingProfileId = newProfile.Id;
        if (changeCustomer && !switchProfile) edited.ItemName = "Unpersisted replacement item";
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(), Username = "linked-name-test", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        var result = await new RentalStateService(db).SaveAssetAsync(edited, session, allowCategoryRecovery: true);
        if (!changeCustomer || switchProfile)
        {
            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var saved = await db.RentalAssets.AsNoTracking().SingleAsync();
            Assert.Equal(switchProfile ? newCustomer.Id : oldCustomer.Id, saved.CustomerId);
            Assert.Equal(switchProfile ? newProfile.Id : profile.Id, saved.BillingProfileId);
            Assert.Equal(switchProfile ? newCustomer.NameOriginal : oldCustomer.NameOriginal, saved.CurrentCustomerName);
            Assert.Equal(33000, saved.MonthlyFee);
            return;
        }
        Assert.False(result.Success);
        Assert.Contains("청구", result.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeAsset),
            System.Text.Json.JsonSerializer.Serialize(await db.RentalAssets.AsNoTracking().SingleAsync()));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(beforeProfile),
            System.Text.Json.JsonSerializer.Serialize(await db.RentalBillingProfiles.AsNoTracking().SingleAsync()));
        Assert.Empty(await db.RentalAssetAssignmentHistories.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
    }

    private static LocalCustomer CreateCustomer(string name) => new()
    {
        NameOriginal = name, NameMatchKey = name.Replace(" ", string.Empty).ToUpperInvariant(),
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, TradeType = "매출"
    };
}
