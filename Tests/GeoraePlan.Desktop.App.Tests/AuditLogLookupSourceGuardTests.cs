using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AuditLogLookupSourceGuardTests
{
    [Fact]
    public void AuditLogLookup_UsesExistingPermissionAndScopedLocalAuditData()
    {
        var appRoot = FindDesktopAppRoot();
        var service = ReadAppFile(appRoot, "Services", "LocalStateService.AuditLogLookup.cs");
        var lookupViewModel = ReadAppFile(appRoot, "ViewModels", "AuditLogLookupViewModel.cs");
        var environmentViewModel = ReadAppFile(appRoot, "ViewModels", "EnvironmentSettingsViewModel.AuditLogLookup.cs");
        var environmentWindow = ReadAppFile(appRoot, "Views", "EnvironmentSettingsWindow.xaml");

        AssertContainsAll(
            service,
            "AppPermissionNames.DataBackupRestore",
            "_session.HasAdministrativePrivileges",
            "_db.AuditLogs.AsNoTracking()",
            ".OrderByDescending(log => log.CreatedAtUtc)",
            "AuditLogLookupLimit = 1000",
            "AuditLogLookupScanLimit = 10000",
            "skip < AuditLogLookupScanLimit",
            "IsScanLimitReached",
            "ScannedCount",
            "CanAccessCustomer(customer, _session)",
            "CanAccessInvoice(invoice, _session)",
            "CanReadItemScope(item, _session)",
            "CanReadAuditInventoryTransfer(transfer)",
            "CanReadAuditRentalScope(",
            "ResolveUnverifiableAuditTarget");
        Assert.DoesNotContain("new AppPermission", service, StringComparison.Ordinal);
        Assert.DoesNotContain("ErpApiClient", service, StringComparison.Ordinal);

        AssertContainsAll(
            lookupViewModel,
            "DateTime.Today.AddDays(-30)",
            "result.IsScanLimitReached",
            "스캔 상한",
            "날짜나 필터를 좁혀 다시 조회하세요.");

        AssertContainsAll(
            environmentViewModel,
            "public bool CanOpenAuditLogLookup =>",
            "_session.HasAdministrativePrivileges",
            "_session.HasPermission(AppPermissionNames.DataBackupRestore)",
            "if (!CanOpenAuditLogLookup)",
            "WindowShowHelper.ShowModelessWithDeferredLoad(");

        AssertContainsAll(
            environmentWindow,
            "Content=\"작업 이력 조회\"",
            "Command=\"{Binding OpenAuditLogLookupCommand}\"",
            "IsEnabled=\"{Binding CanOpenAuditLogLookup}\"");
    }

    [Fact]
    public void AuditLogLookupWindow_IsReadOnlyKeyboardAccessibleAndVirtualized()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "AuditLogLookupWindow.xaml");
        var code = ReadAppFile(appRoot, "Views", "AuditLogLookupWindow.xaml.cs");

        AssertContainsAll(
            xaml,
            "Loaded=\"Window_Loaded\"",
            "KeyDown=\"Window_KeyDown\"",
            "ClipboardCopyMode=\"IncludeHeader\"",
            "EnableRowVirtualization=\"True\"",
            "EnableColumnVirtualization=\"True\"",
            "VirtualizingPanel.IsVirtualizing=\"True\"",
            "VirtualizingPanel.VirtualizationMode=\"Recycling\"",
            "IsReadOnly=\"True\"",
            "IsReadOnlyCaretVisible\" Value=\"True\"",
            "x:Name=\"BeforeJsonTextBox\"",
            "x:Name=\"AfterJsonTextBox\"",
            "Click=\"CopyBeforeJsonButton_Click\"",
            "Click=\"CopyAfterJsonButton_Click\"");

        AssertContainsAll(
            code,
            "SearchTextBox.Focus();",
            "Keyboard.Focus(SearchTextBox);",
            "Key.F12 or Key.Escape",
            "Clipboard.SetText(text)");
    }

    [Fact]
    public void AuditLogLookup_MasksRequiredSensitiveJsonKeys()
    {
        var appRoot = FindDesktopAppRoot();
        var service = ReadAppFile(appRoot, "Services", "LocalStateService.AuditLogLookup.cs");

        AssertContainsAll(
            service,
            "normalized.Contains(\"password\"",
            "normalized.Contains(\"token\"",
            "normalized.Contains(\"secret\"",
            "normalized.Contains(\"apikey\"",
            "jsonObject[key] = \"***\"",
            "jsonValue.TryGetValue<string>",
            "SensitiveJsonFallbackRegex.Replace(");
    }

    private static void AssertContainsAll(string source, params string[] expectedMarkers)
    {
        foreach (var marker in expectedMarkers)
            Assert.Contains(marker, source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string appRoot, params string[] pathParts)
        => File.ReadAllText(Path.Combine([appRoot, .. pathParts]));

    private static string FindDesktopAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var desktopRoot = Path.Combine(directory.FullName, "Desktop");
            if (Directory.Exists(desktopRoot) &&
                Directory.EnumerateFiles(directory.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return Directory.EnumerateDirectories(desktopRoot, "*.Desktop.App", SearchOption.TopDirectoryOnly)
                    .Single();
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Desktop app root was not found.");
    }
}
