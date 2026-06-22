using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SyncDiagnosticsViewModel
{
    private readonly IntegrityIssueExcelExportService _integrityIssueExcelExport = new();
    private IntegrityIssueDetailResultDto? _loadedServerIntegrityDetailResult;

    public ObservableCollection<IntegrityIssueDto> ServerIntegrityIssues { get; } = new();
    public ObservableCollection<IntegrityIssueDetailRowDto> ServerIntegrityDetailRows { get; } = new();
    public ObservableCollection<SyncOutboxListItem> OutboxEntries { get; } = new();

    [ObservableProperty] private SyncOutboxListItem? _selectedOutboxEntry;
    [ObservableProperty] private IntegrityIssueDto? _selectedServerIntegrityIssue;
    [ObservableProperty] private string _serverIntegritySummaryText = "서버 무결성 리포트를 아직 불러오지 않았습니다.";
    [ObservableProperty] private string _serverIntegrityStatusText = "미조회";
    [ObservableProperty] private string _serverIntegrityGeneratedAtText = "미조회";
    [ObservableProperty] private int _serverIntegrityIssueCount;
    [ObservableProperty] private string _serverIntegrityDetailSummaryText = "선택한 서버 무결성 이슈 없음";
    [ObservableProperty] private string _serverIntegrityDetailStatusText = "상세 목록을 보려면 서버 무결성 이슈를 선택하세요.";
    [ObservableProperty] private int _serverIntegrityDetailCount;
    [ObservableProperty] private string _outboxSummaryText = "sync outbox를 아직 불러오지 않았습니다.";
    [ObservableProperty] private string _outboxStatusText = "미조회";
    [ObservableProperty] private int _pendingOutboxCount;
    [ObservableProperty] private int _failedOutboxCount;
    [ObservableProperty] private int _acknowledgedOutboxCount;

    public bool CanQueryServerIntegrity => _session.HasAdministrativePrivileges && !_session.IsOfflineMode;

    partial void OnSelectedServerIntegrityIssueChanged(IntegrityIssueDto? value)
    {
        ResetServerIntegrityDetailState(
            value,
            value is null
                ? "상세 목록을 보려면 서버 무결성 이슈를 선택하세요."
                : "상세 목록 조회 버튼으로 최신 목록을 불러오세요.");
    }

    private async Task RefreshOutboxAsync(CancellationToken ct = default)
    {
        try
        {
            var entriesTask = _local.GetSyncOutboxEntriesAsync(_session, 160, ct);
            var summaryTask = _local.GetSyncOutboxSummaryAsync(_session, ct);
            await Task.WhenAll(entriesTask, summaryTask);

            var entries = await entriesTask;
            var summary = await summaryTask;
            ApplyOutboxState(entries, summary);
        }
        catch (Exception ex)
        {
            OutboxStatusText = $"조회 실패: {GetCompactExceptionMessage(ex)}";
            OutboxSummaryText = "sync outbox를 불러오지 못했습니다. 다시 시도하세요.";
        }
    }

    private void ApplyOutboxState(IReadOnlyList<SyncOutboxListItem> entries, SyncOutboxSummary summary)
    {
        var selectedId = SelectedOutboxEntry?.Id;
        OutboxEntries.Clear();
        foreach (var entry in entries)
            OutboxEntries.Add(entry);

        SelectedOutboxEntry = selectedId.HasValue
            ? OutboxEntries.FirstOrDefault(entry => entry.Id == selectedId.Value) ?? OutboxEntries.FirstOrDefault()
            : OutboxEntries.FirstOrDefault();

        PendingOutboxCount = summary.PendingCount;
        FailedOutboxCount = summary.FailedCount;
        AcknowledgedOutboxCount = summary.AcknowledgedCount;
        OutboxStatusText = $"최근 새로고침 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        OutboxSummaryText = summary.PendingCount == 0
            ? "재시도 대기 중인 sync outbox 항목이 없습니다."
            : $"대기 {summary.PendingCount:N0}건 / 실패 {summary.FailedCount:N0}건 / 완료 {summary.AcknowledgedCount:N0}건";
    }

    private async Task LoadServerIntegrityAsync(bool updateSummaryStatus, CancellationToken ct = default)
    {
        if (_session.IsOfflineMode)
        {
            ApplyServerIntegrityUnavailable("오프라인 모드에서는 서버 무결성 리포트를 조회할 수 없습니다.");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegritySummaryText;
            return;
        }

        if (!_session.HasAdministrativePrivileges)
        {
            ApplyServerIntegrityUnavailable("관리자 계정에서만 서버 무결성 리포트를 조회할 수 있습니다.");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegritySummaryText;
            return;
        }

        try
        {
            var report = await _api.GetIntegrityReportAsync(ct);
            ApplyServerIntegrityReport(report);
            if (updateSummaryStatus)
            {
                var actionRequiredCount = CountActionRequiredServerIssues(report);
                var informationalCount = report?.Issues.Count(IsInformationalServerIssue) ?? 0;
                SummaryStatusText = report is null
                    ? ServerIntegritySummaryText
                    : actionRequiredCount == 0
                        ? informationalCount > 0
                            ? $"서버 무결성 리포트 조회 완료: 업무 처리가 필요한 경고는 없고 참고 정보 {informationalCount:N0}건만 있습니다."
                            : "서버 무결성 리포트 조회를 완료했습니다."
                        : $"서버 무결성 리포트에서 확인이 필요한 항목 {actionRequiredCount:N0}건을 확인했습니다.";
            }
        }
        catch (Exception ex)
        {
            ApplyServerIntegrityUnavailable($"서버 무결성 리포트 조회 실패: {GetCompactExceptionMessage(ex)}");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegritySummaryText;
        }
    }

    private async Task<IntegrityIssueDetailResultDto?> LoadSelectedServerIntegrityDetailsCoreAsync(bool updateSummaryStatus, CancellationToken ct = default)
    {
        var issue = SelectedServerIntegrityIssue;
        if (_session.IsOfflineMode)
        {
            ResetServerIntegrityDetailState(issue, "오프라인 모드에서는 서버 무결성 상세 목록을 조회할 수 없습니다.");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegrityDetailStatusText;
            return null;
        }

        if (!_session.HasAdministrativePrivileges)
        {
            ResetServerIntegrityDetailState(issue, "관리자 계정에서만 서버 무결성 상세 목록을 조회할 수 있습니다.");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegrityDetailStatusText;
            return null;
        }

        if (issue is null)
        {
            ResetServerIntegrityDetailState(null, "상세 목록을 조회할 서버 무결성 이슈를 먼저 선택하세요.");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegrityDetailStatusText;
            return null;
        }

        ServerIntegrityDetailSummaryText = BuildServerIntegrityDetailSummary(issue);
        ServerIntegrityDetailStatusText = "서버 무결성 상세 목록을 조회하는 중...";

        try
        {
            var detail = await _api.GetIntegrityIssueDetailsAsync(issue.Code, ct);
            ApplyServerIntegrityDetailReport(issue, detail);
            if (updateSummaryStatus)
                SummaryStatusText = detail is null
                    ? "서버 무결성 상세 목록 응답이 비어 있습니다."
                    : detail.DetailCount == 0
                        ? "선택한 서버 무결성 이슈의 상세 목록은 비어 있습니다."
                        : $"서버 무결성 상세 목록 {detail.DetailCount:N0}건을 불러왔습니다.";

            return detail;
        }
        catch (Exception ex)
        {
            ResetServerIntegrityDetailState(issue, $"상세 목록 조회 실패: {GetCompactExceptionMessage(ex)}");
            if (updateSummaryStatus)
                SummaryStatusText = ServerIntegrityDetailStatusText;
            return null;
        }
    }

    private void ApplyServerIntegrityReport(IntegrityReportDto? report)
    {
        var routineRepairCandidateCount = report?.Issues.Count(issue =>
            IntegrityIssueReviewPolicy.IsRoutineRepairCandidateForServer(issue.Code)) ?? 0;
        var informationalIssueCount = report?.Issues.Count(IsInformationalServerIssue) ?? 0;
        var manualReviewCount = Math.Max(0, (report?.Issues.Count ?? 0) - routineRepairCandidateCount - informationalIssueCount);
        var selectedCode = SelectedServerIntegrityIssue?.Code;

        ServerIntegrityIssues.Clear();
        foreach (var issue in (report?.Issues ?? new List<IntegrityIssueDto>())
                     .OrderByDescending(issue => GetSeverityWeight(issue.Severity))
                     .ThenByDescending(issue => issue.Count)
                     .ThenBy(issue => issue.Message, StringComparer.CurrentCulture))
        {
            ServerIntegrityIssues.Add(issue);
        }

        ServerIntegrityIssueCount = report?.IssueCount ?? 0;
        ServerIntegrityGeneratedAtText = report?.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "미조회";
        ServerIntegrityStatusText = report is null
            ? "데이터 없음"
            : $"{report.OfficeCode} / {report.TenantCode}";
        ServerIntegritySummaryText = report is null
            ? "서버 무결성 리포트를 불러오지 못했습니다."
            : CountActionRequiredServerIssues(report) == 0
                ? "서버 기준으로 확인된 무결성 이슈가 없습니다."
                : BuildServerIntegritySummary(report, routineRepairCandidateCount, informationalIssueCount, manualReviewCount);

        SelectedServerIntegrityIssue = !string.IsNullOrWhiteSpace(selectedCode)
            ? ServerIntegrityIssues.FirstOrDefault(issue => string.Equals(issue.Code, selectedCode, StringComparison.OrdinalIgnoreCase))
                ?? ServerIntegrityIssues.FirstOrDefault()
            : ServerIntegrityIssues.FirstOrDefault();

        if (report is null)
            ResetServerIntegrityDetailState(null, "서버 무결성 리포트를 불러오지 못했습니다.");
        else if (report.IssueCount == 0)
            ResetServerIntegrityDetailState(null, "서버 기준 무결성 이슈가 없습니다.");
    }

    private void ApplyServerIntegrityDetailReport(IntegrityIssueDto issue, IntegrityIssueDetailResultDto? detail)
    {
        _loadedServerIntegrityDetailResult = detail;
        ServerIntegrityDetailRows.Clear();
        foreach (var row in detail?.Rows ?? new List<IntegrityIssueDetailRowDto>())
            ServerIntegrityDetailRows.Add(row);

        ServerIntegrityDetailCount = detail?.DetailCount ?? 0;
        ServerIntegrityDetailSummaryText = BuildServerIntegrityDetailSummary(issue);
        ServerIntegrityDetailStatusText = detail is null
            ? "상세 목록 응답이 비어 있습니다."
            : detail.DetailCount == 0
                ? $"{detail.Code} 상세 목록이 없습니다."
                : $"{detail.DetailCount:N0}건 / {detail.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} / {detail.OfficeCode} / {detail.TenantCode}";
    }

    private void ApplyServerIntegrityUnavailable(string message)
    {
        ServerIntegrityIssues.Clear();
        ServerIntegrityIssueCount = 0;
        ServerIntegrityGeneratedAtText = "미조회";
        ServerIntegrityStatusText = "사용 불가";
        ServerIntegritySummaryText = message;
        SelectedServerIntegrityIssue = null;
        ResetServerIntegrityDetailState(null, message);
    }

    private void ResetServerIntegrityDetailState(IntegrityIssueDto? issue, string statusText)
    {
        _loadedServerIntegrityDetailResult = null;
        ServerIntegrityDetailRows.Clear();
        ServerIntegrityDetailCount = 0;
        ServerIntegrityDetailSummaryText = issue is null
            ? "선택한 서버 무결성 이슈 없음"
            : BuildServerIntegrityDetailSummary(issue);
        ServerIntegrityDetailStatusText = statusText;
    }

    private static string BuildServerIntegrityDetailSummary(IntegrityIssueDto issue)
        => $"[{issue.Severity}] {issue.Message} ({issue.Count:N0}건)";

    [RelayCommand]
    private async Task RefreshServerIntegrityAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await LoadServerIntegrityAsync(updateSummaryStatus: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadServerIntegrityDetailsAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await LoadSelectedServerIntegrityDetailsCoreAsync(updateSummaryStatus: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportServerIntegrityDetailsExcelAsync()
    {
        if (IsBusy || SelectedServerIntegrityIssue is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "서버 무결성 상세 목록 엑셀 저장",
            Filter = "Excel 통합문서|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = BuildServerIntegrityDetailExportFileName(SelectedServerIntegrityIssue),
            InitialDirectory = AppPaths.UserDownloadsDir
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        try
        {
            var detail = _loadedServerIntegrityDetailResult is not null
                         && string.Equals(_loadedServerIntegrityDetailResult.Code, SelectedServerIntegrityIssue.Code, StringComparison.OrdinalIgnoreCase)
                ? _loadedServerIntegrityDetailResult
                : await LoadSelectedServerIntegrityDetailsCoreAsync(updateSummaryStatus: false);

            if (detail is null)
            {
                SummaryStatusText = ServerIntegrityDetailStatusText;
                return;
            }

            await _integrityIssueExcelExport.ExportAsync(detail, dialog.FileName);
            SummaryStatusText = $"서버 무결성 상세 목록 엑셀을 저장했습니다: {dialog.FileName}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetrySelectedOutboxAsync()
    {
        if (IsBusy || SelectedOutboxEntry is null || SelectedOutboxEntry.IsAcknowledged)
            return;

        IsBusy = true;
        try
        {
            var resetCount = await _local.ResetSyncOutboxEntriesForRetryAsync([SelectedOutboxEntry.Id], _session);
            if (resetCount == 0)
            {
                SummaryStatusText = "재시도할 sync outbox 항목이 없습니다.";
                await RefreshOutboxAsync();
                return;
            }

            var syncOk = await _sync.FlushPendingChangesAsync();
            await RefreshAllPanelsAsync(refreshServerIntegrity: false);
            SummaryStatusText = syncOk
                ? "선택한 sync outbox 재시도를 완료했습니다."
                : "선택한 sync outbox를 재시도했지만 일부 항목이 아직 남아 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryAllOutboxAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var resetCount = await _local.ResetAllPendingSyncOutboxEntriesForRetryAsync(_session);
            if (resetCount == 0)
            {
                SummaryStatusText = "재시도할 sync outbox 항목이 없습니다.";
                await RefreshOutboxAsync();
                return;
            }

            var syncOk = await _sync.FlushPendingChangesAsync();
            await RefreshAllPanelsAsync(refreshServerIntegrity: false);
            SummaryStatusText = syncOk
                ? $"대기 중인 sync outbox {resetCount:N0}건 재시도를 완료했습니다."
                : $"sync outbox {resetCount:N0}건을 재시도했지만 일부 항목이 남아 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearAcknowledgedOutboxAsync()
    {
        if (IsBusy)
            return;

        if (MessageBox.Show(
                "완료(Acknowledged) 상태의 sync outbox 기록을 모두 정리하시겠습니까?",
                "동기화 진단",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var clearedCount = await _local.ClearAcknowledgedSyncOutboxEntriesAsync(_session);
            await RefreshOutboxAsync();
            SummaryStatusText = clearedCount == 0
                ? "정리할 완료 outbox 기록이 없습니다."
                : $"완료 outbox 기록 {clearedCount:N0}건을 정리했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<string> BuildCombinedIntegrityMarkdownAsync(CancellationToken ct = default)
    {
        var localReport = await _local.BuildIntegrityReportAsync(_session, ct);
        IntegrityReportDto? serverReport = null;
        string serverStateMessage;

        if (_session.IsOfflineMode)
        {
            serverStateMessage = "오프라인 모드에서는 서버 무결성 리포트를 조회하지 않았습니다.";
        }
        else if (!_session.HasAdministrativePrivileges)
        {
            serverStateMessage = "현재 계정은 서버 무결성 리포트 조회 권한이 없습니다.";
        }
        else
        {
            try
            {
                serverReport = await _api.GetIntegrityReportAsync(ct);
                serverStateMessage = serverReport is null
                    ? "서버 무결성 리포트 응답이 비어 있습니다."
                    : "서버 무결성 리포트를 포함했습니다.";
            }
            catch (Exception ex)
            {
                serverStateMessage = $"서버 무결성 리포트 조회 실패: {GetCompactExceptionMessage(ex)}";
            }
        }

        return BuildCombinedIntegrityMarkdown(localReport, serverReport, serverStateMessage);
    }

    private static string BuildCombinedIntegrityMarkdown(LocalIntegrityReport localReport, IntegrityReportDto? serverReport, string serverStateMessage)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 통합 무결성 리포트");
        builder.AppendLine();
        builder.AppendLine($"- 생성시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 서버 리포트 상태: {serverStateMessage}");
        builder.AppendLine();

        builder.AppendLine("## 로컬 무결성 리포트");
        builder.AppendLine();
        builder.AppendLine(localReport.ToMarkdown().Trim());
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("## 서버 무결성 리포트");
        builder.AppendLine();

        if (serverReport is null)
        {
            builder.AppendLine("- 서버 무결성 리포트를 포함하지 못했습니다.");
            builder.AppendLine($"- 사유: {serverStateMessage}");
            return builder.ToString();
        }

        builder.AppendLine($"- 생성시각: {serverReport.GeneratedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 테넌트: {serverReport.TenantCode}");
        builder.AppendLine($"- 지점: {serverReport.OfficeCode}");
        builder.AppendLine($"- 이슈 수: {serverReport.IssueCount:N0}건");
        var serverRoutineRepairCandidateCount = serverReport.Issues.Count(issue =>
            IntegrityIssueReviewPolicy.IsRoutineRepairCandidateForServer(issue.Code));
        var serverInformationalIssueCount = serverReport.Issues.Count(IsInformationalServerIssue);
        var serverManualReviewCount = Math.Max(0, serverReport.Issues.Count - serverRoutineRepairCandidateCount - serverInformationalIssueCount);
        builder.AppendLine($"- 재계산/재동기화 후보: {serverRoutineRepairCandidateCount:N0}개");
        builder.AppendLine($"- 참고용 정보: {serverInformationalIssueCount:N0}개");
        builder.AppendLine($"- 수동 확인 필요: {serverManualReviewCount:N0}개");
        builder.AppendLine();

        if (serverReport.IssueCount == 0)
        {
            builder.AppendLine("- 서버 기준 무결성 이슈가 없습니다.");
            return builder.ToString();
        }

        builder.AppendLine("| 심각도 | 코드 | 건수 | 내용 | 수정 방법 |");
        builder.AppendLine("| --- | --- | ---: | --- | --- |");
        foreach (var issue in serverReport.Issues
                     .OrderByDescending(current => GetSeverityWeight(current.Severity))
                     .ThenByDescending(current => current.Count)
                     .ThenBy(current => current.Message, StringComparer.CurrentCulture))
        {
            builder.AppendLine($"| {issue.Severity} | {issue.Code} | {issue.Count:N0} | {EscapeMarkdown(issue.Message)} | {EscapeMarkdown(IntegrityIssueGuidance.GetSuggestedAction(issue.Code, issue.Message))} |");
        }

        return builder.ToString();
    }

    private static int GetSeverityWeight(string? severity)
        => string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase) ? 3
            : string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase) ? 2
            : 1;

    private static string EscapeMarkdown(string value)
        => (value ?? string.Empty).Replace("|", "\\|");

    private static string GetCompactExceptionMessage(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        if (string.IsNullOrWhiteSpace(message))
            return "알 수 없는 오류";

        return message.Length <= 220
            ? message
            : message[..220] + "...";
    }

    private static string BuildServerIntegritySummary(IntegrityReportDto report, int routineRepairCandidateCount, int informationalIssueCount, int manualReviewCount)
    {
        var lines = new List<string>();
        if (routineRepairCandidateCount > 0)
            lines.Add($"재계산/재동기화 후보 {routineRepairCandidateCount:N0}개 유형");
        if (informationalIssueCount > 0)
            lines.Add($"참고용 정보 {informationalIssueCount:N0}개 유형");
        if (manualReviewCount > 0)
            lines.Add($"수동 확인 필요 {manualReviewCount:N0}개 유형");

        lines.AddRange(report.Issues
            .OrderByDescending(issue => GetSeverityWeight(issue.Severity))
            .ThenByDescending(issue => issue.Count)
            .Take(4)
            .Select(issue => $"[{issue.Severity}] {issue.Message} ({issue.Count:N0}건)"));

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsInformationalServerIssue(IntegrityIssueDto issue)
        => string.Equals(issue.Severity, "Info", StringComparison.OrdinalIgnoreCase);

    private static int CountActionRequiredServerIssues(IntegrityReportDto? report)
        => report?.Issues.Count(issue => !IsInformationalServerIssue(issue)) ?? 0;

    private static string BuildServerIntegrityDetailExportFileName(IntegrityIssueDto issue)
    {
        var raw = $"server-integrity-{issue.Code}-{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }
}
