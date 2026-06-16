using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalSelectionOptionConcurrencyTests
{
    [Fact]
    public async Task SaveSelectionOptions_RejectStaleExpectedRevision()
    {
        PrepareAppRoot("georaeplan-selection-option-save-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var categoryId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();
            var itemCategoryId = Guid.NewGuid();
            var tradeType = SelectionOptionDefaults.DefaultTradeTypes.First();

            db.CustomerCategories.Add(new LocalCustomerCategory
            {
                Id = categoryId,
                Name = "공공",
                Revision = 200,
                IsDirty = false,
                IsDeleted = false
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = priceGradeId,
                Name = "A등급",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                IsActive = true
            });
            db.TradeTypeOptions.Add(new LocalTradeTypeOption
            {
                Id = tradeType.Id,
                Name = tradeType.Name,
                AllowsSales = tradeType.AllowsSales,
                AllowsPurchase = tradeType.AllowsPurchase,
                SortOrder = tradeType.SortOrder,
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                IsActive = true
            });
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = itemCategoryId,
                Name = "복합기",
                SortOrder = 10,
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                IsActive = true
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);

            var categoryResult = await local.SaveCustomerCategoryAsync(
                new LocalCustomerCategory { Id = categoryId, Name = "공공기관", Revision = 100 },
                expectedRevision: 100);
            var priceGradeResult = await local.SavePriceGradeOptionAsync(
                new LocalPriceGradeOption { Id = priceGradeId, Name = "B등급", PriceSource = SelectionOptionDefaults.PriceSourceSales, Revision = 100 },
                previousName: "A등급",
                expectedRevision: 100);
            var tradeTypeResult = await local.SaveTradeTypeOptionAsync(
                new LocalTradeTypeOption
                {
                    Id = tradeType.Id,
                    Name = tradeType.Name,
                    AllowsSales = tradeType.AllowsSales,
                    AllowsPurchase = tradeType.AllowsPurchase,
                    SortOrder = tradeType.SortOrder,
                    Revision = 100
                },
                previousName: tradeType.Name,
                expectedRevision: 100);
            var itemCategoryResult = await local.SaveItemCategoryOptionAsync(
                new LocalItemCategoryOption { Id = itemCategoryId, Name = "프린터", SortOrder = 20, Revision = 100 },
                previousName: "복합기",
                expectedRevision: 100);

            Assert.All(
                new[] { categoryResult, priceGradeResult, tradeTypeResult, itemCategoryResult },
                result =>
                {
                    Assert.False(result.Success);
                    Assert.True(result.ConcurrencyConflict, result.Message);
                    Assert.Contains("최신", result.Message);
                });

            Assert.Equal("공공", (await db.CustomerCategories.AsNoTracking().SingleAsync(row => row.Id == categoryId)).Name);
            Assert.Equal("A등급", (await db.PriceGradeOptions.AsNoTracking().SingleAsync(row => row.Id == priceGradeId)).Name);
            Assert.Equal(tradeType.Name, (await db.TradeTypeOptions.AsNoTracking().SingleAsync(row => row.Id == tradeType.Id)).Name);
            Assert.Equal("복합기", (await db.ItemCategoryOptions.AsNoTracking().SingleAsync(row => row.Id == itemCategoryId)).Name);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteSelectionOptionsAndCompanyProfile_RejectStaleExpectedRevision()
    {
        PrepareAppRoot("georaeplan-selection-option-delete-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var categoryId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();
            var itemCategoryId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var otherProfileId = Guid.NewGuid();

            db.CustomerCategories.Add(new LocalCustomerCategory
            {
                Id = categoryId,
                Name = "공공",
                Revision = 200,
                IsDirty = false,
                IsDeleted = false
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = priceGradeId,
                Name = "A등급",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                IsActive = true
            });
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = itemCategoryId,
                Name = "복합기",
                SortOrder = 10,
                Revision = 200,
                IsDirty = false,
                IsDeleted = false,
                IsActive = true
            });
            db.CompanyProfiles.AddRange(
                new LocalCompanyProfile
                {
                    Id = profileId,
                    ProfileName = "본사",
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TradeName = "본사",
                    Revision = 200,
                    IsDirty = false,
                    IsDeleted = false,
                    IsActive = true
                },
                new LocalCompanyProfile
                {
                    Id = otherProfileId,
                    ProfileName = "예비",
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TradeName = "예비",
                    Revision = 1,
                    IsDirty = false,
                    IsDeleted = false,
                    IsActive = true
                });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);

            var categoryResult = await local.DeleteCustomerCategoryAsync(categoryId, expectedRevision: 100);
            var priceGradeResult = await local.DeletePriceGradeOptionAsync(priceGradeId, expectedRevision: 100);
            var itemCategoryResult = await local.DeleteItemCategoryOptionAsync(itemCategoryId, expectedRevision: 100);
            var profileResult = await local.DeleteCompanyProfileAsync(profileId, expectedRevision: 100);

            Assert.All(
                new[] { categoryResult, priceGradeResult, itemCategoryResult, profileResult },
                result =>
                {
                    Assert.False(result.Success);
                    Assert.True(result.ConcurrencyConflict, result.Message);
                    Assert.Contains("최신", result.Message);
                });

            Assert.False((await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == categoryId)).IsDeleted);
            Assert.False((await db.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == priceGradeId)).IsDeleted);
            Assert.False((await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemCategoryId)).IsDeleted);
            Assert.False((await db.CompanyProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == profileId)).IsDeleted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalStateService CreateLocalStateService(LocalDbContext db)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
