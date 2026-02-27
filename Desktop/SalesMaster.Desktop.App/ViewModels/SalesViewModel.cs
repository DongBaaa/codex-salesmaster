using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.Views;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class SalesViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;
    private readonly PrintTemplateCatalogService _templateCatalog = new();
    private readonly 외부 리포팅 도구TemplatePrintService _fastReportTemplatePrint = new();
    private List<LocalItem> _allItems = new();
    private List<LocalCustomer> _allCustomers = new();
    private bool _suppressTemplateSettingWrite;
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);

    public event Action? InvoiceSaved;

    // ?? 怨좉컼 ?뺣낫 (?곷떒) ??????????????????????????????????????????????????
    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerPhone = string.Empty;
    [ObservableProperty] private string _customerMobile = string.Empty;
    [ObservableProperty] private string _customerPriceGrade = string.Empty;
    [ObservableProperty] private string _customerNote = string.Empty;
    [ObservableProperty] private decimal _customerBalance;   // 珥?誘몄닔湲?
    // ?? ?꾪몴 ?ㅻ뜑 ?????????????????????????????????????????????????????????
    [ObservableProperty] private Guid _invoiceId = Guid.NewGuid();
    [ObservableProperty] private DateOnly _workDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _invoiceMemo = string.Empty;
    [ObservableProperty] private VoucherType _voucherType = VoucherType.Sales;
    public Array VoucherTypes => Enum.GetValues<VoucherType>();

    // ?? ?쇱씤 ?낅젰 (?④굔) ??????????????????????????????????????????????????
    [ObservableProperty] private string _inputItemName = string.Empty;
    [ObservableProperty] private string _inputSpec = string.Empty;
    [ObservableProperty] private decimal _inputQty = 1;
    [ObservableProperty] private string _inputUnit = string.Empty;
    [ObservableProperty] private decimal _inputUnitPrice;
    [ObservableProperty] private decimal _inputLineAmount;
    [ObservableProperty] private string _inputRemark = string.Empty;
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
    [ObservableProperty] private bool _editTemplateOnPrint;
    [ObservableProperty] private string _printType = "거래명1/2";
    public string[] PrintTypes { get; } = ["거래명1/2", "거래명A4", "영수증출력", "출고증A4"];
    public ObservableCollection<PrintTemplateOption> StatementTemplates { get; } = new();
    [ObservableProperty] private PrintTemplateOption? _selectedStatementTemplate;
    private const string 외부 리포팅 도구DesignerExeSettingKey = "Print.외부 리포팅 도구DesignerExePath";

    // ?? ?곹깭 ?????????????????????????????????????????????????????????????
    [ObservableProperty] private string _statusMessage = string.Empty;

    public SalesViewModel(
        LocalStateService local,
        StatementPrintService print,
        IPrintService invoicePrintService,
        SessionState session)
    {
        _local = local;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
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
        _allCustomers = await _local.GetCustomersAsync();
        _allItems = await _local.GetItemsAsync();
        await LoadStatementTemplatesAsync();
        RefreshItemSearch();
    }

    partial void OnSelectedStatementTemplateChanged(PrintTemplateOption? value)
    {
        if (_suppressTemplateSettingWrite || value is null)
            return;

        _ = _local.SetSettingAsync(PrintTemplateCatalogService.DefaultStatementTemplateSettingKey, value.Id);
    }

    [RelayCommand]
    private async Task ReloadStatementTemplatesAsync()
    {
        await LoadStatementTemplatesAsync();
    }

    private async Task LoadStatementTemplatesAsync()
    {
        var templates = _templateCatalog.GetStatementTemplates();
        StatementTemplates.Clear();
        foreach (var template in templates)
            StatementTemplates.Add(template);

        var savedTemplateId = await _local.GetSettingAsync(PrintTemplateCatalogService.DefaultStatementTemplateSettingKey);
        var selected = PrintTemplateCatalogService.ResolvePreferredTemplate(templates, savedTemplateId, PrintType);

        _suppressTemplateSettingWrite = true;
        SelectedStatementTemplate = selected;
        _suppressTemplateSettingWrite = false;

        if (selected is not null && !string.Equals(savedTemplateId, selected.Id, StringComparison.OrdinalIgnoreCase))
            await _local.SetSettingAsync(PrintTemplateCatalogService.DefaultStatementTemplateSettingKey, selected.Id);
    }

    public void NewInvoice()
    {
        InvoiceId = Guid.NewGuid();
        InvoiceMemo = string.Empty;
        WorkDate = DateOnly.FromDateTime(DateTime.Today);
        VoucherType = VoucherType.Sales;
        Lines.Clear();
        ClearLineInput();
        RecalcTotals();
        StatusMessage = "???꾪몴瑜??묒꽦?섏꽭??";
    }

    // ?? 怨좉컼 ?ㅼ젙 ?????????????????????????????????????????????????????????
    public void SetCustomer(LocalCustomer customer)
    {
        SelectedCustomer = customer;
        CustomerName = customer.NameOriginal;
        CustomerPhone = customer.Phone;
        CustomerMobile = customer.MobilePhone;
        CustomerPriceGrade = customer.PriceGrade;
        CustomerNote = customer.Notes;
    }

    public LocalStateService LocalStateService => _local;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;
    public List<LocalItem> GetAllItems() => _allItems;

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync();
    }

    public async Task ReloadItemsAsync()
    {
        _allItems = await _local.GetItemsAsync();
        RefreshItemSearch();
    }

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

    private decimal ResolveUnitPrice(LocalItem item)
    {
        var grade = (CustomerPriceGrade ?? string.Empty).Trim().ToUpperInvariant();
        if (grade.StartsWith("A") && item.PriceGradeA > 0) return item.PriceGradeA;
        if (grade.StartsWith("B") && item.PriceGradeB > 0) return item.PriceGradeB;
        if (grade.StartsWith("C") && item.PriceGradeC > 0) return item.PriceGradeC;
        if (item.SalePrice > 0) return item.SalePrice;
        if (item.RetailPrice > 0) return item.RetailPrice;
        return 0;
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
        InputMaterialNo = value.MaterialNumber;
        RecalcInputAmount();
    }

    private void ClearLineInput()
    {
        InputItemName = InputSpec = InputUnit = InputRemark = InputMaterialNo = string.Empty;
        InputQty = 1;
        InputUnitPrice = InputLineAmount = 0;
        SelectedInputItem = null;
    }

    public void RecalcTotals()
    {
        TotalAmount = Lines.Sum(l => l.LineAmount);
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

    public bool HasPendingChanges =>
        !string.IsNullOrWhiteSpace(InputItemName) ||
        !string.IsNullOrWhiteSpace(InputSpec) ||
        !string.IsNullOrWhiteSpace(InputUnit) ||
        !string.IsNullOrWhiteSpace(InputMaterialNo) ||
        !string.IsNullOrWhiteSpace(InputRemark) ||
        InputQty != 1 ||
        InputUnitPrice > 0 ||
        !string.IsNullOrWhiteSpace(InvoiceMemo) ||
        Lines.Any(l => !string.IsNullOrWhiteSpace(l.ItemName));

    public async Task<bool> TryAutoSaveOnCloseAsync()
    {
        if (!HasPendingChanges) return true;
        return await SaveCoreAsync(showValidationFeedback: false, statusPrefix: "자동저장");
    }

    private async Task<bool> SaveCoreAsync(bool showValidationFeedback = true, string statusPrefix = "저장")
    {
        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            if (showValidationFeedback)
                System.Windows.MessageBox.Show(StatusMessage, "알림", System.Windows.MessageBoxButton.OK);
            return false;
        }
        var validLines = Lines.Where(l => !string.IsNullOrWhiteSpace(l.ItemName)).ToList();
        if (!validLines.Any())
        {
            StatusMessage = "항목을 1개 이상 입력하세요.";
            if (showValidationFeedback)
                System.Windows.MessageBox.Show(StatusMessage, "알림", System.Windows.MessageBoxButton.OK);
            return false;
        }
        var inv = new LocalInvoice
        {
            Id = InvoiceId,
            CustomerId = SelectedCustomer.Id,
            InvoiceDate = WorkDate,
            VoucherType = VoucherType,
            Memo = InvoiceMemo,
            Lines = validLines.Select(l => l.ToLocal(InvoiceId)).ToList()
        };
        await _local.SaveInvoiceAsync(inv);
        StatusMessage = $"{statusPrefix}되었습니다.";
        InvoiceSaved?.Invoke();
        // ???꾪몴 踰덊샇濡??댁뼱 ?낅젰 媛?ν븯?꾨줉
        InvoiceId = Guid.NewGuid();
        return true;
    }

    // ?? 湲곗〈 ?꾪몴 遺덈윭?ㅺ린 (?섏젙?? ???????????????????????????????????????
    public void LoadInvoice(LocalInvoice inv)
    {
        InvoiceId = inv.Id;
        WorkDate = inv.InvoiceDate;
        VoucherType = inv.VoucherType;
        InvoiceMemo = inv.Memo;

        var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId);
        if (customer is not null) SetCustomer(customer);

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
                MaterialNumber = line.MaterialNumber
            });
        }
        RecalcTotals();
        StatusMessage = "?꾪몴 ?섏젙 以묒엯?덈떎.";
    }

    // ?? ?좉퇋 ?꾪몴 ?????????????????????????????????????????????????????????
    [RelayCommand]
    private void StartNewInvoice() => NewInvoice();

    [RelayCommand]
    private async Task EditPrintOutputAsync()
    {
        try
        {
            if (SelectedCustomer is null)
            {
                StatusMessage = "거래처를 선택하세요.";
                return;
            }

            var invoice = await _local.GetInvoiceAsync(InvoiceId);
            if (invoice is null)
            {
                StatusMessage = "먼저 전표를 저장한 후 출력물을 편집하세요.";
                return;
            }

            var company = await _local.GetCompanyProfileAsync();
            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                return;
            }

            var model = await LoadOrCreateInvoicePrintModelAsync(invoice, SelectedCustomer, company);
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
            if (SelectedCustomer is null)
            {
                StatusMessage = "거래처를 선택하세요.";
                return;
            }

            var invoice = await _local.GetInvoiceAsync(InvoiceId);
            if (invoice is null)
            {
                StatusMessage = "먼저 저장 후 인쇄하세요.";
                return;
            }

            var company = await _local.GetCompanyProfileAsync();
            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                return;
            }

            var printModel = await LoadOrCreateInvoicePrintModelAsync(invoice, SelectedCustomer, company);
            printModel.PrintWithDate = PrintWithDate;
            printModel.PrintWithPrice = PrintWithPrice;

            var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
            var previewViewModel = new PrintPreviewViewModel(
                previewDocument,
                _invoicePrintService,
                $"거래명세서_{invoice.InvoiceDate:yyyyMMdd}_{SelectedCustomer.NameOriginal}");
            var previewWindow = new PrintPreviewWindow(previewViewModel)
            {
                Owner = GetActiveWindow()
            };

            previewWindow.ShowDialog();
            if (previewViewModel.WasPrinted)
            {
                StatusMessage = "프린터 선택 후 인쇄를 완료했습니다.";
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

    [RelayCommand]
    private async Task PrintTaxInvoiceAsync()
    {
        try
        {
            if (SelectedCustomer is null)
            {
                StatusMessage = "거래처를 선택하세요.";
                return;
            }

            var inv = await _local.GetInvoiceAsync(InvoiceId);
            var company = await _local.GetCompanyProfileAsync();
            if (inv is null)
            {
                StatusMessage = "먼저 저장 후 인쇄하세요.";
                return;
            }

            if (company is null)
            {
                StatusMessage = "회사 정보를 먼저 등록하세요.";
                return;
            }

            var selectedTemplate = SelectedStatementTemplate;
            var runtimeTemplate = ResolveRuntimeTemplateForPrintType(selectedTemplate);
            if (runtimeTemplate is { IsBuiltIn: false })
            {
                try
                {
                    var opened = _fastReportTemplatePrint.ShowPreviewAndPrint(
                        runtimeTemplate.TemplatePath,
                        inv,
                        SelectedCustomer,
                        company,
                        PrintWithDate,
                        PrintWithPrice,
                        $"세금계산서_{WorkDate:yyyyMMdd}_{SelectedCustomer.NameOriginal}");
                    if (opened)
                    {
                        StatusMessage = $"세금계산서 PDF 미리보기를 열었습니다. ({runtimeTemplate.DisplayName})";
                        return;
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"양식 인쇄 실패로 기본 양식으로 전환합니다. ({ex.Message})";
                }
            }

            var document = BuildTaxInvoicePrintDocument(inv, SelectedCustomer, company);
            var printed = PrintPreviewHelper.ShowPreviewAndPrint(
                document,
                "세금계산서 미리보기",
                $"세금계산서_{WorkDate:yyyyMMdd}_{SelectedCustomer.NameOriginal}");
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

    private static System.Windows.Documents.FlowDocument BuildStatementPrintDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        NativeStatementLayoutType layout,
        bool printWithDate,
        bool printWithPrice)
    {
        return StatementDocumentBuilder.BuildStatementPrintDocument(
            invoice,
            customer,
            company,
            layout,
            printWithDate,
            printWithPrice);
    }

    private NativeStatementLayoutType ResolveLayoutForCurrentPrint(PrintTemplateOption? selectedTemplate)
    {
        if (selectedTemplate?.BuiltInLayout is { } builtIn)
            return builtIn;

        return PrintTemplateCatalogService.ResolveBuiltInLayoutFromPrintType(PrintType);
    }

    private PrintTemplateOption? ResolveRuntimeTemplateForPrintType(PrintTemplateOption? selectedTemplate)
    {
        var templates = StatementTemplates.ToList();
        if (templates.Count == 0)
            templates = _templateCatalog.GetStatementTemplates().ToList();

        if (selectedTemplate is { IsBuiltIn: false })
            return selectedTemplate;

        return PrintTemplateCatalogService.ResolveLegacyTemplateForPrintType(templates, PrintType) ?? selectedTemplate;
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

        return _invoicePrintService.CreateDefaultModel(invoice, customer, company, PrintWithDate, PrintWithPrice);
    }

    private async Task SaveInvoicePrintModelAsync(InvoicePrintModel model)
    {
        var payload = JsonSerializer.Serialize(model, PrintModelJsonOptions);
        await _local.SaveInvoicePrintPayloadAsync(model.InvoiceId, payload);
    }

    private static System.Windows.Window? GetActiveWindow()
    {
        return System.Windows.Application.Current?.Windows
            .OfType<System.Windows.Window>()
            .FirstOrDefault(window => window.IsActive);
    }

    private async Task<bool> TryOpenTemplateDesignerAsync(PrintTemplateOption template)
    {
        if (string.IsNullOrWhiteSpace(template.TemplatePath) || !File.Exists(template.TemplatePath))
            return false;

        var designerPath = await ResolveDesignerExecutablePathAsync();
        if (string.IsNullOrWhiteSpace(designerPath))
            return false;

        var openPath = ResolveTemplatePathForDesigner(template.TemplatePath);
        if (TryOpenTemplateWithAssociation(openPath))
            return true;

        var psi = new ProcessStartInfo
        {
            FileName = designerPath,
            Arguments = $"\"{openPath}\"",
            WorkingDirectory = Path.GetDirectoryName(openPath) ?? Path.GetDirectoryName(designerPath) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };

        Process.Start(psi);
        return true;
    }

    private static string ResolveTemplatePathForDesigner(string templatePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath))
            return templatePath;

        if (string.Equals(Path.GetExtension(templatePath), ".fr3", StringComparison.OrdinalIgnoreCase))
        {
            var frxPath = Path.ChangeExtension(templatePath, ".frx");
            if (File.Exists(frxPath))
                return frxPath;
        }

        return templatePath;
    }

    private static bool TryOpenTemplateWithAssociation(string templatePath)
    {
        if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = templatePath,
                WorkingDirectory = Path.GetDirectoryName(templatePath) ?? Environment.CurrentDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ResolveDesignerExecutablePathAsync()
    {
        var savedPath = await _local.GetSettingAsync(외부 리포팅 도구DesignerExeSettingKey);
        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            return savedPath;

        var detectedPath = TryDetect외부 리포팅 도구DesignerPath();
        if (!string.IsNullOrWhiteSpace(detectedPath))
        {
            await _local.SetSettingAsync(외부 리포팅 도구DesignerExeSettingKey, detectedPath);
            return detectedPath;
        }

        return null;
    }

    private static string? TryDetect외부 리포팅 도구DesignerPath()
    {
        var candidates = new List<string>();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(pf))
        {
            candidates.Add(Path.Combine(pf, "외부 리포팅 도구", "외부 리포팅 도구.Net", "Designer.exe"));
            candidates.Add(Path.Combine(pf, "외부 리포팅 도구", "외부 리포팅 도구.Net Designer", "Designer.exe"));
            candidates.Add(Path.Combine(pf, "외부 리포팅 도구.Net", "Designer.exe"));
        }

        if (!string.IsNullOrWhiteSpace(pf86))
        {
            candidates.Add(Path.Combine(pf86, "외부 리포팅 도구", "외부 리포팅 도구.Net", "Designer.exe"));
            candidates.Add(Path.Combine(pf86, "외부 리포팅 도구", "외부 리포팅 도구.Net Designer", "Designer.exe"));
            candidates.Add(Path.Combine(pf86, "외부 리포팅 도구.Net", "Designer.exe"));
        }

        var fixedPath = candidates.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(fixedPath))
            return fixedPath;

        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "외부 리포팅 도구");
        if (Directory.Exists(localRoot))
        {
            var localPath = Directory.EnumerateFiles(localRoot, "Designer.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(localPath))
                return localPath;
        }

        return null;
    }

    private static System.Windows.Documents.FlowDocument BuildTaxInvoicePrintDocument(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company)
    {
        var document = new System.Windows.Documents.FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("맑은 고딕"),
            FontSize = 10.5
        };

        document.Blocks.Add(new System.Windows.Documents.Paragraph(
            new System.Windows.Documents.Bold(
                new System.Windows.Documents.Run("세금계산서 (공급자 보관용)")))
        {
            FontSize = 22,
            Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8B0000")),
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
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(40) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn());
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(85) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(70) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(90) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(110) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(95) });
        linesTable.Columns.Add(new System.Windows.Documents.TableColumn { Width = new System.Windows.GridLength(110) });

        var lineGroup = new System.Windows.Documents.TableRowGroup();
        linesTable.RowGroups.Add(lineGroup);

        var headerRow = new System.Windows.Documents.TableRow();
        headerRow.Cells.Add(CreateHeaderCell("월"));
        headerRow.Cells.Add(CreateHeaderCell("일"));
        headerRow.Cells.Add(CreateHeaderCell("품목"));
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
            emptyRow.Cells.Add(CreateDataCell(invoice.InvoiceDate.Month.ToString(), System.Windows.TextAlignment.Center));
            emptyRow.Cells.Add(CreateDataCell(invoice.InvoiceDate.Day.ToString(), System.Windows.TextAlignment.Center));
            emptyRow.Cells.Add(CreateDataCell(string.Empty));
            emptyRow.Cells.Add(CreateDataCell(string.Empty));
            emptyRow.Cells.Add(CreateDataCell("0", System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell("0", System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell("0", System.Windows.TextAlignment.Right));
            emptyRow.Cells.Add(CreateDataCell("0", System.Windows.TextAlignment.Right));
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
                row.Cells.Add(CreateDataCell(invoice.InvoiceDate.Month.ToString(), System.Windows.TextAlignment.Center));
                row.Cells.Add(CreateDataCell(invoice.InvoiceDate.Day.ToString(), System.Windows.TextAlignment.Center));
                row.Cells.Add(CreateDataCell(line.ItemNameOriginal));
                row.Cells.Add(CreateDataCell(line.SpecificationOriginal));
                row.Cells.Add(CreateDataCell($"{line.Quantity:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{line.UnitPrice:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{lineSupply:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell($"{lineVat:N0}", System.Windows.TextAlignment.Right));
                row.Cells.Add(CreateDataCell(line.Remark));
                lineGroup.Rows.Add(row);
            }
        }

        var minimumTaxRows = 10;
        for (var i = lineGroup.Rows.Count - 1; i < minimumTaxRows; i++)
        {
            var row = new System.Windows.Documents.TableRow();
            row.Cells.Add(CreateDataCell(string.Empty, System.Windows.TextAlignment.Center));
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
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EEF3FA")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A3B1C2")),
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
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A3B1C2")),
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
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DCE8F8")),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8FA5BD")),
            BorderThickness = new System.Windows.Thickness(0.8)
        };
    }

    private static System.Windows.Documents.TableCell CreateDataCell(
        string text,
        System.Windows.TextAlignment align = System.Windows.TextAlignment.Left)
    {
        return new System.Windows.Documents.TableCell(
            new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text ?? string.Empty))
            {
                Margin = new System.Windows.Thickness(0)
            })
        {
            Padding = new System.Windows.Thickness(6, 4, 6, 4),
            TextAlignment = align,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#B7C5D5")),
            BorderThickness = new System.Windows.Thickness(0.6)
        };
    }
}
