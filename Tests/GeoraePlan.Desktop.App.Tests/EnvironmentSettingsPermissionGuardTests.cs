using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsPermissionGuardTests
{
    [Fact]
    public void EnvironmentSettings_EditControls_FollowExplicitServerPermissions()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var source = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.cs"));

        Assert.Contains(
            "public bool CanManageSelectionOptions => _session.HasPermission(AppPermissionNames.SettingsEdit);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool CanEditCompanyProfiles => _session.HasPermission(AppPermissionNames.CompanyProfileEdit);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public bool CanManageSelectionOptions => _session.HasAdministrativePrivileges;",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public bool CanEditCompanyProfiles => _session.HasAdministrativePrivileges;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettings_UserManagementNewCommand_IsGuardedLikeSaveDeleteReload()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var viewModelSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.cs"));
        var windowSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "Views",
            "EnvironmentSettingsWindow.xaml"));
        var normalizedViewModelSource = viewModelSource.Replace("\r\n", "\n");

        Assert.Contains(
            "Command=\"{Binding NewUserCommand}\" IsEnabled=\"{Binding CanManageUsers}\"",
            windowSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void NewUser()\n    {\n        if (!CanManageUsers)",
            normalizedViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "사용자 관리는 관리자 권한이 있는 계정만 사용할 수 있습니다.",
            viewModelSource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")) &&
                Directory.GetFiles(current.FullName, "*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
