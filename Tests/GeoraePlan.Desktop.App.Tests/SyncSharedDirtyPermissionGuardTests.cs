using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncSharedDirtyPermissionGuardTests
{
    [Fact]
    public void SharedDirtyPushCollections_HaveMatchingSessionCountAndPendingPermissionRules()
    {
        var root = FindRepositoryRoot();
        var syncServiceSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "SyncService.cs"));
        var localStateSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "LocalStateService.cs"));
        var pendingSummarySource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "LocalStateService.PendingSummary.cs"));

        AssertSharedPermissionMapping(
            syncServiceSource,
            localStateSource,
            pendingSummarySource,
            permissionName: "CompanyProfileEdit",
            dirtyGateName: "canSyncCompanyProfiles",
            requestPropertyName: "CompanyProfiles",
            localSetName: "CompanyProfiles",
            entityDisplayName: "회사정보 변경");

        foreach (var mapping in new[]
                 {
                     ("Units", "단위 변경"),
                     ("CustomerCategories", "거래처분류 변경"),
                     ("PriceGradeOptions", "가격등급 변경"),
                     ("TradeTypeOptions", "거래유형 변경"),
                     ("ItemCategoryOptions", "품목분류 변경")
                 })
        {
            AssertSharedPermissionMapping(
                syncServiceSource,
                localStateSource,
                pendingSummarySource,
                permissionName: "SettingsEdit",
                dirtyGateName: "canSyncSettings",
                requestPropertyName: mapping.Item1,
                localSetName: mapping.Item1,
                entityDisplayName: mapping.Item2);
        }

        AssertSharedPermissionMapping(
            syncServiceSource,
            localStateSource,
            pendingSummarySource,
            permissionName: "RentalSettingsEdit",
            dirtyGateName: "canSyncRentalSettings",
            requestPropertyName: "RentalManagementCompanies",
            localSetName: "RentalManagementCompanies",
            entityDisplayName: "렌탈 관리업체 변경");
    }

    [Fact]
    public void DesktopPushCollections_HaveMatchingServerPermissionRequirements()
    {
        var root = FindRepositoryRoot();
        var syncServiceSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "SyncService.cs"));
        var serverSyncControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs"));

        AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, "CompanyProfiles", "CompanyProfileEdit");

        foreach (var settingsCollection in new[]
                 {
                     "Units",
                     "CustomerCategories",
                     "PriceGradeOptions",
                     "TradeTypeOptions",
                     "ItemCategoryOptions"
                 })
        {
            AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, settingsCollection, "SettingsEdit");
        }

        foreach (var customerCollection in new[] { "CustomerMasters", "Customers", "CustomerContracts" })
            AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, customerCollection, "CustomerEdit");

        foreach (var itemCollection in new[] { "Items", "ItemWarehouseStocks" })
            AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, itemCollection, "ItemEdit");

        AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, "Invoices", "InvoiceEdit");

        foreach (var paymentCollection in new[] { "Transactions", "TransactionAttachments", "Payments" })
            AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, paymentCollection, "PaymentEdit");

        AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, "InventoryTransfers", "DeliveryEdit");
        AssertServerPermissionRequirement(syncServiceSource, serverSyncControllerSource, "RentalManagementCompanies", "RentalSettingsEdit");

        foreach (var rentalBillingCollection in new[] { "RentalBillingProfiles", "RentalBillingLogs" })
        {
            AssertServerPermissionRequirement(
                syncServiceSource,
                serverSyncControllerSource,
                rentalBillingCollection,
                "RentalProfileEdit",
                "RentalEditAll");
        }

        foreach (var rentalAssetCollection in new[] { "RentalAssets", "RentalAssetAssignmentHistories" })
        {
            AssertServerPermissionRequirement(
                syncServiceSource,
                serverSyncControllerSource,
                rentalAssetCollection,
                "RentalAssetEdit",
                "RentalEditAll");
        }
    }

    private static void AssertSharedPermissionMapping(
        string syncServiceSource,
        string localStateSource,
        string pendingSummarySource,
        string permissionName,
        string dirtyGateName,
        string requestPropertyName,
        string localSetName,
        string entityDisplayName)
    {
        Assert.Contains(
            $"var {dirtyGateName} = includeSharedDirty && session.HasPermission(AppPermissionNames.{permissionName});",
            syncServiceSource,
            StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(
                syncServiceSource,
                $@"var\s+dirty\w+\s*=\s*{Regex.Escape(dirtyGateName)}\s*\?.*?_db\.{Regex.Escape(localSetName)}",
                RegexOptions.Singleline),
            $"{localSetName} dirty query must be gated by {dirtyGateName}.");
        Assert.Contains(
            $"{requestPropertyName} = ",
            syncServiceSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"if (!session.HasPermission(AppPermissionNames.{permissionName}))",
            localStateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"_db.{localSetName}.IgnoreQueryFilters().CountAsync",
            localStateSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"{entityDisplayName}\"",
            pendingSummarySource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"=> session.HasPermission(AppPermissionNames.{permissionName})",
            pendingSummarySource,
            StringComparison.Ordinal);
    }

    private static void AssertServerPermissionRequirement(
        string syncServiceSource,
        string serverSyncControllerSource,
        string requestPropertyName,
        params string[] permissionNames)
    {
        Assert.Contains(
            $"{requestPropertyName} = ",
            syncServiceSource,
            StringComparison.Ordinal);
        Assert.Contains(
            $"HasAny(request.{requestPropertyName})",
            serverSyncControllerSource,
            StringComparison.Ordinal);

        var permissionPattern = string.Join(
            "|",
            permissionNames.Select(permission => Regex.Escape($"PermissionNames.{permission}")));
        Assert.True(
            Regex.IsMatch(
                serverSyncControllerSource,
                $@"HasAny\(request\.{Regex.Escape(requestPropertyName)}\).*?({permissionPattern})",
                RegexOptions.Singleline),
            $"{requestPropertyName} must be guarded by one of: {string.Join(", ", permissionNames)}.");
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

        throw new InvalidOperationException("Repository root not found.");
    }
}
