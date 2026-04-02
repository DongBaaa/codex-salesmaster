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
    private Task? _candidateAssetsLoadTask;
    private bool _suppressFilterReload;
    private bool _pendingFilterReload;
    private string _selectedRowBaselineSignature = string.Empty;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedOfficeFilter;
    [ObservableProperty] private string _selectedStatusFilter = AllOption;
    [ObservableProperty] private bool _dueOnly;
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 청구 대상을 불러오는 중입니다.";
    [ObservableProperty] private RentalBillingViewRow? _selectedRow;
    [ObservableProperty] private RentalBillingTemplateEditorItem? _selectedTemplateItem;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private Guid? _editCustomerId;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private string _editBusinessNumber = string.Empty;
    [ObservableProperty] private string _editRealCustomerName = string.Empty;
    [ObservableProperty] private string _editBillToCustomerName = string.Empty;
    [ObservableProperty] private string _editInstallLocation = string.Empty;
    [ObservableProperty] private string _editItemName = string.Empty;
    [ObservableProperty] private string _editBillingType = "묶음";
    [ObservableProperty] private string _editBillingAdvanceMode = "후불";
    [ObservableProperty] private string _editOfficeCode = string.Empty;
    [ObservableProperty] private string _editBillingMethod = string.Empty;
    [ObservableProperty] private string _editPaymentMethod = string.Empty;
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
    [ObservableProperty] private string _editFollowUpNote = string.Empty;
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
    [ObservableProperty] private string _completionNote = string.Empty;
    [ObservableProperty] private string _billingSchedulePreviewText = "청구일 규칙을 설정하면 다음 청구일이 표시됩니다.";
    [ObservableProperty] private string _documentIssuePreviewText = "서류 발송 규칙을 설정하면 예상 발송일이 표시됩니다.";
    [ObservableProperty] private string _applySelectedAssetsHint = "청구서 표시 품목과 후보 장비를 선택하면 연결할 수 있습니다.";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _dueCount;
    [ObservableProperty] private int _issueCount;
    [ObservableProperty] private int _completedCount;
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
    public ObservableCollection<RentalBillingAssetOption> CandidateAssets { get; } = new();

    public bool CanViewAll => _session.HasGlobalDataScope ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalEditAll);
    public bool CanManageAll => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalEditAll);
    public bool CanSave => SelectedRow is null || CanEditCurrentSelection;
    public bool CanStartBillingSelected => SelectedRow is not null && CanAccessCurrentSelection;
    public bool CanHoldSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanRegisterSettlementSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanMarkCompletedSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanRemoveTemplateItem => SelectedTemplateItem is not null;
    public bool CanApplySelectedAssets => SelectedTemplateItem is not null && CandidateAssets.Any(asset => asset.IsSelected);
    public bool IsFixedBillingDayMode => string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal);
    public bool IsDocumentLeadDaysVisible => string.Equals(EditDocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate, StringComparison.Ordinal);
    public LocalStateService LocalStateService => _local;
    public RentalStateService RentalStateService => _rental;
    public SessionState SessionState => _session;
    public Guid? InvoiceToOpenAfterClose { get; private set; }

    private bool CanAccessCurrentSelection => SelectedRow is null || CanOperateScope(
        string.IsNullOrWhiteSpace(SelectedRow.Source.ResponsibleOfficeCode)
            ? SelectedRow.Source.ManagementCompanyCode
            : SelectedRow.Source.ResponsibleOfficeCode);

    private bool CanEditCurrentSelection => SelectedRow is null || CanOperateScope(
        string.IsNullOrWhiteSpace(SelectedRow.Source.ResponsibleOfficeCode)
            ? SelectedRow.Source.ManagementCompanyCode
            : SelectedRow.Source.ResponsibleOfficeCode);

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
    partial void OnReferenceDateChanged(DateOnly value) => RequestFilterReload();
    partial void OnEditCustomerNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(EditBillToCustomerName))
            EditBillToCustomerName = value;
    }
    partial void OnEditSettledAmountChanged(decimal value) => EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - value);
    partial void OnCompletionNoteChanged(string value) => EditFollowUpNote = value;
    partial void OnEditFollowUpNoteChanged(string value) => CompletionNote = value;
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
    partial void OnEditBillingStartDateChanged(DateTime? value) => UpdateTemplateDerivedValues();
    partial void OnEditBillingAdvanceModeChanged(string value) => UpdateTemplateDerivedValues();
    partial void OnEditContractStartDateChanged(DateTime? value) => UpdateTemplateDerivedValues();
    partial void OnEditContractDateChanged(DateTime? value) => UpdateTemplateDerivedValues();
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
            DateOnly.FromDateTime(DateTime.Today));
        UpdateTemplateDerivedValues();
    }
    partial void OnEditBillingTypeChanged(string value)
    {
        if (string.Equals((value ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal))
        {
            UpdateTemplateDerivedValues();
            return;
        }

        foreach (var item in TemplateItems)
            item.BillingLineMode = NormalizeBillingLineModeValue(value);

        UpdateTemplateDerivedValues();
    }
    partial void OnSelectedTemplateItemChanged(RentalBillingTemplateEditorItem? value)
    {
        SyncAssetSelectionFromTemplate();
        UpdateTemplateDerivedValues();
        RemoveTemplateItemCommand.NotifyCanExecuteChanged();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
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
            var selectedId = SelectedRow?.Source.Id;
            IsBusy = true;
            try
            {
                var rows = await _rental.GetBillingRowsAsync(new RentalBillingFilter
                {
                    SearchText = SearchText,
                    OfficeCode = SelectedOfficeFilter?.Value == AllOption ? string.Empty : SelectedOfficeFilter?.Value ?? string.Empty,
                    Status = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter,
                    DueOnly = DueOnly,
                    ReferenceDate = ReferenceDate
                }, _session);

                Rows.Clear();
                foreach (var row in rows)
                    Rows.Add(row);

                TotalCount = rows.Count;
                DueCount = rows.Count(row => row.DaysRemaining.HasValue && row.DaysRemaining.Value <= 0);
                IssueCount = rows.Count(row => row.HasDataIssue);
                CompletedCount = rows.Count(row => string.Equals(row.CompletionStatus, PaymentFlowConstants.CompletionDone, StringComparison.OrdinalIgnoreCase));
                TotalOutstandingAmount = rows.Sum(row => row.OutstandingAmount);

                StatusMessage = rows.Count == 0
                    ? "조건에 맞는 렌탈 청구 대상이 없습니다."
                    : $"렌탈 청구 {rows.Count:N0}건을 조회했습니다.";

                if (selectedId.HasValue)
                {
                    SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == selectedId.Value);
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
        if (!TryValidateTemplateConfiguration(out var validationMessage))
        {
            StatusMessage = validationMessage;
            return;
        }

        UpdateTemplateDerivedValues();

        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            EditOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));

        var entity = new LocalRentalBillingProfile
        {
            Id = EditId,
            CustomerId = EditCustomerId,
            CustomerName = EditCustomerName,
            BusinessNumber = EditBusinessNumber,
            RealCustomerName = EditRealCustomerName,
            BillToCustomerName = EditBillToCustomerName,
            InstallSiteName = EditInstallLocation,
            ItemName = EditItemName,
            BillingType = EditBillingType,
            BillingAdvanceMode = EditBillingAdvanceMode,
            ManagementCompanyCode = officeCode,
            BillingMethod = EditBillingMethod,
            PaymentMethod = EditPaymentMethod,
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
            FollowUpNote = EditFollowUpNote,
            SubmissionDocuments = EditSubmissionDocuments,
            Notes = EditNotes,
            ResponsibleOfficeCode = officeCode,
            AssignedUsername = string.Empty,
            BillingAnchorDate = ToDateOnly(EditBillingAnchorDate),
            BillingStartDate = ToDateOnly(EditBillingStartDate),
            ContractDate = ToDateOnly(EditContractDate),
            ContractStartDate = ToDateOnly(EditContractStartDate),
            ContractEndDate = ToDateOnly(EditContractEndDate),
            LastBilledDate = ToDateOnly(EditLastBilledDate),
            LastSettledDate = ToDateOnly(EditLastSettledDate),
            IsActive = EditIsActive,
            BillingTemplateJson = _rental.SerializeBillingTemplateItems(ToTemplateModels())
        };

        var result = await _rental.SaveBillingProfileAsync(entity, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

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

        if (HasUnsavedSelectedRowChanges())
        {
            StatusMessage = "현재 청구 설정 편집 내용이 저장되지 않았습니다. 먼저 저장한 뒤 청구를 시작하세요.";
            return;
        }

        InvoiceToOpenAfterClose = null;
        var targetId = SelectedRow.Source.Id;
        var result = await _rental.StartBillingAsync(targetId, ReferenceDate, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        if (result.RelatedEntityId != Guid.Empty)
            InvoiceToOpenAfterClose = result.RelatedEntityId;

        await ClearAutoSaveDraftAsync();
        await ReloadAsync();
        SelectRow(targetId);
    }

    [RelayCommand]
    private async Task HoldBillingAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "보류할 대상을 선택하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var note = string.IsNullOrWhiteSpace(CompletionNote) ? EditFollowUpNote : CompletionNote;
        var result = await _rental.HoldBillingAsync(targetId, note, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        EditFollowUpNote = note;
        CompletionNote = string.Empty;
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

        var targetId = SelectedRow.Source.Id;
        var settledAmount = EditSettledAmount > 0m ? EditSettledAmount : (decimal?)null;
        var note = string.IsNullOrWhiteSpace(CompletionNote) ? EditFollowUpNote : CompletionNote;
        var result = await _rental.RegisterBillingSettlementAsync(targetId, ReferenceDate, settledAmount, note, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        CompletionNote = string.Empty;
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

        var result = await _rental.DeleteBillingProfileAsync(SelectedRow.Source.Id, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await ReloadAsync();
        NewProfile();
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

        var confirmation = MessageBox.Show(
            $"선택한 {targets.Count:N0}건을 삭제하시겠습니까?",
            "렌탈 청구 선택삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        var successCount = 0;
        var failureMessages = new List<string>();
        foreach (var row in targets)
        {
            var result = await _rental.DeleteBillingProfileAsync(row.Source.Id, _session);
            if (result.Success)
            {
                successCount++;
                continue;
            }

                failureMessages.Add($"{row.CustomerDisplayName}: {result.Message}");
        }

        await ReloadAsync();
        NewProfile();

        StatusMessage = failureMessages.Count == 0
            ? $"선택한 렌탈 청구 {successCount:N0}건을 삭제했습니다."
            : $"삭제 성공 {successCount:N0}건 / 실패 {failureMessages.Count:N0}건 - {string.Join(" | ", failureMessages.Take(3))}";
    }

    [RelayCommand]
    private async Task MarkCompletedAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "청구 처리할 대상을 선택하세요.";
            return;
        }

        var targetId = SelectedRow.Source.Id;
        var result = await _rental.MarkBillingCompletedAsync(
            targetId,
            ReferenceDate,
            "완료",
            CompletionNote,
            _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        CompletionNote = string.Empty;
        await ReloadAsync();
        SelectRow(targetId);
    }

    [RelayCommand]
    private void NewProfile()
    {
        DiscardAutoSaveDraft();
        EditId = Guid.NewGuid();
        EditCustomerId = null;
        EditCustomerName = string.Empty;
        EditBusinessNumber = string.Empty;
        EditRealCustomerName = string.Empty;
        EditBillToCustomerName = string.Empty;
        EditInstallLocation = string.Empty;
        EditItemName = string.Empty;
        EditBillingType = "묶음";
        EditBillingAdvanceMode = "후불";
        EditOfficeCode = EditOfficeOptions.FirstOrDefault()?.Value
            ?? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        EditBillingMethod = string.Empty;
        EditPaymentMethod = string.Empty;
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
        EditFollowUpNote = string.Empty;
        EditSubmissionDocuments = string.Empty;
        EditNotes = string.Empty;
        EditBillingAnchorDate = null;
        EditBillingStartDate = DateTime.Today;
        EditContractDate = null;
        EditContractStartDate = null;
        EditContractEndDate = null;
        EditLastBilledDate = null;
        EditLastSettledDate = null;
        EditIsActive = true;
        CompletionNote = string.Empty;
        TemplateItems.Clear();
        TemplateItems.Add(CreateDefaultTemplateItem());
        SelectedTemplateItem = TemplateItems.FirstOrDefault();
        CandidateAssets.Clear();
        TemplateSummary = "표시품목 1건 / 연결장비 0건";
        AssetCandidateSummary = "후보 장비가 없습니다.";
        LinkAssetsLater = false;
        SelectedRow = null;
        InvoiceToOpenAfterClose = null;
        _selectedRowBaselineSignature = string.Empty;
        UpdateTemplateDerivedValues();
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
    }

    [RelayCommand]
    private void AddTemplateItem()
    {
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
            EditBillToCustomerName,
            EditInstallLocation,
            preserveSelection: true,
            autoIncludeAllCandidates: false);
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedAssets))]
    private void ApplySelectedAssetsToTemplate()
    {
        if (SelectedTemplateItem is null)
            return;

        SelectedTemplateItem.IncludedAssetIds.Clear();
        foreach (var assetId in CandidateAssets.Where(asset => asset.IsSelected).Select(asset => asset.AssetId).Distinct())
            SelectedTemplateItem.IncludedAssetIds.Add(assetId);

        SelectedTemplateItem.IncludedAssetSummary = BuildIncludedAssetSummary(SelectedTemplateItem.IncludedAssetIds);
        UpdateTemplateDerivedValues();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

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
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanStartBillingSelected));
            OnPropertyChanged(nameof(CanHoldSelected));
            OnPropertyChanged(nameof(CanRegisterSettlementSelected));
            OnPropertyChanged(nameof(CanDeleteSelected));
            OnPropertyChanged(nameof(CanMarkCompletedSelected));
            return;
        }

        var source = value.Source;
        EditId = source.Id;
        EditCustomerId = source.CustomerId;
        EditCustomerName = string.IsNullOrWhiteSpace(value.CustomerDisplayName) ? source.CustomerName : value.CustomerDisplayName;
        EditBusinessNumber = source.BusinessNumber;
        EditRealCustomerName = source.RealCustomerName;
        EditBillToCustomerName = string.IsNullOrWhiteSpace(source.BillToCustomerName) ? source.CustomerName : source.BillToCustomerName;
        EditInstallLocation = string.IsNullOrWhiteSpace(value.InstallLocationDisplay)
            ? (string.IsNullOrWhiteSpace(source.InstallSiteName) ? source.RealCustomerName : source.InstallSiteName)
            : value.InstallLocationDisplay;
        EditItemName = source.ItemName;
        EditBillingType = string.IsNullOrWhiteSpace(source.BillingType) ? "묶음" : source.BillingType;
        EditBillingAdvanceMode = string.IsNullOrWhiteSpace(source.BillingAdvanceMode) ? "후불" : source.BillingAdvanceMode;
        EditOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            string.IsNullOrWhiteSpace(source.ResponsibleOfficeCode)
                ? source.ManagementCompanyCode
                : source.ResponsibleOfficeCode,
            _session.OfficeCode);
        EditBillingMethod = source.BillingMethod;
        EditPaymentMethod = source.PaymentMethod;
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
        EditRequiresFollowUp = source.RequiresFollowUp;
        EditFollowUpNote = source.FollowUpNote;
        EditSubmissionDocuments = source.SubmissionDocuments;
        EditNotes = source.Notes;
        EditBillingAnchorDate = ToDateTime(source.BillingAnchorDate);
        EditBillingStartDate = ToDateTime(source.BillingStartDate);
        EditContractDate = ToDateTime(source.ContractDate);
        EditContractStartDate = ToDateTime(source.ContractStartDate);
        EditContractEndDate = ToDateTime(source.ContractEndDate);
        EditLastBilledDate = ToDateTime(source.LastBilledDate);
        EditLastSettledDate = ToDateTime(source.LastSettledDate);
        EditIsActive = source.IsActive;
        CompletionNote = string.IsNullOrWhiteSpace(value.DataIssueSummary)
            ? source.FollowUpNote
            : value.DataIssueSummary;
        LoadTemplateItemsFromProfile(source);
        StartCandidateAssetsLoad(
            source.Id,
            EditCustomerId,
            EditCustomerName,
            EditBillToCustomerName,
            EditInstallLocation,
            preserveSelection: false,
            autoIncludeAllCandidates: false);
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
    }

    private async Task ReloadFiltersAsync()
    {
        _suppressFilterReload = true;
        try
        {
            var currentFilterValue = SelectedOfficeFilter?.Value ?? AllOption;

            OfficeOptions.Clear();
            EditOfficeOptions.Clear();
            OfficeOptions.Add(new DisplayOption { Value = AllOption, DisplayName = AllOption });
            foreach (var office in await _local.GetOfficesAsync())
            {
                var option = new DisplayOption
                {
                    Value = office.Code,
                    DisplayName = office.Name
                };
                OfficeOptions.Add(option);
                EditOfficeOptions.Add(new DisplayOption
                {
                    Value = office.Code,
                    DisplayName = office.Name
                });
            }

            SelectedOfficeFilter = OfficeOptions.FirstOrDefault(option =>
                                       string.Equals(option.Value, currentFilterValue, StringComparison.OrdinalIgnoreCase))
                                   ?? OfficeOptions.FirstOrDefault(option => option.Value == AllOption)
                                   ?? OfficeOptions.FirstOrDefault();

            if (!EditOfficeOptions.Any(option => option.Value == EditOfficeCode) && EditOfficeOptions.Count > 0)
                EditOfficeCode = EditOfficeOptions[0].Value;

        }
        finally
        {
            _suppressFilterReload = false;
        }
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
        EditOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            customer.ResponsibleOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));

        var department = customer.Department?.Trim() ?? string.Empty;
        EditRealCustomerName = string.IsNullOrWhiteSpace(department) ? EditCustomerName : department;
        EditBillToCustomerName = EditCustomerName;
        EditInstallLocation = string.IsNullOrWhiteSpace(department)
            ? string.Join(" ", new[] { customer.Address, customer.DetailAddress }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : department;

        StartCandidateAssetsLoad(
            EditId == Guid.Empty ? null : EditId,
            EditCustomerId,
            EditCustomerName,
            EditBillToCustomerName,
            EditInstallLocation,
            preserveSelection: false,
            autoIncludeAllCandidates: true);
    }

    private void SelectRow(Guid entityId)
    {
        SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == entityId);
    }

    private void StartCandidateAssetsLoad(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string billToCustomerName,
        string installLocation,
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
            billToCustomerName,
            installLocation,
            preserveSelection,
            autoIncludeAllCandidates,
            cts.Token);

        UiTaskHelper.Forget(
            _candidateAssetsLoadTask,
            "RENTAL",
            "렌탈 청구 후보 장비 조회",
            ex =>
            {
                if (ex is OperationCanceledException)
                    return;

                StatusMessage = $"후보 장비를 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task LoadCandidateAssetsAsync(
        Guid? billingProfileId,
        Guid? customerId,
        string customerName,
        string billToCustomerName,
        string installLocation,
        bool preserveSelection,
        bool autoIncludeAllCandidates,
        CancellationToken ct = default)
    {
        var previousSelections = preserveSelection && SelectedTemplateItem is not null
            ? SelectedTemplateItem.IncludedAssetIds.ToHashSet()
            : new HashSet<Guid>();

        var assets = await _rental.GetBillingAssetCandidatesAsync(
            billingProfileId,
            customerId,
            customerName,
            billToCustomerName,
            installLocation,
            _session,
            ct);

        ct.ThrowIfCancellationRequested();

        var autoAssignedAssetIds = new HashSet<Guid>();
        if (SelectedTemplateItem is not null &&
            !LinkAssetsLater &&
            autoIncludeAllCandidates &&
            !preserveSelection &&
            !SelectedTemplateItem.IncludedAssetIds.Any() &&
            assets.Count > 0)
        {
            foreach (var asset in assets.Select(asset => asset.Id).Distinct())
            {
                if (!SelectedTemplateItem.IncludedAssetIds.Contains(asset))
                    SelectedTemplateItem.IncludedAssetIds.Add(asset);
                autoAssignedAssetIds.Add(asset);
            }
        }

        CandidateAssets.Clear();
        foreach (var asset in assets)
        {
            var option = new RentalBillingAssetOption
            {
                AssetId = asset.Id,
                ManagementNumber = asset.ManagementNumber,
                ItemName = asset.ItemName,
                MachineNumber = asset.MachineNumber,
                CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName,
                BillToCustomerName = string.IsNullOrWhiteSpace(asset.BillToCustomerName) ? asset.CustomerName : asset.BillToCustomerName,
                InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                AssetStatus = asset.AssetStatus,
                BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? "미확인" : asset.BillingEligibilityStatus,
                MonthlyFee = asset.MonthlyFee,
                IsSelected = autoAssignedAssetIds.Contains(asset.Id) || previousSelections.Contains(asset.Id)
            };
            option.PropertyChanged += (_, _) =>
            {
                ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanApplySelectedAssets));
            };
            CandidateAssets.Add(option);
        }

        AssetCandidateSummary = assets.Count == 0
            ? "후보 장비가 없습니다."
            : $"후보 장비 {assets.Count:N0}대";
        UpdateTemplateDerivedValues();
        SyncAssetSelectionFromTemplate();
    }

    private void LoadTemplateItemsFromProfile(LocalRentalBillingProfile profile)
    {
        TemplateItems.Clear();
        var templateItems = _rental.GetBillingTemplateItems(profile);
        foreach (var item in templateItems)
        {
            var editorItem = new RentalBillingTemplateEditorItem
            {
                ItemId = item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                    ? NormalizeBillingLineModeValue(item.BillingLineMode)
                    : NormalizeBillingLineModeValue(EditBillingType),
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
        UpdateTemplateDerivedValues();
        _selectedRowBaselineSignature = BuildCurrentEditorSignature();
    }

    private RentalBillingTemplateEditorItem CreateDefaultTemplateItem()
    {
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = string.IsNullOrWhiteSpace(EditItemName) ? "렌탈 임대료" : EditItemName,
            BillingLineMode = string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                ? string.Empty
                : NormalizeBillingLineModeValue(EditBillingType),
            Quantity = 1m,
            UnitPrice = EditMonthlyAmount,
            Amount = EditMonthlyAmount
        };
        WireTemplateItem(item);
        return item;
    }

    private void WireTemplateItem(RentalBillingTemplateEditorItem item)
    {
        item.PropertyChanged += (_, _) =>
        {
            UpdateTemplateDerivedValues();
            ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        };
    }

    private void SyncAssetSelectionFromTemplate()
    {
        if (SelectedTemplateItem is null)
        {
            foreach (var asset in CandidateAssets)
                asset.IsSelected = false;
            return;
        }

        var selectedAssetIds = SelectedTemplateItem.IncludedAssetIds.ToHashSet();
        foreach (var asset in CandidateAssets)
            asset.IsSelected = selectedAssetIds.Contains(asset.AssetId);

        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplySelectedAssets));
    }

    private List<RentalBillingTemplateItemModel> ToTemplateModels()
        => TemplateItems.Select(item => new RentalBillingTemplateItemModel
        {
            ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
            DisplayItemName = (item.DisplayItemName ?? string.Empty).Trim(),
            BillingLineMode = string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                ? NormalizeBillingLineModeValue(item.BillingLineMode)
                : NormalizeBillingLineModeValue(EditBillingType),
            Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
            UnitPrice = Math.Max(0m, item.UnitPrice),
            Amount = item.Amount > 0m ? item.Amount : item.EffectiveAmount,
            Note = (item.Note ?? string.Empty).Trim(),
            IncludedAssetIds = item.IncludedAssetIds.Distinct().ToList()
        }).ToList();

    private void UpdateTemplateDerivedValues()
    {
        foreach (var item in TemplateItems)
        {
            item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
            if (item.Quantity <= 0m)
                item.Quantity = 1m;
            if (item.Amount <= 0m)
                item.Amount = item.EffectiveAmount;
        }

        EditMonthlyAmount = TemplateItems.Sum(item => item.EffectiveAmount);
        EditItemName = BuildTemplateItemName();
        EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - EditSettledAmount);
        var linkedAssetCount = TemplateItems.SelectMany(item => item.IncludedAssetIds).Distinct().Count();
        TemplateSummary = $"표시품목 {TemplateItems.Count:N0}건 / 연결장비 {linkedAssetCount:N0}대";
        BillingSchedulePreviewText = BuildBillingSchedulePreview();
        DocumentIssuePreviewText = BuildDocumentIssuePreview();
        ApplySelectedAssetsHint = BuildApplySelectedAssetsHint(linkedAssetCount);
        OnPropertyChanged(nameof(CanApplySelectedAssets));
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

        var labels = CandidateAssets
            .Where(asset => ids.Contains(asset.AssetId))
            .Select(asset => string.IsNullOrWhiteSpace(asset.ManagementNumber)
                ? asset.ItemName
                : $"{asset.ManagementNumber} {asset.ItemName}".Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToList();

        return labels.Count == 0
            ? $"{ids.Count:N0}대 연결"
            : ids.Count > labels.Count
                ? $"{string.Join(", ", labels)} 외 {ids.Count - labels.Count}대"
                : string.Join(", ", labels);
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

        if (string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal))
        {
            if (TemplateItems.Any(item => string.IsNullOrWhiteSpace(NormalizeBillingLineModeValue(item.BillingLineMode))))
            {
                message = "혼합 청구는 모든 청구항목에 라인유형(묶음/개별)을 지정해야 합니다.";
                return false;
            }

            return true;
        }

        var normalizedMode = NormalizeBillingLineModeValue(EditBillingType);
        foreach (var item in TemplateItems)
            item.BillingLineMode = normalizedMode;

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
            NormalizeText(EditRealCustomerName),
            NormalizeText(EditBillToCustomerName),
            NormalizeText(EditInstallLocation),
            NormalizeText(EditItemName),
            NormalizeBillingLineModeValue(EditBillingType),
            NormalizeBillingAdvanceModeValue(EditBillingAdvanceMode),
            NormalizeText(EditOfficeCode),
            NormalizeText(EditBillingMethod),
            NormalizeText(EditPaymentMethod),
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
            NormalizeText(EditFollowUpNote),
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
            BuildTemplateSignature(ToTemplateModels(), EditBillingType));

    private string BuildBillingSchedulePreview()
    {
        var referenceDate = ToDateOnly(EditBillingStartDate) ?? DateOnly.FromDateTime(DateTime.Today);
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
        var referenceDate = ToDateOnly(EditBillingStartDate) ?? DateOnly.FromDateTime(DateTime.Today);
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
            return "청구서 표시 품목을 선택하면 연결 안내가 표시됩니다.";

        if (linkedAssetCount == 0 && CandidateAssets.Count > 0 && !CandidateAssets.Any(asset => asset.IsSelected))
            return $"후보 장비 {CandidateAssets.Count:N0}대가 있습니다. 아래 후보 장비를 체크한 뒤 현재 품목에 연결하세요.";

        if (!CandidateAssets.Any(asset => asset.IsSelected))
            return "후보 장비를 체크한 뒤 현재 품목에 연결하세요.";

        return $"선택한 장비 {CandidateAssets.Count(asset => asset.IsSelected):N0}대를 현재 품목에 연결할 수 있습니다.";
    }

    private static string BuildTemplateSignature(IEnumerable<RentalBillingTemplateItemModel> items, string billingType)
        => string.Join(";;", items.Select(item => BuildTemplateItemSignature(item, billingType)));

    private static string BuildTemplateItemSignature(RentalBillingTemplateItemModel item, string billingType)
    {
        var effectiveMode = string.Equals((billingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
            ? NormalizeBillingLineModeValue(item.BillingLineMode)
            : NormalizeBillingLineModeValue(billingType);
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var amount = item.Amount > 0m ? item.Amount : Math.Max(0m, quantity) * unitPrice;
        var includedAssetIds = string.Join(",", item.IncludedAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .Select(id => id.ToString("N")));

        return string.Join("|",
            NormalizeText(item.DisplayItemName),
            effectiveMode,
            NormalizeDecimal(quantity),
            NormalizeDecimal(unitPrice),
            NormalizeDecimal(amount),
            NormalizeText(item.Note),
            includedAssetIds);
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
