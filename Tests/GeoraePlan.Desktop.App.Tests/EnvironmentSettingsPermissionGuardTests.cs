using System.Xml.Linq;
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

    [Fact]
    public void EnvironmentSettings_YeonsuDefaultBundle_IncludesProfileAndAssetEditWithoutWideRentalPermissions()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var source = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.cs"));
        var normalizedSource = source.Replace("\r\n", "\n");

        Assert.Contains(
            "else if (string.Equals(normalizedOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))\n        {\n            permissions.Add(AppPermissionNames.RentalProfileEdit);\n            permissions.Add(AppPermissionNames.RentalAssetEdit);\n        }",
            normalizedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettings_TenantStorageMode_IsReadOnlyAndPreservedFromSelectedServerDefinition()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var windowSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "Views",
            "EnvironmentSettingsWindow.xaml"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.TenantPolicies.cs"));

        var window = XDocument.Parse(windowSource);
        var storageModeSelector = Assert.Single(
            window.Descendants(),
            element =>
                element.Name.LocalName == "ComboBox" &&
                string.Equals(
                    (string?)element.Attribute("SelectedValue"),
                    "{Binding EditingTenantStorageMode}",
                    StringComparison.Ordinal));
        Assert.Equal("False", (string?)storageModeSelector.Attribute("IsEnabled"));
        Assert.Contains(
            "별도 운영 절차",
            (string?)storageModeSelector.Attribute("ToolTip") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Contains(
            "조회 전용입니다. 저장 방식 변경은 데이터 이관과 서버 라우팅을 포함한 별도 운영 절차로 진행해야 합니다.",
            windowSource,
            StringComparison.Ordinal);
        var normalizedViewModelSource = viewModelSource.Replace("\r\n", "\n");
        Assert.Contains(
            "StorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(\n" +
            "                             SelectedTenantDefinition?.StorageMode,\n" +
            "                             EditingTenantStorageMode)",
            normalizedViewModelSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncMaintenance_DestructiveActions_RequireExplicitMaintenancePermissions()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var environmentWindowSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "Views",
            "EnvironmentSettingsWindow.xaml"));
        var environmentSyncSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.Sync.cs"));
        var diagnosticsWindowSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "Views",
            "SyncDiagnosticsWindow.xaml"));
        var diagnosticsViewModelSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "SyncDiagnosticsViewModel.cs"));
        var normalizedEnvironmentSyncSource = environmentSyncSource.Replace("\r\n", "\n");
        var normalizedDiagnosticsViewModelSource = diagnosticsViewModelSource.Replace("\r\n", "\n");

        var environmentWindow = XDocument.Parse(environmentWindowSource);
        var fullResyncButton = FindButtonByContent(environmentWindow, "전체 재동기화");
        var backupButton = FindButtonByContent(environmentWindow, "백업");
        Assert.Equal("{Binding CanRunFullResync}", (string?)fullResyncButton.Attribute("IsEnabled"));
        Assert.Equal("{Binding FullResyncPermissionHint}", (string?)fullResyncButton.Attribute("ToolTip"));
        Assert.Equal("{Binding CanManageBackupData}", (string?)backupButton.Attribute("IsEnabled"));

        Assert.Contains(
            "public bool CanRunFullResync =>\n" +
            "        _session.IsLoggedIn &&\n" +
            "        !_session.IsOfflineMode &&\n" +
            "        _session.HasGlobalDataScope;",
            normalizedEnvironmentSyncSource,
            StringComparison.Ordinal);
        Assert.Contains("if (!CanRunFullResync)", environmentSyncSource, StringComparison.Ordinal);
        Assert.Contains("if (!CanManageBackupData)", environmentSyncSource, StringComparison.Ordinal);

        var diagnosticsWindow = XDocument.Parse(diagnosticsWindowSource);
        foreach (var content in new[]
                 {
                     "공유 캐시 다시 만들기",
                     "선택 항목 복구",
                     "복구 가능 항목 전체 처리",
                     "해결 이력 정리"
                 })
        {
            var button = FindButtonByContent(diagnosticsWindow, content);
            Assert.Equal("{Binding CanManageSyncMaintenance}", (string?)button.Attribute("IsEnabled"));
            Assert.Equal("{Binding SyncMaintenancePermissionHint}", (string?)button.Attribute("ToolTip"));
        }

        Assert.Contains(
            "public bool CanManageSyncMaintenance =>\n" +
            "        _session.IsLoggedIn &&\n" +
            "        !_session.IsOfflineMode &&\n" +
            "        _session.HasGlobalDataScope;",
            normalizedDiagnosticsViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains("private bool EnsureCanManageSyncMaintenance()", diagnosticsViewModelSource, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(diagnosticsViewModelSource, "if (!EnsureCanManageSyncMaintenance())") >= 4,
            "공유 캐시 재구성, 선택/자동 복구, 해결 이력 정리는 VM에서도 권한을 다시 검사해야 합니다.");
    }

    [Fact]
    public void NormalSyncEntryPoints_DoNotRunPostSyncFullMirrorRefresh()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var environmentSyncSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.Sync.cs")).Replace("\r\n", "\n");
        var recycleBinSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.RecycleBin.cs")).Replace("\r\n", "\n");
        var diagnosticsSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "SyncDiagnosticsViewModel.cs")).Replace("\r\n", "\n");
        var mainSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "MainViewModel.cs")).Replace("\r\n", "\n");

        Assert.DoesNotContain(
            "if (syncOk && dirtyCount == 0)\n                await _sync.RefreshSharedMirrorFromServerAsync();",
            environmentSyncSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (result.Succeeded && dirtyCount == 0)\n                await _sync.RefreshSharedMirrorFromServerAsync();",
            environmentSyncSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (syncSucceeded && !hasPendingChanges)\n                await _sync.RefreshSharedMirrorFromServerAsync();",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (syncOk && dirtyCount == 0)\n                await _sync.RefreshSharedMirrorFromServerAsync();",
            diagnosticsSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (syncOk && dirtyCount == 0)\n            await RunIsolatedSyncAsync(sync => sync.RefreshSharedMirrorFromServerAsync());",
            mainSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleBinPermanentDelete_UsesConfirmedServerPurgeApplyWithReceiptRevision()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var recycleBinSource = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "ViewModels",
            "EnvironmentSettingsViewModel.RecycleBin.cs"));

        Assert.Contains(
            "ApplyConfirmedServerPurgeRecycleBinEntryAsync",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureServerPurgeConfirmationFenceAsync",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "serverMirror.PurgeConfirmationFences.TryGetValue",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains("entry.Revision", recycleBinSource, StringComparison.Ordinal);
        Assert.Contains(
            "if (entry.Kind == RecycleBinEntityKind.Invoice)",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MirrorRecycleBinMutationToServerAsync(\"영구삭제\", orderedEntries)",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "OrderSuccessfulPurgeEntriesForLocalApply",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "mutationTargets = batchTargets.Values",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "locallyCoveredEntries",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "locallySuccessfulPurges",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "serverMirror.EntryFailures",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "reconciliation.SucceededCount",
            recycleBinSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "else\r\n                    {\r\n                        await _local.MarkRecycleBinServerMutationCleanAsync(",
            recycleBinSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedMirrorRefresh_RoutesLimitedScopeBeforeGlobalReset()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory.GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App").Single();
        var source = File.ReadAllText(Path.Combine(
            desktopAppDir,
            "Services",
            "SyncService.cs")).Replace("\r\n", "\n");

        const string route =
            "if (!_session.HasGlobalDataScope)\n" +
            "        {\n" +
            "            SetStatus(\"전체 공유 미러를 초기화하지 않고 현재 권한 범위의 서버 데이터를 갱신합니다.\");\n" +
            "            return await TryRefreshCurrentBusinessScopeCoreAsync(\n" +
            "                ct,\n" +
            "                preserveTrackedChanges);\n" +
            "        }";
        var refreshStart = source.IndexOf(
            "private async Task<bool> TryRefreshSharedMirrorCoreAsync(",
            StringComparison.Ordinal);

        Assert.True(refreshStart >= 0, "전체 공유 미러 갱신의 중앙 진입점을 찾을 수 없습니다.");

        var routeIndex = source.IndexOf(route, refreshStart, StringComparison.Ordinal);
        var resetIndex = source.IndexOf(
            "await _local.ResetSharedMirrorCacheWithAttachmentJournalAsync(",
            refreshStart,
            StringComparison.Ordinal);

        Assert.True(routeIndex > refreshStart, "제한 범위를 현재 권한 범위 갱신으로 우회해야 합니다.");
        Assert.True(resetIndex > routeIndex, "제한 범위 우회는 전역 미러 초기화보다 먼저 실행되어야 합니다.");
        Assert.Contains(
            "private async Task<bool> TryRefreshCurrentBusinessScopeCoreAsync(\n" +
            "        CancellationToken ct,\n" +
            "        bool preserveTrackedChanges = false)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!preserveTrackedChanges && HasPendingTrackedUserChanges())",
            source,
            StringComparison.Ordinal);
    }

    private static XElement FindButtonByContent(XDocument document, string content)
        => Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals((string?)element.Attribute("Content"), content, StringComparison.Ordinal));

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
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
