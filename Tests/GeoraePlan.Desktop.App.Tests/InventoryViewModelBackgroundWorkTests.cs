using System.Data.Common;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryViewModelBackgroundWorkTests
{
    [Theory]
    [InlineData(SelectedItemReadTarget.InventoryMovements)]
    [InlineData(SelectedItemReadTarget.VendorItemScope)]
    public async Task CancelPendingBackgroundWorkAsync_DrainsEachBlockedSelectedItemReadBeforeDbDisposal(
        SelectedItemReadTarget target)
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-inventory-background-drain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var itemId = Guid.NewGuid();
        var queryGate = new SelectedItemReadQueryGate(itemId, target);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(tempRoot, "거래플랜.db")};Pooling=False")
            .AddInterceptors(queryGate)
            .Options;
        var logPath = Path.Combine(AppPaths.LogDir, $"{DateTime.Now:yyyyMMdd}.log");
        var logStart = File.Exists(logPath) ? new FileInfo(logPath).Length : 0L;

        InventoryViewModel? viewModel = null;
        await using var db = new LocalDbContext(options);
        try
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var item = new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "종료 drain 테스트 품목",
                NameMatchKey = "종료DRAIN테스트품목",
                CategoryName = "소모품",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                IsDeleted = false,
                IsDirty = false
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            viewModel = new InventoryViewModel(local, session);

            var selectedRow = new InventoryItemRow(
                item,
                new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
                OfficeCodeCatalog.Usenet);
            queryGate.Arm();
            StartSelectedItemRead(viewModel, selectedRow, target);
            await queryGate.WaitUntilBlockedAsync(TimeSpan.FromSeconds(10));

            var cleanupTask = viewModel.CancelPendingBackgroundWorkAsync();

            Assert.False(cleanupTask.IsCompleted);
            queryGate.Release();
            await cleanupTask.WaitAsync(TimeSpan.FromSeconds(10));
            await db.DisposeAsync();

            var logSuffix = ReadLogSuffix(logPath, logStart);
            Assert.DoesNotContain(nameof(ObjectDisposedException), logSuffix, StringComparison.Ordinal);
            Assert.DoesNotContain("선택 품목 이동내역 조회 실패", logSuffix, StringComparison.Ordinal);
            Assert.DoesNotContain("선택 품목 매입처별 단가 조회 실패", logSuffix, StringComparison.Ordinal);
        }
        finally
        {
            queryGate.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ClearingSelection_InvalidatesBlockedMovementReadAndKeepsDetailCollectionsEmpty()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-inventory-stale-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var itemId = Guid.NewGuid();
        var queryGate = new SelectedItemReadQueryGate(
            itemId,
            SelectedItemReadTarget.InventoryMovements);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(tempRoot, "거래플랜.db")};Pooling=False")
            .AddInterceptors(queryGate)
            .Options;

        InventoryViewModel? viewModel = null;
        await using var db = new LocalDbContext(options);
        try
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var item = new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "stale selection 테스트 품목",
                NameMatchKey = "STALESELECTION테스트품목",
                CategoryName = "소모품",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                IsDeleted = false,
                IsDirty = false
            };
            db.Items.Add(item);
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ItemId = itemId,
                WarehouseCode = DomainConstants.WarehouseUsenetMain,
                MovementType = "PurchaseIn",
                QuantityDelta = 1m,
                OccurredDate = DateOnly.FromDateTime(DateTime.Today),
                IsActive = true,
                Note = "late movement"
            });
            await db.SaveChangesAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            viewModel = new InventoryViewModel(local, session);
            var selectedRow = new InventoryItemRow(
                item,
                new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
                OfficeCodeCatalog.Usenet);

            queryGate.Arm();
            StartSelectedItemRead(
                viewModel,
                selectedRow,
                SelectedItemReadTarget.InventoryMovements);
            await queryGate.WaitUntilBlockedAsync(TimeSpan.FromSeconds(10));
            var movementVersionBeforeClear = ReadPrivateInt(
                viewModel,
                "_selectedItemMovementLoadVersion");
            var vendorVersionBeforeClear = ReadPrivateInt(
                viewModel,
                "_selectedItemVendorPriceLoadVersion");

            viewModel.SelectedItem = null;

            Assert.True(
                ReadPrivateInt(viewModel, "_selectedItemMovementLoadVersion") > movementVersionBeforeClear);
            Assert.True(
                ReadPrivateInt(viewModel, "_selectedItemVendorPriceLoadVersion") > vendorVersionBeforeClear);
            Assert.Empty(viewModel.SelectedItemMovements);
            Assert.Empty(viewModel.SelectedItemVendorPurchasePrices);

            var drainTask = ReadBackgroundWork(viewModel).DrainAsync();
            Assert.False(drainTask.IsCompleted);
            queryGate.Release();
            await drainTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Null(viewModel.SelectedItem);
            Assert.Empty(viewModel.SelectedItemMovements);
            Assert.Empty(viewModel.SelectedItemVendorPurchasePrices);
        }
        finally
        {
            queryGate.Release();
            if (viewModel is not null)
                await viewModel.CancelPendingBackgroundWorkAsync();
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void InventoryWindow_StartsAsyncCleanupBeforeLifetimeObservation()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "InventoryWindow.xaml.cs"));

        Assert.Contains("var cleanupTask = vm.CancelPendingBackgroundWorkAsync();", source, StringComparison.Ordinal);
        var observeStart = source.IndexOf("UiTaskHelper.Forget(", StringComparison.Ordinal);
        var cleanupArgument = source.IndexOf("cleanupTask,", observeStart, StringComparison.Ordinal);
        Assert.True(observeStart >= 0);
        Assert.True(cleanupArgument > observeStart);
        Assert.DoesNotContain("vm.Dispose();", source, StringComparison.Ordinal);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "inventory-background-drain-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static void StartSelectedItemRead(
        InventoryViewModel viewModel,
        InventoryItemRow selectedRow,
        SelectedItemReadTarget target)
    {
        typeof(InventoryViewModel)
            .GetField("_selectedItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, selectedRow);
        var methodName = target == SelectedItemReadTarget.InventoryMovements
            ? "RequestLoadSelectedItemMovements"
            : "RequestLoadSelectedItemVendorPurchasePrices";
        typeof(InventoryViewModel)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [selectedRow.Id]);
    }

    private static BackgroundTaskTracker ReadBackgroundWork(InventoryViewModel viewModel)
        => (BackgroundTaskTracker)typeof(InventoryViewModel)
            .GetField("_backgroundWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;

    private static int ReadPrivateInt(InventoryViewModel viewModel, string fieldName)
        => (int)typeof(InventoryViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;

    private static string ReadLogSuffix(string path, long start)
    {
        if (!File.Exists(path))
            return string.Empty;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (start >= stream.Length)
            return string.Empty;

        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
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

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    public enum SelectedItemReadTarget
    {
        InventoryMovements,
        VendorItemScope
    }

    private sealed class SelectedItemReadQueryGate(
        Guid itemId,
        SelectedItemReadTarget target) : DbCommandInterceptor
    {
        private readonly Guid _itemId = itemId;
        private readonly string _tableName = target == SelectedItemReadTarget.InventoryMovements
            ? "InventoryMovements"
            : "Items";
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task WaitUntilBlockedAsync(TimeSpan timeout)
            => _blocked.Task.WaitAsync(timeout);

        public void Release() => _released.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains(_tableName, StringComparison.OrdinalIgnoreCase) &&
                command.Parameters.Cast<DbParameter>().Any(parameter =>
                    Guid.TryParse(parameter.Value?.ToString(), out var parameterId) &&
                    parameterId == _itemId) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _blocked.TrySetResult();
                await _released.Task;
            }

            return result;
        }
    }
}
