using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetExplicitSaveDebounceTests
{
    [Theory]
    [InlineData("explicit-command")]
    [InlineData("close-auto-save")]
    public async Task ImmediateSaveOwner_CancelsPendingEditAutoSave(
        string saveOwner)
        => await WithViewModelAsync(async (viewModel, debouncer) =>
        {
            var unexpectedAutoSave = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            debouncer.DebounceAsync(
                TimeSpan.FromMilliseconds(150),
                () =>
                {
                    unexpectedAutoSave.TrySetResult();
                    return Task.CompletedTask;
                });

            if (saveOwner == "close-auto-save")
                Assert.True(await viewModel.TryAutoSaveOnCloseAsync());
            else
                await viewModel.SaveCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromMilliseconds(350));

            Assert.False(unexpectedAutoSave.Task.IsCompleted);
            Assert.False(viewModel.HasPendingChanges);
        });

    [Fact]
    public async Task ExplicitSave_WaitsForRunningEditAutoSaveOwner()
        => await WithViewModelAsync(async (viewModel, debouncer) =>
        {
            var actionEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAction = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            debouncer.DebounceAsync(
                TimeSpan.Zero,
                async () =>
                {
                    actionEntered.TrySetResult();
                    await releaseAction.Task;
                });

            await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var saveTask = viewModel.SaveCommand.ExecuteAsync(null);

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Assert.False(saveTask.IsCompleted);
            }
            finally
            {
                releaseAction.TrySetResult();
            }

            await saveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(viewModel.HasPendingChanges);
        });

    [Fact]
    public async Task AsyncBackgroundCleanup_WaitsForRunningEditAutoSave()
        => await WithViewModelAsync(async (viewModel, debouncer) =>
        {
            var actionEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseAction = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            debouncer.DebounceAsync(
                TimeSpan.Zero,
                async () =>
                {
                    actionEntered.TrySetResult();
                    await releaseAction.Task;
                });

            await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cleanupTask = viewModel.CancelPendingBackgroundWorkAsync();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                Assert.False(cleanupTask.IsCompleted);
            }
            finally
            {
                releaseAction.TrySetResult();
            }

            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(5));
        });

    private static async Task WithViewModelAsync(
        Func<RentalAssetViewModel, UiDebouncer, Task> test)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-rental-asset-explicit-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        RentalAssetViewModel? viewModel = null;
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = $"rental-asset-explicit-save-{Guid.NewGuid():N}",
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
                session)
            {
                EditManagementId = $"EXPLICIT-SAVE-{Guid.NewGuid():N}",
                EditManagementNumber = $"EXPLICIT-SAVE-{Guid.NewGuid():N}",
                EditMachineNumber = $"EXPLICIT-SAVE-{Guid.NewGuid():N}",
                EditOfficeCode = OfficeCodeCatalog.Usenet,
                EditAssetStatus = "창고",
                EditBillingEligibilityStatus = "청구제외",
                EditNotes = "explicit save must own the pending draft"
            };

            var debouncerField = typeof(RentalAssetViewModel).GetField(
                "_editAutoSaveDebouncer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(debouncerField);
            var debouncer = Assert.IsType<UiDebouncer>(
                debouncerField!.GetValue(viewModel));
            await test(viewModel, debouncer);
        }
        finally
        {
            viewModel?.CancelPendingBackgroundWork();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
