using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryItemFieldPreservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditingMemo_PreservesFieldsNotExposedByInventoryEditor(bool manualSave)
    {
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-item-field-preservation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False").Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var createdAt = new DateTime(2024, 2, 27, 0, 0, 0, DateTimeKind.Utc);
        var item = new LocalItem
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "보존 검증 품목",
            NameMatchKey = "보존검증품목", TrackingType = ItemTrackingTypes.NonStock,
            ItemKind = ItemKinds.Product, Revision = 100, IsDirty = false,
            SerialNumber = "serial-01", MaterialNumber = "material-02",
            InstallLocation = "기존 설치 위치", Notes = "등록일: 2024-02-27",
            RentalStartDate = new DateOnly(2024, 3, 1), RentalEndDate = new DateOnly(2027, 2, 28),
            CreatedAtUtc = createdAt
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "item-field-admin", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var vm = new InventoryViewModel(local, session);
        try
        {
            await vm.LoadAndSelectItemAsync(item.Id);
            vm.EditSimpleMemo = "화면에서 변경한 메모";
            Assert.True(vm.HasPendingChanges);
            if (manualSave) await vm.SaveItemCommand.ExecuteAsync(null);
            else Assert.True(await vm.TryAutoSaveOnCloseAsync(), vm.StatusMessage);

            var stored = await db.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);
            Assert.Equal("화면에서 변경한 메모", stored.SimpleMemo);
            Assert.Equal("등록일: 2024-02-27", stored.Notes);
            Assert.Equal("serial-01", stored.SerialNumber);
            Assert.Equal("material-02", stored.MaterialNumber);
            Assert.Equal("기존 설치 위치", stored.InstallLocation);
            Assert.Equal(new DateOnly(2024, 3, 1), stored.RentalStartDate);
            Assert.Equal(new DateOnly(2027, 2, 28), stored.RentalEndDate);
            Assert.Equal(createdAt, stored.CreatedAtUtc);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, stored.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.OfficeCode);
            Assert.True(stored.IsDirty);
            Assert.False(stored.IsDeleted);

            // Other item writers can intentionally update or clear these fields.
            stored.Notes = string.Empty;
            stored.SerialNumber = string.Empty;
            stored.MaterialNumber = string.Empty;
            stored.InstallLocation = string.Empty;
            stored.RentalStartDate = null;
            stored.RentalEndDate = null;
            await local.UpsertItemAsync(stored, session);
            var explicitlyCleared = await db.Items.AsNoTracking().SingleAsync(x => x.Id == item.Id);
            Assert.Empty(explicitlyCleared.Notes);
            Assert.Empty(explicitlyCleared.SerialNumber);
            Assert.Empty(explicitlyCleared.MaterialNumber);
            Assert.Empty(explicitlyCleared.InstallLocation);
            Assert.Null(explicitlyCleared.RentalStartDate);
            Assert.Null(explicitlyCleared.RentalEndDate);
        }
        finally
        {
            await vm.CancelPendingBackgroundWorkAsync();
        }
    }
}
