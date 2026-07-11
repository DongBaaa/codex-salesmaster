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
        Assert.Contains("GetItemVendorPurchasePricesAsync(itemId, _session, ct)", source, StringComparison.Ordinal);
        Assert.Contains("ct.ThrowIfCancellationRequested();", source, StringComparison.Ordinal);
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
