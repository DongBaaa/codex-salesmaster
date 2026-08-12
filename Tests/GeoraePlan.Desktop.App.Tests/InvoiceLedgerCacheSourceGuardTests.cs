using System;
using System.IO;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceLedgerCacheSourceGuardTests
{
    [Fact]
    public void InvoiceLedgerCaches_AreBoundedAcrossMainAndLookupScreens()
    {
        var root = FindRepositoryRoot();
        var mainSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var lookupSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "CustomerInvoiceLookupViewModel.cs"));

        Assert.Contains("internal const int MaxEntries = 32;", mainSource, StringComparison.Ordinal);
        Assert.Contains("InvoiceLedgerCacheStore.Set(cache, key, value);", mainSource, StringComparison.Ordinal);
        Assert.Contains("InvoiceLedgerCacheStore.Set(_invoiceRowCache, rowCacheKey, rows);", mainSource, StringComparison.Ordinal);
        Assert.Contains("InvoiceLedgerCacheStore.Set(_invoiceRowCache, rowCacheKey, rows);", lookupSource, StringComparison.Ordinal);
        Assert.Contains("private async Task LoadInvoiceListAsync()\n        => await ReloadInvoiceListAsync();", mainSource, StringComparison.Ordinal);
        Assert.Contains(
            "await LoadInvoiceListCoreAsync(\n                forceReload: false,\n                cancellationToken: ct,\n                dataGateAlreadyHeld: true);",
            mainSource,
            StringComparison.Ordinal);
        Assert.Contains("if (_dashboardMetricsLoaded && !forceReload)", mainSource, StringComparison.Ordinal);
        Assert.Contains("_dashboardMetricsLoaded = false;", mainSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerInvoiceLookupWindow_DelegatesRefreshToMutationWindowClose_ButNotPrint()
    {
        var root = FindRepositoryRoot();
        var windowSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "CustomerInvoiceLookupWindow.xaml.cs"));
        var mainSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "CustomerInvoiceLookupViewModel.cs"));

        Assert.DoesNotContain("RunAndRefreshAsync", windowSource, StringComparison.Ordinal);
        Assert.Contains("OpenInvoiceWindowAsync(row.Id, lookupWindow, RefreshLookupRowsAsync)", mainSource, StringComparison.Ordinal);
        Assert.Contains("OpenCustomerEditorAsync(customerId, lookupWindow, RefreshLookupCustomersAndRowsAsync)", mainSource, StringComparison.Ordinal);
        Assert.Contains("win.Closed += (_, _) => RunUiAsync(", mainSource, StringComparison.Ordinal);
        Assert.Contains("public async Task RefreshCustomersAndRowsAsync()", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("_allCustomers = await _local.GetCustomersAsync(_session);", viewModelSource, StringComparison.Ordinal);

        var printMethodStart = windowSource.IndexOf("private void PrintSelectedInvoiceRow()", StringComparison.Ordinal);
        var printMethodEnd = windowSource.IndexOf("private void InvoiceRowsDataGrid_PreviewMouseRightButtonDown", StringComparison.Ordinal);
        Assert.True(printMethodStart >= 0 && printMethodEnd > printMethodStart);

        var printMethodSource = windowSource.Substring(printMethodStart, printMethodEnd - printMethodStart);
        Assert.DoesNotContain("RefreshRowsAsync", printMethodSource, StringComparison.Ordinal);
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
