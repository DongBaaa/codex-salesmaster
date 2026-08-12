using System.Reflection;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TransactionAttachmentFileConsistencyTests
{
    [Fact]
    public async Task SyncMetadataSettingWrite_DoesNotPersistOrClearMainContextPendingWork()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-main-tracker-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var pendingOutbox = new LocalSyncOutboxEntry
        {
            MutationId = $"pending-{Guid.NewGuid():N}",
            DeviceId = "test-device",
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BusinessDatabaseName = "USENET",
            Status = "Prepared"
        };
        var pendingSetting = new LocalSetting
        {
            Key = "Unrelated.PendingSetting",
            Value = "must-remain-unsaved"
        };

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            db.SyncOutboxEntries.Add(pendingOutbox);
            db.Settings.Add(pendingSetting);
            using var sync = CreateSyncService(db);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TrySetSettingSafeAsync",
                "Sync.LastSuccessAt",
                "2026-08-05T02:00:07.0000000+09:00",
                CancellationToken.None);

            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingSetting).State);
            await using var verificationDb = new LocalDbContext(options);
            Assert.False(await verificationDb.SyncOutboxEntries
                .AnyAsync(entry => entry.Id == pendingOutbox.Id));
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting => setting.Key == pendingSetting.Key));
            Assert.Equal(
                "2026-08-05T02:00:07.0000000+09:00",
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastSuccessAt")
                    .Select(setting => setting.Value)
                    .SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task SyncMetadataSettingWrite_FailureDoesNotClearMainContextPendingOutbox()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-failure-tracker-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var pendingOutbox = new LocalSyncOutboxEntry
        {
            MutationId = $"pending-failure-{Guid.NewGuid():N}",
            DeviceId = "test-device",
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BusinessDatabaseName = "USENET",
            Status = "Prepared"
        };

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                await setupDb.Database.ExecuteSqlRawAsync(
                    "DROP TABLE \"Settings\";");
            }

            await using var db = new LocalDbContext(options);
            db.SyncOutboxEntries.Add(pendingOutbox);
            using var sync = CreateSyncService(db);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TrySetSettingSafeAsync",
                "Sync.LastError",
                "not-persisted",
                CancellationToken.None);

            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using var verificationDb = new LocalDbContext(options);
            Assert.False(await verificationDb.SyncOutboxEntries
                .AnyAsync(entry => entry.Id == pendingOutbox.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }


    [Fact]
    public async Task SyncMetadataSettingWrite_ConcurrentUpsertsKeepOneLatestRowPerKey()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-concurrent-upsert-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
                local.SetSyncMetadataSettingIndependentAsync(
                    "Sync.LastSuccessAt",
                    $"concurrent-{index:D2}")));
            await local.SetSyncMetadataSettingIndependentAsync(
                "Sync.LastSuccessAt",
                "latest-final-value");
            await local.SetSyncMetadataSettingIndependentAsync(
                "Sync.LastError",
                string.Empty);

            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
            Assert.Equal(
                "latest-final-value",
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastSuccessAt")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastError"));
            Assert.Equal(
                string.Empty,
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastError")
                    .Select(setting => setting.Value)
                    .SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:memory:;Cache=Shared")]
    [InlineData("Data Source=sync-metadata-named-private;Mode=Memory;Cache=Private;Pooling=False")]
    public async Task SyncMetadataSettingWrite_PrivateMemoryUsesOwningConnection(
        string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());
        var unrelatedPending = new LocalSetting
        {
            Key = "Unrelated.PrivateMemoryPending",
            Value = "must-remain-unsaved"
        };
        db.Settings.Add(unrelatedPending);

        await local.SetSyncMetadataSettingIndependentAsync(
            "Sync.LastSuccessAt",
            "private-memory-value");

        Assert.Equal(EntityState.Added, db.Entry(unrelatedPending).State);
        Assert.False(await db.Settings.AsNoTracking().AnyAsync(setting =>
            setting.Key == unrelatedPending.Key));
        Assert.Equal(
            "private-memory-value",
            await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastSuccessAt")
                .Select(setting => setting.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task SyncMetadataSettingWrite_NamedSharedMemoryUsesIndependentContext()
    {
        var databaseName = $"sync-metadata-shared-{Guid.NewGuid():N}";
        var connectionString =
            $"Data Source={databaseName};Mode=Memory;Cache=Shared;Pooling=False";
        await using var anchorConnection = new SqliteConnection(connectionString);
        await anchorConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(anchorConnection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());

        await local.SetSyncMetadataSettingIndependentAsync(
            "Sync.LastSuccessAt",
            "named-shared-memory-value");

        Assert.Equal(
            "named-shared-memory-value",
            await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastSuccessAt")
                .Select(setting => setting.Value)
                .SingleAsync());
    }

    [Fact]
    public async Task SyncMetadataSettingWrite_RefreshesTrackedSameKeyAndPreservesUnrelatedPendingWork()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-tracked-refresh-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var unrelatedSetting = new LocalSetting
        {
            Key = "Unrelated.PendingAfterMetadataRefresh",
            Value = "pending"
        };
        var pendingOutbox = new LocalSyncOutboxEntry
        {
            MutationId = $"tracked-refresh-{Guid.NewGuid():N}",
            DeviceId = "test-device",
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BusinessDatabaseName = "USENET",
            Status = "Prepared"
        };

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "Sync.LastSuccessAt",
                    Value = "stale-tracked-value"
                });
                await setupDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            var trackedMetadata = await db.Settings.SingleAsync(setting =>
                setting.Key == "Sync.LastSuccessAt");
            db.Settings.Add(unrelatedSetting);
            db.SyncOutboxEntries.Add(pendingOutbox);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await local.SetSyncMetadataSettingIndependentAsync(
                "Sync.LastSuccessAt",
                "fresh-metadata-value");

            Assert.Equal("fresh-metadata-value", trackedMetadata.Value);
            Assert.Equal(EntityState.Unchanged, db.Entry(trackedMetadata).State);
            Assert.Equal(
                "fresh-metadata-value",
                await local.GetSettingAsync("Sync.LastSuccessAt"));
            Assert.Equal(EntityState.Added, db.Entry(unrelatedSetting).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using (var beforeSaveDb = new LocalDbContext(options))
            {
                Assert.False(await beforeSaveDb.Settings.AnyAsync(setting =>
                    setting.Key == unrelatedSetting.Key));
                Assert.False(await beforeSaveDb.SyncOutboxEntries.AnyAsync(entry =>
                    entry.Id == pendingOutbox.Id));
            }

            await db.SaveChangesAsync();
            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
            Assert.Equal(
                "fresh-metadata-value",
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastSuccessAt")
                    .Select(setting => setting.Value)
                    .SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task SyncMetadataSettingWrite_NormalizesSameKeyPendingState(
        EntityState pendingState)
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-same-key-{pendingState}-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                if (pendingState is not EntityState.Added)
                {
                    setupDb.Settings.Add(new LocalSetting
                    {
                        Key = "Sync.LastError",
                        Value = "persisted-old-value"
                    });
                    await setupDb.SaveChangesAsync();
                }
            }

            await using var db = new LocalDbContext(options);
            LocalSetting trackedSetting;
            if (pendingState == EntityState.Added)
            {
                trackedSetting = new LocalSetting
                {
                    Key = "Sync.LastError",
                    Value = "pending-added-value"
                };
                db.Settings.Add(trackedSetting);
            }
            else
            {
                trackedSetting = await db.Settings.SingleAsync(setting =>
                    setting.Key == "Sync.LastError");
                if (pendingState == EntityState.Modified)
                    trackedSetting.Value = "pending-modified-value";
                else
                    db.Settings.Remove(trackedSetting);
            }

            Assert.Equal(pendingState, db.Entry(trackedSetting).State);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await local.SetSyncMetadataSettingIndependentAsync(
                "Sync.LastError",
                "authoritative-metadata-value");

            Assert.Equal(EntityState.Unchanged, db.Entry(trackedSetting).State);
            Assert.Equal("authoritative-metadata-value", trackedSetting.Value);
            await db.SaveChangesAsync();
            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastError"));
            Assert.Equal(
                "authoritative-metadata-value",
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastError")
                    .Select(setting => setting.Value)
                    .SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Unchanged)]
    public async Task SyncMetadataSettingDelete_RemovesAndDetachesSameKeyPendingState(
        EntityState pendingState)
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-metadata-delete-{pendingState}-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                if (pendingState is not EntityState.Added)
                {
                    setupDb.Settings.Add(new LocalSetting
                    {
                        Key = "Sync.PendingFullMirrorRefresh",
                        Value = "persisted-old-value"
                    });
                    await setupDb.SaveChangesAsync();
                }
            }

            await using var db = new LocalDbContext(options);
            LocalSetting trackedSetting;
            if (pendingState == EntityState.Added)
            {
                trackedSetting = new LocalSetting
                {
                    Key = "Sync.PendingFullMirrorRefresh",
                    Value = "pending-added-value"
                };
                db.Settings.Add(trackedSetting);
            }
            else
            {
                trackedSetting = await db.Settings.SingleAsync(setting =>
                    setting.Key == "Sync.PendingFullMirrorRefresh");
                if (pendingState == EntityState.Modified)
                    trackedSetting.Value = "pending-modified-value";
                else if (pendingState == EntityState.Deleted)
                    db.Settings.Remove(trackedSetting);
            }

            Assert.Equal(pendingState, db.Entry(trackedSetting).State);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await local.DeleteSyncMetadataSettingIndependentAsync(
                "Sync.PendingFullMirrorRefresh");

            Assert.Equal(EntityState.Detached, db.Entry(trackedSetting).State);
            Assert.Null(await local.GetSettingAsync(
                "Sync.PendingFullMirrorRefresh"));
            await db.SaveChangesAsync();
            await using var verificationDb = new LocalDbContext(options);
            Assert.False(await verificationDb.Settings.AnyAsync(setting =>
                setting.Key == "Sync.PendingFullMirrorRefresh"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task SyncMetadataMutation_PrivateMemoryActiveTransactionFailsClosed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Settings.AddRange(
            new LocalSetting
            {
                Key = "Sync.LastError",
                Value = "before-set"
            },
            new LocalSetting
            {
                Key = "Sync.PendingFullMirrorRefresh",
                Value = "before-delete"
            });
        await db.SaveChangesAsync();
        var setEntry = await db.Settings.SingleAsync(setting =>
            setting.Key == "Sync.LastError");
        var deleteEntry = await db.Settings.SingleAsync(setting =>
            setting.Key == "Sync.PendingFullMirrorRefresh");
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());

        await using var transaction =
            await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            local.SetSyncMetadataSettingIndependentAsync(
                "Sync.LastError",
                "must-not-be-written"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            local.DeleteSyncMetadataSettingIndependentAsync(
                "Sync.PendingFullMirrorRefresh"));

        Assert.Equal(EntityState.Unchanged, db.Entry(setEntry).State);
        Assert.Equal("before-set", setEntry.Value);
        Assert.Equal(EntityState.Unchanged, db.Entry(deleteEntry).State);
        Assert.Equal("before-delete", deleteEntry.Value);
        Assert.Equal(
            "before-set",
            await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastError")
                .Select(setting => setting.Value)
                .SingleAsync());
        Assert.Equal(
            "before-delete",
            await db.Settings.AsNoTracking()
                .Where(setting =>
                    setting.Key == "Sync.PendingFullMirrorRefresh")
                .Select(setting => setting.Value)
                .SingleAsync());

        await transaction.RollbackAsync();
        Assert.Equal("before-set", await local.GetSettingAsync("Sync.LastError"));
        Assert.Equal(
            "before-delete",
            await local.GetSettingAsync("Sync.PendingFullMirrorRefresh"));
        Assert.Equal(EntityState.Unchanged, db.Entry(setEntry).State);
        Assert.Equal(EntityState.Unchanged, db.Entry(deleteEntry).State);
    }

    [Fact]
    public async Task SyncMetadataSettingDelete_NamedSharedMemoryUsesIndependentContext()
    {
        var databaseName = $"sync-metadata-delete-shared-{Guid.NewGuid():N}";
        var connectionString =
            $"Data Source={databaseName};Mode=Memory;Cache=Shared;Pooling=False";
        await using var anchorConnection = new SqliteConnection(connectionString);
        await anchorConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(anchorConnection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Settings.Add(new LocalSetting
        {
            Key = "Sync.PendingFullMirrorRefresh",
            Value = "1"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());

        await local.DeleteSyncMetadataSettingIndependentAsync(
            "Sync.PendingFullMirrorRefresh");

        Assert.False(await db.Settings.AsNoTracking().AnyAsync(setting =>
            setting.Key == "Sync.PendingFullMirrorRefresh"));
    }

    [Fact]
    public async Task MirrorRefreshMark_DoesNotPersistUnrelatedPendingChanges()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"mirror-refresh-mark-isolated-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var unrelatedAdded = new LocalSetting
        {
            Key = "Unrelated.MarkAdded",
            Value = "pending-added"
        };
        var pendingOutbox = new LocalSyncOutboxEntry
        {
            MutationId = $"mark-pending-{Guid.NewGuid():N}",
            DeviceId = "test-device",
            EntityName = nameof(LocalCustomer),
            EntityId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BusinessDatabaseName = "USENET",
            Status = "Prepared"
        };

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "Unrelated.MarkModified",
                    Value = "persisted-old"
                });
                await setupDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            var unrelatedModified = await db.Settings.SingleAsync(setting =>
                setting.Key == "Unrelated.MarkModified");
            unrelatedModified.Value = "pending-modified";
            db.Settings.Add(unrelatedAdded);
            db.SyncOutboxEntries.Add(pendingOutbox);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await local.MarkServerMirrorRefreshRequiredAsync();

            Assert.Equal(EntityState.Added, db.Entry(unrelatedAdded).State);
            Assert.Equal(EntityState.Modified, db.Entry(unrelatedModified).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal(
                "1",
                await verificationDb.Settings
                    .Where(setting =>
                        setting.Key == "Sync.PendingFullMirrorRefresh")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.False(await verificationDb.Settings.AnyAsync(setting =>
                setting.Key == unrelatedAdded.Key));
            Assert.Equal(
                "persisted-old",
                await verificationDb.Settings
                    .Where(setting => setting.Key == unrelatedModified.Key)
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.False(await verificationDb.SyncOutboxEntries.AnyAsync(entry =>
                entry.Id == pendingOutbox.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task SyncConflictSummary_ClearThenAppendDoesNotRestoreTrackedPastSummary()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"sync-conflict-summary-refresh-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "Sync.LastConflictSummary",
                    Value = "past-conflict-summary"
                });
                await setupDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            _ = await db.Settings.SingleAsync(setting =>
                setting.Key == "Sync.LastConflictSummary");
            using var sync = CreateSyncService(db);

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TrySetSettingSafeAsync",
                "Sync.LastConflictSummary",
                string.Empty,
                CancellationToken.None);
            var unrelatedPending = new LocalSetting
            {
                Key = "Unrelated.ConflictAppendPending",
                Value = "must-remain-unsaved"
            };
            var pendingOutbox = new LocalSyncOutboxEntry
            {
                MutationId = $"conflict-append-{Guid.NewGuid():N}",
                DeviceId = "test-device",
                EntityName = nameof(LocalCustomer),
                EntityId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                Status = "Prepared"
            };
            db.Settings.Add(unrelatedPending);
            db.SyncOutboxEntries.Add(pendingOutbox);
            await InvokePrivateInstanceTaskAsync(
                sync,
                "AppendConflictSummaryAsync",
                "new-conflict-summary");

            Assert.Equal(EntityState.Added, db.Entry(unrelatedPending).State);
            Assert.Equal(EntityState.Added, db.Entry(pendingOutbox).State);
            await using (var pendingVerificationDb =
                         new LocalDbContext(options))
            {
                Assert.False(await pendingVerificationDb.Settings.AnyAsync(
                    setting => setting.Key == unrelatedPending.Key));
                Assert.False(await pendingVerificationDb.SyncOutboxEntries
                    .AnyAsync(entry => entry.Id == pendingOutbox.Id));
            }
            db.ChangeTracker.Clear();
            Assert.Equal(
                "new-conflict-summary",
                await db.Settings
                    .Where(setting =>
                        setting.Key == "Sync.LastConflictSummary")
                    .Select(setting => setting.Value)
                    .SingleAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task ApplyServerPurgeTransaction_KeepsAttachmentFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-server-purge-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-server-purge-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(attachmentFile, "server purge attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Server purge attachment customer"));
            db.Transactions.Add(CreateDeletedTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: true));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_transaction_delete
                BEFORE DELETE ON Transactions
                BEGIN
                    SELECT RAISE(ABORT, 'blocked transaction delete');
                END;
                """);

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                local.ApplyServerPurgeRecycleBinEntryAsync(RecycleBinEntityKind.Transaction, transactionId));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.Id == attachmentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullDeletedTransactionAttachment_KeepsExistingFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-sync-pull-deleted-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-delete-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(attachmentFile, "sync delete attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Sync deleted attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: false));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_attachment_update
                BEFORE UPDATE ON TransactionAttachments
                BEGIN
                    SELECT RAISE(ABORT, 'blocked attachment update');
                END;
                """);

            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "ApplyPullAsync",
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 200,
                        TransactionAttachments =
                        {
                            new TransactionAttachmentDto
                            {
                                Id = attachmentId,
                                TransactionId = transactionId,
                                FileName = Path.GetFileName(attachmentFile),
                                UploadedAtUtc = now,
                                CreatedAtUtc = now.AddMinutes(-1),
                                UpdatedAtUtc = now,
                                Revision = 200,
                                IsDeleted = true
                            }
                        }
                    },
                    0L,
                    CancellationToken.None,
                    false));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            var stored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(stored.IsDeleted);
            Assert.Equal(attachmentFile, stored.StoredPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullRenamedTransactionAttachment_KeepsOldFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-sync-pull-renamed-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rename-{Guid.NewGuid():N}-old.txt");
            await File.WriteAllTextAsync(attachmentFile, "sync rename old attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Sync renamed attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: false));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_attachment_rename_update
                BEFORE UPDATE ON TransactionAttachments
                BEGIN
                    SELECT RAISE(ABORT, 'blocked attachment rename update');
                END;
                """);

            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "ApplyPullAsync",
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 201,
                        TransactionAttachments =
                        {
                            new TransactionAttachmentDto
                            {
                                Id = attachmentId,
                                TransactionId = transactionId,
                                FileName = $"renamed-{attachmentId:N}.txt",
                                FileContent = "sync rename new attachment evidence"u8.ToArray(),
                                UploadedAtUtc = now,
                                CreatedAtUtc = now.AddMinutes(-1),
                                UpdatedAtUtc = now,
                                Revision = 201,
                                IsDeleted = false
                            }
                        }
                    },
                    0L,
                    CancellationToken.None,
                    false));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            var stored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(stored.IsDeleted);
            Assert.Equal(attachmentFile, stored.StoredPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullMissingAttachmentContent_PreservesExistingFileAndDatabaseMetadata()
    {
        PrepareAppRoot("georaeplan-sync-pull-missing-attachment-content");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(
                Path.GetTempPath(),
                $"georaeplan-sync-missing-content-{Guid.NewGuid():N}.pdf");
            const string originalContent = "%PDF-existing-valid-evidence";
            await File.WriteAllTextAsync(attachmentFile, originalContent);

            db.Customers.Add(CreateCustomer(customerId, "Missing content attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            var existing = CreateAttachment(
                attachmentId,
                transactionId,
                attachmentFile,
                isDeleted: false);
            existing.FileHash = "ORIGINAL-HASH";
            existing.Revision = 300;
            db.TransactionAttachments.Add(existing);
            await db.SaveChangesAsync();
            var persistedRevision = existing.Revision;
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "ApplyPullAsync",
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 301,
                        TransactionAttachments =
                        {
                            new TransactionAttachmentDto
                            {
                                Id = attachmentId,
                                TransactionId = transactionId,
                                FileName = "replacement.pdf",
                                FileContent = [],
                                FileSize = new FileInfo(attachmentFile).Length,
                                FileHash = "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1",
                                UploadedAtUtc = now,
                                CreatedAtUtc = now.AddMinutes(-1),
                                UpdatedAtUtc = now,
                                Revision = 301,
                                IsDeleted = false
                            }
                        }
                    },
                    0L,
                    CancellationToken.None,
                    false));

            Assert.Contains("내용이 비어", exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalContent, await File.ReadAllTextAsync(attachmentFile));
            db.ChangeTracker.Clear();
            var stored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.Equal(persistedRevision, stored.Revision);
            Assert.Equal("ORIGINAL-HASH", stored.FileHash);
            Assert.Equal(attachmentFile, stored.StoredPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AmbientPullTransaction_RollbackAfterFilePromotion_RestoresFileAndDatabaseRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachmentDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            transactionId.ToString("N"));
        Directory.CreateDirectory(attachmentDirectory);
        var attachmentPath = Path.Combine(
            attachmentDirectory,
            $"{attachmentId:N}_receipt.pdf");
        await File.WriteAllBytesAsync(attachmentPath, "%PDF-old-evidence"u8.ToArray());

        try
        {
            db.Customers.Add(CreateCustomer(customerId, "Ambient attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                FileName = "receipt.pdf",
                StoredFileName = Path.GetFileName(attachmentPath),
                StoredPath = attachmentPath,
                MimeType = "application/pdf",
                FileSize = new FileInfo(attachmentPath).Length,
                FileHash = "old-hash",
                UploadedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Revision = 10,
                IsDeleted = false,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
            var persistedRevision = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .Where(current => current.Id == attachmentId)
                .Select(current => current.Revision)
                .SingleAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db);
            await using var transaction = await db.Database.BeginTransactionAsync();
            using var attachmentFiles = new AttachmentFileJournal(
                Path.Combine(AppPaths.TempDir, "attachment-file-journals"),
                AppPaths.TransactionAttachmentsDir);
            var now = DateTime.UtcNow;
            var pull = new SyncPullResponse
            {
                CurrentServerRevision = 11,
                TransactionAttachments =
                {
                    new TransactionAttachmentDto
                    {
                        Id = attachmentId,
                        TransactionId = transactionId,
                        AttachmentType = "입금확인증",
                        FileName = "receipt.pdf",
                        FileContent = "%PDF-new-evidence"u8.ToArray(),
                        MimeType = "application/pdf",
                        FileSize = "%PDF-new-evidence"u8.Length,
                        FileHash = Convert.ToHexString(
                            SHA256.HashData("%PDF-new-evidence"u8)),
                        UploadedAtUtc = now,
                        CreatedAtUtc = now.AddMinutes(-2),
                        UpdatedAtUtc = now,
                        Revision = 11,
                        IsDeleted = false
                    }
                }
            };

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullInternalAsync",
                pull,
                0L,
                CancellationToken.None,
                false,
                attachmentFiles,
                false,
                true,
                null);
            var pending = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            var promotedPath = pending.StoredPath;
            Assert.NotEqual(attachmentPath, promotedPath);
            attachmentFiles.Promote();
            Assert.Equal(
                "%PDF-new-evidence",
                await File.ReadAllTextAsync(promotedPath));
            Assert.Equal(
                "%PDF-old-evidence",
                await File.ReadAllTextAsync(attachmentPath));

            await transaction.RollbackAsync();
            attachmentFiles.Rollback();
            db.ChangeTracker.Clear();

            Assert.Equal(
                "%PDF-old-evidence",
                await File.ReadAllTextAsync(attachmentPath));
            Assert.False(File.Exists(promotedPath));
            var restored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.Equal(persistedRevision, restored.Revision);
            Assert.Equal("old-hash", restored.FileHash);
            Assert.Equal(attachmentPath, restored.StoredPath);
        }
        finally
        {
            try
            {
                if (Directory.Exists(attachmentDirectory))
                    Directory.Delete(attachmentDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated D-drive test root.
            }
        }
    }

    [Fact]
    public async Task FullMirror_LaterEntityFailure_RollsBackDatabaseAndPreservesExistingAttachment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachmentDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            transactionId.ToString("N"));
        Directory.CreateDirectory(attachmentDirectory);
        var originalAttachmentPath = Path.Combine(
            attachmentDirectory,
            $"{attachmentId:N}_original.pdf");
        const string originalContent = "%PDF-full-mirror-original";
        await File.WriteAllTextAsync(originalAttachmentPath, originalContent);

        try
        {
            db.Customers.Add(CreateCustomer(customerId, "Full mirror rollback customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                FileName = "original.pdf",
                StoredFileName = Path.GetFileName(originalAttachmentPath),
                StoredPath = originalAttachmentPath,
                MimeType = "application/pdf",
                FileSize = new FileInfo(originalAttachmentPath).Length,
                FileHash = "original-hash",
                UploadedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 20,
                IsDirty = false,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
            var persistedAttachmentRevision = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .Where(current => current.Id == attachmentId)
                .Select(current => current.Revision)
                .SingleAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER fail_full_mirror_invoice_insert
                BEFORE INSERT ON Invoices
                BEGIN
                    SELECT RAISE(ABORT, 'simulated later entity failure');
                END;
                """);

            var now = DateTime.UtcNow;
            var replacementContent = "%PDF-full-mirror-replacement"u8.ToArray();
            var invoiceId = Guid.NewGuid();
            var pull = new SyncPullResponse
            {
                CurrentServerRevision = 21,
                Customers =
                {
                    new CustomerDto
                    {
                        Id = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "Full mirror rollback customer",
                        NameMatchKey = "FULLMIRRORROLLBACKCUSTOMER",
                        TradeType = CustomerTradeTypes.Sales,
                        Revision = 21,
                        CreatedAtUtc = now.AddDays(-1),
                        UpdatedAtUtc = now
                    }
                },
                Transactions =
                {
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m,
                        Revision = 21,
                        CreatedAtUtc = now.AddDays(-1),
                        UpdatedAtUtc = now
                    }
                },
                TransactionAttachments =
                {
                    new TransactionAttachmentDto
                    {
                        Id = attachmentId,
                        TransactionId = transactionId,
                        AttachmentType = "입금확인증",
                        FileName = "replacement.pdf",
                        MimeType = "application/pdf",
                        FileSize = replacementContent.LongLength,
                        FileHash = Convert.ToHexString(
                            SHA256.HashData(replacementContent)),
                        FileContent = replacementContent,
                        UploadedAtUtc = now,
                        Revision = 21,
                        CreatedAtUtc = now.AddDays(-1),
                        UpdatedAtUtc = now
                    }
                },
                Invoices =
                {
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = customerId,
                        CustomerName = "Full mirror rollback customer",
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        InvoiceNumber = "FULL-MIRROR-FAIL",
                        VersionGroupId = invoiceId,
                        VersionNumber = 1,
                        IsLatestVersion = true,
                        VoucherType = VoucherType.Sales,
                        InvoiceDate = DateOnly.FromDateTime(now),
                        TotalAmount = 100m,
                        SupplyAmount = 100m,
                        Revision = 21,
                        CreatedAtUtc = now.AddDays(-1),
                        UpdatedAtUtc = now
                    }
                }
            };

            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(pull));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "TryRefreshSharedMirrorCoreAsync",
                    CancellationToken.None,
                    false));

            db.ChangeTracker.Clear();
            Assert.Equal(originalContent, await File.ReadAllTextAsync(originalAttachmentPath));
            var restored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.Equal(originalAttachmentPath, restored.StoredPath);
            Assert.Equal("original-hash", restored.FileHash);
            Assert.Equal(persistedAttachmentRevision, restored.Revision);
            Assert.False(await db.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == invoiceId));
            Assert.Single(
                Directory.EnumerateFiles(
                    attachmentDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            try
            {
                if (Directory.Exists(attachmentDirectory))
                    Directory.Delete(attachmentDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated D-drive app root.
            }
        }
    }

    [Fact]
    public async Task IncrementalAttachmentPull_CommitCompletedThenThrew_ReturnsCommittedSuccess()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"incremental-attachment-commit-ambiguous-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        string? storedPath = null;

        try
        {
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(CreateCustomer(customerId, "Ambiguous pull customer"));
                setupDb.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new CommitOrderInterceptor(
                throwBeforeCommit: false,
                throwAfterCommit: true);
            var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(ambiguousOptions);
            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;
            var content = "%PDF-incremental-commit-ambiguous"u8.ToArray();

            await InvokePrivateInstanceTaskAsync(
                sync,
                "ApplyPullAsync",
                new SyncPullResponse
                {
                    CurrentServerRevision = 30,
                    TransactionAttachments =
                    {
                        new TransactionAttachmentDto
                        {
                            Id = attachmentId,
                            TransactionId = transactionId,
                            AttachmentType = "입금확인증",
                            FileName = "ambiguous.pdf",
                            MimeType = "application/pdf",
                            FileSize = content.LongLength,
                            FileHash = Convert.ToHexString(SHA256.HashData(content)),
                            FileContent = content,
                            UploadedAtUtc = now,
                            Revision = 30,
                            CreatedAtUtc = now.AddMinutes(-1),
                            UpdatedAtUtc = now
                        }
                    }
                },
                0L,
                CancellationToken.None,
                false);

            await using var verificationDb = new LocalDbContext(baseOptions);
            var attachment = await verificationDb.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            storedPath = attachment.StoredPath;
            Assert.True(File.Exists(storedPath));
            Assert.Equal(
                "%PDF-incremental-commit-ambiguous",
                await File.ReadAllTextAsync(storedPath));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(storedPath))
                TryDeleteFile(storedPath);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_CommitFailure_DoesNotPublishRentalStateChange()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-commit-failure-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }

            var interceptor = new CommitOrderInterceptor(throwBeforeCommit: true);
            var failingOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(failingOptions);
            var eventCount = 0;
            var pull = CreateRentalProfilePull(Guid.NewGuid(), revision: 31);
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(pull),
                rental => rental.StateChanged += (_, _) => eventCount++,
                diagnosticsDbFactory:
                    () => new LocalDbContext(baseOptions));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "TryRefreshSharedMirrorCoreAsync",
                    CancellationToken.None,
                    false));

            Assert.Equal(0, eventCount);
            await using var verificationDb = new LocalDbContext(baseOptions);
            Assert.False(await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_CommitCompletedThenThrew_ReturnsCommittedSuccessAndPublishesEvent()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-commit-ambiguous-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachmentDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            transactionId.ToString("N"));
        Directory.CreateDirectory(attachmentDirectory);
        var oldAttachmentPath = Path.Combine(
            attachmentDirectory,
            $"{attachmentId:N}_old.pdf");
        await File.WriteAllTextAsync(oldAttachmentPath, "%PDF-old-before-commit");
        string? committedAttachmentPath = null;

        try
        {
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(CreateCustomer(customerId, "Ambiguous mirror customer"));
                setupDb.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
                setupDb.TransactionAttachments.Add(new LocalTransactionAttachment
                {
                    Id = attachmentId,
                    TransactionId = transactionId,
                    FileName = "old.pdf",
                    StoredFileName = Path.GetFileName(oldAttachmentPath),
                    StoredPath = oldAttachmentPath,
                    MimeType = "application/pdf",
                    FileSize = new FileInfo(oldAttachmentPath).Length,
                    FileHash = "old-hash",
                    UploadedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    Revision = 32,
                    IsDirty = false,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                });
                await setupDb.SaveChangesAsync();
            }

            var interceptor = new CommitOrderInterceptor(
                throwBeforeCommit: false,
                throwAfterCommit: true);
            var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(ambiguousOptions);
            var eventCount = 0;
            var pull = CreateRentalProfilePull(profileId, revision: 33);
            var now = DateTime.UtcNow;
            var replacementContent = "%PDF-new-after-commit"u8.ToArray();
            pull.Customers.Add(new CustomerDto
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Ambiguous mirror customer",
                NameMatchKey = "AMBIGUOUSMIRRORCUSTOMER",
                TradeType = CustomerTradeTypes.Sales,
                Revision = 33,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
            pull.Transactions.Add(new TransactionDto
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                SettlementAmount = 100m,
                Revision = 33,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
            pull.TransactionAttachments.Add(new TransactionAttachmentDto
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "입금확인증",
                FileName = "new.pdf",
                MimeType = "application/pdf",
                FileSize = replacementContent.LongLength,
                FileHash = Convert.ToHexString(SHA256.HashData(replacementContent)),
                FileContent = replacementContent,
                UploadedAtUtc = now,
                Revision = 33,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(pull),
                rental => rental.StateChanged += (_, _) => eventCount++,
                diagnosticsDbFactory:
                    () => new LocalDbContext(baseOptions));

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshSharedMirrorCoreAsync",
                CancellationToken.None,
                false);

            Assert.Equal(1, eventCount);
            Assert.Equal(1, interceptor.CommitCount);
            await using var verificationDb = new LocalDbContext(baseOptions);
            Assert.True(await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == profileId));
            var committedAttachment = await verificationDb.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(attachment => attachment.Id == attachmentId);
            committedAttachmentPath = committedAttachment.StoredPath;
            Assert.True(File.Exists(committedAttachmentPath));
            Assert.Equal(
                "%PDF-new-after-commit",
                await File.ReadAllTextAsync(committedAttachmentPath));
            Assert.False(File.Exists(oldAttachmentPath));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(committedAttachmentPath))
                TryDeleteFile(committedAttachmentPath);
            TryDeleteFile(oldAttachmentPath);
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_Success_PublishesRentalStateChangeAfterCommit()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-commit-success-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
            }
            var interceptor = new CommitOrderInterceptor(throwBeforeCommit: false);
            var observedCommitBeforeEvent = false;
            var eventCount = 0;
            var observedProfileId = Guid.Empty;
            var profileId = Guid.NewGuid();
            var observedCommitCount = 0;
            var trackedOptions = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new LocalDbContext(trackedOptions);
            var pull = CreateRentalProfilePull(profileId, revision: 32);
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(pull),
                rental => rental.StateChanged += (_, args) =>
                {
                    eventCount++;
                    observedCommitBeforeEvent = interceptor.CommitCount > 0;
                    observedCommitCount = interceptor.CommitCount;
                    observedProfileId = Assert.Single(args.BillingProfileIds);
                },
                diagnosticsDbFactory:
                    () => new LocalDbContext(baseOptions));

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshSharedMirrorCoreAsync",
                CancellationToken.None,
                false);

            Assert.Equal(1, eventCount);
            Assert.True(observedCommitBeforeEvent);
            Assert.Equal(1, observedCommitCount);
            Assert.Equal(profileId, observedProfileId);
            await using var verificationDb = new LocalDbContext(baseOptions);
            Assert.True(await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == profileId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task IncrementalPull_PostCommitOwnerSwitch_SuppressesPriorOwnerPostProcessingAndQueuesRefresh()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"incremental-owner-switch-after-commit-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var eventCount = 0;
            var refreshScheduleCount = 0;
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 41)),
                rental => rental.StateChanged += (_, _) => eventCount++,
                session);
            sync.AfterAttachmentCommitAsyncForTesting = _ =>
            {
                session.SetBusinessDatabase(
                    TenantScopeCatalog.Itworld,
                    "ITWORLD");
                return Task.CompletedTask;
            };
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshCurrentBusinessScopeCoreAsync",
                CancellationToken.None,
                false);

            Assert.Equal(0, eventCount);
            Assert.Equal(1, refreshScheduleCount);
            await using var verificationDb = new LocalDbContext(options);
            Assert.True(await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == profileId));
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting => setting.Key == "Sync.LastSuccessAt"));
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting => setting.Key == "Sync.LastError"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_EmptyInvoiceSnapshot_PublishesDataChangesAfterCommitAndRemovesPriorInvoice()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-empty-invoices-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var interceptor = new CommitOrderInterceptor(throwBeforeCommit: false);
        var trackedOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        try
        {
            await using (var setupDb = new LocalDbContext(baseOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(CreateCustomer(customerId, "Full mirror prior invoice customer"));
                setupDb.Invoices.Add(new LocalInvoice
                {
                    Id = invoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 20),
                    VersionGroupId = invoiceId,
                    IsLatestVersion = true,
                    IsConfirmed = true,
                    Revision = 1,
                    IsDirty = false,
                    CreatedAtUtc = now.AddDays(-2),
                    UpdatedAtUtc = now.AddDays(-1)
                });
                setupDb.Settings.AddRange(
                    new LocalSetting
                    {
                        Key = "Sync.LastSuccessAt",
                        Value = "stale-before-full-mirror"
                    },
                    new LocalSetting
                    {
                        Key = "Sync.LastError",
                        Value = "stale-error-before-full-mirror"
                    },
                    new LocalSetting
                    {
                        Key = "Sync.PendingFullMirrorRefresh",
                        Value = "1"
                    });
                await setupDb.SaveChangesAsync();
            }

            var pull = new SyncPullResponse
            {
                CurrentServerRevision = 2,
                Customers =
                {
                    new CustomerDto
                    {
                        Id = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "Full mirror prior invoice customer",
                        NameMatchKey = "FULLMIRRORPRIORINVOICECUSTOMER",
                        Revision = 2,
                        CreatedAtUtc = now.AddDays(-2),
                        UpdatedAtUtc = now
                    }
                }
            };

            await using var db = new LocalDbContext(trackedOptions);
            var invoiceHistoryEventCount = 0;
            var inventoryEventCount = 0;
            var observedCommitBeforeEvent = false;
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(pull),
                configureLocal: local =>
                {
                    local.ItemInvoiceHistoryChanged += (_, _) =>
                    {
                        invoiceHistoryEventCount++;
                        observedCommitBeforeEvent = interceptor.CommitCount > 0;
                    };
                    local.InventoryStateChanged += (_, _) => inventoryEventCount++;
                },
                diagnosticsDbFactory: () => new LocalDbContext(baseOptions));
            var postCommitPendingOutbox = new LocalSyncOutboxEntry
            {
                MutationId = $"post-commit-{Guid.NewGuid():N}",
                DeviceId = "test-device",
                EntityName = nameof(LocalCustomer),
                EntityId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                Status = "Prepared"
            };
            sync.AfterAttachmentCommitAsyncForTesting = _ =>
            {
                db.SyncOutboxEntries.Add(postCommitPendingOutbox);
                return Task.CompletedTask;
            };

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshSharedMirrorCoreAsync",
                CancellationToken.None,
                false);

            Assert.Equal(1, invoiceHistoryEventCount);
            Assert.Equal(1, inventoryEventCount);
            Assert.True(observedCommitBeforeEvent);
            Assert.Equal(
                EntityState.Added,
                db.Entry(postCommitPendingOutbox).State);
            await using var verificationDb = new LocalDbContext(baseOptions);
            Assert.False(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == invoiceId));
            Assert.False(await verificationDb.SyncOutboxEntries
                .AnyAsync(entry => entry.Id == postCommitPendingOutbox.Id));
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
            Assert.False(string.IsNullOrWhiteSpace(
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastSuccessAt")
                    .Select(setting => setting.Value)
                    .SingleAsync()));
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key == "Sync.LastError"));
            Assert.Equal(
                string.Empty,
                await verificationDb.Settings
                    .Where(setting => setting.Key == "Sync.LastError")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.False(await verificationDb.Settings.AnyAsync(setting =>
                setting.Key == "Sync.PendingFullMirrorRefresh"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_PostCommitOwnerSwitch_SuppressesPriorOwnerPostProcessingAndQueuesRefresh()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-owner-switch-after-commit-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var eventCount = 0;
            var refreshScheduleCount = 0;
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 42)),
                rental => rental.StateChanged += (_, _) => eventCount++,
                session);
            sync.AfterAttachmentCommitAsyncForTesting = _ =>
            {
                session.SetBusinessDatabase(
                    TenantScopeCatalog.Itworld,
                    "ITWORLD");
                return Task.CompletedTask;
            };
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshSharedMirrorCoreAsync",
                CancellationToken.None,
                false);

            Assert.Equal(0, eventCount);
            Assert.Equal(1, refreshScheduleCount);
            await using var verificationDb = new LocalDbContext(options);
            Assert.True(await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == profileId));
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting => setting.Key == "Sync.LastSuccessAt"));
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting => setting.Key == "Sync.LastError"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_CheckToEffectOwnerSwitchAttempt_IsSerializedByScopeLease()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-owner-effect-lease-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var originalDatabaseName = session.SelectedBusinessDatabaseName;
            var eventCount = 0;
            var refreshScheduleCount = 0;
            var eventDatabaseName = string.Empty;
            var completionStatusDatabaseName = string.Empty;
            Task? mutationTask = null;
            var mutationStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 43)),
                rental => rental.StateChanged += (_, _) =>
                {
                    eventCount++;
                    eventDatabaseName =
                        session.SelectedBusinessDatabaseName;
                },
                session);
            sync.SyncStatusChanged += status =>
            {
                if (status.Contains(
                        "캐시 재구성 완료",
                        StringComparison.Ordinal))
                {
                    completionStatusDatabaseName =
                        session.SelectedBusinessDatabaseName;
                }
            };
            sync.AfterPostCommitOwnerCheckAsyncForTesting = async _ =>
            {
                mutationTask = Task.Run(() =>
                {
                    mutationStarted.TrySetResult(true);
                    session.SetBusinessDatabase(
                        TenantScopeCatalog.Itworld,
                        "ITWORLD");
                });
                await mutationStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                Assert.NotSame(
                    mutationTask,
                    await Task.WhenAny(
                        mutationTask,
                        Task.Delay(TimeSpan.FromMilliseconds(150))));
            };
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshSharedMirrorCoreAsync",
                CancellationToken.None,
                false);
            Assert.NotNull(mutationTask);
            await mutationTask!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, eventCount);
            Assert.Equal(originalDatabaseName, eventDatabaseName);
            Assert.Equal(
                originalDatabaseName,
                completionStatusDatabaseName);
            Assert.Equal(0, refreshScheduleCount);
            Assert.Equal(
                TenantScopeCatalog.GetDatabaseName(
                    TenantScopeCatalog.Itworld),
                session.SelectedBusinessDatabaseName);
            await using var verificationDb = new LocalDbContext(options);
            Assert.True(await verificationDb.Settings
                .AnyAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task IncrementalPull_CheckToRentalEffectOwnerSwitchAttempt_IsSerializedByScopeLease()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"incremental-owner-effect-lease-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var originalDatabaseName = session.SelectedBusinessDatabaseName;
            var eventCount = 0;
            var eventDatabaseName = string.Empty;
            var completionStatusDatabaseName = string.Empty;
            var hookCount = 0;
            var mutationWasBlocked = false;
            Task? mutationTask = null;
            var mutationStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 45)),
                rental => rental.StateChanged += (_, _) =>
                {
                    eventCount++;
                    eventDatabaseName =
                        session.SelectedBusinessDatabaseName;
                },
                session);
            sync.SyncStatusChanged += status =>
            {
                if (status.Contains(
                        "캐시 재구성 완료",
                        StringComparison.Ordinal))
                {
                    completionStatusDatabaseName =
                        session.SelectedBusinessDatabaseName;
                }
            };
            sync.AfterPostCommitOwnerCheckAsyncForTesting = async _ =>
            {
                if (Interlocked.Increment(ref hookCount) != 1)
                    return;

                mutationTask = Task.Run(() =>
                {
                    mutationStarted.TrySetResult(true);
                    session.SetBusinessDatabase(
                        TenantScopeCatalog.Itworld,
                        "ITWORLD");
                });
                await mutationStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                mutationWasBlocked = !ReferenceEquals(
                    mutationTask,
                    await Task.WhenAny(
                        mutationTask,
                        Task.Delay(TimeSpan.FromMilliseconds(150))));
            };

            await InvokePrivateInstanceTaskAsync(
                sync,
                "TryRefreshCurrentBusinessScopeCoreAsync",
                CancellationToken.None,
                false);
            Assert.NotNull(mutationTask);
            await mutationTask!.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(mutationWasBlocked);
            Assert.Equal(1, eventCount);
            Assert.Equal(originalDatabaseName, eventDatabaseName);
            Assert.True(
                string.IsNullOrEmpty(completionStatusDatabaseName) ||
                string.Equals(
                    originalDatabaseName,
                    completionStatusDatabaseName,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                TenantScopeCatalog.GetDatabaseName(
                    TenantScopeCatalog.Itworld),
                session.SelectedBusinessDatabaseName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task IncrementalPull_SynchronousRentalSubscriberOwnerSwitch_CompletesAndSuppressesLaterSubscribers()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"incremental-owner-effect-reentry-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var originalDatabaseName =
                session.SelectedBusinessDatabaseName;
            var firstSubscriberCount = 0;
            var laterSubscriberCount = 0;
            var refreshScheduleCount = 0;
            var firstSubscriberDatabaseName = string.Empty;
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 46)),
                rental =>
                {
                    rental.StateChanged += (_, _) =>
                    {
                        firstSubscriberCount++;
                        firstSubscriberDatabaseName =
                            session.SelectedBusinessDatabaseName;
                        session.SetBusinessDatabase(
                            TenantScopeCatalog.Itworld,
                            "ITWORLD");
                    };
                    rental.StateChanged += (_, _) =>
                        laterSubscriberCount++;
                },
                session);
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                    sync,
                    "TryRefreshCurrentBusinessScopeCoreAsync",
                    CancellationToken.None,
                    false)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, firstSubscriberCount);
            Assert.Equal(
                originalDatabaseName,
                firstSubscriberDatabaseName);
            Assert.Equal(0, laterSubscriberCount);
            Assert.Equal(1, refreshScheduleCount);
            Assert.Equal(
                TenantScopeCatalog.GetDatabaseName(
                    TenantScopeCatalog.Itworld),
                session.SelectedBusinessDatabaseName);
            await using var verificationDb =
                new LocalDbContext(options);
            Assert.False(await verificationDb.Settings
                .AnyAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task FullMirror_SynchronousCompletionStatusOwnerSwitch_CompletesAndSuppressesLaterSubscribers()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"full-mirror-status-reentry-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
                await setupDb.Database.EnsureCreatedAsync();

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var originalDatabaseName =
                session.SelectedBusinessDatabaseName;
            var firstSubscriberCount = 0;
            var laterSubscriberCount = 0;
            var refreshScheduleCount = 0;
            var firstSubscriberDatabaseName = string.Empty;
            using var sync = CreateSyncService(
                db,
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 47)),
                session: session);
            sync.SyncStatusChanged += status =>
            {
                if (!status.Contains(
                        "중앙 서버 기준 캐시 재구성 완료",
                        StringComparison.Ordinal))
                {
                    return;
                }

                firstSubscriberCount++;
                firstSubscriberDatabaseName =
                    session.SelectedBusinessDatabaseName;
                session.SetBusinessDatabase(
                    TenantScopeCatalog.Itworld,
                    "ITWORLD");
            };
            sync.SyncStatusChanged += status =>
            {
                if (status.Contains(
                        "중앙 서버 기준 캐시 재구성 완료",
                        StringComparison.Ordinal))
                {
                    laterSubscriberCount++;
                }
            };
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                    sync,
                    "TryRefreshSharedMirrorCoreAsync",
                    CancellationToken.None,
                    false)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, firstSubscriberCount);
            Assert.Equal(
                originalDatabaseName,
                firstSubscriberDatabaseName);
            Assert.Equal(0, laterSubscriberCount);
            Assert.Equal(1, refreshScheduleCount);
            Assert.Equal(
                TenantScopeCatalog.GetDatabaseName(
                    TenantScopeCatalog.Itworld),
                session.SelectedBusinessDatabaseName);
            await using var verificationDb =
                new LocalDbContext(options);
            Assert.True(await verificationDb.Settings
                .AnyAsync(setting =>
                    setting.Key == "Sync.LastSuccessAt"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task DownloadFullRefresh_PostCommitOwnerChange_DoesNotClearQueuedRefreshRequirement()
    {
        var databasePath = Path.Combine(
            AppPaths.TempDir,
            $"download-owner-refresh-required-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var profileId = Guid.NewGuid();

        try
        {
            await using (var setupDb = new LocalDbContext(options))
            {
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "Sync.PendingFullMirrorRefresh",
                    Value = "0"
                });
                await setupDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            await local.MarkServerMirrorRefreshRequiredAsync();
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(
                session,
                () => new LocalDbContext(options));
            using var http = new HttpClient(
                new PullResponseHandler(
                    CreateRentalProfilePull(profileId, revision: 44)))
            {
                BaseAddress = new Uri("http://localhost/")
            };
            var api = new ErpApiClient(http, session);
            using var sync = new SyncService(
                db,
                local,
                rental,
                api,
                session,
                dispatcher,
                diagnostics);
            var refreshScheduleCount = 0;
            sync.AfterAttachmentCommitAsyncForTesting = _ =>
            {
                session.SetBusinessDatabase(
                    TenantScopeCatalog.Itworld,
                    "ITWORLD");
                return Task.CompletedTask;
            };
            sync.CurrentOwnerRefreshScheduledForTesting =
                () => refreshScheduleCount++;

            await InvokePrivateInstanceTaskAsync(
                sync,
                "PullNewAsync",
                CancellationToken.None);

            Assert.Equal(1, refreshScheduleCount);
            Assert.True(
                await local.IsServerMirrorRefreshRequiredAsync());
            Assert.True(await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == profileId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(databasePath);
        }
    }

    [Fact]
    public async Task AttachmentFileJournal_StageCopyRollback_PreservesSourceAndRemovesPromotedCopy()
    {
        using var attachmentScope = new TemporaryTestDirectory(
            "georaeplan-stage-copy-rollback");
        var attachmentRoot = attachmentScope.Path;
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        var sourcePath = Path.Combine(
            attachmentRoot,
            "transactions",
            "source-evidence.bin");
        var destinationPath = Path.Combine(
            attachmentRoot,
            "transactions",
            ".inventory-transfer-conflicts",
            "archived-evidence.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var expected = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(sourcePath, expected);

        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        await journal.StageCopyAsync(sourcePath, destinationPath);
        journal.Promote();

        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
        journal.Rollback();

        Assert.True(File.Exists(sourcePath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(sourcePath));
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task AttachmentFileJournal_CommittedConflictArchiveReference_PreservesStagedCopy()
    {
        using var attachmentScope = new TemporaryTestDirectory(
            "georaeplan-conflict-archive-reference");
        var attachmentRoot = attachmentScope.Path;
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        var sourcePath = Path.Combine(
            attachmentRoot,
            "transactions",
            "source-evidence.bin");
        var destinationPath = Path.Combine(
            attachmentRoot,
            "transactions",
            ".inventory-transfer-conflicts",
            "archived-evidence.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        var expected = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(sourcePath, expected);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        var transferId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.InventoryTransferTombstoneConflicts.Add(
            new LocalInventoryTransferTombstoneConflict
            {
                TransferId = transferId,
                BusinessDatabaseName = "USENET",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = DomainConstants.OfficeUsenet,
                TargetOfficeCode = DomainConstants.OfficeYeonsu,
                LocalSnapshotJson = "{}",
                ServerTombstoneJson = "{}",
                OutboxMutationIdsJson = "[]",
                ArchivedReceiveEvidencePath = destinationPath,
                ServerUpdatedAtUtc = now,
                Status = InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                DetectedAtUtc = now,
                UpdatedAtUtc = now
            });
        await journal.StageCopyAsync(sourcePath, destinationPath);
        await db.SaveChangesAsync();
        await journal.StageCommitEvidenceAsync(db);
        journal.Promote();
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await journal.CompleteAfterDatabaseCommitAsync(db);

        Assert.True(File.Exists(destinationPath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(destinationPath));
    }

    [Fact]
    public async Task CommitAttachmentTransactionUnderOwnerLease_AsyncCommitDoesNotCaptureUiContext()
    {
        using var attachmentScope = new TemporaryTestDirectory(
            "georaeplan-commit-context");
        var attachmentRoot = attachmentScope.Path;
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setupDb = new LocalDbContext(baseOptions))
            await setupDb.Database.EnsureCreatedAsync();

        var interceptor = new DelayedCommitInterceptor();
        var delayedOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new LocalDbContext(delayedOptions);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = CreateAdminSession();
        using var sync = CreateSyncService(db, session: session);
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);
        var captureOwner = typeof(SyncService).GetMethod(
            "CaptureSyncOperationOwnerBoundary",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        var commitWithLease = typeof(SyncService).GetMethod(
            "CommitAttachmentTransactionUnderOwnerLeaseAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(captureOwner);
        Assert.NotNull(commitWithLease);
        var expectedOwner = captureOwner!.Invoke(sync, null);
        Assert.NotNull(expectedOwner);
        var commitAttempted = false;
        var trappedContext = new NonPumpingSynchronizationContext();
        var originalContext = SynchronizationContext.Current;
        Task<bool> commitTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(trappedContext);
            commitTask = Assert.IsAssignableFrom<Task<bool>>(
                commitWithLease!.Invoke(
                    sync,
                    [
                        transaction,
                        journal,
                        expectedOwner,
                        (Action)(() => commitAttempted = true),
                        CancellationToken.None,
                        session,
                        null,
                        (Func<CancellationToken, Task<bool>>?)null
                    ]));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        await interceptor.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var mutationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationTask = Task.Run(() =>
        {
            mutationStarted.TrySetResult(true);
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        });
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotSame(
            mutationTask,
            await Task.WhenAny(
                mutationTask,
                Task.Delay(TimeSpan.FromMilliseconds(150))));

        interceptor.ReleaseCommit.TrySetResult(true);
        var completion = await Task.WhenAny(
            commitTask,
            Task.Delay(TimeSpan.FromSeconds(2)));
        if (!ReferenceEquals(completion, commitTask))
        {
            // Drain only for deterministic cleanup before reporting the
            // captured-context regression.
            trappedContext.Drain();
        }

        var committed = await commitTask.WaitAsync(TimeSpan.FromSeconds(5));
        await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(commitTask, completion);
        Assert.True(committed);
        Assert.True(commitAttempted);
        Assert.Equal(
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
            session.SelectedBusinessDatabaseName);
    }

    private static SyncPullResponse CreateRentalProfilePull(
        Guid profileId,
        long revision)
    {
        var now = DateTime.UtcNow;
        return new SyncPullResponse
        {
            CurrentServerRevision = revision,
            RentalBillingProfiles =
            {
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = $"commit-order-{profileId:N}",
                    CustomerName = "Commit order rental customer",
                    InstallSiteName = "Commit order rental customer",
                    ItemName = "Commit order rental item",
                    BillingType = "Individual",
                    BillingAdvanceMode = "Advance",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 100_000m,
                    IsActive = true,
                    CreatedAtUtc = now.AddDays(-1),
                    UpdatedAtUtc = now,
                    Revision = revision
                }
            }
        };
    }

    private static SyncService CreateSyncService(
        LocalDbContext db,
        HttpMessageHandler? handler = null,
        Action<RentalStateService>? configureRental = null,
        SessionState? session = null,
        Func<LocalDbContext>? diagnosticsDbFactory = null,
        Action<LocalStateService>? configureLocal = null)
    {
        session ??= CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        configureLocal?.Invoke(local);
        var rental = new RentalStateService(db, local);
        configureRental?.Invoke(rental);
        if (diagnosticsDbFactory is null)
        {
            var diagnosticsConnectionString =
                db.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(diagnosticsConnectionString))
            {
                throw new InvalidOperationException(
                    "격리 진단 DB 연결 문자열을 확인할 수 없습니다.");
            }

            diagnosticsDbFactory = () => new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(diagnosticsConnectionString)
                    .Options);
        }

        var diagnostics = new SyncDiagnosticsService(
            session,
            diagnosticsDbFactory);
        var http = handler is null
            ? new HttpClient()
            : new HttpClient(handler);
        http.BaseAddress = new Uri("http://localhost/");
        var api = new ErpApiClient(http, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private sealed class PullResponseHandler(SyncPullResponse pull) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/sync/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(pull)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CommitOrderInterceptor(
        bool throwBeforeCommit,
        bool throwAfterCommit = false)
        : DbTransactionInterceptor
    {
        public int CommitCount { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (throwBeforeCommit)
            {
                return ValueTask.FromException<InterceptionResult>(
                    new InvalidOperationException("simulated transaction commit failure"));
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            CommitCount++;
            if (throwAfterCommit)
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "simulated exception after transaction commit"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class DelayedCommitInterceptor : DbTransactionInterceptor
    {
        public TaskCompletionSource<bool> CommitEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseCommit { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            CommitEntered.TrySetResult(true);
            await ReleaseCommit.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback d, object? state)
            => _callbacks.Enqueue((d, state));

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var work))
                work.Callback(work.State);
        }
    }

    private sealed class TemporaryTestDirectory : IDisposable
    {
        public TemporaryTestDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the isolated test root.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        foreach (var candidate in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            try
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // Best-effort cleanup in the isolated D-drive test root.
            }
        }
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName,
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsDeleted = false,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransaction CreateActiveTransaction(Guid transactionId, Guid customerId)
        => CreateTransaction(transactionId, customerId, isDeleted: false);

    private static LocalTransaction CreateDeletedTransaction(Guid transactionId, Guid customerId)
        => CreateTransaction(transactionId, customerId, isDeleted: true);

    private static LocalTransaction CreateTransaction(Guid transactionId, Guid customerId, bool isDeleted)
        => new()
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 18),
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            IsDeleted = isDeleted,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransactionAttachment CreateAttachment(Guid attachmentId, Guid transactionId, string filePath, bool isDeleted)
        => new()
        {
            Id = attachmentId,
            TransactionId = transactionId,
            FileName = Path.GetFileName(filePath),
            StoredFileName = Path.GetFileName(filePath),
            StoredPath = filePath,
            FileSize = new FileInfo(filePath).Length,
            UploadedAtUtc = DateTime.UtcNow,
            IsDeleted = isDeleted,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
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
