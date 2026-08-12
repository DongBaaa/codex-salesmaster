using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetSaveDeleteConcurrencyTests
{
    [Fact]
    public void AdministrativeCacheRefresh_PrecedesAssetReadLock()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "RentalStateService.cs");
        var source = File.ReadAllText(sourcePath);

        AssertRefreshPrecedesReadLock(
            source,
            "public async Task<IReadOnlyList<RentalAssetViewRow>> GetAssetRowsAsync(",
            "public async Task<RentalAssetViewRow?> GetAssetRowAsync(");
        AssertRefreshPrecedesReadLock(
            source,
            "public async Task<RentalAssetViewRow?> GetAssetRowAsync(",
            "private List<RentalAssetViewRow> BuildAssetViewRowsForDisplay(");
    }

    [Fact]
    public async Task SaveAssetAsync_StaleRevisionForPhysicallyMissingAsset_ReturnsConflictWithoutRecreatingRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var staleCandidate = CreateAsset(Guid.NewGuid(), "PURGED-STALE-SAVE");
        staleCandidate.Revision = 17;
        staleCandidate.Notes = "stale editor content must not recreate a purged asset";

        var service = new RentalStateService(db);
        var result = await service.SaveAssetAsync(
            staleCandidate,
            CreateAdminSession(),
            allowCategoryRecovery: true);

        db.ChangeTracker.Clear();
        var rowWasRecreated = await db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(asset => asset.Id == staleCandidate.Id);

        Assert.False(result.Success, result.Message);
        Assert.True(result.ConcurrencyConflict, result.Message);
        Assert.Contains("영구삭제", result.Message, StringComparison.Ordinal);
        Assert.Contains("재생성하지 않았", result.Message, StringComparison.Ordinal);
        Assert.False(rowWasRecreated);
    }

    [Fact]
    public async Task SaveAssetAsync_StaleActiveCandidateForTombstone_ReturnsConflictAndPreservesTombstone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var assetId = Guid.NewGuid();
        var tombstone = CreateAsset(assetId, "SERVER-TOMBSTONE");
        tombstone.Revision = 23;
        tombstone.Notes = "server tombstone content";
        tombstone.IsDirty = false;
        tombstone.IsDeleted = true;
        tombstone.CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
        tombstone.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
        db.RentalAssets.Add(tombstone);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var staleCandidate = CreateAsset(assetId, "STALE-ACTIVE-EDITOR");
        staleCandidate.Revision = tombstone.Revision;
        staleCandidate.Notes = "stale editor content must not replace the tombstone";

        var service = new RentalStateService(db);
        var result = await service.SaveAssetAsync(
            staleCandidate,
            CreateAdminSession(),
            allowCategoryRecovery: true);

        db.ChangeTracker.Clear();
        var stored = await db.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(asset => asset.Id == assetId);

        Assert.False(result.Success, result.Message);
        Assert.True(result.ConcurrencyConflict, result.Message);
        Assert.Contains("휴지통", result.Message, StringComparison.Ordinal);
        Assert.Contains("복원", result.Message, StringComparison.Ordinal);
        Assert.True(stored.IsDeleted);
        Assert.False(stored.IsDirty);
        Assert.Equal(tombstone.Revision, stored.Revision);
        Assert.Equal(tombstone.AssetKey, stored.AssetKey);
        Assert.Equal(tombstone.ManagementNumber, stored.ManagementNumber);
        Assert.Equal(tombstone.Notes, stored.Notes);
    }

    [Fact]
    public async Task SaveAndDeleteAssetAsync_SharingDbContext_AreSerialized()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var saveAsset = CreateAsset(Guid.NewGuid(), "SAVE");
        var deleteAsset = CreateAsset(Guid.NewGuid(), "DELETE");
        db.RentalAssets.AddRange(saveAsset, deleteAsset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        saveAsset.Notes = "저장 작업 진행 중";
        saveGate.Enable();

        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var saveTask = service.SaveAssetAsync(
            saveAsset,
            session,
            allowCategoryRecovery: true);
        Task<LocalMutationResult>? deleteTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            deleteTask = service.DeleteAssetAsync(deleteAsset.Id, session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                deleteTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(deleteTask, completedBeforeSaveReleased);

            saveGate.Release();
            var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var deleteResult = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(saveResult.Success, saveResult.Message);
            Assert.True(deleteResult.Success, deleteResult.Message);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (deleteTask is not null)
                    await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }

        db.ChangeTracker.Clear();
        var storedSaveAsset = await db.RentalAssets.IgnoreQueryFilters()
            .SingleAsync(asset => asset.Id == saveAsset.Id);
        var storedDeleteAsset = await db.RentalAssets.IgnoreQueryFilters()
            .SingleAsync(asset => asset.Id == deleteAsset.Id);
        Assert.Equal("저장 작업 진행 중", storedSaveAsset.Notes);
        Assert.True(storedDeleteAsset.IsDeleted);
        Assert.True(storedDeleteAsset.IsDirty);
    }

    [Fact]
    public async Task SaveBillingProfileAndDeleteReferencedAsset_AreSerializedAcrossDbContexts()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-billing-profile-asset-race-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Cache=Shared";
        var saveGate = new BlockingSaveChangesInterceptor();
        var setupOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var saveOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(saveGate)
            .Options;
        var deleteOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            var assetId = Guid.NewGuid();
            await using (var setupDb = new LocalDbContext(setupOptions))
            {
                await setupDb.Database.EnsureCreatedAsync();
                var asset = CreateAsset(assetId, "PROFILE-SAVE-DELETE-RACE");
                asset.TenantCode = TenantScopeCatalog.Itworld;
                asset.OfficeCode = OfficeCodeCatalog.Itworld;
                asset.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
                asset.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
                setupDb.RentalAssets.Add(asset);
                await setupDb.SaveChangesAsync();
            }

            await using var saveDb = new LocalDbContext(saveOptions);
            await using var deleteDb = new LocalDbContext(deleteOptions);
            var profile = CreateBillingProfile(Guid.NewGuid(), "PROFILE-SAVE-DELETE-RACE");
            profile.BillingTemplateJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { IncludedAssetIds = new[] { assetId } }
            });
            saveGate.Enable();

            var session = CreateAdminSession();
            var saveTask = new RentalStateService(saveDb).SaveBillingProfileAsync(profile, session);
            Task<LocalMutationResult>? deleteTask = null;
            try
            {
                await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

                deleteTask = new RentalStateService(deleteDb).DeleteAssetAsync(assetId, session);
                var completedBeforeSaveReleased = await Task.WhenAny(
                    deleteTask,
                    Task.Delay(TimeSpan.FromMilliseconds(200)));

                Assert.NotSame(deleteTask, completedBeforeSaveReleased);

                saveGate.Release();
                var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                var deleteResult = await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.True(saveResult.Success, saveResult.Message);
                Assert.False(deleteResult.Success);
            }
            finally
            {
                saveGate.Release();
                try
                {
                    await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                    if (deleteTask is not null)
                        await deleteTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Preserve the primary assertion or timeout failure.
                }
            }

            await using var verifyDb = new LocalDbContext(setupOptions);
            var storedAsset = await verifyDb.RentalAssets.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == assetId);
            Assert.False(storedAsset.IsDeleted);
            Assert.True(await verifyDb.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(current => current.Id == profile.Id && !current.IsDeleted));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task AssetListRead_WaitsForRunningAssetSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "READ-BARRIER");
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        asset.Notes = "save must finish before list query";
        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var saveTask = service.SaveAssetAsync(
            asset,
            session,
            allowCategoryRecovery: true);
        Task<IReadOnlyList<RentalAssetViewRow>>? readTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            readTask = service.GetAssetRowsAsync(new RentalAssetFilter(), session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                readTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(readTask, completedBeforeSaveReleased);

            saveGate.Release();
            var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var rows = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(saveResult.Success, saveResult.Message);
            Assert.Contains(rows, row => row.Source.Id == asset.Id);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (readTask is not null)
                    await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }
    }

    [Fact]
    public async Task AssetDetailRead_WaitsForRunningAssetSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "DETAIL-READ-BARRIER");
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        asset.Notes = "save must finish before detail query";
        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var saveTask = service.SaveAssetAsync(
            asset,
            session,
            allowCategoryRecovery: true);
        Task<RentalAssetViewRow?>? readTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            readTask = service.GetAssetRowAsync(asset.Id, session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                readTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(readTask, completedBeforeSaveReleased);

            saveGate.Release();
            var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var row = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(saveResult.Success, saveResult.Message);
            Assert.Equal(asset.Id, row?.Source.Id);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (readTask is not null)
                    await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }
    }

    [Fact]
    public async Task AssignmentHistoryEditRead_WaitsForRunningAssetSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "HISTORY-READ-BARRIER");
        var billingProfile = CreateBillingProfile(Guid.NewGuid(), "HISTORY-READ-BARRIER");
        asset.BillingProfileId = billingProfile.Id;
        asset.AssetStatus = "임대진행중";
        db.RentalAssets.Add(asset);
        db.RentalBillingProfiles.Add(billingProfile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        asset.Notes = "save must finish before history edit read";
        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var saveTask = service.SaveAssetAsync(
            asset,
            session,
            allowCategoryRecovery: true);
        Task<RentalAssetAssignmentHistoryEditRequest?>? readTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            readTask = service.CreateAssetAssignmentHistoryEditRequestAsync(
                asset.Id,
                session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                readTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(readTask, completedBeforeSaveReleased);

            saveGate.Release();
            var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var request = await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(saveResult.Success, saveResult.Message);
            Assert.Equal(asset.Id, request?.AssetId);
            Assert.Equal("Concurrency Customer · Concurrency Item", request?.BillingProfileDisplay);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (readTask is not null)
                    await readTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }
    }

    [Fact]
    public async Task AssignmentHistoryMutation_WaitsForRunningAssetSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "HISTORY-WRITE-BARRIER");
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        asset.Notes = "save must finish before history mutation";
        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var saveTask = service.SaveAssetAsync(
            asset,
            session,
            allowCategoryRecovery: true);
        Task<LocalMutationResult>? historyTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            historyTask = service.SaveAssetAssignmentHistoryAsync(
                new RentalAssetAssignmentHistoryEditRequest
                {
                    AssetId = asset.Id,
                    IsCurrent = false,
                    LinkedAtLocal = DateTime.Today.AddDays(-1),
                    UnlinkedAtLocal = DateTime.Today,
                    CustomerName = "동시성 이력 테스트",
                    InstallLocation = "동시성 이력 위치",
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    ChangeReason = "동시성 직렬화 검증"
                },
                session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                historyTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(historyTask, completedBeforeSaveReleased);

            saveGate.Release();
            var saveResult = await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var historyResult = await historyTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(saveResult.Success, saveResult.Message);
            Assert.True(historyResult.Success, historyResult.Message);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await saveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (historyTask is not null)
                    await historyTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }

        db.ChangeTracker.Clear();
        Assert.Single(await db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(history => history.AssetId == asset.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task AssetReadAndDelete_WaitForRunningAssignmentHistorySave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "HISTORY-SAVE-HOLDER");
        db.RentalAssets.Add(asset);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var historySaveTask = service.SaveAssetAssignmentHistoryAsync(
            CreateHistoryEditRequest(asset),
            session);
        Task<IReadOnlyList<RentalAssetAssignmentHistoryViewItem>>? historyReadTask = null;
        Task<LocalMutationResult>? assetDeleteTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            historyReadTask = service.GetAssetAssignmentHistoriesAsync(asset.Id, session);
            assetDeleteTask = service.DeleteAssetAsync(asset.Id, session);
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            Assert.False(historyReadTask.IsCompleted);
            Assert.False(assetDeleteTask.IsCompleted);

            saveGate.Release();
            var historySaveResult = await historySaveTask.WaitAsync(TimeSpan.FromSeconds(10));
            _ = await historyReadTask.WaitAsync(TimeSpan.FromSeconds(10));
            var assetDeleteResult = await assetDeleteTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(historySaveResult.Success, historySaveResult.Message);
            Assert.True(assetDeleteResult.Success, assetDeleteResult.Message);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await historySaveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (historyReadTask is not null)
                    await historyReadTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (assetDeleteTask is not null)
                    await assetDeleteTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }
    }

    [Fact]
    public async Task AssignmentHistoryDelete_WaitsForRunningAssetSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var asset = CreateAsset(Guid.NewGuid(), "HISTORY-DELETE-BARRIER");
        var history = CreateAssignmentHistory(Guid.NewGuid(), asset);
        db.RentalAssets.Add(asset);
        db.RentalAssetAssignmentHistories.Add(history);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        asset.Notes = "save must finish before history delete";
        saveGate.Enable();
        var service = new RentalStateService(db);
        var session = CreateAdminSession();
        var assetSaveTask = service.SaveAssetAsync(
            asset,
            session,
            allowCategoryRecovery: true);
        Task<LocalMutationResult>? historyDeleteTask = null;
        try
        {
            await saveGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            historyDeleteTask = service.DeleteAssetAssignmentHistoryAsync(history.Id, session);
            var completedBeforeSaveReleased = await Task.WhenAny(
                historyDeleteTask,
                Task.Delay(TimeSpan.FromMilliseconds(200)));

            Assert.NotSame(historyDeleteTask, completedBeforeSaveReleased);

            saveGate.Release();
            var assetSaveResult = await assetSaveTask.WaitAsync(TimeSpan.FromSeconds(10));
            var historyDeleteResult = await historyDeleteTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(assetSaveResult.Success, assetSaveResult.Message);
            Assert.True(historyDeleteResult.Success, historyDeleteResult.Message);
        }
        finally
        {
            saveGate.Release();
            try
            {
                await assetSaveTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (historyDeleteTask is not null)
                    await historyDeleteTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Preserve the primary assertion or timeout failure.
            }
        }

        db.ChangeTracker.Clear();
        var storedHistory = await db.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == history.Id);
        Assert.True(storedHistory.IsDeleted);
        Assert.True(storedHistory.IsDirty);
        Assert.False(storedHistory.IsCurrent);
    }

    private static LocalRentalBillingProfile CreateBillingProfile(Guid id, string marker)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"CONCURRENCY-PROFILE-{marker}-{id:N}",
            CustomerName = "Concurrency Customer",
            ItemName = "Concurrency Item",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            IsActive = true,
            IsDirty = false,
            IsDeleted = false
        };

    private static RentalAssetAssignmentHistoryEditRequest CreateHistoryEditRequest(LocalRentalAsset asset)
        => new()
        {
            AssetId = asset.Id,
            IsCurrent = false,
            LinkedAtLocal = DateTime.Today.AddDays(-1),
            UnlinkedAtLocal = DateTime.Today,
            CustomerName = "Concurrency History Customer",
            InstallLocation = "Concurrency History Location",
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            ChangeReason = "Concurrency serialization test"
        };

    private static LocalRentalAssetAssignmentHistory CreateAssignmentHistory(
        Guid id,
        LocalRentalAsset asset)
        => new()
        {
            Id = id,
            AssetId = asset.Id,
            TenantCode = asset.TenantCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerName = "Concurrency History Customer",
            InstallLocation = "Concurrency History Location",
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            ChangeReason = "Concurrency delete test",
            IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow.AddDays(-1),
            IsDirty = false,
            IsDeleted = false
        };

    private static LocalRentalAsset CreateAsset(Guid id, string marker)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"CONCURRENCY-{marker}-{id:N}",
            ManagementId = $"CONCURRENCY-{marker}-{id:N}",
            ManagementNumber = $"CONCURRENCY-{marker}-{id:N}",
            MachineNumber = $"CONCURRENCY-{marker}-{id:N}",
            CurrentLocation = "동시성 테스트",
            ItemCategoryName = "Printer",
            ItemName = string.Empty,
            AssetStatus = "창고",
            BillingEligibilityStatus = "청구제외",
            BillingExclusionReason = "동시성 테스트",
            Notes = marker,
            IsDirty = false,
            IsDeleted = false
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"rental-asset-concurrency-{Guid.NewGuid():N}",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static void AssertRefreshPrecedesReadLock(
        string source,
        string methodStartMarker,
        string methodEndMarker)
    {
        var start = source.IndexOf(methodStartMarker, StringComparison.Ordinal);
        var end = source.IndexOf(methodEndMarker, start + methodStartMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not isolate method: {methodStartMarker}");

        var method = source[start..end];
        var refreshIndex = method.IndexOf(
            "await EnsureAdministrativeBusinessCachesAsync(session, ct);",
            StringComparison.Ordinal);
        var readLockIndex = method.IndexOf(
            "await AssetSaveLock.WaitAsync(ct);",
            StringComparison.Ordinal);

        Assert.True(refreshIndex >= 0, $"Administrative cache refresh missing: {methodStartMarker}");
        Assert.True(readLockIndex >= 0, $"Asset read lock missing: {methodStartMarker}");
        Assert.True(
            refreshIndex < readLockIndex,
            $"Administrative cache refresh must complete before taking AssetSaveLock: {methodStartMarker}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class BlockingSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enabled;
        private int _blocked;

        public Task Entered => _entered.Task;

        public void Enable()
            => Volatile.Write(ref _enabled, 1);

        public void Release()
            => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _enabled) == 0 ||
                Interlocked.Exchange(ref _blocked, 1) != 0)
            {
                return result;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
