using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class IntegrityReportViewModel : ObservableObject
{
    private const int MobileDetailPreviewLimit = 30;

    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;

    private string _summaryText = "서버 무결성 검사 결과를 불러올 준비가 되었습니다.";
    private string _scopeText = "-";
    private string _generatedText = "-";
    private string _statusMessage = "새로고침을 눌러 운영 서버 기준 무결성 결과를 확인하세요.";
    private string _detailStatusMessage = "항목을 선택하면 상세 근거를 조회합니다.";
    private bool _isBusy;
    private bool _hasReport;
    private bool _canViewIntegrityReport;
    private IntegrityIssueDto? _selectedIssue;

    public IntegrityReportViewModel(GeoraePlanApiClient api, SessionStore sessionStore)
    {
        _api = api;
        _sessionStore = sessionStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ClearDetailCommand = new AsyncCommand(ClearDetailAsync);
    }

    public ObservableCollection<IntegrityIssueDto> Issues { get; } = new();
    public ObservableCollection<IntegrityIssueDetailRowDto> DetailRows { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ClearDetailCommand { get; }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    public string ScopeText
    {
        get => _scopeText;
        set => SetProperty(ref _scopeText, value);
    }

    public string GeneratedText
    {
        get => _generatedText;
        set => SetProperty(ref _generatedText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string DetailStatusMessage
    {
        get => _detailStatusMessage;
        set => SetProperty(ref _detailStatusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public bool HasReport
    {
        get => _hasReport;
        set => SetProperty(ref _hasReport, value);
    }

    public bool CanViewIntegrityReport
    {
        get => _canViewIntegrityReport;
        set => SetProperty(ref _canViewIntegrityReport, value);
    }

    public IntegrityIssueDto? SelectedIssue
    {
        get => _selectedIssue;
        private set
        {
            if (!SetProperty(ref _selectedIssue, value))
                return;

            OnPropertyChanged(nameof(HasSelectedIssue));
            OnPropertyChanged(nameof(SelectedIssueTitle));
            OnPropertyChanged(nameof(SelectedIssueSubtitle));
        }
    }

    public bool HasSelectedIssue => SelectedIssue is not null;

    public string SelectedIssueTitle => SelectedIssue is null
        ? "무결성 상세"
        : $"{NormalizeSeverity(SelectedIssue.Severity)} · {SelectedIssue.Count:N0}건";

    public string SelectedIssueSubtitle => SelectedIssue is null
        ? "항목을 선택하면 서버 상세 근거를 조회합니다."
        : $"{SelectedIssue.Code} · {SelectedIssue.Message}";

    public double IssueListHeight => CalculateListHeight(Issues.Count, 92, 48, 8);
    public double DetailListHeight => CalculateListHeight(DetailRows.Count, 118, 48, 5);

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        var session = _sessionStore.GetSnapshot();
        CanViewIntegrityReport = session.CanViewIntegrityReport;
        if (!CanViewIntegrityReport)
        {
            ClearReport();
            ScopeText = session.IsAuthenticated
                ? $"현재 계정: {session.Username} / {session.Role}"
                : "로그인 필요";
            StatusMessage = "운영점검 권한이 필요합니다. 관리자 또는 Settings.Edit 권한 계정으로 로그인하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "운영 서버 무결성 결과를 조회하는 중입니다.";
            var report = await _api.GetIntegrityReportAsync();
            if (report is null)
            {
                ClearReport();
                StatusMessage = "운영점검 결과를 받지 못했습니다.";
                return;
            }

            ApplyReport(report);
        }
        catch (Exception ex)
        {
            StatusMessage = $"운영점검 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectIssueAsync(IntegrityIssueDto issue)
    {
        if (IsBusy)
            return;

        SelectedIssue = issue;
        DetailRows.Clear();
        OnPropertyChanged(nameof(DetailListHeight));
        DetailStatusMessage = $"{issue.Code} 상세 근거를 조회하는 중입니다.";

        try
        {
            IsBusy = true;
            var details = await _api.GetIntegrityIssueDetailsAsync(issue.Code);
            DetailRows.Clear();
            if (details?.Rows is { Count: > 0 } rows)
            {
                foreach (var row in rows.Take(MobileDetailPreviewLimit))
                    DetailRows.Add(row);

                DetailStatusMessage = rows.Count > MobileDetailPreviewLimit
                    ? $"상세 {rows.Count:N0}건 중 모바일에서는 상위 {MobileDetailPreviewLimit:N0}건을 표시합니다. 전체 조치는 PC 운영점검에서 확인하세요."
                    : $"상세 {rows.Count:N0}건을 불러왔습니다.";
            }
            else
            {
                DetailStatusMessage = "상세 근거 행이 없습니다.";
            }

            OnPropertyChanged(nameof(DetailListHeight));
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"상세 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ClearDetailAsync()
    {
        SelectedIssue = null;
        DetailRows.Clear();
        DetailStatusMessage = "항목을 선택하면 상세 근거를 조회합니다.";
        OnPropertyChanged(nameof(DetailListHeight));
        return Task.CompletedTask;
    }

    private void ApplyReport(IntegrityReportDto report)
    {
        var orderedIssues = report.Issues
            .OrderBy(SeverityRank)
            .ThenByDescending(issue => issue.Count)
            .ThenBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Issues.Clear();
        foreach (var issue in orderedIssues)
            Issues.Add(issue);

        DetailRows.Clear();
        SelectedIssue = null;
        DetailStatusMessage = "항목을 선택하면 상세 근거를 조회합니다.";

        var errorTypeCount = orderedIssues.Count(IsError);
        var warningTypeCount = orderedIssues.Count(IsWarning);
        var infoTypeCount = orderedIssues.Count(IsInfo);
        var errorRowCount = orderedIssues.Where(IsError).Sum(issue => issue.Count);
        var warningRowCount = orderedIssues.Where(IsWarning).Sum(issue => issue.Count);
        var infoRowCount = orderedIssues.Where(IsInfo).Sum(issue => issue.Count);

        SummaryText = orderedIssues.Count == 0
            ? "서버 무결성 검사 결과: 오류/경고 없음"
            : $"오류 {errorTypeCount:N0}유형/{errorRowCount:N0}건 · 경고 {warningTypeCount:N0}유형/{warningRowCount:N0}건 · 참고 {infoTypeCount:N0}유형/{infoRowCount:N0}건";
        ScopeText = $"범위: 업체 {Normalize(report.TenantCode, "-")} / 지점 {Normalize(report.OfficeCode, "-")}";
        GeneratedText = $"기준시각: {report.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        HasReport = true;
        StatusMessage = errorTypeCount > 0
            ? "치명/높음 위험 후보가 있습니다. PC 운영점검에서 상세 처리 전까지 임의 수정하지 마세요."
            : warningTypeCount > 0
                ? "경고 항목이 있습니다. PC 운영점검에서 업무 영향 여부를 확인하세요."
                : "서버 무결성 요약을 불러왔습니다.";
        OnPropertyChanged(nameof(IssueListHeight));
        OnPropertyChanged(nameof(DetailListHeight));
    }

    private void ClearReport()
    {
        Issues.Clear();
        DetailRows.Clear();
        SelectedIssue = null;
        HasReport = false;
        SummaryText = "서버 무결성 검사 결과가 없습니다.";
        GeneratedText = "-";
        DetailStatusMessage = "항목을 선택하면 상세 근거를 조회합니다.";
        OnPropertyChanged(nameof(IssueListHeight));
        OnPropertyChanged(nameof(DetailListHeight));
    }

    private static int SeverityRank(IntegrityIssueDto issue)
        => NormalizeSeverity(issue.Severity) switch
        {
            "Error" => 0,
            "Warning" => 1,
            _ => 2
        };

    private static bool IsError(IntegrityIssueDto issue)
        => string.Equals(NormalizeSeverity(issue.Severity), "Error", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarning(IntegrityIssueDto issue)
        => string.Equals(NormalizeSeverity(issue.Severity), "Warning", StringComparison.OrdinalIgnoreCase);

    private static bool IsInfo(IntegrityIssueDto issue)
        => !IsError(issue) && !IsWarning(issue);

    private static string NormalizeSeverity(string? severity)
        => string.IsNullOrWhiteSpace(severity) ? "Info" : severity.Trim();

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 8;
    }
}
