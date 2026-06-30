using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingViewModel : ObservableObject
{
    private readonly record struct IndividualTemplateMergeKey(
        string ModelNameKey,
        string Unit,
        string Note);

    private const string AllOption = "전체";
    private const int BillingHistoryDisplayLimit = 600;
    private const int AssignmentHistoryDisplayLimit = 300;

    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly ErpApiClient? _api;
    private readonly UiDebouncer _searchDebouncer = new();
    private CancellationTokenSource? _filterReloadCts;
    private CancellationTokenSource? _candidateAssetsLoadCts;
    private CancellationTokenSource? _includedAssetHistoryLoadCts;
    private CancellationTokenSource? _billingHistoryLoadCts;
    private CancellationTokenSource? _contractDateRefreshCts;
    private Task? _candidateAssetsLoadTask;
    private bool _suppressFilterReload;
    private bool _pendingFilterReload;
    private bool _suppressCandidateAssetSelectionChanges;
    private bool _suppressIncludedAssetRepresentativeChanges;
    private bool _suppressIncludedAssetMonthlyFeeChanges;
    private bool _suppressContractDateSynchronization;
    private bool _suppressTemplateItemChangeHandling;
    private bool _updatingTemplateDerivedValues;
    private bool _isDisposed;
    private readonly HashSet<RentalBillingViewRow> _observedBillingRows = new();
    private int _filterReloadVersion;
    private IReadOnlyList<LocalOffice>? _officeFilterSourceCache;
    private string _pendingFilterReloadSignature = string.Empty;
    private string _activeFilterReloadSignature = string.Empty;
    private string _activeCandidateAssetsLoadSignature = string.Empty;
    private readonly List<RentalBillingAssetOption> _includedAssetPool = new();
    private readonly List<RentalBillingAssetOption> _candidateAssetPool = new();
    private readonly Dictionary<Guid, RentalBillingAssetLinkEdit> _pendingAssetLinkEdits = new();
    private string _selectedRowBaselineSignature = string.Empty;
    private long _editRevision;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedOfficeFilter;
    [ObservableProperty] private string _selectedStatusFilter = AllOption;
    [ObservableProperty] private bool _dueOnly;
    [ObservableProperty] private bool _pastDueOnly;
    [ObservableProperty] private bool _showIndividualProfiles;
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 청구 대상을 불러오는 중입니다.";
    [ObservableProperty] private RentalBillingViewRow? _selectedRow;
    [ObservableProperty] private RentalBillingHistoryRow? _selectedBillingHistory;
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
    [ObservableProperty] private string _billingAssetCoverageWarning = string.Empty;
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
    [ObservableProperty] private string _applySelectedAssetsHint = "거래처 임대 자산에서 전표에 넣을 장비를 선택한 뒤 표시품목에 추가할 수 있습니다.";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _dueCount;
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _partialSettlementCount;
    [ObservableProperty] private int _pastUnresolvedCustomerCount;
    [ObservableProperty] private int _pastUnresolvedCount;
    [ObservableProperty] private decimal _pastUnresolvedAmount;
    [ObservableProperty] private decimal _totalOutstandingAmount;

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new ResettableObservableCollection<DisplayOption>();
    public ObservableCollection<DisplayOption> EditOfficeOptions { get; } = new ResettableObservableCollection<DisplayOption>();
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
    public ObservableCollection<RentalBillingViewRow> Rows { get; } = new ResettableObservableCollection<RentalBillingViewRow>();
    public ObservableCollection<RentalBillingHistoryRow> BillingHistoryRows { get; } = new ResettableObservableCollection<RentalBillingHistoryRow>();
    public ObservableCollection<RentalBillingTemplateEditorItem> TemplateItems { get; } = new ResettableObservableCollection<RentalBillingTemplateEditorItem>();
    public ObservableCollection<RentalBillingAssetOption> IncludedAssets { get; } = new ResettableObservableCollection<RentalBillingAssetOption>();
    public ObservableCollection<RentalAssetAssignmentHistoryViewItem> IncludedAssetAssignmentHistories { get; } = new ResettableObservableCollection<RentalAssetAssignmentHistoryViewItem>();
    public ObservableCollection<RentalBillingAssetOption> CandidateAssets { get; } = new ResettableObservableCollection<RentalBillingAssetOption>();

    public bool CanViewAll => _session.HasAdministrativePrivileges ||
                              _session.HasGlobalDataScope ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalEditAll);
    public bool CanManageAll => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalEditAll);
    private bool CanEditRentalProfiles => _session.HasAdministrativePrivileges ||
                                           _session.HasPermission(AppPermissionNames.RentalEditAll) ||
                                           _session.HasPermission(AppPermissionNames.RentalProfileEdit);
    private bool CanEditPayments => _session.HasAdministrativePrivileges ||
                                    _session.HasPermission(AppPermissionNames.PaymentEdit);
    private bool CanEditInvoices => _session.HasAdministrativePrivileges ||
                                    _session.HasPermission(AppPermissionNames.InvoiceEdit);
    public bool CanSave => CanEditRentalProfiles && (SelectedRow is null || (CanEditCurrentSelection && CanEditSelectedRowInEditor));
    public bool IsCustomerGroupSelection => SelectedRow?.IsAggregateRow == true;
    public bool CanEditBillingProfileDetails => SelectedRow is null || !SelectedRow.IsAggregateRow;
    public bool CanOpenAssetLinkDialog => CanEditBillingProfileDetails &&
                                          CanEditCurrentSelection &&
                                          !string.IsNullOrWhiteSpace(EditCustomerName);
    public bool CanEditTemplateLineMode => CanEditBillingProfileDetails &&
                                           string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal);
    public bool CanExpandSelectedSummary => SelectedRow?.IsAggregateRow == true && !ShowIndividualProfiles;
    public bool CanStartBillingSelected => SelectedRow is not null &&
                                           CanEditCurrentSelection &&
                                           CanEditInvoices &&
                                           (SelectedRow.IsAggregateRow
                                               ? SelectedRow.GroupedPersistedProfileIds.Any(id => id != Guid.Empty)
                                               : HasPersistedSelectedProfile);
    public bool CanHoldSelected => SelectedRow is not null && HasPersistedSelectedProfile && CanEditCurrentSelection && !SelectedRow.IsAggregateRow;
    public bool CanRegisterSettlementSelected => SelectedRow is not null && HasPersistedSelectedProfile && CanEditCurrentSelection && CanEditPayments && !SelectedRow.IsAggregateRow;
    public bool CanDeleteSelectedBillingHistory => SelectedRow is not null &&
                                                   SelectedBillingHistory is not null &&
                                                   HasPersistedSelectedProfile &&
                                                   CanEditCurrentSelection &&
                                                   !SelectedRow.IsAggregateRow &&
                                                   SelectedBillingHistory.BillingProfileId == SelectedRow.Source.Id &&
                                                   SelectedBillingHistory.CanDelete &&
                                                   CanDeleteSelectedBillingHistoryFinancialEffects;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection && !SelectedRow.IsAggregateRow;
    public bool CanDeleteChecked => Rows.Any(row => row.IsSelected && CanDeleteBillingRow(row));
    public bool CanMarkCompletedSelected => SelectedRow is not null &&
                                            HasPersistedSelectedProfile &&
                                            CanEditCurrentSelection &&
                                            !SelectedRow.IsAggregateRow &&
                                            SelectedRow.OutstandingAmount <= 0m;
    public bool CanRemoveTemplateItem => CanEditBillingProfileDetails && CanEditCurrentSelection && SelectedTemplateItem is not null;
    public bool CanMoveTemplateItemUp => CanEditBillingProfileDetails &&
                                         CanEditCurrentSelection &&
                                         SelectedTemplateItem is not null &&
                                         TemplateItems.IndexOf(SelectedTemplateItem) > 0;
    public bool CanMoveTemplateItemDown => CanEditBillingProfileDetails &&
                                           CanEditCurrentSelection &&
                                           SelectedTemplateItem is not null &&
                                           TemplateItems.IndexOf(SelectedTemplateItem) >= 0 &&
                                           TemplateItems.IndexOf(SelectedTemplateItem) < TemplateItems.Count - 1;
    public bool CanRemoveIncludedAsset => CanEditBillingProfileDetails &&
                                          CanEditCurrentSelection &&
                                          SelectedIncludedAsset is not null &&
                                          SelectedIncludedAsset.AssetId != Guid.Empty &&
                                          IsSelectedIncludedAssetLinkedForBilling();
    public bool CanAddSelectedIncludedAssetToTemplateItem => CanEditBillingProfileDetails &&
                                                             CanEditCurrentSelection &&
                                                             SelectedIncludedAsset is not null &&
                                                             SelectedIncludedAsset.AssetId != Guid.Empty;
    public bool CanSetRepresentativeAsset => CanEditBillingProfileDetails &&
                                             CanEditCurrentSelection &&
                                             SelectedIncludedAsset is not null &&
                                             SelectedIncludedAsset.AssetId != Guid.Empty &&
                                             TemplateItems.Count > 0;
    public bool CanAddIncludedAssetAssignmentHistory => CanEditBillingProfileDetails &&
                                                        SelectedIncludedAsset is not null &&
                                                        SelectedIncludedAsset.AssetId != Guid.Empty &&
                                                        CanEditCurrentSelection;
    public bool CanEditIncludedAssetAssignmentHistory => CanAddIncludedAssetAssignmentHistory &&
                                                         SelectedIncludedAssetAssignmentHistory is not null;
    public bool CanDeleteIncludedAssetAssignmentHistory => CanEditIncludedAssetAssignmentHistory;
    public bool CanApplySelectedAssets => CanEditBillingProfileDetails && CanEditCurrentSelection && SelectedTemplateItem is not null && CandidateAssets.Any(asset => asset.IsSelected);
    public bool CanOpenCustomerContract => EditCustomerId.HasValue && EditCustomerId.Value != Guid.Empty;
    public bool IsFixedBillingDayMode => string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal);
    public bool IsDocumentLeadDaysVisible => string.Equals(EditDocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate, StringComparison.Ordinal);
    public bool IsContractDateMissing => !EditContractDate.HasValue;
    public bool ShouldShowContractDateWarning => IsContractDateMissing && (EditCustomerId.HasValue || !string.IsNullOrWhiteSpace(EditCustomerName) || SelectedRow is not null);
    public string ContractDateWarningMessage => "계약 체결일을 확인할 수 없습니다. 저장은 가능하지만 청구 기준 검토가 필요합니다.";
    public bool HasPastUnresolved => PastUnresolvedCount > 0 || PastUnresolvedAmount > 0m;
    public bool HasBillingAssetCoverageWarning => !string.IsNullOrWhiteSpace(BillingAssetCoverageWarning);
    public string PastUnresolvedSummaryText => HasPastUnresolved
        ? $"과거 미처리 알림: 거래처 {PastUnresolvedCustomerCount:N0}곳 / 청구월 {PastUnresolvedCount:N0}건 / 총 미수 {PastUnresolvedAmount:N0}원"
        : "과거 미처리 입금 내역이 없습니다.";
    public bool SelectedRowHasPastUnresolved => SelectedRow?.HasPastUnresolved == true;
    public string SelectedPastUnresolvedSummaryText => SelectedRowHasPastUnresolved
        ? $"이 거래처는 이전 청구월 미처리 {SelectedRow!.PastUnresolvedCount:N0}건 / 미수 {SelectedRow.PastUnresolvedAmount:N0}원이 있습니다. 아래 '청구/입금 내역'에서 해당 월을 선택해 입금 등록하세요."
        : "선택 거래처의 이전 청구월 미처리 내역이 없습니다.";
    public LocalStateService LocalStateService => _local;
    public RentalStateService RentalStateService => _rental;
    public SessionState SessionState => _session;
    public Guid? InvoiceToOpenAfterClose { get; private set; }

    private bool CanAccessCurrentSelection => SelectedRow is null || CanOperateScope(
        ResolveProfileOfficeCode(SelectedRow.Source, _session.OfficeCode));

    private bool HasPersistedSelectedProfile => SelectedRow?.HasPersistedProfile == true;
    private bool CanEditSelectedRowInEditor => SelectedRow is null || !SelectedRow.IsAggregateRow;

    private bool CanEditCurrentSelection => CanEditRentalProfiles && (SelectedRow is null || CanOperateScope(
        ResolveProfileOfficeCode(SelectedRow.Source, _session.OfficeCode)));

    private bool CanDeleteSelectedBillingHistoryFinancialEffects =>
        SelectedBillingHistory is null ||
        ((!SelectedBillingHistory.HasInvoice || CanEditInvoices) &&
         (!SelectedBillingHistory.HasSettlement || CanEditPayments));

    public RentalBillingViewModel(RentalStateService rental, LocalStateService local, SessionState session, ErpApiClient? api = null)
    {
        _rental = rental;
        _local = local;
        _session = session;
        _api = api ?? App.TryGetService<ErpApiClient>();

        StatusOptions.Add(AllOption);
        StatusOptions.Add("활성");
        StatusOptions.Add("비활성");
        StatusOptions.Add("예정");
        StatusOptions.Add("청구중");
        StatusOptions.Add("보류");
        StatusOptions.Add("완료");
        StatusOptions.Add("미수");
        StatusOptions.Add("청구설정 필요");

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
    partial void OnPastDueOnlyChanged(bool value) => RequestFilterReload();
    partial void OnPastUnresolvedCustomerCountChanged(int value) => OnPropertyChanged(nameof(PastUnresolvedSummaryText));
    partial void OnPastUnresolvedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPastUnresolved));
        OnPropertyChanged(nameof(PastUnresolvedSummaryText));
    }
    partial void OnPastUnresolvedAmountChanged(decimal value)
    {
        OnPropertyChanged(nameof(HasPastUnresolved));
        OnPropertyChanged(nameof(PastUnresolvedSummaryText));
    }
    partial void OnSelectedBillingHistoryChanged(RentalBillingHistoryRow? value)
    {
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelectedBillingHistory));
        DeleteSelectedBillingHistoryCommand.NotifyCanExecuteChanged();
    }
    partial void OnShowIndividualProfilesChanged(bool value)
    {
        NotifySelectionActionState();
        RequestFilterReload();
    }
    partial void OnReferenceDateChanged(DateOnly value)
    {
        UpdateTemplateDerivedValues();
        if (_local is not null)
            RequestFilterReload();
    }
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
        ApplyBillingTypeToTemplateLineModes(value);
        EnsureAllIncludedAssetsAssignedForBillingType();
        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        RefreshTemplateAmountsFromIncludedAssets(applyZeroFees: true);
        RefreshBillingAssetCollections();
        UpdateTemplateDerivedValues();
        OnPropertyChanged(nameof(CanEditTemplateLineMode));
    }

    partial void OnBillingAssetCoverageWarningChanged(string value)
        => OnPropertyChanged(nameof(HasBillingAssetCoverageWarning));
    partial void OnSelectedTemplateItemChanged(RentalBillingTemplateEditorItem? value)
    {
        NormalizeTemplateRepresentativeAssets();
        SyncAssetSelectionFromTemplate();
        SyncIncludedAssetsFromTemplate();
        UpdateTemplateDerivedValues();
        NotifyTemplateItemMoveState();
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        OnPropertyChanged(nameof(CanAddSelectedIncludedAssetToTemplateItem));
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        OnPropertyChanged(nameof(CanApplySelectedAssets));
        AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedIncludedAssetChanged(RentalBillingAssetOption? value)
    {
        SelectedIncludedAssetAssignmentHistory = null;
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddSelectedIncludedAssetToTemplateItem));
        AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
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
        CancelIncludedAssetHistoryLoad();
        var cts = new CancellationTokenSource();
        _includedAssetHistoryLoadCts = cts;
        var ct = cts.Token;
        try
        {
            if (assetId == Guid.Empty)
            {
                ApplyIncludedAssetAssignmentHistoriesForDisplay(Array.Empty<RentalAssetAssignmentHistoryViewItem>());
                SelectedIncludedAssetAssignmentHistory = null;
                NotifyIncludedAssetAssignmentHistoryCommandState();
                return;
            }

            var histories = await _rental.GetAssetAssignmentHistoriesAsync(assetId, AssignmentHistoryDisplayLimit, _session, ct);
            ct.ThrowIfCancellationRequested();
            if (SelectedIncludedAsset?.AssetId != assetId)
                return;

            var selectedHistoryId = SelectedIncludedAssetAssignmentHistory?.HistoryId;
            ApplyIncludedAssetAssignmentHistoriesForDisplay(histories);
            SelectedIncludedAssetAssignmentHistory = selectedHistoryId.HasValue
                ? IncludedAssetAssignmentHistories.FirstOrDefault(history => history.HistoryId == selectedHistoryId.Value)
                : IncludedAssetAssignmentHistories.FirstOrDefault();
            NotifyIncludedAssetAssignmentHistoryCommandState();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_includedAssetHistoryLoadCts, cts))
                _includedAssetHistoryLoadCts = null;
            cts.Dispose();
        }
    }

    private void CancelIncludedAssetHistoryLoad()
    {
        _includedAssetHistoryLoadCts?.Cancel();
        _includedAssetHistoryLoadCts?.Dispose();
        _includedAssetHistoryLoadCts = null;
    }

    private void ApplyIncludedAssetAssignmentHistoriesForDisplay(IReadOnlyList<RentalAssetAssignmentHistoryViewItem> histories)
    {
        var displayRows = LimitAssignmentHistoriesForDisplay(histories);
        IncludedAssetAssignmentHistories.ReplaceWith(displayRows);

        if (histories.Count > displayRows.Count)
        {
            StatusMessage = $"포함 장비 임대이력 {histories.Count:N0}건 중 최근 {displayRows.Count:N0}건만 먼저 표시합니다. 오래된 이력이 필요하면 자산관리에서 해당 장비를 직접 확인하세요.";
        }
    }

    private static IReadOnlyList<RentalAssetAssignmentHistoryViewItem> LimitAssignmentHistoriesForDisplay(IReadOnlyList<RentalAssetAssignmentHistoryViewItem> histories)
        => histories.Count > AssignmentHistoryDisplayLimit
            ? histories.Take(AssignmentHistoryDisplayLimit).ToList()
            : histories;

    public async Task LoadAsync()
    {
        StatusMessage = "렌탈 청구관리 화면을 준비하는 중입니다. 필터와 임시 저장값을 먼저 불러옵니다.";
        await ReloadFiltersAsync();

        BeginAutoSaveSuppression();
        try
        {
            StatusMessage = "이전 작성 중인 청구 설정을 확인하는 중입니다.";
            var restoredDraft = await RestoreAutoSaveDraftAsync();
            if (!restoredDraft)
                NewProfile();

            StatusMessage = restoredDraft
                ? "이전 작성 중인 청구 설정을 복원했습니다. 청구 목록은 백그라운드에서 조회 중입니다."
                : "렌탈 청구관리 화면을 먼저 표시했습니다. 청구 목록은 백그라운드에서 조회 중입니다.";
        }
        finally
        {
            EndAutoSaveSuppression();
        }

        StartInitialRowsLoad();
    }

    public void CancelPendingBackgroundWork()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        CancelPendingFilterReload();
        CancelBillingHistoryLoad();
        CancelIncludedAssetHistoryLoad();
        _candidateAssetsLoadCts?.Cancel();
        _candidateAssetsLoadCts?.Dispose();
        _candidateAssetsLoadCts = null;
        _candidateAssetsLoadTask = null;
        _activeCandidateAssetsLoadSignature = string.Empty;
        _contractDateRefreshCts?.Cancel();
        _contractDateRefreshCts?.Dispose();
        _contractDateRefreshCts = null;
        _searchDebouncer.Dispose();
        UnsubscribeBillingRowSelectionHandlers();
    }

    private void StartInitialRowsLoad()
    {
        if (_isDisposed)
            return;

        UiTaskHelper.Forget(
            LoadInitialRowsThenDeferredMaintenanceAsync(),
            "RENTAL",
            "렌탈 청구 초기 목록 및 후속 보정",
            ex => StatusMessage = $"렌탈 청구 목록을 불러오지 못했습니다. {ex.Message}");
    }

    private async Task LoadInitialRowsThenDeferredMaintenanceAsync()
    {
        await ReloadAsync();
        await RunDeferredInitialMaintenanceAsync();
    }

    private async Task RunDeferredInitialMaintenanceAsync()
    {
        if (_isDisposed)
            return;

        StatusMessage = "렌탈 청구 목록 표시 후 전표/청구 연결 보정을 백그라운드에서 확인하는 중입니다.";
        var cleanedLegacyAssignments = 0;
        RentalBillingReferenceRepairResult? repairResult = null;
        try
        {
            IsBusy = true;
            cleanedLegacyAssignments = await _rental.CleanupLegacyAssignedUsernamesAsync();
            repairResult = await _rental.RepairBillingInvoicePeriodLinksAsync(_session, ReferenceDate);
        }
        finally
        {
            if (!_isDisposed)
                IsBusy = false;
        }

        if (_isDisposed)
            return;

        var hasMaintenanceChanges = cleanedLegacyAssignments > 0 || repairResult is { HasChanges: true };
        if (_pendingFilterReload || hasMaintenanceChanges)
        {
            StatusMessage = repairResult is { HasChanges: true }
                ? $"{repairResult.SummaryMessage} 보정 결과를 반영하기 위해 청구 목록을 다시 조회합니다."
                : cleanedLegacyAssignments > 0
                    ? $"렌탈 청구 이전 연결값 {cleanedLegacyAssignments:N0}건을 정리했습니다. 청구 목록을 다시 조회합니다."
                    : "변경된 필터 조건을 반영해 청구 목록을 다시 조회합니다.";
            await ReloadAsync();
            return;
        }

        StatusMessage = "렌탈 청구 목록과 전표/청구 연결 상태를 확인했습니다.";
    }

    public async Task LoadAndSelectProfileAsync(Guid profileId)
    {
        CancelPendingFilterReload();
        StatusMessage = "운영 점검 항목의 청구 프로필을 여는 중입니다.";
        await _rental.RepairBillingInvoicePeriodLinksAsync(_session, ReferenceDate);
        await ReloadFiltersAsync();
        var row = await _rental.GetBillingRowAsync(profileId, _session, ReferenceDate);
        Rows.ReplaceWith(row is null ? Array.Empty<RentalBillingViewRow>() : new[] { row });
        RebindBillingRowSelectionHandlers();
        NotifyDeleteCheckedState();
        SelectRow(profileId);
        StatusMessage = SelectedRow is null
            ? "점검 항목의 청구 프로필을 목록에서 찾지 못했습니다. 필터, 권한, 삭제 상태를 확인하세요."
            : "운영 점검 항목의 청구 프로필을 선택했습니다. 표시 품목, 월 기준금액, 연결 자산을 확인한 뒤 저장하세요.";
    }

    [RelayCommand]
    private void ShowPastDueOnly()
    {
        PastDueOnly = true;
        StatusMessage = "과거 미처리 거래처만 보이도록 필터를 적용했습니다.";
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_isDisposed)
            return;

        CancelPendingFilterReload();
        using var cts = new CancellationTokenSource();
        _filterReloadCts = cts;
        try
        {
            await ReloadCoreAsync(cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_filterReloadCts, cts))
                _filterReloadCts = null;
        }
    }

    private async Task ReloadCoreAsync(CancellationToken ct)
    {
        if (_isDisposed)
            return;

        if (IsBusy)
        {
            _pendingFilterReload = true;
            StatusMessage = "현재 조회 중입니다. 변경한 필터는 조회가 끝난 뒤 자동으로 다시 적용됩니다.";
            return;
        }

        do
        {
            _pendingFilterReload = false;
            _activeFilterReloadSignature = BuildCurrentFilterReloadSignature();
            var requestVersion = Interlocked.Increment(ref _filterReloadVersion);
            var selectedId = SelectedRow?.SelectionId;
            var selectedRowBeforeReload = SelectedRow;
            var preserveSelectedEditor = ShouldPreserveSelectedEditorDuringReload();
            IsBusy = true;
            StatusMessage = "렌탈 청구 목록을 조회하는 중입니다. 데이터가 많은 경우 잠시 걸릴 수 있습니다.";
            try
            {
                ct.ThrowIfCancellationRequested();
                var rows = await _rental.GetBillingRowsAsync(new RentalBillingFilter
                {
                    SearchText = SearchText,
                    OfficeCode = ResolveSelectedOfficeFilterCode(),
                    Status = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter,
                    DueOnly = DueOnly,
                    PastDueOnly = PastDueOnly,
                    ExpandCustomerSummaryRows = ShowIndividualProfiles,
                    IncludeHistoryRows = false,
                    ReferenceDate = ReferenceDate
                }, _session, ct);

                ct.ThrowIfCancellationRequested();
                if (_isDisposed)
                    return;

                if (requestVersion != Volatile.Read(ref _filterReloadVersion))
                    return;

                Rows.ReplaceWith(rows);
                RebindBillingRowSelectionHandlers();
                NotifyDeleteCheckedState();

                TotalCount = rows.Count;
                DueCount = rows.Count(row => row.DaysRemaining.HasValue && row.DaysRemaining.Value <= 0);
                IssueCount = rows.Count(row => row.HasDataIssue);
                CompletedCount = rows.Count(row => string.Equals(row.CompletionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase));
                PartialSettlementCount = rows.Count(row => string.Equals(row.SettlementStatus, PaymentFlowConstants.SettlementStatusPartial, StringComparison.OrdinalIgnoreCase));
                PastUnresolvedCustomerCount = rows.Count(row => row.HasPastUnresolved);
                PastUnresolvedCount = rows.Sum(row => row.PastUnresolvedCount);
                PastUnresolvedAmount = rows.Sum(row => row.PastUnresolvedAmount);
                TotalOutstandingAmount = rows.Sum(row => row.OutstandingAmount);
                var unlinkedCount = rows.Sum(row => row.GroupedUnlinkedAssetCount);
                var unlinkedLimitNotice = BuildUnlinkedAssetLimitNotice(unlinkedCount);
                var profileLimitNotice = BuildBillingProfileLimitNotice(rows);

                StatusMessage = rows.Count == 0
                    ? "조건에 맞는 렌탈 청구 대상이 없습니다."
                    : unlinkedCount > 0
                        ? $"렌탈 청구 {rows.Count:N0}건을 조회했습니다. 청구 설정이 필요한 장비 {unlinkedCount:N0}대가 포함되어 있습니다.{unlinkedLimitNotice}{profileLimitNotice}"
                        : ShowIndividualProfiles
                            ? $"렌탈 청구 프로필 {rows.Count:N0}건을 개별 조회했습니다.{profileLimitNotice}"
                            : $"렌탈 청구 {rows.Count:N0}건을 조회했습니다.{profileLimitNotice}";

                if (selectedId.HasValue)
                {
                    var reloadedSelection = FindRow(selectedId.Value);
                    if (reloadedSelection is not null)
                    {
                        if (preserveSelectedEditor)
                            PreserveEditorAfterReload(selectedRowBeforeReload);
                        else
                            SelectedRow = reloadedSelection;
                    }
                    else if (preserveSelectedEditor)
                    {
                        PreserveEditorAfterReload(selectedRowBeforeReload);
                    }
                    else
                    {
                        NewProfile();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StatusMessage = "검색 조건이 변경되어 이전 조회를 중단했습니다.";
                return;
            }
            finally
            {
                if (!_isDisposed)
                    IsBusy = false;
                _activeFilterReloadSignature = string.Empty;
            }
        }
        while (_pendingFilterReload && !ct.IsCancellationRequested);
    }

    private bool ShouldPreserveSelectedEditorDuringReload()
    {
        if (SelectedRow is null)
            return false;
        if (!HasMeaningfulDraftState())
            return false;

        return string.IsNullOrWhiteSpace(_selectedRowBaselineSignature) ||
               HasUnsavedEditorChangesAgainstBaseline();
    }

    private void PreserveEditorAfterReload(RentalBillingViewRow? selectedRowBeforeReload)
    {
        if (selectedRowBeforeReload is not null && !ReferenceEquals(SelectedRow, selectedRowBeforeReload))
            SelectedRow = selectedRowBeforeReload;

        StatusMessage = "목록은 새로고침했지만 저장하지 않은 렌탈 청구 편집 내용은 보존했습니다. 저장하거나 취소한 뒤 다른 항목을 선택하세요.";
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
        await RefreshEditRevisionFromStoreAsync(result.EntityId);
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
            StatusMessage = "청구설정이 필요한 장비입니다. 먼저 저장해 청구 프로필을 만든 뒤 청구를 시작하세요.";
            return;
        }

        if (HasUnsavedSelectedRowChanges())
        {
            StatusMessage = "현재 청구 설정 변경 내용을 저장한 뒤 청구서를 만듭니다.";
            await SaveAsync();

            if (SelectedRow is null ||
                SelectedRow.IsAggregateRow ||
                !SelectedRow.HasPersistedProfile ||
                HasUnsavedSelectedRowChanges())
            {
                if (string.IsNullOrWhiteSpace(StatusMessage))
                    StatusMessage = "청구 설정 저장이 완료되지 않아 청구서를 만들지 않았습니다.";

                return;
            }
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
            StatusMessage = "거래처별 요약에 청구 가능한 개별 프로필이 없습니다. '개별 청구건 직접 보기'에서 프로필을 생성/저장한 뒤 다시 시도하세요.";
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
            ? $" / 청구설정 필요 장비 {aggregateRow.GroupedUnlinkedAssetCount:N0}대 제외"
            : string.Empty;

        if (failureMessages.Count == 0)
        {
            StatusMessage = $"거래처별 요약에 포함된 개별 청구 프로필 {successCount:N0}건을 청구 시작했습니다.{skippedUnlinkedText}";
            return;
        }

        var failureSummary = string.Join(" | ", failureMessages.Distinct().Take(3));
        StatusMessage = successCount > 0
            ? $"거래처별 요약 청구 일부 완료: 성공 {successCount:N0}건 / 실패 {failureMessages.Count:N0}건{skippedUnlinkedText} - {failureSummary}"
            : $"거래처별 요약 청구 시작 실패 {failureMessages.Count:N0}건{skippedUnlinkedText} - {failureSummary}";
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
            StatusMessage = "청구설정이 필요한 장비입니다. 먼저 저장해 청구 프로필을 만든 뒤 보류 처리하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var expectedRevision = SelectedRow.Source.Revision;
        var result = await _rental.HoldBillingAsync(targetId, ReferenceDate, string.Empty, _session, expectedRevision: expectedRevision);
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
            StatusMessage = "청구설정이 필요한 장비입니다. 먼저 저장해 청구 프로필을 만든 뒤 수금을 등록하세요.";
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

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedBillingHistory))]
    private async Task DeleteSelectedBillingHistoryAsync()
    {
        if (SelectedRow is null || SelectedBillingHistory is null)
        {
            StatusMessage = "삭제할 청구/입금 내역을 먼저 선택하세요.";
            return;
        }

        if (TryRejectAggregateSelection("청구/입금 내역 삭제"))
            return;

        if (!SelectedRow.HasPersistedProfile)
        {
            StatusMessage = "청구설정이 필요한 장비입니다. 먼저 저장해 청구 프로필을 만든 뒤 청구/입금 내역을 삭제하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var history = SelectedBillingHistory;
        if (history.BillingProfileId != targetId)
        {
            StatusMessage = "거래처별 요약에 포함된 다른 청구건입니다. '개별 청구건 직접 보기'으로 실제 청구건을 선택한 뒤 삭제하세요.";
            return;
        }

        if (!history.CanDelete)
        {
            StatusMessage = "연결된 판매전표 또는 입금 내역이 없는 예정 청구월은 삭제할 내역이 없습니다.";
            return;
        }

        var invoiceDeleteText = history.HasInvoice
            ? "연결된 판매전표도 함께 삭제됩니다."
            : "연결된 판매전표가 없으면 전표 삭제는 건너뜁니다.";
        var settlementDeleteText = history.HasSettlement
            ? $"입금 {history.SettledAmount:N0}원 내역도 함께 삭제됩니다."
            : "입금 내역이 없으면 입금 삭제는 건너뜁니다.";
        var confirmation = MessageBox.Show(
            GetActiveWindow(),
            $"{SelectedRow.CustomerDisplayName} / {history.PeriodLabel} 청구·입금 내역을 삭제하시겠습니까?{Environment.NewLine}{invoiceDeleteText}{Environment.NewLine}{settlementDeleteText}",
            "청구/입금 내역 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        IsBusy = true;
        try
        {
            var result = await _rental.DeleteBillingHistoryAsync(
                targetId,
                history.BillingRunId,
                _session,
                expectedRevision: SelectedRow.Source.Revision,
                expectedInvoiceRevision: history.InvoiceRevision);
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

            RemoveBillingHistoryRowFromDisplay(history);
            IsBusy = false;
            await ReloadAsync();
            SelectRow(targetId);
            await RefreshBillingHistoryRowsForProfileAsync(targetId);
            StatusMessage = $"{result.Message} 청구/입금 내역 목록에 바로 반영했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
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

        var confirmationMessage = SelectedRow.HasPersistedProfile
            ? $"선택한 렌탈 청구 프로필을 삭제하시겠습니까?{Environment.NewLine}연결된 자산 정보는 삭제되지 않고 청구 목록에서는 제외됩니다."
            : $"청구설정 필요 장비를 청구 목록에서 제외하시겠습니까?{Environment.NewLine}자산 정보는 삭제되지 않습니다.";
        var confirmation = MessageBox.Show(
            GetActiveWindow(),
            confirmationMessage,
            "렌탈 청구 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
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

    [RelayCommand(CanExecute = nameof(CanDeleteChecked))]
    private async Task DeleteCheckedAsync()
    {
        var targets = Rows.Where(row => row.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "삭제할 렌탈 청구 대상을 먼저 선택하세요.";
            return;
        }

        if (!CanDeleteChecked)
        {
            StatusMessage = "권한이 있거나 담당지점 범위 안에 있는 선택 청구건이 없습니다.";
            return;
        }

        var aggregateTargets = targets.Where(row => row.IsAggregateRow).ToList();
        var editableTargets = targets.Where(CanDeleteBillingRow).ToList();
        var skippedPermissionCount = targets.Count - aggregateTargets.Count - editableTargets.Count;
        var persistedTargets = editableTargets.Where(row => row.HasPersistedProfile).ToList();
        var unlinkedTargets = editableTargets.Where(row => !row.HasPersistedProfile).ToList();
        var skippedAggregateCount = aggregateTargets.Count;
        if (persistedTargets.Count == 0 && unlinkedTargets.Count == 0)
        {
            StatusMessage = skippedAggregateCount > 0
                ? "거래처별 요약행은 선택삭제할 수 없습니다. '개별 청구건 직접 보기'으로 실제 청구건을 선택해 정리한 뒤 다시 시도하세요."
                : "삭제할 수 있는 청구 프로필 또는 청구설정 필요 장비가 없습니다.";
            return;
        }

        var confirmation = MessageBox.Show(
            skippedAggregateCount > 0
                ? $"청구 프로필 {persistedTargets.Count:N0}건은 삭제하고, 청구설정 필요 장비 {unlinkedTargets.Count:N0}대는 청구 목록에서 제외하시겠습니까?\n거래처별 요약행 {skippedAggregateCount:N0}건은 제외됩니다."
                : unlinkedTargets.Count > 0 && persistedTargets.Count > 0
                    ? $"청구 프로필 {persistedTargets.Count:N0}건은 삭제하고, 청구설정 필요 장비 {unlinkedTargets.Count:N0}대는 청구 목록에서 제외하시겠습니까?"
                    : unlinkedTargets.Count > 0
                        ? $"청구설정 필요 장비 {unlinkedTargets.Count:N0}대를 청구 목록에서 제외하시겠습니까?\n자산 정보는 삭제되지 않습니다."
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

        var skippedPermissionMessage = skippedPermissionCount > 0
            ? $" / 권한/담당지점 제외 {skippedPermissionCount:N0}건"
            : string.Empty;
        StatusMessage = failureMessages.Count == 0
            ? BuildDeleteCheckedSuccessMessage(successCount, excludedUnlinkedCount, skippedAggregateCount, skippedPermissionCount)
            : skippedAggregateCount > 0
                ? $"삭제/제외 성공 {successCount + excludedUnlinkedCount:N0}건 / 실패 {failureMessages.Count:N0}건 / 거래처별 요약행 제외 {skippedAggregateCount:N0}건{skippedPermissionMessage} - {string.Join(" | ", failureMessages.Take(3))}"
                : $"삭제/제외 성공 {successCount + excludedUnlinkedCount:N0}건 / 실패 {failureMessages.Count:N0}건{skippedPermissionMessage} - {string.Join(" | ", failureMessages.Take(3))}";

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
        int skippedAggregateCount,
        int skippedPermissionCount)
    {
        var parts = new List<string>();
        if (deletedProfileCount > 0)
            parts.Add($"청구 프로필 {deletedProfileCount:N0}건 삭제");
        if (excludedUnlinkedCount > 0)
            parts.Add($"청구설정 필요 장비 {excludedUnlinkedCount:N0}대 청구목록 제외");
        if (parts.Count == 0)
            parts.Add("선택 항목 처리");

        var message = string.Join(", ", parts) + " 완료.";
        if (skippedAggregateCount > 0)
            message += $" 거래처별 요약행 {skippedAggregateCount:N0}건은 제외했습니다.";
        if (skippedPermissionCount > 0)
            message += $" 권한/담당지점 범위 밖 {skippedPermissionCount:N0}건은 제외했습니다.";
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
            StatusMessage = "청구설정이 필요한 장비입니다. 먼저 저장해 청구 프로필을 만든 뒤 완료 처리하세요.";
            return;
        }

        if (SelectedRow.OutstandingAmount > 0m)
        {
            StatusMessage = "미수금이 남아 있어 완납 처리할 수 없습니다. 먼저 '입금 등록'으로 수금을 완료하세요.";
            MessageBox.Show(
                "미수금이 남아 있어 완납 처리할 수 없습니다. 먼저 '입금 등록'으로 수금을 완료하세요.",
                "완납 처리 불가",
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
            expectedRevision: expectedRevision,
            billingRunId: SelectedRow.CurrentBillingRunId);
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
        EditOfficeCode = ResolveDefaultEditOfficeCode();
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
        RefreshBillingHistoryRows(null);
        TemplateItems.ReplaceWith(new[] { CreateDefaultTemplateItem() });
        SelectedTemplateItem = TemplateItems.FirstOrDefault();
        CandidateAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
        IncludedAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
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
            StatusMessage = "거래처별 요약행을 먼저 선택하세요.";
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
            ? "개별 청구건 직접 보기로 전환했습니다. 목록에서 수정할 청구건을 선택하세요."
            : "개별 청구건 직접 보기로 전환했습니다. 선택된 청구건에서 저장/삭제/장비연결을 진행하세요.";
    }

    [RelayCommand]
    private void AddTemplateItem()
    {
        if (!CanEditCurrentSelection)
        {
            StatusMessage = "권한이 없어 렌탈 청구 품목을 편집할 수 없습니다.";
            return;
        }

        if (!CanEditBillingProfileDetails)
        {
            StatusMessage = "거래처별 요약행에서는 표시 품목을 직접 편집할 수 없습니다. '개별 청구건 직접 보기'으로 실제 청구건을 선택한 뒤 진행하세요.";
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
        if (!CanEditCurrentSelection)
            return;

        if (SelectedTemplateItem is null)
            return;

        var index = TemplateItems.IndexOf(SelectedTemplateItem);
        TemplateItems.Remove(SelectedTemplateItem);
        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateDefaultTemplateItem());
        SelectedTemplateItem = TemplateItems[Math.Clamp(index, 0, TemplateItems.Count - 1)];
        UpdateTemplateDerivedValues();
        NotifyTemplateItemMoveState();
    }

    [RelayCommand(CanExecute = nameof(CanMoveTemplateItemUp))]
    private void MoveTemplateItemUp()
    {
        if (!CanEditCurrentSelection)
            return;

        if (SelectedTemplateItem is null)
            return;

        var index = TemplateItems.IndexOf(SelectedTemplateItem);
        if (index <= 0)
            return;

        var item = SelectedTemplateItem;
        TemplateItems.Move(index, index - 1);
        SelectedTemplateItem = item;
        UpdateTemplateDerivedValues();
        NotifyTemplateItemMoveState();
        StatusMessage = "표시 품목 순서를 위로 이동했습니다. 저장하면 청구서 품목 순서에 반영됩니다.";
    }

    [RelayCommand(CanExecute = nameof(CanMoveTemplateItemDown))]
    private void MoveTemplateItemDown()
    {
        if (!CanEditCurrentSelection)
            return;

        if (SelectedTemplateItem is null)
            return;

        var index = TemplateItems.IndexOf(SelectedTemplateItem);
        if (index < 0 || index >= TemplateItems.Count - 1)
            return;

        var item = SelectedTemplateItem;
        TemplateItems.Move(index, index + 1);
        SelectedTemplateItem = item;
        UpdateTemplateDerivedValues();
        NotifyTemplateItemMoveState();
        StatusMessage = "표시 품목 순서를 아래로 이동했습니다. 저장하면 청구서 품목 순서에 반영됩니다.";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveIncludedAsset))]
    private void RemoveIncludedAsset()
    {
        if (!CanEditCurrentSelection)
            return;

        if (SelectedIncludedAsset is null ||
            SelectedIncludedAsset.AssetId == Guid.Empty)
        {
            return;
        }

        var removedAssetId = SelectedIncludedAsset.AssetId;
        var removedAsset = CloneBillingAssetOption(SelectedIncludedAsset, isSelected: false);
        var removedFromPoolCount = _includedAssetPool.RemoveAll(asset => asset.AssetId == removedAssetId);
        var removedCount = 0;
        foreach (var templateItem in TemplateItems)
        {
            removedCount += RemoveIncludedAssetId(templateItem.IncludedAssetIds, removedAssetId);
            if (templateItem.RepresentativeAssetId == removedAssetId)
                templateItem.RepresentativeAssetId = null;
        }

        if (removedCount == 0 && removedFromPoolCount == 0)
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

        removedAsset.BillingProfileId = null;
        removedAsset.IsLinkedToCurrentProfile = false;
        removedAsset.IsLinkedToAnotherProfile = false;
        removedAsset.IsRepresentativeAsset = false;
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

    [RelayCommand(CanExecute = nameof(CanAddSelectedIncludedAssetToTemplateItem))]
    private void AddSelectedIncludedAssetToTemplateItem()
    {
        if (!CanEditCurrentSelection || SelectedIncludedAsset is null || SelectedIncludedAsset.AssetId == Guid.Empty)
            return;

        var asset = SelectedIncludedAsset;
        EnsureIncludedAssetPoolContains(asset);

        if (SelectedTemplateItem is null)
        {
            var newItem = CreateTemplateItemFromIncludedAsset(asset);
            TemplateItems.Add(newItem);
            SelectedTemplateItem = newItem;
        }
        else
        {
            var shouldApplyAssetDefaults = !SelectedTemplateItem.IncludedAssetIds.Any(id => id != Guid.Empty) &&
                                           IsDefaultRentalDisplayItemName(SelectedTemplateItem.DisplayItemName);
            if (SelectedTemplateItem.IncludedAssetIds.Contains(asset.AssetId))
            {
                StatusMessage = "이미 선택한 표시 품목에 포함된 장비입니다.";
                return;
            }

            SelectedTemplateItem.IncludedAssetIds.Add(asset.AssetId);
            if (shouldApplyAssetDefaults || string.IsNullOrWhiteSpace(SelectedTemplateItem.DisplayItemName))
                SelectedTemplateItem.DisplayItemName = ResolveAssetDisplayItemName(asset);
            if (string.IsNullOrWhiteSpace(SelectedTemplateItem.BillingLineMode))
                SelectedTemplateItem.BillingLineMode = ResolveDefaultTemplateBillingLineMode(EditBillingType);
            if (shouldApplyAssetDefaults || (SelectedTemplateItem.UnitPrice <= 0m && SelectedTemplateItem.Amount <= 0m))
                ApplyIncludedAssetMonthlyFeesToTemplateItem(SelectedTemplateItem, applyZeroFees: true);
            ApplyTemplateSalesFieldDefaults(SelectedTemplateItem);
        }

        SyncIndividualTemplateItemsFromIncludedAssets();
        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections();
        UpdateTemplateDerivedValues();
        AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
        StatusMessage = $"'{BuildAssetShortLabel(asset)}' 장비를 청구서 표시 품목에 추가했습니다. 청구명을 수정한 뒤 저장하면 전표/청구 내역에 함께 반영됩니다.";
    }

    [RelayCommand(CanExecute = nameof(CanSetRepresentativeAsset))]
    private void SetRepresentativeAsset()
    {
        if (!CanEditCurrentSelection)
            return;

        if (SelectedIncludedAsset is null)
            return;

        SetRepresentativeAssetFromIncludedOption(SelectedIncludedAsset);
    }

    private void SetRepresentativeAssetFromIncludedOption(RentalBillingAssetOption asset)
    {
        if (asset.AssetId == Guid.Empty)
        {
            RefreshBillingAssetCollections();
            return;
        }

        var targetItem = ResolveRepresentativeTargetTemplateItem(asset.AssetId);
        if (targetItem is null)
        {
            RefreshBillingAssetCollections();
            return;
        }

        var includedAsset = IncludedAssets.FirstOrDefault(current => current.AssetId == asset.AssetId) ?? asset;
        SelectedIncludedAsset = includedAsset;
        var representativeLabel = BuildAssetShortLabel(includedAsset);
        if (IsTemplateItemBundleMode(targetItem))
        {
            if (!targetItem.IncludedAssetIds.Contains(asset.AssetId))
                targetItem.IncludedAssetIds.Add(asset.AssetId);

            targetItem.RepresentativeAssetId = asset.AssetId;
            SelectedTemplateItem = targetItem;
        }

        _suppressIncludedAssetRepresentativeChanges = true;
        try
        {
            foreach (var current in _includedAssetPool.Concat(IncludedAssets))
                current.IsRepresentativeAsset = current.AssetId == asset.AssetId;
        }
        finally
        {
            _suppressIncludedAssetRepresentativeChanges = false;
        }

        NormalizeTemplateRepresentativeAssets();
        RefreshBillingAssetCollections();
        UpdateTemplateDerivedValues();
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        StatusMessage = IsTemplateItemBundleMode(targetItem)
            ? $"대표자산을 '{representativeLabel}'로 지정했습니다. 저장하면 묶음 청구 규격에 반영됩니다."
            : $"'{representativeLabel}'을 대표 표시로 선택했습니다. 개별 청구는 대표자산과 무관하게 자산별로 전표에 반영됩니다.";
    }

    private RentalBillingTemplateEditorItem? ResolveRepresentativeTargetTemplateItem(Guid assetId)
    {
        if (assetId == Guid.Empty)
            return null;

        if (SelectedTemplateItem is not null &&
            (SelectedTemplateItem.IncludedAssetIds.Contains(assetId) || TemplateItems.Count == 1))
        {
            return SelectedTemplateItem;
        }

        return TemplateItems.FirstOrDefault(item => IsTemplateItemBundleMode(item) && item.IncludedAssetIds.Contains(assetId))
               ?? TemplateItems.FirstOrDefault(item => item.IncludedAssetIds.Contains(assetId))
               ?? TemplateItems.FirstOrDefault(item => IsTemplateItemBundleMode(item))
               ?? TemplateItems.FirstOrDefault();
    }


    [RelayCommand(CanExecute = nameof(CanAddIncludedAssetAssignmentHistory))]
    private async Task AddIncludedAssetAssignmentHistoryAsync()
    {
        if (SelectedIncludedAsset is null || SelectedIncludedAsset.AssetId == Guid.Empty)
        {
            StatusMessage = "임대이력을 추가할 거래처 임대 자산을 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(SelectedIncludedAsset.AssetId, _session);
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
            StatusMessage = "수정할 거래처 임대 자산의 임대이력을 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(
            SelectedIncludedAsset.AssetId,
            _session,
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
            StatusMessage = "삭제할 거래처 임대 자산의 임대이력을 먼저 선택하세요.";
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
        OnPropertyChanged(nameof(CanDeleteSelectedBillingHistory));
        OnPropertyChanged(nameof(CanDeleteSelected));
        NotifyDeleteCheckedState();
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
        NotifyTemplateItemMoveState();
        OnPropertyChanged(nameof(CanRemoveIncludedAsset));
        OnPropertyChanged(nameof(CanSetRepresentativeAsset));
        OnPropertyChanged(nameof(CanApplySelectedAssets));
        OnPropertyChanged(nameof(CanEditTemplateLineMode));
        ExpandSelectedSummaryCommand.NotifyCanExecuteChanged();
        RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
        SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        DeleteSelectedBillingHistoryCommand.NotifyCanExecuteChanged();
        NotifyIncludedAssetAssignmentHistoryCommandState();
    }

    private bool CanDeleteBillingRow(RentalBillingViewRow row)
        => CanEditRentalProfiles &&
           !row.IsAggregateRow &&
           CanOperateScope(ResolveProfileOfficeCode(row.Source, _session.OfficeCode));

    private void NotifyDeleteCheckedState()
    {
        OnPropertyChanged(nameof(CanDeleteChecked));
        DeleteCheckedCommand.NotifyCanExecuteChanged();
    }

    private void RebindBillingRowSelectionHandlers()
    {
        UnsubscribeBillingRowSelectionHandlers();
        foreach (var row in Rows)
        {
            row.PropertyChanged += BillingRow_PropertyChanged;
            _observedBillingRows.Add(row);
        }
    }

    private void UnsubscribeBillingRowSelectionHandlers()
    {
        foreach (var row in _observedBillingRows)
            row.PropertyChanged -= BillingRow_PropertyChanged;
        _observedBillingRows.Clear();
    }

    private void BillingRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RentalBillingViewRow.IsSelected), StringComparison.Ordinal))
            return;

        NotifyDeleteCheckedState();
    }

    private void NotifyTemplateItemMoveState()
    {
        OnPropertyChanged(nameof(CanRemoveTemplateItem));
        OnPropertyChanged(nameof(CanMoveTemplateItemUp));
        OnPropertyChanged(nameof(CanMoveTemplateItemDown));
        RemoveTemplateItemCommand.NotifyCanExecuteChanged();
        MoveTemplateItemUpCommand.NotifyCanExecuteChanged();
        MoveTemplateItemDownCommand.NotifyCanExecuteChanged();
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
            StatusMessage = "거래처별 요약행에서는 장비 연결을 직접 편집할 수 없습니다. '개별 청구건 직접 보기'으로 실제 청구건을 선택한 뒤 진행하세요.";
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
            StatusMessage = "\uAC70\uB798\uCC98 \uADF8\uB8F9\uC5D0\uC11C\uB294 \uC7A5\uBE44 \uC5F0\uACB0\uC744 \uC9C1\uC811 \uD3B8\uC9D1\uD560 \uC218 \uC5C6\uC2B5\uB2C8\uB2E4. '\uAC1C\uBCC4 \uCCAD\uAD6C\uAC74 \uBCF4\uAE30'\uB85C \uC804\uD658\uD55C \uB4A4 \uC9C4\uD589\uD558\uC138\uC694.";
            return;
        }

        if (selectedAssets.Count == 0)
            return;

        foreach (var asset in selectedAssets.Where(asset => asset.AssetId != Guid.Empty))
        {
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
        UpdateTemplateDerivedValues();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
        ScheduleContractDateRefresh();
        StatusMessage = $"\uC7A5\uBE44 {selectedAssets.Count:N0}\uB300\uB97C \uB0B4\uBD80 \uD3EC\uD568 \uC7A5\uBE44\uC5D0 \uC5F0\uACB0\uD588\uC2B5\uB2C8\uB2E4. \uC804\uD45C\uC5D0 \uB123\uC744 \uC7A5\uBE44\uB294 \uB0B4\uBD80 \uD3EC\uD568 \uC7A5\uBE44\uC5D0\uC11C \uC120\uD0DD\uD55C \uB4A4 '\uD45C\uC2DC\uD488\uBAA9\uC5D0 \uCD94\uAC00'\uB97C \uB204\uB974\uC138\uC694.";
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
            if (!CustomerContractContentService.HasLocalContent(contract))
                StatusMessage = "거래처 계약서 PDF를 서버에서 내려받는 중입니다.";

            var readyContract = await CustomerContractContentService.EnsureContentAsync(contract, _local, _session, _api);
            CustomerContractPreviewService.Open(readyContract);
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
            .Concat(_includedAssetPool.Select(asset => asset.AssetId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet();

        foreach (var asset in _includedAssetPool.Where(asset => asset.AssetId != Guid.Empty))
            _pendingAssetLinkEdits.TryAdd(asset.AssetId, BuildAssetLinkEdit(asset));

        return _pendingAssetLinkEdits
            .Where(pair => includedAssetIds.Contains(pair.Key))
            .Select(pair => new RentalBillingAssetLinkEdit
            {
                AssetId = pair.Value.AssetId,
                CustomerId = pair.Value.CustomerId,
                CustomerName = pair.Value.CustomerName,
                InstallLocation = pair.Value.InstallLocation,
                InstallSiteName = pair.Value.InstallSiteName,
                ItemCategoryName = pair.Value.ItemCategoryName,
                Manufacturer = pair.Value.Manufacturer,
                ItemName = pair.Value.ItemName,
                MachineNumber = pair.Value.MachineNumber,
                PurchaseVendor = pair.Value.PurchaseVendor,
                PurchasePrice = pair.Value.PurchasePrice,
                SalePrice = pair.Value.SalePrice,
                AssetStatus = pair.Value.AssetStatus,
                BillingEligibilityStatus = pair.Value.BillingEligibilityStatus,
                BillingExclusionReason = pair.Value.BillingExclusionReason,
                DepositText = pair.Value.DepositText,
                MonthlyFee = pair.Value.MonthlyFee,
                ContractMonths = pair.Value.ContractMonths,
                ContractDate = pair.Value.ContractDate,
                ContractStartDate = pair.Value.ContractStartDate,
                RentalEndDate = pair.Value.RentalEndDate,
                PurchaseDate = pair.Value.PurchaseDate,
                DisposalDate = pair.Value.DisposalDate,
                InstallDate = pair.Value.InstallDate,
                FreeSupplyItems = pair.Value.FreeSupplyItems,
                PaidSupplyItems = pair.Value.PaidSupplyItems,
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
            ItemCategoryName = asset.ItemCategoryName,
            Manufacturer = asset.Manufacturer,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            PurchaseVendor = asset.PurchaseVendor,
            PurchasePrice = asset.PurchasePrice,
            SalePrice = asset.SalePrice,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            BillingExclusionReason = asset.BillingExclusionReason,
            DepositText = asset.DepositText,
            MonthlyFee = asset.MonthlyFee,
            ContractMonths = asset.ContractMonths,
            ContractDate = ToDateOnly(asset.ContractDate),
            ContractStartDate = ToDateOnly(asset.ContractStartDate),
            RentalEndDate = ToDateOnly(asset.RentalEndDate),
            PurchaseDate = ToDateOnly(asset.PurchaseDate),
            DisposalDate = ToDateOnly(asset.DisposalDate),
            InstallDate = ToDateOnly(asset.InstallDate),
            FreeSupplyItems = asset.FreeSupplyItems,
            PaidSupplyItems = asset.PaidSupplyItems,
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
            CancelBillingHistoryLoad();
            RefreshBillingHistoryRows(null);
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
        RefreshBillingHistoryRows(value);
        StartBillingHistoryRowsLoad(value);
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
            StatusMessage = "청구설정이 필요한 장비입니다. 내용을 확인한 뒤 저장하면 청구 프로필이 생성됩니다.";
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

    private void RefreshBillingHistoryRows(RentalBillingViewRow? row)
    {
        ApplyBillingHistoryRowsForDisplay(row is null
            ? Array.Empty<RentalBillingHistoryRow>()
            : row.BillingHistoryRows);
        SelectedBillingHistory = null;

        OnPropertyChanged(nameof(SelectedRowHasPastUnresolved));
        OnPropertyChanged(nameof(SelectedPastUnresolvedSummaryText));
    }

    private void StartBillingHistoryRowsLoad(RentalBillingViewRow row)
    {
        CancelBillingHistoryLoad();

        var profileIds = ResolveBillingHistoryProfileIds(row);
        if (profileIds.Count == 0)
            return;

        var cts = new CancellationTokenSource();
        var token = cts.Token;
        _billingHistoryLoadCts = cts;
        UiTaskHelper.Forget(
            LoadBillingHistoryRowsAsync(row, profileIds, cts, token),
            "RENTAL",
            "청구/입금 내역 선택 조회",
            ex => StatusMessage = $"청구/입금 내역을 불러오지 못했습니다. {ex.Message}");
    }

    private async Task LoadBillingHistoryRowsAsync(
        RentalBillingViewRow row,
        IReadOnlyList<Guid> profileIds,
        CancellationTokenSource cts,
        CancellationToken ct)
    {
        try
        {
            var histories = await _rental.GetBillingHistoryRowsAsync(profileIds, _session, ReferenceDate, BillingHistoryDisplayLimit, ct);
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(SelectedRow, row))
                return;

            ApplyBillingHistoryRowsForDisplay(histories);
            SelectedBillingHistory = null;
            OnPropertyChanged(nameof(SelectedRowHasPastUnresolved));
            OnPropertyChanged(nameof(SelectedPastUnresolvedSummaryText));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_billingHistoryLoadCts, cts))
                _billingHistoryLoadCts = null;
            cts.Dispose();
        }
    }

    private void ApplyBillingHistoryRowsForDisplay(IReadOnlyList<RentalBillingHistoryRow> histories)
    {
        var displayRows = LimitBillingHistoryRowsForDisplay(histories);
        BillingHistoryRows.ReplaceWith(displayRows);

        if (histories.Count > displayRows.Count)
        {
            StatusMessage = $"청구/입금 내역 {histories.Count:N0}건 중 최근 {displayRows.Count:N0}건만 먼저 표시합니다. 과거 내역이 필요하면 개별 프로필 보기 또는 거래처/상태 조건을 좁혀 조회하세요.";
        }
    }

    private void RemoveBillingHistoryRowFromDisplay(RentalBillingHistoryRow history)
    {
        var remainingRows = BillingHistoryRows
            .Where(row => !IsSameBillingHistoryRow(row, history))
            .ToList();
        if (remainingRows.Count != BillingHistoryRows.Count)
            BillingHistoryRows.ReplaceWith(remainingRows);

        if (SelectedRow is not null)
            SelectedRow.BillingHistoryRows.RemoveAll(row => IsSameBillingHistoryRow(row, history));

        SelectedBillingHistory = null;
        OnPropertyChanged(nameof(SelectedRowHasPastUnresolved));
        OnPropertyChanged(nameof(SelectedPastUnresolvedSummaryText));
        DeleteSelectedBillingHistoryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelectedBillingHistory));
    }

    private async Task RefreshBillingHistoryRowsForProfileAsync(Guid profileId)
    {
        if (profileId == Guid.Empty || SelectedRow is null)
            return;

        var profileIds = ResolveBillingHistoryProfileIds(SelectedRow);
        if (!profileIds.Contains(profileId))
            return;

        CancelBillingHistoryLoad();
        var histories = await _rental.GetBillingHistoryRowsAsync(
            profileIds,
            _session,
            ReferenceDate,
            BillingHistoryDisplayLimit,
            CancellationToken.None);
        if (SelectedRow is null || !ResolveBillingHistoryProfileIds(SelectedRow).Contains(profileId))
            return;

        ApplyBillingHistoryRowsForDisplay(histories);
        SelectedBillingHistory = null;
        OnPropertyChanged(nameof(SelectedRowHasPastUnresolved));
        OnPropertyChanged(nameof(SelectedPastUnresolvedSummaryText));
    }

    private static bool IsSameBillingHistoryRow(RentalBillingHistoryRow left, RentalBillingHistoryRow right)
        => left.BillingProfileId == right.BillingProfileId &&
           left.BillingRunId == right.BillingRunId &&
           left.InvoiceId == right.InvoiceId &&
           string.Equals(left.PeriodLabel, right.PeriodLabel, StringComparison.Ordinal) &&
           left.ScheduledDate == right.ScheduledDate;

    private static IReadOnlyList<RentalBillingHistoryRow> LimitBillingHistoryRowsForDisplay(IReadOnlyList<RentalBillingHistoryRow> histories)
        => histories.Count > BillingHistoryDisplayLimit
            ? histories.Take(BillingHistoryDisplayLimit).ToList()
            : histories;

    private void CancelBillingHistoryLoad()
    {
        _billingHistoryLoadCts?.Cancel();
        _billingHistoryLoadCts?.Dispose();
        _billingHistoryLoadCts = null;
    }

    private static IReadOnlyList<Guid> ResolveBillingHistoryProfileIds(RentalBillingViewRow row)
    {
        var ids = row.GroupedPersistedProfileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count > 0)
            return ids;

        return row.HasPersistedProfile && row.Source.Id != Guid.Empty
            ? new List<Guid> { row.Source.Id }
            : Array.Empty<Guid>();
    }

    private async Task ReloadFiltersAsync()
    {
        _suppressFilterReload = true;
        try
        {
            var offices = await GetOfficeFilterSourceAsync();
            var currentFilterValue = SelectedOfficeFilter?.Value;
            var currentEditOfficeCode = EditOfficeCode;
            var readableOfficeCodes = _local.GetReadableRentalOfficeCodesForSession(_session);
            var defaultFilterValue = ResolveDefaultOfficeFilterValue(readableOfficeCodes);
            var desiredFilterValue = string.IsNullOrWhiteSpace(currentFilterValue) ||
                                     (!CanUseAllOfficeFilter &&
                                      string.Equals(currentFilterValue, AllOption, StringComparison.OrdinalIgnoreCase))
                ? defaultFilterValue
                : currentFilterValue;
            var writableOfficeCodes = CanManageAll
                ? OfficeCodeCatalog.All
                : _local.GetWritableRentalOfficeCodesForSession(_session);
            var selectedRowOfficeCode = ResolveProfileOfficeCode(SelectedRow?.Source, _session.OfficeCode);

            var officeOptions = new List<DisplayOption>();
            if (CanUseAllOfficeFilter)
                officeOptions.Add(new DisplayOption { Value = AllOption, DisplayName = AllOption });
            officeOptions.AddRange(BuildOfficeDisplayOptions(offices, readableOfficeCodes)
                .Select(office => new DisplayOption
                {
                    Value = office.Value,
                    DisplayName = office.DisplayName
                }));
            OfficeOptions.ReplaceWith(officeOptions);

            var editableOfficeCodes = BuildEditableBillingOfficeCodes(
                writableOfficeCodes,
                readableOfficeCodes,
                [currentEditOfficeCode, selectedRowOfficeCode]);
            var editableOfficeOptions = BuildOfficeDisplayOptions(offices, editableOfficeCodes)
                .Select(office => new DisplayOption
                {
                    Value = office.Value,
                    DisplayName = office.DisplayName
                })
                .ToList();
            if (editableOfficeOptions.Count == 0)
            {
                var fallbackOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
                editableOfficeOptions.Add(new DisplayOption
                {
                    Value = fallbackOfficeCode,
                    DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(fallbackOfficeCode)
                });
            }
            EditOfficeOptions.ReplaceWith(editableOfficeOptions);

            SelectedOfficeFilter = OfficeOptions.FirstOrDefault(option =>
                                       string.Equals(option.Value, desiredFilterValue, StringComparison.OrdinalIgnoreCase))
                                   ?? OfficeOptions.FirstOrDefault(option =>
                                       string.Equals(option.Value, defaultFilterValue, StringComparison.OrdinalIgnoreCase))
                                   ?? OfficeOptions.FirstOrDefault(option => option.Value == AllOption)
                                   ?? OfficeOptions.FirstOrDefault();

            EditOfficeCode = ResolveDefaultEditOfficeCode(currentEditOfficeCode);

        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private async Task<IReadOnlyList<LocalOffice>> GetOfficeFilterSourceAsync()
    {
        if (_officeFilterSourceCache is not null)
            return _officeFilterSourceCache;

        _officeFilterSourceCache = await _local.GetOfficesAsync();
        return _officeFilterSourceCache;
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

    private string ResolveSelectedOfficeFilterCode()
    {
        if (SelectedOfficeFilter is null ||
            string.Equals(SelectedOfficeFilter.Value, AllOption, StringComparison.OrdinalIgnoreCase))
        {
            return CanUseAllOfficeFilter
                ? string.Empty
                : ResolveDefaultOfficeFilterValue(_local.GetReadableRentalOfficeCodesForSession(_session));
        }

        return OfficeCodeCatalog.TryNormalizeOfficeCode(SelectedOfficeFilter.Value, out var normalizedOfficeCode)
            ? normalizedOfficeCode
            : string.Empty;
    }

    private bool CanUseAllOfficeFilter => _session.HasAdministrativePrivileges || _session.HasGlobalDataScope;

    private string ResolveDefaultOfficeFilterValue(IEnumerable<string> readableOfficeCodes)
    {
        var normalizedOfficeCodes = NormalizeOfficeCodes(readableOfficeCodes)
            .OrderBy(OfficeCodeCatalog.GetOfficeDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);

        return normalizedOfficeCodes.FirstOrDefault(code =>
                   string.Equals(code, sessionOfficeCode, StringComparison.OrdinalIgnoreCase))
               ?? normalizedOfficeCodes.FirstOrDefault()
               ?? AllOption;
    }

    private string ResolveDefaultEditOfficeCode(string? preferredOfficeCode = null)
    {
        var fallbackOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        var candidates = new[]
        {
            preferredOfficeCode,
            ResolveSelectedOfficeFilterCode(),
            _session.OfficeCode
        };

        foreach (var candidate in candidates)
        {
            if (!OfficeCodeCatalog.TryNormalizeOfficeCode(candidate, out var normalizedOfficeCode))
                continue;

            if (EditOfficeOptions.Any(option => string.Equals(option.Value, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase)))
                return normalizedOfficeCode;
        }

        return EditOfficeOptions.FirstOrDefault()?.Value ?? fallbackOfficeCode;
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
        var customers = await _local.GetCustomersForRentalScopeAsync(
            _session,
            responsibleOfficeCode: ResolveSelectedOfficeFilterCode());
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
        SelectedRow = FindRow(entityId);
    }

    private RentalBillingViewRow? FindRow(Guid entityId)
        => Rows.FirstOrDefault(row =>
            row.SelectionId == entityId ||
            row.Source.Id == entityId ||
            row.GroupedSelectionIds.Contains(entityId) ||
            row.GroupedPersistedProfileIds.Contains(entityId));

    private async Task RefreshEditRevisionFromStoreAsync(Guid profileId)
    {
        if (profileId == Guid.Empty)
            return;

        var latestRevision = await _rental.GetBillingProfileRevisionAsync(profileId);
        if (latestRevision > 0)
            _editRevision = latestRevision;
    }

    private void ScheduleContractDateRefresh(bool preserveCurrentValue = false, bool updateSelectedRowBaselineIfUnchanged = false)
    {
        if (_isDisposed)
            return;

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
                if (_isDisposed || ex is OperationCanceledException)
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
        if (_isDisposed)
            return;

        if (preserveExistingValue && EditContractDate.HasValue)
            return;

        var contractDate = await ResolveContractDateFromSourcesAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (_isDisposed)
            return;

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
        => CustomerContractContentService.HasRegisteredFile(contract);

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
        if (_isDisposed)
            return;

        var signature = BuildCandidateAssetsLoadSignature(
            billingProfileId,
            customerId,
            customerName,
            officeCode,
            preserveSelection,
            autoIncludeAllCandidates);
        if (_candidateAssetsLoadTask is { IsCompleted: false } &&
            _candidateAssetsLoadCts is { IsCancellationRequested: false } &&
            string.Equals(_activeCandidateAssetsLoadSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _candidateAssetsLoadCts?.Cancel();
        _candidateAssetsLoadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _candidateAssetsLoadCts = cts;
        _activeCandidateAssetsLoadSignature = signature;
        _candidateAssetsLoadTask = LoadCandidateAssetsAsync(
            billingProfileId,
            customerId,
            customerName,
            officeCode,
            preserveSelection,
            autoIncludeAllCandidates,
            signature,
            cts,
            cts.Token);

        UiTaskHelper.Forget(
            _candidateAssetsLoadTask,
            "RENTAL",
            "렌탈 청구 연결 장비 조회",
            ex =>
            {
                if (_isDisposed || ex is OperationCanceledException)
                    return;

                StatusMessage = $"연결 장비 정보를 불러오지 못했습니다. {ex.Message}";
            });
    }

    private string BuildCandidateAssetsLoadSignature(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string officeCode,
        bool preserveSelection,
        bool autoIncludeAllCandidates)
        => string.Join(
            "|",
            billingProfileId?.ToString("N") ?? string.Empty,
            customerId?.ToString("N") ?? string.Empty,
            NormalizeFilterSignaturePart(customerName),
            NormalizeFilterSignaturePart(OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode)
                ? normalizedOfficeCode
                : officeCode),
            preserveSelection ? "PRESERVE" : "RESET",
            autoIncludeAllCandidates ? "AUTO" : "MANUAL",
            LinkAssetsLater ? "LATER" : "NOW",
            SelectedTemplateItem?.ItemId.ToString("N") ?? string.Empty,
            string.Join(
                ";",
                TemplateItems
                    .Select(item => string.Join(
                        ":",
                        item.ItemId.ToString("N"),
                        NormalizeFilterSignaturePart(item.BillingLineMode),
                        item.RepresentativeAssetId?.ToString("N") ?? string.Empty,
                        string.Join(
                            ",",
                            item.IncludedAssetIds
                                .Where(assetId => assetId != Guid.Empty)
                                .Distinct()
                                .OrderBy(assetId => assetId)
                                .Select(assetId => assetId.ToString("N")))))
                    .OrderBy(value => value, StringComparer.Ordinal)));

    private async Task LoadCandidateAssetsAsync(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string officeCode,
        bool preserveSelection,
        bool autoIncludeAllCandidates,
        string activeSignature = "",
        CancellationTokenSource? cts = null,
        CancellationToken ct = default)
    {
        try
        {
            if (_isDisposed)
                return;

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
            var customerInstalledAssetCandidates = await _rental.GetBillingAssetCandidatesAsync(
                billingProfileId,
                customerId,
                customerName,
                officeCode,
                includeOfficePoolAssets: false,
                _session,
                ct);

            ct.ThrowIfCancellationRequested();
            if (_isDisposed)
                return;

            _includedAssetPool.Clear();
            _includedAssetPool.AddRange(includedAssets
                .Select(asset =>
                {
                    var option = CreateBillingAssetOption(asset, isSelected: true);
                    ApplyPendingAssetLinkEdit(option);
                    return option;
                }));

            var includedAssetIdSet = _includedAssetPool
                .Select(asset => asset.AssetId)
                .Where(id => id != Guid.Empty)
                .ToHashSet();
            _candidateAssetPool.Clear();
            _candidateAssetPool.AddRange(customerInstalledAssetCandidates
                .Where(asset => !includedAssetIdSet.Contains(asset.Id))
                .Select(asset =>
                {
                    var option = CreateBillingAssetOption(asset, isSelected: false);
                    ApplyPendingAssetLinkEdit(option);
                    return option;
                }));

            if (autoIncludeAllCandidates && !LinkAssetsLater)
                AutoIncludeCustomerRentalAssets();

            RefreshBillingAssetCollections(previousSelections);
            SyncIndividualTemplateItemsFromIncludedAssets();
            NormalizeTemplateRepresentativeAssets();
            RefreshBillingAssetCollections(previousSelections);
            UpdateTemplateDerivedValues();
        }
        finally
        {
            if (cts is not null &&
                ReferenceEquals(_candidateAssetsLoadCts, cts) &&
                string.Equals(_activeCandidateAssetsLoadSignature, activeSignature, StringComparison.Ordinal))
            {
                _candidateAssetsLoadCts = null;
                _candidateAssetsLoadTask = null;
                _activeCandidateAssetsLoadSignature = string.Empty;
                cts.Dispose();
            }
        }
    }

    private void LoadTemplateItemsFromProfile(LocalRentalBillingProfile profile)
    {
        _pendingAssetLinkEdits.Clear();
        var templateItems = _rental.GetBillingTemplateItems(profile);
        var editorItems = new List<RentalBillingTemplateEditorItem>();
        foreach (var item in templateItems)
        {
            var editorItem = new RentalBillingTemplateEditorItem
            {
                ItemId = item.ItemId,
                CatalogItemId = item.CatalogItemId,
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
            editorItems.Add(editorItem);
        }

        if (editorItems.Count == 0)
            editorItems.Add(CreateDefaultTemplateItem());

        TemplateItems.ReplaceWith(editorItems);
        SelectedTemplateItem = TemplateItems.FirstOrDefault();
        ApplyBillingTypeToTemplateLineModes(EditBillingType);
        SyncIndividualTemplateItemsFromIncludedAssets();
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

    private RentalBillingTemplateEditorItem CreateTemplateItemFromIncludedAsset(RentalBillingAssetOption asset)
    {
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = ResolveAssetDisplayItemName(asset),
            BillingLineMode = ResolveDefaultTemplateBillingLineMode(EditBillingType),
            Specification = BuildAssetInvoiceSpecification(asset),
            MaterialNumber = asset.ManagementNumber?.Trim() ?? string.Empty,
            Quantity = 1m,
            UnitPrice = Math.Max(0m, Math.Round(asset.MonthlyFee, 0, MidpointRounding.AwayFromZero))
        };
        item.IncludedAssetIds.Add(asset.AssetId);
        item.NormalizeCalculatedAmount();
        WireTemplateItem(item);
        return item;
    }

    private static string ResolveAssetDisplayItemName(RentalBillingAssetOption asset)
    {
        var displayName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName);
        return string.IsNullOrWhiteSpace(displayName) ? "렌탈 임대료" : displayName;
    }

    private static bool IsDefaultRentalDisplayItemName(string? value)
    {
        var normalized = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(value ?? string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "렌탈 임대료", StringComparison.CurrentCultureIgnoreCase);
    }

    private void EnsureIncludedAssetPoolContains(RentalBillingAssetOption asset)
    {
        if (asset.AssetId == Guid.Empty)
            return;

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

        var existingIndex = _includedAssetPool.FindIndex(current => current.AssetId == linkedOption.AssetId);
        if (existingIndex >= 0)
            _includedAssetPool[existingIndex] = linkedOption;
        else
            _includedAssetPool.Add(linkedOption);

        _candidateAssetPool.RemoveAll(current => current.AssetId == linkedOption.AssetId);
        _pendingAssetLinkEdits[linkedOption.AssetId] = BuildAssetLinkEdit(linkedOption);
    }

    private bool IsSelectedIncludedAssetLinkedForBilling()
    {
        var assetId = SelectedIncludedAsset?.AssetId ?? Guid.Empty;
        if (assetId == Guid.Empty)
            return false;

        return _includedAssetPool.Any(asset => asset.AssetId == assetId) ||
               TemplateItems.Any(item => item.IncludedAssetIds.Contains(assetId)) ||
               _pendingAssetLinkEdits.ContainsKey(assetId);
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
                ApplyIncludedAssetMonthlyFeesToTemplateItem(item, applyZeroFees: true);
                RefreshBillingAssetCollections();
            }

            UpdateTemplateDerivedValues();
            ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        };
    }

    private void AutoIncludeCustomerRentalAssets()
    {
        var autoTargets = _candidateAssetPool
            .Where(asset => asset.AssetId != Guid.Empty && !asset.IsLinkedToAnotherProfile)
            .GroupBy(asset => asset.AssetId)
            .Select(group => group.First())
            .ToList();
        if (autoTargets.Count == 0)
            return;

        foreach (var asset in autoTargets)
        {
            var linkedOption = CloneBillingAssetOption(asset, isSelected: true);
            linkedOption.CustomerId = EditCustomerId;
            linkedOption.TargetCustomerName = string.IsNullOrWhiteSpace(EditCustomerName)
                ? linkedOption.TargetCustomerName
                : EditCustomerName;
            linkedOption.CurrentCustomerName = string.IsNullOrWhiteSpace(linkedOption.TargetCustomerName)
                ? linkedOption.CurrentCustomerName
                : linkedOption.TargetCustomerName;
            linkedOption.BillingProfileId = EditId == Guid.Empty ? null : EditId;
            linkedOption.IsLinkedToCurrentProfile = true;
            linkedOption.IsLinkedToAnotherProfile = false;
            EnsureIncludedAssetPoolContains(linkedOption);
        }

        _candidateAssetPool.RemoveAll(asset => autoTargets.Any(target => target.AssetId == asset.AssetId));
        EnsureAllIncludedAssetsAssignedForBillingType();
    }

    private void EnsureAllIncludedAssetsAssignedForBillingType()
    {
        var includedAssetIds = _includedAssetPool
            .Select(asset => asset.AssetId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return;

        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateDefaultTemplateItem());

        if (string.Equals(ResolveDefaultTemplateBillingLineMode(EditBillingType), "묶음", StringComparison.Ordinal))
        {
            var target = TemplateItems.FirstOrDefault(item => IsTemplateItemBundleMode(item)) ?? TemplateItems.First();
            foreach (var assetId in includedAssetIds)
            {
                if (!target.IncludedAssetIds.Contains(assetId))
                    target.IncludedAssetIds.Add(assetId);
            }

            ApplyIncludedAssetMonthlyFeesToTemplateItem(target, applyZeroFees: true);
            ApplyTemplateSalesFieldDefaults(target);
            return;
        }

        var lookup = BuildBillingAssetOptionLookup();
        foreach (var assetId in includedAssetIds)
        {
            if (TemplateItems.Any(item => item.IncludedAssetIds.Contains(assetId)))
                continue;

            lookup.TryGetValue(assetId, out var asset);
            if (asset is null)
                continue;

            var item = CreateTemplateItemFromIncludedAsset(asset);
            TemplateItems.Add(item);
        }
    }

    private void SyncAssetSelectionFromTemplate()
        => RefreshBillingAssetCollections();

    private void SyncIncludedAssetsFromTemplate()
        => RefreshBillingAssetCollections();

    private void RefreshBillingAssetCollections(ISet<Guid>? candidateSelectionIds = null)
    {
        if (SelectedTemplateItem is null)
        {
            IncludedAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
            SelectedIncludedAsset = null;
            CandidateAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
            AssetCandidateSummary = "후보 장비가 없습니다.";
            RemoveIncludedAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanRemoveIncludedAsset));
            AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanAddSelectedIncludedAssetToTemplateItem));
            SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSetRepresentativeAsset));
            ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanApplySelectedAssets));
            return;
        }

        var selectedTemplateAssetIds = SelectedTemplateItem.IncludedAssetIds
            .Where(assetId => assetId != Guid.Empty)
            .Distinct()
            .ToHashSet();
        var linkedAssetIds = _includedAssetPool
            .Select(asset => asset.AssetId)
            .Where(assetId => assetId != Guid.Empty)
            .Concat(selectedTemplateAssetIds)
            .Distinct()
            .ToHashSet();
        var visibleAssetIds = linkedAssetIds;
        var assetLookup = _includedAssetPool
            .Concat(_candidateAssetPool)
            .GroupBy(asset => asset.AssetId)
            .ToDictionary(group => group.Key, group => group.First());

        var includedAssetRows = new List<RentalBillingAssetOption>();
        foreach (var assetId in visibleAssetIds)
        {
            if (assetLookup.TryGetValue(assetId, out var option))
            {
                var clone = CloneBillingAssetOption(option, isSelected: selectedTemplateAssetIds.Contains(assetId));
                clone.IsRepresentativeAsset = IsTemplateItemBundleMode(SelectedTemplateItem)
                    ? selectedTemplateAssetIds.Contains(clone.AssetId) &&
                      SelectedTemplateItem.RepresentativeAssetId == clone.AssetId
                    : option.IsRepresentativeAsset;
                clone.PropertyChanged += HandleIncludedAssetOptionPropertyChanged;
                includedAssetRows.Add(clone);
            }
        }
        var previousIncludedAssetId = SelectedIncludedAsset?.AssetId;
        IncludedAssets.ReplaceWith(includedAssetRows);
        SelectedIncludedAsset = previousIncludedAssetId.HasValue
            ? IncludedAssets.FirstOrDefault(asset => asset.AssetId == previousIncludedAssetId.Value)
              ?? IncludedAssets.FirstOrDefault()
            : IncludedAssets.FirstOrDefault();

        var selectedCandidateIds = candidateSelectionIds ?? CandidateAssets
            .Where(asset => asset.IsSelected)
            .Select(asset => asset.AssetId)
            .ToHashSet();

        _suppressCandidateAssetSelectionChanges = true;
        try
        {
            var candidateRows = _candidateAssetPool
                .Where(asset => !linkedAssetIds.Contains(asset.AssetId))
                .Select(option =>
                {
                    var clone = CloneBillingAssetOption(option, isSelected: selectedCandidateIds.Contains(option.AssetId));
                    clone.PropertyChanged += HandleCandidateAssetOptionPropertyChanged;
                    return clone;
                })
                .ToList();
            CandidateAssets.ReplaceWith(candidateRows);
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
        AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddSelectedIncludedAssetToTemplateItem));
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
        if (sender is not RentalBillingAssetOption asset)
            return;

        if (string.Equals(e.PropertyName, nameof(RentalBillingAssetOption.MonthlyFee), StringComparison.Ordinal))
        {
            HandleIncludedAssetMonthlyFeeChanged(asset);
            return;
        }

        if (IsIncludedAssetDetailEditProperty(e.PropertyName))
        {
            HandleIncludedAssetDetailChanged(asset);
            return;
        }

        if (_suppressIncludedAssetRepresentativeChanges ||
            !string.Equals(e.PropertyName, nameof(RentalBillingAssetOption.IsRepresentativeAsset), StringComparison.Ordinal))
        {
            return;
        }

        if (!asset.IsRepresentativeAsset)
            return;

        SetRepresentativeAssetFromIncludedOption(asset);
    }

    private static bool IsIncludedAssetDetailEditProperty(string? propertyName)
        => propertyName is nameof(RentalBillingAssetOption.ItemCategoryName)
            or nameof(RentalBillingAssetOption.Manufacturer)
            or nameof(RentalBillingAssetOption.ItemName)
            or nameof(RentalBillingAssetOption.MachineNumber)
            or nameof(RentalBillingAssetOption.PurchaseVendor)
            or nameof(RentalBillingAssetOption.PurchasePrice)
            or nameof(RentalBillingAssetOption.SalePrice)
            or nameof(RentalBillingAssetOption.CurrentCustomerName)
            or nameof(RentalBillingAssetOption.InstallLocation)
            or nameof(RentalBillingAssetOption.AssetStatus)
            or nameof(RentalBillingAssetOption.BillingEligibilityStatus)
            or nameof(RentalBillingAssetOption.BillingExclusionReason)
            or nameof(RentalBillingAssetOption.DepositText)
            or nameof(RentalBillingAssetOption.ContractMonths)
            or nameof(RentalBillingAssetOption.ContractDate)
            or nameof(RentalBillingAssetOption.ContractStartDate)
            or nameof(RentalBillingAssetOption.RentalEndDate)
            or nameof(RentalBillingAssetOption.PurchaseDate)
            or nameof(RentalBillingAssetOption.DisposalDate)
            or nameof(RentalBillingAssetOption.InstallDate)
            or nameof(RentalBillingAssetOption.FreeSupplyItems)
            or nameof(RentalBillingAssetOption.PaidSupplyItems)
            or nameof(RentalBillingAssetOption.Notes);

    private void HandleIncludedAssetDetailChanged(RentalBillingAssetOption asset)
    {
        if (asset.AssetId == Guid.Empty)
            return;

        UpdateBillingAssetOptionPool(asset);
        _pendingAssetLinkEdits[asset.AssetId] = BuildAssetLinkEdit(asset);
        foreach (var item in TemplateItems.Where(item => item.IncludedAssetIds.Contains(asset.AssetId)))
        {
            ApplyTemplateSalesFieldDefaults(item);
            item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
            item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        }

        UpdateTemplateDerivedValues();
        StatusMessage = $"'{BuildAssetShortLabel(asset)}' 거래처 임대 자산 정보를 저장 대기 중입니다. 저장하면 렌탈 자산/설치현황에 반영됩니다.";
    }

    private void UpdateBillingAssetOptionPool(RentalBillingAssetOption asset)
    {
        var includedIndex = _includedAssetPool.FindIndex(current => current.AssetId == asset.AssetId);
        if (includedIndex >= 0)
            _includedAssetPool[includedIndex] = CloneBillingAssetOption(asset, isSelected: true);

        var candidateIndex = _candidateAssetPool.FindIndex(current => current.AssetId == asset.AssetId);
        if (candidateIndex >= 0)
            _candidateAssetPool[candidateIndex] = CloneBillingAssetOption(asset, isSelected: false);
    }

    private void HandleIncludedAssetMonthlyFeeChanged(RentalBillingAssetOption asset)
    {
        if (_suppressIncludedAssetMonthlyFeeChanges || asset.AssetId == Guid.Empty)
            return;

        var monthlyFee = Math.Max(0m, asset.MonthlyFee);
        SetCachedAssetMonthlyFeeSilently(asset.AssetId, monthlyFee);

        var edit = GetOrCreatePendingAssetLinkEdit(asset.AssetId);
        edit.MonthlyFee = monthlyFee;

        var affectedItems = TemplateItems
            .Where(item => item.IncludedAssetIds.Contains(asset.AssetId))
            .ToList();
        foreach (var item in affectedItems)
        {
            ApplyIncludedAssetMonthlyFeesToTemplateItem(item, applyZeroFees: true);
            item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
            item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        }

        // 묶음/복수 자산 라인에서 템플릿 금액 역산이 불가능한 경우에도
        // 사용자가 직접 수정한 자산 월요금은 저장 시 자산 원본에 우선 반영한다.
        edit.MonthlyFee = monthlyFee;
        SetCachedAssetMonthlyFeeSilently(asset.AssetId, monthlyFee);
        UpdateTemplateDerivedValues();
        StatusMessage = $"'{BuildAssetShortLabel(asset)}' 월요금을 {monthlyFee:N0}원으로 반영했습니다. 저장하면 렌탈 자산과 월 기준금액에 적용됩니다.";
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
            PurchaseVendor = asset.PurchaseVendor,
            PurchasePrice = asset.PurchasePrice,
            SalePrice = asset.SalePrice,
            CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName,
            InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? "미확인" : asset.BillingEligibilityStatus,
            BillingExclusionReason = asset.BillingExclusionReason,
            ResponsibleOfficeName = responsibleOfficeName,
            ManagementCompanyName = managementCompanyName,
            AssetScopeDisplay = BuildAssetScopeDisplay(responsibleOfficeName, managementCompanyName),
            Notes = asset.Notes ?? string.Empty,
            DepositText = asset.DepositText,
            MonthlyFee = asset.MonthlyFee,
            ContractMonths = asset.ContractMonths,
            ContractDate = ToDateTime(asset.ContractDate),
            ContractStartDate = ToDateTime(asset.ContractStartDate),
            RentalEndDate = ToDateTime(asset.RentalEndDate),
            PurchaseDate = ToDateTime(asset.PurchaseDate),
            DisposalDate = ToDateTime(asset.DisposalDate),
            InstallDate = ToDateTime(asset.InstallDate),
            FreeSupplyItems = asset.FreeSupplyItems,
            PaidSupplyItems = asset.PaidSupplyItems,
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
            PurchaseVendor = asset.PurchaseVendor,
            PurchasePrice = asset.PurchasePrice,
            SalePrice = asset.SalePrice,
            CurrentCustomerName = asset.CurrentCustomerName,
            TargetCustomerName = asset.TargetCustomerName,
            InstallLocation = asset.InstallLocation,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            BillingExclusionReason = asset.BillingExclusionReason,
            CurrentBillingProfileDisplay = asset.CurrentBillingProfileDisplay,
            ResponsibleOfficeName = asset.ResponsibleOfficeName,
            ManagementCompanyName = asset.ManagementCompanyName,
            AssetScopeDisplay = asset.AssetScopeDisplay,
            IsOutsideCurrentOffice = asset.IsOutsideCurrentOffice,
            Notes = asset.Notes,
            DepositText = asset.DepositText,
            MonthlyFee = asset.MonthlyFee,
            ContractMonths = asset.ContractMonths,
            ContractDate = asset.ContractDate,
            ContractStartDate = asset.ContractStartDate,
            RentalEndDate = asset.RentalEndDate,
            PurchaseDate = asset.PurchaseDate,
            DisposalDate = asset.DisposalDate,
            InstallDate = asset.InstallDate,
            FreeSupplyItems = asset.FreeSupplyItems,
            PaidSupplyItems = asset.PaidSupplyItems,
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
        if (!string.IsNullOrWhiteSpace(edit.ItemCategoryName))
            asset.ItemCategoryName = edit.ItemCategoryName;
        if (!string.IsNullOrWhiteSpace(edit.Manufacturer))
            asset.Manufacturer = edit.Manufacturer;
        if (!string.IsNullOrWhiteSpace(edit.ItemName))
            asset.ItemName = edit.ItemName;
        if (!string.IsNullOrWhiteSpace(edit.MachineNumber))
            asset.MachineNumber = edit.MachineNumber;
        if (!string.IsNullOrWhiteSpace(edit.PurchaseVendor))
            asset.PurchaseVendor = edit.PurchaseVendor;
        if (edit.PurchasePrice.HasValue)
            asset.PurchasePrice = edit.PurchasePrice.Value;
        if (edit.SalePrice.HasValue)
            asset.SalePrice = edit.SalePrice.Value;
        if (!string.IsNullOrWhiteSpace(edit.AssetStatus))
            asset.AssetStatus = edit.AssetStatus;
        if (!string.IsNullOrWhiteSpace(edit.BillingEligibilityStatus))
            asset.BillingEligibilityStatus = edit.BillingEligibilityStatus;
        asset.BillingExclusionReason = edit.BillingExclusionReason ?? asset.BillingExclusionReason;
        if (!string.IsNullOrWhiteSpace(edit.DepositText))
            asset.DepositText = edit.DepositText;
        if (edit.MonthlyFee.HasValue)
            asset.MonthlyFee = edit.MonthlyFee.Value;
        if (edit.ContractMonths.HasValue)
            asset.ContractMonths = edit.ContractMonths.Value;
        if (edit.ContractDate.HasValue)
            asset.ContractDate = ToDateTime(edit.ContractDate);
        if (edit.ContractStartDate.HasValue)
            asset.ContractStartDate = ToDateTime(edit.ContractStartDate);
        if (edit.RentalEndDate.HasValue)
            asset.RentalEndDate = ToDateTime(edit.RentalEndDate);
        if (edit.PurchaseDate.HasValue)
            asset.PurchaseDate = ToDateTime(edit.PurchaseDate);
        if (edit.DisposalDate.HasValue)
            asset.DisposalDate = ToDateTime(edit.DisposalDate);
        if (edit.InstallDate.HasValue)
            asset.InstallDate = ToDateTime(edit.InstallDate);
        asset.FreeSupplyItems = edit.FreeSupplyItems ?? asset.FreeSupplyItems;
        asset.PaidSupplyItems = edit.PaidSupplyItems ?? asset.PaidSupplyItems;
        asset.Notes = edit.Notes ?? string.Empty;
    }

    private void ApplyIncludedAssetMonthlyFeesToTemplateItem(RentalBillingTemplateEditorItem? item, bool applyZeroFees = false)
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
        if (monthlyFees.All(fee => fee <= 0m) && !applyZeroFees)
            return;

        var totalMonthlyFee = monthlyFees.Sum();
        var distinctPositiveFees = monthlyFees
            .Where(fee => fee > 0m)
            .Distinct()
            .ToList();
        var effectiveLineMode = GetTemplateItemEffectiveLineMode(item);
        if (monthlyFees.All(fee => fee <= 0m))
        {
            item.Quantity = string.Equals(effectiveLineMode, "묶음", StringComparison.OrdinalIgnoreCase)
                ? 1m
                : includedAssetIds.Count;
            item.UnitPrice = 0m;
            item.NormalizeCalculatedAmount();
            ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
            return;
        }

        if (includedAssetIds.Count == 1 ||
            string.Equals(effectiveLineMode, "묶음", StringComparison.OrdinalIgnoreCase))
        {
            item.Quantity = 1m;
            item.UnitPrice = totalMonthlyFee;
        }
        else if (distinctPositiveFees.Count == 1 && monthlyFees.All(fee => fee <= 0m || fee == distinctPositiveFees[0]))
        {
            item.Quantity = includedAssetIds.Count;
            item.UnitPrice = distinctPositiveFees[0];
        }
        else
        {
            item.Quantity = includedAssetIds.Count;
            item.UnitPrice = includedAssetIds.Count == 0
                ? 0m
                : totalMonthlyFee / includedAssetIds.Count;
        }

        item.NormalizeCalculatedAmount();
        ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
    }

    private bool SyncIndividualTemplateItemsFromIncludedAssets()
    {
        if (TemplateItems.Count == 0)
            return false;

        var valueChanged = false;
        _suppressTemplateItemChangeHandling = true;
        try
        {
            foreach (var item in TemplateItems)
            {
                var normalizedIncludedAssetIds = item.IncludedAssetIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();
                if (!item.IncludedAssetIds.SequenceEqual(normalizedIncludedAssetIds))
                {
                    item.IncludedAssetIds.Clear();
                    foreach (var assetId in normalizedIncludedAssetIds)
                        item.IncludedAssetIds.Add(assetId);
                    valueChanged = true;
                }

            if (IsTemplateItemIndividualMode(item))
            {
                valueChanged |= SetIfChanged(() => item.BillingLineMode, value => item.BillingLineMode = value, "\uAC1C\uBCC4");
                valueChanged |= SetIfChanged(() => item.RepresentativeAssetId, value => item.RepresentativeAssetId = value, null);
                if (normalizedIncludedAssetIds.Count > 0)
                {
                    valueChanged |= SetIfChanged(
                        () => item.Quantity,
                        value => item.Quantity = value,
                        normalizedIncludedAssetIds.Count);
                }
            }

                item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
                item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
                ApplyTemplateSalesFieldDefaults(item);
                item.NormalizeCalculatedAmount();
            }
        }
        finally
        {
            _suppressTemplateItemChangeHandling = false;
        }

        return valueChanged;
    }

    private void RefreshTemplateAmountsFromIncludedAssets(bool applyZeroFees = false)
    {
        foreach (var item in TemplateItems)
            ApplyIncludedAssetMonthlyFeesToTemplateItem(item, applyZeroFees);
    }

    private bool TryPreserveIndividualAggregateTemplateItem(
        RentalBillingTemplateEditorItem item,
        IReadOnlyList<Guid> includedAssetIds,
        IReadOnlyDictionary<Guid, RentalBillingAssetOption> assetLookup,
        out bool valueChanged)
    {
        valueChanged = false;
        if (!IsTemplateItemIndividualMode(item) || includedAssetIds.Count <= 1)
            return false;

        var includedAssets = includedAssetIds
            .Select(id => assetLookup.TryGetValue(id, out var asset) ? asset : null)
            .Where(asset => asset is not null)
            .Cast<RentalBillingAssetOption>()
            .ToList();
        if (includedAssets.Count != includedAssetIds.Count)
            return false;

        var modelNames = includedAssets
            .Select(asset => RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset.ItemName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (modelNames.Count != 1)
            return false;

        var linkedFees = includedAssets
            .Select(asset => Math.Max(0m, asset.MonthlyFee))
            .Distinct()
            .ToList();
        if (linkedFees.Count != 1)
            return false;

        var quantity = item.Quantity <= 0m ? includedAssetIds.Count : item.Quantity;
        if (quantity != includedAssetIds.Count)
            return false;

        var unitPrice = Math.Max(0m, item.UnitPrice);
        if (unitPrice <= 0m && item.Amount > 0m)
            unitPrice = item.Amount / includedAssetIds.Count;
        if (unitPrice < 0m)
            return false;

        valueChanged |= SetIfChanged(() => item.DisplayItemName, value => item.DisplayItemName = value, modelNames[0]);
        valueChanged |= SetIfChanged(() => item.BillingLineMode, value => item.BillingLineMode = value, "개별");
        valueChanged |= SetIfChanged(() => item.RepresentativeAssetId, value => item.RepresentativeAssetId = value, null);
        valueChanged |= SetIfChanged(() => item.Quantity, value => item.Quantity = value, includedAssetIds.Count);
        valueChanged |= SetIfChanged(() => item.UnitPrice, value => item.UnitPrice = value, unitPrice);

        var normalizedIncludedAssetIds = includedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (!item.IncludedAssetIds.SequenceEqual(normalizedIncludedAssetIds))
        {
            item.IncludedAssetIds.Clear();
            foreach (var assetId in normalizedIncludedAssetIds)
                item.IncludedAssetIds.Add(assetId);
            valueChanged = true;
        }

        item.NormalizeCalculatedAmount();
        item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
        item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
        ApplyTemplateSalesFieldDefaults(item);
        ApplyTemplateMonthlyFeesToPendingAssetEdits(item);
        return true;
    }

    private bool MergeIndividualTemplateItemsWithSameModelAndPrice(
        List<RentalBillingTemplateEditorItem> items,
        out bool valueChanged)
    {
        valueChanged = false;
        if (items.Count <= 1)
            return false;

        var firstItemByKey = new Dictionary<IndividualTemplateMergeKey, RentalBillingTemplateEditorItem>();
        var mergedItems = new List<RentalBillingTemplateEditorItem>(items.Count);
        var collectionChanged = false;

        foreach (var item in items)
        {
            if (!IsTemplateItemIndividualMode(item) || item.IncludedAssetIds.All(id => id == Guid.Empty))
            {
                mergedItems.Add(item);
                continue;
            }

            var key = BuildIndividualTemplateMergeKey(item);
            if (!firstItemByKey.TryGetValue(key, out var existing))
            {
                firstItemByKey[key] = item;
                mergedItems.Add(item);
                continue;
            }

            collectionChanged = true;
            var mergedAssetIds = existing.IncludedAssetIds
                .Concat(item.IncludedAssetIds)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (!existing.IncludedAssetIds.SequenceEqual(mergedAssetIds))
            {
                existing.IncludedAssetIds.Clear();
                foreach (var assetId in mergedAssetIds)
                    existing.IncludedAssetIds.Add(assetId);
                valueChanged = true;
            }

            valueChanged |= SetIfChanged(() => existing.DisplayItemName, value => existing.DisplayItemName = value, key.ModelNameKey);
            valueChanged |= SetIfChanged(() => existing.BillingLineMode, value => existing.BillingLineMode = value, "개별");
            valueChanged |= SetIfChanged(() => existing.Unit, value => existing.Unit = value, key.Unit);
            valueChanged |= SetIfChanged(() => existing.Note, value => existing.Note = value, key.Note);
            ApplyIncludedAssetMonthlyFeesToTemplateItem(existing, applyZeroFees: true);
            ApplyTemplateSalesFieldDefaults(existing);
            existing.IncludedAssetSummary = BuildIncludedAssetSummary(existing.IncludedAssetIds);
            existing.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(existing);
        }

        if (collectionChanged)
        {
            items.Clear();
            items.AddRange(mergedItems);
        }

        return collectionChanged;
    }

    private IndividualTemplateMergeKey BuildIndividualTemplateMergeKey(RentalBillingTemplateEditorItem item)
    {
        var modelName = ResolveIndividualTemplateModelName(item);
        if (string.IsNullOrWhiteSpace(modelName))
            modelName = "렌탈 임대료";

        return new IndividualTemplateMergeKey(
            modelName,
            (item.Unit ?? string.Empty).Trim(),
            (item.Note ?? string.Empty).Trim());
    }

    private string ResolveIndividualTemplateModelName(RentalBillingTemplateEditorItem item)
    {
        var modelNames = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Select(FindBillingAssetOption)
            .Where(asset => asset is not null)
            .Select(asset => RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset!.ItemName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (modelNames.Count == 1)
            return modelNames[0];

        var displayItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName);
        if (!string.IsNullOrWhiteSpace(displayItemName))
            return displayItemName;

        var specification = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.Specification);
        return specification;
    }

    private bool ApplyBillingTypeToTemplateLineModes(string? billingType)
    {
        var normalizedBillingType = NormalizeBillingProfileTypeValue(billingType);
        if (string.Equals(normalizedBillingType, "혼합", StringComparison.Ordinal) ||
            TemplateItems.Count == 0)
        {
            return false;
        }

        var changed = false;
        _suppressTemplateItemChangeHandling = true;
        try
        {
            foreach (var item in TemplateItems)
            {
                if (!string.Equals(item.BillingLineMode, normalizedBillingType, StringComparison.Ordinal))
                {
                    item.BillingLineMode = normalizedBillingType;
                    changed = true;
                }

                if (string.Equals(normalizedBillingType, "개별", StringComparison.Ordinal) &&
                    item.RepresentativeAssetId.HasValue)
                {
                    item.RepresentativeAssetId = null;
                    changed = true;
                }
            }
        }
        finally
        {
            _suppressTemplateItemChangeHandling = false;
        }

        return changed;
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

        if (IsIndividualAggregateRepresentativeOnlySpecification(item, current, defaultSpecification))
            return true;

        if (IsLegacyIndividualAggregateEtcSpecification(item, current, defaultSpecification))
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
            var representativeSpecification = ResolveBundleRepresentativeSpecificationLabel(
                representativeAsset,
                item.DisplayItemName);
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

        var individualModelNames = includedAssetIds
            .Select(FindBillingAssetOption)
            .Where(asset => asset is not null)
            .Select(asset => RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(asset!.ItemName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (individualModelNames.Count == 1)
            return individualModelNames[0];

        var individualSpecifications = includedAssetIds
            .Select(FindBillingAssetOption)
            .Where(asset => asset is not null)
            .Select(BuildAssetInvoiceSpecification)
            .Where(specification => !string.IsNullOrWhiteSpace(specification))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (individualSpecifications.Count == 1)
            return individualSpecifications[0];

        var individualRepresentativeAsset = includedAssetIds
            .Select(FindBillingAssetOption)
            .FirstOrDefault(asset => asset is not null);
        var individualRepresentativeSpecification = BuildAssetInvoiceSpecification(individualRepresentativeAsset);
        if (string.IsNullOrWhiteSpace(individualRepresentativeSpecification))
            individualRepresentativeSpecification = FirstNonEmpty(
                RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName),
                "대표 장비");

        return $"{individualRepresentativeSpecification} 외 {includedAssetIds.Count - 1:N0}대";
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

        var representativeSpecification = ResolveBundleRepresentativeSpecificationLabel(
            representativeAsset,
            item.DisplayItemName);
        if (string.IsNullOrWhiteSpace(representativeSpecification))
            representativeSpecification = FirstNonEmpty(
                RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName),
                "대표 장비");

        return string.Equals(currentSpecification.Trim(), representativeSpecification, StringComparison.CurrentCultureIgnoreCase) &&
               string.Equals(defaultSpecification.Trim(), $"{representativeSpecification} 외", StringComparison.CurrentCultureIgnoreCase);
    }

    private bool IsIndividualAggregateRepresentativeOnlySpecification(
        RentalBillingTemplateEditorItem item,
        string currentSpecification,
        string defaultSpecification)
    {
        if (!IsTemplateItemIndividualMode(item) ||
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

        var representativeAsset = includedAssetIds
            .Select(FindBillingAssetOption)
            .FirstOrDefault(asset => asset is not null);
        var representativeSpecification = BuildAssetInvoiceSpecification(representativeAsset);
        if (string.IsNullOrWhiteSpace(representativeSpecification))
            representativeSpecification = FirstNonEmpty(
                RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.DisplayItemName),
                "대표 장비");

        return string.Equals(currentSpecification.Trim(), representativeSpecification, StringComparison.CurrentCultureIgnoreCase) &&
               string.Equals(defaultSpecification.Trim(), $"{representativeSpecification} 외 {includedAssetIds.Count - 1:N0}대", StringComparison.CurrentCultureIgnoreCase);
    }

    private bool IsLegacyIndividualAggregateEtcSpecification(
        RentalBillingTemplateEditorItem item,
        string currentSpecification,
        string defaultSpecification)
    {
        if (!IsTemplateItemIndividualMode(item) ||
            string.IsNullOrWhiteSpace(currentSpecification) ||
            string.IsNullOrWhiteSpace(defaultSpecification))
        {
            return false;
        }

        var includedAssetCount = item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Count();
        if (includedAssetCount <= 1)
            return false;

        var current = currentSpecification.Trim();
        var expectedPrefix = $"{defaultSpecification.Trim()} 외";
        if (current.StartsWith(expectedPrefix, StringComparison.CurrentCultureIgnoreCase) ||
            current.Contains($" {expectedPrefix}", StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        var modelName = ResolveIndividualTemplateModelName(item).Trim();
        if (string.IsNullOrWhiteSpace(modelName))
            return false;

        var modelEtc = $"{modelName} 외";
        return current.StartsWith(modelEtc, StringComparison.CurrentCultureIgnoreCase) ||
               current.Contains($" {modelEtc}", StringComparison.CurrentCultureIgnoreCase) ||
               (!string.Equals(current, modelName, StringComparison.CurrentCultureIgnoreCase) &&
                current.EndsWith(modelName, StringComparison.CurrentCultureIgnoreCase));
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

    private static string ResolveBundleRepresentativeSpecificationLabel(
        RentalBillingAssetOption? representativeAsset,
        string? fallbackItemName)
    {
        var representativeItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(
            representativeAsset?.ItemName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(representativeItemName))
            return representativeItemName;

        return FirstNonEmpty(
            RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(fallbackItemName ?? string.Empty),
            "대표 장비");
    }

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
            SetCachedAssetMonthlyFeeSilently(assetId, monthlyFee);
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

        var linkedAssetFees = includedAssetIds
            .Select(FindBillingAssetOption)
            .Where(asset => asset is not null)
            .Select(asset => Math.Max(0m, asset!.MonthlyFee))
            .ToList();
        if (linkedAssetFees.Count != includedAssetIds.Count ||
            linkedAssetFees.Distinct().Count() != 1)
        {
            return false;
        }

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
            if (asset.MonthlyFee != monthlyFee)
                asset.MonthlyFee = monthlyFee;
        }
    }

    private void SetCachedAssetMonthlyFeeSilently(Guid assetId, decimal monthlyFee)
    {
        _suppressIncludedAssetMonthlyFeeChanges = true;
        try
        {
            SetCachedAssetMonthlyFee(assetId, monthlyFee);
        }
        finally
        {
            _suppressIncludedAssetMonthlyFeeChanges = false;
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
            CatalogItemId = item.CatalogItemId,
            DisplayItemName = (item.DisplayItemName ?? string.Empty).Trim(),
            BillingLineMode = ResolveTemplateBillingLineMode(item.BillingLineMode, EditBillingType),
            Specification = (item.Specification ?? string.Empty).Trim(),
            Unit = (item.Unit ?? string.Empty).Trim(),
            MaterialNumber = (item.MaterialNumber ?? string.Empty).Trim(),
            RepresentativeAssetId = ResolveTemplateRepresentativeAssetId(item),
            Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
            UnitPrice = Math.Round(Math.Max(0m, item.UnitPrice), 0, MidpointRounding.AwayFromZero),
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
            var previewBillingMonths = BuildPreviewBillingMonths();
            foreach (var item in TemplateItems)
            {
                item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
                item.RepresentativeAssetSummary = BuildRepresentativeAssetSummary(item);
                ApplyTemplateSalesFieldDefaults(item);
                if (item.Quantity <= 0m)
                    item.Quantity = 1m;
                item.NormalizeCalculatedAmount();
                item.InvoiceItemNamePreview = BuildInvoiceItemNamePreview(previewBillingMonths);
            }

            EditMonthlyAmount = TemplateItems.Sum(item => item.EffectiveAmount);
            EditItemName = BuildTemplateItemName();
            EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - EditSettledAmount);
            var linkedAssetCount = CountDistinctEditorIncludedAssets();
            var profileAssetCount = ResolveCurrentProfileLinkedAssetCount();
            TemplateSummary = $"표시품목 {TemplateItems.Count:N0}건 / 연결장비 {linkedAssetCount:N0}대";
            BillingAssetCoverageWarning = BuildBillingAssetCoverageWarning(profileAssetCount, linkedAssetCount, LinkAssetsLater);
            BillingSchedulePreviewText = BuildBillingSchedulePreview();
            DocumentIssuePreviewText = BuildDocumentIssuePreview();
            ApplySelectedAssetsHint = BuildApplySelectedAssetsHint(linkedAssetCount);
            SetRepresentativeAssetCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSetRepresentativeAsset));
            OnPropertyChanged(nameof(CanApplySelectedAssets));
            AddSelectedIncludedAssetToTemplateItemCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanAddSelectedIncludedAssetToTemplateItem));
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

    public string GetBillingAssetCoverageStartWarning()
    {
        if (SelectedRow is null || SelectedRow.IsAggregateRow || !SelectedRow.HasPersistedProfile)
            return string.Empty;

        return BuildBillingAssetCoverageWarning(
            SelectedRow.AssetCount,
            SelectedRow.IncludedAssetCount,
            linkAssetsLater: false);
    }

    private int CountDistinctEditorIncludedAssets()
        => TemplateItems
            .SelectMany(item => item.IncludedAssetIds)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Count();

    private int ResolveCurrentProfileLinkedAssetCount()
    {
        var selectedAssetCount = SelectedRow is { IsAggregateRow: false }
            ? Math.Max(0, SelectedRow.AssetCount)
            : 0;
        var pendingLinkedAssetCount = BuildPendingAssetLinkEdits()
            .Select(edit => edit.AssetId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Count();
        return Math.Max(selectedAssetCount, pendingLinkedAssetCount);
    }

    private static string BuildBillingAssetCoverageWarning(
        int profileAssetCount,
        int includedAssetCount,
        bool linkAssetsLater)
    {
        profileAssetCount = Math.Max(0, profileAssetCount);
        includedAssetCount = Math.Max(0, includedAssetCount);

        if (includedAssetCount == 0 && !linkAssetsLater)
        {
            return "청구서 표시 품목에 거래처 임대 자산이 없습니다. 실제 청구/전표 대상 자산이 빠질 수 있으니 새 장비연결로 자산을 추가하거나 '장비 나중 연결'을 선택하세요.";
        }

        if (profileAssetCount > 0 &&
            includedAssetCount > 0 &&
            profileAssetCount != includedAssetCount)
        {
            return $"청구 프로필 연결 자산 {profileAssetCount:N0}대 중 표시품목 포함 자산 {includedAssetCount:N0}대만 실제 청구/전표 대상입니다. 일부 장비만 청구하려는 것이 아니라면 거래처 임대 자산을 확인하세요.";
        }

        return string.Empty;
    }

    private bool TryRejectAggregateSelection(string actionName)
    {
        if (SelectedRow is null || !SelectedRow.IsAggregateRow)
            return false;

        var aggregateSummary = string.IsNullOrWhiteSpace(SelectedRow.AggregateSummary)
            ? "여러 청구 프로필/자산이 묶인 거래처별 요약행입니다."
            : $"{SelectedRow.AggregateSummary} 기준 거래처별 요약행입니다.";
        StatusMessage = $"{aggregateSummary} {actionName}은 '개별 청구건 직접 보기'으로 실제 청구건을 선택한 뒤 진행하세요.";
        return true;
    }

    private void LoadAggregateSelectionSummary(RentalBillingViewRow value)
    {
        _contractDateRefreshCts?.Cancel();
        _pendingAssetLinkEdits.Clear();
        TemplateItems.ReplaceWith(Array.Empty<RentalBillingTemplateEditorItem>());
        SelectedTemplateItem = null;
        _includedAssetPool.Clear();
        _candidateAssetPool.Clear();
        IncludedAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
        CandidateAssets.ReplaceWith(Array.Empty<RentalBillingAssetOption>());
        TemplateSummary = string.IsNullOrWhiteSpace(value.AggregateSummary)
            ? "거래처별 요약 보기입니다."
            : value.AggregateSummary;
        AssetCandidateSummary = "거래처별 요약행에서는 장비 연결 편집을 지원하지 않습니다. '개별 청구건 직접 보기'으로 실제 청구건을 선택하세요.";
        ApplySelectedAssetsHint = value.GroupedPersistedProfileIds.Any(id => id != Guid.Empty)
            ? "거래처별 요약 보기입니다. 청구서 만들기는 연결된 개별 프로필을 한 번에 처리하고, 저장/삭제/장비연결은 '개별 청구건 직접 보기' 후 진행하세요."
            : "거래처별 요약 보기입니다. 청구 가능한 프로필이 없습니다. '개별 청구건 직접 보기'에서 프로필을 생성/저장하세요.";
        _selectedRowBaselineSignature = BuildCurrentEditorSignature();
        StatusMessage = string.IsNullOrWhiteSpace(value.AggregateSummary)
            ? "거래처별 요약 보기입니다. 청구서 만들기는 한 번에 가능하지만 편집은 '개별 청구건 직접 보기' 후 진행하세요."
            : $"{value.AggregateSummary} 기준 거래처별 요약행입니다. 청구서 만들기는 한 번에 가능하지만 편집은 '개별 청구건 직접 보기' 후 진행하세요.";
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
            message = "청구명은 비워둘 수 없습니다.";
            return false;
        }

        var includedAssetCount = CountDistinctEditorIncludedAssets();
        if (includedAssetCount == 0 && !LinkAssetsLater)
        {
            message = "청구서 표시 품목에 거래처 임대 자산이 없습니다. 실제 청구 대상 자산을 연결하거나 '장비 나중 연결'을 선택하세요.";
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

        return HasUnsavedEditorChangesAgainstBaseline();
    }

    private string BuildCurrentEditorSignature()
        => string.Join("||",
            EditCustomerId?.ToString("N") ?? string.Empty,
            NormalizeText(EditCustomerName),
            NormalizeText(EditBusinessNumber),
            NormalizeText(EditInstallLocation),
            NormalizeText(EditItemName),
            NormalizeBillingProfileTypeValue(EditBillingType),
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
            ToDateOnly(EditLastBilledDate),
            ResolvePreviewFirstBillingDate(
                EditBillingDay,
                EditBillingDayMode,
                anchorMonth,
                referenceDate,
                ToDateOnly(EditBillingAnchorDate),
                ToDateOnly(EditBillingStartDate),
                ToDateOnly(EditContractStartDate),
                ToDateOnly(EditContractDate)));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, EditBillingAdvanceMode, dueDate);
        var dayModeText = string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeEndOfMonth, StringComparison.Ordinal)
            ? "말일"
            : $"매월 {RentalBillingScheduleRules.NormalizeBillingDay(EditBillingDay)}일";
        var anchorText = cycleMonths == 1
            ? "매월"
            : $"{anchorMonth}월부터 반복";
        return $"청구 대상 기간: {period.StartDate:yyyy-MM} ~ {period.EndDate:yyyy-MM} / 청구일 규칙: {dayModeText} / 청구기간 시작월: {anchorText} / 예상 결제일: {dueDate:yyyy-MM-dd}";
    }

    private IReadOnlyList<DateOnly> BuildPreviewBillingMonths()
    {
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
            ToDateOnly(EditLastBilledDate),
            ResolvePreviewFirstBillingDate(
                EditBillingDay,
                EditBillingDayMode,
                anchorMonth,
                referenceDate,
                ToDateOnly(EditBillingAnchorDate),
                ToDateOnly(EditBillingStartDate),
                ToDateOnly(EditContractStartDate),
                ToDateOnly(EditContractDate)));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, EditBillingAdvanceMode, dueDate);
        var months = new List<DateOnly>(cycleMonths);
        var cursor = new DateOnly(period.StartDate.Year, period.StartDate.Month, 1);
        var endMonth = new DateOnly(period.EndDate.Year, period.EndDate.Month, 1);
        var guard = Math.Max(cycleMonths, 1) + 24;
        while (cursor <= endMonth && months.Count < guard)
        {
            months.Add(cursor);
            cursor = cursor.AddMonths(1);
        }

        if (months.Count == 0)
            months.Add(new DateOnly(dueDate.Year, dueDate.Month, 1));

        return months;
    }

    private static string BuildInvoiceItemNamePreview(IReadOnlyList<DateOnly> billingMonths)
    {
        if (billingMonths.Count == 0)
            return "전표 품명 계산 대기";

        return string.Join(
            ", ",
            billingMonths
                .Select(month => $"사무기기 렌탈대금[{month.Month}월]")
                .Distinct(StringComparer.Ordinal));
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
            ToDateOnly(EditLastBilledDate),
            ResolvePreviewFirstBillingDate(
                EditBillingDay,
                EditBillingDayMode,
                anchorMonth,
                referenceDate,
                ToDateOnly(EditBillingAnchorDate),
                ToDateOnly(EditBillingStartDate),
                ToDateOnly(EditContractStartDate),
                ToDateOnly(EditContractDate)));
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

    private static DateOnly ResolvePreviewFirstBillingDate(
        int billingDay,
        string? billingDayMode,
        int anchorMonth,
        DateOnly referenceDate,
        DateOnly? billingAnchorDate,
        DateOnly? billingStartDate,
        DateOnly? contractStartDate,
        DateOnly? contractDate)
    {
        var explicitStartDate = billingAnchorDate
                                ?? billingStartDate
                                ?? contractStartDate
                                ?? contractDate;
        var startMonth = explicitStartDate.HasValue
            ? new DateOnly(explicitStartDate.Value.Year, explicitStartDate.Value.Month, 1)
            : new DateOnly(referenceDate.Year, Math.Clamp(anchorMonth, 1, 12), 1);
        return RentalBillingScheduleRules.BuildBillingDate(
            startMonth.Year,
            startMonth.Month,
            billingDay,
            billingDayMode);
    }

    private string BuildApplySelectedAssetsHint(int linkedAssetCount)
    {
        if (SelectedTemplateItem is null)
            return "청구서 표시 품목(거래명세서 출력 라인)을 선택하면, 그 라인에 실제로 청구할 거래처 임대 자산을 연결할 수 있습니다.";

        if (linkedAssetCount == 0)
            return "거래처 임대 자산에서 전표에 넣을 장비를 선택한 뒤 '선택 장비 표시품목 추가'를 누르세요. 청구명만 저장해도 자산은 자동 추가되지 않습니다.";

        return $"표시품목 포함 자산 {linkedAssetCount:N0}대를 확인했습니다. 거래처 임대 자산에서 추가 청구할 장비를 선택해 표시품목에 추가한 뒤 저장하세요.";
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
                    NormalizeText(edit.ItemCategoryName),
                    NormalizeText(edit.Manufacturer),
                    NormalizeText(edit.ItemName),
                    NormalizeText(edit.MachineNumber),
                    NormalizeText(edit.PurchaseVendor),
                    edit.PurchasePrice?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.SalePrice?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                    NormalizeText(edit.AssetStatus),
                    NormalizeText(edit.BillingEligibilityStatus),
                    NormalizeText(edit.BillingExclusionReason),
                    NormalizeText(edit.DepositText),
                    edit.MonthlyFee?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.ContractMonths?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.ContractDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.ContractStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.RentalEndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.PurchaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.DisposalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    edit.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    NormalizeText(edit.FreeSupplyItems),
                    NormalizeText(edit.PaidSupplyItems),
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
        var normalizedDefault = NormalizeBillingProfileTypeValue(defaultBillingType);
        return string.Equals(normalizedDefault, "개별", StringComparison.Ordinal) ? "개별" : "묶음";
    }

    private static string ResolveTemplateBillingLineMode(string? itemBillingLineMode, string? defaultBillingType)
    {
        var normalizedDefault = NormalizeBillingProfileTypeValue(defaultBillingType);
        if (string.Equals(normalizedDefault, "개별", StringComparison.Ordinal) ||
            string.Equals(normalizedDefault, "묶음", StringComparison.Ordinal))
        {
            return normalizedDefault;
        }

        var normalizedItemMode = NormalizeBillingLineModeValue(itemBillingLineMode);
        return string.IsNullOrWhiteSpace(normalizedItemMode)
            ? "묶음"
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

    private static string NormalizeBillingProfileTypeValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed switch
        {
            "개별" => "개별",
            "혼합" => "혼합",
            _ => "묶음"
        };
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
        if (_isDisposed || _suppressFilterReload)
            return;

        if (!CanReloadForSearchText())
        {
            CancelPendingFilterReload();
            StatusMessage = "검색어는 2글자 이상 입력하면 조회합니다. 1글자는 결과가 너무 많아 조회를 보류했습니다.";
            return;
        }

        var signature = BuildCurrentFilterReloadSignature();
        if (!string.IsNullOrWhiteSpace(_pendingFilterReloadSignature) &&
            string.Equals(_pendingFilterReloadSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        if (IsBusy &&
            !string.IsNullOrWhiteSpace(_activeFilterReloadSignature) &&
            string.Equals(_activeFilterReloadSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        CancelPendingFilterReload();
        var cts = new CancellationTokenSource();
        _filterReloadCts = cts;
        _pendingFilterReloadSignature = signature;
        _searchDebouncer.DebounceAsync(
            ResolveFilterReloadDebounceDelay(),
            async () =>
            {
                if (_isDisposed)
                    return;

                _pendingFilterReloadSignature = string.Empty;
                await ReloadCoreAsync(cts.Token);
            },
            ex =>
            {
                if (!_isDisposed)
                    StatusMessage = $"렌탈 청구 목록을 다시 불러오지 못했습니다. {ex.Message}";
            });
    }

    private void CancelPendingFilterReload()
    {
        Interlocked.Increment(ref _filterReloadVersion);
        _pendingFilterReloadSignature = string.Empty;
        _activeFilterReloadSignature = string.Empty;
        _filterReloadCts?.Cancel();
        _filterReloadCts?.Dispose();
        _filterReloadCts = null;
    }

    private TimeSpan ResolveFilterReloadDebounceDelay()
        => string.IsNullOrWhiteSpace(SearchText)
            ? TimeSpan.FromMilliseconds(350)
            : TimeSpan.FromMilliseconds(650);

    private bool CanReloadForSearchText()
    {
        var keyword = (SearchText ?? string.Empty).Trim();
        return keyword.Length == 0 || keyword.Length >= 2;
    }

    private string BuildCurrentFilterReloadSignature()
        => string.Join(
            "|",
            NormalizeFilterSignaturePart(SearchText),
            NormalizeFilterSignaturePart(ResolveSelectedOfficeFilterCode()),
            NormalizeFilterSignaturePart(SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter),
            DueOnly ? "DUE" : string.Empty,
            PastDueOnly ? "PAST" : string.Empty,
            ShowIndividualProfiles ? "INDIVIDUAL" : "GROUPED",
            ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private string BuildUnlinkedAssetLimitNotice(int unlinkedCount)
    {
        if (!string.Equals(SelectedStatusFilter, AllOption, StringComparison.Ordinal) ||
            unlinkedCount < RentalStateService.BillingUnlinkedDefaultResultLimit)
        {
            return string.Empty;
        }

        return $" 기본 조회에서는 청구설정 필요 장비를 최대 {RentalStateService.BillingUnlinkedDefaultResultLimit:N0}대까지만 표시합니다. 전체 확인은 상태 필터를 '청구설정 필요'로 선택하세요.";
    }

    private string BuildBillingProfileLimitNotice(IReadOnlyCollection<RentalBillingViewRow> rows)
    {
        var profileLimit = ResolveInteractiveBillingProfileLimit();
        if (!profileLimit.HasValue)
            return string.Empty;

        var persistedProfileCount = rows.Sum(row => row.GroupedPersistedProfileCount);
        if (persistedProfileCount < profileLimit.Value)
            return string.Empty;

        return $" 청구 프로필은 최대 {profileLimit.Value:N0}건까지 표시 중입니다. 결과가 많으면 검색어 또는 담당지점/상태 필터를 좁혀주세요.";
    }

    private int? ResolveInteractiveBillingProfileLimit()
    {
        if (DueOnly || PastDueOnly)
            return null;

        var selectedStatus = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter;
        if (IsUnlinkedBillingStatusFilterText(selectedStatus))
            return null;

        return string.IsNullOrWhiteSpace(SearchText)
            ? RentalStateService.BillingProfileListResultLimit
            : RentalStateService.BillingProfileSearchResultLimit;
    }

    private static bool IsUnlinkedBillingStatusFilterText(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var normalized = status.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.Equals(normalized, "미연결", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "생성필요", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "청구설정필요", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFilterSignaturePart(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);
}
