using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class PaymentViewModel : ObservableObject
{
    public sealed record TransactionKindOption(string Value, string Label);

    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private List<LocalCustomer> _allCustomers = new();
    private LocalInvoice? _linkedInvoice;
    private LocalRentalBillingProfile? _linkedRentalProfile;

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
    [NotifyPropertyChangedFor(nameof(CanAddAttachment))]
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
    public bool CanSelectCustomer => !IsCustomerSelectionLocked;
    public bool IsSettlementAmountEnabled =>
        PaymentFlowConstants.IsInvoiceSettlementKind(SelectedTransactionKind) ||
        PaymentFlowConstants.IsRentalSettlementKind(SelectedTransactionKind);

    public bool CanAddAttachment => SelectedHistory is not null;
    public bool CanPreviewAttachment => SelectedAttachment is not null && File.Exists(SelectedAttachment.StoredPath);
    public bool CanDeleteAttachment =>
        SelectedAttachment is not null &&
        (_session.IsAdmin || !string.Equals(SelectedAttachment.VerificationStatus, "확인완료", StringComparison.OrdinalIgnoreCase));
    public bool CanVerifyAttachment => _session.IsAdmin && SelectedAttachment is not null;
    public bool CanRejectAttachment => _session.IsAdmin && SelectedAttachment is not null;
    public bool IsAdmin => _session.IsAdmin;

    public PaymentViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        RefreshTransactionKinds();
    }

    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;

    public async Task LoadAsync(LocalCustomer? preselect = null)
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        if (preselect is not null)
            SetCustomer(preselect);
        else
            ResetCustomerDisplay();

        NewEntry();
        await RefreshContextAsync();
    }

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
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
        _ = LoadHistoryAsync(customer.Id);
        _ = RefreshContextAsync();
    }

    public async Task ConfigureForInvoiceAsync(LocalInvoice invoice)
    {
        _linkedInvoice = invoice;
        _linkedRentalProfile = null;
        IsCustomerSelectionLocked = true;

        var customer = _allCustomers.FirstOrDefault(current => current.Id == invoice.CustomerId)
            ?? await _local.GetCustomerAsync(invoice.CustomerId, _session);
        if (customer is not null)
            SetCustomer(customer);

        SelectedTransactionKind = invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement
            ? PaymentFlowConstants.TransactionKindInvoicePayment
            : PaymentFlowConstants.TransactionKindInvoiceReceipt;

        RefreshTransactionKinds();
        await RefreshContextAsync();
        ApplySuggestedAmounts(forceResetAmounts: true);
        Memo = invoice.Memo;
    }

    public async Task ConfigureForRentalBillingAsync(LocalRentalBillingProfile profile, LocalCustomer? customer = null)
    {
        _linkedInvoice = null;
        _linkedRentalProfile = profile;
        IsCustomerSelectionLocked = true;

        customer ??= profile.CustomerId.HasValue
            ? _allCustomers.FirstOrDefault(current => current.Id == profile.CustomerId.Value)
                ?? await _local.GetCustomerAsync(profile.CustomerId.Value, _session)
            : _allCustomers.FirstOrDefault(current => string.Equals(current.NameOriginal, profile.CustomerName, StringComparison.OrdinalIgnoreCase));
        if (customer is not null)
            SetCustomer(customer);

        SelectedTransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
        RefreshTransactionKinds();
        await RefreshContextAsync();
        ApplySuggestedAmounts(forceResetAmounts: true);
        Memo = profile.FollowUpNote ?? string.Empty;
    }

    public Task ConfigureForRentalAsync(LocalRentalBillingProfile profile)
        => ConfigureForRentalBillingAsync(profile);

    public void NewEntry()
    {
        ReceiptDate = PaymentDate = DateOnly.FromDateTime(DateTime.Today);
        CashReceipt = CardReceipt = BankReceipt = DiscountApplied = ReceiptTotal = 0m;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;
        SettlementAmount = 0m;
        Note = string.Empty;
        Memo = string.Empty;
        SelectedTransactionKind = ResolveDefaultTransactionKind();
        RefreshTransactionKinds();
        ApplySuggestedAmounts(forceResetAmounts: false);
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
        _ = LoadAttachmentsAsync(value?.Id ?? Guid.Empty);
        OnPropertyChanged(nameof(CanAddAttachment));
    }

    partial void OnSelectedTransactionKindChanged(string value)
    {
        RefreshTransactionKinds();
        ApplySuggestedAmounts(forceResetAmounts: false);
        OnPropertyChanged(nameof(PaymentActionLabel));
        OnPropertyChanged(nameof(IsSettlementAmountEnabled));
        _ = RefreshContextAsync();
    }

    partial void OnSelectedCustomerChanged(LocalCustomer? value)
    {
        _ = RefreshContextAsync();
    }

    partial void OnIsCustomerSelectionLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectCustomer));
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

        return isReceipt
            ? kind != PaymentFlowConstants.TransactionKindInvoicePayment
            : kind == PaymentFlowConstants.TransactionKindInvoicePayment;
    }

    private async Task LoadHistoryAsync(Guid customerId)
    {
        var currentSelectedId = SelectedHistory?.Id;
        var list = await _local.GetTransactionsAsync(customerId, _session);

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
            await LoadAttachmentsAsync(Guid.Empty);
    }

    private async Task LoadAttachmentsAsync(Guid transactionId)
    {
        Attachments.Clear();
        SelectedAttachment = null;

        if (transactionId == Guid.Empty)
            return;

        var items = await _local.GetTransactionAttachmentsAsync(transactionId, _session);
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
    }

    private void ResetAttachmentEditor()
    {
        SelectedAttachmentType = AttachmentTypes[0];
        AttachmentDescription = string.Empty;
        SelectedAttachment = null;
    }

    private void RefreshTransactionKinds()
    {
        var previous = SelectedTransactionKind;
        TransactionKinds.Clear();

        if (_linkedRentalProfile is not null)
        {
            TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindRentalReceipt, "렌탈수금"));
        }
        else if (_linkedInvoice is not null)
        {
            if (_linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            {
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindInvoicePayment, "전표지급"));
            }
            else
            {
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindInvoiceReceipt, "전표수금"));
                TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceApply, "선수금차감"));
            }
        }
        else
        {
            TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindReceipt, "일반수금"));
            TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindPayment, "일반지급"));
            TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceDeposit, "선수금입금"));
            TransactionKinds.Add(new(PaymentFlowConstants.TransactionKindAdvanceRefund, "선수금환불"));
        }

        if (TransactionKinds.Count == 0)
            return;

        if (TransactionKinds.Any(option => option.Value == previous))
        {
            if (!string.Equals(SelectedTransactionKind, previous, StringComparison.Ordinal))
                SelectedTransactionKind = previous;
        }
        else
        {
            SelectedTransactionKind = TransactionKinds[0].Value;
        }
    }

    private string ResolveDefaultTransactionKind()
    {
        if (_linkedRentalProfile is not null)
            return PaymentFlowConstants.TransactionKindRentalReceipt;

        if (_linkedInvoice is not null)
        {
            return _linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement
                ? PaymentFlowConstants.TransactionKindInvoicePayment
                : PaymentFlowConstants.TransactionKindInvoiceReceipt;
        }

        return PaymentFlowConstants.TransactionKindReceipt;
    }

    private string GetTransactionKindLabel(string? kind)
    {
        return TransactionKinds.FirstOrDefault(option =>
                   string.Equals(option.Value, kind, StringComparison.OrdinalIgnoreCase))?.Label
               ?? kind
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

    private void ApplySuggestedAmounts(bool forceResetAmounts)
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
            {
                _ = ApplyInvoiceDefaultSettlementAsync(forceResetAmounts: forceResetAmounts, advanceOnly: true);
            }

            return;
        }

        if (_linkedInvoice is not null && SelectedCustomer is not null)
        {
            _ = ApplyInvoiceDefaultSettlementAsync(forceResetAmounts, advanceOnly: false);
            return;
        }

        if (_linkedRentalProfile is not null && SelectedCustomer is not null)
        {
            _ = ApplyRentalDefaultSettlementAsync(forceResetAmounts);
            return;
        }

        if (forceResetAmounts)
            SettlementAmount = 0m;
    }

    private async Task ApplyInvoiceDefaultSettlementAsync(bool forceResetAmounts, bool advanceOnly)
    {
        if (_linkedInvoice is null)
            return;

        var summary = await GetInvoiceSettlementSummaryAsync(_linkedInvoice.Id);
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

    private async Task ApplyRentalDefaultSettlementAsync(bool forceResetAmounts)
    {
        if (_linkedRentalProfile is null)
            return;

        var summary = await GetRentalSettlementSummaryAsync(_linkedRentalProfile.Id);
        SettlementAmount = Math.Max(0m, summary.OutstandingAmount > 0m ? summary.OutstandingAmount : summary.BilledAmount);
        if (!forceResetAmounts)
            return;

        CashReceipt = CardReceipt = DiscountApplied = ReceiptTotal = 0m;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0m;
        BankReceipt = SettlementAmount;
    }

    private async Task RefreshContextAsync()
    {
        var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
        if (SelectedCustomer is null)
        {
            AdvanceBalance = 0m;
            TransactionContextSummary = "거래처를 선택하면 거래 맥락이 표시됩니다.";
            TransactionSummary = "수금/지급 요약이 없습니다.";
            OnPropertyChanged(nameof(PaymentActionLabel));
            return;
        }

        AdvanceBalance = await GetAdvanceBalanceAsync(SelectedCustomer.Id);

        if (_linkedInvoice is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_linkedInvoice.Id, _session) ?? _linkedInvoice;
            _linkedInvoice = invoice;
            var summary = await GetInvoiceSettlementSummaryAsync(invoice.Id);
            var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber;
            TransactionContextSummary = $"{displayNumber} · {invoice.InvoiceDate:yyyy-MM-dd} · 전표금액 {summary.InvoiceTotal:N0}";
            if (kind == PaymentFlowConstants.TransactionKindAdvanceApply)
            {
                TransactionSummary = $"선수금 잔액 {AdvanceBalance:N0} / 차감가능 {Math.Min(AdvanceBalance, summary.RemainingAmount):N0} / 전표잔액 {summary.RemainingAmount:N0}";
            }
            else
            {
                TransactionSummary = $"{GetSettlementDirectionLabel(kind)}누계 {summary.SettledAmount:N0} / 잔액 {summary.RemainingAmount:N0}";
            }
        }
        else if (_linkedRentalProfile is not null)
        {
            var summary = await _local.GetRentalSettlementSummaryAsync(_linkedRentalProfile.Id, _session);
            var customerName = string.IsNullOrWhiteSpace(_linkedRentalProfile.CustomerName)
                ? SelectedCustomer.NameOriginal
                : _linkedRentalProfile.CustomerName;
            TransactionContextSummary = $"{customerName} · 렌탈청구 {summary.BilledAmount:N0}";
            TransactionSummary = $"수금누계 {summary.SettledAmount:N0} / 미수 {summary.OutstandingAmount:N0} · {summary.BillingStatus} / {summary.SettlementStatus}";
        }
        else if (PaymentFlowConstants.IsAdvanceKind(kind))
        {
            TransactionContextSummary = $"{SelectedCustomer.NameOriginal} · {PaymentActionLabel}";
            TransactionSummary = $"선수금 잔액 {AdvanceBalance:N0}";
        }
        else
        {
            TransactionContextSummary = $"{SelectedCustomer.NameOriginal} · 일반 {GetSettlementDirectionLabel(kind)}";
            TransactionSummary = $"선수금 잔액 {AdvanceBalance:N0}";
        }

        OnPropertyChanged(nameof(PaymentActionLabel));
    }

    private async Task<decimal> GetAdvanceBalanceAsync(Guid customerId)
    {
        try
        {
            return await _local.GetAdvanceBalanceAsync(customerId, _session);
        }
        catch (NotSupportedException)
        {
            var transactions = await _local.GetTransactionsAsync(customerId, _session);
            return transactions.Where(transaction => !transaction.IsDeleted).Sum(transaction => transaction.AdvanceDelta);
        }
    }

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

    private async Task<RentalSettlementSummary> GetRentalSettlementSummaryAsync(Guid billingProfileId)
    {
        try
        {
            return await _local.GetRentalSettlementSummaryAsync(billingProfileId, _session);
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
                    transaction.LinkedRentalBillingProfileId == billingProfileId)
                .Sum(transaction => transaction.SettlementAmount);
            var billedAmount = profile.MonthlyAmount;
            return new RentalSettlementSummary
            {
                BilledAmount = billedAmount,
                SettledAmount = settledAmount,
                OutstandingAmount = Math.Max(0m, billedAmount - settledAmount),
                BillingStatus = string.IsNullOrWhiteSpace(profile.BillingStatus)
                    ? PaymentFlowConstants.BillingStatusInProgress
                    : profile.BillingStatus,
                SettlementStatus = string.IsNullOrWhiteSpace(profile.SettlementStatus)
                    ? (settledAmount <= 0m ? PaymentFlowConstants.SettlementStatusUnpaid : PaymentFlowConstants.SettlementStatusPartial)
                    : profile.SettlementStatus,
                CompletionStatus = Math.Max(0m, billedAmount - settledAmount) <= 0m
                    ? PaymentFlowConstants.CompletionDone
                    : PaymentFlowConstants.CompletionPending
            };
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            return;
        }

        var kind = PaymentFlowConstants.NormalizeTransactionKind(SelectedTransactionKind);
        var transaction = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = SelectedCustomer.Id,
            TransactionKind = kind,
            LinkedInvoiceId = _linkedInvoice?.Id,
            LinkedRentalBillingProfileId = _linkedRentalProfile?.Id,
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
                    StatusMessage = "선수금환불은 지급 금액만 입력해야 합니다.";
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
                break;

            default:
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
                break;
        }

        var result = await _local.SaveTransactionAsync(transaction, _session);
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }

        await LoadHistoryAsync(SelectedCustomer.Id);
        SelectedHistory = History.FirstOrDefault(current => current.Id == result.EntityId);
        NewEntry();
        StatusMessage = result.Message;
        await RefreshContextAsync();
    }

    [RelayCommand]
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

        await LoadAttachmentsAsync(SelectedHistory.Id);
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

    [RelayCommand]
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
            await LoadAttachmentsAsync(SelectedHistory.Id);
    }

    [RelayCommand]
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
            await LoadAttachmentsAsync(SelectedHistory.Id);
            SelectedAttachment = Attachments.FirstOrDefault(current => current.Id == selectedAttachmentId);
        }
    }

    [RelayCommand]
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
            await LoadAttachmentsAsync(SelectedHistory.Id);
            SelectedAttachment = Attachments.FirstOrDefault(current => current.Id == selectedAttachmentId);
        }
    }
}
