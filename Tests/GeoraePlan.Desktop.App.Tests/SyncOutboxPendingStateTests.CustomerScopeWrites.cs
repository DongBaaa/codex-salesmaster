using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("save", false)]
    [InlineData("save", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    public async Task CustomerScopeSnapshot_RejectsStaleEditorWritesAndPreservesPendingData(string operation, bool dirty)
    {
        PrepareAppRoot("customer-scope-stale-editor");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var id = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(id, 5, dirty));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            // The editor obtained this model before the server revoked access.
            var editor = await local.GetCustomerAsync(id, session);
            Assert.NotNull(editor);
            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
            {
                CurrentServerRevision = 6,
                CustomerScopeSnapshot = CustomerScopeSnapshotFor(session)
            }, 5);
            Assert.Null(await local.GetCustomerAsync(id, session));
            editor.Notes = "stale editor submitted after revocation";
            var result = operation == "save"
                ? await local.UpsertCustomerAsync(editor, session)
                : await local.DeleteCustomerAsync(id, session, expectedRevision: 5);
            Assert.False(result.Success);
            await using var verify = new LocalDbContext();
            var stored = await verify.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.False(stored.IsDeleted);
            Assert.Equal(dirty, stored.IsDirty);
            Assert.Equal(5, stored.Revision);
            Assert.Equal("preserve my edit", stored.Notes);
            Assert.Empty(await verify.SyncOutboxEntries.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerScopeSnapshot_EditorCloseKeepsUnsavedTextWhenScopeWasRevoked()
    {
        PrepareAppRoot("customer-scope-editor-close");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var id = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(id, 5, false));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new CustomerEditViewModel(local, session);
            await vm.LoadAsync(await local.GetCustomerAsync(id, session));
            vm.Notes = "unsaved text must remain available";
            Assert.True(vm.HasPendingChanges);
            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
            {
                CurrentServerRevision = 6,
                CustomerScopeSnapshot = CustomerScopeSnapshotFor(session)
            }, 5);
            Assert.False(await vm.TryAutoSaveOnCloseAsync());
            Assert.True(vm.HasPendingChanges);
            Assert.Equal("unsaved text must remain available", vm.Notes);
            Assert.Contains("저장할 수 없습니다", vm.StatusMessage);
            var stored = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal("preserve my edit", stored.Notes);
            Assert.False(stored.IsDirty);
            Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
