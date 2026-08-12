using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ViewModelLifecycleGuardTests
{
    [Fact]
    public void InventoryViewModel_CancelsSelectionLoadsBeforeScopedDbIsDisposed()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "InventoryViewModel.cs"));

        Assert.Contains("private readonly CancellationTokenSource _lifetimeCts = new();", source, StringComparison.Ordinal);
        Assert.Contains("_lifetimeCts.Cancel();", source, StringComparison.Ordinal);
        Assert.Contains("GetInventoryMovementsAsync(itemId, ct: ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetItemConfirmedInvoiceDatesAsync(itemId, _session, ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetItemVendorPurchasePricesAsync(itemId, _session, ct)", source, StringComparison.Ordinal);
        Assert.Contains("ct.ThrowIfCancellationRequested();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryViewModel_UsesLegacyDatesAsFallbackWithoutChangingSavedItemDates()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "InventoryViewModel.cs"));

        Assert.Contains("DisplayLastPurchaseDate = item.LastPurchaseDate;", source, StringComparison.Ordinal);
        Assert.Contains("DisplayLastSaleDate = item.LastSaleDate;", source, StringComparison.Ordinal);
        Assert.Contains("if (invoiceDates.LastPurchaseDate.HasValue)", source, StringComparison.Ordinal);
        Assert.Contains("if (invoiceDates.LastSaleDate.HasValue)", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentSelectedItemVendorPriceLoad(version, itemId))", source, StringComparison.Ordinal);
        Assert.Contains("&& SelectedItem?.Id == itemId;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EditLastPurchaseDate = invoiceDates.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EditLastSaleDate = invoiceDates.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryViewModel_RefreshesInvoiceHistoryOnDispatcherAndUnsubscribesOnDispose()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "InventoryViewModel.cs"));

        Assert.Contains("_local.ItemInvoiceHistoryChanged += HandleItemInvoiceHistoryChanged;", source, StringComparison.Ordinal);
        Assert.Contains("_local.ItemInvoiceHistoryChanged -= HandleItemInvoiceHistoryChanged;", source, StringComparison.Ordinal);
        Assert.Contains("dispatcher.InvokeAsync(() =>", source, StringComparison.Ordinal);

        var handlerStart = source.IndexOf(
            "private void HandleItemInvoiceHistoryChanged",
            StringComparison.Ordinal);
        var handlerEnd = source.IndexOf(
            "partial void OnSearchTextChanged",
            handlerStart,
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        Assert.True(handlerEnd > handlerStart);
        var handler = source[handlerStart..handlerEnd];
        var purchaseFallback = handler.IndexOf(
            "DisplayLastPurchaseDate = selectedItem.Source.LastPurchaseDate;",
            StringComparison.Ordinal);
        var saleFallback = handler.IndexOf(
            "DisplayLastSaleDate = selectedItem.Source.LastSaleDate;",
            StringComparison.Ordinal);
        var reload = handler.IndexOf(
            "RequestLoadSelectedItemVendorPurchasePrices(selectedItem.Id);",
            StringComparison.Ordinal);
        Assert.True(purchaseFallback >= 0);
        Assert.True(saleFallback > purchaseFallback);
        Assert.True(reload > saleFallback);
    }

    [Fact]
    public void SharedDataNotifierSubscribers_UnsubscribeWhenTheirWindowsClose()
    {
        var repositoryRoot = FindRepositoryRoot();
        var transferViewModel = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "InventoryTransferViewModel.cs"));
        var transferWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "InventoryTransferWindow.xaml.cs"));
        var rentalAssetViewModel = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "RentalAssetViewModel.cs"));

        Assert.Contains(
            "InventoryTransferViewModel : ObservableObject, IDisposable",
            transferViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "_local.InventoryStateChanged -= HandleInventoryStateChanged;",
            transferViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Closed += (_, _) => _vm.Dispose();",
            transferWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "_local.InventoryStateChanged -= HandleInventoryStateChanged;",
            rentalAssetViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedDataNotifierSubscribers_MarshalRefreshesToTheWpfDispatcher()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceFiles = new[]
        {
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "ViewModels",
                "InventoryViewModel.cs"),
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "ViewModels",
                "InventoryTransferViewModel.cs"),
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "ViewModels",
                "RentalAssetViewModel.cs")
        };

        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.Contains(
                "dispatcher.InvokeAsync(QueueInventoryStateRefreshOnDispatcher)",
                source,
                StringComparison.Ordinal);
        }

        var salesSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "SalesViewModel.cs"));
        Assert.Contains(
            "dispatcher.InvokeAsync(QueueInventoryReloadOnDispatcher)",
            salesSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncService_PublishesInvoiceHistoryOnlyAfterPullCommitAndOwnerValidation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "SyncService.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var notifierSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "LocalStateService.ItemInvoiceHistory.cs"));

        var methodStart = source.IndexOf(
            "private async Task<bool> TryApplyPullAtomicallyAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task<bool> CommitAttachmentTransactionUnderOwnerLeaseAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var completion = method.IndexOf(
            "CompleteAfterDatabaseCommitAsync",
            StringComparison.Ordinal);
        var ownerPublish = method.IndexOf(
            "TryPublishOwnerBoundItemInvoiceHistory",
            StringComparison.Ordinal);
        var unownedPublish = method.IndexOf(
            "_local.TryPublishItemInvoiceHistoryChanged();",
            StringComparison.Ordinal);
        Assert.True(completion >= 0);
        Assert.True(ownerPublish > completion);
        Assert.True(unownedPublish > completion);
        Assert.Contains(
            "itemInvoiceHistoryChanged = await ApplyPullInternalAsync(",
            method,
            StringComparison.Ordinal);
        Assert.True(
            source.Split(
                "TryPublishOwnerBoundItemInvoiceHistory(",
                StringSplitOptions.None).Length - 1 >= 3);
        Assert.Contains(
            "itemInvoiceHistoryChanged = await ApplyPullInternalAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            source.Split(
                "itemInvoiceHistoryChanged = await ApplyPullInternalAsync(",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "var invoicePurgeApplied = await ApplyPulledPurgeRecordsAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (IsInvoicePurgeRecordKind(record.Kind))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ApplyDeferredPurgeRecordsCoreAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "services.AddSingleton<DesktopDataChangeNotifier>();",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_local.CaptureInventoryStateChanges()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "itemInvoiceHistoryChanged = true;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "inventoryStateChanged: true",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed class DesktopDataChangeNotifier",
            notifierSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryPublishOwnerBoundInventoryState(",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void SyncService_ItemInvoiceHistoryPublishCondition_IncludesAppliedInvoicePurgeOnly(
        bool hasPulledInvoices,
        bool invoicePurgeApplied,
        bool expected)
    {
        Assert.Equal(
            expected,
            SyncService.ShouldPublishItemInvoiceHistoryChanged(
                hasPulledInvoices,
                invoicePurgeApplied));
    }

    [Theory]
    [InlineData("invoice", true)]
    [InlineData(" Invoice ", true)]
    [InlineData("transaction", false)]
    [InlineData("item", false)]
    [InlineData("", false)]
    public void SyncService_InvoicePurgeKindNormalization_ExcludesUnrelatedPurges(
        string kind,
        bool expected)
    {
        Assert.Equal(expected, SyncService.IsInvoicePurgeRecordKind(kind));
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
}
