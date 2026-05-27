using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PrintEditViewModel : ObservableObject, IDisposable
{
    private readonly Func<InvoicePrintModel, Task> _saveAction;
    private readonly Func<InvoicePrintModel, string, FixedDocument> _previewBuilder;
    private readonly UiDebouncer _previewDebouncer = new();
    private readonly Guid _invoiceId;
    private DateTime _snapshotCreatedAtUtc;
    private DateTime _snapshotLastSavedAtUtc;
    private string _snapshotPolicy = InvoicePrintModel.DefaultSnapshotPolicy;
    private string _baselineStateSignature = string.Empty;
    private bool _isInitializing;

    public const string PreviewDocumentStatement = "거래명세서";
    public const string PreviewDocumentEstimate = "견적서";
    public const string PreviewDocumentPaymentClaim = "대금청구서";

    public event Action? RequestClose;

    [ObservableProperty] private string _invoiceNumber = string.Empty;
    [ObservableProperty] private DateTime _invoiceDate = DateTime.Today;
    [ObservableProperty] private string _voucherType = string.Empty;

    [ObservableProperty] private string _supplierBusinessNumber = string.Empty;
    [ObservableProperty] private string _supplierName = string.Empty;
    [ObservableProperty] private string _supplierRepresentative = string.Empty;
    [ObservableProperty] private string _supplierPhone = string.Empty;
    [ObservableProperty] private string _supplierAddress = string.Empty;

    [ObservableProperty] private string _buyerBusinessNumber = string.Empty;
    [ObservableProperty] private string _buyerName = string.Empty;
    [ObservableProperty] private string _buyerRepresentative = string.Empty;
    [ObservableProperty] private string _buyerPhone = string.Empty;
    [ObservableProperty] private string _buyerAddress = string.Empty;

    [ObservableProperty] private string _managerName = string.Empty;
    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty] private string _estimateOrganization = string.Empty;
    [ObservableProperty] private string _estimateValidityText = string.Empty;
    [ObservableProperty] private string _estimateRemarks = string.Empty;
    [ObservableProperty] private string _footerText = string.Empty;
    [ObservableProperty] private string _bankAccountText = string.Empty;

    [ObservableProperty] private bool _printWithDate = true;
    [ObservableProperty] private bool _printWithPrice = true;

    [ObservableProperty] private string _vatMode = InvoiceVatModes.Included;
    [ObservableProperty] private decimal _supplyAmount;
    [ObservableProperty] private decimal _vatAmount;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private decimal _paidAmount;
    [ObservableProperty] private decimal _balanceAmount;

    [ObservableProperty] private System.Windows.Documents.FixedDocument? _previewDocument;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatementPreviewSelected))]
    [NotifyPropertyChangedFor(nameof(IsEstimatePreviewSelected))]
    [NotifyPropertyChangedFor(nameof(IsPaymentClaimPreviewSelected))]
    [NotifyPropertyChangedFor(nameof(ShowLineEditor))]
    [NotifyPropertyChangedFor(nameof(PreviewDocumentGuideText))]
    [NotifyPropertyChangedFor(nameof(AmountSectionTitleText))]
    [NotifyPropertyChangedFor(nameof(LineEditorTitleText))]
    private string _selectedPreviewDocument = PreviewDocumentStatement;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _wasSaved;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isSaving;
    [ObservableProperty] private InvoicePrintLineEditModel? _selectedLine;

    public ObservableCollection<InvoicePrintLineEditModel> Lines { get; } = new();
    public IReadOnlyList<string> PreviewDocumentOptions { get; } =
    [
        PreviewDocumentStatement,
        PreviewDocumentEstimate,
        PreviewDocumentPaymentClaim
    ];
    public bool IsStatementPreviewSelected => string.Equals(SelectedPreviewDocument, PreviewDocumentStatement, StringComparison.Ordinal);
    public bool IsEstimatePreviewSelected => string.Equals(SelectedPreviewDocument, PreviewDocumentEstimate, StringComparison.Ordinal);
    public bool IsPaymentClaimPreviewSelected => string.Equals(SelectedPreviewDocument, PreviewDocumentPaymentClaim, StringComparison.Ordinal);
    public bool ShowLineEditor => !IsPaymentClaimPreviewSelected;
    public string PreviewDocumentGuideText => SelectedPreviewDocument switch
    {
        PreviewDocumentEstimate => "견적서에 보이는 견적기간·특이사항·품목을 바로 편집합니다.",
        PreviewDocumentPaymentClaim => "대금청구서에 보이는 용역명과 계좌정보를 바로 편집합니다.",
        _ => "거래명세서에 보이는 메모·입금안내·하단문구·품목을 바로 편집합니다."
    };
    public string AmountSectionTitleText => SelectedPreviewDocument switch
    {
        PreviewDocumentEstimate => "견적 금액 정보",
        PreviewDocumentPaymentClaim => "청구 금액 정보",
        _ => "거래명세 금액 정보"
    };
    public string LineEditorTitleText => SelectedPreviewDocument switch
    {
        PreviewDocumentEstimate => "견적 품목 편집",
        _ => "거래명세 품목 편집"
    };
    public bool IsPurchaseDocument => string.Equals(VoucherType, 거래플랜.Shared.Contracts.VoucherType.Purchase.ToString(), StringComparison.OrdinalIgnoreCase);
    public string SettlementAmountLabelText => IsPurchaseDocument ? "지급액" : "입금액";
    public string OutstandingAmountLabelText => IsPurchaseDocument ? "미지급" : "미수금";
    public bool HasPendingChanges => !string.Equals(_baselineStateSignature, BuildStateSignature(BuildModel()), StringComparison.Ordinal);
    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent();

    public PrintEditViewModel(
        InvoicePrintModel model,
        Func<InvoicePrintModel, Task> saveAction,
        Func<InvoicePrintModel, string, FixedDocument> previewBuilder)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(saveAction);
        ArgumentNullException.ThrowIfNull(previewBuilder);

        _saveAction = saveAction;
        _previewBuilder = previewBuilder;
        _invoiceId = model.InvoiceId;
        _snapshotCreatedAtUtc = model.SnapshotCreatedAtUtc == default ? DateTime.UtcNow : model.SnapshotCreatedAtUtc;
        _snapshotLastSavedAtUtc = model.SnapshotLastSavedAtUtc;
        _snapshotPolicy = string.IsNullOrWhiteSpace(model.SnapshotPolicy)
            ? InvoicePrintModel.DefaultSnapshotPolicy
            : model.SnapshotPolicy.Trim();
        _isInitializing = true;
        Lines.CollectionChanged += Lines_CollectionChanged;
        PropertyChanged += PrintEditViewModel_PropertyChanged;

        InvoiceNumber = model.InvoiceNumber ?? string.Empty;
        InvoiceDate = model.InvoiceDate.ToDateTime(TimeOnly.MinValue);
        VoucherType = model.VoucherType ?? string.Empty;
        SupplierBusinessNumber = model.SupplierBusinessNumber ?? string.Empty;
        SupplierName = model.SupplierName ?? string.Empty;
        SupplierRepresentative = model.SupplierRepresentative ?? string.Empty;
        SupplierPhone = model.SupplierPhone ?? string.Empty;
        SupplierAddress = model.SupplierAddress ?? string.Empty;
        BuyerBusinessNumber = model.BuyerBusinessNumber ?? string.Empty;
        BuyerName = model.BuyerName ?? string.Empty;
        BuyerRepresentative = model.BuyerRepresentative ?? string.Empty;
        BuyerPhone = model.BuyerPhone ?? string.Empty;
        BuyerAddress = model.BuyerAddress ?? string.Empty;
        ManagerName = model.ManagerName ?? string.Empty;
        Memo = model.Memo ?? string.Empty;
        EstimateOrganization = model.EstimateOrganization ?? string.Empty;
        EstimateValidityText = model.EstimateValidityText ?? string.Empty;
        EstimateRemarks = model.EstimateRemarks ?? string.Empty;
        FooterText = model.FooterText ?? string.Empty;
        BankAccountText = model.BankAccountText ?? string.Empty;
        PrintWithDate = model.PrintWithDate;
        PrintWithPrice = model.PrintWithPrice;
        VatMode = InvoiceVatModes.Normalize(model.VatMode);
        SupplyAmount = model.SupplyAmount;
        VatAmount = model.VatAmount;
        TotalAmount = model.TotalAmount;
        PaidAmount = model.PaidAmount;
        BalanceAmount = model.BalanceAmount;

        foreach (var line in model.Lines)
        {
            Lines.Add(InvoicePrintLineEditModel.FromModel(line));
        }

        if (Lines.Count == 0)
            Lines.Add(new InvoicePrintLineEditModel { No = 1 });

        _isInitializing = false;
        RefreshPreviewCore(showErrorDialog: false, announceSuccess: false);
        CaptureBaselineState();
    }

    partial void OnVoucherTypeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPurchaseDocument));
        OnPropertyChanged(nameof(SettlementAmountLabelText));
        OnPropertyChanged(nameof(OutstandingAmountLabelText));
    }

    [RelayCommand]
    private void AddLine()
    {
        var nextNo = Lines.Count == 0 ? 1 : Lines.Max(l => l.No) + 1;
        Lines.Add(new InvoicePrintLineEditModel { No = nextNo });
    }

    [RelayCommand]
    private void RemoveLine(InvoicePrintLineEditModel? line)
    {
        if (line is null)
            return;

        Lines.Remove(line);
        if (Lines.Count == 0)
            Lines.Add(new InvoicePrintLineEditModel { No = 1 });

        for (var i = 0; i < Lines.Count; i++)
            Lines[i].No = i + 1;
    }

    [RelayCommand]
    private void RefreshPreview()
        => RefreshPreviewCore(showErrorDialog: true, announceSuccess: true);

    private void RefreshPreviewCore(bool showErrorDialog, bool announceSuccess)
    {
        try
        {
            PreviewDocument = _previewBuilder(BuildModel(), NormalizePreviewDocument(SelectedPreviewDocument));
            if (announceSuccess)
                StatusMessage = "미리보기를 갱신했습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"미리보기 생성 실패: {ex.Message}";
            if (showErrorDialog)
            {
                System.Windows.MessageBox.Show(
                    StatusMessage,
                    "오류",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private bool CanSave() => !IsSaving;

    private bool CanClose() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
        => await SaveCoreAsync(closeAfterSave: true, successMessage: "출력물 편집 내용을 저장했습니다.", showErrorDialog: true);

    public async Task<bool> TryAutoSaveOnCloseAsync()
    {
        if (!HasPendingChanges || !HasMeaningfulDraftContentForClose)
            return true;

        return await SaveCoreAsync(closeAfterSave: false, successMessage: "출력물 편집 내용을 자동 저장했습니다.", showErrorDialog: false);
    }

    private async Task<bool> SaveCoreAsync(bool closeAfterSave, string successMessage, bool showErrorDialog)
    {
        if (IsSaving)
            return false;

        try
        {
            IsSaving = true;
            StatusMessage = "출력물 편집 내용을 저장하는 중...";
            var model = BuildModel();
            await _saveAction(model);
            WasSaved = true;
            StatusMessage = successMessage;
            CaptureBaselineState();
            if (closeAfterSave)
                RequestClose?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 실패: {ex.Message}";
            if (showErrorDialog)
            {
                System.Windows.MessageBox.Show(
                    StatusMessage,
                    "오류",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }

            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    public InvoicePrintModel BuildModel()
    {
        var lines = Lines
            .Select((line, index) => line.ToModel(index + 1))
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemName)
                        || !string.IsNullOrWhiteSpace(line.Specification)
                        || line.Quantity != 0
                        || line.UnitPrice != 0
                        || line.Amount != 0
                        || !string.IsNullOrWhiteSpace(line.Remark))
            .ToList();

        if (lines.Count == 0)
            lines.Add(new InvoicePrintLineModel { No = 1 });

        return new InvoicePrintModel
        {
            InvoiceId = _invoiceId,
            InvoiceNumber = InvoiceNumber?.Trim() ?? string.Empty,
            InvoiceDate = DateOnly.FromDateTime(InvoiceDate == default ? DateTime.Today : InvoiceDate),
            VoucherType = VoucherType?.Trim() ?? string.Empty,
            SnapshotCreatedAtUtc = _snapshotCreatedAtUtc,
            SnapshotLastSavedAtUtc = _snapshotLastSavedAtUtc,
            SnapshotPolicy = string.IsNullOrWhiteSpace(_snapshotPolicy)
                ? InvoicePrintModel.DefaultSnapshotPolicy
                : _snapshotPolicy,
            SupplierBusinessNumber = SupplierBusinessNumber?.Trim() ?? string.Empty,
            SupplierName = SupplierName?.Trim() ?? string.Empty,
            SupplierRepresentative = SupplierRepresentative?.Trim() ?? string.Empty,
            SupplierPhone = SupplierPhone?.Trim() ?? string.Empty,
            SupplierAddress = SupplierAddress?.Trim() ?? string.Empty,
            BuyerBusinessNumber = BuyerBusinessNumber?.Trim() ?? string.Empty,
            BuyerName = BuyerName?.Trim() ?? string.Empty,
            BuyerRepresentative = BuyerRepresentative?.Trim() ?? string.Empty,
            BuyerPhone = BuyerPhone?.Trim() ?? string.Empty,
            BuyerAddress = BuyerAddress?.Trim() ?? string.Empty,
            ManagerName = ManagerName?.Trim() ?? string.Empty,
            Memo = Memo?.Trim() ?? string.Empty,
            EstimateOrganization = EstimateOrganization?.Trim() ?? string.Empty,
            EstimateValidityText = EstimateValidityText?.Trim() ?? string.Empty,
            EstimateRemarks = EstimateRemarks?.Trim() ?? string.Empty,
            FooterText = FooterText?.Trim() ?? string.Empty,
            BankAccountText = BankAccountText?.Trim() ?? string.Empty,
            PrintWithDate = PrintWithDate,
            PrintWithPrice = PrintWithPrice,
            VatMode = InvoiceVatModes.Normalize(VatMode),
            SupplyAmount = SupplyAmount,
            VatAmount = VatAmount,
            TotalAmount = TotalAmount,
            PaidAmount = PaidAmount,
            BalanceAmount = BalanceAmount,
            Lines = lines
        };
    }

    private bool HasMeaningfulDraftContent()
    {
        var model = BuildModel();
        return !string.IsNullOrWhiteSpace(model.InvoiceNumber)
               || !string.IsNullOrWhiteSpace(model.SupplierName)
               || !string.IsNullOrWhiteSpace(model.BuyerName)
               || !string.IsNullOrWhiteSpace(model.ManagerName)
               || !string.IsNullOrWhiteSpace(model.Memo)
               || !string.IsNullOrWhiteSpace(model.EstimateOrganization)
               || !string.IsNullOrWhiteSpace(model.EstimateValidityText)
               || !string.IsNullOrWhiteSpace(model.EstimateRemarks)
               || !string.IsNullOrWhiteSpace(model.FooterText)
               || !string.IsNullOrWhiteSpace(model.BankAccountText)
               || model.SupplyAmount != 0m
               || model.VatAmount != 0m
               || model.TotalAmount != 0m
               || model.PaidAmount != 0m
               || model.BalanceAmount != 0m
               || model.Lines.Any(line =>
                   !string.IsNullOrWhiteSpace(line.ItemName)
                   || !string.IsNullOrWhiteSpace(line.Specification)
                   || !string.IsNullOrWhiteSpace(line.Unit)
                   || line.Quantity != 0m
                   || line.UnitPrice != 0m
                   || line.Amount != 0m
                   || !string.IsNullOrWhiteSpace(line.Remark));
    }

    private void CaptureBaselineState()
        => _baselineStateSignature = BuildStateSignature(BuildModel());

    private static string BuildStateSignature(InvoicePrintModel model)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(model.InvoiceId.ToString("D"))
            .Append('|').Append(model.InvoiceNumber ?? string.Empty)
            .Append('|').Append(model.InvoiceDate.ToString("yyyy-MM-dd"))
            .Append('|').Append(model.VoucherType ?? string.Empty)
            .Append('|').Append(model.SupplierBusinessNumber ?? string.Empty)
            .Append('|').Append(model.SupplierName ?? string.Empty)
            .Append('|').Append(model.SupplierRepresentative ?? string.Empty)
            .Append('|').Append(model.SupplierPhone ?? string.Empty)
            .Append('|').Append(model.SupplierAddress ?? string.Empty)
            .Append('|').Append(model.BuyerBusinessNumber ?? string.Empty)
            .Append('|').Append(model.BuyerName ?? string.Empty)
            .Append('|').Append(model.BuyerRepresentative ?? string.Empty)
            .Append('|').Append(model.BuyerPhone ?? string.Empty)
            .Append('|').Append(model.BuyerAddress ?? string.Empty)
            .Append('|').Append(model.ManagerName ?? string.Empty)
            .Append('|').Append(model.Memo ?? string.Empty)
            .Append('|').Append(model.EstimateOrganization ?? string.Empty)
            .Append('|').Append(model.EstimateValidityText ?? string.Empty)
            .Append('|').Append(model.EstimateRemarks ?? string.Empty)
            .Append('|').Append(model.FooterText ?? string.Empty)
            .Append('|').Append(model.BankAccountText ?? string.Empty)
            .Append('|').Append(model.PrintWithDate)
            .Append('|').Append(model.PrintWithPrice)
            .Append('|').Append(InvoiceVatModes.Normalize(model.VatMode))
            .Append('|').Append(model.SupplyAmount)
            .Append('|').Append(model.VatAmount)
            .Append('|').Append(model.TotalAmount)
            .Append('|').Append(model.PaidAmount)
            .Append('|').Append(model.BalanceAmount);

        foreach (var line in model.Lines)
        {
            builder.Append('|').Append(line.SourceLineId?.ToString("N") ?? string.Empty)
                .Append(':').Append(line.No)
                .Append(':').Append(line.ItemName ?? string.Empty)
                .Append(':').Append(line.Specification ?? string.Empty)
                .Append(':').Append(line.Unit ?? string.Empty)
                .Append(':').Append(line.Quantity)
                .Append(':').Append(line.UnitPrice)
                .Append(':').Append(line.Amount)
                .Append(':').Append(line.Remark ?? string.Empty);
        }

        return builder.ToString();
    }

    private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (InvoicePrintLineEditModel line in e.OldItems)
                line.PropertyChanged -= Line_PropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (InvoicePrintLineEditModel line in e.NewItems)
                line.PropertyChanged += Line_PropertyChanged;
        }

        QueuePreviewRefresh();
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        => QueuePreviewRefresh();

    private void PrintEditViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShouldQueuePreviewRefresh(e.PropertyName))
            return;

        QueuePreviewRefresh();
    }

    private bool ShouldQueuePreviewRefresh(string? propertyName)
    {
        if (_isInitializing || IsSaving)
            return false;

        return propertyName switch
        {
            nameof(PreviewDocument) => false,
            nameof(StatusMessage) => false,
            nameof(WasSaved) => false,
            nameof(IsSaving) => false,
            nameof(SelectedLine) => false,
            nameof(IsPurchaseDocument) => false,
            nameof(SettlementAmountLabelText) => false,
            nameof(OutstandingAmountLabelText) => false,
            nameof(IsStatementPreviewSelected) => false,
            nameof(IsEstimatePreviewSelected) => false,
            nameof(IsPaymentClaimPreviewSelected) => false,
            nameof(ShowLineEditor) => false,
            nameof(PreviewDocumentGuideText) => false,
            nameof(AmountSectionTitleText) => false,
            nameof(LineEditorTitleText) => false,
            _ => true
        };
    }

    private void QueuePreviewRefresh()
    {
        if (_isInitializing)
            return;

        _previewDebouncer.Debounce(
            TimeSpan.FromMilliseconds(180),
            () => RefreshPreviewCore(showErrorDialog: false, announceSuccess: false));
    }

    private static string NormalizePreviewDocument(string? documentName)
        => documentName switch
        {
            PreviewDocumentEstimate => PreviewDocumentEstimate,
            PreviewDocumentPaymentClaim => PreviewDocumentPaymentClaim,
            _ => PreviewDocumentStatement
        };

    public void Dispose()
    {
        PropertyChanged -= PrintEditViewModel_PropertyChanged;
        Lines.CollectionChanged -= Lines_CollectionChanged;
        foreach (var line in Lines)
            line.PropertyChanged -= Line_PropertyChanged;
        _previewDebouncer.Dispose();
    }
}

public sealed partial class InvoicePrintLineEditModel : ObservableObject
{
    public Guid? SourceLineId { get; set; }

    [ObservableProperty] private int _no;
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _remark = string.Empty;

    public static InvoicePrintLineEditModel FromModel(InvoicePrintLineModel model)
    {
        return new InvoicePrintLineEditModel
        {
            SourceLineId = model.SourceLineId,
            No = model.No,
            ItemName = model.ItemName ?? string.Empty,
            Specification = model.Specification ?? string.Empty,
            Unit = model.Unit ?? string.Empty,
            Quantity = model.Quantity,
            UnitPrice = model.UnitPrice,
            Amount = model.Amount,
            Remark = model.Remark ?? string.Empty
        };
    }

    public InvoicePrintLineModel ToModel(int no)
    {
        return new InvoicePrintLineModel
        {
            SourceLineId = SourceLineId,
            No = no,
            ItemName = ItemName?.Trim() ?? string.Empty,
            Specification = Specification?.Trim() ?? string.Empty,
            Unit = Unit?.Trim() ?? string.Empty,
            Quantity = Quantity,
            UnitPrice = UnitPrice,
            Amount = Amount,
            Remark = Remark?.Trim() ?? string.Empty
        };
    }
}
