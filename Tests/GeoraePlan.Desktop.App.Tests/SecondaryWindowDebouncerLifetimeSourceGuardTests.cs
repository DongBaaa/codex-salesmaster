using System.IO;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SecondaryWindowDebouncerLifetimeSourceGuardTests
{
    [Fact]
    public void SecondaryWindowViewModels_ExposeAwaitedDebouncerDrainContracts()
    {
        var appRoot = FindDesktopAppRoot();
        var customerLookup = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "CustomerInvoiceLookupViewModel.cs"));
        var rentalBilling = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "RentalBillingViewModel.cs"));
        var yeonsuDelivery = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "YeonsuDeliveryViewModel.cs"));

        Assert.Contains("IAsyncDisposable", customerLookup, StringComparison.Ordinal);
        Assert.Contains("public ValueTask DisposeAsync()", customerLookup, StringComparison.Ordinal);
        Assert.Contains("_invoiceReloadDebouncer.DisposeAsync().AsTask()", customerLookup, StringComparison.Ordinal);

        Assert.Contains("public Task CancelAndDrainPendingBackgroundWorkAsync()", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("await _searchDebouncer.DisposeAsync();", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("await _selectionPipelineCoordinator.CancelAndDrainAsync();", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("await _filterReloadGate.WaitAsync();", rentalBilling, StringComparison.Ordinal);

        Assert.Contains("IAsyncDisposable", yeonsuDelivery, StringComparison.Ordinal);
        Assert.Contains("public async ValueTask DisposeAsync()", yeonsuDelivery, StringComparison.Ordinal);
        Assert.Contains("await _filterDebouncer.DisposeAsync();", yeonsuDelivery, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondaryWindowClosedHandlers_RegisterDrainTasksWithUiTaskHelper()
    {
        var appRoot = FindDesktopAppRoot();
        AssertClosedHandlerTracksDrain(
            Path.Combine(appRoot, "Views", "CustomerInvoiceLookupWindow.xaml.cs"),
            "() => _viewModel.DisposeAsync().AsTask()");
        AssertClosedHandlerTracksDrain(
            Path.Combine(appRoot, "Views", "RentalBillingWindow.xaml.cs"),
            "() => viewModel.CancelAndDrainPendingBackgroundWorkAsync()");
        AssertClosedHandlerTracksDrain(
            Path.Combine(appRoot, "Views", "YeonsuDeliveryWindow.xaml.cs"),
            "() => _viewModel.DisposeAsync().AsTask()");
    }

    private static void AssertClosedHandlerTracksDrain(string path, string drainCall)
    {
        var source = File.ReadAllText(path);
        var closedHandlerStart = source.IndexOf("Closed +=", StringComparison.Ordinal);
        Assert.True(closedHandlerStart >= 0, $"Closed handler not found in {path}.");

        var closedHandlerSource = source.Substring(closedHandlerStart, Math.Min(700, source.Length - closedHandlerStart));
        Assert.Contains("UiTaskHelper.Forget(", closedHandlerSource, StringComparison.Ordinal);
        Assert.Contains(drainCall, closedHandlerSource, StringComparison.Ordinal);
    }

    private static string FindDesktopAppRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var appRoot = Path.Combine(current.FullName, "Desktop", "거래플랜.Desktop.App");
            if (Directory.Exists(appRoot))
                return appRoot;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 데스크톱 앱 루트를 찾지 못했습니다.");
    }
}
