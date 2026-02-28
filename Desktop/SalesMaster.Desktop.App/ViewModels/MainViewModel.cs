using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.Views;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly SessionState _session;
    private readonly PrintTemplateCatalogService _templateCatalog = new();
    private readonly 외부 리포팅 도구TemplatePrintService _fastReportTemplatePrint = new();
    private readonly IPrintService _invoicePrintService = new WpfInvoicePrintService();
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LegacyDataMigrationService _legacyMigrationService;
    private const string LegacySourceDbPathSettingKey = "LegacyMigration.SourceDbPath";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";

    // ?? Status bar ?????????????????????????????????????????????????????????
    [ObservableProperty] private string _syncStatus = "동기화 대기";
    [ObservableProperty] private string _currentUserDisplay = string.Empty;

    // ?? Tabs ???????????????????????????????????????????????????????????????
    [ObservableProperty] private int _selectedTabIndex;

    // Dashboard card metrics
    [ObservableProperty] private decimal _dashboardMonthlySales;
    [ObservableProperty] private decimal _dashboardReceivable;
    [ObservableProperty] private int _dashboardCustomerCount;
    [ObservableProperty] private int _dashboardSafetyStockAlerts;
    [ObservableProperty] private int _dashboardMonthlyInvoiceCount;
    [ObservableProperty] private decimal _dashboardMonthlyAverageSales;
    [ObservableProperty] private decimal _dashboardSalesTrendPercent;

    // ?? ?꾪몴 紐⑸줉 ?? Left panel (嫄곕옒泥??꾪꽣) ??????????????????????????????
    private List<LocalCustomer> _allCustomers = new();
    public ObservableCollection<LocalCustomer> FilteredCustomers { get; } = new();
    [ObservableProperty] private string _customerFilterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    private LocalCustomer? _selectedCustomerFilter;
    public bool HasSelectedCustomer => SelectedCustomerFilter is not null;

    // ?? 嫄곕옒泥??몃씪???몄쭛 (?곗륫 ?⑤꼸) ??????????????????????????????????????
    private bool _suppressCustomerSave;
    [ObservableProperty] private string _editCustBizNumber = string.Empty;
    [ObservableProperty] private string _editCustPhone = string.Empty;
    [ObservableProperty] private string _editCustDept = string.Empty;
    [ObservableProperty] private string _editCustContactPerson = string.Empty;
    [ObservableProperty] private string _editCustAddress = string.Empty;
    [ObservableProperty] private string _editCustNotes = string.Empty;

    partial void OnEditCustBizNumberChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustPhoneChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustDeptChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustContactPersonChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustAddressChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustNotesChanged(string value) => _ = AutoSaveCustomerAsync();

    private async Task AutoSaveCustomerAsync()
    {
        if (_suppressCustomerSave) return;
        var customer = SelectedCustomerFilter;
        if (customer is null) return;
        customer.BusinessNumber = EditCustBizNumber;
        customer.Phone = EditCustPhone;
        customer.Department = EditCustDept;
        customer.ContactPerson = EditCustContactPerson;
        customer.Address = EditCustAddress;
        customer.Notes = EditCustNotes;
        customer.NameMatchKey = customer.NameOriginal.ToUpperInvariant();
        await _local.UpsertCustomerAsync(customer);
    }

    // ?? ?꾪몴 紐⑸줉 ?? Bottom panel (?좏깮???꾪몴 ?쇱씤 誘몃━蹂닿린) ???????????????
    public ObservableCollection<InvoiceLineEditModel> PreviewLines { get; } = new();
    [ObservableProperty] private decimal _previewSupplyAmount;
    [ObservableProperty] private decimal _previewVatAmount;
    [ObservableProperty] private decimal _previewTotalAmount;

    // ?? ?꾪몴 紐⑸줉 ?? Right panel (嫄곕옒泥??뺣낫 誘몃━蹂닿린) ?????????????????????
    [ObservableProperty] private string _previewCustomerName = string.Empty;
    [ObservableProperty] private string _previewCustomerBizNumber = string.Empty;
    [ObservableProperty] private string _previewCustomerPhone = string.Empty;
    [ObservableProperty] private string _previewCustomerAddress = string.Empty;
    [ObservableProperty] private string _previewCustomerNotes = string.Empty;
    [ObservableProperty] private string _previewCustomerDepartment = string.Empty;
    [ObservableProperty] private string _previewCustomerContactPerson = string.Empty;

    // ?? Invoice List (?꾪몴 紐⑸줉) ????????????????????????????????????????????
    public ObservableCollection<InvoiceListRow> InvoiceRows { get; } = new();
    public ObservableCollection<FavoriteInvoiceQuickItem> FavoriteInvoices { get; } = new();
    [ObservableProperty] private InvoiceListRow? _selectedInvoiceRow;
    [ObservableProperty] private FavoriteInvoiceQuickItem? _selectedFavoriteInvoice;
    [ObservableProperty] private DateOnly _filterFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateOnly _filterTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _filterCustomerName = string.Empty;
    [ObservableProperty] private string _selectedVoucherTypeFilter = "전체";
    [ObservableProperty] private string _filterMinAmountText = string.Empty;
    [ObservableProperty] private string _filterMaxAmountText = string.Empty;
    public IReadOnlyList<string> VoucherTypeFilterOptions { get; } = ["전체", "매출", "매입", "발주", "경비", "수금"];
    private bool _suppressFilterAutoSave;
    private const string InvoiceFilterFromSettingKey = "InvoiceFilter.From";
    private const string InvoiceFilterToSettingKey = "InvoiceFilter.To";
    private const string InvoiceFilterCustomerSettingKey = "InvoiceFilter.CustomerName";
    private const string InvoiceFilterVoucherTypeSettingKey = "InvoiceFilter.VoucherType";
    private const string InvoiceFilterMinAmountSettingKey = "InvoiceFilter.MinAmount";
    private const string InvoiceFilterMaxAmountSettingKey = "InvoiceFilter.MaxAmount";
    private const string FavoriteInvoiceIdsSettingKey = "InvoiceFavorites.Ids";

    // ?? Invoice Editor (?꾪몴 ?묒꽦) ??????????????????????????????????????????
    [ObservableProperty] private Guid _editInvoiceId = Guid.NewGuid();
    [ObservableProperty] private LocalCustomer? _editCustomer;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private DateOnly _editInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private VoucherType _editVoucherType = VoucherType.Sales;
    [ObservableProperty] private string _editMemo = string.Empty;
    [ObservableProperty] private decimal _editTotalAmount;
    [ObservableProperty] private decimal _editSupplyAmount;
    [ObservableProperty] private decimal _editVatAmount;
    public ObservableCollection<InvoiceLineEditModel> EditLines { get; } = new();
    public Array VoucherTypes => Enum.GetValues<VoucherType>();

    // ?? Payment Tab (?섍툑 ?낅젰) ????????????????????????????????????????????
    [ObservableProperty] private InvoiceListRow? _paymentInvoice;
    public ObservableCollection<PaymentRowModel> PaymentRows { get; } = new();
    [ObservableProperty] private decimal _paymentTotalPaid;
    [ObservableProperty] private decimal _paymentBalance;

    // ?? Statement tab (嫄곕옒紐낆꽭?? ?????????????????????????????????????????
    [ObservableProperty] private InvoiceListRow? _statementInvoice;
    public ObservableCollection<PrintTemplateOption> StatementTemplates { get; } = new();
    [ObservableProperty] private PrintTemplateOption? _selectedStatementTemplate;
    private bool _suppressStatementTemplateSave;
    [ObservableProperty] private bool _editTemplateOnPrint;
    [ObservableProperty] private string _fastReportDesignerPath = "(미설정)";
    private const string 외부 리포팅 도구DesignerExeSettingKey = "Print.외부 리포팅 도구DesignerExePath";

    // ?? Company settings (?뚯궗 ?ㅼ젙) ??????????????????????????????????????
    [ObservableProperty] private string _companyTradeName = string.Empty;
    [ObservableProperty] private string _companyRepresentative = string.Empty;
    [ObservableProperty] private string _companyBusinessNumber = string.Empty;
    [ObservableProperty] private string _companyBusinessType = string.Empty;
    [ObservableProperty] private string _companyBusinessItem = string.Empty;
    [ObservableProperty] private string _companyAddress = string.Empty;
    [ObservableProperty] private string _companyContactNumber = string.Empty;
    [ObservableProperty] private string _companyEmail = string.Empty;
    [ObservableProperty] private string _companyBankAccountText = string.Empty;
    [ObservableProperty] private byte[]? _companyStampImage;
    [ObservableProperty] private string _companyStampImagePath = "(?놁쓬)";
    [ObservableProperty] private string _legacySourceDbPath = string.Empty;
    [ObservableProperty] private string _legacyCustomerExcelPath = string.Empty;
    [ObservableProperty] private string _legacyItemExcelPath = string.Empty;
    [ObservableProperty] private string _legacyMigrationStatus = "원본 데이터 추출/가져오기 대기";
    private Guid _companyProfileId = Guid.NewGuid();

    public MainViewModel(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        SessionState session)
    {
        _local = local;
        _sync = sync;
        _backup = backup;
        _session = session;
        _legacyMigrationService = new LegacyDataMigrationService(local);

        _sync.SyncStatusChanged += HandleSyncStatusChanged;
        var offlineTag = session.IsOfflineMode ? " [?ㅽ봽?쇱씤]" : string.Empty;
        CurrentUserDisplay = $"{session.User?.Username} ({session.User?.Role}){offlineTag}";
    }

    private void HandleSyncStatusChanged(string status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => SyncStatus = status);
        else
            SyncStatus = status;

        AppLogger.Info("SYNC-UI", status);
    }

    public async Task LoadAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceFilterSettingsAsync();
        await LoadInvoiceListAsync();
        await LoadCompanyProfileAsync();
        await LoadStatementTemplatesAsync();
        await Load외부 리포팅 도구DesignerPathAsync();
        await LoadLegacyMigrationSettingsAsync();
        if (!_session.IsOfflineMode)
            _sync.Start(15);
        else
            SyncStatus = "?ㅽ봽?쇱씤 紐⑤뱶 ???쒕쾭 ?곌껐 ???먮룞 ?숆린?붾맗?덈떎";
    }

    partial void OnSelectedStatementTemplateChanged(PrintTemplateOption? value)
    {
        if (_suppressStatementTemplateSave || value is null)
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
        var selected = PrintTemplateCatalogService.ResolvePreferredTemplate(templates, savedTemplateId, "거래명1/2");

        _suppressStatementTemplateSave = true;
        SelectedStatementTemplate = selected;
        _suppressStatementTemplateSave = false;

        if (selected is not null && !string.Equals(savedTemplateId, selected.Id, StringComparison.OrdinalIgnoreCase))
            await _local.SetSettingAsync(PrintTemplateCatalogService.DefaultStatementTemplateSettingKey, selected.Id);
    }

    private async Task Load외부 리포팅 도구DesignerPathAsync()
    {
        var savedPath = await _local.GetSettingAsync(외부 리포팅 도구DesignerExeSettingKey);
        if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
        {
            외부 리포팅 도구DesignerPath = savedPath;
            return;
        }

        var detectedPath = TryDetect외부 리포팅 도구DesignerPath();
        if (!string.IsNullOrWhiteSpace(detectedPath))
        {
            외부 리포팅 도구DesignerPath = detectedPath;
            await _local.SetSettingAsync(외부 리포팅 도구DesignerExeSettingKey, detectedPath);
            return;
        }

        외부 리포팅 도구DesignerPath = "(미설정)";
    }

    [RelayCommand]
    private async Task Select외부 리포팅 도구DesignerPathAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "외부 리포팅 도구 Designer 실행파일 선택",
            Filter = "실행 파일|*.exe"
        };

        if (dlg.ShowDialog() != true)
            return;

        외부 리포팅 도구DesignerPath = dlg.FileName;
        await _local.SetSettingAsync(외부 리포팅 도구DesignerExeSettingKey, dlg.FileName);
    }

    [RelayCommand]
    private async Task EditStatementTemplateAsync()
    {
        var template = SelectedStatementTemplate ?? await ResolveDefaultStatementTemplateAsync();
        if (template is null)
        {
            System.Windows.MessageBox.Show("편집할 양식이 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        if (template.IsBuiltIn)
        {
            System.Windows.MessageBox.Show("내장 양식은 편집할 수 없습니다. 외부 .fr3/.frx 양식을 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        if (string.IsNullOrWhiteSpace(template.TemplatePath) || !File.Exists(template.TemplatePath))
        {
            System.Windows.MessageBox.Show("선택한 양식 파일을 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
            return;
        }

        await OpenTemplateInDesignerAsync(template);
    }

    private async Task<bool> OpenTemplateInDesignerAsync(PrintTemplateOption template)
    {
        var designerPath = await ResolveDesignerExecutablePathAsync();
        if (string.IsNullOrWhiteSpace(designerPath))
        {
            System.Windows.MessageBox.Show(
                "외부 리포팅 도구 Designer 실행파일 경로가 필요합니다.\n[디자이너 경로 설정] 버튼으로 .exe 파일을 지정하세요.",
                "알림",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return false;
        }

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

    [RelayCommand]
    private void OpenTemplateFolder()
    {
        var templateDirectory = _templateCatalog.ResolveTemplateDirectory();
        if (string.IsNullOrWhiteSpace(templateDirectory) || !Directory.Exists(templateDirectory))
        {
            System.Windows.MessageBox.Show("양식 폴더를 찾을 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = templateDirectory,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void OpenMigrationGuide()
    {
        var templateDirectory = _templateCatalog.ResolveTemplateDirectory();
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(templateDirectory))
        {
            candidates.Add(Path.Combine(templateDirectory, "FR3_TO_FRX_전환_워크플로우.md"));
            candidates.Add(Path.Combine(templateDirectory, "FR3_TO_FRX_수동변환_체크리스트.md"));
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "양식", "FR3_TO_FRX_전환_워크플로우.md"));

        var guidePath = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(guidePath))
        {
            System.Windows.MessageBox.Show("전환 가이드 파일을 찾을 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = guidePath,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private async Task PrepareManualConversionAsync()
    {
        var template = SelectedStatementTemplate ?? await ResolveDefaultStatementTemplateAsync();
        if (template is null || template.IsBuiltIn)
        {
            System.Windows.MessageBox.Show("외부 양식(.fr3/.frx)을 먼저 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        if (string.IsNullOrWhiteSpace(template.TemplatePath) || !File.Exists(template.TemplatePath))
        {
            System.Windows.MessageBox.Show("선택한 양식 파일을 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
            return;
        }

        var templateDirectory = _templateCatalog.ResolveTemplateDirectory();
        if (string.IsNullOrWhiteSpace(templateDirectory))
        {
            System.Windows.MessageBox.Show("양식 폴더를 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
            return;
        }

        var baseName = Path.GetFileNameWithoutExtension(template.TemplatePath);
        var ext = Path.GetExtension(template.TemplatePath).ToLowerInvariant();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        var workDir = Path.Combine(templateDirectory, "_manual_conversion", $"{baseName}_{stamp}");
        Directory.CreateDirectory(workDir);

        var sourceFileName = Path.GetFileName(template.TemplatePath);
        var copiedSourcePath = Path.Combine(workDir, sourceFileName);
        File.Copy(template.TemplatePath, copiedSourcePath, overwrite: true);

        var targetFrxName = $"{baseName}.frx";
        var checklistPath = Path.Combine(workDir, "CHECKLIST.md");
        var checklist = $"""
# FR3 -> FRX 수동 변환 체크리스트

## 대상 양식
- 원본: `{sourceFileName}`
- 작업폴더: `{workDir}`
- 목표 결과물: `{targetFrxName}`

## 작업 순서
1. 외부 리포팅 도구 Designer에서 `{sourceFileName}`를 엽니다.
2. 데이터 밴드/필드 바인딩을 확인합니다.
3. `Save As`로 `{targetFrxName}`를 저장합니다.
4. 저장한 `.frx`를 `양식` 폴더 루트로 복사합니다.
5. 앱에서 `양식 다시읽기` 후 해당 양식을 선택해 미리보기를 검증합니다.

## 검증 항목
- PDF가 0 byte가 아니다.
- 거래처/품목/수량/금액/합계가 정상 위치에 출력된다.
- 페이지 수/줄바꿈/표 경계가 무너지지 않는다.
- 인쇄 미리보기와 실제 인쇄 결과가 동일하다.

## 결과 기록
- `FR3_TO_FRX_우선순위.csv`의 해당 행 `Status/Notes`를 업데이트합니다.
""";
        await File.WriteAllTextAsync(checklistPath, checklist);

        Process.Start(new ProcessStartInfo
        {
            FileName = workDir,
            UseShellExecute = true
        });

        var designerPath = await ResolveDesignerExecutablePathAsync();
        if (!string.IsNullOrWhiteSpace(designerPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = designerPath,
                Arguments = $"\"{copiedSourcePath}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true
            });
        }

        var modeLabel = ext == ".fr3" ? "FR3 수동 변환 작업" : "FRX 수동 편집 작업";
        System.Windows.MessageBox.Show(
            $"{modeLabel} 폴더를 준비했습니다.\n{workDir}",
            "완료",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async Task<string?> ResolveDesignerExecutablePathAsync()
    {
        if (!string.IsNullOrWhiteSpace(외부 리포팅 도구DesignerPath) &&
            !string.Equals(외부 리포팅 도구DesignerPath, "(미설정)", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(외부 리포팅 도구DesignerPath))
        {
            return 외부 리포팅 도구DesignerPath;
        }

        await Load외부 리포팅 도구DesignerPathAsync();
        if (!string.IsNullOrWhiteSpace(외부 리포팅 도구DesignerPath) &&
            !string.Equals(외부 리포팅 도구DesignerPath, "(미설정)", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(외부 리포팅 도구DesignerPath))
        {
            return 외부 리포팅 도구DesignerPath;
        }

        return null;
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

    // ?? Customer Filter (Left Panel) ???????????????????????????????????????
    private async Task LoadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync();
        DashboardCustomerCount = _allCustomers.Count;
        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        var text = CustomerFilterText.Trim();
        FilteredCustomers.Clear();
        var filtered = string.IsNullOrEmpty(text)
            ? _allCustomers
            : _allCustomers.Where(c => c.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var c in filtered)
            FilteredCustomers.Add(c);
    }

    partial void OnCustomerFilterTextChanged(string value) => ApplyCustomerFilter();
    partial void OnSelectedCustomerFilterChanged(LocalCustomer? value)
    {
        _suppressCustomerSave = true;
        try
        {
            PreviewCustomerName = value?.NameOriginal ?? string.Empty;
            EditCustBizNumber = value?.BusinessNumber ?? string.Empty;
            EditCustPhone = value?.Phone ?? string.Empty;
            EditCustDept = value?.Department ?? string.Empty;
            EditCustContactPerson = value?.ContactPerson ?? string.Empty;
            EditCustAddress = value?.Address ?? string.Empty;
            EditCustNotes = value?.Notes ?? string.Empty;
        }
        finally { _suppressCustomerSave = false; }

        HandleInvoiceFilterChanged();
    }

    partial void OnFilterFromChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterToChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterCustomerNameChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnSelectedVoucherTypeFilterChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMinAmountTextChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMaxAmountTextChanged(string value) => HandleInvoiceFilterChanged();

    [RelayCommand]
    private async Task ResetInvoiceFiltersAsync()
    {
        _suppressFilterAutoSave = true;
        FilterFrom = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        FilterTo = DateOnly.FromDateTime(DateTime.Today);
        FilterCustomerName = string.Empty;
        SelectedVoucherTypeFilter = "전체";
        FilterMinAmountText = string.Empty;
        FilterMaxAmountText = string.Empty;
        SelectedCustomerFilter = null;
        _suppressFilterAutoSave = false;

        await PersistInvoiceFiltersAsync();
        await LoadInvoiceListAsync();
    }

    [RelayCommand]
    private void ClearCustomerFilter()
    {
        SelectedCustomerFilter = null;
    }

    [RelayCommand]
    private void SelectRecentInvoice()
    {
        if (InvoiceRows.Count == 0)
            return;

        SelectedInvoiceRow = InvoiceRows[0];
    }

    [RelayCommand]
    private async Task ToggleInvoiceFavoriteAsync()
    {
        if (SelectedInvoiceRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기에 등록할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var ids = await GetFavoriteInvoiceIdsAsync();
        if (ids.Contains(SelectedInvoiceRow.Id))
            ids.Remove(SelectedInvoiceRow.Id);
        else
            ids.Insert(0, SelectedInvoiceRow.Id);

        await SaveFavoriteInvoiceIdsAsync(ids);
        await LoadInvoiceFavoritesAsync();
    }

    [RelayCommand]
    private async Task OpenFavoriteInvoiceAsync()
    {
        if (SelectedFavoriteInvoice is null)
        {
            System.Windows.MessageBox.Show("이동할 즐겨찾기 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetId = SelectedFavoriteInvoice.InvoiceId;
        var targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);

        if (targetRow is null)
        {
            var invoice = await _local.GetInvoiceAsync(targetId);
            if (invoice is null)
            {
                System.Windows.MessageBox.Show("선택한 즐겨찾기 전표를 찾을 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            _suppressFilterAutoSave = true;
            SelectedCustomerFilter = _allCustomers.FirstOrDefault(c => c.Id == invoice.CustomerId);
            FilterCustomerName = string.Empty;
            SelectedVoucherTypeFilter = "전체";
            FilterMinAmountText = string.Empty;
            FilterMaxAmountText = string.Empty;
            FilterFrom = new DateOnly(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month, 1);
            FilterTo = new DateOnly(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month, DateTime.DaysInMonth(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month));
            _suppressFilterAutoSave = false;

            await PersistInvoiceFiltersAsync();
            await LoadInvoiceListAsync();
            targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);
        }

        if (targetRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기 전표를 현재 목록에서 찾지 못했습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        SelectedTabIndex = 0;
        SelectedInvoiceRow = targetRow;
    }

    // ?? Invoice Preview (on selection) ?????????????????????????????????????
    partial void OnSelectedInvoiceRowChanged(InvoiceListRow? value)
        => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(InvoiceListRow? row)
    {
        PreviewLines.Clear();
        PreviewTotalAmount = 0;
        PreviewSupplyAmount = 0;
        PreviewVatAmount = 0;

        if (row is null) return;

        var inv = await _local.GetInvoiceAsync(row.Id);
        if (inv is null) return;

        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
            PreviewLines.Add(InvoiceLineEditModel.FromLocal(line));

        PreviewTotalAmount = inv.TotalAmount;
        PreviewSupplyAmount = inv.SupplyAmount;
        PreviewVatAmount = inv.VatAmount;

        // 醫뚯륫 嫄곕옒泥섍? ?좏깮?섏? ?딆? 寃쎌슦?먮쭔 ?곗륫 ?⑤꼸 怨좉컼 ?뺣낫 ?낅뜲?댄듃
        if (SelectedCustomerFilter is null)
        {
            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId);
            if (customer is not null)
            {
                PreviewCustomerName = customer.NameOriginal;
                _suppressCustomerSave = true;
                try
                {
                    EditCustBizNumber = customer.BusinessNumber;
                    EditCustPhone = customer.Phone;
                    EditCustDept = customer.Department;
                    EditCustContactPerson = customer.ContactPerson;
                    EditCustAddress = customer.Address;
                    EditCustNotes = customer.Notes;
                }
                finally { _suppressCustomerSave = false; }
            }
        }
    }

    // ?? Invoice List ??????????????????????????????????????????????????????
    [RelayCommand]
    private async Task LoadInvoiceListAsync()
    {
        Guid? customerId = SelectedCustomerFilter?.Id;
        var invoices = await _local.GetInvoicesAsync(FilterFrom, FilterTo, customerId);
        var customerMap = _allCustomers.ToDictionary(c => c.Id, c => c.NameOriginal);
        IEnumerable<LocalInvoice> filteredInvoices = invoices;

        if (!string.IsNullOrWhiteSpace(FilterCustomerName))
        {
            var needle = FilterCustomerName.Trim();
            filteredInvoices = filteredInvoices.Where(inv =>
            {
                var name = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : string.Empty;
                return name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (!string.Equals(SelectedVoucherTypeFilter, "전체", StringComparison.OrdinalIgnoreCase))
        {
            var selectedType = SelectedVoucherTypeFilter switch
            {
                "매출" => VoucherType.Sales,
                "매입" => VoucherType.Purchase,
                "발주" => VoucherType.Procurement,
                "경비" => VoucherType.Expense,
                "수금" => VoucherType.Collection,
                _ => (VoucherType?)null
            };

            if (selectedType is { } type)
                filteredInvoices = filteredInvoices.Where(inv => inv.VoucherType == type);
        }

        var minAmount = ParseAmountFilter(FilterMinAmountText);
        var maxAmount = ParseAmountFilter(FilterMaxAmountText);
        if (minAmount.HasValue)
            filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount >= minAmount.Value);
        if (maxAmount.HasValue)
            filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount <= maxAmount.Value);

        var finalInvoices = filteredInvoices
            .OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.InvoiceNumber)
            .ToList();

        InvoiceRows.Clear();
        foreach (var inv in finalInvoices)
        {
            var custName = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : "(미지정)";
            InvoiceRows.Add(InvoiceListRow.From(inv, custName));
        }

        await RefreshDashboardMetricsAsync();
        await LoadInvoiceFavoritesAsync();
    }

    private async Task RefreshDashboardMetricsAsync(IEnumerable<LocalInvoice>? invoices = null)
    {
        var sourceInvoices = invoices?.ToList() ?? await _local.GetInvoicesAsync();
        var now = DateOnly.FromDateTime(DateTime.Today);
        var prevMonthDate = now.AddMonths(-1);

        var monthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == now.Year
                     && i.InvoiceDate.Month == now.Month)
            .Sum(i => i.TotalAmount);

        var previousMonthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == prevMonthDate.Year
                     && i.InvoiceDate.Month == prevMonthDate.Month)
            .Sum(i => i.TotalAmount);

        var monthlyInvoiceCount = sourceInvoices.Count(i =>
            i.InvoiceDate.Year == now.Year && i.InvoiceDate.Month == now.Month);

        DashboardMonthlySales = monthlySales;
        DashboardMonthlyInvoiceCount = monthlyInvoiceCount;
        DashboardMonthlyAverageSales = monthlyInvoiceCount == 0
            ? 0
            : Math.Round(monthlySales / monthlyInvoiceCount, 0, MidpointRounding.AwayFromZero);
        DashboardSalesTrendPercent = previousMonthlySales == 0
            ? (monthlySales > 0 ? 100m : 0m)
            : Math.Round(((monthlySales - previousMonthlySales) / previousMonthlySales) * 100m, 1, MidpointRounding.AwayFromZero);

        DashboardReceivable = sourceInvoices.Sum(i =>
            i.TotalAmount - i.Payments.Where(p => !p.IsDeleted).Sum(p => p.Amount));

        var items = await _local.GetItemsAsync();
        DashboardSafetyStockAlerts = items.Count(i =>
            i.SafetyStock > 0 && i.CurrentStock <= i.SafetyStock);
        DashboardCustomerCount = _allCustomers.Count;
    }

    private void HandleInvoiceFilterChanged()
    {
        if (_suppressFilterAutoSave)
            return;

        _ = ApplyInvoiceFiltersAsync();
    }

    private async Task ApplyInvoiceFiltersAsync()
    {
        await PersistInvoiceFiltersAsync();
        await LoadInvoiceListAsync();
    }

    private async Task PersistInvoiceFiltersAsync()
    {
        await _local.SetSettingAsync(InvoiceFilterFromSettingKey, FilterFrom.ToString("yyyy-MM-dd"));
        await _local.SetSettingAsync(InvoiceFilterToSettingKey, FilterTo.ToString("yyyy-MM-dd"));
        await _local.SetSettingAsync(InvoiceFilterCustomerSettingKey, FilterCustomerName ?? string.Empty);
        await _local.SetSettingAsync(InvoiceFilterVoucherTypeSettingKey, SelectedVoucherTypeFilter ?? "전체");
        await _local.SetSettingAsync(InvoiceFilterMinAmountSettingKey, FilterMinAmountText ?? string.Empty);
        await _local.SetSettingAsync(InvoiceFilterMaxAmountSettingKey, FilterMaxAmountText ?? string.Empty);
    }

    private async Task LoadInvoiceFilterSettingsAsync()
    {
        _suppressFilterAutoSave = true;

        var fromValue = await _local.GetSettingAsync(InvoiceFilterFromSettingKey);
        var toValue = await _local.GetSettingAsync(InvoiceFilterToSettingKey);
        var customerNameValue = await _local.GetSettingAsync(InvoiceFilterCustomerSettingKey);
        var voucherTypeValue = await _local.GetSettingAsync(InvoiceFilterVoucherTypeSettingKey);
        var minAmountValue = await _local.GetSettingAsync(InvoiceFilterMinAmountSettingKey);
        var maxAmountValue = await _local.GetSettingAsync(InvoiceFilterMaxAmountSettingKey);

        if (DateOnly.TryParse(fromValue, out var parsedFrom))
            FilterFrom = parsedFrom;
        if (DateOnly.TryParse(toValue, out var parsedTo))
            FilterTo = parsedTo;

        FilterCustomerName = customerNameValue ?? string.Empty;
        SelectedVoucherTypeFilter = VoucherTypeFilterOptions.Contains(voucherTypeValue ?? string.Empty)
            ? voucherTypeValue!
            : "전체";
        FilterMinAmountText = minAmountValue ?? string.Empty;
        FilterMaxAmountText = maxAmountValue ?? string.Empty;

        _suppressFilterAutoSave = false;
    }

    private static decimal? ParseAmountFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
            return value;
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return value;

        return null;
    }

    private async Task<List<Guid>> GetFavoriteInvoiceIdsAsync()
    {
        var raw = await _local.GetSettingAsync(FavoriteInvoiceIdsSettingKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new List<Guid>();

        var ids = new List<Guid>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(token, out var id))
                continue;
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    private Task SaveFavoriteInvoiceIdsAsync(IEnumerable<Guid> ids)
    {
        var payload = string.Join(',', ids.Select(id => id.ToString("D")));
        return _local.SetSettingAsync(FavoriteInvoiceIdsSettingKey, payload);
    }

    private async Task LoadInvoiceFavoritesAsync()
    {
        var selectedId = SelectedFavoriteInvoice?.InvoiceId;
        var ids = await GetFavoriteInvoiceIdsAsync();
        var allInvoices = await _local.GetInvoicesAsync();
        var invoiceMap = allInvoices.ToDictionary(i => i.Id);
        var customerMap = _allCustomers.ToDictionary(c => c.Id, c => c.NameOriginal);

        FavoriteInvoices.Clear();
        foreach (var id in ids)
        {
            if (!invoiceMap.TryGetValue(id, out var invoice))
                continue;

            var customerName = customerMap.TryGetValue(invoice.CustomerId, out var n) ? n : "(미지정)";
            var display = $"{invoice.InvoiceDate:yyyy/MM/dd}  {customerName}  {invoice.TotalAmount:N0}원";

            FavoriteInvoices.Add(new FavoriteInvoiceQuickItem
            {
                InvoiceId = id,
                DisplayText = display
            });
        }

        if (FavoriteInvoices.Count != ids.Count)
            await SaveFavoriteInvoiceIdsAsync(FavoriteInvoices.Select(f => f.InvoiceId));

        SelectedFavoriteInvoice = selectedId.HasValue
            ? FavoriteInvoices.FirstOrDefault(f => f.InvoiceId == selectedId.Value)
            : FavoriteInvoices.FirstOrDefault();
    }

    [RelayCommand]
    private void NewInvoice()
    {
        EditInvoiceId = Guid.NewGuid();
        EditCustomer = null;
        EditCustomerName = string.Empty;
        EditInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
        EditVoucherType = VoucherType.Sales;
        EditMemo = string.Empty;
        EditTotalAmount = 0;
        EditSupplyAmount = 0;
        EditVatAmount = 0;
        EditLines.Clear();
        AddNewLine();
    }

    [RelayCommand]
    private async Task EditInvoiceAsync()
    {
        if (SelectedInvoiceRow is null) return;
        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id);
        if (inv is null) return;

        EditInvoiceId = inv.Id;
        EditInvoiceDate = inv.InvoiceDate;
        EditVoucherType = inv.VoucherType;
        EditMemo = inv.Memo;
        EditTotalAmount = inv.TotalAmount;
        EditSupplyAmount = inv.SupplyAmount;
        EditVatAmount = inv.VatAmount;

        EditCustomer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId);
        EditCustomerName = EditCustomer?.NameOriginal ?? string.Empty;

        EditLines.Clear();
        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
            EditLines.Add(InvoiceLineEditModel.FromLocal(line));
    }

    [RelayCommand]
    private async Task SaveInvoiceAsync()
    {
        if (EditCustomer is null)
        {
            System.Windows.MessageBox.Show("嫄곕옒泥섎? ?좏깮?섏꽭??", "?뚮┝", System.Windows.MessageBoxButton.OK);
            return;
        }

        var lines = EditLines.Where(l => !string.IsNullOrWhiteSpace(l.ItemName)).ToList();
        var inv = new LocalInvoice
        {
            Id = EditInvoiceId,
            CustomerId = EditCustomer.Id,
            InvoiceDate = EditInvoiceDate,
            VoucherType = EditVoucherType,
            Memo = EditMemo,
            Lines = lines.Select(l => l.ToLocal(EditInvoiceId)).ToList()
        };

        await _local.SaveInvoiceAsync(inv);
        await LoadInvoiceListAsync();
        System.Windows.MessageBox.Show("??λ릺?덉뒿?덈떎.", "?뚮┝", System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private async Task DeleteInvoiceAsync()
    {
        if (SelectedInvoiceRow is null) return;
        var confirm = System.Windows.MessageBox.Show(
            "?좏깮???꾪몴瑜???젣?섏떆寃좎뒿?덇퉴?", "??젣 ?뺤씤",
            System.Windows.MessageBoxButton.YesNo);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        await _local.DeleteInvoiceAsync(SelectedInvoiceRow.Id);
        await LoadInvoiceListAsync();
    }

    // ?? Lines ??????????????????????????????????????????????????????????????
    [RelayCommand]
    private void AddNewLine()
    {
        EditLines.Add(new InvoiceLineEditModel());
        RecalcTotals();
    }

    [RelayCommand]
    private void RemoveLine(InvoiceLineEditModel? line)
    {
        if (line is null) return;
        EditLines.Remove(line);
        RecalcTotals();
    }

    public void RecalcTotals()
    {
        EditTotalAmount = EditLines.Sum(l => l.LineAmount);
        EditSupplyAmount = Math.Round(EditTotalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        EditVatAmount = EditTotalAmount - EditSupplyAmount;
    }

    // ?? Payments ??????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task LoadPaymentsAsync()
    {
        if (SelectedInvoiceRow is null) return;
        PaymentInvoice = SelectedInvoiceRow;

        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id);
        if (inv is null) return;

        PaymentRows.Clear();
        foreach (var p in inv.Payments.Where(p => !p.IsDeleted))
            PaymentRows.Add(PaymentRowModel.FromLocal(p));

        RecalcPaymentTotals(inv);
        SelectedTabIndex = 1; // ?섍툑 ?낅젰 ??(?꾪몴?묒꽦 ???쒓굅 ??
    }

    [RelayCommand]
    private void AddPaymentRow()
    {
        if (PaymentInvoice is null) return;
        PaymentRows.Add(new PaymentRowModel { InvoiceId = PaymentInvoice.Id });
    }

    [RelayCommand]
    private async Task SavePaymentsAsync()
    {
        if (PaymentInvoice is null) return;

        if (PaymentRows.Any(row => row.Amount < 0))
        {
            System.Windows.MessageBox.Show("수금 금액은 0 이상으로 입력하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetInvoice = await _local.GetInvoiceAsync(PaymentInvoice.Id);
        if (targetInvoice is null)
            return;

        var inputTotal = PaymentRows.Sum(row => row.Amount);
        if (inputTotal > targetInvoice.TotalAmount)
        {
            var proceed = System.Windows.MessageBox.Show(
                "입력한 수금 합계가 전표 합계를 초과합니다. 계속 저장할까요?",
                "수금 검증",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (proceed != System.Windows.MessageBoxResult.Yes)
                return;
        }

        foreach (var row in PaymentRows)
        {
            if (row.Amount == 0) continue;
            row.InvoiceId = PaymentInvoice.Id;
            await _local.SavePaymentAsync(row.ToLocal());
        }

        var inv = await _local.GetInvoiceAsync(PaymentInvoice.Id);
        if (inv is not null) RecalcPaymentTotals(inv);
        await LoadInvoiceListAsync();
        System.Windows.MessageBox.Show("수금이 저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
    }

    private void RecalcPaymentTotals(LocalInvoice inv)
    {
        PaymentTotalPaid = PaymentRows.Sum(p => p.Amount);
        PaymentBalance = inv.TotalAmount - PaymentTotalPaid;
    }

    // ?? Statement Print (F9) ?????????????????????????????????????????????
    [RelayCommand]
    private async Task PrintStatementAsync()
    {
        try
        {
            var target = StatementInvoice ?? SelectedInvoiceRow;
            if (target is null)
            {
                System.Windows.MessageBox.Show("출력할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            var inv = await _local.GetInvoiceAsync(target.Id);
            var company = await _local.GetCompanyProfileAsync();

            if (inv is null || company is null)
            {
                System.Windows.MessageBox.Show("전표 또는 회사 정보가 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId);
            if (customer is null)
            {
                System.Windows.MessageBox.Show("거래처 정보를 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var printModel = await LoadOrCreateInvoicePrintModelAsync(
                inv,
                customer,
                company,
                printWithDate: true,
                printWithPrice: true);
            var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
            var previewViewModel = new PrintPreviewViewModel(
                previewDocument,
                _invoicePrintService,
                $"거래명세서_{inv.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
            var previewWindow = new PrintPreviewWindow(previewViewModel)
            {
                Owner = GetActiveWindow()
            };

            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"거래명세서 인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<PrintTemplateOption?> ResolveDefaultStatementTemplateAsync()
    {
        if (SelectedStatementTemplate is not null)
            return SelectedStatementTemplate;

        var templates = _templateCatalog.GetStatementTemplates();
        if (templates.Count == 0)
            return null;

        var savedTemplateId = await _local.GetSettingAsync(PrintTemplateCatalogService.DefaultStatementTemplateSettingKey);
        return PrintTemplateCatalogService.ResolvePreferredTemplate(templates, savedTemplateId, "거래명1/2");
    }

    private PrintTemplateOption? ResolveRuntimeStatementTemplate(PrintTemplateOption? selectedTemplate)
    {
        var templates = StatementTemplates.ToList();
        if (templates.Count == 0)
            templates = _templateCatalog.GetStatementTemplates().ToList();

        if (selectedTemplate is { IsBuiltIn: false })
            return selectedTemplate;

        return PrintTemplateCatalogService.ResolveLegacyTemplateForPrintType(templates, "거래명1/2")
            ?? selectedTemplate;
    }

    private async Task<InvoicePrintModel> LoadOrCreateInvoicePrintModelAsync(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
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
                    saved.PrintWithDate = printWithDate;
                    saved.PrintWithPrice = printWithPrice;

                    if (saved.Lines.Count == 0)
                    {
                        saved.Lines = _invoicePrintService
                            .CreateDefaultModel(invoice, customer, company, printWithDate, printWithPrice)
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

        return _invoicePrintService.CreateDefaultModel(invoice, customer, company, printWithDate, printWithPrice);
    }

    private static Window? GetActiveWindow()
    {
        return Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);
    }
// ?? Company Settings ??????????????????????????????????????????????????
    private async Task LoadCompanyProfileAsync()
    {
        var profile = await _local.GetCompanyProfileAsync();
        if (profile is null) return;

        _companyProfileId = profile.Id;
        CompanyTradeName = profile.TradeName;
        CompanyRepresentative = profile.Representative;
        CompanyBusinessNumber = profile.BusinessNumber;
        CompanyBusinessType = profile.BusinessType;
        CompanyBusinessItem = profile.BusinessItem;
        CompanyAddress = profile.Address;
        CompanyContactNumber = profile.ContactNumber;
        CompanyEmail = profile.Email;
        CompanyBankAccountText = profile.BankAccountText;
        CompanyStampImage = profile.StampImage;
        CompanyStampImagePath = profile.StampImage is { Length: > 0 } ? "(?대?吏 ?덉쓬)" : "(?놁쓬)";
    }

    [RelayCommand]
    private async Task SaveCompanyProfileAsync()
    {
        if (!_session.HasPermission("CompanyProfile.Edit")
            && _session.User?.Role != "Admin")
        {
            System.Windows.MessageBox.Show("沅뚰븳???놁뒿?덈떎.", "?ㅻ쪟", System.Windows.MessageBoxButton.OK);
            return;
        }

        var profile = new LocalCompanyProfile
        {
            Id = _companyProfileId,
            TradeName = CompanyTradeName,
            Representative = CompanyRepresentative,
            BusinessNumber = CompanyBusinessNumber,
            BusinessType = CompanyBusinessType,
            BusinessItem = CompanyBusinessItem,
            Address = CompanyAddress,
            ContactNumber = CompanyContactNumber,
            Email = CompanyEmail,
            BankAccountText = CompanyBankAccountText,
            StampImage = CompanyStampImage
        };

        await _local.SaveCompanyProfileAsync(profile);
        System.Windows.MessageBox.Show("?뚯궗 ?뺣낫媛 ??λ릺?덉뒿?덈떎.", "?뚮┝", System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private void SelectStampImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "吏곸씤 ?대?吏 ?좏깮",
            Filter = "?대?吏 ?뚯씪|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;
        CompanyStampImage = File.ReadAllBytes(dlg.FileName);
        CompanyStampImagePath = "(?대?吏 ?덉쓬)";
    }

    [RelayCommand]
    private void ClearStampImage()
    {
        CompanyStampImage = null;
        CompanyStampImagePath = "(?놁쓬)";
    }

    private async Task LoadLegacyMigrationSettingsAsync()
    {
        var defaultDb = GetDefaultLegacySourceDbPath();
        var defaultCustomerExcel = Path.Combine(AppContext.BaseDirectory, "거래처 목록.xlsx");
        var defaultItemExcel = Path.Combine(AppContext.BaseDirectory, "제품 목록.xlsx");

        LegacySourceDbPath = await _local.GetSettingAsync(LegacySourceDbPathSettingKey) ?? defaultDb;
        LegacyCustomerExcelPath = await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey) ?? defaultCustomerExcel;
        LegacyItemExcelPath = await _local.GetSettingAsync(LegacyItemExcelPathSettingKey) ?? defaultItemExcel;

        if (string.IsNullOrWhiteSpace(LegacySourceDbPath))
            LegacySourceDbPath = defaultDb;
        if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath))
            LegacyCustomerExcelPath = defaultCustomerExcel;
        if (string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            LegacyItemExcelPath = defaultItemExcel;
    }

    private async Task PersistLegacyMigrationSettingsAsync()
    {
        await _local.SetSettingAsync(LegacySourceDbPathSettingKey, LegacySourceDbPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyCustomerExcelPathSettingKey, LegacyCustomerExcelPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyItemExcelPathSettingKey, LegacyItemExcelPath ?? string.Empty);
    }

    private static string GetDefaultLegacySourceDbPath()
    {
        var candidate = @"C:\LegacyVendor\LegacySalesApp\DATA\SALE_ACE_DATA.FDB";
        if (File.Exists(candidate))
            return candidate;
        return string.Empty;
    }

    [RelayCommand]
    private async Task SelectLegacySourceDbPathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "외부 레거시 DB(FDB) 선택",
            Filter = "Firebird DB|*.fdb|모든 파일|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacySourceDbPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyCustomerExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyCustomerExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "거래처 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "거래처 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyCustomerExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyItemExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyItemExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "제품 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "제품 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyItemExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task ExportLegacyDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacySourceDbPath) || !File.Exists(LegacySourceDbPath))
            {
                MessageBox.Show("외부 레거시 DB 경로를 먼저 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            {
                MessageBox.Show("거래처/제품 엑셀 경로를 먼저 지정하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "외부 레거시 데이터를 엑셀로 추출 중...";
            var result = await _legacyMigrationService.ExportFromOriginalAsync(
                LegacySourceDbPath,
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();

            LegacyMigrationStatus = $"추출 완료: 거래처 {result.CustomerCount:N0}건, 제품 {result.ItemCount:N0}건";
            MessageBox.Show(
                $"추출 완료\n거래처: {result.CustomerCount:N0}건\n제품: {result.ItemCount:N0}건\n\n{result.CustomerExcelPath}\n{result.ItemExcelPath}",
                "데이터 추출",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"추출 실패: {ex.Message}";
            MessageBox.Show($"데이터 추출 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportLegacyExcelDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || !File.Exists(LegacyCustomerExcelPath))
            {
                MessageBox.Show("거래처 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyItemExcelPath) || !File.Exists(LegacyItemExcelPath))
            {
                MessageBox.Show("제품 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "엑셀 데이터를 코덱스 레거시 판매관리로 가져오는 중...";
            var result = await _legacyMigrationService.ImportFromExcelAsync(
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();
            await LoadCustomersAsync();
            await LoadInvoiceListAsync();

            LegacyMigrationStatus =
                $"가져오기 완료: 거래처 +{result.CreatedCustomers:N0}/수정 {result.UpdatedCustomers:N0}, " +
                $"제품 +{result.CreatedItems:N0}/수정 {result.UpdatedItems:N0}";

            MessageBox.Show(
                $"가져오기 완료\n" +
                $"거래처: 신규 {result.CreatedCustomers:N0}, 수정 {result.UpdatedCustomers:N0}, 건너뜀 {result.SkippedCustomers:N0}\n" +
                $"제품: 신규 {result.CreatedItems:N0}, 수정 {result.UpdatedItems:N0}, 건너뜀 {result.SkippedItems:N0}",
                "데이터 가져오기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"가져오기 실패: {ex.Message}";
            MessageBox.Show($"데이터 가져오기 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportAndImportLegacyDataAsync()
    {
        await ExportLegacyDataAsync();
        if (!LegacyMigrationStatus.StartsWith("추출 완료", StringComparison.Ordinal))
            return;
        await ImportLegacyExcelDataAsync();
    }

    // ?? Refresh Customers (嫄곕옒泥??깅줉/?섏젙 ??媛깆떊) ??????????????????????
    [RelayCommand]
    public async Task RefreshCustomersAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    // ?? Sync ??????????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task ForceSyncAsync()
    {
        await _sync.TrySyncAsync();
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    // ?? Backup ???????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var ok = await _backup.BackupNowAsync();
        System.Windows.MessageBox.Show(
            ok ? "諛깆뾽???꾨즺?섏뿀?듬땲??" : "諛깆뾽 以??ㅻ쪟媛 諛쒖깮?덉뒿?덈떎.",
            "諛깆뾽", System.Windows.MessageBoxButton.OK);
    }
}

