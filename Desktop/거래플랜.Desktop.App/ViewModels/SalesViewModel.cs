using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SalesViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;
    private readonly VoucherType _newInvoiceVoucherType;
    private List<LocalItem> _allItems = new();
    private List<LocalCustomer> _allCustomers = new();
    private readonly Dictionary<string, string> _priceGradeSourceMap = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, (bool AllowsSales, bool AllowsPurchase)> _tradeTypeRuleMap = new(StringComparer.CurrentCultureIgnoreCase);
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);
    private string _baselineStateSignature = string.Empty;
    private bool _lastSaveWasConcurrencyConflict;
    public string LastAutoSaveFailureMessage { get; private set; } = string.Empty;

    public event Action? InvoiceSaved;

    // ?? 怨좉컼 ?뺣낫 (?곷떒) ??????????????????????????????????????????????????
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLoadPreviousHistory))]
    private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerPhone = string.Empty;
    [ObservableProperty] private string _customerMobile = string.Empty;
    [ObservableProperty] private string _customerTradeType = string.Empty;
    [ObservableProperty] private string _customerPriceGrade = string.Empty;
    [ObservableProperty] private string _customerNote = string.Empty;
    [ObservableProperty] private decimal _customerBalance;   // 珥?誘몄닔湲?
    [ObservableProperty] private decimal _customerAdvanceBalance;
    [ObservableProperty] private string _selectedResponsibleOfficeCode = DomainConstants.OfficeUsenet;
    [ObservableProperty] private string _selectedWarehouseCode = DomainConstants.WarehouseUsenetMain;
    // ?? ?꾪몴 ?ㅻ뜑 ?????????????????????????????????????????????????????????
    [ObservableProperty] private Guid _invoiceId = Guid.NewGuid();
    [ObservableProperty] private DateOnly _workDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _invoiceMemo = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPurchaseDocument))]
    [NotifyPropertyChangedFor(nameof(IsProcurementDocument))]
    [NotifyPropertyChangedFor(nameof(IsPurchaseLikeDocument))]
    [NotifyPropertyChangedFor(nameof(IsSalesDocument))]
    [NotifyPropertyChangedFor(nameof(WindowTitleText))]
    [NotifyPropertyChangedFor(nameof(HeaderTitleText))]
    [NotifyPropertyChangedFor(nameof(HeaderSubtitleText))]
    [NotifyPropertyChangedFor(nameof(CustomerSectionTitleText))]
    [NotifyPropertyChangedFor(nameof(DocumentSectionTitleText))]
    [NotifyPropertyChangedFor(nameof(DocumentDetailTitleText))]
    [NotifyPropertyChangedFor(nameof(NewDocumentButtonText))]
    [NotifyPropertyChangedFor(nameof(WarehouseLabelText))]
    [NotifyPropertyChangedFor(nameof(ShowStatementOptions))]
    [NotifyPropertyChangedFor(nameof(ShowPurchasePrintHint))]
    [NotifyPropertyChangedFor(nameof(ShowProcurementPrintHint))]
    [NotifyPropertyChangedFor(nameof(ShowProcurementTitleSelector))]
    [NotifyPropertyChangedFor(nameof(CanEditPrintOutput))]
    [NotifyPropertyChangedFor(nameof(CanPrintTaxInvoice))]
    [NotifyPropertyChangedFor(nameof(ShowPaymentAction))]
    [NotifyPropertyChangedFor(nameof(PaymentActionButtonText))]
    [NotifyPropertyChangedFor(nameof(PaymentSummaryTitleText))]
    private VoucherType _voucherType = VoucherType.Sales;
    public Array VoucherTypes => Enum.GetValues<VoucherType>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentDetailTitleText))]
    private string _selectedProcurementDocumentTitle = "발주서";

    public IReadOnlyList<string> ProcurementDocumentTitleOptions { get; } = ["발주서", "납품서", "의뢰서"];

    // ?? ?쇱씤 ?낅젰 (?④굔) ??????????????????????????????????????????????????
    [ObservableProperty] private string _inputItemName = string.Empty;
    [ObservableProperty] private string _inputSpec = string.Empty;
    [ObservableProperty] private decimal _inputQty = 1;
    [ObservableProperty] private string _inputUnit = string.Empty;
    [ObservableProperty] private decimal _inputUnitPrice;
    [ObservableProperty] private decimal _inputLineAmount;
    [ObservableProperty] private string _inputRemark = string.Empty;
    [ObservableProperty] private string _inputSerialNumber = string.Empty;
    [ObservableProperty] private string _inputMaterialNo = string.Empty;
    [ObservableProperty] private LocalItem? _selectedInputItem;

    // ?? ?쇱씤 紐⑸줉 ?????????????????????????????????????????????????????????
    public ObservableCollection<InvoiceLineEditModel> Lines { get; } = new();
    [ObservableProperty] private InvoiceLineEditModel? _selectedLine;

    // ?? ?⑷퀎 ?????????????????????????????????????????????????????????????
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private decimal _supplyAmount;
    [ObservableProperty] private decimal _vatAmount;

    // ?? ?곹뭹 ?뺣낫 ?⑤꼸 (?섎떒) ?????????????????????????????????????????????
    [ObservableProperty] private string _itemSearchText = string.Empty;
    public ObservableCollection<LocalItem> ItemSearchResults { get; } = new();

    // ?? ?몄뇙 ?듭뀡 ?????????????????????????????????????????????????????????
    [ObservableProperty] private bool _printWithDate = true;
    [ObservableProperty] private bool _printWithPrice = true;
    [ObservableProperty] private bool _printStatementDocument = true;
    [ObservableProperty] private bool _printEstimateDocument;
    [ObservableProperty] private bool _printPaymentClaimDocument;
    [ObservableProperty] private string _printType = "거래명1/2";
    public string[] PrintTypes { get; } = ["거래명1/2", "거래명A4", "영수증출력", "출고증A4"];

    // ?? ?곹깭 ?????????????????????????????????????????????????????????????
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _lastSavedBy = string.Empty;
    [ObservableProperty] private string _lastSavedAtDisplay = string.Empty;
    [ObservableProperty] private string _versionDisplay = "v1";
    [ObservableProperty] private string _currentConcurrencyStamp = string.Empty;
    [ObservableProperty] private string _paymentSummaryContextText = "전표를 저장하면 수금/지급 요약이 표시됩니다.";
    [ObservableProperty] private string _paymentSummaryDetailText = "연결 전표가 없으면 지급/수금 요약은 표시되지 않습니다.";
    [ObservableProperty] private string _paymentSummaryAdvanceText = "선수금 잔액 0";

    public ObservableCollection<LocalOffice> Offices { get; } = new();
    public ObservableCollection<LocalWarehouse> Warehouses { get; } = new();
    public ObservableCollection<LocalInvoice> InvoiceVersions { get; } = new();
    public bool IsPurchaseDocument => VoucherType == VoucherType.Purchase;
    public bool IsProcurementDocument => VoucherType == VoucherType.Procurement;
    public bool IsPurchaseLikeDocument => VoucherType is VoucherType.Purchase or VoucherType.Procurement;
    public bool IsSalesDocument => VoucherType == VoucherType.Sales;
    public bool CanLoadPreviousHistory => SelectedCustomer is not null;
    public bool ShowStatementOptions => IsSalesDocument;
    public bool ShowPurchasePrintHint => IsPurchaseDocument;
    public bool ShowProcurementPrintHint => IsProcurementDocument;
    public bool ShowProcurementTitleSelector => IsProcurementDocument;
    public bool CanEditPrintOutput => IsSalesDocument;
    public bool CanPrintTaxInvoice => IsSalesDocument;
    public bool ShowPaymentAction => IsSalesDocument || IsPurchaseDocument;
    public string PaymentActionButtonText => IsPurchaseDocument ? "지급 입력" : "수금 입력";
    public string PaymentSummaryTitleText => IsPurchaseDocument ? "지급 요약" : "수금 요약";
    public string WindowTitleText => VoucherType switch
    {
        VoucherType.Purchase => "구매(매입)",
        VoucherType.Procurement => "견적/발주",
        _ => "판매(매출)"
    };
    public string HeaderTitleText => VoucherType switch
    {
        VoucherType.Purchase => "구매(매입)",
        VoucherType.Procurement => "견적/발주",
        _ => "판매(매출)"
    };
    public string HeaderSubtitleText => VoucherType switch
    {
        VoucherType.Purchase => "구매(매입) 전표작성/수정 - USENET 재고 등록 및 매입 명세서 출력",
        VoucherType.Procurement => "견적/발주 전표작성/수정 - 발주서, 납품서, 의뢰서 출력",
        _ => "판매(매출) 전표작성/수정/삭제 - 거래명세서, 세금계산서, 영수증 발행"
    };
    public string CustomerSectionTitleText => IsPurchaseLikeDocument ? "매입처/거래처" : "고객/거래처";
    public string DocumentSectionTitleText => VoucherType switch
    {
        VoucherType.Purchase => "구매작성",
        VoucherType.Procurement => "견적발주작성",
        _ => "전표작성"
    };
    public string DocumentDetailTitleText => VoucherType switch
    {
        VoucherType.Purchase => "구매[매입] 세부내역",
        VoucherType.Procurement => $"{NormalizeProcurementDocumentTitle(SelectedProcurementDocumentTitle)} 세부내역",
        _ => "판매[매출] 세부내역"
    };
    public string NewDocumentButtonText => VoucherType switch
    {
        VoucherType.Purchase => "신규구매(F6)",
        VoucherType.Procurement => "신규발주(F6)",
        _ => "신규전표(F6)"
    };
    public string WarehouseLabelText => IsPurchaseLikeDocument ? "입고창고" : "출고창고";

    public SalesViewModel(
        LocalStateService local,
        StatementPrintService print,
        IPrintService invoicePrintService,
        SessionState session,
        VoucherType newInvoiceVoucherType = VoucherType.Sales)
    {
        _local = local;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        _newInvoiceVoucherType = newInvoiceVoucherType;
        VoucherType = newInvoiceVoucherType;
        Lines.CollectionChanged += Lines_CollectionChanged;
    }

    private void Lines_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (InvoiceLineEditModel line in e.OldItems)
                line.PropertyChanged -= Line_PropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (InvoiceLineEditModel line in e.NewItems)
                line.PropertyChanged += Line_PropertyChanged;
        }

        RecalcTotals();
    }

    private void Line_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RecalcTotals();
    }

    public async Task LoadAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        _allItems = await _local.GetItemsAsync();
        await LoadMasterOptionsAsync();
        await LoadOfficeWarehouseAsync();
        RefreshItemSearch();
        InitializeOfficeAndWarehouseDefaults();
        await RefreshPaymentSummaryAsync();
    }

    private async Task LoadMasterOptionsAsync()
    {
        _priceGradeSourceMap.Clear();
        foreach (var option in await _local.GetPriceGradeOptionsAsync())
        {
            if (string.IsNullOrWhiteSpace(option.Name))
                continue;

            _priceGradeSourceMap[option.Name.Trim()] = SelectionOptionDefaults.NormalizePriceSource(option.PriceSource);
        }

        _tradeTypeRuleMap.Clear();
        foreach (var option in await _local.GetTradeTypeOptionsAsync())
        {
            if (string.IsNullOrWhiteSpace(option.Name))
                continue;

            _tradeTypeRuleMap[option.Name.Trim()] = (option.AllowsSales, option.AllowsPurchase);
        }
    }

    private async Task LoadOfficeWarehouseAsync()
    {
        Offices.Clear();
        foreach (var office in await _local.GetOfficesAsync())
            Offices.Add(office);

        Warehouses.Clear();
        foreach (var warehouse in await _local.GetWarehousesAsync())
            Warehouses.Add(warehouse);
    }

    private void InitializeOfficeAndWarehouseDefaults()
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);

        SelectedResponsibleOfficeCode = officeCode;
        SelectedWarehouseCode = ResolveDefaultWarehouseCode(officeCode);
    }

    private async Task LoadInvoiceVersionsAsync(LocalInvoice invoice)
    {
        InvoiceVersions.Clear();
        var versionGroupId = invoice.VersionGroupId == Guid.Empty ? invoice.Id : invoice.VersionGroupId;
        var versions = await _local.GetInvoiceVersionsAsync(versionGroupId, _session);
        foreach (var version in versions.OrderByDescending(v => v.VersionNumber))
            InvoiceVersions.Add(version);
    }

    public void NewInvoice()
    {
        InvoiceId = Guid.NewGuid();
        SelectedCustomer = null;
        CustomerName = string.Empty;
        CustomerPhone = string.Empty;
        CustomerMobile = string.Empty;
        CustomerTradeType = string.Empty;
        CustomerPriceGrade = string.Empty;
        CustomerNote = string.Empty;
        CustomerBalance = 0;
        CustomerAdvanceBalance = 0;
        InvoiceMemo = string.Empty;
        WorkDate = DateOnly.FromDateTime(DateTime.Today);
        VoucherType = _newInvoiceVoucherType;
        SelectedProcurementDocumentTitle = "발주서";
        CurrentConcurrencyStamp = string.Empty;
        LastSavedBy = string.Empty;
        LastSavedAtDisplay = string.Empty;
        VersionDisplay = "v1";
        InvoiceVersions.Clear();
        Lines.Clear();
        ClearLineInput();
        InitializeOfficeAndWarehouseDefaults();
        RecalcTotals();
        ResetPaymentSummary();
        StatusMessage = VoucherType switch
        {
            VoucherType.Purchase => "구매 전표를 작성하세요.",
            VoucherType.Procurement => "견적/발주 전표를 작성하세요.",
            _ => "판매 전표를 작성하세요."
        };
        CaptureBaselineState();
    }

    // ?? 怨좉컼 ?ㅼ젙 ?????????????????????????????????????????????????????????
    public void SetCustomer(LocalCustomer customer, bool ignoreTradeType = false)
    {
        if (!ignoreTradeType && !CanSelectCustomer(customer))
        {
            StatusMessage = IsPurchaseLikeDocument
                ? "매입 가능한 거래처만 선택할 수 있습니다."
                : "매출 가능한 거래처만 선택할 수 있습니다.";
            return;
        }

        SelectedCustomer = customer;
        CustomerName = customer.NameOriginal;
        CustomerPhone = customer.Phone;
        CustomerMobile = customer.MobilePhone;
        CustomerTradeType = customer.TradeType;
        CustomerPriceGrade = customer.PriceGrade;
        CustomerNote = customer.Notes;
        SelectedResponsibleOfficeCode = string.IsNullOrWhiteSpace(customer.ResponsibleOfficeCode)
            ? SelectedResponsibleOfficeCode
            : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(customer.ResponsibleOfficeCode, SelectedResponsibleOfficeCode);
        _ = RefreshPaymentSummaryAsync();
    }

    partial void OnVoucherTypeChanged(VoucherType value)
    {
        OnPropertyChanged(nameof(ShowPaymentAction));
        OnPropertyChanged(nameof(PaymentActionButtonText));
        OnPropertyChanged(nameof(PaymentSummaryTitleText));
        _ = RefreshPaymentSummaryAsync();
    }

    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;
    public IReadOnlyList<LocalCustomer> GetSelectableCustomers()
        => _allCustomers.Where(CanSelectCustomer).ToList();
    public List<LocalItem> GetAllItems() => _allItems;

    public bool CanSelectCustomer(LocalCustomer? customer)
    {
        if (customer is null)
            return false;

        var tradeType = (customer.TradeType ?? string.Empty).Trim();
        if (_tradeTypeRuleMap.TryGetValue(tradeType, out var rule))
            return IsPurchaseLikeDocument ? rule.AllowsPurchase : rule.AllowsSales;

        return IsPurchaseLikeDocument
            ? CustomerTradeTypes.AllowsPurchase(customer.TradeType)
            : CustomerTradeTypes.AllowsSales(customer.TradeType);
    }

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        await LoadMasterOptionsAsync();
    }

    public async Task ReloadItemsAsync()
    {
        _allItems = await _local.GetItemsAsync();
        RefreshItemSearch();
    }

    public async Task RefreshPaymentSummaryAsync()
    {
        if (SelectedCustomer is null)
        {
            ResetPaymentSummary();
            return;
        }

        var advanceBalance = await GetAdvanceBalanceAsync(SelectedCustomer.Id);
        CustomerAdvanceBalance = advanceBalance;
        PaymentSummaryAdvanceText = $"선수금 잔액 {advanceBalance:N0}";

        var invoice = await _local.GetInvoiceAsync(InvoiceId, _session);
        if (invoice is null)
        {
            PaymentSummaryContextText = $"{SelectedCustomer.NameOriginal} · 저장된 전표 없음";
            PaymentSummaryDetailText = "전표를 저장하면 수금/지급 요약이 표시됩니다.";
            OnPropertyChanged(nameof(PaymentSummaryTitleText));
            return;
        }

        var summary = GetInvoiceSettlementSummary(invoice);
        var displayNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.LocalTempNumber
            : invoice.InvoiceNumber;
        PaymentSummaryContextText = $"{displayNumber} · {invoice.InvoiceDate:yyyy-MM-dd} · 전표금액 {summary.InvoiceTotal:N0}";
        PaymentSummaryDetailText = $"{(IsPurchaseDocument ? "지급" : "수금")}누계 {summary.SettledAmount:N0} / 잔액 {summary.RemainingAmount:N0}";
        OnPropertyChanged(nameof(PaymentSummaryTitleText));
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

    private static InvoiceSettlementSummary GetInvoiceSettlementSummary(LocalInvoice invoice)
    {
        var settledAmount = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        var remainingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount);
        return new InvoiceSettlementSummary
        {
            InvoiceTotal = invoice.TotalAmount,
            SettledAmount = settledAmount,
            RemainingAmount = remainingAmount
        };
    }

    private void ResetPaymentSummary()
    {
        CustomerAdvanceBalance = 0m;
        PaymentSummaryContextText = "전표를 저장하면 수금/지급 요약이 표시됩니다.";
        PaymentSummaryDetailText = "연결 전표가 없으면 지급/수금 요약은 표시되지 않습니다.";
        PaymentSummaryAdvanceText = "선수금 잔액 0";
        OnPropertyChanged(nameof(PaymentSummaryTitleText));
    }

    public void MarkCurrentStateAsPristine()
        => CaptureBaselineState();

    public List<LocalItem> FindItemsForQuickInput(string keyword, int maxCount = 300)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return _allItems.Take(maxCount).ToList();

        return _allItems
            .Where(i =>
                i.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                i.SpecificationOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                i.MaterialNumber.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(maxCount)
            .ToList();
    }

    public void ApplyInputItem(LocalItem item)
    {
        SelectedInputItem = item;
        InputItemName = item.NameOriginal;
        InputSpec = item.SpecificationOriginal;
        InputUnit = item.Unit;
        InputMaterialNo = item.MaterialNumber;
        InputUnitPrice = ResolveUnitPrice(item);
        RecalcInputAmount();
    }

    // ?? ?쇱씤 ?낅젰 ?????????????????????????????????????????????????????????
    partial void OnInputQtyChanged(decimal value) => RecalcInputAmount();
    partial void OnInputUnitPriceChanged(decimal value) => RecalcInputAmount();
    private void RecalcInputAmount() =>
        InputLineAmount = Math.Round(InputQty * InputUnitPrice, 0, MidpointRounding.AwayFromZero);

    partial void OnSelectedInputItemChanged(LocalItem? value)
    {
        if (value is null) return;
        InputItemName = value.NameOriginal;
        InputSpec = value.SpecificationOriginal;
        InputUnit = value.Unit;
        InputMaterialNo = value.MaterialNumber;
        InputUnitPrice = ResolveUnitPrice(value);
        RecalcInputAmount();
    }

    partial void OnSelectedResponsibleOfficeCodeChanged(string value)
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(value, DomainConstants.OfficeUsenet);

        if (!string.Equals(value, officeCode, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(SelectedResponsibleOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase))
            {
                SelectedResponsibleOfficeCode = officeCode;
            }

            return;
        }

        var selectedWarehouse = Warehouses.FirstOrDefault(warehouse =>
            string.Equals(warehouse.Code, SelectedWarehouseCode, StringComparison.OrdinalIgnoreCase));

        if (selectedWarehouse is null ||
            !string.Equals(
                OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(selectedWarehouse.OfficeCode, DomainConstants.OfficeUsenet),
                officeCode,
                StringComparison.OrdinalIgnoreCase))
        {
            SelectedWarehouseCode = ResolveDefaultWarehouseCode(officeCode);
        }
    }

    private string ResolveDefaultWarehouseCode(string? officeCode)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        return Warehouses.FirstOrDefault(warehouse =>
                   string.Equals(warehouse.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))?.Code
               ?? OfficeCodeCatalog.GetMainWarehouseCode(normalizedOfficeCode);
    }

    private decimal ResolveUnitPrice(LocalItem item)
    {
        if (IsPurchaseLikeDocument)
        {
            if (item.PurchasePrice > 0) return item.PurchasePrice;
            if (item.SalePrice > 0) return item.SalePrice;
            if (item.RetailPrice > 0) return item.RetailPrice;
            return 0;
        }

        var grade = (CustomerPriceGrade ?? string.Empty).Trim();
        var priceSource = _priceGradeSourceMap.TryGetValue(grade, out var configuredSource)
            ? configuredSource
            : ResolveLegacyPriceSource(grade);

        switch (priceSource)
        {
            case SelectionOptionDefaults.PriceSourceA when item.PriceGradeA > 0:
                return item.PriceGradeA;
            case SelectionOptionDefaults.PriceSourceB when item.PriceGradeB > 0:
                return item.PriceGradeB;
            case SelectionOptionDefaults.PriceSourceC when item.PriceGradeC > 0:
                return item.PriceGradeC;
            case SelectionOptionDefaults.PriceSourceRetail when item.RetailPrice > 0:
                return item.RetailPrice;
            default:
                if (item.SalePrice > 0) return item.SalePrice;
                if (item.RetailPrice > 0) return item.RetailPrice;
                return 0;
        }
    }

    private static string ResolveLegacyPriceSource(string? priceGrade)
    {
        var grade = (priceGrade ?? string.Empty).Trim().ToUpperInvariant();
        if (grade.StartsWith("A")) return SelectionOptionDefaults.PriceSourceA;
        if (grade.StartsWith("B")) return SelectionOptionDefaults.PriceSourceB;
        if (grade.StartsWith("C")) return SelectionOptionDefaults.PriceSourceC;
        if (grade.Contains("소매", StringComparison.OrdinalIgnoreCase)) return SelectionOptionDefaults.PriceSourceRetail;
        return SelectionOptionDefaults.PriceSourceSales;
    }

    [RelayCommand]
    private void AddLine()
    {
        if (string.IsNullOrWhiteSpace(InputItemName)) return;
        var line = new InvoiceLineEditModel
        {
            ItemId = SelectedInputItem?.Id,
            ItemName = InputItemName,
            Specification = InputSpec,
            Unit = InputUnit,
            Quantity = InputQty,
            UnitPrice = InputUnitPrice,
            Remark = InputRemark,
            SerialNumber = InputSerialNumber,
            MaterialNumber = InputMaterialNo,
        };
        Lines.Add(line);
        RecalcTotals();
        ClearLineInput();
    }

    [RelayCommand]
    private void UpdateLine()
    {
        if (SelectedLine is null || string.IsNullOrWhiteSpace(InputItemName)) return;
        SelectedLine.ItemName = InputItemName;
        SelectedLine.Specification = InputSpec;
        SelectedLine.Unit = InputUnit;
        SelectedLine.Quantity = InputQty;
        SelectedLine.UnitPrice = InputUnitPrice;
        SelectedLine.Remark = InputRemark;
        SelectedLine.SerialNumber = InputSerialNumber;
        RecalcTotals();
        ClearLineInput();
        SelectedLine = null;
    }

    [RelayCommand]
    private void DeleteLine()
    {
        if (SelectedLine is null) return;
        Lines.Remove(SelectedLine);
        SelectedLine = null;
        RecalcTotals();
    }

    partial void OnSelectedLineChanged(InvoiceLineEditModel? value)
    {
        if (value is null) return;
        InputItemName = value.ItemName;
        InputSpec = value.Specification;
        InputUnit = value.Unit;
        InputQty = value.Quantity;
        InputUnitPrice = value.UnitPrice;
        InputRemark = value.Remark;
        InputSerialNumber = value.SerialNumber;
        InputMaterialNo = value.MaterialNumber;
        RecalcInputAmount();
    }

    private void ClearLineInput()
    {
        InputItemName = InputSpec = InputUnit = InputRemark = InputSerialNumber = InputMaterialNo = string.Empty;
        InputQty = 1;
        InputUnitPrice = InputLineAmount = 0;
        SelectedInputItem = null;
    }

    public void RecalcTotals()
    {
        var lineAmountSum = Lines.Sum(l => l.LineAmount);
        if (IsPurchaseLikeDocument)
        {
            SupplyAmount = lineAmountSum;
            VatAmount = Math.Round(SupplyAmount * 0.1m, 0, MidpointRounding.AwayFromZero);
            TotalAmount = SupplyAmount + VatAmount;
            return;
        }

        TotalAmount = lineAmountSum;
        SupplyAmount = Math.Round(TotalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        VatAmount = TotalAmount - SupplyAmount;
    }

    // ?? ?곹뭹 寃???????????????????????????????????????????????????????????
    partial void OnItemSearchTextChanged(string value) => RefreshItemSearch();

    private void RefreshItemSearch()
    {
        var text = ItemSearchText.Trim();
        ItemSearchResults.Clear();
        var list = string.IsNullOrEmpty(text)
            ? _allItems.Take(50)
            : _allItems.Where(i =>
                i.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                i.SpecificationOriginal.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var i in list)
            ItemSearchResults.Add(i);
    }

    // ?? ????????????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task SaveAsync()
        => await SaveCoreAsync();

    public bool HasPendingChanges => !string.Equals(
        _baselineStateSignature,
        BuildStateSignature(),
        StringComparison.Ordinal);

    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent();

    private string BuildStateSignature()
    {
        var effectiveResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            SelectedResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        var builder = new StringBuilder();
        builder.Append(SelectedCustomer?.Id.ToString("D") ?? string.Empty)
            .Append('|').Append(WorkDate.ToString("yyyy-MM-dd"))
            .Append('|').Append(VoucherType)
            .Append('|').Append(NormalizeProcurementDocumentTitle(SelectedProcurementDocumentTitle))
            .Append('|').Append(effectiveResponsibleOfficeCode)
            .Append('|').Append(SelectedWarehouseCode ?? string.Empty)
            .Append('|').Append(InvoiceMemo ?? string.Empty)
            .Append('|').Append(InputItemName ?? string.Empty)
            .Append('|').Append(InputSpec ?? string.Empty)
            .Append('|').Append(InputUnit ?? string.Empty)
            .Append('|').Append(InputQty)
            .Append('|').Append(InputUnitPrice)
            .Append('|').Append(InputRemark ?? string.Empty)
            .Append('|').Append(InputSerialNumber ?? string.Empty)
            .Append('|').Append(InputMaterialNo ?? string.Empty);

        foreach (var line in Lines)
        {
            builder.Append("||")
                .Append(line.ItemId?.ToString("D") ?? string.Empty)
                .Append('|').Append(line.ItemName ?? string.Empty)
                .Append('|').Append(line.Specification ?? string.Empty)
                .Append('|').Append(line.Unit ?? string.Empty)
                .Append('|').Append(line.Quantity)
                .Append('|').Append(line.UnitPrice)
                .Append('|').Append(line.Remark ?? string.Empty)
                .Append('|').Append(line.SerialNumber ?? string.Empty)
                .Append('|').Append(line.MaterialNumber ?? string.Empty);
        }

        return builder.ToString();
    }

    private void CaptureBaselineState()
    {
        _baselineStateSignature = BuildStateSignature();
    }

    public async Task<IReadOnlyList<LocalInvoice>> GetPreviousInvoicesAsync()
    {
        if (SelectedCustomer is null)
            return [];

        var invoices = await _local.GetInvoicesAsync(
            from: null,
            to: null,
            customerId: SelectedCustomer.Id,
            session: _session);

        return invoices
            .Where(invoice => invoice.Id != InvoiceId)
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => invoice.UpdatedAtUtc)
            .ToList();
    }

    public void ImportPreviousInvoices(IEnumerable<LocalInvoice> invoices, bool replaceExistingLines)
    {
        var selectedInvoices = invoices
            .Where(invoice => invoice is not null)
            .OrderBy(invoice => invoice.InvoiceDate)
            .ThenBy(invoice => string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.LocalTempNumber
                : invoice.InvoiceNumber)
            .ThenBy(invoice => invoice.UpdatedAtUtc)
            .ToList();

        if (selectedInvoices.Count == 0)
            return;

        if (replaceExistingLines)
        {
            Lines.Clear();
        }

        foreach (var invoice in selectedInvoices)
        {
            foreach (var line in invoice.Lines.Where(line => !line.IsDeleted))
            {
                Lines.Add(InvoiceLineEditModel.FromLocal(line));
            }
        }

        if (string.IsNullOrWhiteSpace(InvoiceMemo) && selectedInvoices.Count == 1)
            InvoiceMemo = selectedInvoices[0].Memo;

        RecalcTotals();
        StatusMessage = $"{selectedInvoices.Count}건의 이전 기록을 불러왔습니다.";
    }

    public async Task<bool> TryAutoSaveOnCloseAsync()
    {
        if (!HasPendingChanges || !HasMeaningfulDraftContent())
            return true;

        var saved = await SaveCoreAsync(
            showValidationFeedback: false,
            statusPrefix: "자동저장",
            showFailureStatus: false,
            forceOverride: false);

        if (saved || !_lastSaveWasConcurrencyConflict)
            return saved;

        AppLogger.Warn("AUTOSAVE", "Sales window close auto-save detected a concurrency conflict. Retrying with force override.");
        return await SaveCoreAsync(
            showValidationFeedback: false,
            statusPrefix: "자동저장",
            showFailureStatus: false,
            forceOverride: true);
    }

    private bool HasMeaningfulDraftContent()
    {
        if (SelectedCustomer is not null)
            return true;

        if (!string.IsNullOrWhiteSpace(InvoiceMemo))
            return true;

        if (HasMeaningfulLineInput())
            return true;

        return Lines.Any(IsMeaningfulLine);
    }

    private bool HasMeaningfulLineInput()
    {
        return SelectedInputItem is not null
            || !string.IsNullOrWhiteSpace(InputItemName)
            || !string.IsNullOrWhiteSpace(InputSpec)
            || !string.IsNullOrWhiteSpace(InputUnit)
            || !string.IsNullOrWhiteSpace(InputRemark)
            || !string.IsNullOrWhiteSpace(InputSerialNumber)
            || !string.IsNullOrWhiteSpace(InputMaterialNo)
            || InputQty != 1m
            || InputUnitPrice != 0
            || InputLineAmount != 0;
    }

    private static bool IsMeaningfulLine(InvoiceLineEditModel line)
    {
        return !string.IsNullOrWhiteSpace(line.ItemName)
            || !string.IsNullOrWhiteSpace(line.Specification)
            || !string.IsNullOrWhiteSpace(line.Unit)
            || !string.IsNullOrWhiteSpace(line.Remark)
            || !string.IsNullOrWhiteSpace(line.SerialNumber)
            || !string.IsNullOrWhiteSpace(line.MaterialNumber)
            || !string.IsNullOrWhiteSpace(line.InstallLocation)
            || line.RentalStartDate is not null
            || line.RentalEndDate is not null
            || line.Quantity != 1m
            || line.UnitPrice != 0
            || line.LineAmount != 0;
    }

    private async Task<LocalInvoice?> EnsureInvoiceReadyForOutputAsync(string actionName)
    {
        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            System.Windows.MessageBox.Show(
                StatusMessage,
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return null;
        }

        if (HasPendingChanges)
        {
            var saved = await SaveCoreAsync(showValidationFeedback: true, statusPrefix: "저장");
            if (!saved)
                return null;
        }

        var invoice = await _local.GetInvoiceAsync(InvoiceId, _session);
        if (invoice is not null)
            return invoice;

        StatusMessage = $"{actionName}할 전표를 찾을 수 없습니다.";
        System.Windows.MessageBox.Show(
            StatusMessage,
            "알림",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        return null;
    }

    private async Task<bool> SaveCoreAsync(
        bool showValidationFeedback = true,
        string statusPrefix = "저장",
        bool showFailureStatus = true,
        bool forceOverride = false)
    {
        _lastSaveWasConcurrencyConflict = false;

        if (SelectedCustomer is null)
        {
            if (showFailureStatus)
                StatusMessage = "거래처를 선택하세요.";
            if (showValidationFeedback)
                System.Windows.MessageBox.Show("거래처를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return false;
        }
        var validLines = Lines.Where(l => !string.IsNullOrWhiteSpace(l.ItemName)).ToList();
        if (!validLines.Any())
        {
            if (showFailureStatus)
                StatusMessage = "항목을 1개 이상 입력하세요.";
            if (showValidationFeedback)
                System.Windows.MessageBox.Show("항목을 1개 이상 입력하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return false;
        }

        var inv = new LocalInvoice
        {
            Id = InvoiceId,
            CustomerId = SelectedCustomer.Id,
            InvoiceDate = WorkDate,
            VoucherType = VoucherType,
            Memo = InvoiceMemo,
            ResponsibleOfficeCode = SelectedResponsibleOfficeCode,
            SourceWarehouseCode = SelectedWarehouseCode,
            ConcurrencyStamp = CurrentConcurrencyStamp,
            Lines = validLines.Select(l => l.ToLocal(InvoiceId)).ToList()
        };

        var saveContext = new InvoiceSaveContext
        {
            Username = _session.User?.Username ?? "local-user",
            Role = _session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = _session.OfficeCode,
            ForceOverride = forceOverride,
            ExpectedConcurrencyStamp = string.IsNullOrWhiteSpace(CurrentConcurrencyStamp)
                ? null
                : CurrentConcurrencyStamp
        };

        var saveResult = await _local.SaveInvoiceAsync(inv, saveContext, _session);
        if (!saveResult.Success)
        {
            _lastSaveWasConcurrencyConflict = saveResult.ConcurrencyConflict;
            LastAutoSaveFailureMessage = saveResult.Message;
            if (showFailureStatus)
                StatusMessage = saveResult.Message;
            else
                AppLogger.Warn("AUTOSAVE", $"Sales invoice auto-save failed silently. {saveResult.Message}");
            if (showValidationFeedback)
            {
                System.Windows.MessageBox.Show(
                    saveResult.Message,
                    saveResult.ConcurrencyConflict
                        ? "동시 수정 충돌"
                        : saveResult.PermissionDenied ? "권한 없음" : "저장 실패",
                    System.Windows.MessageBoxButton.OK,
                    saveResult.ConcurrencyConflict || saveResult.PermissionDenied
                        ? System.Windows.MessageBoxImage.Warning
                        : System.Windows.MessageBoxImage.Error);
            }
            return false;
        }

        await _local.WaitForServerWriteAsync();
        _lastSaveWasConcurrencyConflict = false;
        LastAutoSaveFailureMessage = string.Empty;
        var savedInvoice = await _local.GetInvoiceAsync(saveResult.SavedInvoiceId, _session);
        if (savedInvoice is not null)
        {
            InvoiceId = savedInvoice.Id;
            CurrentConcurrencyStamp = savedInvoice.ConcurrencyStamp;
            LastSavedBy = savedInvoice.LastSavedByUsername;
            LastSavedAtDisplay = savedInvoice.LastSavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            VersionDisplay = $"v{savedInvoice.VersionNumber}";
            await LoadInvoiceVersionsAsync(savedInvoice);
        }

        if (VoucherType == VoucherType.Procurement && SelectedCustomer is not null)
        {
            var company = await _local.GetCompanyProfileAsync(_session);
            if (company is not null)
            {
                var printModel = await LoadOrCreateInvoicePrintModelAsync(inv, SelectedCustomer, company);
                printModel.DocumentTitle = NormalizeProcurementDocumentTitle(SelectedProcurementDocumentTitle);
                await SaveInvoicePrintModelAsync(printModel);
            }
        }

        StatusMessage = $"{statusPrefix}되었습니다. {saveResult.Message}".Trim();
        CaptureBaselineState();
        await RefreshPaymentSummaryAsync();
        InvoiceSaved?.Invoke();
        return true;
    }

    // ?? 湲곗〈 ?꾪몴 遺덈윭?ㅺ린 (?섏젙?? ???????????????????????????????????????
    public void LoadInvoice(LocalInvoice inv)
    {
        InvoiceId = inv.Id;
        WorkDate = inv.InvoiceDate;
        VoucherType = inv.VoucherType;
        InvoiceMemo = inv.Memo;
        SelectedResponsibleOfficeCode = string.IsNullOrWhiteSpace(inv.ResponsibleOfficeCode)
            ? SelectedResponsibleOfficeCode
            : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(inv.ResponsibleOfficeCode, SelectedResponsibleOfficeCode);
        SelectedWarehouseCode = string.IsNullOrWhiteSpace(inv.SourceWarehouseCode)
            ? ResolveDefaultWarehouseCode(SelectedResponsibleOfficeCode)
            : OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(inv.SourceWarehouseCode, SelectedResponsibleOfficeCode, SelectedResponsibleOfficeCode);
        CurrentConcurrencyStamp = inv.ConcurrencyStamp;
        LastSavedBy = inv.LastSavedByUsername;
        LastSavedAtDisplay = inv.LastSavedAtUtc == default
            ? string.Empty
            : inv.LastSavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        VersionDisplay = $"v{Math.Max(1, inv.VersionNumber)}";
        SelectedProcurementDocumentTitle = "발주서";

        if (inv.VoucherType == VoucherType.Procurement)
        {
            try
            {
                var payload = _local.GetInvoicePrintPayloadAsync(inv.Id).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var saved = JsonSerializer.Deserialize<InvoicePrintModel>(payload, PrintModelJsonOptions);
                    if (saved is not null && !string.IsNullOrWhiteSpace(saved.DocumentTitle))
                        SelectedProcurementDocumentTitle = NormalizeProcurementDocumentTitle(saved.DocumentTitle);
                }
            }
            catch
            {
                SelectedProcurementDocumentTitle = "발주서";
            }
        }

        var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
            ?? _local.GetCustomerAsync(inv.CustomerId).GetAwaiter().GetResult();
        if (customer is not null) SetCustomer(customer, ignoreTradeType: true);

        Lines.Clear();
        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
        {
            Lines.Add(new InvoiceLineEditModel
            {
                Id = line.Id,
                ItemId = line.ItemId,
                ItemName = line.ItemNameOriginal,
                Specification = line.SpecificationOriginal,
                Unit = line.Unit,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Remark = line.Remark,
                SerialNumber = line.SerialNumber,
                MaterialNumber = line.MaterialNumber
            });
        }
        RecalcTotals();
        _ = LoadInvoiceVersionsAsync(inv);
        StatusMessage = VoucherType switch
        {
            VoucherType.Purchase => "구매 전표 수정 중입니다.",
            VoucherType.Procurement => "견적/발주 전표 수정 중입니다.",
            _ => "판매 전표 수정 중입니다."
        };
        CaptureBaselineState();
        _ = RefreshPaymentSummaryAsync();
    }

    // ?? ?좉퇋 ?꾪몴 ?????????????????????????????????????????????????????????
    [RelayCommand]
    private void StartNewInvoice() => NewInvoice();

    [RelayCommand]
    private async Task EditPrintOutputAsync()
    {
        try
        {
            if (IsPurchaseLikeDocument)
            {
                StatusMessage = IsProcurementDocument
                    ? "견적/발주 전표는 선택한 제목의 고정 양식으로 바로 출력합니다."
                    : "구매 전표는 고정 양식으로 바로 출력합니다.";
                return;
            }

            var invoice = await EnsureInvoiceReadyForOutputAsync("출력물 편집");
            if (invoice is null)
                return;

            var customer = SelectedCustomer;
            if (customer is null)
                return;

            var company = await _local.GetCompanyProfileAsync(_session);
            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                System.Windows.MessageBox.Show(
                    StatusMessage,
                    "알림",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var model = await LoadOrCreateInvoicePrintModelAsync(invoice, customer, company);
            var editorViewModel = new PrintEditViewModel(model, _invoicePrintService, SaveInvoicePrintModelAsync);
            var editorWindow = new PrintEditWindow(editorViewModel)
            {
                Owner = GetActiveWindow()
            };

            editorWindow.ShowDialog();
            if (editorViewModel.WasSaved)
            {
                StatusMessage = "출력물 편집 내용을 저장했습니다.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"출력물 편집 중 오류: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"출력물 편집 창을 여는 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // ?? 嫄곕옒紐낆꽭???몄뇙 ???????????????????????????????????????????????????
    [RelayCommand]
    private async Task PrintAsync()
    {
        try
        {
            if (IsProcurementDocument)
            {
                await PrintProcurementAsync();
                return;
            }

            if (IsPurchaseDocument)
            {
                await PrintPurchaseAsync();
                return;
            }

            if (!PrintStatementDocument && !PrintEstimateDocument && !PrintPaymentClaimDocument)
            {
                StatusMessage = "출력할 서류를 1개 이상 선택하세요.";
                System.Windows.MessageBox.Show(
                    "인쇄옵션에서 출력할 서류를 1개 이상 선택하세요.",
                    "알림",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var invoice = await EnsureInvoiceReadyForOutputAsync("인쇄");
            if (invoice is null)
                return;

            var customer = SelectedCustomer;
            if (customer is null)
                return;

            var company = await _local.GetCompanyProfileAsync(_session);
            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                System.Windows.MessageBox.Show(
                    StatusMessage,
                    "알림",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var selectedCodes = new List<string>();
            if (PrintPaymentClaimDocument)
            {
                var selectedFromDialog = await SelectPaymentClaimAttachmentsAsync(customer);
                if (selectedFromDialog is null)
                {
                    StatusMessage = "첨부서류 선택이 취소되어 인쇄를 중단했습니다.";
                    return;
                }

                selectedCodes.AddRange(selectedFromDialog);
            }
            else
            {
                if (PrintStatementDocument) selectedCodes.Add(AttachmentDocumentCatalog.Statement);
                if (PrintEstimateDocument) selectedCodes.Add(AttachmentDocumentCatalog.Estimate);
            }

            if (selectedCodes.Count == 0)
            {
                StatusMessage = "출력할 서류를 선택하세요.";
                return;
            }

            var documents = await BuildDocumentsForCodesAsync(
                selectedCodes,
                invoice,
                customer,
                company);

            if (documents.Count == 0)
            {
                StatusMessage = "출력 가능한 문서가 없습니다.";
                return;
            }

            var previewDocument = documents.Count == 1
                ? documents[0]
                : SupplementDocumentBuilder.MergeDocuments(documents);
            var previewViewModel = new PrintPreviewViewModel(
                previewDocument,
                _invoicePrintService,
                $"출력물_{invoice.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
            var previewWindow = new PrintPreviewWindow(previewViewModel)
            {
                Owner = GetActiveWindow()
            };

            previewWindow.ShowDialog();
            if (previewViewModel.WasPrinted)
            {
                var completed = selectedCodes
                    .Select(AttachmentDocumentCatalog.GetDisplayName)
                    .ToList();
                StatusMessage = $"인쇄 완료: {string.Join(", ", completed)}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"인쇄 중 오류: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"거래명세서 인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task PrintPurchaseAsync()
    {
        var invoice = await EnsureInvoiceReadyForOutputAsync("인쇄");
        if (invoice is null)
            return;

        var customer = SelectedCustomer;
        if (customer is null)
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        if (company is null)
        {
            StatusMessage = "회사 정보를 먼저 등록하세요.";
            System.Windows.MessageBox.Show(
                StatusMessage,
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var printModel = await LoadOrCreateInvoicePrintModelAsync(invoice, customer, company);
        printModel.PrintWithDate = true;
        printModel.PrintWithPrice = true;

        var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
        var previewViewModel = new PrintPreviewViewModel(
            previewDocument,
            _invoicePrintService,
            $"구매명세서_{invoice.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
        var previewWindow = new PrintPreviewWindow(previewViewModel)
        {
            Owner = GetActiveWindow()
        };

        previewWindow.ShowDialog();
        if (previewViewModel.WasPrinted)
            StatusMessage = "매입 명세서를 인쇄했습니다.";
    }

    private async Task PrintProcurementAsync()
    {
        var invoice = await EnsureInvoiceReadyForOutputAsync("인쇄");
        if (invoice is null)
            return;

        var customer = SelectedCustomer;
        if (customer is null)
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        if (company is null)
        {
            StatusMessage = "회사 정보를 먼저 등록하세요.";
            System.Windows.MessageBox.Show(
                StatusMessage,
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var printModel = await LoadOrCreateInvoicePrintModelAsync(invoice, customer, company);
        printModel.PrintWithDate = true;
        printModel.PrintWithPrice = true;
        printModel.DocumentTitle = NormalizeProcurementDocumentTitle(SelectedProcurementDocumentTitle);
        await SaveInvoicePrintModelAsync(printModel);

        var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
        var jobTitle = NormalizeProcurementDocumentTitle(printModel.DocumentTitle);
        var previewViewModel = new PrintPreviewViewModel(
            previewDocument,
            _invoicePrintService,
            $"{jobTitle}_{invoice.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
        var previewWindow = new PrintPreviewWindow(previewViewModel)
        {
            Owner = GetActiveWindow()
        };

        previewWindow.ShowDialog();
        if (previewViewModel.WasPrinted)
            StatusMessage = $"{jobTitle}를 인쇄했습니다.";
    }

    [RelayCommand]
    private async Task PrintTaxInvoiceAsync()
    {
        try
        {
            if (!IsSalesDocument)
                return;

            var inv = await EnsureInvoiceReadyForOutputAsync("세금계산서 인쇄");
            var company = await _local.GetCompanyProfileAsync(_session);
            if (inv is null)
                return;

            var customer = SelectedCustomer;
            if (customer is null)
                return;

            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                System.Windows.MessageBox.Show(
                    StatusMessage,
                    "알림",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var document = BuildTaxInvoicePrintDocument(inv, customer, company);
            var printed = PrintPreviewHelper.ShowPreviewAndPrint(
                document,
                "세금계산서 미리보기",
                $"세금계산서_{WorkDate:yyyyMMdd}_{customer.NameOriginal}");
            if (printed)
            {
                StatusMessage = "세금계산서를 인쇄했습니다.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"세금계산서 인쇄 중 오류: {ex.Message}";
            System.Windows.MessageBox.Show(
                $"세금계산서 인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<InvoicePrintModel> LoadOrCreateInvoicePrintModelAsync(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company)
    {
        var payload = await _local.GetInvoicePrintPayloadAsync(invoice.Id);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<InvoicePrintModel>(payload, PrintModelJsonOptions);
                if (saved is not null)
                {
                    saved.InvoiceId = invoice.Id;
                    if (invoice.VoucherType == VoucherType.Procurement)
                        saved.DocumentTitle = NormalizeProcurementDocumentTitle(saved.DocumentTitle);
                    if (saved.Lines.Count == 0)
                    {
                        saved.Lines = _invoicePrintService
                            .CreateDefaultModel(invoice, customer, company, PrintWithDate, PrintWithPrice)
                            .Lines;
                    }

                    return saved;
                }
            }
            catch
            {
                // Corrupted payload falls back to default model.
            }
        }

        var model = _invoicePrintService.CreateDefaultModel(invoice, customer, company, PrintWithDate, PrintWithPrice);
        if (invoice.VoucherType == VoucherType.Procurement)
            model.DocumentTitle = NormalizeProcurementDocumentTitle(SelectedProcurementDocumentTitle);
        return model;
    }

    private async Task SaveInvoicePrintModelAsync(InvoicePrintModel model)
    {
        var payload = JsonSerializer.Serialize(model, PrintModelJsonOptions);
        await _local.SaveInvoicePrintPayloadAsync(model.InvoiceId, payload);
    }

    private static string NormalizeProcurementDocumentTitle(string? title)
    {
        var normalized = (title ?? string.Empty).Trim();
        return normalized is "납품서" or "의뢰서" ? normalized : "발주서";
    }

    private async Task<List<string>?> SelectPaymentClaimAttachmentsAsync(LocalCustomer customer)
    {
        var customerKey = BuildAttachmentCustomerKey(customer);
        var loadedStates = await _local.GetAttachmentSelectionsAsync(customerKey);
        var defaultStates = BuildDefaultAttachmentSelections();
        var initialStates = loadedStates.Count == 0 ? defaultStates : MergeAttachmentStates(defaultStates, loadedStates);
        EnforceMainPrintOptionSelections(initialStates);
        var lockedCodes = BuildLockedAttachmentCodes();

        var dialogViewModel = new AttachmentSelectionDialogViewModel(
            AttachmentDocumentCatalog.OrderedDocuments,
            initialStates,
            AttachmentDocumentCatalog.PaymentClaim,
            lockedCodes);
        var window = new AttachmentSelectionWindow(dialogViewModel)
        {
            Owner = GetActiveWindow()
        };

        var dialogResult = window.ShowDialog();
        if (dialogResult != true || !dialogViewModel.WasConfirmed)
            return null;

        var finalStates = dialogViewModel.GetSelectionStates();
        await _local.SaveAttachmentSelectionsAsync(customerKey, finalStates);

        return dialogViewModel.GetCheckedStatesInOrder()
            .Select(s => s.DocCode)
            .ToList();
    }

    private async Task<List<System.Windows.Documents.FixedDocument>> BuildDocumentsForCodesAsync(
        IReadOnlyList<string> codes,
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company)
    {
        var documents = new List<System.Windows.Documents.FixedDocument>();
        InvoicePrintModel? statementPrintModel = null;
        var orderedAttachmentNames = codes
            .Select(AttachmentDocumentCatalog.GetDisplayName)
            .ToList();

        foreach (var code in codes)
        {
            switch (code)
            {
                case AttachmentDocumentCatalog.Statement:
                    statementPrintModel ??= await LoadOrCreateInvoicePrintModelAsync(invoice, customer, company);
                    statementPrintModel.PrintWithDate = PrintWithDate;
                    statementPrintModel.PrintWithPrice = PrintWithPrice;
                    documents.Add(_invoicePrintService.BuildFixedDocument(statementPrintModel));
                    break;
                case AttachmentDocumentCatalog.Estimate:
                    documents.Add(SupplementDocumentBuilder.BuildEstimateDocument(invoice, customer, company));
                    break;
                case AttachmentDocumentCatalog.PaymentClaim:
                    documents.Add(SupplementDocumentBuilder.BuildPaymentClaimDocument(
                        invoice,
                        customer,
                        company,
                        orderedAttachmentNames));
                    break;
                default:
                    // Non-core attachments are metadata only (printed in payment-claim attachment list).
                    // They are not generated as separate print pages.
                    break;
            }
        }

        return documents;
    }

    private List<AttachmentSelectionState> BuildDefaultAttachmentSelections()
    {
        var states = AttachmentDocumentCatalog.OrderedDocuments
            .Select(d => new AttachmentSelectionState
            {
                DocCode = d.Code,
                IsChecked = false,
                OrderIndex = null
            })
            .ToList();

        var order = 1;
        if (PrintStatementDocument)
            SetAttachmentState(states, AttachmentDocumentCatalog.Statement, true, order++);
        if (PrintEstimateDocument)
            SetAttachmentState(states, AttachmentDocumentCatalog.Estimate, true, order++);

        // Policy: payment claim is required in this flow and always placed last.
        SetAttachmentState(states, AttachmentDocumentCatalog.PaymentClaim, true, order);
        return states;
    }

    private static List<AttachmentSelectionState> MergeAttachmentStates(
        IReadOnlyList<AttachmentSelectionState> defaults,
        IReadOnlyList<AttachmentSelectionState> loaded)
    {
        var loadedByCode = loaded.ToDictionary(s => s.DocCode, StringComparer.OrdinalIgnoreCase);
        var merged = defaults
            .Select(d =>
            {
                if (loadedByCode.TryGetValue(d.DocCode, out var fromSaved))
                {
                    return new AttachmentSelectionState
                    {
                        DocCode = d.DocCode,
                        IsChecked = fromSaved.IsChecked,
                        OrderIndex = fromSaved.OrderIndex
                    };
                }

                return new AttachmentSelectionState
                {
                    DocCode = d.DocCode,
                    IsChecked = d.IsChecked,
                    OrderIndex = d.OrderIndex
                };
            })
            .ToList();

        var claimState = merged.First(s =>
            string.Equals(s.DocCode, AttachmentDocumentCatalog.PaymentClaim, StringComparison.OrdinalIgnoreCase));
        claimState.IsChecked = true;

        if (!claimState.OrderIndex.HasValue)
        {
            var maxOrder = merged.Where(s => s.OrderIndex.HasValue).Select(s => s.OrderIndex!.Value).DefaultIfEmpty(0).Max();
            claimState.OrderIndex = maxOrder + 1;
        }

        return merged;
    }

    private static void SetAttachmentState(
        List<AttachmentSelectionState> states,
        string code,
        bool isChecked,
        int? orderIndex)
    {
        var state = states.First(s => string.Equals(s.DocCode, code, StringComparison.OrdinalIgnoreCase));
        state.IsChecked = isChecked;
        state.OrderIndex = isChecked ? orderIndex : null;
    }

    private void EnforceMainPrintOptionSelections(List<AttachmentSelectionState> states)
    {
        var byCode = states.ToDictionary(s => s.DocCode, StringComparer.OrdinalIgnoreCase);

        EnsureState(byCode, AttachmentDocumentCatalog.Statement);
        EnsureState(byCode, AttachmentDocumentCatalog.Estimate);
        EnsureState(byCode, AttachmentDocumentCatalog.PaymentClaim);

        if (PrintStatementDocument)
            byCode[AttachmentDocumentCatalog.Statement].IsChecked = true;
        if (PrintEstimateDocument)
            byCode[AttachmentDocumentCatalog.Estimate].IsChecked = true;

        byCode[AttachmentDocumentCatalog.PaymentClaim].IsChecked = true;

        var ordered = byCode.Values
            .Where(s => s.IsChecked &&
                        !string.Equals(s.DocCode, AttachmentDocumentCatalog.Statement, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.DocCode, AttachmentDocumentCatalog.Estimate, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(s.DocCode, AttachmentDocumentCatalog.PaymentClaim, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.OrderIndex ?? int.MaxValue)
            .ThenBy(s => s.DocCode)
            .ToList();

        var order = 1;
        if (byCode[AttachmentDocumentCatalog.Statement].IsChecked)
            byCode[AttachmentDocumentCatalog.Statement].OrderIndex = order++;
        else
            byCode[AttachmentDocumentCatalog.Statement].OrderIndex = null;

        if (byCode[AttachmentDocumentCatalog.Estimate].IsChecked)
            byCode[AttachmentDocumentCatalog.Estimate].OrderIndex = order++;
        else
            byCode[AttachmentDocumentCatalog.Estimate].OrderIndex = null;

        byCode[AttachmentDocumentCatalog.PaymentClaim].OrderIndex = order++;

        foreach (var extra in ordered)
            extra.OrderIndex = order++;

        states.Clear();
        states.AddRange(byCode.Values);
    }

    private static void EnsureState(
        Dictionary<string, AttachmentSelectionState> byCode,
        string code)
    {
        if (byCode.ContainsKey(code))
            return;

        byCode[code] = new AttachmentSelectionState
        {
            DocCode = code,
            IsChecked = false,
            OrderIndex = null
        };
    }

    private List<string> BuildLockedAttachmentCodes()
    {
        var locked = new List<string> { AttachmentDocumentCatalog.PaymentClaim };
        if (PrintStatementDocument)
            locked.Add(AttachmentDocumentCatalog.Statement);
        if (PrintEstimateDocument)
            locked.Add(AttachmentDocumentCatalog.Estimate);

        return locked;
    }

    private static string BuildAttachmentCustomerKey(LocalCustomer customer)
    {
        if (customer.Id != Guid.Empty)
            return $"customer-id:{customer.Id:N}";

        var nameKey = (customer.NameOriginal ?? string.Empty).Trim();
        return $"customer-name:{nameKey}";
    }

    private static System.Windows.Window? GetActiveWindow()
    {
        return System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(window => window.IsActive);
    }

    private static System.Windows.Documents.FlowDocument BuildTaxInvoicePrintDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company)
    {
        var document = new System.Windows.Documents.FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("맑은 고딕"),
            FontSize = 10.5,
            Background = System.Windows.Media.Brushes.White,
            Foreground = System.Windows.Media.Brushes.Black
        };

        document.Blocks.Add(new System.Windows.Documents.Paragraph(
            new System.Windows.Documents.Bold(
                new System.Windows.Documents.Run("세금계산서 (공급자 보관용)")))
        {
            FontSize = 22,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#444444")),
            TextAlignment = System.Windows.TextAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        });

        var headerParagraph = new System.Windows.Documents.Paragraph
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 8)
        };
        headerParagraph.Inlines.Add(new System.Windows.Documents.Bold(
            new System.Windows.Documents.Run($"작성일: {invoice.InvoiceDate:yyyy-MM-dd}")));
        headerParagraph.Inlines.Add(new System.Windows.Documents.Run(
            $"    발행유형: {(invoice.VoucherType == VoucherType.Sales ? "세금계산서" : "계산서")}"));
        document.Blocks.Add(headerParagraph);

        var partyTable = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        partyTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(100) });
        partyTable.Columns.Add(new System.Windows.Documents.TableColumn());
        partyTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(110) });
        partyTable.Columns.Add(new System.Windows.Documents.TableColumn());

        var partyGroup = new System.Windows.Documents.TableRowGroup();
        partyTable.RowGroups.Add(partyGroup);

        AddDualInfoRow(
            partyGroup,
            "공급자 상호", company.TradeName,
            "공급받는자 상호", customer.NameOriginal);
        AddDualInfoRow(
            partyGroup,
            "공급자 사업자번호", company.BusinessNumber,
            "공급받는자 사업자번호", customer.BusinessNumber);
        AddDualInfoRow(
            partyGroup,
            "공급자 대표자", company.Representative,
            "공급받는자 대표자", customer.Representative);
        AddDualInfoRow(
            partyGroup,
            "공급자 주소", company.Address,
            "공급받는자 주소", customer.Address);
        AddDualInfoRow(
            partyGroup,
            "공급자 업태/종목", $"{company.BusinessType} {company.BusinessItem}".Trim(),
            "공급받는자 업태/종목", $"{customer.BusinessType} {customer.BusinessItem}".Trim());

        document.Blocks.Add(partyTable);

        var linesTable = new System.Windows.Documents.Table
        {
            CellSpacing = 0,
            Margin = new System.Windows.Thickness(0, 0, 0, 10)
        };
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(40) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(236) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(150) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(44) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(68) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(84) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(78) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(56) });

        var lineGroup = new System.Windows.Documents.TableRowGroup();
        linesTable.RowGroups.Add(lineGroup);

        var headerRow = new System.Windows.Documents.TableRow();
        headerRow.Cells.Add(CreateHeaderCell("순번"));
        headerRow.Cells.Add(CreateHeaderCell("품명"));
        headerRow.Cells.Add(CreateHeaderCell("규격"));
        headerRow.Cells.Add(CreateHeaderCell("수량"));
        headerRow.Cells.Add(CreateHeaderCell("단가"));
        headerRow.Cells.Add(CreateHeaderCell("공급가액"));
        headerRow.Cells.Add(CreateHeaderCell("세액"));
        headerRow.Cells.Add(CreateHeaderCell("비고"));
        lineGroup.Rows.Add(headerRow);

        var lines = invoice.Lines.Where(l => !l.IsDeleted).ToList();
        if (lines.Count == 0)
        {
            var emptyRow = new System.Windows.Documents.TableRow();
            emptyRow.Cells.Add(CreateDataCell("1", System.Windows.TextAlignment.Center));
            emptyRow.Cells.Add(CreateDataCell(string.Empty));
            emptyRow.Cells.Add(CreateDataCell(string.Empty));
            emptyRow.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell(string.Empty));
            lineGroup.Rows.Add(emptyRow);
        }
        else
        {
            decimal distributedSupply = 0;
            decimal distributedVat = 0;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                decimal lineSupply;
                decimal lineVat;
                if (i == lines.Count - 1)
                {
                    lineSupply = invoice.SupplyAmount - distributedSupply;
                    lineVat = invoice.VatAmount - distributedVat;
                }
                else
                {
                    lineSupply = Math.Round(line.LineAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
                    lineVat = line.LineAmount - lineSupply;
                    distributedSupply += lineSupply;
                    distributedVat += lineVat;
                }

                var row = new System.Windows.Documents.TableRow();
                row.Cells.Add(CreateDataCell((i + 1).ToString(), System.Windows.TextAlignment.Center));
                row.Cells.Add(CreateDataCell(line.ItemNameOriginal, noWrap: true, autoShrink: true));
                row.Cells.Add(CreateDataCell(line.SpecificationOriginal, noWrap: true, autoShrink: true));
                row.Cells.Add(CreateDataCell($"{line.Quantity:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{line.UnitPrice:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{lineSupply:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{lineVat:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell(line.Remark));
                lineGroup.Rows.Add(row);
            }
        }

        var minimumTaxRows = 14;
        for (var i = lineGroup.Rows.Count - 1; i < minimumTaxRows; i++)
        {
            var row = new System.Windows.Documents.TableRow();
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Center));
            row.Cells.Add(CreateDataCell(string.Empty));
            row.Cells.Add(CreateDataCell(string.Empty));
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Right));
            row.Cells.Add(CreateDataCell(string.Empty));
            lineGroup.Rows.Add(row);
        }

        document.Blocks.Add(linesTable);

        var totals = new System.Windows.Documents.Paragraph
        {
            TextAlignment = System.Windows.TextAlignment.Right,
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };
        totals.Inlines.Add(new System.Windows.Documents.Run($"공급가액: {invoice.SupplyAmount:N0}원   "));
        totals.Inlines.Add(new System.Windows.Documents.Run($"세액: {invoice.VatAmount:N0}원   "));
        totals.Inlines.Add(new System.Windows.Documents.Bold(
            new System.Windows.Documents.Run($"합계금액: {invoice.TotalAmount:N0}원")));
        document.Blocks.Add(totals);

        if (!string.IsNullOrWhiteSpace(company.BankAccountText))
        {
            document.Blocks.Add(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run($"비고: {company.BankAccountText}"))
            {
                Margin = new System.Windows.Thickness(0, 10, 0, 0)
            });
        }

        return document;
    }

    private static void AddInfoRow(
        System.Windows.Documents.TableRowGroup group,
        string label,
        string value)
    {
        var row = new System.Windows.Documents.TableRow();
        row.Cells.Add(CreateInfoLabelCell(label));
        row.Cells.Add(CreateInfoValueCell(value));
        group.Rows.Add(row);
    }

    private static void AddDualInfoRow(
        System.Windows.Documents.TableRowGroup group,
        string leftLabel,
        string leftValue,
        string rightLabel,
        string rightValue)
    {
        var row = new System.Windows.Documents.TableRow();
        row.Cells.Add(CreateInfoLabelCell(leftLabel));
        row.Cells.Add(CreateInfoValueCell(leftValue));
        row.Cells.Add(CreateInfoLabelCell(rightLabel));
        row.Cells.Add(CreateInfoValueCell(rightValue));
        group.Rows.Add(row);
    }

    private static System.Windows.Documents.TableCell CreateInfoLabelCell(string text)
    {
        return new System.Windows.Documents.TableCell(
            new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text ?? string.Empty))
            {
                Margin = new System.Windows.Thickness(0)
            })
        {
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            FontWeight = System.Windows.FontWeights.Bold,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F2F2F2")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BABABA")),
            BorderThickness = new System.Windows.Thickness(0.8)
        };
    }

    private static System.Windows.Documents.TableCell CreateInfoValueCell(string text)
    {
        return new System.Windows.Documents.TableCell(
            new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text ?? string.Empty))
            {
                Margin = new System.Windows.Thickness(0)
            })
        {
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BABABA")),
            BorderThickness = new System.Windows.Thickness(0.8)
        };
    }

    private static System.Windows.Documents.TableCell CreateHeaderCell(string text)
    {
        return new System.Windows.Documents.TableCell(
            new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text))
            {
                Margin = new System.Windows.Thickness(0)
            })
        {
            Padding = new System.Windows.Thickness(6, 5, 6, 5),
            FontWeight = System.Windows.FontWeights.Bold,
            TextAlignment = System.Windows.TextAlignment.Center,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F2F2F2")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BABABA")),
            BorderThickness = new System.Windows.Thickness(0.8)
        };
    }

    private static System.Windows.Documents.TableCell CreateDataCell(
        string text,
        System.Windows.TextAlignment align = System.Windows.TextAlignment.Left,
        bool noWrap = false,
        bool autoShrink = false)
    {
        System.Windows.Documents.Block content;
        if (noWrap)
        {
            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = text ?? string.Empty,
                FontSize = 8.8,
                TextAlignment = align,
                TextWrapping = System.Windows.TextWrapping.NoWrap,
                TextTrimming = autoShrink ? System.Windows.TextTrimming.None : System.Windows.TextTrimming.CharacterEllipsis
            };

            if (autoShrink)
            {
                textBlock.HorizontalAlignment = align switch
                {
                    System.Windows.TextAlignment.Center => System.Windows.HorizontalAlignment.Center,
                    System.Windows.TextAlignment.Right => System.Windows.HorizontalAlignment.Right,
                    _ => System.Windows.HorizontalAlignment.Left
                };
            }

            content = new System.Windows.Documents.BlockUIContainer(
                autoShrink
                    ? new System.Windows.Controls.Viewbox
                    {
                        Stretch = System.Windows.Media.Stretch.Uniform,
                        StretchDirection = System.Windows.Controls.StretchDirection.DownOnly,
                        Child = textBlock
                    }
                    : textBlock)
            {
                Margin = new System.Windows.Thickness(0)
            };
        }
        else
        {
            content = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text ?? string.Empty))
            {
                Margin = new System.Windows.Thickness(0)
            };
        }

        return new System.Windows.Documents.TableCell(content)
        {
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            TextAlignment = align,
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BABABA")),
            BorderThickness = new System.Windows.Thickness(0.6)
        };
    }
}
