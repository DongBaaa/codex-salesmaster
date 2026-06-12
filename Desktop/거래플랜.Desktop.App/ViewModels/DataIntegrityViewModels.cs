using System.Collections.ObjectModel;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class DataIntegrityAlertViewModel : ObservableObject
{
    public DataIntegrityAlertViewModel(DataIntegrityScanResult scanResult)
    {
        ScanResult = scanResult;
        foreach (var summary in scanResult.Summaries)
            Summaries.Add(summary);
    }

    public DataIntegrityScanResult ScanResult { get; }
    public ObservableCollection<DataIntegrityIssueSummary> Summaries { get; } = new();

    public string HeadingText => "동기화 후 운영 점검에서 확인할 항목이 있습니다.";
    public string SummaryText => $"총 {ScanResult.TotalIssueCount:N0}건 / {Summaries.Count:N0}개 유형";
    public string ScannedAtText => $"점검 시각 {ScanResult.ScannedAtText}";
    public bool HasIssues => ScanResult.HasIssues;
}

public sealed partial class DataIntegrityIssueViewModel : ObservableObject, IDisposable
{
    private const int DisplayIssueLimit = 500;

    private readonly DataIntegrityIssueService _service;
    private readonly SessionState _session;
    private readonly UiDebouncer _filterDebouncer = new();
    private readonly string? _initialCode;
    private readonly DataIntegrityScanResult? _initialScanResult;
    private DataIntegrityScanResult? _lastScanResult;
    private List<DataIntegrityIssueDetail> _allIssues = new();
    private List<DataIntegrityIssueDetail> _filteredIssues = new();

    public DataIntegrityIssueViewModel(
        DataIntegrityIssueService service,
        SessionState session,
        string? initialCode = null,
        DataIntegrityScanResult? initialScanResult = null)
    {
        _service = service;
        _session = session;
        _initialCode = initialCode;
        _initialScanResult = initialScanResult;
    }

    public ObservableCollection<DataIntegrityIssueSummary> Summaries { get; } = new();
    public ObservableCollection<DataIntegrityIssueFilterOption> IssueTypeOptions { get; } = new();
    public ObservableCollection<DataIntegrityIssueDetail> Issues { get; } = new ResettableObservableCollection<DataIntegrityIssueDetail>();
    public IReadOnlyList<string> SeverityOptions { get; } = ["전체", "오류", "주의"];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "운영 점검 항목을 불러오는 중입니다.";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DataIntegrityIssueFilterOption? _selectedIssueType;
    [ObservableProperty] private string _selectedSeverity = "전체";
    [ObservableProperty] private DataIntegrityIssueDetail? _selectedIssue;
    [ObservableProperty] private string _scanSummaryText = string.Empty;

    public async Task LoadAsync()
    {
        if (_initialScanResult is null)
            await RefreshAsync();
        else
            ApplyScanResult(_initialScanResult);

        if (!string.IsNullOrWhiteSpace(_initialCode))
        {
            var option = IssueTypeOptions.FirstOrDefault(option => string.Equals(option.Code, _initialCode, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
            {
                SelectedIssueType = option;
                ApplyFilter();
            }
        }
    }

    public void Dispose()
    {
        _filterDebouncer.Dispose();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            ApplyScanResult(await _service.ScanAsync(_session));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ExportExcel()
    {
        if (IsBusy)
            return;

        var issuesToExport = _filteredIssues.ToList();
        if (issuesToExport.Count == 0)
        {
            StatusMessage = "엑셀로 저장할 점검 항목이 없습니다.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "운영 점검 내역 엑셀 저장",
            Filter = "Excel 통합 문서 (*.xlsx)|*.xlsx",
            FileName = $"거래플랜_운영점검_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            InitialDirectory = AppPaths.UserDownloadsDir
        };

        if (dialog.ShowDialog() != true)
            return;

        using var workbook = new XLWorkbook();
        var summarySheet = workbook.Worksheets.Add("요약");
        summarySheet.Cell(1, 1).Value = "점검 시각";
        summarySheet.Cell(1, 2).Value = _lastScanResult?.ScannedAtText ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        summarySheet.Cell(2, 1).Value = "필터 결과";
        summarySheet.Cell(2, 2).Value = issuesToExport.Count;
        summarySheet.Cell(4, 1).Value = "유형코드";
        summarySheet.Cell(4, 2).Value = "유형";
        summarySheet.Cell(4, 3).Value = "등급";
        summarySheet.Cell(4, 4).Value = "영역";
        summarySheet.Cell(4, 5).Value = "건수";
        summarySheet.Cell(4, 6).Value = "설명";
        summarySheet.Cell(4, 7).Value = "권장 조치";

        var summaryRow = 5;
        foreach (var summary in Summaries)
        {
            summarySheet.Cell(summaryRow, 1).Value = summary.Code;
            summarySheet.Cell(summaryRow, 2).Value = summary.Title;
            summarySheet.Cell(summaryRow, 3).Value = summary.SeverityDisplay;
            summarySheet.Cell(summaryRow, 4).Value = summary.Area;
            summarySheet.Cell(summaryRow, 5).Value = summary.Count;
            summarySheet.Cell(summaryRow, 6).Value = summary.Description;
            summarySheet.Cell(summaryRow, 7).Value = summary.SuggestedAction;
            summaryRow++;
        }

        var detailSheet = workbook.Worksheets.Add("상세");
        var headers = new[]
        {
            "등급",
            "유형코드",
            "유형",
            "영역",
            "대상유형",
            "거래처",
            "품목",
            "자산",
            "담당지점",
            "현재값",
            "기준값",
            "판단/참조",
            "관련 후보 ID",
            "내용",
            "권장 조치",
            "병합 가능",
            "바로가기"
        };

        for (var i = 0; i < headers.Length; i++)
            detailSheet.Cell(1, i + 1).Value = headers[i];

        var detailRow = 2;
        foreach (var issue in issuesToExport)
        {
            detailSheet.Cell(detailRow, 1).Value = issue.SeverityDisplay;
            detailSheet.Cell(detailRow, 2).Value = issue.Code;
            detailSheet.Cell(detailRow, 3).Value = issue.Title;
            detailSheet.Cell(detailRow, 4).Value = issue.Area;
            detailSheet.Cell(detailRow, 5).Value = issue.EntityType;
            detailSheet.Cell(detailRow, 6).Value = issue.CustomerName;
            detailSheet.Cell(detailRow, 7).Value = issue.ItemName;
            detailSheet.Cell(detailRow, 8).Value = issue.AssetDisplayName;
            detailSheet.Cell(detailRow, 9).Value = issue.OfficeCode;
            detailSheet.Cell(detailRow, 10).Value = issue.CurrentValue;
            detailSheet.Cell(detailRow, 11).Value = issue.ExpectedValue;
            detailSheet.Cell(detailRow, 12).Value = issue.ReviewInfoDisplay;
            detailSheet.Cell(detailRow, 13).Value = issue.RelatedEntityIdText;
            detailSheet.Cell(detailRow, 14).Value = issue.Message;
            detailSheet.Cell(detailRow, 15).Value = issue.SuggestedAction;
            detailSheet.Cell(detailRow, 16).Value = issue.CanMergeDuplicates ? "가능" : "불가";
            detailSheet.Cell(detailRow, 17).Value = issue.DirectActionText;
            detailRow++;
        }

        foreach (var worksheet in workbook.Worksheets)
        {
            worksheet.Row(1).Style.Font.Bold = true;
            worksheet.Columns().AdjustToContents();
            worksheet.SheetView.FreezeRows(1);
        }

        try
        {
            workbook.SaveAs(dialog.FileName);
            StatusMessage = $"운영 점검 내역 {issuesToExport.Count:N0}건을 엑셀로 저장했습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"엑셀 저장에 실패했습니다. 파일이 열려 있거나 저장 권한이 없는지 확인하세요. {ex.Message}";
        }
    }
    partial void OnSearchTextChanged(string value) => RequestFilterApply();
    partial void OnSelectedIssueTypeChanged(DataIntegrityIssueFilterOption? value) => RequestFilterApply();
    partial void OnSelectedSeverityChanged(string value) => RequestFilterApply();

    private void RequestFilterApply()
    {
        if (IsBusy)
            return;

        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(180), ApplyFilter);
    }

    private void ApplyFilter()
    {
        var selectedCode = SelectedIssueType?.Code ?? string.Empty;
        var query = (SearchText ?? string.Empty).Trim();
        var severity = SelectedSeverity ?? "전체";

        var filtered = _allIssues.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(selectedCode))
            filtered = filtered.Where(issue => string.Equals(issue.Code, selectedCode, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(severity, "오류", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(issue => string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase));
        else if (string.Equals(severity, "주의", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(issue => !string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(issue =>
                Contains(issue.Title, query) ||
                Contains(issue.CustomerName, query) ||
                Contains(issue.ItemName, query) ||
                Contains(issue.AssetDisplayName, query) ||
                Contains(issue.OfficeCode, query) ||
                Contains(issue.ReviewInfoDisplay, query) ||
                Contains(issue.RelatedEntityIdText, query) ||
                Contains(issue.Message, query));
        }

        var previousId = SelectedIssue?.Id;
        var list = filtered
            .OrderByDescending(issue => string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .ThenBy(issue => issue.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(issue => issue.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _filteredIssues = list;
        var displayed = list.Count > DisplayIssueLimit
            ? list.Take(DisplayIssueLimit).ToList()
            : list;

        Issues.ReplaceWith(displayed);

        SelectedIssue = Issues.FirstOrDefault(issue => issue.Id == previousId) ?? Issues.FirstOrDefault();
        StatusMessage = list.Count == 0
            ? "필터 조건에 맞는 점검 항목이 없습니다."
            : list.Count > Issues.Count
                ? $"필터 결과 {list.Count:N0}건 중 {Issues.Count:N0}건만 먼저 표시합니다. 전체 내역은 엑셀 저장으로 확인하세요."
                : $"필터 결과 {Issues.Count:N0}건을 표시합니다.";
    }

    private void ApplyScanResult(DataIntegrityScanResult scanResult)
    {
        _lastScanResult = scanResult;
        _allIssues = scanResult.Issues.ToList();
        var previousCode = SelectedIssueType?.Code ?? string.Empty;

        Summaries.Clear();
        IssueTypeOptions.Clear();
        IssueTypeOptions.Add(new DataIntegrityIssueFilterOption { Code = string.Empty, DisplayName = "전체 유형" });
        foreach (var summary in scanResult.Summaries)
        {
            Summaries.Add(summary);
            IssueTypeOptions.Add(new DataIntegrityIssueFilterOption
            {
                Code = summary.Code,
                DisplayName = $"{summary.Title} ({summary.Count:N0})"
            });
        }

        SelectedIssueType = IssueTypeOptions.FirstOrDefault(option => string.Equals(option.Code, previousCode, StringComparison.OrdinalIgnoreCase))
                            ?? IssueTypeOptions.FirstOrDefault();
        ScanSummaryText = scanResult.HasIssues
            ? $"총 {scanResult.TotalIssueCount:N0}건 / {scanResult.Summaries.Count:N0}개 유형 / 점검 {scanResult.ScannedAtText}"
            : $"확인된 운영 위험 신호가 없습니다. 점검 {scanResult.ScannedAtText}";
        ApplyFilter();
        if (!scanResult.HasIssues)
            StatusMessage = "현재 확인된 위험 신호가 없습니다.";
    }

    private static bool Contains(string? value, string query)
        => (value ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
