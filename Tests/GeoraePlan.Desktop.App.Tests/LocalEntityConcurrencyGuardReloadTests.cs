using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalEntityConcurrencyGuardReloadTests
{
    [Fact]
    public async Task UpsertItem_ReloadsTrackedItemBeforeSaveRevisionCheck()
    {
        PrepareAppRoot("georaeplan-local-entity-reload-item-save");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Tracked save item",
                NameMatchKey = "Tracked save item",
                Unit = "EA",
                Revision = 100,
                IsDirty = false,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
            });
            await db.SaveChangesAsync();

            var trackedItem = await db.Items.SingleAsync(current => current.Id == itemId);
            Assert.Equal(100, trackedItem.Revision);

            await using (var updateDb = new LocalDbContext())
            {
                var storedItem = await updateDb.Items.SingleAsync(current => current.Id == itemId);
                storedItem.Revision = 200;
                storedItem.NameOriginal = "Tracked save item from sync";
                storedItem.NameMatchKey = "Tracked save item from sync";
                storedItem.UpdatedAtUtc = DateTime.UtcNow;
                await updateDb.SaveChangesAsync();
            }

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var candidate = new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Tracked save item edited",
                NameMatchKey = "Tracked save item edited",
                Unit = "EA",
                Revision = 200,
                IsDirty = false,
                IsDeleted = false
            };

            var result = await local.UpsertItemAsync(candidate);

            Assert.Equal(itemId, result.Id);
            var savedItem = await db.Items.AsNoTracking().SingleAsync(current => current.Id == itemId);
            Assert.Equal("Tracked save item edited", savedItem.NameOriginal);
            Assert.Equal(200, savedItem.Revision);
            Assert.True(savedItem.IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteItem_ReloadsTrackedItemBeforeRevisionCheck()
    {
        PrepareAppRoot("georaeplan-local-entity-reload-item");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Tracked revision item",
                NameMatchKey = "Tracked revision item",
                Unit = "EA",
                Revision = 100,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var trackedItem = await db.Items.SingleAsync(current => current.Id == itemId);
            Assert.Equal(100, trackedItem.Revision);

            await using (var updateDb = new LocalDbContext())
            {
                var storedItem = await updateDb.Items.SingleAsync(current => current.Id == itemId);
                storedItem.Revision = 200;
                storedItem.UpdatedAtUtc = DateTime.UtcNow;
                await updateDb.SaveChangesAsync();
            }

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var result = await local.DeleteItemAsync(itemId, session, expectedRevision: 200);

            Assert.True(result.Success, result.Message);
            var deletedItem = await db.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == itemId);
            Assert.True(deletedItem.IsDeleted);
            Assert.True(deletedItem.IsDirty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SessionState CreateAdminSession()
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
        return session;
    }
}
