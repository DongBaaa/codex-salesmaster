using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class RentalBillingViewModel : ObservableObject
{
    private const string AllOption = "전체";

    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly SessionState _session;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedOfficeFilter;
    [ObservableProperty] private string _selectedAssignedUsernameFilter = AllOption;
    [ObservableProperty] private string _selectedStatusFilter = AllOption;
    [ObservableProperty] private bool _dueOnly;
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 청구 대상을 불러오는 중입니다.";
    [ObservableProperty] private RentalBillingViewRow? _selectedRow;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private string _editBusinessNumber = string.Empty;
    [ObservableProperty] private string _editRealCustomerName = string.Empty;
    [ObservableProperty] private string _editModelName = string.Empty;
    [ObservableProperty] private string _editOfficeCode = string.Empty;
    [ObservableProperty] private string _editBillingMethod = string.Empty;
    [ObservableProperty] private string _editPaymentMethod = string.Empty;
    [ObservableProperty] private string _editBillingStatus = "예정";
    [ObservableProperty] private string _editSettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid;
    [ObservableProperty] private string _editCompletionStatus = PaymentFlowConstants.CompletionPending;
    [ObservableProperty] private string _editEmail = string.Empty;
    [ObservableProperty] private int _editBillingDay = 25;
    [ObservableProperty] private int _editBillingCycleMonths = 1;
    [ObservableProperty] private decimal _editMonthlyAmount;
    [ObservableProperty] private decimal _editDepositAmount;
    [ObservableProperty] private decimal _editSettledAmount;
    [ObservableProperty] private decimal _editOutstandingAmount;
    [ObservableProperty] private bool _editRequiresFollowUp;
    [ObservableProperty] private string _editFollowUpNote = string.Empty;
    [ObservableProperty] private string _editSubmissionDocuments = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private string _editAssignedUsername = string.Empty;
    [ObservableProperty] private DateTime? _editBillingAnchorDate;
    [ObservableProperty] private DateTime? _editContractDate;
    [ObservableProperty] private DateTime? _editContractStartDate;
    [ObservableProperty] private DateTime? _editContractEndDate;
    [ObservableProperty] private DateTime? _editLastBilledDate;
    [ObservableProperty] private DateTime? _editLastSettledDate;
    [ObservableProperty] private bool _editIsActive = true;
    [ObservableProperty] private string _completionNote = string.Empty;

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<string> AssignedUsernameOptions { get; } = new();
    public ObservableCollection<string> StatusOptions { get; } = new();
    public ObservableCollection<string> SettlementStatusOptions { get; } = new();
    public ObservableCollection<string> CompletionStatusOptions { get; } = new();
    public ObservableCollection<string> BillingMethodOptions { get; } = new();
    public ObservableCollection<RentalBillingViewRow> Rows { get; } = new();

    public bool CanViewAll => _session.IsAdmin ||
                              _session.HasPermission(AppPermissionNames.RentalViewAll) ||
                              _session.HasPermission(AppPermissionNames.RentalEditAll);
    public bool CanManageAll => _session.IsAdmin || _session.HasPermission(AppPermissionNames.RentalEditAll);
    public bool CanSave => SelectedRow is null || CanEditCurrentSelection;
    public bool CanStartBillingSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanHoldSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanRegisterSettlementSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool CanMarkCompletedSelected => SelectedRow is not null && CanEditCurrentSelection;
    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;

    private bool CanEditCurrentSelection => SelectedRow is null || CanEditScope(SelectedRow.Source.AssignedUsername, SelectedRow.Source.ResponsibleOfficeCode);

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
    }

    partial void OnEditMonthlyAmountChanged(decimal value) => SyncOutstandingAmount();
    partial void OnEditSettledAmountChanged(decimal value) => SyncOutstandingAmount();
    partial void OnEditFollowUpNoteChanged(string value) => CompletionNote = value;
    partial void OnCompletionNoteChanged(string value) => EditFollowUpNote = value;

    public async Task LoadAsync()
    {
        await ReloadFiltersAsync();
        await ReloadAsync();
        NewProfile();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var selectedId = SelectedRow?.Source.Id;
        IsBusy = true;
        try
        {
            var rows = await _rental.GetBillingRowsAsync(new RentalBillingFilter
            {
                SearchText = SearchText,
                OfficeCode = SelectedOfficeFilter?.Value == AllOption ? string.Empty : SelectedOfficeFilter?.Value ?? string.Empty,
                AssignedUsername = SelectedAssignedUsernameFilter == AllOption ? string.Empty : SelectedAssignedUsernameFilter,
                Status = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter,
                DueOnly = DueOnly,
                ReferenceDate = ReferenceDate
            }, _session);

            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);

            StatusMessage = rows.Count == 0
                ? "조건에 맞는 렌탈 청구 대상이 없습니다."
                : $"렌탈 청구 {rows.Count:N0}건을 조회했습니다.";

            if (selectedId.HasValue)
                SelectRow(selectedId.Value);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            EditOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));

        var entity = new LocalRentalBillingProfile
        {
            Id = EditId,
            CustomerName = EditCustomerName,
            BusinessNumber = EditBusinessNumber,
            RealCustomerName = EditRealCustomerName,
            ModelName = EditModelName,
            ManagementCompanyCode = officeCode,
            BillingMethod = EditBillingMethod,
            PaymentMethod = EditPaymentMethod,
            BillingStatus = EditBillingStatus,
            SettlementStatus = EditSettlementStatus,
            CompletionStatus = EditCompletionStatus,
            Email = EditEmail,
            BillingDay = EditBillingDay,
            BillingCycleMonths = EditBillingCycleMonths,
            MonthlyAmount = EditMonthlyAmount,
            DepositAmount = EditDepositAmount,
            SettledAmount = EditSettledAmount,
            OutstandingAmount = EditOutstandingAmount,
            RequiresFollowUp = EditRequiresFollowUp,
            FollowUpNote = EditFollowUpNote,
            SubmissionDocuments = EditSubmissionDocuments,
            Notes = EditNotes,
            ResponsibleOfficeCode = officeCode,
            AssignedUsername = EditAssignedUsername,
            BillingAnchorDate = ToDateOnly(EditBillingAnchorDate),
            ContractDate = ToDateOnly(EditContractDate),
            ContractStartDate = ToDateOnly(EditContractStartDate),
            ContractEndDate = ToDateOnly(EditContractEndDate),
            LastBilledDate = ToDateOnly(EditLastBilledDate),
            LastSettledDate = ToDateOnly(EditLastSettledDate),
            IsActive = EditIsActive
        };

        var result = await _rental.SaveBillingProfileAsync(entity, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

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

        var targetId = SelectedRow.Source.Id;
        var result = await _rental.StartBillingAsync(targetId, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

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
        var targets = Rows
            .Where(row => row.IsSelected)
            .ToList();
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

            failureMessages.Add($"{row.Source.CustomerName}: {result.Message}");
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
        EditId = Guid.NewGuid();
        EditCustomerName = string.Empty;
        EditBusinessNumber = string.Empty;
        EditRealCustomerName = string.Empty;
        EditModelName = string.Empty;
        EditOfficeCode = OfficeOptions.FirstOrDefault(option => option.Value != AllOption)?.Value
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
        EditAssignedUsername = CanManageAll ? string.Empty : (_session.User?.Username ?? string.Empty);
        EditBillingAnchorDate = null;
        EditContractDate = null;
        EditContractStartDate = null;
        EditContractEndDate = null;
        EditLastBilledDate = null;
        EditLastSettledDate = null;
        EditIsActive = true;
        CompletionNote = string.Empty;
        SelectedRow = null;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
    }

    partial void OnSelectedRowChanged(RentalBillingViewRow? value)
    {
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
        EditCustomerName = source.CustomerName;
        EditBusinessNumber = source.BusinessNumber;
        EditRealCustomerName = source.RealCustomerName;
        EditModelName = source.ModelName;
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
        EditBillingDay = source.BillingDay;
        EditBillingCycleMonths = source.BillingCycleMonths;
        EditMonthlyAmount = source.MonthlyAmount;
        EditDepositAmount = source.DepositAmount;
        EditSettledAmount = source.SettledAmount;
        EditOutstandingAmount = source.OutstandingAmount;
        EditRequiresFollowUp = source.RequiresFollowUp;
        EditFollowUpNote = source.FollowUpNote;
        EditSubmissionDocuments = source.SubmissionDocuments;
        EditNotes = source.Notes;
        EditAssignedUsername = string.IsNullOrWhiteSpace(source.AssignedUsername)
            ? (_session.User?.Username ?? string.Empty)
            : source.AssignedUsername;
        EditBillingAnchorDate = ToDateTime(source.BillingAnchorDate);
        EditContractDate = ToDateTime(source.ContractDate);
        EditContractStartDate = ToDateTime(source.ContractStartDate);
        EditContractEndDate = ToDateTime(source.ContractEndDate);
        EditLastBilledDate = ToDateTime(source.LastBilledDate);
        EditLastSettledDate = ToDateTime(source.LastSettledDate);
        EditIsActive = source.IsActive;
        CompletionNote = source.FollowUpNote;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanStartBillingSelected));
        OnPropertyChanged(nameof(CanHoldSelected));
        OnPropertyChanged(nameof(CanRegisterSettlementSelected));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanMarkCompletedSelected));
    }

    private async Task ReloadFiltersAsync()
    {
        OfficeOptions.Clear();
        OfficeOptions.Add(new DisplayOption { Value = AllOption, DisplayName = AllOption });
        foreach (var office in await _local.GetOfficesAsync())
        {
            OfficeOptions.Add(new DisplayOption
            {
                Value = office.Code,
                DisplayName = office.Name
            });
        }

        SelectedOfficeFilter ??= OfficeOptions.FirstOrDefault();
        if (!OfficeOptions.Any(option => option.Value == EditOfficeCode) && OfficeOptions.Count > 1)
            EditOfficeCode = OfficeOptions[1].Value;

        AssignedUsernameOptions.Clear();
        AssignedUsernameOptions.Add(AllOption);
        foreach (var username in await _rental.GetAssignedUsernamesAsync())
            AssignedUsernameOptions.Add(username);
        if (!AssignedUsernameOptions.Contains(SelectedAssignedUsernameFilter))
            SelectedAssignedUsernameFilter = AllOption;
    }

    private void SelectRow(Guid entityId)
    {
        SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == entityId);
    }

    private bool CanEditScope(string? assignedUsername, string? officeCode)
    {
        if (CanManageAll)
            return true;

        var username = (_session.User?.Username ?? string.Empty).Trim();
        var normalizedAssigned = (assignedUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedAssigned) &&
            string.Equals(normalizedAssigned, username, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rowOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        var userOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        return string.IsNullOrWhiteSpace(normalizedAssigned) &&
               !string.IsNullOrWhiteSpace(rowOffice) &&
               string.Equals(rowOffice, userOffice, StringComparison.OrdinalIgnoreCase);
    }

    private void SyncOutstandingAmount()
    {
        EditOutstandingAmount = Math.Max(0m, EditMonthlyAmount - EditSettledAmount);
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);
}
