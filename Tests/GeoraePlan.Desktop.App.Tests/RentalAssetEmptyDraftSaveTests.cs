using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetEmptyDraftSaveTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public async Task ExplicitSave_EmptyNewDraft_DoesNotCreateBusinessRows(string blank)
        => await WithViewModelAsync(async (db, vm) =>
        {
            vm.EditCustomerName = blank;
            vm.EditItemName = blank;
            vm.EditNotes = blank;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.True(vm.IsNewAsset);
            Assert.Contains("정보를 입력", vm.StatusMessage);
            Assert.Empty(await db.RentalAssets.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.Customers.IgnoreQueryFilters().ToListAsync());
        });

    [Fact]
    public async Task ExplicitSave_NewWarehouseAssetWithMachineNumber_IsSaved()
        => await WithViewModelAsync(async (db, vm) =>
        {
            vm.EditAssetStatus = "창고";
            vm.EditMachineNumber = "EMPTY-DRAFT-CONTROL";
            await vm.SaveCommand.ExecuteAsync(null);

            var saved = Assert.Single(await db.RentalAssets.ToListAsync());
            Assert.Equal("EMPTY-DRAFT-CONTROL", saved.MachineNumber);
            Assert.Null(saved.CustomerId);
            Assert.True(saved.IsDirty);
        });

    [Fact]
    public async Task ExplicitSave_ExistingAssetWithMissingNames_CanStillBeEdited()
        => await WithViewModelAsync(async (db, vm) =>
        {
            var asset = new LocalRentalAsset
            {
                ManagementId = "EXISTING-EMPTY",
                ManagementNumber = "EXISTING-EMPTY",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                AssetStatus = "창고",
                Revision = 100,
                IsDirty = false
            };
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();
            await vm.LoadAndSelectAssetAsync(asset.Id);
            Assert.False(vm.IsNewAsset);
            vm.EditNotes = "기존 자료 확인 메모";
            await vm.SaveCommand.ExecuteAsync(null);

            var saved = Assert.Single(await db.RentalAssets.ToListAsync());
            Assert.Equal(asset.Id, saved.Id);
            Assert.Equal("기존 자료 확인 메모", saved.Notes);
            Assert.True(saved.IsDirty);
        });

    private static async Task WithViewModelAsync(Func<LocalDbContext, RentalAssetViewModel, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-empty-asset-draft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        RentalAssetViewModel? vm = null;
        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "isolated.db")}")
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "empty-draft-test", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), null!, session);
            await test(db, vm);
        }
        finally
        {
            if (vm is not null) await vm.CancelPendingBackgroundWorkAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
