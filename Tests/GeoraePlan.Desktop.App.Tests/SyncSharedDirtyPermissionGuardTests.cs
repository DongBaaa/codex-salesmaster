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
