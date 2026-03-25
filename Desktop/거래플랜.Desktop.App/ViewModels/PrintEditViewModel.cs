using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PrintEditViewModel : ObservableObject
{
    private readonly IPrintService _printService;
    private readonly Func<InvoicePrintModel, Task> _saveAction;
    private readonly Guid _invoiceId;

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
    [ObservableProperty] private string _footerText = string.Empty;
    [ObservableProperty] private string _bankAccountText = string.Empty;

    [ObservableProperty] private bool _printWithDate = true;
    [ObservableProperty] private bool _printWithPrice = true;

    [ObservableProperty] private decimal _supplyAmount;
    [ObservableProperty] private decimal _vatAmount;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private decimal _paidAmount;
    [ObservableProperty] private decimal _balanceAmount;

    [ObservableProperty] private System.Windows.Documents.FixedDocument? _previewDocument;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _wasSaved;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isSaving;
    [ObservableProperty] private InvoicePrintLineEditModel? _selectedLine;

    public ObservableCollection<InvoicePrintLineEditModel> Lines { get; } = new();

    public PrintEditViewModel(
        InvoicePrintModel model,
        IPrintService printService,
        Func<InvoicePrintModel, Task> saveAction)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(printService);
        ArgumentNullException.ThrowIfNull(saveAction);

        _printService = printService;
        _saveAction = saveAction;
        _invoiceId = model.InvoiceId;

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
        FooterText = model.FooterText ?? string.Empty;
        BankAccountText = model.BankAccountText ?? string.Empty;
        PrintWithDate = model.PrintWithDate;
        PrintWithPrice = model.PrintWithPrice;
        SupplyAmount = model.SupplyAmount;
        VatAmount = model.VatAmount;
        TotalAmount = model.TotalAmount;
        PaidAmount = model.PaidAmount;
        BalanceAmount = model.BalanceAmount;

        foreach (var line in model.Lines.OrderBy(l => l.No))
        {
            Lines.Add(InvoicePrintLineEditModel.FromModel(line));
        }

        if (Lines.Count == 0)
            Lines.Add(new InvoicePrintLineEditModel { No = 1 });

        RefreshPreview();
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
    {
        try
        {
            PreviewDocument = _printService.BuildFixedDocument(BuildModel());
            StatusMessage = "미리보기를 갱신했습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"미리보기 생성 실패: {ex.Message}";
            System.Windows.MessageBox.Show(
                StatusMessage,
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private bool CanSave() => !IsSaving;

    private bool CanClose() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (IsSaving)
            return;

        try
        {
            IsSaving = true;
            StatusMessage = "출력물 편집 내용을 저장하는 중...";
            var model = BuildModel();
            await _saveAction(model);
            WasSaved = true;
            StatusMessage = "출력물 편집 내용을 저장했습니다.";
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 실패: {ex.Message}";
            System.Windows.MessageBox.Show(
                StatusMessage,
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
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
            FooterText = FooterText?.Trim() ?? string.Empty,
            BankAccountText = BankAccountText?.Trim() ?? string.Empty,
            PrintWithDate = PrintWithDate,
            PrintWithPrice = PrintWithPrice,
            SupplyAmount = SupplyAmount,
            VatAmount = VatAmount,
            TotalAmount = TotalAmount,
            PaidAmount = PaidAmount,
            BalanceAmount = BalanceAmount,
            Lines = lines
        };
    }
}

public sealed partial class InvoicePrintLineEditModel : ObservableObject
{
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
