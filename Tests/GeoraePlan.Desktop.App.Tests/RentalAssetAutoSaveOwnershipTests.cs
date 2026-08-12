using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetAutoSaveOwnershipTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task CompletedSaveForPreviousAsset_DoesNotResetCurrentEditorBaseline()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-editor-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var previousAssetId = Guid.NewGuid();
            var currentAssetId = Guid.NewGuid();
            db.RentalAssets.AddRange(
                CreateAsset(previousAssetId, "AUTO-SAVE-PREVIOUS"),
                CreateAsset(currentAssetId, "AUTO-SAVE-CURRENT"));
            await db.SaveChangesAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = $"rental-asset-editor-baseline-{Guid.NewGuid():N}",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);

            await viewModel.LoadAndSelectAssetAsync(currentAssetId);
            Assert.Equal(currentAssetId, viewModel.SelectedRow?.Source.Id);

            const string unsavedCurrentNotes = "current asset edit must remain pending";
            viewModel.EditNotes = unsavedCurrentNotes;
            Assert.True(viewModel.HasPendingChanges);

            var refreshMethod = typeof(RentalAssetViewModel).GetMethod(
                "RefreshSavedAssetRowInPlaceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var captureMethod = typeof(RentalAssetViewModel).GetMethod(
                "CaptureEditSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(refreshMethod);
            Assert.NotNull(captureMethod);
            var savedSnapshot = captureMethod!.Invoke(viewModel, null);
            Assert.NotNull(savedSnapshot);
            var refreshTask = Assert.IsAssignableFrom<Task>(
                refreshMethod!.Invoke(
                    viewModel,
                    new[] { (object)previousAssetId, previousAssetId, savedSnapshot }));
            await refreshTask;

            Assert.Equal(currentAssetId, viewModel.SelectedRow?.Source.Id);
            Assert.Equal(unsavedCurrentNotes, viewModel.EditNotes);
            Assert.True(viewModel.HasPendingChanges);
        }
        finally
        {
            viewModel?.CancelPendingBackgroundWork();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ExplicitSave_DeduplicatesQueuedSelectionAutoSave_ButNewerEditStillSaves()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        RentalAssetViewModel? viewModel = null;
        try
        {
            var firstAssetId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            await using (var seedDb = new LocalDbContext(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.RentalAssets.AddRange(
                    CreateAsset(firstAssetId, "AUTO-SAVE-RECEIPT-FIRST"),
                    CreateAsset(secondAssetId, "AUTO-SAVE-RECEIPT-SECOND"));
                await seedDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext(options);
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);

            await viewModel.LoadAndSelectAssetAsync(firstAssetId);
            var secondAsset = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == secondAssetId);

            const string explicitlySavedValue = "exact snapshot saved explicitly";
            viewModel.EditNotes = explicitlySavedValue;
            viewModel.CancelPendingEditAutoSave();
            saveGate.Enable();
            var explicitSave = viewModel.SaveCommand.ExecuteAsync(null);
            await saveGate.Entered.WaitAsync(TestTimeout);

            viewModel.SelectedRow = new RentalAssetViewRow
            {
                Source = secondAsset,
                HasFullDetail = true
            };

            var ownerCountField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ownerCountField);
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField!.GetValue(viewModel)) >= 2);

            saveGate.Release();
            await explicitSave.WaitAsync(TestTimeout);
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField.GetValue(viewModel)) == 0);

            var receiptField = typeof(RentalAssetViewModel).GetField(
                "_lastCompletedEditAutoSave",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(receiptField);
            Assert.NotNull(receiptField!.GetValue(viewModel));
            Assert.Equal(1, saveGate.EnabledSaveCount);

            await using (var verificationDb = new LocalDbContext(options))
            {
                var explicitlyStored = await verificationDb.RentalAssets
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(asset => asset.Id == firstAssetId);
                Assert.Equal(explicitlySavedValue, explicitlyStored.Notes);
            }

            Assert.Equal(secondAssetId, viewModel.SelectedRow?.Source.Id);

            await viewModel.LoadAndSelectAssetAsync(firstAssetId);
            const string genuinelyNewerValue = "genuinely newer edit after explicit save";
            viewModel.EditNotes = genuinelyNewerValue;
            viewModel.CancelPendingEditAutoSave();
            viewModel.SelectedRow = new RentalAssetViewRow
            {
                Source = secondAsset,
                HasFullDetail = true
            };

            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField.GetValue(viewModel)) == 0);
            await using (var verificationDb = new LocalDbContext(options))
            {
                var newerStored = await verificationDb.RentalAssets
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(asset => asset.Id == firstAssetId);
                Assert.Equal(genuinelyNewerValue, newerStored.Notes);
            }

            Assert.Equal(2, saveGate.EnabledSaveCount);
        }
        finally
        {
            saveGate.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
        }
    }

    [Fact]
    public async Task ExplicitSave_PreservesNewerEditEnteredWhileSaveIsRunning()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(saveGate)
            .Options;

        RentalAssetViewModel? viewModel = null;
        try
        {
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId, "RUNNING-SAVE-DRAFT"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            const string capturedBySave = "captured by explicit save";
            const string enteredWhileSaving = "entered while explicit save is running";
            viewModel.EditNotes = capturedBySave;
            saveGate.Enable();
            var saveTask = viewModel.SaveCommand.ExecuteAsync(null);
            try
            {
                await saveGate.Entered.WaitAsync(TestTimeout);
                Assert.True(viewModel.IsEditAutoSaveOwnershipActive);
                Assert.False(viewModel.CanEditAssetDetails);

                viewModel.EditNotes = enteredWhileSaving;
                saveGate.Release();
                await saveTask.WaitAsync(TestTimeout);
            }
            finally
            {
                saveGate.Release();
                try
                {
                    await saveTask.WaitAsync(TestTimeout);
                }
                catch
                {
                    // Preserve the primary assertion or timeout failure.
                }
            }

            Assert.Equal(assetId, viewModel.SelectedRow?.Source.Id);
            Assert.Equal(enteredWhileSaving, viewModel.EditNotes);
            Assert.True(viewModel.HasPendingChanges);

            await using var verificationDb = new LocalDbContext(options);
            var storedNotes = await verificationDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.Notes)
                .SingleAsync();
            Assert.Equal(capturedBySave, storedNotes);
        }
        finally
        {
            viewModel?.CancelPendingBackgroundWork();
        }
    }

    [Fact]
    public async Task RefreshedRemoteRevision_IsNotAdoptedByPreservedLocalDraft()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-remote-revision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId, "REMOTE-REVISION"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            viewModel.EditNotes = "captured save snapshot";
            var captureMethod = typeof(RentalAssetViewModel).GetMethod(
                "CaptureEditSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(captureMethod);
            var savedSnapshot = captureMethod!.Invoke(viewModel, null);
            Assert.NotNull(savedSnapshot);

            const string preservedDraft = "newer local draft keeps its expected revision";
            viewModel.EditNotes = preservedDraft;
            viewModel.CancelPendingEditAutoSave();
            await db.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => asset.Id == assetId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(asset => asset.Revision, 2)
                    .SetProperty(asset => asset.CurrentLocation, "remote PC location"));

            var refreshMethod = typeof(RentalAssetViewModel).GetMethod(
                "RefreshSavedAssetRowInPlaceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(refreshMethod);
            var refreshTask = Assert.IsAssignableFrom<Task>(
                refreshMethod!.Invoke(
                    viewModel,
                    new[] { (object)assetId, assetId, savedSnapshot }));
            await refreshTask;

            var editRevisionField = typeof(RentalAssetViewModel).GetField(
                "_editRevision",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(editRevisionField);
            Assert.Equal(1L, Assert.IsType<long>(editRevisionField!.GetValue(viewModel)));
            Assert.Equal(2L, viewModel.SelectedRow?.Source.Revision);
            Assert.Equal(preservedDraft, viewModel.EditNotes);
            Assert.True(viewModel.HasPendingChanges);
        }
        finally
        {
            viewModel?.CancelPendingBackgroundWork();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TargetedReload_WaitsForSelectionAutoSaveOwner_ThenAdoptsRemoteRevision()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-targeted-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        SemaphoreSlim? ownerGate = null;
        var ownerGateHeld = false;
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var assetId = Guid.NewGuid();
            db.RentalAssets.Add(CreateAsset(assetId, "TARGETED-RELOAD"));
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            const string preservedDraft = "selection auto-save stale draft";
            const string remoteWinner = "remote winner before targeted reload";
            viewModel.EditNotes = preservedDraft;
            viewModel.CancelPendingEditAutoSave();
            await db.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset => asset.Id == assetId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(asset => asset.Revision, 2)
                    .SetProperty(asset => asset.Notes, remoteWinner));

            var remoteAsset = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId);
            var ownerGateField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerGate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ownerCountField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ownerGateField);
            Assert.NotNull(ownerCountField);
            ownerGate = Assert.IsType<SemaphoreSlim>(
                ownerGateField!.GetValue(viewModel));

            await ownerGate.WaitAsync(TestTimeout);
            ownerGateHeld = true;
            viewModel.SelectedRow = new RentalAssetViewRow
            {
                Source = remoteAsset,
                HasFullDetail = true
            };
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField!.GetValue(viewModel)) == 1);

            var targetedReload = viewModel.LoadAndSelectAssetAsync(assetId);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(targetedReload.IsCompleted);

            ownerGate.Release();
            ownerGateHeld = false;
            await targetedReload.WaitAsync(TestTimeout);
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField.GetValue(viewModel)) == 0);

            var editRevisionField = typeof(RentalAssetViewModel).GetField(
                "_editRevision",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(editRevisionField);
            Assert.Equal(2L, viewModel.SelectedRow?.Source.Revision);
            Assert.Equal(2L, Assert.IsType<long>(editRevisionField!.GetValue(viewModel)));
            Assert.Equal(remoteWinner, viewModel.EditNotes);
            Assert.False(viewModel.HasPendingChanges);

            const string retryValue = "explicit retry after targeted reload";
            viewModel.EditNotes = retryValue;
            viewModel.CancelPendingEditAutoSave();
            await viewModel.SaveCommand.ExecuteAsync(null);

            var stored = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId);
            Assert.Equal(retryValue, stored.Notes);
            Assert.Equal(2L, stored.Revision);
            Assert.True(stored.IsDirty);
        }
        finally
        {
            if (ownerGateHeld)
                ownerGate?.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GenericReload_WaitsForSelectionAutoSaveOwner_ThenAdoptsRemoteTombstone()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-generic-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        SemaphoreSlim? ownerGate = null;
        var ownerGateHeld = false;
        try
        {
            var assetId = Guid.NewGuid();
            await using (var seedDb = new LocalDbContext())
            {
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.RentalAssets.Add(CreateAsset(assetId, "GENERIC-RELOAD-TOMBSTONE"));
                await seedDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            const string staleActiveValue = "stale active state must not be replayed";
            const string remoteTombstoneValue = "remote tombstone wins";
            viewModel.EditNotes = staleActiveValue;
            viewModel.CancelPendingEditAutoSave();
            await using (var remoteDb = new LocalDbContext())
            {
                await remoteDb.RentalAssets
                    .IgnoreQueryFilters()
                    .Where(asset => asset.Id == assetId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(asset => asset.Revision, 2)
                        .SetProperty(asset => asset.Notes, remoteTombstoneValue)
                        .SetProperty(asset => asset.IsDeleted, true)
                        .SetProperty(asset => asset.IsDirty, false));
            }

            var ownerGateField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerGate",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ownerCountField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ownerGateField);
            Assert.NotNull(ownerCountField);
            ownerGate = Assert.IsType<SemaphoreSlim>(ownerGateField!.GetValue(viewModel));

            await ownerGate.WaitAsync(TestTimeout);
            ownerGateHeld = true;
            viewModel.SelectedRow = null;
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField!.GetValue(viewModel)) == 1);

            await viewModel.ReloadCommand.ExecuteAsync(null).WaitAsync(TestTimeout);
            Assert.Contains(viewModel.Rows, row => row.Source.Id == assetId);

            ownerGate.Release();
            ownerGateHeld = false;
            await WaitForConditionAsync(
                () => Assert.IsType<int>(ownerCountField.GetValue(viewModel)) == 0 &&
                      viewModel.Rows.All(row => row.Source.Id != assetId) &&
                      viewModel.SelectedRow is null &&
                      !viewModel.HasPendingChanges);

            Assert.Null(viewModel.SelectedRow);
            Assert.False(viewModel.HasPendingChanges);
            await using var verificationDb = new LocalDbContext();
            var tombstone = await verificationDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == assetId);
            Assert.True(tombstone.IsDeleted);
            Assert.False(tombstone.IsDirty);
            Assert.Equal(2L, tombstone.Revision);
            Assert.Equal(remoteTombstoneValue, tombstone.Notes);
        }
        finally
        {
            if (ownerGateHeld)
                ownerGate?.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GenericReload_SelectionChangesWhileAssetQueryIsAwaiting_PersistsPreviousDraft()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var queryGate = new RentalAssetQueryGate();
        var saveCounter = new SaveCountingInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(queryGate, saveCounter)
            .Options;

        RentalAssetViewModel? viewModel = null;
        try
        {
            var firstAssetId = Guid.NewGuid();
            var secondAssetId = Guid.NewGuid();
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.RentalAssets.AddRange(
                CreateAsset(firstAssetId, "GENERIC-RELOAD-SELECTION-FIRST"),
                CreateAsset(secondAssetId, "GENERIC-RELOAD-SELECTION-SECOND"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(firstAssetId);

            var secondAsset = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == secondAssetId);
            const string firstAssetDraft = "first asset draft entered before awaited reload";
            viewModel.EditNotes = firstAssetDraft;
            viewModel.CancelPendingEditAutoSave();

            var suppressionCountField = typeof(RentalAssetViewModel).GetField(
                "_selectionAutoSaveSuppressionCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var ownerCountField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(suppressionCountField);
            Assert.NotNull(ownerCountField);

            saveCounter.Enable();
            queryGate.Arm();
            var reloadTask = viewModel.ReloadCommand.ExecuteAsync(null);
            await queryGate.Entered.WaitAsync(TestTimeout);

            viewModel.SelectedRow = new RentalAssetViewRow
            {
                Source = secondAsset,
                HasFullDetail = true
            };

            queryGate.Release();
            await reloadTask.WaitAsync(TestTimeout);
            await viewModel.WaitForEditAutoSaveQuiescenceAsync().WaitAsync(TestTimeout);

            await using var verificationDb = new LocalDbContext(options);
            var storedNotes = await verificationDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.Id == firstAssetId)
                .Select(asset => asset.Notes)
                .SingleAsync();
            Assert.Equal(firstAssetDraft, storedNotes);
            Assert.Equal(secondAssetId, viewModel.SelectedRow?.Source.Id);
            Assert.Equal(0, Assert.IsType<int>(ownerCountField!.GetValue(viewModel)));
            Assert.Equal(0, Assert.IsType<int>(suppressionCountField!.GetValue(viewModel)));
            Assert.False(viewModel.HasPendingChanges);
            Assert.Equal(1, saveCounter.EnabledSaveCount);
        }
        finally
        {
            queryGate.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
        }
    }

    [Fact]
    public async Task NewerEditDuringSave_WithDeferredExternalReload_IsEventuallyPersistedAndOwnerDrains()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-deferred-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var databasePath = Path.Combine(tempRoot, "deferred-reload.db");
        var saveGate = new BlockingSaveChangesInterceptor();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(saveGate)
            .Options;

        RentalAssetViewModel? viewModel = null;
        RentalStateService? rental = null;
        EventHandler<RentalStateChangedEventArgs>? stateObserver = null;
        try
        {
            var assetId = Guid.NewGuid();
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.RentalAssets.Add(CreateAsset(assetId, "DEFERRED-EXTERNAL-RELOAD"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            var ownerCountField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveOwnerCount",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var externalRefreshMethod = typeof(RentalAssetViewModel).GetMethod(
                "RefreshAfterRentalStateChangedAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(ownerCountField);
            Assert.NotNull(externalRefreshMethod);

            const string capturedByFirstSave = "first snapshot blocked during save";
            const string newerEdit = "newer edit survives deferred external reload";
            viewModel.EditNotes = capturedByFirstSave;
            viewModel.CancelPendingEditAutoSave();
            saveGate.Enable();
            var firstSaveTask = viewModel.SaveCommand.ExecuteAsync(null);
            await saveGate.Entered.WaitAsync(TestTimeout);

            viewModel.EditNotes = newerEdit;
            Assert.True(viewModel.HasPendingChanges);
            Assert.True(viewModel.IsEditAutoSaveOwnershipActive);

            RentalStateChangedEventArgs? observedExternalChange = null;
            stateObserver = (_, args) => observedExternalChange = args;
            rental.StateChanged += stateObserver;
            rental.PublishSynchronizedStateChanges([assetId], null);
            Assert.NotNull(observedExternalChange);
            Assert.NotSame(viewModel, observedExternalChange!.Origin);

            // Unit tests have no WPF Application dispatcher. Invoke the callback that the
            // external StateChanged handler dispatches in the running desktop application.
            var deferredRefreshTask = Assert.IsAssignableFrom<Task>(
                externalRefreshMethod!.Invoke(viewModel, null));
            await deferredRefreshTask.WaitAsync(TestTimeout);

            saveGate.Release();
            await firstSaveTask.WaitAsync(TestTimeout);

            await WaitForConditionAsync(async () =>
            {
                if (viewModel.IsEditAutoSaveOwnershipActive || viewModel.HasPendingChanges)
                    return false;

                await using var verificationDb = new LocalDbContext(options);
                var storedNotes = await verificationDb.RentalAssets
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(asset => asset.Id == assetId)
                    .Select(asset => asset.Notes)
                    .SingleAsync();
                return string.Equals(storedNotes, newerEdit, StringComparison.Ordinal);
            });

            await using var finalDb = new LocalDbContext(options);
            var finalNotes = await finalDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.Notes)
                .SingleAsync();
            Assert.Equal(newerEdit, finalNotes);
            Assert.Equal(0, Assert.IsType<int>(ownerCountField!.GetValue(viewModel)));
            Assert.False(viewModel.IsEditAutoSaveOwnershipActive);
            Assert.False(viewModel.HasPendingChanges);
            Assert.True(saveGate.EnabledSaveCount >= 2);
        }
        finally
        {
            saveGate.Release();
            if (viewModel is not null)
            {
                if (stateObserver is not null)
                {
                    if (rental is not null)
                        rental.StateChanged -= stateObserver;
                }

                await viewModel.CancelPendingBackgroundWorkAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TargetedReload_RemoteTombstone_ResetsStaleEditorIdentityAndContent()
    {
        await AssertTargetedReloadMissingAssetResetsEditorAsync(physicallyPurge: false);
    }

    [Fact]
    public async Task TargetedReload_PhysicallyPurgedAsset_ResetsStaleEditorIdentityAndContent()
    {
        await AssertTargetedReloadMissingAssetResetsEditorAsync(physicallyPurge: true);
    }

    private static async Task AssertTargetedReloadMissingAssetResetsEditorAsync(bool physicallyPurge)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-targeted-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        try
        {
            var assetId = Guid.NewGuid();
            await using (var seedDb = new LocalDbContext())
            {
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.RentalAssets.Add(CreateAsset(assetId, "TARGETED-MISSING-ASSET"));
                await seedDb.SaveChangesAsync();
            }

            await using var db = new LocalDbContext();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            viewModel = new RentalAssetViewModel(
                rental,
                local,
                new RentalDocumentService(),
                null!,
                session);
            await viewModel.LoadAndSelectAssetAsync(assetId);

            const string staleEditorContent = "deleted asset content must not become a new asset";
            viewModel.EditNotes = staleEditorContent;
            viewModel.CancelPendingEditAutoSave();

            await using (var remoteDb = new LocalDbContext())
            {
                if (physicallyPurge)
                {
                    await remoteDb.RentalAssets
                        .IgnoreQueryFilters()
                        .Where(asset => asset.Id == assetId)
                        .ExecuteDeleteAsync();
                }
                else
                {
                    await remoteDb.RentalAssets
                        .IgnoreQueryFilters()
                        .Where(asset => asset.Id == assetId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(asset => asset.Revision, 2)
                            .SetProperty(asset => asset.IsDeleted, true)
                            .SetProperty(asset => asset.IsDirty, false));
                }
            }

            await viewModel.LoadAndSelectAssetAsync(assetId).WaitAsync(TestTimeout);

            Assert.Null(viewModel.SelectedRow);
            Assert.DoesNotContain(viewModel.Rows, row => row.Source.Id == assetId);
            Assert.NotEqual(assetId, viewModel.EditId);
            Assert.NotEqual(staleEditorContent, viewModel.EditNotes);
            Assert.True(string.IsNullOrWhiteSpace(viewModel.EditNotes));
            Assert.False(viewModel.HasPendingChanges);

            await using var verificationDb = new LocalDbContext();
            var storedState = await verificationDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.Id == assetId)
                .Select(asset => new { asset.IsDeleted, asset.Notes })
                .SingleOrDefaultAsync();
            if (physicallyPurge)
            {
                Assert.Null(storedState);
            }
            else
            {
                Assert.NotNull(storedState);
                Assert.True(storedState!.IsDeleted);
                Assert.NotEqual(staleEditorContent, storedState.Notes);
            }
        }
        finally
        {
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"rental-asset-auto-save-owner-{Guid.NewGuid():N}",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static async Task WaitForAssetNotesAsync(Guid assetId, string expectedNotes)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var verificationDb = new LocalDbContext();
            var notes = await verificationDb.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.Notes)
                .SingleAsync();
            if (string.Equals(notes, expectedNotes, StringComparison.Ordinal))
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"Timed out waiting for rental asset notes '{expectedNotes}'.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        Assert.Fail("Timed out waiting for the expected rental asset auto-save state.");
    }

    private static async Task WaitForConditionAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail("Timed out waiting for the expected asynchronous rental asset auto-save state.");
    }

    private static LocalRentalAsset CreateAsset(Guid id, string managementNumber)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = managementNumber,
            ManagementId = managementNumber,
            ManagementNumber = managementNumber,
            MachineNumber = $"{managementNumber}-MACHINE",
            CurrentLocation = "창고",
            AssetStatus = "창고",
            BillingEligibilityStatus = "청구제외",
            Revision = 1,
            IsDirty = false
        };

    private sealed class BlockingSaveChangesInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enabled;
        private int _enabledSaveCount;

        public Task Entered => _entered.Task;
        public int EnabledSaveCount => Volatile.Read(ref _enabledSaveCount);

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
                Interlocked.Increment(ref _enabledSaveCount) != 1)
            {
                return result;
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class RentalAssetQueryGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public Task Entered => _entered.Task;

        public void Arm()
            => Volatile.Write(ref _armed, 1);

        public void Release()
            => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RentalAssets", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class SaveCountingInterceptor : SaveChangesInterceptor
    {
        private int _enabled;
        private int _enabledSaveCount;

        public int EnabledSaveCount => Volatile.Read(ref _enabledSaveCount);

        public void Enable()
            => Volatile.Write(ref _enabled, 1);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _enabled) == 1)
                Interlocked.Increment(ref _enabledSaveCount);

            return ValueTask.FromResult(result);
        }
    }
}
