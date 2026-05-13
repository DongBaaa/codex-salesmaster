using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingViewModel : ObservableObject
{
    private const string AllOption = "전체";

    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly UiDebouncer _searchDebouncer = new();
    private CancellationTokenSource? _candidateAssetsLoadCts;
    private CancellationTokenSource? _contractDateRefreshCts;
    private Task? _candidateAssetsLoadTask;
    private bool _suppressFilterReload;
    private bool _pendingFilterReload;
    private bool _suppressCandidateAssetSelectionChanges;
    private bool _suppressIncludedAssetRepresentativeChanges;
    private bool _suppressContractDateSynchronization;
    private bool _suppressTemplateItemChangeHandling;
    private bool _updatingTemplateDerivedValues;
    private readonly List<RentalBillingAssetOption> _includedAssetPool = new();
    private readonly List<RentalBillingAssetOption> _candidateAssetPool = new();
    private readonly Dictionary<Guid, RentalBillingAssetLinkEdit> _pendingAssetLinkEdits = new();
    private string _selectedRowBaselineSignature = string.Empty;
    private long _editRevision;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedOfficeFilter;
    [ObservableProperty] private string _selectedStatusFilter = AllOption;
    [ObservableProperty] private bool _dueOnly;
    [ObservableProperty] private bool _showIndividualProfiles;
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 청구 대상을 불러오는 중입니다.";
    [ObservableProperty] private RentalBillingViewRow? _selectedRow;
    [ObservableProperty] private RentalBillingTemplateEditorItem? _selectedTemplateItem;
    [ObservableProperty] private RentalBillingAssetOption? _selectedIncludedAsset;
    [ObservableProperty] private RentalAssetAssignmentHistoryViewItem? _selectedIncludedAssetAssignmentHistory;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private Guid? _editCustomerId;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private string _editBusinessNumber = string.Empty;
    [ObservableProperty] private string _editInstallLocation = string.Empty;
    [ObservableProperty] private string _editItemName = string.Empty;
    [ObservableProperty] private string _editBillingType = "묶음";
    [ObservableProperty] private string _editBillingAdvanceMode = "후불";
    [ObservableProperty] private string _editOfficeCode = string.Empty;
    [ObservableProperty] private string _editBillingMethod = string.Empty;
    [ObservableProperty] private string _editBillingStatus = "예정";
    [ObservableProperty] private string _editSettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
    [ObservableProperty] private string _editCompletionStatus = PaymentFlowConstants.CompletionPending;
    [ObservableProperty] private string _editEmail = string.Empty;
    [ObservableProperty] private int _editBillingDay = 25;
    [ObservableProperty] private string _editBillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
    [ObservableProperty] private int _editBillingCycleMonths = 1;
    [ObservableProperty] private int _editBillingAnchorMonth = 3;
    [ObservableProperty] private string _editDocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    [ObservableProperty] private int _editDocumentLeadDays;
    [ObservableProperty] private decimal _editMonthlyAmount;
    [ObservableProperty] private decimal _editDepositAmount;
    [ObservableProperty] private decimal _editSettledAmount;
    [ObservableProperty] private decimal _editOutstandingAmount;
    [ObservableProperty] private bool _editRequiresFollowUp;
    [ObservableProperty] private string _editSubmissionDocuments = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private string _templateSummary = string.Empty;
    [ObservableProperty] private string _assetCandidateSummary = string.Empty;
    [ObservableProperty] private bool _linkAssetsLater;
    [ObservableProperty] private DateTime? _editBillingAnchorDate;
    [ObservableProperty] private DateTime? _editBillingStartDate;
    [ObservableProperty] private DateTime? _editContractDate;
    [ObservableProperty] private DateTime? _editContractStartDate;
    [ObservableProperty] private DateTime? _editContractEndDate;
    [ObservableProperty] private DateTime? _editLastBilledDate;
    [ObservableProperty] private DateTime? _editLastSettledDate;
    [ObservableProperty] private bool _editIsActive = true;
    [ObservableProperty] private string _billingSchedulePreviewText = "청구일 규칙을 설정하면 다음 청구일이 표시됩니다.";
    [ObservableProperty] private string _documentIssuePreviewText = "서류 발송 규칙을 설정하면 예상 발송일이 표시됩니다.";
    [ObservableProperty] private string _applySelectedAssetsHint = "새 장비연결로 설치현황 자산을 골라 현재 청구 품목에 연결할 수 있습니다.";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _dueCount;
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _partialSettlementCount;
    [ObservableProperty] private decimal _totalOutstandingAmount;

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<DisplayOption> EditOfficeOptions { get; } = new();
    public ObservableCollection<string> StatusOptions { get; } = new();
    public ObservableCollection<string> SettlementStatusOptions { get; } = new();
    public ObservableCollection<string> CompletionStatusOptions { get; } = new();
    public ObservableCollection<string> BillingMethodOptions { get; } = new();
    public ObservableCollection<string> BillingTypeOptions { get; } = new();
    public ObservableCollection<string> BillingLineModeOptions { get; } = new();
    public ObservableCollection<string> BillingAdvanceModeOptions { get; } = new();
    public ObservableCollection<string> BillingDayModeOptions { get; } = new();
    public ObservableCollection<int> BillingAnchorMonthOptions { get; } = new();
    public ObservableCollection<string> DocumentIssueModeOptions { get; } = new();
    public ObservableCollection<RentalBillingViewRow> Rows { get; } = new();
    public ObservableCollection<RentalBillingTemplateEditorItem> TemplateItems { get; } = new();
    public ObservableCollection<RentalBillingAssetOption> IncludedAssets { get; } = new();
    public ObservableCollection<RentalAssetAssignmentHistoryViewItem> IncludedAssetAssignmentHistories { get; } = new();
    public ObservableCollection<RentalBillingAssetOption> CandidateAssets { get; } = new();

    public bool CanViewAll => _session.HasAdministrativePrivileges ||
                              _session.HasGlobalDataScope ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalEditAll);
    public bool CanManageAll => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalEditAll);
    public bool CanSave => SelectedRow is null || (CanEditCurrentSelection && CanEditSelectedRowInEditor);
    public bool IsCustomerGroupSelection => SelectedRow?.IsAggregateRow == true;
    public bool CanEditBillingProfileDetails => SelectedRow is null || !SelectedRow.IsAggregateRow;
    public bool CanOpenAssetLinkDialog => CanEditBillingProfileDetails && !string.IsNullOrWhiteSpace(EditCustomerName);
    public bool CanExpandSelectedSummary => SelectedRow?.IsAggregateRow == true && !ShowIndividualProfiles;
    public bool CanStartBillingSelected => SelectedRow is not null &&
                                           CanAccessCurrentSelection &&
                                           (SelectedRow.IsAggregateRow
                                               ? SelectedRow.GroupedPersistedProfileIds.Any(id => id != Guid.Empty)
                                               : HasPersistedSelectedProfile);
    public bool CanHoldSelected => SelectedRow is not null && HasPersistedSelectedProfile && CanEditCurrentSelection && !SelectedRow.IsAggregateRow;
    public bool CanRegisterSettlementSelected => SelectedRow is not null && HasPersistedSelectedProfile && CanEditCurrentSelection && !SelectedRow.IsAggregateRow;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection && !SelectedRow.IsAggregateRow;
    public bool CanMarkCompletedSelected => SelectedRow is not null &&
                                            HasPersistedSelectedProfile &&
                                            CanEditCurrentSelection &&
                                            !SelectedRow.IsAggregateRow &&
                                            SelectedRow.OutstandingAmount <= 0m;
    public bool CanRemoveTemplateItem => CanEditBillingProfileDetails && SelectedTemplateItem is not null;
    public bool CanRemoveIncludedAsset => CanEditBillingProfileDetails &&
                                          SelectedTemplateItem is not null &&
                                          SelectedIncludedAsset is not null &&
                                          SelectedIncludedAsset.AssetId != Guid.Empty;
    public bool CanSetRepresentativeAsset => CanEditBillingProfileDetails &&
                                             SelectedTemplateItem is not null &&
                                             SelectedIncludedAsset is not null &&
                                             SelectedIncludedAsset.AssetId != Guid.Empty &&
                                             IsTemplateItemBundleMode(SelectedTemplateItem) &&
                                             SelectedTemplateItem.IncludedAssetIds.Contains(SelectedIncludedAsset.AssetId);
    public bool CanAddIncludedAssetAssignmentHistory => CanEditBillingProfileDetails &&
                                                        SelectedIncludedAsset is not null &&
                                                        SelectedIncludedAsset.AssetId != Guid.Empty &&
                                                        CanEditCurrentSelection;
    public bool CanEditIncludedAssetAssignmentHistory => CanAddIncludedAssetAssignmentHistory &&
                                                         SelectedIncludedAssetAssignmentHistory is not null;
    public bool CanDeleteIncludedAssetAssignmentHistory => CanEditIncludedAssetAssignmentHistory;
    public bool CanApplySelectedAssets => CanEditBillingProfileDetails && SelectedTemplateItem is not null && CandidateAssets.Any(asset => asset.IsSelected);
    public bool CanOpenCustomerContract => EditCustomerId.HasValue && EditCustomerId.Value != Guid.Empty;
    public bool IsFixedBillingDayMode => string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal);
    public bool IsDocumentLeadDaysVisible => string.Equals(EditDocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate, StringComparison.Ordinal);
    public bool IsContractDateMissing => !EditContractDate.HasValue;
    public bool ShouldShowContractDateWarning => IsContractDateMissing && (EditCustomerId.HasValue || !string.IsNullOrWhiteSpace(EditCustomerName) || SelectedRow is not null);
    public string ContractDateWarningMessage => "계약 체결일을 확인할 수 없습니다. 저장은 가능하지만 청구 기준 검토가 필요합니다.";
    public LocalStateService LocalStateService => _local;
    public RentalStateService RentalStateService => _rental;
    public SessionState SessionState => _session;
    public Guid? InvoiceToOpenAfterClose { get; private set; }

    private bool CanAccessCurrentSelection => SelectedRow is null || CanOperateScope(
        ResolveProfileOfficeCode(SelectedRow.Source, _session.OfficeCode));

    private bool HasPersistedSelectedProfile => SelectedRow?.HasPersistedProfile == true;
    private bool CanEditSelectedRowInEditor => SelectedRow is null || !SelectedRow.IsAggregateRow;

    private bool CanEditCurrentSelection => SelectedRow is null || CanOperateScope(
        ResolveProfileOfficeCode(SelectedRow.Source, _session.OfficeCode));

    public RentalBillingViewModel(RentalStateService rental, LocalStateService local, SessionState session)
    {
        _rental = rental;
        _local = local;
        _session = session;

        StatusOptions.Add(AllOption);
        StatusOptions.Add("활성");
        StatusOptions.Add("비활성");
        StatusOptions.Add("예정");
        StatusOptions.Add("청구중");
        StatusOptions.Add("보류");
        StatusOptions.Add("완료");
        StatusOptions.Add("미수");
        StatusOptions.Add("미연결");

        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusUnpaid);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusPending);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusPartial);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusConfirmed);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusCardPending);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusCardApproved);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusCmsPending);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusCmsFailed);
        SettlementStatusOptions.Add(PaymentFlowConstants.SettlementStatusRefunded);

        CompletionStatusOptions.Add(PaymentFlowConstants.CompletionPending);
        CompletionStatusOptions.Add(PaymentFlowConstants.CompletionDone);

        BillingMethodOptions.Add("전자세금계산서");
        BillingMethodOptions.Add("CMS");
        BillingMethodOptions.Add("현금");
        BillingMethodOptions.Add("카드");

        BillingTypeOptions.Add("묶음");
        BillingTypeOptions.Add("개별");
        BillingTypeOptions.Add("혼합");

        BillingLineModeOptions.Add("묶음");
        BillingLineModeOptions.Add("개별");

        BillingAdvanceModeOptions.Add("후불");
        BillingAdvanceModeOptions.Add("선불");

        BillingDayModeOptions.Add(RentalBillingScheduleRules.BillingDayModeFixedDay);
        BillingDayModeOptions.Add(RentalBillingScheduleRules.BillingDayModeEndOfMonth);

        for (var month = 1; month <= 12; month++)
            BillingAnchorMonthOptions.Add(month);

        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModePreviousBusinessDay);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModePreviousMonthEnd);

        InitializeAutoSave();
    }

    partial void OnSearchTextChanged(string value) => RequestFilterReload();
    partial void OnSelectedOfficeFilterChanged(DisplayOption? value) => RequestFilterReload();
    partial void OnSelectedStatusFilterChanged(string value) => RequestFilterReload();
    partial void OnDueOnlyChanged(bool value) => RequestFilterReload();
    partial void OnShowIndividualProfilesChanged(bool value)
    {
        NotifySelectionActionState();
        RequestFilterReload();
    }
    partial void OnReferenceDateChanged(DateOnly value) => RequestFilterReload();
    partial void OnEditCustomerNameChanged(string value)
    {
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(CanOpenAssetLinkDialog));
    }
    partial void OnEditCustomerIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(CanOpenCustomerContract));
        OpenCustomerContractCommand.NotifyCanExecuteChanged();
    }
    partial void OnEditSettledAmountChanged(decimal value) => EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - value);
    partial void OnEditBillingDayChanged(int value) => UpdateTemplateDerivedValues();
    partial void OnEditBillingDayModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFixedBillingDayMode));
        UpdateTemplateDerivedValues();
    }
    partial void OnEditBillingAnchorMonthChanged(int value) => UpdateTemplateDerivedValues();
    partial void OnEditDocumentIssueModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDocumentLeadDaysVisible));
        UpdateTemplateDerivedValues();
    }
    partial void OnEditDocumentLeadDaysChanged(int value) => UpdateTemplateDerivedValues();
    partial void OnEditBillingStartDateChanged(DateTime? value)
    {
        if (_suppressContractDateSynchronization)
            return;

        UpdateTemplateDerivedValues();
    }
    partial void OnEditBillingAdvanceModeChanged(string value) => UpdateTemplateDerivedValues();
    partial void OnEditContractStartDateChanged(DateTime? value) => UpdateTemplateDerivedValues();
    partial void OnEditContractDateChanged(DateTime? value)
    {
        if (!_suppressContractDateSynchronization)
        {
            _suppressContractDateSynchronization = true;
            try
            {
                EditBillingStartDate = value;
            }
            finally
            {
                _suppressContractDateSynchronization = false;
            }
        }

        OnPropertyChanged(nameof(IsContractDateMissing));
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(ContractDateWarningMessage));
        UpdateTemplateDerivedValues();
    }
    partial void OnEditLastBilledDateChanged(DateTime? value) => UpdateTemplateDerivedValues();
    partial void OnEditBillingCycleMonthsChanged(int value)
    {
        EditBillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(value);
        EditBillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            EditBillingCycleMonths,
            EditBillingAnchorMonth,
            ToDateOnly(EditBillingAnchorDate),
            ToDateOnly(EditBillingStartDate),
            ToDateOnly(EditContractStartDate),
            ToDateOnly(EditContractDate),
            ToDateOnly(EditLastBilledDate),
            GetBillingReferenceDate());
        UpdateTemplateDerivedValues();
    }
    partial void OnEditBillingTypeChanged(string value)
    {
        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        UpdateTemplateDerivedValues();
    }
    partial void OnSelectedTemplateItemChanged(RentalBillingTemplateEditorItem? value)
    {
        NormalizeTemplateRepresentativeAssets();
        SyncAssetSelectionFromTemplate();
        SyncIncludedAssetsFromTemplate();
        UpdateTemplateDerivedValues();
        OnPropertyChanged(nameof(CanRemoveTemplateItem));
        RemoveTemplateItemCommand.NotifyCanExecuteChanged();
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        OnPropertyChanged(nameof(CanApplySelectedAssets));
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIncludedAssetChanged(RentalBillingAssetOption? value)
    {
        SelectedIncludedAssetAssignmentHistory = null;
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        NotifyIncludedAssetAssignmentHistoryCommandState();
        UiTaskHelper.Forget(
            LoadIncludedAssetAssignmentHistoriesAsync(value?.AssetId ?? Guid.Empty),
            "RENTAL",
            "청구 포함 장비 임대 이력 조회",
            ex => StatusMessage = $"선택 장비 임대 이력을 불러오지 못했습니다. {ex.Message}");
    }

    partial void OnSelectedIncludedAssetAssignmentHistoryChanged(RentalAssetAssignmentHistoryViewItem? value)
        => NotifyIncludedAssetAssignmentHistoryCommandState();

    private async Task LoadIncludedAssetAssignmentHistoriesAsync(Guid assetId)
    {
        if (assetId == Guid.Empty)
        {
            IncludedAssetAssignmentHistories.Clear();
            SelectedIncludedAssetAssignmentHistory = null;
            NotifyIncludedAssetAssignmentHistoryCommandState();
            return;
        }

        var histories = await _rental.GetAssetAssignmentHistoriesAsync(assetId);
        if (SelectedIncludedAsset?.AssetId != assetId)
            return;

        var selectedHistoryId = SelectedIncludedAssetAssignmentHistory?.HistoryId;
        IncludedAssetAssignmentHistories.Clear();
        foreach (var history in histories)
            IncludedAssetAssignmentHistories.Add(history);
        SelectedIncludedAssetAssignmentHistory = selectedHistoryId.HasValue
            ? IncludedAssetAssignmentHistories.FirstOrDefault(history => history.HistoryId == selectedHistoryId.Value)
            : IncludedAssetAssignmentHistories.FirstOrDefault();
        NotifyIncludedAssetAssignmentHistoryCommandState();
    }

    public async Task LoadAsync()
    {
        await _rental.CleanupLegacyAssignedUsernamesAsync();
        await ReloadFiltersAsync();
        await ReloadAsync();

        BeginAutoSaveSuppression();
        try
        {
            if (!await RestoreAutoSaveDraftAsync())
                NewProfile();
        }
        finally
        {
            EndAutoSaveSuppression();
        }
    }

    public async Task LoadAndSelectProfileAsync(Guid profileId)
    {
        await LoadAsync();
        SelectRow(profileId);
        StatusMessage = SelectedRow is null
            ? "점검 항목의 청구 프로필을 목록에서 찾지 못했습니다. 필터, 권한, 삭제 상태를 확인하세요."
            : "운영 점검 항목의 청구 프로필을 선택했습니다. 표시 품목, 월 기준금액, 연결 자산을 확인한 뒤 저장하세요.";
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy)
        {
            _pendingFilterReload = true;
            return;
        }

        do
        {
            _pendingFilterReload = false;
            var selectedId = SelectedRow?.SelectionId;
            IsBusy = true;
            try
            {
                var rows = await _rental.GetBillingRowsAsync(new RentalBillingFilter
                {
                    SearchText = SearchText,
                    OfficeCode = SelectedOfficeFilter?.Value == AllOption ? string.Empty : SelectedOfficeFilter?.Value ?? string.Empty,
                    Status = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter,
                    DueOnly = DueOnly,
                    ExpandCustomerSummaryRows = ShowIndividualProfiles,
                    ReferenceDate = ReferenceDate
                }, _session);

                Rows.Clear();
                foreach (var row in rows)
                    Rows.Add(row);

                TotalCount = rows.Count;
                DueCount = rows.Count(row => row.DaysRemaining.HasValue && row.DaysRemaining.Value <= 0);
                IssueCount = rows.Count(row => row.HasDataIssue);
                CompletedCount = rows.Count(row => string.Equals(row.CompletionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase));
                PartialSettlementCount = rows.Count(row => string.Equals(row.SettlementStatus, PaymentFlowConstants.SettlementStatusPartial, StringComparison.OrdinalIgnoreCase));
                TotalOutstandingAmount = rows.Sum(row => row.OutstandingAmount);
                var unlinkedCount = rows.Count(row => !row.HasPersistedProfile);

                StatusMessage = rows.Count == 0
                    ? "조건에 맞는 렌탈 청구 대상이 없습니다."
                    : unlinkedCount > 0
                        ? $"렌탈 청구 {rows.Count:N0}건을 조회했습니다. 미연결 설치처 {unlinkedCount:N0}건이 포함되어 있습니다."
                        : ShowIndividualProfiles
                            ? $"렌탈 청구 프로필 {rows.Count:N0}건을 개별 조회했습니다."
                            : $"렌탈 청구 {rows.Count:N0}건을 조회했습니다.";

                if (selectedId.HasValue)
                {
                    SelectRow(selectedId.Value);
                    if (SelectedRow is null)
                        NewProfile();
                }
            }
            finally
            {
                IsBusy = false;
            }
        }
        while (_pendingFilterReload);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (TryRejectAggregateSelection("저장"))
            return;

        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        UpdateTemplateDerivedValues();

        if (!TryValidateTemplateConfiguration(out var validationMessage))
        {
            StatusMessage = validationMessage;
            return;
        }

        await RefreshContractDateFromSourcesAsync(preserveExistingValue: true);
        UpdateTemplateDerivedValues();

        if (!EditContractDate.HasValue)
        {
            MessageBox.Show(
                "계약 체결일을 확인할 수 없습니다. 저장은 가능하지만 청구 기준 검토가 필요합니다.",
                "렌탈 청구 저장",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            EditOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));
        var contractDate = ToDateOnly(EditContractDate);
        var templateModels = ToTemplateModels();
        var effectiveBillingType = ResolveProfileBillingTypeFromTemplateItems(templateModels, EditBillingType);

        var entity = new LocalRentalBillingProfile
        {
            Id = EditId,
            Revision = _editRevision,
            CustomerId = EditCustomerId,
            CustomerName = EditCustomerName,
            BusinessNumber = EditBusinessNumber,
            InstallSiteName = EditInstallLocation,
            ItemName = EditItemName,
            BillingType = effectiveBillingType,
            BillingAdvanceMode = EditBillingAdvanceMode,
            ManagementCompanyCode = officeCode,
            BillingMethod = EditBillingMethod,
            BillingStatus = EditBillingStatus,
            SettlementStatus = EditSettlementStatus,
            CompletionStatus = EditCompletionStatus,
            Email = EditEmail,
            BillingDay = EditBillingDay,
            BillingDayMode = EditBillingDayMode,
            BillingCycleMonths = Math.Max(1, EditBillingCycleMonths),
            BillingAnchorMonth = EditBillingAnchorMonth,
            DocumentIssueMode = EditDocumentIssueMode,
            DocumentLeadDays = EditDocumentLeadDays,
            MonthlyAmount = EditMonthlyAmount,
            DepositAmount = EditDepositAmount,
            SettledAmount = EditSettledAmount,
            OutstandingAmount = EditOutstandingAmount,
            RequiresFollowUp = EditRequiresFollowUp,
            SubmissionDocuments = EditSubmissionDocuments,
            Notes = EditNotes,
            ResponsibleOfficeCode = officeCode,
            BillingAnchorDate = ToDateOnly(EditBillingAnchorDate),
            BillingStartDate = contractDate,
            ContractDate = contractDate,
            ContractStartDate = ToDateOnly(EditContractStartDate),
            ContractEndDate = ToDateOnly(EditContractEndDate),
            LastBilledDate = ToDateOnly(EditLastBilledDate),
            LastSettledDate = ToDateOnly(EditLastSettledDate),
            IsActive = EditIsActive,
            BillingTemplateJson = _rental.SerializeBillingTemplateItems(templateModels)
        };

        var result = await _rental.SaveBillingProfileAsync(entity, _session, BuildPendingAssetLinkEdits());
        if (!result.Success)
        {
            StatusMessage = result.ConcurrencyConflict
                ? $"{result.Message} 현재 입력 내용은 유지됩니다. 최신 목록을 확인한 뒤 다시 저장하세요."
                : result.Message;
            if (result.ConcurrencyConflict)
            {
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ClearAutoSaveDraftAsync();
        await ReloadAsync();
        SelectRow(result.EntityId);
    }

    [RelayCommand]
    private async Task StartBillingAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "청구를 시작할 대상을 선택하세요.";
            return;
        }

        if (SelectedRow.IsAggregateRow)
        {
            await StartAggregateBillingAsync(SelectedRow);
            return;
        }

        if (TryRejectAggregateSelection("청구 시작"))
            return;

        if (!SelectedRow.HasPersistedProfile)
        {
            StatusMessage = "청구 프로필이 없는 설치처입니다. 저장 후 다시 청구를 시작하세요.";
            return;
        }

        if (HasUnsavedSelectedRowChanges())
        {
            StatusMessage = "현재 청구 설정 편집 내용이 저장되지 않았습니다. 먼저 저장한 뒤 청구를 시작하세요.";
            return;
        }

        InvoiceToOpenAfterClose = null;
        var targetId = SelectedRow.Source.Id;
        var expectedRevision = SelectedRow.Source.Revision;
        var result = await _rental.StartBillingAsync(targetId, ReferenceDate, _session, expectedRevision: expectedRevision);
        StatusMessage = result.Message;
        if (!result.Success)
        {
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRow(targetId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        if (result.RelatedEntityId != Guid.Empty)
            InvoiceToOpenAfterClose = result.RelatedEntityId;

        await ClearAutoSaveDraftAsync();
        await ReloadAsync();
        SelectRow(targetId);
    }

    private async Task StartAggregateBillingAsync(RentalBillingViewRow aggregateRow)
    {
        var targetIds = aggregateRow.GroupedPersistedProfileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (targetIds.Count == 0)
        {
            StatusMessage = "거래처 그룹에 청구 가능한 개별 프로필이 없습니다. 개별 청구건 보기로 프로필을 생성/저장한 뒤 다시 시도하세요.";
            return;
        }

        InvoiceToOpenAfterClose = null;
        var successCount = 0;
        var relatedInvoiceIds = new List<Guid>();
        var failureMessages = new List<string>();

        foreach (var targetId in targetIds)
        {
            var expectedRevision = aggregateRow.GroupedProfileRevisions.TryGetValue(targetId, out var revision)
                ? revision
                : (long?)null;
            var result = await _rental.StartBillingAsync(targetId, ReferenceDate, _session, expectedRevision: expectedRevision);
            if (result.Success)
            {
                successCount++;
                if (result.RelatedEntityId != Guid.Empty)
                    relatedInvoiceIds.Add(result.RelatedEntityId);
                continue;
            }

            failureMessages.Add(string.IsNullOrWhiteSpace(result.Message)
                ? "알 수 없는 오류"
                : result.Message.Trim());
        }

        var distinctInvoiceIds = relatedInvoiceIds.Distinct().ToList();
        if (distinctInvoiceIds.Count == 1)
            InvoiceToOpenAfterClose = distinctInvoiceIds[0];

        if (successCount > 0)
            await ClearAutoSaveDraftAsync();

        var aggregateSelectionId = aggregateRow.SelectionId;
        await ReloadAsync();
        SelectRow(aggregateSelectionId);

        var skippedUnlinkedText = aggregateRow.GroupedUnlinkedAssetCount > 0
            ? $" / 프로필 생성 필요 설치처 {aggregateRow.GroupedUnlinkedAssetCount:N0}건 제외"
            : string.Empty;

        if (failureMessages.Count == 0)
        {
            StatusMessage = $"거래처 그룹의 개별 청구 프로필 {successCount:N0}건을 청구 시작했습니다.{skippedUnlinkedText}";
            return;
        }

        var failureSummary = string.Join(" | ", failureMessages.Distinct().Take(3));
        StatusMessage = successCount > 0
            ? $"거래처 그룹 청구 일부 완료: 성공 {successCount:N0}건 / 실패 {failureMessages.Count:N0}건{skippedUnlinkedText} - {failureSummary}"
            : $"거래처 그룹 청구 시작 실패 {failureMessages.Count:N0}건{skippedUnlinkedText} - {failureSummary}";
    }

    [RelayCommand]
    private async Task HoldBillingAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "보류할 대상을 선택하세요.";
            return;
        }

        if (TryRejectAggregateSelection("보류 처리"))
            return;

        if (!SelectedRow.HasPersistedProfile)
        {
            StatusMessage = "청구 프로필이 없는 설치처입니다. 저장 후 다시 보류 처리하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var expectedRevision = SelectedRow.Source.Revision;
        var result = await _rental.HoldBillingAsync(targetId, string.Empty, _session, expectedRevision: expectedRevision);
        StatusMessage = result.Message;
        if (!result.Success)
        {
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRow(targetId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ReloadAsync();
        SelectRow(targetId);
    }

    [RelayCommand]
    private async Task RegisterSettlementAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "수금을 등록할 대상을 선택하세요.";
            return;
        }

        if (TryRejectAggregateSelection("수금 등록"))
            return;

        if (!SelectedRow.HasPersistedProfile)
        {
            StatusMessage = "청구 프로필이 없는 설치처입니다. 저장 후 다시 수금을 등록하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var settledAmount = EditSettledAmount > 0m ? EditSettledAmount : (decimal?)null;
        var expectedRevision = SelectedRow.Source.Revision;
        var result = await _rental.RegisterBillingSettlementAsync(targetId, ReferenceDate, settledAmount, string.Empty, _session, expectedRevision: expectedRevision);
        StatusMessage = result.Message;
        if (!result.Success)
        {
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRow(targetId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ReloadAsync();
        SelectRow(targetId);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "삭제할 렌탈 청구 대상을 선택하세요.";
            return;
        }

        if (TryRejectAggregateSelection("삭제"))
            return;

        var targetProfileId = SelectedRow.Source.Id;
        var targetSelectionId = SelectedRow.SelectionId;
        var result = SelectedRow.HasPersistedProfile
            ? await _rental.DeleteBillingProfileAsync(targetProfileId, _session, SelectedRow.Source.Revision)
            : await _rental.ExcludeUnlinkedBillingAssetFromBillingListAsync(targetSelectionId, _session);
        if (!result.Success)
        {
            StatusMessage = result.Message;
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRow(targetProfileId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ReloadAsync();
        NewProfile();
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private async Task DeleteCheckedAsync()
    {
        var targets = Rows.Where(row => row.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "삭제할 렌탈 청구 대상을 먼저 선택하세요.";
            return;
        }

        var aggregateTargets = targets.Where(row => row.IsAggregateRow).ToList();
        var persistedTargets = targets.Where(row => row.HasPersistedProfile && !row.IsAggregateRow).ToList();
        var unlinkedTargets = targets.Where(row => !row.HasPersistedProfile && !row.IsAggregateRow).ToList();
        var skippedAggregateCount = aggregateTargets.Count;
        if (persistedTargets.Count == 0 && unlinkedTargets.Count == 0)
        {
            StatusMessage = skippedAggregateCount > 0
                ? "거래처 그룹은 선택삭제할 수 없습니다. 개별 청구건 보기에서 정리 후 다시 시도하세요."
                : "삭제할 수 있는 청구 프로필 또는 미연결 설치처가 없습니다.";
            return;
        }

        var confirmation = MessageBox.Show(
            skippedAggregateCount > 0
                ? $"청구 프로필 {persistedTargets.Count:N0}건은 삭제하고, 프로필 없는 설치처 {unlinkedTargets.Count:N0}건은 청구 목록에서 제외하시겠습니까?\n거래처 그룹 {skippedAggregateCount:N0}건은 제외됩니다."
                : unlinkedTargets.Count > 0 && persistedTargets.Count > 0
                    ? $"청구 프로필 {persistedTargets.Count:N0}건은 삭제하고, 프로필 없는 설치처 {unlinkedTargets.Count:N0}건은 청구 목록에서 제외하시겠습니까?"
                    : unlinkedTargets.Count > 0
                        ? $"프로필 없는 설치처 {unlinkedTargets.Count:N0}건을 청구 목록에서 제외하시겠습니까?\n자산 정보는 삭제되지 않습니다."
                        : $"선택한 청구 프로필 {persistedTargets.Count:N0}건을 삭제하시겠습니까?",
            "렌탈 청구 선택삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        var successCount = 0;
        var conflictCount = 0;
        var failureMessages = new List<string>();
        foreach (var row in persistedTargets)
        {
            var result = await _rental.DeleteBillingProfileAsync(row.Source.Id, _session, row.Source.Revision);
            if (result.Success)
            {
                successCount++;
                continue;
            }

            if (result.ConcurrencyConflict)
                conflictCount++;

            failureMessages.Add($"{row.CustomerDisplayName}: {result.Message}");
        }

        var excludedUnlinkedCount = 0;
        foreach (var row in unlinkedTargets)
        {
            var result = await _rental.ExcludeUnlinkedBillingAssetFromBillingListAsync(row.SelectionId, _session);
            if (result.Success)
            {
                excludedUnlinkedCount++;
                continue;
            }

            if (result.ConcurrencyConflict)
                conflictCount++;

            failureMessages.Add($"{row.CustomerDisplayName}: {result.Message}");
        }

        await ReloadAsync();
        NewProfile();

        StatusMessage = failureMessages.Count == 0
            ? BuildDeleteCheckedSuccessMessage(successCount, excludedUnlinkedCount, skippedAggregateCount)
            : skippedAggregateCount > 0
                ? $"삭제/제외 성공 {successCount + excludedUnlinkedCount:N0}건 / 실패 {failureMessages.Count:N0}건 / 거래처 그룹 제외 {skippedAggregateCount:N0}건 - {string.Join(" | ", failureMessages.Take(3))}"
                : $"삭제/제외 성공 {successCount + excludedUnlinkedCount:N0}건 / 실패 {failureMessages.Count:N0}건 - {string.Join(" | ", failureMessages.Take(3))}";

        if (conflictCount > 0)
        {
            MessageBox.Show(
                $"{conflictCount:N0}건은 다른 PC에서 먼저 수정되어 삭제하지 못했습니다. 최신 목록을 다시 불러왔습니다.",
                "동시 수정 충돌",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static string BuildDeleteCheckedSuccessMessage(
        int deletedProfileCount,
        int excludedUnlinkedCount,
        int skippedAggregateCount)
    {
        var parts = new List<string>();
        if (deletedProfileCount > 0)
            parts.Add($"청구 프로필 {deletedProfileCount:N0}건 삭제");
        if (excludedUnlinkedCount > 0)
            parts.Add($"프로필 없는 설치처 {excludedUnlinkedCount:N0}건 청구목록 제외");
        if (parts.Count == 0)
            parts.Add("선택 항목 처리");

        var message = string.Join(", ", parts) + " 완료.";
        if (skippedAggregateCount > 0)
            message += $" 거래처 그룹 {skippedAggregateCount:N0}건은 제외했습니다.";
        return message;
    }

    [RelayCommand]
    private async Task MarkCompletedAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "청구 처리할 대상을 선택하세요.";
            return;
        }

        if (TryRejectAggregateSelection("완료 처리"))
            return;

        if (!SelectedRow.HasPersistedProfile)
        {
            StatusMessage = "청구 프로필이 없는 설치처입니다. 저장 후 다시 완료 처리하세요.";
            return;
        }

        if (SelectedRow.OutstandingAmount > 0m)
        {
            StatusMessage = "미수금이 남아 있어 완납처리할 수 없습니다. 먼저 '이번 입금 등록'으로 수금을 완료하세요.";
            MessageBox.Show(
                "미수금이 남아 있어 완납처리할 수 없습니다. 먼저 '이번 입금 등록'으로 수금을 완료하세요.",
                "완납처리 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var expectedRevision = SelectedRow.Source.Revision;
        var result = await _rental.MarkBillingCompletedAsync(
            targetId,
            ReferenceDate,
            "완료",
            string.Empty,
            _session,
            expectedRevision: expectedRevision);
        StatusMessage = result.Message;
        if (!result.Success)
        {
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRow(targetId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ReloadAsync();
        SelectRow(targetId);
    }

    [RelayCommand]
    private void NewProfile()
    {
        DiscardAutoSaveDraft();
        _pendingAssetLinkEdits.Clear();
        _editRevision = 0;
        EditId = Guid.NewGuid();
        EditCustomerId = null;
        EditCustomerName = string.Empty;
        EditBusinessNumber = string.Empty;
        EditInstallLocation = string.Empty;
        EditItemName = string.Empty;
        EditBillingType = "묶음";
        EditBillingAdvanceMode = "후불";
        EditOfficeCode = EditOfficeOptions.FirstOrDefault()?.Value
            ?? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        EnsureEditOfficeOption(EditOfficeCode);
        EditBillingMethod = string.Empty;
        EditBillingStatus = "예정";
        EditSettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
        EditCompletionStatus = PaymentFlowConstants.CompletionPending;
        EditEmail = string.Empty;
        EditBillingDay = 25;
        EditBillingCycleMonths = 1;
        EditMonthlyAmount = 0m;
        EditDepositAmount = 0m;
        EditSettledAmount = 0m;
        EditOutstandingAmount = 0m;
        EditRequiresFollowUp = false;
        EditSubmissionDocuments = string.Empty;
        EditNotes = string.Empty;
        EditBillingAnchorDate = null;
        SetContractReferenceDates(null);
        EditContractStartDate = null;
        EditContractEndDate = null;
        EditLastBilledDate = null;
        EditLastSettledDate = null;
        EditIsActive = true;
        TemplateItems.Clear();
        TemplateItems.Add(CreateDefaultTemplateItem());
        SelectedTemplateItem = TemplateItems.FirstOrDefault();
        CandidateAssets.Clear();
        IncludedAssets.Clear();
        TemplateSummary = "표시품목 1건 / 연결장비 0건";
        AssetCandidateSummary = "후보 장비가 없습니다.";
        LinkAssetsLater = false;
        _contractDateRefreshCts?.Cancel();
        SelectedRow = null;
        InvoiceToOpenAfterClose = null;
        _selectedRowBaselineSignature = string.Empty;
        UpdateTemplateDerivedValues();
        OnPropertyChanged(nameof(IsContractDateMissing));
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(ContractDateWarningMessage));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanExpandSelectedSummary));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
        ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
        NotifySelectionActionState();
    }

    [RelayCommand(CanExecute = nameof(CanExpandSelectedSummary))]
    private async Task ExpandSelectedSummaryAsync()
    {
        if (SelectedRow is null || !SelectedRow.IsAggregateRow)
        {
            StatusMessage = "거래처 그룹을 먼저 선택하세요.";
            return;
        }

        var targetId = SelectedRow.GroupedPersistedProfileIds.FirstOrDefault(id => id != Guid.Empty);
        if (targetId == Guid.Empty)
            targetId = SelectedRow.GroupedSelectionIds.FirstOrDefault(id => id != Guid.Empty);
        if (targetId == Guid.Empty)
            targetId = SelectedRow.SelectionId;

        _suppressFilterReload = true;
        try
        {
            ShowIndividualProfiles = true;
        }
        finally
        {
            _suppressFilterReload = false;
        }

        await ReloadAsync();

        if (targetId != Guid.Empty)
            SelectRow(targetId);

        StatusMessage = SelectedRow is null
            ? "거래처 그룹을 개별 청구건 보기로 전환했습니다. 목록에서 정리할 프로필을 선택하세요."
            : "거래처 그룹을 개별 청구건으로 전환했습니다. 필요한 프로필을 선택해 삭제/수정하세요.";
    }

    [RelayCommand]
    private void AddTemplateItem()
    {
        if (!CanEditBillingProfileDetails)
        {
            StatusMessage = "거래처 그룹에서는 표시 품목을 직접 편집할 수 없습니다. '개별 청구건 보기'로 전환한 뒤 진행하세요.";
            return;
        }

        var item = CreateDefaultTemplateItem();
        TemplateItems.Add(item);
        SelectedTemplateItem = item;
        UpdateTemplateDerivedValues();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTemplateItem))]
    private void RemoveTemplateItem()
    {
        if (SelectedTemplateItem is null)
            return;

        var index = TemplateItems.IndexOf(SelectedTemplateItem);
        TemplateItems.Remove(SelectedTemplateItem);
        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateDefaultTemplateItem());
        SelectedTemplateItem = TemplateItems[Math.Clamp(index, 0, TemplateItems.Count - 1)];
        UpdateTemplateDerivedValues();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveIncludedAsset))]
    private void RemoveIncludedAsset()
    {
        if (SelectedTemplateItem is null ||
            SelectedIncludedAsset is null ||
            SelectedIncludedAsset.AssetId == Guid.Empty)
        {
            return;
        }

        var removedAssetId = SelectedIncludedAsset.AssetId;
        var removedAsset = CloneBillingAssetOption(SelectedIncludedAsset, isSelected: false);
        var removedCount = 0;
        foreach (var templateItem in TemplateItems)
        {
            removedCount += RemoveIncludedAssetId(templateItem.IncludedAssetIds, removedAssetId);
            if (templateItem.RepresentativeAssetId == removedAssetId)
                templateItem.RepresentativeAssetId = null;
        }

        if (removedCount == 0)
            return;

        var emptyIndividualItems = TemplateItems
            .Where(item => IsTemplateItemIndividualMode(item) && item.IncludedAssetIds.All(id => id == Guid.Empty))
            .ToList();
        foreach (var emptyItem in emptyIndividualItems)
        {
            if (TemplateItems.Count <= 1)
                break;
            TemplateItems.Remove(emptyItem);
        }
        if (SelectedTemplateItem is not null && !TemplateItems.Contains(SelectedTemplateItem))
            SelectedTemplateItem = TemplateItems.FirstOrDefault();

        if (!TemplateItems.SelectMany(item => item.IncludedAssetIds).Contains(removedAssetId))
            _pendingAssetLinkEdits.Remove(removedAssetId);

        if (_candidateAssetPool.All(asset => asset.AssetId != removedAssetId))
            _candidateAssetPool.Add(removedAsset);

        NormalizeTemplateRepresentativeAssets();
        SyncIndividualTemplateItemsFromIncludedAssets();
        RefreshBillingAssetCollections();
        foreach (var item in TemplateItems)
        {
            item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
            item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        }

        UpdateTemplateDerivedValues();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        StatusMessage = "선택 장비를 현재 청구 포함 목록에서 제거했습니다. 저장하면 설치현황 청구 연결도 해제됩니다.";
    }

    [RelayCommand(CanExecute = nameof(CanSetRepresentativeAsset))]
    private void SetRepresentativeAsset()
    {
        if (SelectedIncludedAsset is null)
            return;

        SetRepresentativeAssetFromIncludedOption(SelectedIncludedAsset);
    }

    private void SetRepresentativeAssetFromIncludedOption(RentalBillingAssetOption asset)
    {
        if (SelectedTemplateItem is null ||
            asset.AssetId == Guid.Empty ||
            !IsTemplateItemBundleMode(SelectedTemplateItem) ||
            !SelectedTemplateItem.IncludedAssetIds.Contains(asset.AssetId))
        {
            RefreshBillingAssetCollections();
            return;
        }

        var includedAsset = IncludedAssets.FirstOrDefault(current => current.AssetId == asset.AssetId) ?? asset;
        SelectedIncludedAsset = includedAsset;
        var representativeLabel = BuildAssetShortLabel(includedAsset);
        SelectedTemplateItem.RepresentativeAssetId = asset.AssetId;
        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections();
        UpdateTemplateDerivedValues();
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        StatusMessage = $"대표자산을 '{representativeLabel}'로 지정했습니다. 저장하면 묶음 청구 대표 장비로 반영됩니다.";
    }


    [RelayCommand(CanExecute = nameof(CanAddIncludedAssetAssignmentHistory))]
    private async Task AddIncludedAssetAssignmentHistoryAsync()
    {
        if (SelectedIncludedAsset is null || SelectedIncludedAsset.AssetId == Guid.Empty)
        {
            StatusMessage = "임대이력을 추가할 내부 포함 장비를 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(SelectedIncludedAsset.AssetId);
        if (request is null)
        {
            StatusMessage = "임대이력 추가 정보를 만들 수 없습니다.";
            return;
        }

        if (!ShowIncludedAssetAssignmentHistoryDialog(request, "임대이력 추가"))
            return;

        await SaveIncludedAssetAssignmentHistoryRequestAsync(request);
    }

    [RelayCommand(CanExecute = nameof(CanEditIncludedAssetAssignmentHistory))]
    private async Task EditIncludedAssetAssignmentHistoryAsync()
    {
        if (SelectedIncludedAsset is null || SelectedIncludedAssetAssignmentHistory is null)
        {
            StatusMessage = "수정할 내부 포함 장비의 임대이력을 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(
            SelectedIncludedAsset.AssetId,
            SelectedIncludedAssetAssignmentHistory.HistoryId);
        if (request is null)
        {
            StatusMessage = "수정할 임대이력을 찾을 수 없습니다.";
            return;
        }

        if (!ShowIncludedAssetAssignmentHistoryDialog(request, "임대이력 수정"))
            return;

        await SaveIncludedAssetAssignmentHistoryRequestAsync(request);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteIncludedAssetAssignmentHistory))]
    private async Task DeleteIncludedAssetAssignmentHistoryAsync()
    {
        if (SelectedIncludedAssetAssignmentHistory is null)
        {
            StatusMessage = "삭제할 내부 포함 장비의 임대이력을 먼저 선택하세요.";
            return;
        }

        var confirmation = MessageBox.Show(
            GetActiveWindow(),
            "선택한 임대이력을 삭제하시겠습니까?\n자산 자체는 삭제되지 않고, 임대이력만 삭제됩니다.",
            "임대이력 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        var assetId = SelectedIncludedAssetAssignmentHistory.AssetId;
        IsBusy = true;
        try
        {
            var result = await _rental.DeleteAssetAssignmentHistoryAsync(SelectedIncludedAssetAssignmentHistory.HistoryId, _session);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                MessageBox.Show(GetActiveWindow(), result.Message, "임대이력 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadIncludedAssetAssignmentHistoriesAsync(assetId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool ShowIncludedAssetAssignmentHistoryDialog(RentalAssetAssignmentHistoryEditRequest request, string title)
    {
        var dialog = new RentalAssignmentHistoryEditWindow(request)
        {
            Owner = GetActiveWindow(),
            Title = title
        };
        return dialog.ShowDialog() == true;
    }

    private async Task SaveIncludedAssetAssignmentHistoryRequestAsync(RentalAssetAssignmentHistoryEditRequest request)
    {
        IsBusy = true;
        try
        {
            var result = await _rental.SaveAssetAssignmentHistoryAsync(request, _session);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                MessageBox.Show(GetActiveWindow(), result.Message, "임대이력 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadIncludedAssetAssignmentHistoriesAsync(request.AssetId);
            SelectedIncludedAssetAssignmentHistory = IncludedAssetAssignmentHistories
                .FirstOrDefault(history => history.HistoryId == result.EntityId)
                ?? IncludedAssetAssignmentHistories.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyIncludedAssetAssignmentHistoryCommandState()
    {
        OnPropertyChanged(nameof(CanAddIncludedAssetAssignmentHistory));
        OnPropertyChanged(nameof(CanEditIncludedAssetAssignmentHistory));
        OnPropertyChanged(nameof(CanDeleteIncludedAssetAssignmentHistory));
        AddIncludedAssetAssignmentHistoryCommand.NotifyCanExecuteChanged();
        EditIncludedAssetAssignmentHistoryCommand.NotifyCanExecuteChanged();
        DeleteIncludedAssetAssignmentHistoryCommand.NotifyCanExecuteChanged();
    }

    private void NotifySelectionActionState()
    {
        OnPropertyChanged(nameof(IsCustomerGroupSelection));
        OnPropertyChanged(nameof(CanEditBillingProfileDetails));
        OnPropertyChanged(nameof(CanOpenAssetLinkDialog));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanExpandSelectedSummary));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
        OnPropertyChanged(nameof(CanRemoveTemplateItem));
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        OnPropertyChanged(nameof(CanApplySelectedAssets));
        ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
        RemoveTemplateItemCommand.NotifyCanExecuteChanged();
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        NotifyIncludedAssetAssignmentHistoryCommandState();
    }

    private static Window? GetActiveWindow()
        => Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
           ?? Application.Current?.MainWindow;

    private static int RemoveIncludedAssetId(ObservableCollection<Guid> includedAssetIds, Guid assetId)
    {
        var removedCount = 0;
        for (var index = includedAssetIds.Count - 1; index >= 0; index--)
        {
            if (includedAssetIds[index] != assetId)
                continue;

            includedAssetIds.RemoveAt(index);
            removedCount++;
        }

        return removedCount;
    }

    [RelayCommand]
    private void ApplyBillingCyclePreset(object? parameter)
    {
        if (!TryResolveBillingCycleMonths(parameter, out var months) || months <= 0)
            return;

        EditBillingCycleMonths = months;
        UpdateTemplateDerivedValues();
    }

    [RelayCommand]
    private async Task RefreshCandidateAssetsAsync()
    {
        await LoadCandidateAssetsAsync(
            EditId == Guid.Empty ? null : EditId,
            EditCustomerId,
            EditCustomerName,
            EditOfficeCode,
            preserveSelection: true,
            autoIncludeAllCandidates: false);
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedAssets))]
    private void ApplySelectedAssetsToTemplate()
    {
        if (!CanEditBillingProfileDetails)
        {
            StatusMessage = "거래처 그룹에서는 장비 연결을 직접 편집할 수 없습니다. '개별 청구건 보기'로 전환한 뒤 진행하세요.";
            return;
        }

        if (SelectedTemplateItem is null)
            return;

        var mergedAssetIds = SelectedTemplateItem.IncludedAssetIds
            .Where(assetId => assetId != Guid.Empty)
            .Concat(CandidateAssets.Where(asset => asset.IsSelected).Select(asset => asset.AssetId))
            .Distinct()
            .ToList();

        SelectedTemplateItem.IncludedAssetIds.Clear();
        foreach (var assetId in mergedAssetIds)
            SelectedTemplateItem.IncludedAssetIds.Add(assetId);

        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections();
        if (SelectedTemplateItem is not null)
        {
            SelectedTemplateItem.IncludedAssetSummary = BuildIncludedAssetSummary(SelectedTemplateItem.IncludedAssetIds);
            SelectedTemplateItem.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(SelectedTemplateItem);
        }
        UpdateTemplateDerivedValues();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

    public void ApplyAssetLinkSelections(IReadOnlyList<RentalBillingAssetOption> selectedAssets)
    {
        if (!CanEditBillingProfileDetails)
        {
            StatusMessage = "거래처 그룹에서는 장비 연결을 직접 편집할 수 없습니다. '개별 청구건 보기'로 전환한 뒤 진행하세요.";
            return;
        }

        if (selectedAssets.Count == 0)
            return;

        if (SelectedTemplateItem is null)
        {
            if (TemplateItems.Count == 0)
                TemplateItems.Add(CreateDefaultTemplateItem());
            SelectedTemplateItem = TemplateItems.FirstOrDefault();
        }

        if (SelectedTemplateItem is null)
            return;

        foreach (var asset in selectedAssets.Where(asset => asset.AssetId != Guid.Empty))
        {
            foreach (var templateItem in TemplateItems.Where(item => !ReferenceEquals(item, SelectedTemplateItem)))
                templateItem.IncludedAssetIds.Remove(asset.AssetId);

            if (!SelectedTemplateItem.IncludedAssetIds.Contains(asset.AssetId))
                SelectedTemplateItem.IncludedAssetIds.Add(asset.AssetId);

            var linkedOption = CloneBillingAssetOption(asset, isSelected: true);
            linkedOption.CustomerId = EditCustomerId;
            linkedOption.TargetCustomerName = string.IsNullOrWhiteSpace(linkedOption.TargetCustomerName)
                ? EditCustomerName
                : linkedOption.TargetCustomerName;
            linkedOption.CurrentCustomerName = string.IsNullOrWhiteSpace(linkedOption.TargetCustomerName)
                ? linkedOption.CurrentCustomerName
                : linkedOption.TargetCustomerName;
            linkedOption.BillingProfileId = EditId == Guid.Empty ? null : EditId;
            linkedOption.IsLinkedToCurrentProfile = true;
            linkedOption.IsLinkedToAnotherProfile = false;

            _pendingAssetLinkEdits[linkedOption.AssetId] = BuildAssetLinkEdit(linkedOption);

            _candidateAssetPool.RemoveAll(current => current.AssetId == linkedOption.AssetId);
            var includedIndex = _includedAssetPool.FindIndex(current => current.AssetId == linkedOption.AssetId);
            if (includedIndex >= 0)
                _includedAssetPool[includedIndex] = linkedOption;
            else
                _includedAssetPool.Add(linkedOption);
        }

        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections();
        if (SelectedTemplateItem is not null)
        {
            SelectedTemplateItem.IncludedAssetSummary = BuildIncludedAssetSummary(SelectedTemplateItem.IncludedAssetIds);
            SelectedTemplateItem.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(SelectedTemplateItem);
            ApplyIncludedAssetMonthlyFeesToTemplateItem(SelectedTemplateItem);
        }
        UpdateTemplateDerivedValues();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        ScheduleContractDateRefresh();
        StatusMessage = $"장비 {selectedAssets.Count:N0}대를 현재 거래처 청구에 연결하도록 반영했습니다. 저장하면 설치현황도 함께 갱신됩니다.";
    }

    [RelayCommand(CanExecute = nameof(CanOpenCustomerContract))]
    private async Task OpenCustomerContractAsync()
    {
        if (!EditCustomerId.HasValue || EditCustomerId.Value == Guid.Empty)
        {
            StatusMessage = "먼저 계약서를 확인할 거래처를 선택하세요.";
            return;
        }

        var contract = await _local.GetPreferredCustomerContractAsync(EditCustomerId.Value, _session);
        if (contract is null)
        {
            StatusMessage = "해당 거래처에 등록된 계약서가 없습니다.";
            MessageBox.Show(
                "해당 거래처에 등록된 계약서가 없습니다.",
                "계약서 열기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ContractHasPdfFile(contract))
        {
            StatusMessage = "해당 거래처 계약서에는 아직 PDF 파일이 등록되지 않았습니다.";
            MessageBox.Show(
                "해당 거래처 계약서에는 아직 PDF 파일이 등록되지 않았습니다. 거래처 등록/수정 창에서 PDF를 추가한 뒤 다시 시도하세요.",
                "계약서 열기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            CustomerContractPreviewService.Open(contract);
            StatusMessage = "거래처 계약서를 열었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"계약서를 열지 못했습니다. {ex.Message}";
            MessageBox.Show(
                $"계약서를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                "계약서 열기",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private IReadOnlyList<RentalBillingAssetLinkEdit> BuildPendingAssetLinkEdits()
    {
        EnsureTemplateMonthlyFeesInPendingAssetEdits();

        var includedAssetIds = TemplateItems
            .SelectMany(item => item.IncludedAssetIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        return _pendingAssetLinkEdits
            .Where(pair => includedAssetIds.Contains(pair.Key))
            .Select(pair => new RentalBillingAssetLinkEdit
            {
                AssetId = pair.Value.AssetId,
                CustomerId = pair.Value.CustomerId,
                CustomerName = pair.Value.CustomerName,
                InstallLocation = pair.Value.InstallLocation,
                InstallSiteName = pair.Value.InstallSiteName,
                MonthlyFee = pair.Value.MonthlyFee,
                ContractStartDate = pair.Value.ContractStartDate,
                Notes = pair.Value.Notes
            })
            .ToList();
    }

    private RentalBillingAssetLinkEdit BuildAssetLinkEdit(RentalBillingAssetOption asset)
        => new()
        {
            AssetId = asset.AssetId,
            CustomerId = EditCustomerId,
            CustomerName = string.IsNullOrWhiteSpace(EditCustomerName) ? asset.TargetCustomerName : EditCustomerName,
            InstallLocation = asset.InstallLocation,
            InstallSiteName = asset.InstallLocation,
            MonthlyFee = asset.MonthlyFee,
            ContractStartDate = ToDateOnly(asset.ContractStartDate),
            Notes = asset.Notes
        };

    private static bool TryResolveBillingCycleMonths(object? parameter, out int months)
    {
        switch (parameter)
        {
            case int value:
                months = value;
                return true;
            case string text when int.TryParse(text, out var parsed):
                months = parsed;
                return true;
            default:
                months = 0;
                return false;
        }
    }

    partial void OnSelectedRowChanged(RentalBillingViewRow? value)
    {
        PersistDraftBeforeContextSwitch();

        if (value is null)
        {
            _contractDateRefreshCts?.Cancel();
            OnPropertyChanged(nameof(IsContractDateMissing));
            OnPropertyChanged(nameof(ShouldShowContractDateWarning));
            OnPropertyChanged(nameof(ContractDateWarningMessage));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanExpandSelectedSummary));
            OnPropertyChanged(nameof(CanStartBillingSelected));
            OnPropertyChanged(nameof(CanHoldSelected));
            OnPropertyChanged(nameof(CanRegisterSettlementSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(CanMarkCompletedSelected));
            ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
            NotifySelectionActionState();
            return;
        }

        var source = value.Source;
        _editRevision = source.Revision;
        EditId = source.Id;
        EditCustomerId = source.CustomerId;
        EditCustomerName = string.IsNullOrWhiteSpace(value.CustomerDisplayName) ? source.CustomerName : value.CustomerDisplayName;
        EditBusinessNumber = source.BusinessNumber;
        EditInstallLocation = string.IsNullOrWhiteSpace(value.InstallLocationDisplay)
            ? source.InstallSiteName
            : value.InstallLocationDisplay;
        EditItemName = source.ItemName;
        EditBillingType = string.IsNullOrWhiteSpace(source.BillingType) ? "묶음" : source.BillingType;
        EditBillingAdvanceMode = string.IsNullOrWhiteSpace(source.BillingAdvanceMode) ? "후불" : source.BillingAdvanceMode;
        EditOfficeCode = ResolveProfileOfficeCode(source, _session.OfficeCode);
        EnsureEditOfficeOption(EditOfficeCode);
        EditBillingMethod = source.BillingMethod;
        EditBillingStatus = source.BillingStatus;
        EditSettlementStatus = PaymentFlowConstants.NormalizeSettlementStatus(source.SettlementStatus);
        EditCompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(source.CompletionStatus);
        EditEmail = source.Email;
        EditBillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(source.BillingDayMode);
        EditBillingDay = RentalBillingScheduleRules.NormalizeBillingDay(source.BillingDay);
        EditBillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(source.BillingCycleMonths);
        EditBillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            EditBillingCycleMonths,
            source.BillingAnchorMonth,
            source.BillingAnchorDate,
            source.BillingStartDate,
            source.ContractStartDate,
            source.ContractDate,
            source.LastBilledDate,
            value.NextBillingDate ?? ReferenceDate);
        EditDocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(source.DocumentIssueMode);
        EditDocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(source.DocumentLeadDays);
        EditMonthlyAmount = value.CurrentBilledAmount > 0m ? value.CurrentBilledAmount : source.MonthlyAmount;
        EditDepositAmount = source.DepositAmount;
        EditSettledAmount = value.SettledAmount;
        EditOutstandingAmount = value.OutstandingAmount;
        EditRequiresFollowUp = value.RequiresFollowUp;
        EditSubmissionDocuments = source.SubmissionDocuments;
        EditNotes = source.Notes;
        EditBillingAnchorDate = ToDateTime(source.BillingAnchorDate);
        SetContractReferenceDates(ToDateTime(source.ContractDate ?? source.BillingStartDate));
        EditContractStartDate = ToDateTime(source.ContractStartDate);
        EditContractEndDate = ToDateTime(source.ContractEndDate);
        EditLastBilledDate = ToDateTime(source.LastBilledDate);
        EditLastSettledDate = ToDateTime(source.LastSettledDate);
        EditIsActive = source.IsActive;
        if (value.IsAggregateRow)
        {
            LoadAggregateSelectionSummary(value);
            OnPropertyChanged(nameof(IsContractDateMissing));
            OnPropertyChanged(nameof(ShouldShowContractDateWarning));
            OnPropertyChanged(nameof(ContractDateWarningMessage));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanExpandSelectedSummary));
            OnPropertyChanged(nameof(CanStartBillingSelected));
            OnPropertyChanged(nameof(CanHoldSelected));
            OnPropertyChanged(nameof(CanRegisterSettlementSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(CanMarkCompletedSelected));
            ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
            NotifySelectionActionState();
            return;
        }

        LoadTemplateItemsFromProfile(source);
        StartCandidateAssetsLoad(
            source.Id,
            EditCustomerId,
            EditCustomerName,
            EditOfficeCode,
            preserveSelection: false,
            autoIncludeAllCandidates: false);
        ScheduleContractDateRefresh(updateSelectedRowBaselineIfUnchanged: true);
        if (!value.HasPersistedProfile)
            StatusMessage = "청구 프로필이 없는 설치처입니다. 내용을 확인한 뒤 저장하면 청구 프로필이 생성됩니다.";
        OnPropertyChanged(nameof(IsContractDateMissing));
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(ContractDateWarningMessage));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanExpandSelectedSummary));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
        ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
        NotifySelectionActionState();
    }

    private async Task ReloadFiltersAsync()
    {
        _suppressFilterReload = true;
        try
        {
        var offices = await _local.GetOfficesAsync();
        var currentFilterValue = SelectedOfficeFilter?.Value ?? AllOption;
        var currentEditOfficeCode = EditOfficeCode;
        var readableOfficeCodes = _local.GetReadableRentalOfficeCodesForSession(_session);
        var writableOfficeCodes = CanManageAll
            ? OfficeCodeCatalog.All
            : _local.GetWritableRentalOfficeCodesForSession(_session);
        var selectedRowOfficeCode = ResolveProfileOfficeCode(SelectedRow?.Source, _session.OfficeCode);

        OfficeOptions.Clear();
        EditOfficeOptions.Clear();
        OfficeOptions.Add(new DisplayOption { Value = AllOption, DisplayName = AllOption });
        foreach (var office in BuildOfficeDisplayOptions(offices, readableOfficeCodes))
        {
            OfficeOptions.Add(new DisplayOption
            {
                Value = office.Value,
                DisplayName = office.DisplayName
            });
        }

        var editableOfficeCodes = BuildEditableBillingOfficeCodes(
            writableOfficeCodes,
            readableOfficeCodes,
            [currentEditOfficeCode, selectedRowOfficeCode]);
        foreach (var office in BuildOfficeDisplayOptions(offices, editableOfficeCodes))
        {
            EditOfficeOptions.Add(new DisplayOption
            {
                Value = office.Value,
                DisplayName = office.DisplayName
            });
        }

        SelectedOfficeFilter = OfficeOptions.FirstOrDefault(option =>
                                   string.Equals(option.Value, currentFilterValue, StringComparison.OrdinalIgnoreCase))
                               ?? OfficeOptions.FirstOrDefault(option => option.Value == AllOption)
                               ?? OfficeOptions.FirstOrDefault();

        if (EditOfficeOptions.Count == 0)
        {
            var fallbackOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
            EditOfficeOptions.Add(new DisplayOption
            {
                Value = fallbackOfficeCode,
                DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(fallbackOfficeCode)
            });
        }

        EditOfficeCode = EditOfficeOptions.FirstOrDefault(option =>
                           string.Equals(option.Value, currentEditOfficeCode, StringComparison.OrdinalIgnoreCase))?.Value
                       ?? EditOfficeOptions.First().Value;

    }
    finally
        {
            _suppressFilterReload = false;
        }
    }

    private static IReadOnlyList<string> BuildEditableBillingOfficeCodes(
        IEnumerable<string> writableOfficeCodes,
        IEnumerable<string> readableOfficeCodes,
        IEnumerable<string?> preserveOfficeCodes)
    {
        var editableOfficeCodes = NormalizeOfficeCodes(writableOfficeCodes);
        var readableOfficeCodeSet = NormalizeOfficeCodes(readableOfficeCodes);
        foreach (var officeCode in preserveOfficeCodes)
        {
            if (!OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
                continue;

            if (readableOfficeCodeSet.Contains(normalizedOfficeCode))
                editableOfficeCodes.Add(normalizedOfficeCode);
        }

        return editableOfficeCodes.ToList();
    }

    private static HashSet<string> NormalizeOfficeCodes(IEnumerable<string?> officeCodes)
    {
        var normalizedOfficeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in officeCodes)
        {
            if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
                normalizedOfficeCodes.Add(normalizedOfficeCode);
        }

        return normalizedOfficeCodes;
    }

    private static IReadOnlyList<DisplayOption> BuildOfficeDisplayOptions(
        IEnumerable<LocalOffice> offices,
        IEnumerable<string> officeCodes)
    {
        var normalizedOfficeCodes = NormalizeOfficeCodes(officeCodes)
            .OrderBy(OfficeCodeCatalog.GetOfficeDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var officeLookup = offices
            .Select(office => new
            {
                Office = office,
                Code = OfficeCodeCatalog.TryNormalizeOfficeCode(office.Code, out var normalizedOfficeCode)
                    ? normalizedOfficeCode
                    : string.Empty
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Code))
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Office,
                StringComparer.OrdinalIgnoreCase);

        return normalizedOfficeCodes
            .Select(code =>
            {
                var displayName = officeLookup.TryGetValue(code, out var office) && !string.IsNullOrWhiteSpace(office.Name)
                    ? office.Name
                    : OfficeCodeCatalog.GetOfficeDisplayName(code);
                return new DisplayOption
                {
                    Value = code,
                    DisplayName = displayName
                };
            })
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureEditOfficeOption(string? officeCode)
    {
        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
            return;

        if (EditOfficeOptions.Any(option => string.Equals(option.Value, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase)))
            return;

        var readableOfficeCodes = CanManageAll
            ? OfficeCodeCatalog.All
            : _local.GetReadableRentalOfficeCodesForSession(_session);
        if (!readableOfficeCodes.Contains(normalizedOfficeCode))
            return;

        EditOfficeOptions.Add(new DisplayOption
        {
            Value = normalizedOfficeCode,
            DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode)
        });
    }

    private static string ResolveProfileOfficeCode(LocalRentalBillingProfile? profile, string fallbackOfficeCode)
    {
        if (profile is null)
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(fallbackOfficeCode, DomainConstants.OfficeUsenet);

        return RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            fallbackOfficeCode);
    }

    public async Task<IReadOnlyList<LookupRow>> BuildCustomerLookupRowsAsync()
    {
        var customers = await _local.GetCustomersForRentalScopeAsync(_session);
        return customers
            .OrderBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(customer => new LookupRow
            {
                Id = customer.Id,
                PrimaryText = customer.NameOriginal,
                SecondaryText = string.Join(" | ", new[]
                {
                    customer.BusinessNumber,
                    customer.Phone,
                    customer.Address
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Tag = customer
            })
            .ToList();
    }

    public void ApplySelectedCustomer(LocalCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        EditCustomerId = customer.Id;
        EditCustomerName = customer.NameOriginal?.Trim() ?? string.Empty;
        EditBusinessNumber = customer.BusinessNumber?.Trim() ?? string.Empty;
        EditOfficeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
            customer.TenantCode,
            customer.OfficeCode,
            customer.OfficeCode,
            customer.ResponsibleOfficeCode,
            _session.OfficeCode);
        EnsureEditOfficeOption(EditOfficeCode);

        var department = customer.Department?.Trim() ?? string.Empty;
        EditInstallLocation = string.IsNullOrWhiteSpace(department)
            ? string.Join(" ", new[] { customer.Address, customer.DetailAddress }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : department;

        foreach (var edit in _pendingAssetLinkEdits.Values)
        {
            edit.CustomerId = customer.Id;
            edit.CustomerName = EditCustomerName;
        }

        foreach (var asset in _includedAssetPool.Concat(_candidateAssetPool))
            asset.TargetCustomerName = EditCustomerName;

        StartCandidateAssetsLoad(
            EditId == Guid.Empty ? null : EditId,
            EditCustomerId,
            EditCustomerName,
            EditOfficeCode,
            preserveSelection: false,
            autoIncludeAllCandidates: true);
        ScheduleContractDateRefresh();
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
    }

    public async Task RefreshSelectedCustomerContextAsync()
    {
        if (!EditCustomerId.HasValue || EditCustomerId.Value == Guid.Empty)
        {
            await RefreshContractDateFromSourcesAsync(preserveExistingValue: false);
            return;
        }

        var customer = await _local.GetCustomerForRentalScopeAsync(EditCustomerId.Value, _session);
        if (customer is not null)
        {
            EditCustomerName = customer.NameOriginal?.Trim() ?? string.Empty;
            EditBusinessNumber = customer.BusinessNumber?.Trim() ?? string.Empty;
            EditEmail = customer.Email?.Trim() ?? string.Empty;
            EditOfficeCode = RentalScopeNormalizer.ResolveResponsibleOfficeCode(
                customer.TenantCode,
                customer.OfficeCode,
                customer.OfficeCode,
                customer.ResponsibleOfficeCode,
                _session.OfficeCode);
            EnsureEditOfficeOption(EditOfficeCode);

            var department = customer.Department?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(department))
                EditInstallLocation = department;
        }

        await RefreshContractDateFromSourcesAsync(preserveExistingValue: false);
    }

    private void SelectRow(Guid entityId)
    {
        SelectedRow = Rows.FirstOrDefault(row =>
            row.SelectionId == entityId ||
            row.Source.Id == entityId ||
            row.GroupedSelectionIds.Contains(entityId) ||
            row.GroupedPersistedProfileIds.Contains(entityId));
    }

    private void ScheduleContractDateRefresh(bool preserveCurrentValue = false, bool updateSelectedRowBaselineIfUnchanged = false)
    {
        _contractDateRefreshCts?.Cancel();
        _contractDateRefreshCts?.Dispose();

        var cts = new CancellationTokenSource();
        _contractDateRefreshCts = cts;
        var baselineSelectionId = updateSelectedRowBaselineIfUnchanged ? SelectedRow?.SelectionId : null;
        var baselineSignature = updateSelectedRowBaselineIfUnchanged ? BuildCurrentEditorSignature() : null;
        UiTaskHelper.Forget(
            RefreshContractDateFromSourcesAsync(
                preserveCurrentValue,
                updateSelectedRowBaselineIfUnchanged,
                baselineSelectionId,
                baselineSignature,
                cts.Token),
            "RENTAL",
            "렌탈 계약 체결일 조회",
            ex =>
            {
                if (ex is OperationCanceledException)
                    return;

                StatusMessage = $"계약 체결일 정보를 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task RefreshContractDateFromSourcesAsync(
        bool preserveExistingValue,
        bool updateSelectedRowBaselineIfUnchanged = false,
        Guid? baselineSelectionId = null,
        string? baselineSignature = null,
        CancellationToken ct = default)
    {
        if (preserveExistingValue && EditContractDate.HasValue)
            return;

        var contractDate = await ResolveContractDateFromSourcesAsync(ct);
        ct.ThrowIfCancellationRequested();
        var shouldRefreshSelectedRowBaseline = updateSelectedRowBaselineIfUnchanged &&
                                              baselineSelectionId.HasValue &&
                                              SelectedRow?.SelectionId == baselineSelectionId.Value &&
                                              string.Equals(baselineSignature, BuildCurrentEditorSignature(), StringComparison.Ordinal);
        SetContractReferenceDates(ToDateTime(contractDate));
        if (shouldRefreshSelectedRowBaseline)
            _selectedRowBaselineSignature = BuildCurrentEditorSignature();
    }

    private async Task<DateOnly?> ResolveContractDateFromSourcesAsync(CancellationToken ct = default)
    {
        if (EditCustomerId.HasValue && EditCustomerId.Value != Guid.Empty)
        {
            var representativeContract = await _local.GetRepresentativeCustomerContractAsync(EditCustomerId.Value, _session, ct);
            if (representativeContract?.SignedDate is DateOnly signedDate)
                return signedDate;
        }

        return await ResolveContractDateFromLinkedA3ColorAssetsAsync(ct);
    }

    private async Task<DateOnly?> ResolveContractDateFromLinkedA3ColorAssetsAsync(CancellationToken ct = default)
    {
        var linkedAssetIds = TemplateItems
            .SelectMany(item => item.IncludedAssetIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (linkedAssetIds.Count == 0)
            return null;

        var linkedAssets = await _rental.GetIncludedBillingAssetsAsync(
            null,
            linkedAssetIds,
            EditCustomerId,
            EditOfficeCode,
            _session,
            ct);

        var earliestInstallDate = linkedAssets
            .Where(IsA3ColorMultiFunctionAsset)
            .Select(asset => asset.InstallDate)
            .Where(installDate => installDate.HasValue)
            .Select(installDate => installDate!.Value)
            .OrderBy(installDate => installDate)
            .FirstOrDefault();

        return earliestInstallDate == default ? null : earliestInstallDate;
    }

    private static bool ContractHasPdfFile(LocalCustomerContract? contract)
        => contract is not null &&
           !string.IsNullOrWhiteSpace(contract.FileName) &&
           contract.FileSize > 0 &&
           contract.FileContent is { Length: > 0 };

    private static bool IsA3ColorMultiFunctionAsset(LocalRentalAsset asset)
        => RentalAssetCategoryRules.IsA3ColorMultiFunctionAsset(asset);

    private DateOnly GetBillingReferenceDate()
        => ToDateOnly(EditContractDate)
           ?? ToDateOnly(EditBillingStartDate)
           ?? ToDateOnly(EditContractStartDate)
           ?? DateOnly.FromDateTime(DateTime.Today);

    private void SetContractReferenceDates(DateTime? value)
    {
        _suppressContractDateSynchronization = true;
        try
        {
            EditContractDate = value?.Date;
            EditBillingStartDate = value?.Date;
        }
        finally
        {
            _suppressContractDateSynchronization = false;
        }

        OnPropertyChanged(nameof(IsContractDateMissing));
        OnPropertyChanged(nameof(ShouldShowContractDateWarning));
        OnPropertyChanged(nameof(ContractDateWarningMessage));
        UpdateTemplateDerivedValues();
    }

    private void StartCandidateAssetsLoad(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string officeCode,
        bool preserveSelection,
        bool autoIncludeAllCandidates = false)
    {
        _candidateAssetsLoadCts?.Cancel();
        _candidateAssetsLoadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _candidateAssetsLoadCts = cts;
        _candidateAssetsLoadTask = LoadCandidateAssetsAsync(
            billingProfileId,
            customerId,
            customerName,
            officeCode,
            preserveSelection,
            autoIncludeAllCandidates,
            cts.Token);

        UiTaskHelper.Forget(
            _candidateAssetsLoadTask,
            "RENTAL",
            "렌탈 청구 연결 장비 조회",
            ex =>
            {
                if (ex is OperationCanceledException)
                    return;

                StatusMessage = $"연결 장비 정보를 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task LoadCandidateAssetsAsync(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string officeCode,
        bool preserveSelection,
        bool autoIncludeAllCandidates,
        CancellationToken ct = default)
    {
        var previousSelections = preserveSelection
            ? CandidateAssets.Where(asset => asset.IsSelected).Select(asset => asset.AssetId).ToHashSet()
            : new HashSet<Guid>();

        var explicitIncludedAssetIds = TemplateItems
            .SelectMany(item => item.IncludedAssetIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var includedAssets = await _rental.GetIncludedBillingAssetsAsync(
            billingProfileId,
            explicitIncludedAssetIds,
            customerId,
            officeCode,
            _session,
            ct);

        var hasExplicitIncludedAssets = TemplateItems
            .SelectMany(item => item.IncludedAssetIds)
            .Any(id => id != Guid.Empty);

        if (SelectedTemplateItem is not null &&
            !LinkAssetsLater &&
            !hasExplicitIncludedAssets &&
            includedAssets.Count > 0 &&
            TemplateItems.Count == 1)
        {
            foreach (var assetId in includedAssets.Select(asset => asset.Id).Distinct())
            {
                if (!SelectedTemplateItem.IncludedAssetIds.Contains(assetId))
                    SelectedTemplateItem.IncludedAssetIds.Add(assetId);
            }
        }

        ct.ThrowIfCancellationRequested();

        _includedAssetPool.Clear();
        _includedAssetPool.AddRange(includedAssets
            .Select(asset =>
            {
                var option = CreateBillingAssetOption(asset, isSelected: true);
                ApplyPendingAssetLinkEdit(option);
                return option;
            }));

        _candidateAssetPool.Clear();
        RefreshBillingAssetCollections(previousSelections);
        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections(previousSelections);
        UpdateTemplateDerivedValues();
    }

    private void LoadTemplateItemsFromProfile(LocalRentalBillingProfile profile)
    {
        _pendingAssetLinkEdits.Clear();
        TemplateItems.Clear();
        var templateItems = _rental.GetBillingTemplateItems(profile);
        foreach (var item in templateItems)
        {
            var editorItem = new RentalBillingTemplateEditorItem
            {
                ItemId = item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = ResolveTemplateBillingLineMode(item.BillingLineMode, EditBillingType),
                Specification = item.Specification,
                Unit = item.Unit,
                MaterialNumber = item.MaterialNumber,
                RepresentativeAssetId = item.RepresentativeAssetId,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = item.Amount,
                Note = item.Note,
                IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds)
            };

            foreach (var assetId in item.IncludedAssetIds.Distinct())
                editorItem.IncludedAssetIds.Add(assetId);

            WireTemplateItem(editorItem);
            TemplateItems.Add(editorItem);
        }

        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateDefaultTemplateItem());

        SelectedTemplateItem = TemplateItems.FirstOrDefault();
        NormalizeTemplateRepresentativeAssets();
        UpdateTemplateDerivedValues();
        _selectedRowBaselineSignature = BuildCurrentEditorSignature();
    }

    private RentalBillingTemplateEditorItem CreateDefaultTemplateItem()
    {
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = string.IsNullOrWhiteSpace(EditItemName) ? "렌탈 임대료" : EditItemName,
            BillingLineMode = ResolveDefaultTemplateBillingLineMode(EditBillingType),
            Quantity = 1m,
            UnitPrice = EditMonthlyAmount,
            Amount = EditMonthlyAmount
        };
        WireTemplateItem(item);
        return item;
    }

    private void WireTemplateItem(RentalBillingTemplateEditorItem item)
    {
        item.PropertyChanged += (_, args) =>
        {
            if (_updatingTemplateDerivedValues ||
                _suppressTemplateItemChangeHandling ||
                !IsTemplateEditorChangeProperty(args.PropertyName))
                return;

            if (IsTemplatePriceProperty(args.PropertyName))
                ApplyTemplateMonthlyFeesToPendingAssetEdits(item);

            if (string.Equals(args.PropertyName, nameof(RentalBillingTemplateEditorItem.BillingLineMode), StringComparison.Ordinal))
            {
                SyncIndividualTemplateItemsFromIncludedAssets();
                NormalizeTemplateRepresentativeAssets();
                RefreshBillingAssetCollections();
            }

            UpdateTemplateDerivedValues();
            ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        };
    }

    private void SyncAssetSelectionFromTemplate()
        => RefreshBillingAssetCollections();

    private void SyncIncludedAssetsFromTemplate()
        => RefreshBillingAssetCollections();

    private void RefreshBillingAssetCollections(ISet<Guid>? candidateSelectionIds = null)
    {
        if (SelectedTemplateItem is null)
        {
            IncludedAssets.Clear();
            SelectedIncludedAsset = null;
            CandidateAssets.Clear();
            AssetCandidateSummary = "후보 장비가 없습니다.";
            RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanRemoveIncludedAsset));
            SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSetRepresentativeAsset));
            ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanApplySelectedAssets));
            return;
        }

        var selectedAssetIds = SelectedTemplateItem.IncludedAssetIds
            .Where(assetId => assetId != Guid.Empty)
            .Distinct()
            .ToHashSet();
        var assetLookup = _includedAssetPool
            .Concat(_candidateAssetPool)
            .GroupBy(asset => asset.AssetId)
            .ToDictionary(group => group.Key, group => group.First());

        IncludedAssets.Clear();
        foreach (var assetId in selectedAssetIds)
        {
            if (assetLookup.TryGetValue(assetId, out var option))
            {
                var clone = CloneBillingAssetOption(option, isSelected: true);
                clone.IsRepresentativeAsset = SelectedTemplateItem.RepresentativeAssetId == clone.AssetId;
                clone.PropertyChanged += HandleIncludedAssetOptionPropertyChanged;
                IncludedAssets.Add(clone);
            }
        }

        if (SelectedIncludedAsset is null ||
            !IncludedAssets.Any(asset => asset.AssetId == SelectedIncludedAsset.AssetId))
        {
            SelectedIncludedAsset = IncludedAssets.FirstOrDefault();
        }

        var selectedCandidateIds = candidateSelectionIds ?? CandidateAssets
            .Where(asset => asset.IsSelected)
            .Select(asset => asset.AssetId)
            .ToHashSet();

        _suppressCandidateAssetSelectionChanges = true;
        try
        {
            CandidateAssets.Clear();
            foreach (var option in _candidateAssetPool.Where(asset => !selectedAssetIds.Contains(asset.AssetId)))
            {
                var clone = CloneBillingAssetOption(option, isSelected: selectedCandidateIds.Contains(option.AssetId));
                clone.PropertyChanged += HandleCandidateAssetOptionPropertyChanged;
                CandidateAssets.Add(clone);
            }
        }
        finally
        {
            _suppressCandidateAssetSelectionChanges = false;
        }

        AssetCandidateSummary = CandidateAssets.Count == 0
            ? "후보 장비가 없습니다."
            : $"후보 장비 {CandidateAssets.Count:N0}대";
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplySelectedAssets));
    }

    private void HandleCandidateAssetOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressCandidateAssetSelectionChanges ||
            sender is not RentalBillingAssetOption ||
            !string.Equals(e.PropertyName, nameof(RentalBillingAssetOption.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplySelectedAssets));
    }

    private void HandleIncludedAssetOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressIncludedAssetRepresentativeChanges ||
            sender is not RentalBillingAssetOption asset ||
            !string.Equals(e.PropertyName, nameof(RentalBillingAssetOption.IsRepresentativeAsset), StringComparison.Ordinal))
        {
            return;
        }

        if (!asset.IsRepresentativeAsset)
        {
            if (SelectedTemplateItem?.RepresentativeAssetId == asset.AssetId)
            {
                _suppressIncludedAssetRepresentativeChanges = true;
                try
                {
                    asset.IsRepresentativeAsset = true;
                }
                finally
                {
                    _suppressIncludedAssetRepresentativeChanges = false;
                }
            }

            return;
        }

        SetRepresentativeAssetFromIncludedOption(asset);
    }

    private static RentalBillingAssetOption CreateBillingAssetOption(LocalRentalAsset asset, bool isSelected = false)
    {
        var responsibleOfficeName = OfficeCodeCatalog.GetOfficeDisplayName(
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                ? asset.ManagementCompanyCode
                : asset.ResponsibleOfficeCode);
        var managementCompanyName = OfficeCodeCatalog.GetOfficeDisplayName(
            string.IsNullOrWhiteSpace(asset.ManagementCompanyCode)
                ? asset.OfficeCode
                : asset.ManagementCompanyCode);

        return new()
        {
            AssetId = asset.Id,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementNumber = asset.ManagementNumber,
            ItemName = asset.ItemName,
            ItemCategoryName = asset.ItemCategoryName,
            Manufacturer = asset.Manufacturer,
            MachineNumber = asset.MachineNumber,
            CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName,
            InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? "미확인" : asset.BillingEligibilityStatus,
            ResponsibleOfficeName = responsibleOfficeName,
            ManagementCompanyName = managementCompanyName,
            AssetScopeDisplay = BuildAssetScopeDisplay(responsibleOfficeName, managementCompanyName),
            Notes = asset.Notes ?? string.Empty,
            MonthlyFee = asset.MonthlyFee,
            ContractStartDate = ToDateTime(asset.ContractStartDate),
            PurchaseDate = ToDateTime(asset.PurchaseDate),
            InstallDate = ToDateTime(asset.InstallDate),
            IsSelected = isSelected
        };
    }

    private static RentalBillingAssetOption CloneBillingAssetOption(RentalBillingAssetOption asset, bool isSelected = false)
        => new()
        {
            AssetId = asset.AssetId,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementNumber = asset.ManagementNumber,
            ItemName = asset.ItemName,
            ItemCategoryName = asset.ItemCategoryName,
            Manufacturer = asset.Manufacturer,
            MachineNumber = asset.MachineNumber,
            CurrentCustomerName = asset.CurrentCustomerName,
            TargetCustomerName = asset.TargetCustomerName,
            InstallLocation = asset.InstallLocation,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            CurrentBillingProfileDisplay = asset.CurrentBillingProfileDisplay,
            ResponsibleOfficeName = asset.ResponsibleOfficeName,
            ManagementCompanyName = asset.ManagementCompanyName,
            AssetScopeDisplay = asset.AssetScopeDisplay,
            IsOutsideCurrentOffice = asset.IsOutsideCurrentOffice,
            Notes = asset.Notes,
            MonthlyFee = asset.MonthlyFee,
            ContractStartDate = asset.ContractStartDate,
            PurchaseDate = asset.PurchaseDate,
            InstallDate = asset.InstallDate,
            IsLinkedToCurrentProfile = asset.IsLinkedToCurrentProfile,
            IsLinkedToAnotherProfile = asset.IsLinkedToAnotherProfile,
            IsRepresentativeAsset = asset.IsRepresentativeAsset,
            IsSelected = isSelected
        };

    private static string BuildAssetScopeDisplay(string responsibleOfficeName, string managementCompanyName)
    {
        var responsible = string.IsNullOrWhiteSpace(responsibleOfficeName) ? "-" : responsibleOfficeName.Trim();
        var management = string.IsNullOrWhiteSpace(managementCompanyName) ? "-" : managementCompanyName.Trim();
        return string.Equals(responsible, management, StringComparison.CurrentCultureIgnoreCase)
            ? responsible
            : $"담당 {responsible} / 관리 {management}";
    }

    private void ApplyPendingAssetLinkEdit(RentalBillingAssetOption asset)
    {
        if (!_pendingAssetLinkEdits.TryGetValue(asset.AssetId, out var edit))
            return;

        asset.TargetCustomerName = string.IsNullOrWhiteSpace(edit.CustomerName)
            ? asset.TargetCustomerName
            : edit.CustomerName;
        asset.CurrentCustomerName = string.IsNullOrWhiteSpace(asset.TargetCustomerName)
            ? asset.CurrentCustomerName
            : asset.TargetCustomerName;
        if (!string.IsNullOrWhiteSpace(edit.InstallLocation))
            asset.InstallLocation = edit.InstallLocation;
        if (edit.MonthlyFee.HasValue)
            asset.MonthlyFee = edit.MonthlyFee.Value;
        if (edit.ContractStartDate.HasValue)
            asset.ContractStartDate = ToDateTime(edit.ContractStartDate);
        asset.Notes = edit.Notes ?? string.Empty;
    }

    private void ApplyIncludedAssetMonthlyFeesToTemplateItem(RentalBillingTemplateEditorItem? item)
    {
        if (item is null)
            return;

        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return;

        var includedAssets = includedAssetIds
            .Select(FindBillingAssetOption)
            .Where(asset => asset is not null)
            .Cast<RentalBillingAssetOption>()
            .ToList();
        if (includedAssets.Count == 0)
            return;

        var monthlyFees = includedAssets
            .Select(asset => Math.Max(0m, asset.MonthlyFee))
            .ToList();
        if (monthlyFees.All(fee => fee <= 0m))
            return;

        var totalMonthlyFee = monthlyFees.Sum();
        var distinctPositiveFees = monthlyFees
            .Where(fee => fee > 0m)
            .Distinct()
            .ToList();
        var effectiveLineMode = GetTemplateItemEffectiveLineMode(item);

        if (includedAssetIds.Count == 1 ||
            string.Equals(effectiveLineMode, "묶음", StringComparison.OrdinalIgnoreCase) ||
            distinctPositiveFees.Count != 1)
        {
            item.Quantity = 1m;
            item.UnitPrice = totalMonthlyFee;
        }
        else
        {
            item.Quantity = includedAssetIds.Count;
            item.UnitPrice = distinctPositiveFees[0];
        }

        item.NormalizeCalculatedAmount();
        ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
    }

    private bool SyncIndividualTemplateItemsFromIncludedAssets()
    {
        if (TemplateItems.Count == 0)
            return false;

        var assetLookup = BuildBillingAssetOptionLookup();
        var previousSelectedItemId = SelectedTemplateItem?.ItemId;
        var previousSelectedIncludedAssetId = SelectedIncludedAsset?.AssetId;
        var rebuiltItems = new List<RentalBillingTemplateEditorItem>();
        var collectionChanged = false;
        var valueChanged = false;

        _suppressTemplateItemChangeHandling = true;
        try
        {
            foreach (var item in TemplateItems.ToList())
            {
                var includedAssetIds = item.IncludedAssetIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();

                if (!IsTemplateItemIndividualMode(item) || includedAssetIds.Count == 0)
                {
                    rebuiltItems.Add(item);
                    continue;
                }

                if (includedAssetIds.Count == 1)
                {
                    valueChanged |= ConfigureIndividualTemplateItemFromAsset(
                        item,
                        includedAssetIds[0],
                        assetLookup,
                        item.Note);
                    rebuiltItems.Add(item);
                    continue;
                }

                collectionChanged = true;
                var sourceNote = item.Note;
                for (var index = 0; index < includedAssetIds.Count; index++)
                {
                    var assetId = includedAssetIds[index];
                    var targetItem = index == 0
                        ? item
                        : new RentalBillingTemplateEditorItem
                        {
                            ItemId = Guid.NewGuid(),
                            BillingLineMode = "개별",
                            Note = sourceNote
                        };

                    ConfigureIndividualTemplateItemFromAsset(targetItem, assetId, assetLookup, sourceNote);
                    if (index > 0)
                        WireTemplateItem(targetItem);

                    rebuiltItems.Add(targetItem);
                }
            }

            if (collectionChanged)
            {
                TemplateItems.Clear();
                foreach (var item in rebuiltItems)
                    TemplateItems.Add(item);
            }
        }
        finally
        {
            _suppressTemplateItemChangeHandling = false;
        }

        if (collectionChanged)
        {
            SelectedTemplateItem = previousSelectedIncludedAssetId.HasValue
                ? TemplateItems.FirstOrDefault(item => item.IncludedAssetIds.Contains(previousSelectedIncludedAssetId.Value))
                  ?? TemplateItems.FirstOrDefault(item => item.ItemId == previousSelectedItemId)
                  ?? TemplateItems.FirstOrDefault()
                : TemplateItems.FirstOrDefault(item => item.ItemId == previousSelectedItemId)
                  ?? TemplateItems.FirstOrDefault();
        }

        return collectionChanged || valueChanged;
    }

    private bool ConfigureIndividualTemplateItemFromAsset(
        RentalBillingTemplateEditorItem item,
        Guid assetId,
        IReadOnlyDictionary<Guid, RentalBillingAssetOption> assetLookup,
        string sourceNote)
    {
        assetLookup.TryGetValue(assetId, out var asset);
        var displayItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset?.ItemName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(displayItemName))
            displayItemName = string.IsNullOrWhiteSpace(item.DisplayItemName) ? "렌탈 임대료" : item.DisplayItemName.Trim();

        var unitPrice = asset is null ? Math.Max(0m, item.UnitPrice) : Math.Max(0m, asset.MonthlyFee);
        var specification = BuildAssetInvoiceSpecification(asset);
        var materialNumber = asset?.ManagementNumber?.Trim() ?? string.Empty;
        var changed = false;
        changed |= SetIfChanged(() => item.DisplayItemName, value => item.DisplayItemName = value, displayItemName);
        changed |= SetIfChanged(() => item.BillingLineMode, value => item.BillingLineMode = value, "개별");
        changed |= SetIfChanged(() => item.Specification, value => item.Specification = value, specification);
        changed |= SetIfChanged(() => item.MaterialNumber, value => item.MaterialNumber = value, materialNumber);
        changed |= SetIfChanged(() => item.RepresentativeAssetId, value => item.RepresentativeAssetId = value, null);
        changed |= SetIfChanged(() => item.Quantity, value => item.Quantity = value, 1m);
        changed |= SetIfChanged(() => item.UnitPrice, value => item.UnitPrice = value, unitPrice);
        changed |= SetIfChanged(() => item.Note, value => item.Note = value, sourceNote ?? string.Empty);

        if (item.IncludedAssetIds.Count != 1 || item.IncludedAssetIds[0] != assetId)
        {
            item.IncludedAssetIds.Clear();
            item.IncludedAssetIds.Add(assetId);
            changed = true;
        }

        item.NormalizeCalculatedAmount();
        item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
        item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
        return changed;
    }

    private static bool SetIfChanged<T>(Func<T> currentValue, Action<T> setValue, T newValue)
    {
        if (EqualityComparer<T>.Default.Equals(currentValue(), newValue))
            return false;

        setValue(newValue);
        return true;
    }

    private Dictionary<Guid, RentalBillingAssetOption> BuildBillingAssetOptionLookup()
        => _includedAssetPool
            .Concat(_candidateAssetPool)
            .Concat(IncludedAssets)
            .Concat(CandidateAssets)
            .Where(asset => asset.AssetId != Guid.Empty)
            .GroupBy(asset => asset.AssetId)
            .ToDictionary(group => group.Key, group => group.First());

    private string GetTemplateItemEffectiveLineMode(RentalBillingTemplateEditorItem item)
    {
        var billingType = (EditBillingType ?? string.Empty).Trim();
        return ResolveTemplateBillingLineMode(item.BillingLineMode, billingType);
    }

    private bool IsTemplateItemBundleMode(RentalBillingTemplateEditorItem item)
        => string.Equals(GetTemplateItemEffectiveLineMode(item), "묶음", StringComparison.Ordinal);

    private bool IsTemplateItemIndividualMode(RentalBillingTemplateEditorItem item)
        => string.Equals(GetTemplateItemEffectiveLineMode(item), "개별", StringComparison.Ordinal);

    private void NormalizeTemplateRepresentativeAssets()
    {
        foreach (var item in TemplateItems)
        {
            var representativeAssetId = ResolveTemplateRepresentativeAssetId(item);
            if (item.RepresentativeAssetId != representativeAssetId)
                item.RepresentativeAssetId = representativeAssetId;

            item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        }
    }

    private Guid? ResolveTemplateRepresentativeAssetId(RentalBillingTemplateEditorItem item)
    {
        if (!IsTemplateItemBundleMode(item))
            return null;

        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return null;

        return item.RepresentativeAssetId.HasValue && includedAssetIds.Contains(item.RepresentativeAssetId.Value)
            ? item.RepresentativeAssetId.Value
            : includedAssetIds[0];
    }

    private string BuildRepresentativeAssetSummary(RentalBillingTemplateEditorItem item)
    {
        if (!IsTemplateItemBundleMode(item))
            return "개별 청구";

        var representativeAssetId = ResolveTemplateRepresentativeAssetId(item);
        if (!representativeAssetId.HasValue)
            return "대표자산 미지정";

        var asset = FindBillingAssetOption(representativeAssetId.Value);
        return asset is null
            ? "대표자산 지정됨"
            : BuildAssetShortLabel(asset);
    }

    private static string BuildAssetShortLabel(RentalBillingAssetOption asset)
    {
        var itemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        if (!string.IsNullOrWhiteSpace(asset.MachineNumber))
            return $"{asset.MachineNumber} {itemName}".Trim();
        if (!string.IsNullOrWhiteSpace(asset.ManagementNumber))
            return $"{asset.ManagementNumber} {itemName}".Trim();
        return string.IsNullOrWhiteSpace(itemName) ? "선택 장비" : itemName;
    }

    private void ApplyTemplateSalesFieldDefaults(RentalBillingTemplateEditorItem item)
    {
        var defaultSpecification = BuildDefaultTemplateSpecification(item);
        if (ShouldRefreshTemplateSpecification(item, defaultSpecification))
            item.Specification = defaultSpecification;

        if (string.IsNullOrWhiteSpace(item.MaterialNumber))
            item.MaterialNumber = BuildDefaultTemplateMaterialNumber(item);
    }

    private bool ShouldRefreshTemplateSpecification(RentalBillingTemplateEditorItem item, string defaultSpecification)
    {
        if (string.IsNullOrWhiteSpace(defaultSpecification))
            return false;

        var current = (item.Specification ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(current))
            return true;

        if (string.Equals(current, defaultSpecification, StringComparison.CurrentCultureIgnoreCase))
            return false;

        if (IsTemplateSpecificationPlaceholder(current))
            return true;

        if (IsBundleRepresentativeOnlySpecification(item, current, defaultSpecification))
            return true;

        var legacyDefaultSpecification = BuildLegacyTemplateSpecification(item);
        return !string.IsNullOrWhiteSpace(legacyDefaultSpecification) &&
               !string.Equals(legacyDefaultSpecification, defaultSpecification, StringComparison.CurrentCultureIgnoreCase) &&
               string.Equals(current, legacyDefaultSpecification, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsTemplateSpecificationPlaceholder(string specification)
        => string.Equals(specification, "대표 장비", StringComparison.CurrentCultureIgnoreCase) ||
           string.Equals(specification.Trim(), "대표 장비 외", StringComparison.CurrentCultureIgnoreCase) ||
           specification.Trim().StartsWith("대표 장비 외 ", StringComparison.CurrentCultureIgnoreCase) ||
           string.Equals(specification, "장비별 개별 표시", StringComparison.CurrentCultureIgnoreCase);

    private string BuildDefaultTemplateSpecification(RentalBillingTemplateEditorItem item)
    {
        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return string.Empty;

        if (IsTemplateItemBundleMode(item))
        {
            var representativeAssetId = ResolveTemplateRepresentativeAssetId(item);
            var representativeAsset = representativeAssetId.HasValue
                ? FindBillingAssetOption(representativeAssetId.Value)
                : null;
            representativeAsset ??= includedAssetIds
                .Select(FindBillingAssetOption)
                .FirstOrDefault(asset => asset is not null);
            var resolvedRepresentativeAssetId = representativeAsset?.AssetId ?? representativeAssetId;
            var representativeSpecification = BuildAssetInvoiceSpecification(representativeAsset);
            if (string.IsNullOrWhiteSpace(representativeSpecification))
                representativeSpecification = FirstNonEmpty(
                    RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName),
                    "대표 장비");

            var otherCount = includedAssetIds
                .Where(id => !resolvedRepresentativeAssetId.HasValue || id != resolvedRepresentativeAssetId.Value)
                .Distinct()
                .Count();

            return otherCount <= 0
                ? representativeSpecification
                : $"{representativeSpecification} 외";
        }

        if (includedAssetIds.Count == 1)
        {
            var asset = FindBillingAssetOption(includedAssetIds[0]);
            return BuildAssetInvoiceSpecification(asset);
        }

        return "장비별 개별 표시";
    }

    private bool IsBundleRepresentativeOnlySpecification(
        RentalBillingTemplateEditorItem item,
        string currentSpecification,
        string defaultSpecification)
    {
        if (!IsTemplateItemBundleMode(item) ||
            string.IsNullOrWhiteSpace(currentSpecification) ||
            string.IsNullOrWhiteSpace(defaultSpecification))
        {
            return false;
        }

        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count <= 1)
            return false;

        var representativeAssetId = ResolveTemplateRepresentativeAssetId(item);
        var representativeAsset = representativeAssetId.HasValue
            ? FindBillingAssetOption(representativeAssetId.Value)
            : null;
        representativeAsset ??= includedAssetIds
            .Select(FindBillingAssetOption)
            .FirstOrDefault(asset => asset is not null);

        var representativeSpecification = BuildAssetInvoiceSpecification(representativeAsset);
        if (string.IsNullOrWhiteSpace(representativeSpecification))
            representativeSpecification = FirstNonEmpty(
                RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName),
                "대표 장비");

        return string.Equals(currentSpecification.Trim(), representativeSpecification, StringComparison.CurrentCultureIgnoreCase) &&
               string.Equals(defaultSpecification.Trim(), $"{representativeSpecification} 외", StringComparison.CurrentCultureIgnoreCase);
    }

    private string BuildLegacyTemplateSpecification(RentalBillingTemplateEditorItem item)
    {
        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return string.Empty;

        if (IsTemplateItemBundleMode(item))
        {
            var representativeAssetId = ResolveTemplateRepresentativeAssetId(item);
            var representativeAsset = representativeAssetId.HasValue
                ? FindBillingAssetOption(representativeAssetId.Value)
                : null;
            representativeAsset ??= includedAssetIds
                .Select(FindBillingAssetOption)
                .FirstOrDefault(asset => asset is not null);
            var resolvedRepresentativeAssetId = representativeAsset?.AssetId ?? representativeAssetId;
            var representativeItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(representativeAsset?.ItemName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(representativeItemName))
                representativeItemName = "대표 장비";

            var otherCount = includedAssetIds
                .Where(id => !resolvedRepresentativeAssetId.HasValue || id != resolvedRepresentativeAssetId.Value)
                .Distinct()
                .Count();
            var otherCategories = includedAssetIds
                .Where(id => !resolvedRepresentativeAssetId.HasValue || id != resolvedRepresentativeAssetId.Value)
                .Select(FindBillingAssetOption)
                .Where(asset => asset is not null)
                .Select(asset => asset!.ItemCategoryName?.Trim() ?? string.Empty)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (otherCategories.Count == 1)
                return $"{representativeItemName} 외 {otherCategories[0]}";

            return otherCount <= 0
                ? representativeItemName
                : $"{representativeItemName} 외 {otherCount:N0}대";
        }

        if (includedAssetIds.Count == 1)
        {
            var asset = FindBillingAssetOption(includedAssetIds[0]);
            return RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset?.ItemName ?? string.Empty);
        }

        return "장비별 개별 표시";
    }

    private static string BuildAssetInvoiceSpecification(RentalBillingAssetOption? asset)
        => BuildAssetInvoiceSpecification(asset?.ItemName, asset?.Manufacturer);

    private static string BuildAssetInvoiceSpecification(string? itemName, string? manufacturer)
    {
        var normalizedItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(itemName);
        var normalizedManufacturer = RentalCatalogValueNormalizer.NormalizeDisplayText(manufacturer);
        if (string.IsNullOrWhiteSpace(normalizedManufacturer) || string.IsNullOrWhiteSpace(normalizedItemName))
            return normalizedItemName;

        var itemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedItemName);
        var manufacturerKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedManufacturer);
        if (string.IsNullOrWhiteSpace(manufacturerKey) ||
            itemKey.StartsWith(manufacturerKey, StringComparison.OrdinalIgnoreCase) ||
            itemKey.Contains(manufacturerKey, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedItemName;
        }

        return $"{normalizedManufacturer} {normalizedItemName}".Trim();
    }

    private string BuildDefaultTemplateMaterialNumber(RentalBillingTemplateEditorItem item)
    {
        var targetAssetId = IsTemplateItemBundleMode(item)
            ? ResolveTemplateRepresentativeAssetId(item)
            : item.IncludedAssetIds.FirstOrDefault(id => id != Guid.Empty);
        if (!targetAssetId.HasValue || targetAssetId.Value == Guid.Empty)
            return string.Empty;

        var asset = FindBillingAssetOption(targetAssetId.Value);
        return asset?.ManagementNumber?.Trim() ?? string.Empty;
    }

    private void EnsureTemplateMonthlyFeesInPendingAssetEdits()
    {
        foreach (var item in TemplateItems)
            ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
    }

    private void ApplyTemplateMonthlyFeesToPendingAssetEdits(RentalBillingTemplateEditorItem item)
    {
        if (!TryResolveTemplateMonthlyFeeForLinkedAssets(item, out var monthlyFee))
            return;

        foreach (var assetId in item.IncludedAssetIds.Where(id => id != Guid.Empty).Distinct())
        {
            var edit = GetOrCreatePendingAssetLinkEdit(assetId);
            edit.MonthlyFee = monthlyFee;
            SetCachedAssetMonthlyFee(assetId, monthlyFee);
        }
    }

    private RentalBillingAssetLinkEdit GetOrCreatePendingAssetLinkEdit(Guid assetId)
    {
        if (_pendingAssetLinkEdits.TryGetValue(assetId, out var edit))
            return edit;

        var asset = FindBillingAssetOption(assetId);
        edit = asset is null
            ? new RentalBillingAssetLinkEdit
            {
                AssetId = assetId,
                CustomerId = EditCustomerId,
                CustomerName = EditCustomerName
            }
            : BuildAssetLinkEdit(asset);

        _pendingAssetLinkEdits[assetId] = edit;
        return edit;
    }

    private bool TryResolveTemplateMonthlyFeeForLinkedAssets(
        RentalBillingTemplateEditorItem item,
        out decimal monthlyFee)
    {
        monthlyFee = 0m;
        var includedAssetIds = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return false;

        if (includedAssetIds.Count == 1)
        {
            monthlyFee = item.EffectiveAmount;
            return monthlyFee >= 0m;
        }

        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        if (unitPrice <= 0m || quantity != includedAssetIds.Count)
            return false;

        monthlyFee = unitPrice;
        return true;
    }

    private RentalBillingAssetOption? FindBillingAssetOption(Guid assetId)
        => _includedAssetPool
            .Concat(_candidateAssetPool)
            .Concat(IncludedAssets)
            .Concat(CandidateAssets)
            .FirstOrDefault(asset => asset.AssetId == assetId);

    private void SetCachedAssetMonthlyFee(Guid assetId, decimal monthlyFee)
    {
        foreach (var asset in _includedAssetPool
                     .Concat(_candidateAssetPool)
                     .Concat(IncludedAssets)
                     .Concat(CandidateAssets)
                     .Where(asset => asset.AssetId == assetId))
        {
            asset.MonthlyFee = monthlyFee;
        }
    }

    private static bool IsTemplatePriceProperty(string? propertyName)
        => string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Quantity), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.UnitPrice), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Amount), StringComparison.Ordinal);

    private static bool IsTemplateEditorChangeProperty(string? propertyName)
        => string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.DisplayItemName), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.BillingLineMode), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Specification), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Unit), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.MaterialNumber), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.RepresentativeAssetId), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Quantity), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.UnitPrice), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Amount), StringComparison.Ordinal) ||
           string.Equals(propertyName, nameof(RentalBillingTemplateEditorItem.Note), StringComparison.Ordinal);

    private List<RentalBillingTemplateItemModel> ToTemplateModels()
        => TemplateItems.Select(item => new RentalBillingTemplateItemModel
        {
            ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
            DisplayItemName = (item.DisplayItemName ?? string.Empty).Trim(),
            BillingLineMode = ResolveTemplateBillingLineMode(item.BillingLineMode, EditBillingType),
            Specification = (item.Specification ?? string.Empty).Trim(),
            Unit = (item.Unit ?? string.Empty).Trim(),
            MaterialNumber = (item.MaterialNumber ?? string.Empty).Trim(),
            RepresentativeAssetId = ResolveTemplateRepresentativeAssetId(item),
            Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
            UnitPrice = Math.Max(0m, item.UnitPrice),
            Amount = item.EffectiveAmount,
            Note = (item.Note ?? string.Empty).Trim(),
            IncludedAssetIds = item.IncludedAssetIds.Distinct().ToList()
        }).ToList();

    private void UpdateTemplateDerivedValues()
    {
        if (_updatingTemplateDerivedValues)
            return;

        _updatingTemplateDerivedValues = true;
        try
        {
            NormalizeTemplateRepresentativeAssets();
            foreach (var item in TemplateItems)
            {
                item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
                item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
                ApplyTemplateSalesFieldDefaults(item);
                if (item.Quantity <= 0m)
                    item.Quantity = 1m;
                item.NormalizeCalculatedAmount();
            }

            EditMonthlyAmount = TemplateItems.Sum(item => item.EffectiveAmount);
            EditItemName = BuildTemplateItemName();
            EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - EditSettledAmount);
            var linkedAssetCount = TemplateItems.SelectMany(item => item.IncludedAssetIds).Distinct().Count();
            TemplateSummary = $"표시품목 {TemplateItems.Count:N0}건 / 연결장비 {linkedAssetCount:N0}대";
            BillingSchedulePreviewText = BuildBillingSchedulePreview();
            DocumentIssuePreviewText = BuildDocumentIssuePreview();
            ApplySelectedAssetsHint = BuildApplySelectedAssetsHint(linkedAssetCount);
            SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSetRepresentativeAsset));
            OnPropertyChanged(nameof(CanApplySelectedAssets));
        }
        finally
        {
            _updatingTemplateDerivedValues = false;
        }
    }

    private string BuildTemplateItemName()
    {
        if (TemplateItems.Count == 0)
            return string.Empty;
        if (TemplateItems.Count == 1)
            return (TemplateItems[0].DisplayItemName ?? string.Empty).Trim();
        return $"{(TemplateItems[0].DisplayItemName ?? string.Empty).Trim()} 외 {TemplateItems.Count - 1}건";
    }

    private string BuildIncludedAssetSummary(IEnumerable<Guid> assetIds)
    {
        var ids = assetIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return LinkAssetsLater ? "장비 나중 연결" : "연결 장비 없음";

        var labels = _includedAssetPool
            .Concat(_candidateAssetPool)
            .Where(asset => ids.Contains(asset.AssetId))
            .Select(asset => string.IsNullOrWhiteSpace(asset.MachineNumber)
                ? string.IsNullOrWhiteSpace(asset.ManagementNumber)
                    ? asset.ItemName
                    : $"{asset.ManagementNumber} {asset.ItemName}".Trim()
                : $"{asset.MachineNumber} {asset.ItemName}".Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToList();

        return labels.Count == 0
            ? $"{ids.Count:N0}대 연결"
            : ids.Count > labels.Count
                ? $"{string.Join(", ", labels)} 외 {ids.Count - labels.Count}대"
                : string.Join(", ", labels);
    }

    private bool TryRejectAggregateSelection(string actionName)
    {
        if (SelectedRow is null || !SelectedRow.IsAggregateRow)
            return false;

        var aggregateSummary = string.IsNullOrWhiteSpace(SelectedRow.AggregateSummary)
            ? "여러 청구 프로필/자산이 묶인 거래처 그룹입니다."
            : $"{SelectedRow.AggregateSummary} 거래처 그룹입니다.";
        StatusMessage = $"{aggregateSummary} {actionName}은 '개별 청구건 보기'로 개별 프로필을 표시한 뒤 진행하세요.";
        return true;
    }

    private void LoadAggregateSelectionSummary(RentalBillingViewRow value)
    {
        _contractDateRefreshCts?.Cancel();
        _pendingAssetLinkEdits.Clear();
        TemplateItems.Clear();
        SelectedTemplateItem = null;
        _includedAssetPool.Clear();
        _candidateAssetPool.Clear();
        IncludedAssets.Clear();
        CandidateAssets.Clear();
        TemplateSummary = string.IsNullOrWhiteSpace(value.AggregateSummary)
            ? "거래처별 묶음 보기입니다."
            : value.AggregateSummary;
        AssetCandidateSummary = "거래처 그룹에서는 장비 연결 편집을 지원하지 않습니다. 개별 청구건 보기로 전환한 뒤 청구 프로필을 선택하세요.";
        ApplySelectedAssetsHint = value.GroupedPersistedProfileIds.Any(id => id != Guid.Empty)
            ? "거래처별 묶음 보기입니다. 청구 시작은 연결된 개별 프로필을 한 번에 처리하고, 저장/삭제는 개별 청구건 보기에서 진행하세요."
            : "거래처별 묶음 보기입니다. 청구 가능한 프로필이 없습니다. 개별 청구건 보기에서 프로필을 생성/저장하세요.";
        _selectedRowBaselineSignature = BuildCurrentEditorSignature();
        StatusMessage = string.IsNullOrWhiteSpace(value.AggregateSummary)
            ? "거래처별 묶음 보기입니다. 청구 시작은 연결된 개별 프로필을 한 번에 처리하고, 편집은 개별 청구건 보기로 진행하세요."
            : $"{value.AggregateSummary} 기준 거래처 그룹입니다. 청구 시작은 연결된 개별 프로필을 한 번에 처리하고, 편집은 개별 청구건 보기로 진행하세요.";
    }

    private bool CanOperateScope(string? officeCode)
    {
        if (CanManageAll)
            return true;

        if (_session.HasGlobalDataScope)
            return true;

        var rowOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        if (string.IsNullOrWhiteSpace(rowOffice))
            return false;

        if (string.Equals(_session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetOfficeCodesForTenant(_session.TenantCode)
                .Any(code => string.Equals(
                    OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(code, DomainConstants.OfficeUsenet),
                    rowOffice,
                    StringComparison.OrdinalIgnoreCase));
        }

        var userOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        return string.Equals(rowOffice, userOffice, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryValidateTemplateConfiguration(out string message)
    {
        message = string.Empty;
        if (TemplateItems.Count == 0)
        {
            message = "청구항목을 하나 이상 입력하세요.";
            return false;
        }

        if (TemplateItems.Any(item => string.IsNullOrWhiteSpace(item.DisplayItemName)))
        {
            message = "표시 품목명은 비워둘 수 없습니다.";
            return false;
        }

        var missingRepresentativeItem = TemplateItems.FirstOrDefault(item =>
            IsTemplateItemBundleMode(item) &&
            item.IncludedAssetIds.Any(id => id != Guid.Empty) &&
            (!item.RepresentativeAssetId.HasValue ||
             !item.IncludedAssetIds.Contains(item.RepresentativeAssetId.Value)));
        if (missingRepresentativeItem is not null)
        {
            message = $"묶음 청구항목 '{missingRepresentativeItem.DisplayItemName}'의 대표자산을 지정하세요.";
            return false;
        }

        if (TemplateItems.Any(item => string.IsNullOrWhiteSpace(NormalizeBillingLineModeValue(item.BillingLineMode))))
        {
            message = "모든 청구항목에 청구 유형(묶음/개별)을 지정해야 합니다.";
            return false;
        }

        var effectiveBillingType = ResolveProfileBillingTypeFromTemplateItems(ToTemplateModels(), EditBillingType);
        if (!string.Equals(EditBillingType, effectiveBillingType, StringComparison.Ordinal))
            EditBillingType = effectiveBillingType;

        return true;
    }

    private bool HasUnsavedSelectedRowChanges()
    {
        if (SelectedRow is null)
            return false;

        return !string.Equals(_selectedRowBaselineSignature, BuildCurrentEditorSignature(), StringComparison.Ordinal);
    }

    private string BuildCurrentEditorSignature()
        => string.Join("||",
            EditCustomerId?.ToString("N") ?? string.Empty,
            NormalizeText(EditCustomerName),
            NormalizeText(EditBusinessNumber),
            NormalizeText(EditInstallLocation),
            NormalizeText(EditItemName),
            NormalizeBillingLineModeValue(EditBillingType),
            NormalizeBillingAdvanceModeValue(EditBillingAdvanceMode),
            NormalizeText(EditOfficeCode),
            NormalizeText(EditBillingMethod),
            NormalizeText(EditBillingStatus),
            NormalizeText(EditSettlementStatus),
            NormalizeText(EditCompletionStatus),
            NormalizeText(EditEmail),
            NormalizeText(EditBillingDayMode),
            EditBillingDay.ToString(CultureInfo.InvariantCulture),
            EditBillingCycleMonths.ToString(CultureInfo.InvariantCulture),
            EditBillingAnchorMonth.ToString(CultureInfo.InvariantCulture),
            NormalizeText(EditDocumentIssueMode),
            EditDocumentLeadDays.ToString(CultureInfo.InvariantCulture),
            NormalizeDecimal(EditMonthlyAmount),
            NormalizeDecimal(EditDepositAmount),
            NormalizeDecimal(EditSettledAmount),
            NormalizeDecimal(EditOutstandingAmount),
            EditRequiresFollowUp ? "Y" : "N",
            NormalizeText(EditSubmissionDocuments),
            NormalizeText(EditNotes),
            LinkAssetsLater ? "Y" : "N",
            NormalizeNullableDate(EditBillingAnchorDate),
            NormalizeNullableDate(EditBillingStartDate),
            NormalizeNullableDate(EditContractDate),
            NormalizeNullableDate(EditContractStartDate),
            NormalizeNullableDate(EditContractEndDate),
            NormalizeNullableDate(EditLastBilledDate),
            NormalizeNullableDate(EditLastSettledDate),
            EditIsActive ? "Y" : "N",
            BuildTemplateSignature(ToTemplateModels(), EditBillingType),
            BuildAssetLinkEditSignature(BuildPendingAssetLinkEdits()));

    private string BuildBillingSchedulePreview()
    {
        if (!EditContractDate.HasValue)
            return "계약 체결일을 확인하면 다음 청구일이 표시됩니다.";

        var referenceDate = GetBillingReferenceDate();
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(EditBillingCycleMonths);
        var anchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            cycleMonths,
            EditBillingAnchorMonth,
            ToDateOnly(EditBillingAnchorDate),
            ToDateOnly(EditBillingStartDate),
            ToDateOnly(EditContractStartDate),
            ToDateOnly(EditContractDate),
            ToDateOnly(EditLastBilledDate),
            referenceDate);
        var dueDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            EditBillingDay,
            EditBillingDayMode,
            cycleMonths,
            anchorMonth,
            referenceDate,
            ToDateOnly(EditLastBilledDate));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, EditBillingAdvanceMode, dueDate);
        var dayModeText = string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeEndOfMonth, StringComparison.Ordinal)
            ? "말일"
            : $"매월 {RentalBillingScheduleRules.NormalizeBillingDay(EditBillingDay)}일";
        var anchorText = cycleMonths == 1
            ? "매월"
            : $"{anchorMonth}월 기준";
        return $"청구 대상 기간: {period.StartDate:yyyy-MM} ~ {period.EndDate:yyyy-MM} / 청구일 규칙: {dayModeText} / 기준월: {anchorText} / 예상 결제일: {dueDate:yyyy-MM-dd}";
    }

    private string BuildDocumentIssuePreview()
    {
        if (!EditContractDate.HasValue)
            return "계약 체결일을 확인하면 예상 발송일이 표시됩니다.";

        var referenceDate = GetBillingReferenceDate();
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(EditBillingCycleMonths);
        var anchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            cycleMonths,
            EditBillingAnchorMonth,
            ToDateOnly(EditBillingAnchorDate),
            ToDateOnly(EditBillingStartDate),
            ToDateOnly(EditContractStartDate),
            ToDateOnly(EditContractDate),
            ToDateOnly(EditLastBilledDate),
            referenceDate);
        var dueDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            EditBillingDay,
            EditBillingDayMode,
            cycleMonths,
            anchorMonth,
            referenceDate,
            ToDateOnly(EditLastBilledDate));
        var issueDate = RentalBillingScheduleRules.CalculateDocumentIssueDate(dueDate, EditDocumentIssueMode, EditDocumentLeadDays);
        if (!issueDate.HasValue)
            return "서류 발송일을 계산할 수 없습니다.";

        var modeText = EditDocumentIssueMode switch
        {
            RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate => $"결제일 {RentalBillingScheduleRules.NormalizeDocumentLeadDays(EditDocumentLeadDays)}일 전",
            RentalBillingScheduleRules.DocumentIssueModePreviousBusinessDay => "결제일 직전 영업일",
            RentalBillingScheduleRules.DocumentIssueModePreviousMonthEnd => "전월 말일",
            _ => "결제일과 동일"
        };

        return $"서류 발송 규칙: {modeText} / 예상 발송일: {issueDate.Value:yyyy-MM-dd}";
    }

    private string BuildApplySelectedAssetsHint(int linkedAssetCount)
    {
        if (SelectedTemplateItem is null)
            return "청구서 표시 품목을 선택하면 새 장비연결과 내부 포함 장비 안내가 표시됩니다.";

        if (linkedAssetCount == 0)
            return "새 장비연결로 설치현황 자산을 선택해 현재 품목에 연결하세요. 저장하면 설치 거래처/담당지점/상세정보가 현재 거래처 기준으로 반영됩니다.";

        return $"현재 품목에 연결된 장비 {linkedAssetCount:N0}대를 확인했습니다. 새 장비연결로 자산을 더 추가하거나 내부 포함 장비에서 시리얼번호 기준으로 검토한 뒤 저장하세요.";
    }

    private static string BuildTemplateSignature(IEnumerable<RentalBillingTemplateItemModel> items, string billingType)
        => string.Join(";;", items.Select(item => BuildTemplateItemSignature(item, billingType)));

    private static string BuildAssetLinkEditSignature(IEnumerable<RentalBillingAssetLinkEdit> edits)
        => string.Join(
            ";;",
            edits
                .OrderBy(edit => edit.AssetId)
                .Select(edit => string.Join("|",
                    edit.AssetId.ToString("N"),
                    edit.CustomerId?.ToString("N") ?? string.Empty,
                    NormalizeText(edit.CustomerName),
                    NormalizeText(edit.InstallLocation),
                    NormalizeText(edit.InstallSiteName),
                    edit.MonthlyFee?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.ContractStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    NormalizeText(edit.Notes))));

    private static string BuildTemplateItemSignature(RentalBillingTemplateItemModel item, string billingType)
    {
        var effectiveMode = ResolveTemplateBillingLineMode(item.BillingLineMode, billingType);
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var amount = item.Amount > 0m ? item.Amount : Math.Max(0m, quantity) * unitPrice;
        var includedAssetIds = string.Join(",", item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .Select(id => id.ToString("N")));
        var representativeAssetId = string.Equals(effectiveMode, "묶음", StringComparison.Ordinal) &&
                                    item.RepresentativeAssetId.HasValue &&
                                    item.IncludedAssetIds.Contains(item.RepresentativeAssetId.Value)
            ? item.RepresentativeAssetId.Value.ToString("N")
            : string.Empty;

        return string.Join("|",
            NormalizeText(item.DisplayItemName),
            effectiveMode,
            NormalizeText(item.Specification),
            NormalizeText(item.Unit),
            NormalizeText(item.MaterialNumber),
            representativeAssetId,
            NormalizeDecimal(quantity),
            NormalizeDecimal(unitPrice),
            NormalizeDecimal(amount),
            NormalizeText(item.Note),
            includedAssetIds);
    }

    private static string ResolveDefaultTemplateBillingLineMode(string? defaultBillingType)
    {
        var normalizedDefault = NormalizeBillingLineModeValue(defaultBillingType);
        return string.IsNullOrWhiteSpace(normalizedDefault) ? "묶음" : normalizedDefault;
    }

    private static string ResolveTemplateBillingLineMode(string? itemBillingLineMode, string? defaultBillingType)
    {
        var normalizedItemMode = NormalizeBillingLineModeValue(itemBillingLineMode);
        return string.IsNullOrWhiteSpace(normalizedItemMode)
            ? ResolveDefaultTemplateBillingLineMode(defaultBillingType)
            : normalizedItemMode;
    }

    private static string ResolveProfileBillingTypeFromTemplateItems(
        IEnumerable<RentalBillingTemplateItemModel> templateItems,
        string? defaultBillingType)
    {
        var modes = (templateItems ?? Enumerable.Empty<RentalBillingTemplateItemModel>())
            .Select(item => ResolveTemplateBillingLineMode(item.BillingLineMode, defaultBillingType))
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return modes.Count switch
        {
            0 => ResolveDefaultTemplateBillingLineMode(defaultBillingType),
            1 => modes[0],
            _ => "혼합"
        };
    }

    private static string NormalizeBillingLineModeValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.Equals(trimmed, "개별", StringComparison.Ordinal)
            ? "개별"
            : string.Equals(trimmed, "묶음", StringComparison.Ordinal)
                ? "묶음"
                : string.Empty;
    }

    private static string NormalizeBillingAdvanceModeValue(string? value)
        => string.Equals((value ?? string.Empty).Trim(), "선불", StringComparison.Ordinal) ? "선불" : "후불";

    private static string FirstNonEmpty(params string?[] values)
        => values
            .Select(value => value?.Trim() ?? string.Empty)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim();

    private static string NormalizeDecimal(decimal value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string NormalizeNullableDate(DateTime? value)
        => value?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private void RequestFilterReload()
    {
        if (_suppressFilterReload)
            return;

        _searchDebouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(350),
            () => ReloadAsync(),
            ex => StatusMessage = $"렌탈 청구 목록을 다시 불러오지 못했습니다. {ex.Message}");
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);
}
