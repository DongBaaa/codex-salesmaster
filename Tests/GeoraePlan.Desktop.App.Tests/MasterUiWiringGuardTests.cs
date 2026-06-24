using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MasterUiWiringGuardTests
{
    [Fact]
    public void CustomerManagementWindow_WiresVisibleActionsToGuardedCustomerServices()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "CustomerManagementWindow.xaml");
        var code = ReadAppFile(appRoot, "Views", "CustomerManagementWindow.xaml.cs");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "CustomerManagementViewModel.cs");

        AssertContainsAll(
            xaml,
            "Click=\"CreateCustomerButton_Click\"",
            "Click=\"EditCustomerButton_Click\"",
            "Click=\"DeleteCustomerButton_Click\"",
            "Command=\"{Binding SaveOfficeChangesCommand}\"",
            "SelectionChanged=\"ResponsibleOfficeComboBox_SelectionChanged\"",
            "SelectedItem=\"{Binding ResponsibleOfficeCode, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");

        AssertContainsAll(
            code,
            "new CustomerEditViewModel(_local, _session, _api)",
            "await customerVm.LoadAsync(_vm.SelectedCustomer.Source)",
            "() => _vm.SaveOfficeChangeAsync(row)",
            "await _local.DeleteCustomerAsync(selectedCustomer.Id, _session, selectedCustomer.Source.Revision)",
            "MessageBox.Show(result.Message, \"거래처 삭제\"");

        AssertContainsAll(
            viewModel,
            "await _local.GetCustomersAsync(_session)",
            ".Where(code => readableOfficeCodes.Contains(code))",
            "row.ApplyToSource()",
            "await _local.UpsertCustomerAsync(row.Source, _session)",
            "row.RestoreSavedOfficeCode()",
            "row.AcceptChanges()");
    }

    [Fact]
    public void CustomerEditWindow_SaveButtonsUseCustomerEditViewModelGuardedUpsert()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "CustomerEditWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "CustomerEditViewModel.cs");

        AssertContainsAll(
            xaml,
            "Command=\"{Binding SaveAndNewCommand}\"",
            "Command=\"{Binding SaveCommand}\"",
            "SelectedItem=\"{Binding ResponsibleOfficeCode}\"");

        AssertContainsAll(
            viewModel,
            "private async Task SaveAsync()",
            "private async Task SaveAndNewAsync()",
            "public async Task<bool> TryAutoSaveOnCloseAsync()",
            "ResponsibleOfficeCode = NormalizeOfficeCode(ResponsibleOfficeCode)",
            "var result = await _local.UpsertCustomerAsync(customer, _session)",
            "result.ConcurrencyConflict");
    }

    [Fact]
    public void InventoryWindow_ItemActionsUseScopedItemCommandsAndGuardedServices()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "InventoryWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "InventoryViewModel.cs");

        AssertContainsAll(
            xaml,
            "Command=\"{Binding NewItemCommand}\"",
            "Command=\"{Binding SaveItemCommand}\"",
            "Command=\"{Binding DeleteItemCommand}\"",
            "Command=\"{Binding ShowUsenetOfficeCommand}\"",
            "Command=\"{Binding ShowItworldOfficeCommand}\"",
            "Command=\"{Binding ShowYeonsuOfficeCommand}\"");

        AssertContainsAll(
            viewModel,
            "public bool CanDeleteSelectedItem =>",
            "_local.CanWriteItemScope(SelectedItem.Source, _session)",
            "[RelayCommand(CanExecute = nameof(CanDeleteSelectedItem))]",
            "var deleteResult = await _local.DeleteItemAsync(SelectedItem.Id, _session, SelectedItem.Source.Revision)",
            "if (!CanSaveItems)",
            "catch (UnauthorizedAccessException ex)",
            "await _local.UpsertItemAsync(BuildItem(snapshot), _session, snapshot.PreferredOfficeCode)",
            "var allItems = await _local.GetItemsAsync(_session)");
    }

    [Fact]
    public void EnvironmentSettingsWindow_LegacyAndSelectionMutationsRequireExplicitPermissions()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "EnvironmentSettingsWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "EnvironmentSettingsViewModel.cs");
        var masterViewModel = ReadAppFile(appRoot, "ViewModels", "EnvironmentSettingsViewModel.Masters.cs");

        AssertContainsAll(
            xaml,
            "Text=\"{Binding LegacySourceDbPath, UpdateSourceTrigger=PropertyChanged}\" Margin=\"0,0,8,0\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding SelectLegacySourceDbPathCommand}\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Text=\"{Binding LegacyCustomerExcelPath, UpdateSourceTrigger=PropertyChanged}\" Margin=\"0,0,8,0\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding SelectLegacyCustomerExcelPathCommand}\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Text=\"{Binding LegacyItemExcelPath, UpdateSourceTrigger=PropertyChanged}\" Margin=\"0,0,8,0\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding SelectLegacyItemExcelPathCommand}\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding ExportLegacyDataCommand}\" Margin=\"0,0,8,0\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding ImportLegacyExcelDataCommand}\" Margin=\"0,0,8,0\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"",
            "Command=\"{Binding ExportAndImportLegacyDataCommand}\" IsEnabled=\"{Binding CanManageLegacyMigrationData}\"");

        AssertContainsAll(
            viewModel,
            "public bool CanManageLegacyMigrationData =>",
            "AppPermissionNames.DataBackupRestore",
            "AppPermissionNames.CustomerEdit",
            "AppPermissionNames.ItemEdit",
            "AppPermissionNames.InventoryReset",
            "private bool EnsureCanManageLegacyMigrationData()",
            "if (!EnsureCanManageLegacyMigrationData())");
        Assert.Equal(6, CountOccurrences(viewModel, "if (!EnsureCanManageLegacyMigrationData())"));

        AssertContainsAll(
            masterViewModel,
            "private bool EnsureCanManageSelectionOptions()",
            "환경설정 기준값은 환경설정 편집 권한이 있는 계정만 수정할 수 있습니다.",
            "private async Task SaveCategoryOptionAsync()",
            "private async Task DeleteCategoryOptionAsync()",
            "private async Task SavePriceGradeOptionAsync()",
            "private async Task DeletePriceGradeOptionAsync()",
            "private async Task SaveTradeTypeOptionAsync()",
            "private async Task DeleteTradeTypeOptionAsync()",
            "private async Task SaveItemCategoryOptionAsync()",
            "private async Task DeleteItemCategoryOptionAsync()");
        Assert.Equal(8, CountOccurrences(masterViewModel, "if (!EnsureCanManageSelectionOptions())"));
    }

    [Fact]
    public void EnvironmentSettingsWindow_SyncCredentialScopeActionsStayWithinCurrentSessionScope()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "EnvironmentSettingsWindow.xaml");
        var syncViewModel = ReadAppFile(appRoot, "ViewModels", "EnvironmentSettingsViewModel.Sync.cs");
        var syncService = ReadAppFile(appRoot, "Services", "SyncService.cs");

        AssertContainsAll(
            xaml,
            "ItemsSource=\"{Binding SyncCredentialOfficeOptions}\"",
            "SelectedValue=\"{Binding SyncCredentialOfficeCode}\"",
            "Command=\"{Binding SaveSyncCredentialCommand}\"",
            "Command=\"{Binding DeleteSyncCredentialCommand}\"",
            "Command=\"{Binding SaveSelectedSyncScopeCredentialCommand}\"",
            "Command=\"{Binding RunSelectedSyncScopeSyncCommand}\"",
            "Command=\"{Binding ViewSelectedSyncScopePendingCommand}\"");
        Assert.True(
            CountOccurrences(xaml, "IsEnabled=\"{Binding CanManageSyncCredentials}\"") >= 8,
            "동기화 자격증명 입력/실행 컨트롤은 온라인 로그인 세션에서만 활성화되어야 합니다.");

        AssertContainsAll(
            syncViewModel,
            "public ObservableCollection<DisplayOption> SyncCredentialOfficeOptions { get; } = new();",
            "public bool CanManageSyncCredentials =>",
            "private PendingSyncSummary FilterPendingSyncSummaryForCurrentSession(PendingSyncSummary pendingSummary)",
            ".Where(bucket => CanManageSyncScope(bucket.ScopeKey))",
            ".Where(credential => CanManageSyncCredentialOffice(credential.OfficeCode))",
            "private bool CanManageSyncCredentialOffice(string? officeCode)",
            "private bool CanManageSyncScope(string? scopeKey)",
            "private bool EnsureCanManageSyncCredentialOffice(string? officeCode, string action)",
            "private bool EnsureCanManageSyncScope(SyncScopeStatusRow? target, string action)",
            "if (!EnsureCanManageSyncCredentialOffice(officeCode, \"저장\"))",
            "if (!EnsureCanManageSyncCredentialOffice(target.OfficeCode, \"삭제\"))",
            "if (!EnsureCanManageSyncScope(target, \"계정 저장\"))",
            "if (!EnsureCanManageSyncScope(target, \"실행\"))",
            "if (!EnsureCanManageSyncScope(target, \"확인\"))");

        AssertContainsAll(
            syncService,
            "if (string.Equals(scopeKey, \"SHARED\", StringComparison.OrdinalIgnoreCase))",
            "if (!blockingReason.IsCurrentScope)",
            "SetStatus(\"공용 마스터 범위를 동기화하는 중...\");");
    }

    [Fact]
    public void HighRiskWorkflowWindows_CommandBindingsResolveToViewModelCommands()
    {
        var appRoot = FindDesktopAppRoot();
        var targets = new[]
        {
            new WpfCommandBindingTarget(
                ["MainWindow.xaml"],
                [
                    ["ViewModels", "MainViewModel.cs"],
                    ["ViewModels", "MainViewModel.Update.cs"],
                    ["ViewModels", "MainViewModel.CustomerContracts.cs"],
                    ["ViewModels", "MainViewModel.SyncDiagnostics.cs"],
                    ["ViewModels", "MainViewModel.BusinessDatabase.cs"],
                    ["ViewModels", "MainViewModel.RecycleBin.cs"],
                    ["ViewModels", "MainViewModel.ContractAlerts.cs"]
                ]),
            new WpfCommandBindingTarget(["Views", "SalesWindow.xaml"], [["ViewModels", "SalesViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "PaymentWindow.xaml"], [["ViewModels", "PaymentViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "PeriodLedgerWindow.xaml"], [["ViewModels", "PeriodLedgerViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "PrintEditWindow.xaml"], [["ViewModels", "PrintEditViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "CustomerEditWindow.xaml"], [["ViewModels", "CustomerEditViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "CustomerManagementWindow.xaml"], [["ViewModels", "CustomerManagementViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "InventoryWindow.xaml"], [["ViewModels", "InventoryViewModel.cs"]]),
            new WpfCommandBindingTarget(
                ["Views", "EnvironmentSettingsWindow.xaml"],
                [
                    ["ViewModels", "EnvironmentSettingsViewModel.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.Backup.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.BusinessDatabase.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.Masters.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.RecycleBin.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.Sync.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.TenantPolicies.cs"],
                    ["ViewModels", "EnvironmentSettingsViewModel.Update.cs"]
                ]),
            new WpfCommandBindingTarget(
                ["Views", "RentalBillingWindow.xaml"],
                [
                    ["ViewModels", "RentalBillingViewModel.cs"],
                    ["ViewModels", "RentalBillingViewModel.AutoSave.cs"]
                ]),
            new WpfCommandBindingTarget(["Views", "RentalAssetWindow.xaml"], [["ViewModels", "RentalAssetViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "InventoryTransferWindow.xaml"], [["ViewModels", "InventoryTransferViewModel.cs"]]),
            new WpfCommandBindingTarget(
                ["Views", "RentalCustomerOnboardingWindow.xaml"],
                [
                    ["ViewModels", "RentalCustomerOnboardingViewModel.cs"],
                    ["ViewModels", "RentalCustomerOnboardingViewModel.AutoSave.cs"]
                ]),
            new WpfCommandBindingTarget(["Views", "RentalContractEditorWindow.xaml"], [["ViewModels", "RentalContractEditorViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "RentalSettingsWindow.xaml"], [["ViewModels", "RentalSettingsViewModel.cs"]]),
            new WpfCommandBindingTarget(["Views", "YeonsuDeliveryWindow.xaml"], [["ViewModels", "YeonsuDeliveryViewModel.cs"]]),
            new WpfCommandBindingTarget(
                ["Views", "SyncDiagnosticsWindow.xaml"],
                [
                    ["ViewModels", "SyncDiagnosticsViewModel.cs"],
                    ["ViewModels", "SyncDiagnosticsViewModel.Export.cs"],
                    ["ViewModels", "SyncDiagnosticsViewModel.Integrity.cs"]
                ])
        };

        foreach (var target in targets)
        {
            var xaml = ReadAppFile(appRoot, target.XamlPath);
            var boundCommands = WpfCommandBindingRegex.Matches(xaml)
                .Select(match => match.Groups["command"].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(command => command, StringComparer.Ordinal)
                .ToArray();
            var viewModelCommands = target.ViewModelPaths
                .Select(path => ReadAppFile(appRoot, path))
                .SelectMany(ExtractViewModelCommandNames)
                .ToHashSet(StringComparer.Ordinal);
            var missingCommands = boundCommands
                .Where(command => !viewModelCommands.Contains(command))
                .ToArray();

            Assert.NotEmpty(boundCommands);
            Assert.True(
                missingCommands.Length == 0,
                $"{string.Join(Path.DirectorySeparatorChar, target.XamlPath)} has unresolved Command binding(s): {string.Join(", ", missingCommands)}");
        }
    }

    private static void AssertContainsAll(string source, params string[] expectedMarkers)
    {
        foreach (var marker in expectedMarkers)
            Assert.Contains(marker, source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string marker)
        => source.Split(marker, StringSplitOptions.None).Length - 1;

    private static string ReadAppFile(string appRoot, params string[] pathParts)
        => File.ReadAllText(Path.Combine([appRoot, .. pathParts]));

    private static IEnumerable<string> ExtractViewModelCommandNames(string source)
    {
        foreach (Match match in RelayCommandMethodRegex.Matches(source))
        {
            var methodName = match.Groups["method"].Value;
            var commandName = methodName.EndsWith("Async", StringComparison.Ordinal)
                ? methodName[..^"Async".Length]
                : methodName;
            yield return $"{commandName}Command";
        }

        foreach (Match match in ExplicitCommandPropertyRegex.Matches(source))
            yield return match.Groups["command"].Value;
    }

    private static string FindDesktopAppRoot()
    {
        var root = FindRepositoryRoot();
        return Directory.EnumerateDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App", SearchOption.TopDirectoryOnly)
            .Single();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.EnumerateFiles(directory.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record WpfCommandBindingTarget(string[] XamlPath, string[][] ViewModelPaths);

    private static readonly Regex WpfCommandBindingRegex = new(
        "Command=\"\\{Binding\\s+(?:DataContext\\.)?(?<command>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RelayCommandMethodRegex = new(
        @"\[RelayCommand[^\]]*\]\s*(?:\r?\n\s*(?:\[[^\]]+\]\s*)?)*\s*(?:private|public|protected|internal)\s+(?:async\s+)?[\w<>,?\[\]\s.]+\s+(?<method>[A-Z][A-Za-z0-9_]*)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExplicitCommandPropertyRegex = new(
        @"public\s+[\w<>,?\[\]\s.]+Command\s+(?<command>[A-Z][A-Za-z0-9_]*Command)\s*\{",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
