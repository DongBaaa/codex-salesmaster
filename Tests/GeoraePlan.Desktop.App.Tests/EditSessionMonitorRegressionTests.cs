using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EditSessionMonitorRegressionTests
{
    [Fact]
    public void SalesAndCustomerEditors_DoNotRegisterUnsavedGuidEmptySubjects()
    {
        var root = FindRepositoryRoot();
        var salesWindowSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "SalesWindow.xaml.cs"));
        var customerEditWindowSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "CustomerEditWindow.xaml.cs"));

        Assert.Contains("vm.InvoiceId == Guid.Empty", salesWindowSource, StringComparison.Ordinal);
        Assert.Contains("? null", salesWindowSource, StringComparison.Ordinal);
        Assert.Contains("vm.CustomerId == Guid.Empty", customerEditWindowSource, StringComparison.Ordinal);
        Assert.Contains("? null", customerEditWindowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EntityEditSessionMonitor_ReleasesRegisteredSessionWhenSubjectDisappears()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "EntityEditSessionMonitor.cs"));

        Assert.Contains("private bool _hasRegisteredSession;", source, StringComparison.Ordinal);
        Assert.Contains("await ReleaseRegisteredSessionAsync(ct);", source, StringComparison.Ordinal);
        Assert.Contains("_hasRegisteredSession = true;", source, StringComparison.Ordinal);
        Assert.Contains("private async Task ReleaseRegisteredSessionAsync", source, StringComparison.Ordinal);
        Assert.Contains("_hasRegisteredSession = false;", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
