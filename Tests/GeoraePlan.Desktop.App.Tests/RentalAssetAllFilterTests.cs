using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetAllFilterTests
{
    [Theory]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("cleared", false)]
    [InlineData("select-all", false)]
    [InlineData("category", false)]
    [InlineData("status", false)]
    [InlineData("combined", false)]
    public async Task QueryFilters_MatchDisplayedAllSemantics_AndKeepOfficeScope(string mode, bool admin)
    {
        var previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-asset-all-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        RentalAssetViewModel? vm = null;
        LocalDbContext? db = null;
        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
            db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.ItemCategoryOptions.AddRange(
                new LocalItemCategoryOption { Id = Guid.NewGuid(), Name = "A", IsActive = true, IsDirty = false },
                new LocalItemCategoryOption { Id = Guid.NewGuid(), Name = "B", IsActive = true, IsDirty = false },
                new LocalItemCategoryOption { Id = Guid.NewGuid(), Name = "Inactive", IsActive = false, IsDirty = false });
            var assets = new[]
            {
                Asset("known-active", "A", RentalAssetStatusNormalizer.Active),
                Asset("known-warehouse", "B", RentalAssetStatusNormalizer.Warehouse),
                Asset("no-category", "", RentalAssetStatusNormalizer.Active),
                Asset("legacy-category", "Legacy", RentalAssetStatusNormalizer.Active),
                Asset("legacy-status", "A", "설치"),
                Asset("inactive-category", "Inactive", RentalAssetStatusNormalizer.Warehouse),
                Asset("other-office", "A", RentalAssetStatusNormalizer.Active, OfficeCodeCatalog.Yeonsu),
                Asset("deleted", "A", RentalAssetStatusNormalizer.Active)
            };
            assets[^1].IsDeleted = true;
            db.RentalAssets.AddRange(assets);
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                Username = "asset-all-filter", Role = admin ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = admin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            vm = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
            await vm.LoadAsync();

            switch (mode)
            {
                case "cleared":
                    vm.ClearItemCategoryFiltersCommand.Execute(null);
                    vm.ClearStatusFiltersCommand.Execute(null);
                    break;
                case "select-all":
                    vm.ClearItemCategoryFiltersCommand.Execute(null);
                    vm.ClearStatusFiltersCommand.Execute(null);
                    vm.SelectAllItemCategoryFiltersCommand.Execute(null);
                    vm.SelectAllStatusFiltersCommand.Execute(null);
                    break;
                case "category":
                case "combined":
                    vm.ClearItemCategoryFiltersCommand.Execute(null);
                    vm.ItemCategoryFilterOptions.Single(option => option.Value == "A").IsSelected = true;
                    break;
            }
            if (mode is "status" or "combined")
            {
                vm.ClearStatusFiltersCommand.Execute(null);
                var status = mode == "status" ? RentalAssetStatusNormalizer.Warehouse : RentalAssetStatusNormalizer.Active;
                vm.StatusFilterOptions.Single(option => option.Value == status).IsSelected = true;
            }
            await vm.ReloadCommand.ExecuteAsync(null);

            string[] expected = mode switch
            {
                "category" => ["known-active", "legacy-status"],
                "status" => ["known-warehouse", "inactive-category"],
                "combined" => ["known-active"],
                _ => ["known-active", "known-warehouse", "no-category", "legacy-category", "legacy-status", "inactive-category"]
            };
            Assert.Equal(expected.OrderBy(x => x), vm.Rows.Select(row => row.Source.ManagementNumber).OrderBy(x => x));
            Assert.Equal(OfficeCodeCatalog.Usenet, Assert.Single(vm.OfficeFilterOptions, option => option.IsSelected).Value);
            Assert.DoesNotContain(vm.Rows, row => row.Source.ManagementNumber is "other-office" or "deleted");
            if (mode is "default" or "cleared" or "select-all")
            {
                Assert.Equal("품목분류 전체", vm.SelectedItemCategoryFilterSummary);
                Assert.Equal("상태 전체", vm.SelectedStatusFilterSummary);
            }
            // Drain scheduled UI queries before this test reads the shared context directly.
            await vm.CancelPendingBackgroundWorkAsync();
            vm = null;
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
            Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(asset => asset.IsDirty));
        }
        finally
        {
            if (vm is not null) await vm.CancelPendingBackgroundWorkAsync();
            if (db is not null) await db.DisposeAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalAsset Asset(string name, string category, string status, string office = OfficeCodeCatalog.Usenet)
        => new()
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = office, ResponsibleOfficeCode = office, ManagementCompanyCode = office,
            ManagementNumber = name, ManagementId = name, AssetKey = name,
            ItemName = name, ItemCategoryName = category, AssetStatus = status,
            Revision = 1, IsDirty = false
        };
}
