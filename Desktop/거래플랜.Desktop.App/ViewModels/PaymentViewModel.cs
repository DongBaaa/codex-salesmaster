using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PaymentViewModel : ObservableObject
{
    public sealed record TransactionKindOption(string Value, string Label);

    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private List<LocalCustomer> _allCustomers = new();
    private LocalInvoice? _contextInvoice;
    private LocalRentalBillingProfile? _contextRentalProfile;
    private Guid? _contextRentalBillingRunId;
    private decimal _contextRentalBilledAmount;
    private string _contextRentalPeriodLabel = string.Empty;
    private LocalInvoice? _linkedInvoice;
    private LocalRentalBillingProfile? _linkedRentalProfile;
    private Guid? _linkedRentalBillingRunId;
    private decimal _linkedRentalBilledAmount;
    private string _linkedRentalPeriodLabel = string.Empty;
    private Guid? _editingTransactionId;
    private long _editingTransactionRevision;
    private bool _suppressTransactionKindChange;
    private int _historyLoadVersion;
    private int _attachmentLoadVersion;
    private int _contextRefreshVersion;
    private int _settlementSuggestionVersion;

    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _customerName = "거래처 선택";
    [ObservableProperty] private string _customerPhone = "-";
    [ObservableProperty] private string _customerCategory = "-";
    [ObservableProperty] private string _customerDepartment = "-";
    [ObservableProperty] private string _customerContactPerson = "-";
    [ObservableProperty] private decimal _advanceBalance;
    [ObservableProperty] private decimal _settlementAmount;
    [ObservableProperty] private string _transactionContextSummary = "거래처를 선택하면 거래 맥락이 표시됩니다.";
    [ObservableProperty] private string _transactionSummary = "수금/지급 요약이 없습니다.";
    [ObservableProperty] private bool _isCustomerSelectionLocked;

    [ObservableProperty] private DateOnly _receiptDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private decimal _cashReceipt;
    [ObservableProperty] private decimal _cardReceipt;
    [ObservableProperty] private decimal _bankReceipt;
    [ObservableProperty] private decimal _discountApplied;
    [ObservableProperty] private decimal _receiptTotal;

    [ObservableProperty] private DateOnly _paymentDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private decimal _cashPayment;
    [ObservableProperty] private decimal _cardPayment;
    [ObservableProperty] private decimal _bankPayment;
    [ObservableProperty] private decimal _discountReceived;
    [ObservableProperty] private decimal _paymentTotal;

    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _selectedTransactionKind = PaymentFlowConstants.TransactionKindReceipt;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelHistoryEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddAttachmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAttachmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(VerifyAttachmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(RejectAttachmentCommand))]
    private bool _isSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelHistoryEditCommand))]
    private bool _isEditingHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddAttachment))]
    [NotifyPropertyChangedFor(nameof(CanEditHistory))]
    [NotifyPropertyChangedFor(nameof(CanDeleteHistory))]
    private LocalTransaction? _selectedHistory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviewAttachment))]
    [NotifyPropertyChangedFor(nameof(CanDeleteAttachment))]
    [NotifyPropertyChangedFor(nameof(CanVerifyAttachment))]
    [NotifyPropertyChangedFor(nameof(CanRejectAttachment))]
    private LocalTransactionAttachment? _selectedAttachment;

    [ObservableProperty] private string _selectedAttachmentType = "입금확인증";
    [ObservableProperty] private string _attachmentDescription = string.Empty;

    public ObservableCollection<TransactionKindOption> TransactionKinds { get; } = new();
    public ObservableCollection<LocalTransaction> History { get; } = new();
    public ObservableCollection<LocalTransactionAttachment> Attachments { get; } = new();

    public IReadOnlyList<string> AttachmentTypes { get; } =
    [
        "입금확인증",
        "영수증",
        "계좌이체",
        "세금계산서",
        "카드전표",
        "기타"
    ];

    public string PaymentActionLabel => GetTransactionKindLabel(SelectedTransactionKind);
    public string ReserveBalanceLabelText => UsesPrepaidReserve() ? "선지급잔액" : "선수금잔액";
    public bool CanSelectCustomer => !IsCustomerSelectionLocked;
    public bool IsSettlementAmountEnabled =>
        PaymentFlowConstants.IsInvoiceSettlementKind(SelectedTransactionKind) ||
        PaymentFlowConstants.IsRentalSettlementKind(SelectedTransactionKind) ||
        (_linkedInvoice is not null && PaymentFlowConstants.IsGeneralSettlementKind(SelectedTransactionKind));

    public bool CanAddAttachment => SelectedHistory is not null;
    public bool CanEditHistory => SelectedHistory is not null && !IsSaving;
    public bool CanDeleteHistory => SelectedHistory is not null && !IsSaving;
    public bool CanCancelHistoryEdit => IsEditingHistory && !IsSaving;
    public bool CanPreviewAttachment => SelectedAttachment is not null && File.Exists(SelectedAttachment.StoredPath);
    public bool CanDeleteAttachment =>
        SelectedAttachment is not null &&
        (_session.HasAdministrativePrivileges || !string.Equals(SelectedAttachment.VerificationStatus, "확인완료", StringComparison.OrdinalIgnoreCase));
    public bool CanVerifyAttachment => _session.HasAdministrativePrivileges && SelectedAttachment is not null;
    public bool CanRejectAttachment => _session.HasAdministrativePrivileges && SelectedAttachment is not null;
    public bool IsAdmin => _session.HasAdministrativePrivileges;
    public string SaveButtonLabel => IsEditingHistory ? "수정 저장" : "저장";

    public PaymentViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        RebuildTransactionKinds();
    }

    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;

    public async Task LoadAsync(LocalCustomer? preselect = null)
    {
        _allCustomers = await _local.GetCustomersForOperationalSelectionAsync(_session);
        _contextInvoice = null;
        _contextRentalProfile = null;
        _contextRentalBillingRunId = null;
        _contextRentalBilledAmount = 0m;
        _contextRentalPeriodLabel = string.Empty;
        _linkedInvoice = null;
        _linkedRentalProfile = null;
        _linkedRentalBillingRunId = null;
        _linkedRentalBilledAmount = 0m;
        _linkedRentalPeriodLabel = string.Empty;
        IsCustomerSelectionLocked = false;
        if (preselect is not null)
            SetCustomer(preselect);
        else
            ResetCustomerDisplay();

        NewEntry();
        await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
    }

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersForOperationalSelectionAsync(_session);
    }

    public void SetCustomer(LocalCustomer customer)
    {
        if (!CanSelectCustomer && SelectedCustomer is not null && SelectedCustomer.Id != customer.Id)
            return;

        SelectedCustomer = customer;
        CustomerName = customer.NameOriginal;
        CustomerPhone = string.IsNullOrWhiteSpace(customer.Phone) ? "-" : customer.Phone;
        CustomerCategory = string.IsNullOrWhiteSpace(customer.PriceGrade) ? "-" : customer.PriceGrade;
        CustomerDepartment = string.IsNullOrWhiteSpace(customer.Department) ? "-" : customer.Department;
        CustomerContactPerson = string.IsNullOrWhiteSpace(customer.ContactPerson) ? "-" : customer.ContactPerson;
        RequestLoadHistory(customer.Id);
        RequestRefreshContext();
    }

    public async Task ConfigureForInvoiceAsync(LocalInvoice invoice)
    {
        _contextInvoice = invoice;
        _contextRentalProfile = null;
        _contextRentalBillingRunId = null;
        _contextRentalBilledAmount = 0m;
        _contextRentalPeriodLabel = string.Empty;
        _linkedInvoice = invoice;
        _linkedRentalProfile = null;
        _linkedRentalBillingRunId = null;
        _linkedRentalBilledAmount = 0m;
        _linkedRentalPeriodLabel = string.Empty;
        IsCustomerSelectionLocked = true;

        var customer = _allCustomers.FirstOrDefault(current => current.Id == invoice.CustomerId)
            ?? await _local.GetCustomerForOperationalSelectionAsync(invoice.CustomerId, _session);
        if (customer is not null)
            SetCustomer(customer);

        var transactionKind = invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement
            ? PaymentFlowConstants.TransactionKindPayment
            : PaymentFlowConstants.TransactionKindReceipt;
        RebuildTransactionKinds(transactionKind);
        await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
        await ApplySuggestedAmountsCoreAsync(forceResetAmounts: true, Interlocked.Increment(ref _settlementSuggestionVersion));
        Memo = invoice.Memo;
    }

    public async Task ConfigureForRentalBillingAsync(
        LocalRentalBillingProfile profile,
        Guid? billingRunId,
        decimal billedAmount,
        string? periodLabel,
        LocalCustomer? customer = null)
    {
        _contextInvoice = null;
        _contextRentalProfile = profile;
        _contextRentalBillingRunId = billingRunId;
        _contextRentalBilledAmount = Math.Max(0m, billedAmount);
        _contextRentalPeriodLabel = (periodLabel ?? string.Empty).Trim();
        _linkedInvoice = null;
        _linkedRentalProfile = profile;
        _linkedRentalBillingRunId = billingRunId;
        _linkedRentalBilledAmount = Math.Max(0m, billedAmount);
        _linkedRentalPeriodLabel = (periodLabel ?? string.Empty).Trim();
        IsCustomerSelectionLocked = true;

        customer ??= profile.CustomerId.HasValue
            ? _allCustomers.FirstOrDefault(current => current.Id == profile.CustomerId.Value)
                ?? await _local.GetCustomerForOperationalSelectionAsync(profile.CustomerId.Value, _session)
            : _allCustomers.FirstOrDefault(current => string.Equals(current.NameOriginal, profile.CustomerName, StringComparison.OrdinalIgnoreCase));
        if (customer is not null)
            SetCustomer(customer);

        RebuildTransactionKinds(PaymentFlowConstants.TransactionKindRentalReceipt);
        await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
        await ApplySuggestedAmountsCoreAsync(forceResetAmounts: true, Interlocked.Increment(ref _settlementSuggestionVersion));
        Memo = profile.Notes ?? string.Empty;
    }

    public Task ConfigureForRentalBillingAsync(LocalRentalBillingProfile profile, LocalCustomer? customer = null)
        => ConfigureForRentalBillingAsync(
            profile,
            billingRunId: null,
            billedAmount: profile.MonthlyAmount,
            periodLabel: string.Empty,
            customer);

    public Task ConfigureForRentalAsync(LocalRentalBillingProfile profile)
        => ConfigureForRentalBillingAsync(profile);

    public void NewEntry()
    {
        _editingTransactionId = null;
        _editingTransactionRevision = 0;
        IsEditingHistory = false;
        _linkedInvoice = _contextInvoice;
        _linkedRentalProfile = _contextRentalProfile;
        _linkedRentalBillingRunId = _contextRentalBillingRunId;
        _linkedRentalBilledAmount = _contextRentalBilledAmount;
        _linkedRentalPeriodLabel = _contextRentalPeriodLabel;
        IsCustomerSelectionLocked = _linkedInvoice is not null || _linkedRentalProfile is not null;
        ReceiptDate = PaymentDate = DateOnly.FromDateTime(DateTime.Today);
        CashReceipt = CardReceipt = BankReceipt = DiscountApplied = ReceiptTotal = 0m;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;
        SettlementAmount = 0m;
        Note = string.Empty;
        Memo = string.Empty;
        RebuildTransactionKinds(ResolveDefaultTransactionKind());
        RequestApplySuggestedAmounts(forceResetAmounts: false);
        ResetAttachmentEditor();
    }

    partial void OnCashReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnCardReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnBankReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnDiscountAppliedChanged(decimal value) => RecalcReceipt();

    partial void OnCashPaymentChanged(decimal value) => RecalcPayment();
    partial void OnCardPaymentChanged(decimal value) => RecalcPayment();
    partial void OnBankPaymentChanged(decimal value) => RecalcPayment();
    partial void OnDiscountReceivedChanged(decimal value) => RecalcPayment();

    partial void OnSelectedHistoryChanged(LocalTransaction? value)
    {
        RequestLoadAttachments(value?.Id ?? Guid.Empty);
        OnPropertyChanged(nameof(CanAddAttachment));
        OnPropertyChanged(nameof(CanEditHistory));
        OnPropertyChanged(nameof(CanDeleteHistory));
        AddAttachmentCommand.NotifyCanExecuteChanged();
        EditHistoryCommand.NotifyCanExecuteChanged();
        DeleteHistoryCommand.NotifyCanExecuteChanged();
        CancelHistoryEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTransactionKindChanged(string value)
    {
        if (_suppressTransactionKindChange)
            return;

        RequestApplySuggestedAmounts(forceResetAmounts: false);
        OnPropertyChanged(nameof(PaymentActionLabel));
        OnPropertyChanged(nameof(ReserveBalanceLabelText));
        OnPropertyChanged(nameof(IsSettlementAmountEnabled));
        RequestRefreshContext();
    }

    partial void OnSelectedCustomerChanged(LocalCustomer? value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RequestRefreshContext();
    }

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditHistory));
        OnPropertyChanged(nameof(CanDeleteHistory));
        OnPropertyChanged(nameof(CanCancelHistoryEdit));
        EditHistoryCommand.NotifyCanExecuteChanged();
        DeleteHistoryCommand.NotifyCanExecuteChanged();
        CancelHistoryEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAttachmentChanged(LocalTransactionAttachment? value)
    {
        DeleteAttachmentCommand.NotifyCanExecuteChanged();
        VerifyAttachmentCommand.NotifyCanExecuteChanged();
        RejectAttachmentCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCustomerSelectionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectCustomer));
    }

    partial void OnIsEditingHistoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonLabel));
        OnPropertyChanged(nameof(CanCancelHistoryEdit));
        EditHistoryCommand.NotifyCanExecuteChanged();
        DeleteHistoryCommand.NotifyCanExecuteChanged();
        CancelHistoryEditCommand.NotifyCanExecuteChanged();
    }

    private void RecalcReceipt()
    {
        ReceiptTotal = Math.Max(0m, CashReceipt + CardReceipt + BankReceipt - DiscountApplied);
        if (ShouldSyncSettlementAmountFromInput(isReceipt: true))
            SettlementAmount = ReceiptTotal;
    }

    private void RecalcPayment()
    {
        PaymentTotal = Math.Max(0m, CashPayment + CardPayment + BankPayment - DiscountReceived);
        if (ShouldSyncSettlementAmountFromInput(isReceipt: false))
            SettlementAmount = PaymentTotal;
    }

    private bool ShouldSyncSettlementAmountFromInput(bool isReceipt)
    {
        if (!IsSettlementAmountEnabled)
            return false;

        var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
        if (kind == PaymentFlowConstants.TransactionKindAdvanceApply)
            return false;

        if (_linkedInvoice is not null)
        {
            var isPaymentKind = UsesPrepaidReserve(kind);
            return isReceipt ? !isPaymentKind : isPaymentKind;
        }

        return isReceipt
            ? kind != PaymentFlowConstants.TransactionKindInvoicePayment
            : kind == PaymentFlowConstants.TransactionKindInvoicePayment;
    }

    private async Task LoadHistoryAsync(Guid customerId, int version)
    {
        var currentSelectedId = SelectedHistory?.Id;
        var list = await _local.GetTransactionsAsync(customerId, _session);
        if (!IsCurrentHistoryLoad(version))
            return;

        History.Clear();
        foreach (var transaction in list
                     .OrderByDescending(current => current.TransactionDate)
                     .ThenByDescending(current => current.UpdatedAtUtc))
        {
            History.Add(transaction);
        }

        SelectedHistory = currentSelectedId.HasValue
            ? History.FirstOrDefault(current => current.Id == currentSelectedId.Value)
            : null;

        if (SelectedHistory is null)
            RequestLoadAttachments(Guid.Empty);
    }

    private async Task LoadAttachmentsAsync(Guid transactionId, int version)
    {
        if (transactionId == Guid.Empty)
        {
            if (!IsCurrentAttachmentLoad(version))
                return;

            Attachments.Clear();
            SelectedAttachment = null;
            return;
        }

        var items = await _local.GetTransactionAttachmentsAsync(transactionId, _session);
        if (!IsCurrentAttachmentLoad(version))
            return;

        Attachments.Clear();
        SelectedAttachment = null;
        foreach (var attachment in items)
            Attachments.Add(attachment);
    }

    private void ResetCustomerDisplay()
    {
        SelectedCustomer = null;
        CustomerName = "거래처 선택";
        CustomerPhone = "-";
        CustomerCategory = "-";
        CustomerDepartment = "-";
        CustomerContactPerson = "-";
        AdvanceBalance = 0m;
        TransactionContextSummary = "거래처를 선택하면 거래 맥락이 표시됩니다.";
        TransactionSummary = "수금/지급 요약이 없습니다.";
        History.Clear();
        Attachments.Clear();
        SelectedHistory = null;
        SelectedAttachment = null;
        InvalidateAsyncUiRequests();
    }

    private void ResetAttachmentEditor()
    {
        SelectedAttachmentType = AttachmentTypes[0];
        AttachmentDescription = string.Empty;
        SelectedAttachment = null;
    }

    private void RebuildTransactionKinds(string? preferredKind = null)
    {
        var normalizedPreferred = PaymentFlowConstants.NormalizeTransactionKind(
            preferredKind ?? SelectedTransactionKind,
            preferPayment: _linkedInvoice?.VoucherType is VoucherType.Purchase or VoucherType.Procurement);

        _suppressTransactionKindChange = true;
        try
        {
            TransactionKinds.Clear();

            if (_linkedRentalProfile is not null)
            {
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindRentalReceipt, "렌탈수금"));
            }
            else if (_linkedInvoice is not null)
            {
                if (_linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
                {
                    TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindPayment, "일반지급"));
                    TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindInvoicePayment, "전표지급"));
                }
                else
                {
                    TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindReceipt, "일반수금"));
                    TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindInvoiceReceipt, "전표수금"));
                    TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceApply, "선수금차감"));
                }
            }
            else
            {
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindReceipt, PaymentFlowConstants.GetTransactionKindDisplayName(PaymentFlowConstants.TransactionKindReceipt)));
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindPayment, PaymentFlowConstants.GetTransactionKindDisplayName(PaymentFlowConstants.TransactionKindPayment)));
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceDeposit, PaymentFlowConstants.GetTransactionKindDisplayName(PaymentFlowConstants.TransactionKindAdvanceDeposit)));
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceRefund, PaymentFlowConstants.GetTransactionKindDisplayName(PaymentFlowConstants.TransactionKindAdvanceRefund)));
            }

            if (TransactionKinds.Count == 0)
                return;

            var nextValue = TransactionKinds.Any(option => option.Value == normalizedPreferred)
                ? normalizedPreferred
                : TransactionKinds[0].Value;

            if (!string.Equals(SelectedTransactionKind, nextValue, StringComparison.Ordinal))
                SelectedTransactionKind = nextValue;
        }
        finally
        {
            _suppressTransactionKindChange = false;
        }
    }

    private string ResolveDefaultTransactionKind()
    {
        if (_linkedRentalProfile is not null)
            return PaymentFlowConstants.TransactionKindRentalReceipt;

        if (_linkedInvoice is not null)
        {
            return _linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement
                ? PaymentFlowConstants.TransactionKindPayment
                : PaymentFlowConstants.TransactionKindReceipt;
        }

        return PaymentFlowConstants.TransactionKindReceipt;
    }

    private string GetTransactionKindLabel(string? kind)
    {
        return TransactionKinds.FirstOrDefault(option =>
                   string.Equals(option.Value, kind, StringComparison.OrdinalIgnoreCase))?.Label
               ?? PaymentFlowConstants.GetTransactionKindDisplayName(kind)
               ?? "거래구분";
    }

    private string GetSettlementDirectionLabel(string kind)
    {
        return kind switch
        {
            PaymentFlowConstants.TransactionKindPayment or PaymentFlowConstants.TransactionKindInvoicePayment => "지급",
            _ => "수금"
        };
    }

    private bool UsesPrepaidReserve(string? kind = null)
    {
        if (_linkedInvoice?.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            return true;

        var normalized = PaymentFlowConstants.NormalizeTransactionKind(kind ?? SelectedTransactionKind);
        return normalized is PaymentFlowConstants.TransactionKindPayment or PaymentFlowConstants.TransactionKindInvoicePayment;
    }

    private string GetReserveLabel(string? kind = null)
        => UsesPrepaidReserve(kind) ? "선지급금" : "선수금";

    private void RequestApplySuggestedAmounts(bool forceResetAmounts)
    {
        var version = Interlocked.Increment(ref _settlementSuggestionVersion);
        UiTaskHelper.Forget(
            ApplySuggestedAmountsCoreAsync(forceResetAmounts, version),
            "PAYMENT",
            "수금/지급 기본 정산금액 계산",
            ex => StatusMessage = $"정산금액 계산 중 오류가 발생했습니다. {ex.Message}");
    }

    private async Task ApplySuggestedAmountsCoreAsync(bool forceResetAmounts, int version)
    {
        var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
        if (kind == PaymentFlowConstants.TransactionKindAdvanceApply)
        {
            if (forceResetAmounts)
            {
                CashReceipt = CardReceipt = BankReceipt = DiscountApplied = ReceiptTotal = 0m;
                CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;
            }

            if (_linkedInvoice is not null && SelectedCustomer is not null)
                await ApplyInvoiceDefaultSettlementAsync(forceResetAmounts: forceResetAmounts, advanceOnly: true, version);

            return;
        }

        if (_linkedInvoice is not null && SelectedCustomer is not null)
        {
            await ApplyInvoiceDefaultSettlementAsync(forceResetAmounts, advanceOnly: false, version);
            return;
        }

        if (_linkedRentalProfile is not null && SelectedCustomer is not null)
        {
            await ApplyRentalDefaultSettlementAsync(forceResetAmounts, version);
            return;
        }

        if (forceResetAmounts && IsCurrentSettlementSuggestion(version))
            SettlementAmount = 0m;
    }

    private async Task ApplyInvoiceDefaultSettlementAsync(bool forceResetAmounts, bool advanceOnly, int version)
    {
        if (_linkedInvoice is null)
            return;

        var summary = await GetInvoiceSettlementSummaryAsync(_linkedInvoice.Id);
        if (!IsCurrentSettlementSuggestion(version))
            return;

        var suggested = summary.RemainingAmount;
        if (advanceOnly)
            suggested = Math.Min(suggested, AdvanceBalance);

        SettlementAmount = Math.Max(0m, suggested);
        if (!forceResetAmounts)
            return;

        CashReceipt = CardReceipt = BankReceipt = DiscountApplied = ReceiptTotal = 0m;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;

        if (advanceOnly || SettlementAmount <= 0m)
            return;

        if (_linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            BankPayment = SettlementAmount;
        else
            BankReceipt = SettlementAmount;
    }

    private async Task ApplyRentalDefaultSettlementAsync(bool forceResetAmounts, int version)
    {
        if (_linkedRentalProfile is null)
            return;

        var summary = await GetRentalSettlementSummaryAsync(_linkedRentalProfile.Id, _linkedRentalBillingRunId, _linkedRentalBilledAmount);
        if (!IsCurrentSettlementSuggestion(version))
            return;

        SettlementAmount = Math.Max(0m, summary.OutstandingAmount > 0m ? summary.OutstandingAmount : summary.BilledAmount);
        if (!forceResetAmounts)
            return;

        CashReceipt = CardReceipt = DiscountApplied = ReceiptTotal = 0m;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;
        BankReceipt = SettlementAmount;
    }

    private void RequestRefreshContext()
    {
        var version = Interlocked.Increment(ref _contextRefreshVersion);
        UiTaskHelper.Forget(
            RefreshContextCoreAsync(version),
            "PAYMENT",
            "수금/지급 맥락 갱신",
            ex =>
            {
                if (IsCurrentContextRefresh(version))
                    StatusMessage = $"거래 맥락을 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task RefreshContextCoreAsync(int version)
    {
        var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
        if (SelectedCustomer is null)
        {
            if (!IsCurrentContextRefresh(version))
                return;

            AdvanceBalance = 0m;
            TransactionContextSummary = "거래처를 선택하면 거래 맥락이 표시됩니다.";
            TransactionSummary = "수금/지급 요약이 없습니다.";
            OnPropertyChanged(nameof(PaymentActionLabel));
            return;
        }

        var financialSummary = await _local.GetCustomerFinancialSummaryAsync(SelectedCustomer.Id, _session);
        if (!IsCurrentContextRefresh(version))
            return;

        AdvanceBalance = UsesPrepaidReserve(kind)
            ? financialSummary.PrepaidAmount
            : financialSummary.AdvanceBalance;

        if (_linkedInvoice is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_linkedInvoice.Id, _session) ?? _linkedInvoice;
            if (!IsCurrentContextRefresh(version))
                return;
            _linkedInvoice = invoice;
            var summary = await GetInvoiceSettlementSummaryAsync(invoice.Id);
            if (!IsCurrentContextRefresh(version))
                return;
            var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber;
            TransactionContextSummary = $"{displayNumber} · {invoice.InvoiceDate:yyyy-MM-dd} · 전표금액 {summary.InvoiceTotal:N0}";
            if (kind == PaymentFlowConstants.TransactionKindAdvanceApply)
            {
                TransactionSummary = $"{GetReserveLabel(kind)} 잔액 {AdvanceBalance:N0} / 차감가능 {Math.Min(AdvanceBalance, summary.RemainingAmount):N0} / 전표잔액 {summary.RemainingAmount:N0}";
            }
            else
            {
                TransactionSummary = $"{GetSettlementDirectionLabel(kind)}누계 {summary.SettledAmount:N0} / 잔액 {summary.RemainingAmount:N0}";
            }
        }
        else if (_linkedRentalProfile is not null)
        {
            var summary = await _local.GetRentalSettlementSummaryAsync(
                _linkedRentalProfile.Id,
                _linkedRentalBillingRunId,
                _linkedRentalBilledAmount > 0m ? _linkedRentalBilledAmount : null,
                _session);
            if (!IsCurrentContextRefresh(version))
                return;
            var customerName = string.IsNullOrWhiteSpace(_linkedRentalProfile.CustomerName)
                ? SelectedCustomer.NameOriginal
                : _linkedRentalProfile.CustomerName;
            var periodLabel = string.IsNullOrWhiteSpace(_linkedRentalPeriodLabel) ? "현재 회차" : _linkedRentalPeriodLabel;
            TransactionContextSummary = $"{customerName} · 렌탈청구 {summary.BilledAmount:N0} · {periodLabel}";
            TransactionSummary = $"수금누계 {summary.SettledAmount:N0} / 미수 {summary.OutstandingAmount:N0} · {summary.BillingStatus} / {summary.SettlementStatus}";
        }
        else if (PaymentFlowConstants.IsAdvanceKind(kind))
        {
            TransactionContextSummary = $"{SelectedCustomer.NameOriginal} · {PaymentActionLabel}";
            TransactionSummary = $"{GetReserveLabel(kind)} 잔액 {AdvanceBalance:N0}";
        }
        else
        {
            TransactionContextSummary = $"{SelectedCustomer.NameOriginal} · 일반 {GetSettlementDirectionLabel(kind)}";
            TransactionSummary = $"{GetReserveLabel(kind)} 잔액 {AdvanceBalance:N0}";
        }

        OnPropertyChanged(nameof(PaymentActionLabel));
    }

    private void RequestLoadHistory(Guid customerId)
    {
        var version = Interlocked.Increment(ref _historyLoadVersion);
        UiTaskHelper.Forget(
            LoadHistoryAsync(customerId, version),
            "PAYMENT",
            "수금/지급 이력 조회",
            ex =>
            {
                if (IsCurrentHistoryLoad(version))
                    StatusMessage = $"최근 처리내역을 불러오지 못했습니다. {ex.Message}";
            });
    }

    private void RequestLoadAttachments(Guid transactionId)
    {
        var version = Interlocked.Increment(ref _attachmentLoadVersion);
        UiTaskHelper.Forget(
            LoadAttachmentsAsync(transactionId, version),
            "PAYMENT",
            "수금/지급 증빙 조회",
            ex =>
            {
                if (IsCurrentAttachmentLoad(version))
                    StatusMessage = $"증빙 목록을 불러오지 못했습니다. {ex.Message}";
            });
    }

    private void InvalidateAsyncUiRequests()
    {
        Interlocked.Increment(ref _historyLoadVersion);
        Interlocked.Increment(ref _attachmentLoadVersion);
        Interlocked.Increment(ref _contextRefreshVersion);
        Interlocked.Increment(ref _settlementSuggestionVersion);
    }

    private bool IsCurrentHistoryLoad(int version) => version == Volatile.Read(ref _historyLoadVersion);
    private bool IsCurrentAttachmentLoad(int version) => version == Volatile.Read(ref _attachmentLoadVersion);
    private bool IsCurrentContextRefresh(int version) => version == Volatile.Read(ref _contextRefreshVersion);
    private bool IsCurrentSettlementSuggestion(int version) => version == Volatile.Read(ref _settlementSuggestionVersion);

    private async Task<InvoiceSettlementSummary> GetInvoiceSettlementSummaryAsync(Guid invoiceId)
    {
        var invoice = await _local.GetInvoiceAsync(invoiceId, _session) ?? _linkedInvoice;
        if (invoice is null)
            return new InvoiceSettlementSummary();

        try
        {
            return await _local.GetInvoiceSettlementSummaryAsync(invoiceId, _session);
        }
        catch (NotSupportedException)
        {
            var settledAmount = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
            return new InvoiceSettlementSummary
            {
                InvoiceTotal = invoice.TotalAmount,
                SettledAmount = settledAmount,
                RemainingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount)
            };
        }
    }

    private async Task<RentalSettlementSummary> GetRentalSettlementSummaryAsync(Guid billingProfileId, Guid? billingRunId, decimal billedAmount)
    {
        try
        {
            return await _local.GetRentalSettlementSummaryAsync(
                billingProfileId,
                billingRunId,
                billedAmount > 0m ? billedAmount : null,
                _session);
        }
        catch (NotSupportedException)
        {
            var profile = _linkedRentalProfile;
            if (profile is null)
                return new RentalSettlementSummary();

            var transactions = await _local.GetTransactionsAsync(SelectedCustomer?.Id ?? Guid.Empty, _session);
            var settledAmount = transactions
                .Where(transaction =>
                    !transaction.IsDeleted &&
                    transaction.LinkedRentalBillingProfileId == billingProfileId &&
                    (!billingRunId.HasValue || transaction.LinkedRentalBillingRunId == billingRunId.Value))
                .Sum(transaction => transaction.SettlementAmount);
            var effectiveBilledAmount = billedAmount > 0m ? billedAmount : profile.MonthlyAmount;
            return new RentalSettlementSummary
            {
                BilledAmount = effectiveBilledAmount,
                SettledAmount = settledAmount,
                OutstandingAmount = Math.Max(0m, effectiveBilledAmount - settledAmount),
                BillingStatus = string.IsNullOrWhiteSpace(profile.BillingStatus)
                    ? PaymentFlowConstants.BillingStatusInProgress
                    : profile.BillingStatus,
                SettlementStatus = string.IsNullOrWhiteSpace(profile.SettlementStatus)
                    ? (settledAmount <= 0m ? PaymentFlowConstants.SettlementStatusUnpaid : PaymentFlowConstants.SettlementStatusPartial)
                    : profile.SettlementStatus,
                CompletionStatus = Math.Max(0m, effectiveBilledAmount - settledAmount) <= 0m
                    ? PaymentFlowConstants.CompletionDone
                    : PaymentFlowConstants.CompletionPending
            };
        }
    }

    private bool CanSave() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (IsSaving)
            return;

        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            return;
        }

        try
        {
            IsSaving = true;

            var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
            var transaction = new LocalTransaction
            {
                Id = _editingTransactionId ?? Guid.NewGuid(),
                Revision = _editingTransactionRevision,
                CustomerId = SelectedCustomer.Id,
                TransactionKind = kind,
                LinkedInvoiceId = _linkedInvoice?.Id,
                LinkedRentalBillingProfileId = _linkedRentalProfile?.Id,
                LinkedRentalBillingRunId = _linkedRentalBillingRunId,
                LinkedInvoiceNumber = _linkedInvoice is null
                    ? string.Empty
                    : string.IsNullOrWhiteSpace(_linkedInvoice.InvoiceNumber)
                        ? _linkedInvoice.LocalTempNumber
                        : _linkedInvoice.InvoiceNumber,
                Note = (Note ?? string.Empty).Trim(),
                Memo = (Memo ?? string.Empty).Trim()
            };

            switch (kind)
            {
                case var current when current == PaymentFlowConstants.TransactionKindAdvanceDeposit:
                    if (ReceiptTotal <= 0m || PaymentTotal > 0m)
                    {
                        StatusMessage = "선수금입금은 수금 금액만 입력해야 합니다.";
                        return;
                    }

                    transaction.TransactionDate = ReceiptDate;
                    transaction.CashReceipt = CashReceipt;
                    transaction.CardReceipt = CardReceipt;
                    transaction.BankReceipt = BankReceipt;
                    transaction.DiscountApplied = DiscountApplied;
                    transaction.ReceiptTotal = ReceiptTotal;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindAdvanceRefund:
                    if (PaymentTotal <= 0m || ReceiptTotal > 0m)
                    {
                        StatusMessage = $"{PaymentFlowConstants.GetTransactionKindDisplayName(PaymentFlowConstants.TransactionKindAdvanceRefund)}은 지급 금액만 입력해야 합니다.";
                        return;
                    }

                    transaction.TransactionDate = PaymentDate;
                    transaction.CashPayment = CashPayment;
                    transaction.CardPayment = CardPayment;
                    transaction.BankPayment = BankPayment;
                    transaction.DiscountReceived = DiscountReceived;
                    transaction.PaymentTotal = PaymentTotal;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindAdvanceApply:
                    if (_linkedInvoice is null)
                    {
                        StatusMessage = "선수금차감은 연결 전표에서만 처리할 수 있습니다.";
                        return;
                    }

                    if (SettlementAmount <= 0m)
                    {
                        StatusMessage = "선수금차감 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = ReceiptDate;
                    transaction.SettlementAmount = SettlementAmount;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindInvoiceReceipt:
                    if (_linkedInvoice is null)
                    {
                        StatusMessage = "전표수금은 연결 전표가 필요합니다.";
                        return;
                    }

                    if (ReceiptTotal <= 0m && SettlementAmount > 0m)
                    {
                        BankReceipt = SettlementAmount;
                        RecalcReceipt();
                    }

                    if (ReceiptTotal <= 0m || PaymentTotal > 0m)
                    {
                        StatusMessage = "전표수금 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = ReceiptDate;
                    transaction.CashReceipt = CashReceipt;
                    transaction.CardReceipt = CardReceipt;
                    transaction.BankReceipt = BankReceipt;
                    transaction.DiscountApplied = DiscountApplied;
                    transaction.ReceiptTotal = ReceiptTotal;
                    transaction.SettlementAmount = SettlementAmount > 0m ? Math.Min(SettlementAmount, ReceiptTotal) : ReceiptTotal;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindInvoicePayment:
                    if (_linkedInvoice is null)
                    {
                        StatusMessage = "전표지급은 연결 전표가 필요합니다.";
                        return;
                    }

                    if (PaymentTotal <= 0m && SettlementAmount > 0m)
                    {
                        BankPayment = SettlementAmount;
                        RecalcPayment();
                    }

                    if (PaymentTotal <= 0m || ReceiptTotal > 0m)
                    {
                        StatusMessage = "전표지급 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = PaymentDate;
                    transaction.CashPayment = CashPayment;
                    transaction.CardPayment = CardPayment;
                    transaction.BankPayment = BankPayment;
                    transaction.DiscountReceived = DiscountReceived;
                    transaction.PaymentTotal = PaymentTotal;
                    transaction.SettlementAmount = SettlementAmount > 0m ? Math.Min(SettlementAmount, PaymentTotal) : PaymentTotal;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindRentalReceipt:
                    if (_linkedRentalProfile is null)
                    {
                        StatusMessage = "렌탈수금은 연결 청구건이 필요합니다.";
                        return;
                    }

                    if (ReceiptTotal <= 0m && SettlementAmount > 0m)
                    {
                        BankReceipt = SettlementAmount;
                        RecalcReceipt();
                    }

                    if (ReceiptTotal <= 0m || PaymentTotal > 0m)
                    {
                        StatusMessage = "렌탈 수금 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = ReceiptDate;
                    transaction.CashReceipt = CashReceipt;
                    transaction.CardReceipt = CardReceipt;
                    transaction.BankReceipt = BankReceipt;
                    transaction.DiscountApplied = DiscountApplied;
                    transaction.ReceiptTotal = ReceiptTotal;
                    transaction.SettlementAmount = SettlementAmount > 0m ? Math.Min(SettlementAmount, ReceiptTotal) : ReceiptTotal;
                    break;

                case var current when current == PaymentFlowConstants.TransactionKindPayment:
                    if (_linkedInvoice is not null && PaymentTotal <= 0m && SettlementAmount > 0m)
                    {
                        BankPayment = SettlementAmount;
                        RecalcPayment();
                    }

                    if (PaymentTotal <= 0m || ReceiptTotal > 0m)
                    {
                        StatusMessage = "일반지급 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = PaymentDate;
                    transaction.CashPayment = CashPayment;
                    transaction.CardPayment = CardPayment;
                    transaction.BankPayment = BankPayment;
                    transaction.DiscountReceived = DiscountReceived;
                    transaction.PaymentTotal = PaymentTotal;
                    transaction.SettlementAmount = _linkedInvoice is not null
                        ? (SettlementAmount > 0m ? Math.Min(SettlementAmount, PaymentTotal) : PaymentTotal)
                        : 0m;
                    break;

                default:
                    if (_linkedInvoice is not null && ReceiptTotal <= 0m && SettlementAmount > 0m)
                    {
                        BankReceipt = SettlementAmount;
                        RecalcReceipt();
                    }

                    if (ReceiptTotal <= 0m || PaymentTotal > 0m)
                    {
                        StatusMessage = "일반수금 금액을 입력하세요.";
                        return;
                    }

                    transaction.TransactionDate = ReceiptDate;
                    transaction.CashReceipt = CashReceipt;
                    transaction.CardReceipt = CardReceipt;
                    transaction.BankReceipt = BankReceipt;
                    transaction.DiscountApplied = DiscountApplied;
                    transaction.ReceiptTotal = ReceiptTotal;
                    transaction.SettlementAmount = _linkedInvoice is not null
                        ? (SettlementAmount > 0m ? Math.Min(SettlementAmount, ReceiptTotal) : ReceiptTotal)
                        : 0m;
                    break;
            }

            var result = await _local.SaveTransactionAsync(transaction, _session);
            if (!result.Success)
            {
                if (result.ConcurrencyConflict)
                {
                    StatusMessage = $"{result.Message} 최신 처리내역을 다시 불러왔습니다. 확인 후 다시 저장하세요.";
                    if (SelectedCustomer is not null)
                        await LoadHistoryAsync(SelectedCustomer.Id, Interlocked.Increment(ref _historyLoadVersion));

                    await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                else
                {
                    StatusMessage = result.Message;
                }

                return;
            }

            await LoadHistoryAsync(SelectedCustomer.Id, Interlocked.Increment(ref _historyLoadVersion));
            SelectedHistory = History.FirstOrDefault(current => current.Id == result.EntityId);
            NewEntry();
            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(result.Message, serverWriteResult);
            await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditHistory))]
    private async Task EditHistoryAsync()
    {
        if (SelectedHistory is null)
        {
            StatusMessage = "수정할 최근 처리내역을 선택하세요.";
            return;
        }

        await LoadHistoryIntoEditorAsync(SelectedHistory);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteHistory))]
    private async Task DeleteHistoryAsync()
    {
        if (SelectedHistory is null)
        {
            StatusMessage = "삭제할 최근 처리내역을 선택하세요.";
            return;
        }

        var target = SelectedHistory;
        var confirm = System.Windows.MessageBox.Show(
            $"선택한 처리내역 '{target.TransactionDate:yyyy-MM-dd} / {target.TransactionKindDisplay}'을(를) 삭제하시겠습니까?",
            "처리내역 삭제",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            IsSaving = true;

            var result = await _local.DeleteTransactionAsync(target.Id, _session, target.Revision);
            if (!result.Success)
            {
                if (result.ConcurrencyConflict)
                {
                    StatusMessage = $"{result.Message} 최신 처리내역을 다시 불러왔습니다.";
                    if (SelectedCustomer is not null)
                    {
                        await LoadHistoryAsync(SelectedCustomer.Id, Interlocked.Increment(ref _historyLoadVersion));
                        SelectedHistory = History.FirstOrDefault(current => current.Id == target.Id);
                    }

                    await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                else
                {
                    StatusMessage = result.Message;
                }

                return;
            }

            if (SelectedCustomer is not null)
            {
                await LoadHistoryAsync(SelectedCustomer.Id, Interlocked.Increment(ref _historyLoadVersion));
                SelectedHistory = null;
            }

            if (_editingTransactionId == target.Id)
                NewEntry();

            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(result.Message, serverWriteResult);
            await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelHistoryEdit))]
    private void CancelHistoryEdit()
    {
        NewEntry();
        StatusMessage = "최근 처리내역 수정이 취소되었습니다.";
    }

    private bool CanAddAttachmentAction() => !IsSaving && SelectedHistory is not null;

    [RelayCommand(CanExecute = nameof(CanAddAttachmentAction))]
    private async Task AddAttachmentAsync()
    {
        if (SelectedHistory is null)
        {
            StatusMessage = "증빙을 첨부할 수금/지급 내역을 먼저 선택하세요.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "수금/지급 증빙 첨부",
            Filter = "지원 파일|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|PDF 파일|*.pdf|이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|모든 파일|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        var result = await _local.SaveTransactionAttachmentAsync(
            SelectedHistory.Id,
            dialog.FileName,
            SelectedAttachmentType,
            AttachmentDescription,
            _session);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await LoadAttachmentsAsync(SelectedHistory.Id, Interlocked.Increment(ref _attachmentLoadVersion));
        SelectedAttachment = Attachments.FirstOrDefault(current => current.Id == result.EntityId);
        AttachmentDescription = string.Empty;
    }

    [RelayCommand]
    private void PreviewAttachment()
    {
        var attachment = SelectedAttachment;
        if (attachment is null || !File.Exists(attachment.StoredPath))
        {
            StatusMessage = "미리볼 증빙 파일을 찾을 수 없습니다.";
            return;
        }

        Process.Start(new ProcessStartInfo(attachment.StoredPath)
        {
            UseShellExecute = true
        });
        StatusMessage = "증빙 파일을 열었습니다.";
    }

    private bool CanDeleteAttachmentAction() => !IsSaving && CanDeleteAttachment;

    [RelayCommand(CanExecute = nameof(CanDeleteAttachmentAction))]
    private async Task DeleteAttachmentAsync()
    {
        if (SelectedAttachment is null)
        {
            StatusMessage = "삭제할 증빙을 선택하세요.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"선택한 증빙 '{SelectedAttachment.FileName}'을(를) 삭제하시겠습니까?",
            "증빙 삭제",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        var result = await _local.DeleteTransactionAttachmentAsync(SelectedAttachment.Id, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        if (SelectedHistory is not null)
            await LoadAttachmentsAsync(SelectedHistory.Id, Interlocked.Increment(ref _attachmentLoadVersion));
    }

    private bool CanVerifyAttachmentAction() => !IsSaving && CanVerifyAttachment;

    [RelayCommand(CanExecute = nameof(CanVerifyAttachmentAction))]
    private async Task VerifyAttachmentAsync()
    {
        if (SelectedAttachment is null)
        {
            StatusMessage = "확인할 증빙을 선택하세요.";
            return;
        }

        var selectedAttachmentId = SelectedAttachment.Id;

        var result = await _local.UpdateTransactionAttachmentVerificationAsync(
            selectedAttachmentId,
            "확인완료",
            AttachmentDescription,
            _session);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        if (SelectedHistory is not null)
        {
            await LoadAttachmentsAsync(SelectedHistory.Id, Interlocked.Increment(ref _attachmentLoadVersion));
            SelectedAttachment = Attachments.FirstOrDefault(current => current.Id == selectedAttachmentId);
        }
    }

    private bool CanRejectAttachmentAction() => !IsSaving && CanRejectAttachment;

    [RelayCommand(CanExecute = nameof(CanRejectAttachmentAction))]
    private async Task RejectAttachmentAsync()
    {
        if (SelectedAttachment is null)
        {
            StatusMessage = "반려할 증빙을 선택하세요.";
            return;
        }

        var selectedAttachmentId = SelectedAttachment.Id;

        var result = await _local.UpdateTransactionAttachmentVerificationAsync(
            selectedAttachmentId,
            "반려",
            AttachmentDescription,
            _session);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        if (SelectedHistory is not null)
        {
            await LoadAttachmentsAsync(SelectedHistory.Id, Interlocked.Increment(ref _attachmentLoadVersion));
            SelectedAttachment = Attachments.FirstOrDefault(current => current.Id == selectedAttachmentId);
        }
    }

    private async Task LoadHistoryIntoEditorAsync(LocalTransaction history)
    {
        if (history is null)
            return;

        var customer = _allCustomers.FirstOrDefault(current => current.Id == history.CustomerId)
            ?? await _local.GetCustomerForOperationalSelectionAsync(history.CustomerId, _session);
        if (customer is null)
        {
            StatusMessage = "연결된 거래처를 찾을 수 없어 처리내역을 수정할 수 없습니다.";
            return;
        }

        LocalInvoice? editInvoice = null;
        if (history.LinkedInvoiceId.HasValue && history.LinkedInvoiceId.Value != Guid.Empty)
        {
            editInvoice = await _local.GetInvoiceAsync(history.LinkedInvoiceId.Value, _session);
            if (editInvoice is null)
            {
                StatusMessage = "연결 전표를 찾을 수 없어 이 처리내역은 수정할 수 없습니다.";
                return;
            }
        }

        LocalRentalBillingProfile? editRentalProfile = null;
        if (history.LinkedRentalBillingProfileId.HasValue && history.LinkedRentalBillingProfileId.Value != Guid.Empty)
        {
            editRentalProfile = await _local.GetRentalBillingProfileAsync(history.LinkedRentalBillingProfileId.Value, _session);
            if (editRentalProfile is null)
            {
                StatusMessage = "연결 렌탈 청구건을 찾을 수 없어 이 처리내역은 수정할 수 없습니다.";
                return;
            }
        }

        if (SelectedCustomer?.Id != customer.Id)
            SetCustomer(customer);

        _editingTransactionId = history.Id;
        _editingTransactionRevision = history.Revision;
        _linkedInvoice = editInvoice;
        _linkedRentalProfile = editRentalProfile;
        _linkedRentalBillingRunId = history.LinkedRentalBillingRunId;
        _linkedRentalBilledAmount = 0m;
        _linkedRentalPeriodLabel = string.Empty;
        IsCustomerSelectionLocked = editInvoice is not null || editRentalProfile is not null || _contextInvoice is not null || _contextRentalProfile is not null;

        var normalizedKind = PaymentFlowConstants.NormalizeTransactionKind(
            history.TransactionKind,
            preferPayment: history.PaymentTotal > 0m && history.ReceiptTotal <= 0m);

        _suppressTransactionKindChange = true;
        try
        {
            RebuildTransactionKinds(normalizedKind);
            SelectedTransactionKind = normalizedKind;
            ReceiptDate = history.TransactionDate;
            PaymentDate = history.TransactionDate;
            CashReceipt = history.CashReceipt;
            CardReceipt = history.CardReceipt;
            BankReceipt = history.BankReceipt;
            DiscountApplied = history.DiscountApplied;
            CashPayment = history.CashPayment;
            CardPayment = history.CardPayment;
            BankPayment = history.BankPayment;
            DiscountReceived = history.DiscountReceived;
            SettlementAmount = history.SettlementAmount;
            Note = history.Note;
            Memo = history.Memo;
        }
        finally
        {
            _suppressTransactionKindChange = false;
        }

        IsEditingHistory = true;
        ResetAttachmentEditor();
        OnPropertyChanged(nameof(PaymentActionLabel));
        OnPropertyChanged(nameof(ReserveBalanceLabelText));
        OnPropertyChanged(nameof(IsSettlementAmountEnabled));
        await RefreshContextCoreAsync(Interlocked.Increment(ref _contextRefreshVersion));
        StatusMessage = "최근 처리내역 수정 모드입니다.";
    }
}

