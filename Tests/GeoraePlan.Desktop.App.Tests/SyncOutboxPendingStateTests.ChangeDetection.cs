using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(true, 16)]
    [InlineData(true, 256)]
    [InlineData(false, 16)]
    [InlineData(false, 256)]
    public async Task PushBoundary_BatchPreservesUnsavedRootsAndChildrenWithOneFullScan(bool automaticDetection, int count)
    {
        PrepareAppRoot($"sync-batch-preservation-{automaticDetection}-{count}");
        try
        {
            var detections = 0;
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .LogTo(_ => detections++, [CoreEventId.DetectChangesStarting], LogLevel.Debug)
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
            var units = Enumerable.Range(0, count).Select(i => new LocalUnit
            {
                Id = Guid.NewGuid(), Name = $"original-{i}", Revision = 9, IsDirty = false
            }).ToList();
            var invoices = Enumerable.Range(0, count / 4).Select(i => new LocalInvoice
            {
                Id = Guid.NewGuid(), Memo = "original", Revision = 17, IsDirty = false,
                Lines = [new LocalInvoiceLine { Id = Guid.NewGuid(), LineAmount = 100m }]
            }).ToList();
            foreach (var invoice in invoices) invoice.Lines.Single().InvoiceId = invoice.Id;
            db.AttachRange(units);
            db.AttachRange(invoices);
            using var sync = CreateSyncService(db, CreateAdminSession());
            var baseline = InvokeCaptureTrackedStateBeforePush(sync);
            foreach (var unit in units) unit.Name += "-edited";
            // Only children are edited: preserving the parent must retain its original body.
            foreach (var invoice in invoices) invoice.Lines.Single().LineAmount = 250m;
            db.ChangeTracker.AutoDetectChangesEnabled = automaticDetection;
            detections = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            InvokeCaptureNonMutationTrackedChangesAtPushBoundary(sync, baseline, includeExistingChanges: false);
            watch.Stop();
            var scanCount = detections;
            Console.WriteLine($"batch_preservation roots={count + invoices.Count} full_scans={scanCount} elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}");
            Assert.Equal(automaticDetection, db.ChangeTracker.AutoDetectChangesEnabled);
            foreach (var unit in units) Assert.Equal(EntityState.Detached, db.Entry(unit).State);
            foreach (var invoice in invoices)
            {
                Assert.Equal(EntityState.Detached, db.Entry(invoice).State);
                Assert.Equal(EntityState.Detached, db.Entry(invoice.Lines.Single()).State);
            }
            InvokeRestoreTrackedMutationsPreservedDuringSync(sync);
            foreach (var unit in units)
            {
                Assert.EndsWith("-edited", unit.Name);
                Assert.Equal(unit.Name[..^7], db.Entry(unit).Property(x => x.Name).OriginalValue);
                Assert.Equal(EntityState.Modified, db.Entry(unit).State);
                Assert.Equal(9, unit.Revision);
            }
            foreach (var invoice in invoices)
            {
                Assert.Equal("original", invoice.Memo);
                Assert.Equal(250m, invoice.Lines.Single().LineAmount);
                Assert.Equal(100m, db.Entry(invoice.Lines.Single()).Property(x => x.LineAmount).OriginalValue);
                Assert.Equal(EntityState.Modified, db.Entry(invoice.Lines.Single()).State);
                Assert.Equal(17, invoice.Revision);
            }
            Assert.Equal(0, await db.Units.CountAsync());
            Assert.Equal(0, await db.Invoices.CountAsync());
            Assert.Equal(1, scanCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PendingUserChanges_DetectsScalarAndChildEditsWithOneFullScan(
        bool automaticDetection, bool editChild)
    {
        PrepareAppRoot($"sync-detection-{automaticDetection}-{editChild}");
        try
        {
            var detections = 0;
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .LogTo(_ => detections++, [CoreEventId.DetectChangesStarting], LogLevel.Debug)
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA query_only=ON");
            var invoice = new LocalInvoice
            {
                Id = Guid.NewGuid(), Memo = "original",
                Lines = [new LocalInvoiceLine { Id = Guid.NewGuid(), LineAmount = 100m }]
            };
            invoice.Lines.Single().InvoiceId = invoice.Id;
            db.Attach(invoice);
            using var sync = CreateSyncService(db, CreateAdminSession());
            var method = typeof(SyncService).GetMethod("HasPendingTrackedUserChanges", BindingFlags.Instance | BindingFlags.NonPublic)!;
            db.ChangeTracker.AutoDetectChangesEnabled = automaticDetection;

            detections = 0;
            Assert.False((bool)method.Invoke(sync, null)!);
            Assert.Equal(1, detections);
            if (editChild)
                invoice.Lines.Single().LineAmount = 200m;
            else
                invoice.Memo = "unsaved edit";

            detections = 0;
            Assert.True((bool)method.Invoke(sync, null)!);
            Assert.Equal(1, detections);
            Assert.Equal(automaticDetection, db.ChangeTracker.AutoDetectChangesEnabled);
            Assert.Equal(editChild ? "original" : "unsaved edit", invoice.Memo);
            Assert.Equal(editChild ? 200m : 100m, invoice.Lines.Single().LineAmount);
            Assert.Equal(0, await db.Invoices.CountAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
