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
    public void MainWindow_SelectedInvoiceCustomerInfoFieldsStayFocusableForCopy()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "MainWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "MainViewModel.cs");

        Assert.Contains(
            "[NotifyPropertyChangedFor(nameof(IsPreviewCustomerInfoReadOnly))]",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool IsPreviewCustomerInfoReadOnly => !HasSelectedCustomer;",
            viewModel,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "IsEnabled=\"{Binding HasSelectedCustomer}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding PreviewCustomerName, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource CopyablePreviewCustomerNameTextBoxStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTip=\"거래처명을 선택해 복사할 수 있습니다.\"",
            xaml,
            StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(xaml, "IsReadOnly=\"{Binding IsPreviewCustomerInfoReadOnly}\"") >= 5,
            "전표 선택 상태의 거래처 정보 입력칸은 비활성화하지 말고 읽기전용으로 둬야 텍스트 선택/복사가 가능합니다.");
        Assert.True(
            CountOccurrences(xaml, "IsReadOnlyCaretVisible=\"True\"") >= 5,
            "읽기전용 상태에서도 복사할 위치를 확인할 수 있도록 caret 표시를 유지해야 합니다.");
    }

    [Fact]
    public void RentalBillingWindow_ExplainsDisplayItemsAndIncludedAssetsSeparately()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "RentalBillingWindow.xaml");
        var code = ReadAppFile(appRoot, "Views", "RentalBillingWindow.xaml.cs");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "RentalBillingViewModel.cs");

        AssertContainsAll(
            xaml,
            "청구서 표시 품목(거래명세서 출력 라인)과 내부 포함 장비(실제 청구/전표 대상 자산)를 분리 관리합니다.",
            "청구서 표시 품목 (거래명세서 출력 라인)",
            "실제 청구/전표 대상 자산은 아래 '내부 포함 장비' 목록에서만 결정됩니다.",
            "개별 라인은 같은 모델명끼리 청구서 만들기 시 수량 합산됩니다.",
            "수정할 청구건 선택",
            "개별 청구건 직접 보기",
            "표시품목 요약",
            "선택 표시 라인 삭제",
            "내부 포함 장비 연결",
            "내부장비수",
            "내부 포함 장비 (거래처 설치/연결 자산)",
            "선택 장비 표시품목 추가",
            "전표에 넣을 장비를 선택한 뒤 표시품목에 추가하세요.",
            "이 행은 거래처별 요약입니다.",
            "실제 데이터를 바꾸는 작업은 수정할 청구건을 먼저 선택한 뒤 진행하세요.",
            "BillingAssetCoverageWarning",
            "HasBillingAssetCoverageWarning");

        Assert.DoesNotContain("청구항목 요약", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("그룹을 개별 청구건으로 보기", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"선택 품목 삭제\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"새 장비연결\"", xaml, StringComparison.Ordinal);

        AssertContainsAll(
            code,
            "GetBillingAssetCoverageStartWarning()",
            "거래처별 요약행에서는 장비 연결을 직접 편집할 수 없습니다.",
            "'수정할 청구건 선택'",
            "청구 대상 장비 확인");

        AssertContainsAll(
            viewModel,
            "표시 품목명만 저장해도 자산은 자동 추가되지 않습니다.",
            "내부 포함 장비에서 전표에 넣을 장비를 선택한 뒤 '선택 장비 표시품목 추가'를 누르세요.",
            "청구서 표시 품목에 내부 포함 장비가 없습니다.",
            "청구 프로필 연결 자산",
            "표시품목 포함 자산",
            "거래처별 요약 보기입니다.",
            "수정할 청구건 선택",
            "GetBillingAssetCoverageStartWarning");
    }

    [Fact]
    public void RentalBillingWindow_BillingHistoryDoubleClickOpensLinkedInvoice()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "RentalBillingWindow.xaml");
        var code = ReadAppFile(appRoot, "Views", "RentalBillingWindow.xaml.cs");
        var mainWindow = ReadAppFile(appRoot, "MainWindow.xaml.cs");
        var models = ReadAppFile(appRoot, "Services", "RentalModels.cs");

        AssertContainsAll(
            xaml,
            "청구/입금 내역",
            "MouseDoubleClick=\"BillingHistoryDataGrid_MouseDoubleClick\"",
            "행을 더블클릭하면 입력된 연결 전표 창을 엽니다.");

        AssertContainsAll(
            code,
            "Func<Guid, Window?, Task>? openInvoiceWindowAsync",
            "private void BillingHistoryDataGrid_MouseDoubleClick",
            "OpenBillingHistoryInvoiceAsync(history)",
            "history.InvoiceId is not Guid invoiceId || invoiceId == Guid.Empty",
            "await _openInvoiceWindowAsync(invoiceId, this);");

        Assert.Contains(
            "new RentalBillingWindow(vm, OpenInvoiceWindowAsync)",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "public Guid? InvoiceId { get; init; }",
            models,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RentalCustomerOnboardingWindow_UsesSameDisplayItemAndIncludedAssetTerminology()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "RentalCustomerOnboardingWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "RentalCustomerOnboardingViewModel.cs");

        AssertContainsAll(
            xaml,
            "거래처 등록 → 렌탈 설정 → 청구 설정 → 내부 장비 연결 → 표시품목 구성 순서로 진행합니다.",
            "5. 표시품목/내부 장비 구성",
            "표시품목/장비 연결",
            "표시 라인 추가",
            "선택 표시 라인 삭제",
            "선택 장비를 내부 포함 장비로 연결",
            "청구서 표시 품목은 거래명세서/청구서에 인쇄될 출력 라인입니다.",
            "실제 청구/전표 대상 자산은 표시 라인에 연결한 내부 포함 장비만 적용됩니다.",
            "표시 품목명",
            "내부 포함 장비",
            "표시품목/내부 포함 장비 요약",
            "실제 청구 대상 내부 포함 장비",
            "실제 청구/전표 대상이 됩니다.");

        Assert.DoesNotContain("청구항목 구성", xaml, StringComparison.Ordinal);

        AssertContainsAll(
            viewModel,
            "청구서 표시 품목(거래명세서 출력 라인)을 선택하면 실제 청구할 내부 포함 장비를 연결할 수 있습니다.",
            "현재 표시 라인의 내부 포함 장비로 연결하세요.",
            "표시 품목명만 저장해도 자산은 자동 추가되지 않습니다.",
            "현재 표시 라인의 내부 포함 장비로 연결할 수 있습니다.");
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

    [Fact]
    public void HighRiskMutationCommands_KeepPermissionConcurrencyAndServiceGuards()
    {
        var appRoot = FindDesktopAppRoot();
        var mainViewModel = ReadAppFile(appRoot, "ViewModels", "MainViewModel.cs");
        var salesViewModel = ReadAppFile(appRoot, "ViewModels", "SalesViewModel.cs");
        var paymentViewModel = ReadAppFile(appRoot, "ViewModels", "PaymentViewModel.cs");
        var rentalBillingViewModel = ReadAppFile(appRoot, "ViewModels", "RentalBillingViewModel.cs");
        var rentalAssetViewModel = ReadAppFile(appRoot, "ViewModels", "RentalAssetViewModel.cs");
        var inventoryTransferViewModel = ReadAppFile(appRoot, "ViewModels", "InventoryTransferViewModel.cs");
        var yeonsuDeliveryViewModel = ReadAppFile(appRoot, "ViewModels", "YeonsuDeliveryViewModel.cs");

        AssertContainsAll(
            mainViewModel,
            "var saveResult = await _local.SaveInvoiceAsync(inv, saveContext, _session);",
            "AutoRebaseWhenLatestSavedBySameUser = true,",
            "ExpectedConcurrencyStamp = string.IsNullOrWhiteSpace(_editConcurrencyStamp)",
            "saveResult.ConcurrencyConflict",
            "saveResult.PermissionDenied",
            ".GroupBy(row => row.Id)",
            ".Select(group => group.First())",
            "await _local.DeleteInvoiceAsync(row.Id, _session, row.Revision)",
            "result.ConcurrencyConflict ? \"동시 수정 충돌\" : result.PermissionDenied ? \"권한 없음\" : \"삭제 실패\"",
            "var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));");

        AssertContainsAll(
            salesViewModel,
            "[RelayCommand(CanExecute = nameof(CanConfirmPurchaseReceiving))]",
            "var saveResult = await _local.SaveInvoiceAsync(inv, saveContext, _session);",
            "AutoRebaseWhenLatestSavedBySameUser = true,",
            "ExpectedConcurrencyStamp = string.IsNullOrWhiteSpace(CurrentConcurrencyStamp)",
            "saveResult.ConcurrencyConflict",
            "saveResult.PermissionDenied",
            "var savedInvoice = await _local.GetInvoiceAsync(saveResult.SavedInvoiceId, _session)",
            "await RefreshPaymentSummaryAsync();");

        AssertContainsAll(
            paymentViewModel,
            "public bool CanEditPayments => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.PaymentEdit);",
            "[RelayCommand(CanExecute = nameof(CanSave))]",
            "if (!CanEditPayments)",
            "var result = await _local.SaveTransactionAsync(transaction, _session);",
            "if (result.ConcurrencyConflict)",
            "[RelayCommand(CanExecute = nameof(CanDeleteHistory))]",
            "var result = await _local.DeleteTransactionAsync(target.Id, _session, target.Revision);",
            "await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));",
            "var result = await _local.SaveTransactionAttachmentAsync(",
            "var result = await _local.DeleteTransactionAttachmentAsync(selectedAttachmentId, _session, expectedRevision);");

        AssertContainsAll(
            rentalBillingViewModel,
            "private bool CanEditPayments => _session.HasAdministrativePrivileges ||",
            "private bool CanEditInvoices => _session.HasAdministrativePrivileges ||",
            "public bool CanSave => CanEditRentalProfiles &&",
            "public bool CanStartBillingSelected => SelectedRow is not null &&",
            "public bool CanRegisterSettlementSelected => SelectedRow is not null && HasPersistedSelectedProfile && CanEditCurrentSelection && CanEditPayments",
            "var result = await _rental.SaveBillingProfileAsync(entity, _session, BuildPendingAssetLinkEdits());",
            "var result = await _rental.StartBillingAsync(targetId, ReferenceDate, _session, expectedRevision: expectedRevision);",
            "var result = await _rental.HoldBillingAsync(targetId, ReferenceDate, string.Empty, _session, expectedRevision: expectedRevision);",
            "var result = await _rental.RegisterBillingSettlementAsync(targetId, ReferenceDate, settledAmount, string.Empty, _session, expectedRevision: expectedRevision);",
            "var result = await _rental.DeleteBillingHistoryAsync(",
            "expectedRevision: SelectedRow.Source.Revision,",
            "expectedInvoiceRevision: history.InvoiceRevision);",
            "? await _rental.DeleteBillingProfileAsync(targetProfileId, _session, SelectedRow.Source.Revision)",
            "await _rental.DeleteBillingProfileAsync(row.Source.Id, _session, row.Source.Revision);",
            "var result = await _rental.MarkBillingCompletedAsync(");

        AssertContainsAll(
            rentalAssetViewModel,
            "public bool CanSave => SelectedRow is null",
            "public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection;",
            "[RelayCommand(CanExecute = nameof(CanSave))]",
            "[RelayCommand(CanExecute = nameof(CanDeleteSelected))]",
            "[RelayCommand(CanExecute = nameof(CanDeleteChecked))]",
            "[RelayCommand(CanExecute = nameof(CanReplaceSelected))]",
            "if (!CanDeleteSelected)",
            ".Where(row => !CanEditAssetRow(row))",
            "var result = await _rental.DeleteAssetAsync(targetAssetId, _session, SelectedRow.Source.Revision);",
            "var result = await _rental.DeleteAssetAsync(row.Source.Id, _session, row.Source.Revision);",
            "OriginalAssetRevision = original.Revision,",
            "ReplacementAssetRevision = replacement.Revision,",
            "result = await _rental.ReplaceRentalEquipmentAsync(request, _session);",
            "if (!CanSave)",
            "var result = await _rental.SaveAssetAsync(BuildAsset(snapshot), _session);");

        AssertContainsAll(
            inventoryTransferViewModel,
            "public bool CanDeleteTransfer => HasSavedTransfer && CanCurrentUserDelete;",
            "public bool CanConfirmReceipt => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;",
            "public bool CanRejectTransfer => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;",
            "var result = await _local.DeleteInventoryTransferAsync(targetTransferId, _session, _transferRevision);",
            "if (!CanConfirmReceipt)",
            "var result = await _local.ConfirmInventoryTransferReceiptAsync(",
            "expectedRevision: _transferRevision);",
            "if (!CanRejectTransfer)",
            "var result = await _local.RejectInventoryTransferAsync(TransferId, RejectReason, _session, expectedRevision: _transferRevision);",
            "var result = await _local.SaveInventoryTransferAsync(transfer, _session);",
            "if (result.ConcurrencyConflict && showConflictDialog)");

        AssertContainsAll(
            yeonsuDeliveryViewModel,
            "var invoices = await _local.GetSalesPurchaseLedgerInvoicesAsync(",
            "responsibleOfficeCode: accountOfficeCode,",
            "session: _session);");
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
