using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("unit", true)]
    [InlineData("unit", false)]
    [InlineData("child-edit", true)]
    [InlineData("child-edit", false)]
    [InlineData("child-add", true)]
    [InlineData("child-add", false)]
    [InlineData("child-delete", true)]
    [InlineData("child-delete", false)]
    [InlineData("unchanged", true)]
    [InlineData("unchanged", false)]
    public async Task AcceptedRevision_EditAfterPayloadCheckRemainsPendingAndCanBeSaved(string edit, bool automaticDetection)
    {
        PrepareAppRoot($"accepted-late-edit-{edit}-{automaticDetection}");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "late-edit.db")
            }.ToString();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connectionString).Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var now = DateTime.UtcNow.AddMinutes(-1);
            var unit = new LocalUnit
            {
                Id = Guid.NewGuid(), Name = "original unit", Revision = 41, IsDirty = true,
                CreatedAtUtc = now.AddHours(-1), UpdatedAtUtc = now, IsActive = true
            };
            var transfer = new LocalInventoryTransfer
            {
                Id = Guid.NewGuid(), TransferNumber = "late-edit-transfer",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Revision = 41, IsDirty = true, CreatedAtUtc = now.AddHours(-1), UpdatedAtUtc = now,
                Lines = [new LocalInventoryTransferLine
                {
                    Id = Guid.NewGuid(), ItemNameOriginal = "original item", Unit = "EA", Quantity = 1m,
                    ReceiptRemark = "original remark"
                }]
            };
            transfer.Lines.Single().TransferId = transfer.Id;
            var useUnit = edit is "unit" or "unchanged";
            ILocalSyncEntity root = useUnit ? unit : transfer;
            if (useUnit) db.Units.Add(unit); else db.InventoryTransfers.Add(transfer);
            await db.SaveChangesAsync();
            // Push reads persisted snapshots; use the same SQLite DateTime representation.
            if (useUnit)
            {
                db.ChangeTracker.Clear();
                unit = await db.Units.SingleAsync(u => u.Id == unit.Id);
                root = unit;
            }
            var request = new SyncPushRequest { DeviceId = "late-edit-device" };
            if (useUnit) request.Units = [LocalMappings.ToDto(unit)];
            else request.InventoryTransfers = [LocalMappings.ToDto(transfer)];
            InvokeStampOutgoingMutations(request, request.DeviceId, TenantScopeCatalog.UsenetGroup);
            Assert.Equal(EntityState.Unchanged, db.Entry(root).State);
            if (useUnit)
            {
                var persistedUnit = await db.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id);
                Assert.True(persistedUnit.IsDirty);
                Assert.Equal(unit.Revision, persistedUnit.Revision);
                var hashMethod = typeof(SyncService).GetMethod("ComputePreparedMutationPayloadHash", BindingFlags.Static | BindingFlags.NonPublic)!;
                Assert.Equal(hashMethod.Invoke(null, ["Unit", LocalMappings.ToDto(persistedUnit)]),
                    hashMethod.Invoke(null, ["Unit", request.Units.Single()]));
            }
            var session = CreateAdminSession();
            using var sync = CreateSyncService(db, session);
            var beforeCalls = 0;
            sync.BeforeAcceptedRevisionCleanAsyncForTesting = ct =>
            {
                beforeCalls++;
                switch (edit)
                {
                    case "unit": unit.Name = "unsaved later unit"; break;
                    case "child-edit": transfer.Lines.Single().ReceiptRemark = "unsaved later remark"; break;
                    case "child-add":
                        var addedLine = new LocalInventoryTransferLine
                        {
                            Id = Guid.NewGuid(), TransferId = transfer.Id, ItemNameOriginal = "new unsaved item",
                            Unit = "EA", Quantity = 2m, ReceiptRemark = "new unsaved remark"
                        };
                        transfer.Lines.Add(addedLine);
                        db.Add(addedLine);
                        break;
                    case "child-delete": db.Remove(transfer.Lines.Single()); break;
                }
                return Task.CompletedTask;
            };
            db.ChangeTracker.AutoDetectChangesEnabled = automaticDetection;
            var task = InvokeApplyAcceptedRevisionsAsync(sync,
                [new SyncAcceptedRevisionDto
                {
                    EntityName = useUnit ? "Unit" : "InventoryTransfer", EntityId = root.Id,
                    Revision = 42, UpdatedAtUtc = now.AddSeconds(30)
                }], request);
            await task;
            var returnedKeys = (System.Collections.IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
            var lateKeys = returnedKeys.Cast<object>().Count();
            Assert.Equal(1, beforeCalls);
            InvokeRestoreTrackedMutationsPreservedDuringSync(sync);
            Assert.Equal(42, root.Revision);
            if (edit == "unchanged")
            {
                Assert.Equal(0, lateKeys);
                Assert.False(root.IsDirty);
                Assert.Equal(EntityState.Unchanged, db.Entry(root).State);
                return;
            }
            // The upload accepted the old payload, so this newer edit must remain eligible for the next push.
            Assert.True(root.IsDirty);
            Assert.Equal(1, lateKeys);
            if (edit == "unit")
            {
                Assert.Equal(EntityState.Modified, db.Entry(root).State);
                Assert.Equal("unsaved later unit", unit.Name);
                Assert.Equal("original unit", db.Entry(unit).Property(u => u.Name).OriginalValue);
            }
            else
            {
                var expectedState = edit switch
                {
                    "child-add" => EntityState.Added,
                    "child-delete" => EntityState.Deleted,
                    _ => EntityState.Modified
                };
                Assert.Single(db.ChangeTracker.Entries<LocalInventoryTransferLine>(),
                    entry => entry.State == expectedState);
            }
            db.ChangeTracker.AutoDetectChangesEnabled = true;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            if (useUnit)
            {
                var saved = await db.Units.AsNoTracking().SingleAsync(u => u.Id == unit.Id);
                Assert.Equal("unsaved later unit", saved.Name);
                Assert.True(saved.IsDirty);
                Assert.Equal(42, saved.Revision);
            }
            else
            {
                var saved = await db.InventoryTransfers.AsNoTracking().Include(t => t.Lines)
                    .SingleAsync(t => t.Id == transfer.Id);
                Assert.True(saved.IsDirty);
                Assert.Equal(42, saved.Revision);
                if (edit == "child-edit") Assert.Equal("unsaved later remark", Assert.Single(saved.Lines).ReceiptRemark);
                if (edit == "child-add") Assert.Equal(2, saved.Lines.Count);
                if (edit == "child-delete") Assert.Empty(saved.Lines);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
