using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

public sealed partial class DataIntegrityIssueViewModel : ObservableObject
{
    private readonly DataIntegrityIssueService _service;
    private readonly SessionState _session;
    private readonly string? _initialCode;
    private DataIntegrityScanResult? _lastScanResult;
    private List<DataIntegrityIssueDetail> _allIssues = new();

    public DataIntegrityIssueViewModel(DataIntegrityIssueService service, SessionState session, string? initialCode = null)
    {
        _service = service;
        _session = session;
        _initialCode = initialCode;
    }

    public ObservableCollection<DataIntegrityIssueSummary> Summaries { get; } = new();
    public ObservableCollection<DataIntegrityIssueFilterOption> IssueTypeOptions { get; } = new();
    public ObservableCollection<DataIntegrityIssueDetail> Issues { get; } = new();
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
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(_initialCode))
        {
            var option = IssueTypeOptions.FirstOrDefault(option => string.Equals(option.Code, _initialCode, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
                SelectedIssueType = option;
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            _lastScanResult = await _service.ScanAsync(_session);
            _allIssues = _lastScanResult.Issues.ToList();
            var previousCode = SelectedIssueType?.Code ?? string.Empty;

            Summaries.Clear();
            IssueTypeOptions.Clear();
            IssueTypeOptions.Add(new DataIntegrityIssueFilterOption { Code = string.Empty, DisplayName = "전체 유형" });
            foreach (var summary in _lastScanResult.Summaries)
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
            ScanSummaryText = _lastScanResult.HasIssues
                ? $"총 {_lastScanResult.TotalIssueCount:N0}건 / {_lastScanResult.Summaries.Count:N0}개 유형 / 점검 {_lastScanResult.ScannedAtText}"
                : $"확인된 운영 위험 신호가 없습니다. 점검 {_lastScanResult.ScannedAtText}";
            ApplyFilter();
            StatusMessage = _lastScanResult.HasIssues
                ? "항목을 선택한 뒤 바로가기 버튼으로 원본 화면에서 수정하세요."
                : "현재 확인된 위험 신호가 없습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedIssueTypeChanged(DataIntegrityIssueFilterOption? value) => ApplyFilter();
    partial void OnSelectedSeverityChanged(string value) => ApplyFilter();

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
                Contains(issue.Message, query));
        }

        var previousId = SelectedIssue?.Id;
        var list = filtered
            .OrderByDescending(issue => string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            .ThenBy(issue => issue.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(issue => issue.CustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Issues.Clear();
        foreach (var issue in list)
            Issues.Add(issue);

        SelectedIssue = Issues.FirstOrDefault(issue => issue.Id == previousId) ?? Issues.FirstOrDefault();
        StatusMessage = Issues.Count == 0
            ? "필터 조건에 맞는 점검 항목이 없습니다."
            : $"필터 결과 {Issues.Count:N0}건을 표시합니다.";
    }

    private static bool Contains(string? value, string query)
        => (value ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
