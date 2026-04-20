using System.Text;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record LocalIntegrityIssue(
    string Code,
    string Severity,
    int Count,
    string Message);

public sealed class LocalIntegrityReport
{
    public LocalIntegrityReport(
        DateTime createdAtUtc,
        string officeCode,
        string tenantCode,
        int dirtyCount,
        bool pendingServerMirrorRefresh,
        IReadOnlyList<LocalIntegrityIssue> issues)
    {
        CreatedAtUtc = createdAtUtc;
        OfficeCode = officeCode;
        TenantCode = tenantCode;
        DirtyCount = dirtyCount;
        PendingServerMirrorRefresh = pendingServerMirrorRefresh;
        Issues = issues;
    }

    public DateTime CreatedAtUtc { get; }
    public string OfficeCode { get; }
    public string TenantCode { get; }
    public int DirtyCount { get; }
    public bool PendingServerMirrorRefresh { get; }
    public IReadOnlyList<LocalIntegrityIssue> Issues { get; }
    public bool HasIssues => PendingServerMirrorRefresh || Issues.Count > 0;
    public bool RequiresFullMirrorRefresh => HasIssues;
    public int RoutineRepairCandidateIssueTypeCount =>
        (PendingServerMirrorRefresh ? 1 : 0) +
        Issues.Count(issue => IntegrityIssueReviewPolicy.IsRoutineRepairCandidateForLocal(issue.Code));
    public int ManualReviewIssueTypeCount =>
        Issues.Count(issue => !IntegrityIssueReviewPolicy.IsRoutineRepairCandidateForLocal(issue.Code));

    public string BuildSummaryText(int maxIssues = 4)
    {
        var lines = new List<string>();
        if (PendingServerMirrorRefresh)
            lines.Add("버전 변경 후 중앙 서버 기준 전체 재동기화가 대기 중입니다.");

        if (RoutineRepairCandidateIssueTypeCount > 0)
            lines.Add($"재동기화/재계산으로 정리 가능한 후보가 {RoutineRepairCandidateIssueTypeCount:N0}개 항목 있습니다.");

        if (ManualReviewIssueTypeCount > 0)
            lines.Add($"중복 키·고아 참조·첨부 누락처럼 수동 확인이 필요한 항목이 {ManualReviewIssueTypeCount:N0}개 있습니다.");

        foreach (var issue in Issues
                     .OrderByDescending(issue => GetSeverityWeight(issue.Severity))
                     .ThenByDescending(issue => issue.Count)
                     .ThenBy(issue => issue.Message, StringComparer.CurrentCulture)
                     .Take(Math.Max(1, maxIssues)))
        {
            lines.Add(issue.Count > 0
                ? $"{issue.Message} ({issue.Count:N0}건)"
                : issue.Message);
        }

        var hiddenCount = Math.Max(0, Issues.Count - Math.Max(1, maxIssues));
        if (hiddenCount > 0)
            lines.Add($"그 외 {hiddenCount:N0}개 항목은 무결성 리포트에서 확인하세요.");

        if (DirtyCount > 0)
            lines.Add($"현재 미동기화 변경 {DirtyCount:N0}건이 있어 전체 재동기화 전 동기화 정리가 필요합니다.");

        return lines.Count == 0 ? "확인된 무결성 문제는 없습니다." : string.Join(Environment.NewLine, lines);
    }

    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 무결성 점검 리포트");
        builder.AppendLine();
        builder.AppendLine($"- 생성시각: {CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 담당지점: {OfficeCode}");
        builder.AppendLine($"- 테넌트: {TenantCode}");
        builder.AppendLine($"- 미동기화 변경: {DirtyCount:N0}건");
        builder.AppendLine($"- 버전 변경 후 전체 재동기화 대기: {(PendingServerMirrorRefresh ? "예" : "아니오")}");
        builder.AppendLine($"- 무결성 점검 항목: {Issues.Count:N0}건");
        builder.AppendLine($"- 재동기화/재계산 후보: {RoutineRepairCandidateIssueTypeCount:N0}개");
        builder.AppendLine($"- 수동 확인 필요: {ManualReviewIssueTypeCount:N0}개");
        builder.AppendLine();

        if (Issues.Count == 0)
        {
            builder.AppendLine("## 결과");
            builder.AppendLine();
            builder.AppendLine(PendingServerMirrorRefresh
                ? "- 버전 변경 후 전체 재동기화 대기 외 별도 이상 항목은 없습니다."
                : "- 확인된 이상 항목이 없습니다.");
            return builder.ToString();
        }

        builder.AppendLine("## 점검 항목");
        builder.AppendLine();
        builder.AppendLine("| 심각도 | 코드 | 건수 | 내용 |");
        builder.AppendLine("| --- | --- | ---: | --- |");
        foreach (var issue in Issues
                     .OrderByDescending(current => GetSeverityWeight(current.Severity))
                     .ThenByDescending(current => current.Count)
                     .ThenBy(current => current.Message, StringComparer.CurrentCulture))
        {
            builder.AppendLine($"| {issue.Severity} | {issue.Code} | {issue.Count:N0} | {EscapeMarkdown(issue.Message)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 요약");
        builder.AppendLine();
        foreach (var line in BuildSummaryText().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            builder.AppendLine($"- {line}");

        return builder.ToString();
    }

    private static int GetSeverityWeight(string severity)
        => severity switch
        {
            "Error" => 3,
            "Warning" => 2,
            "Info" => 1,
            _ => 0
        };

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

public sealed record PendingSyncBucket(
    string ScopeKey,
    string ScopeDisplayName,
    string EntityDisplayName,
    int Count);

public sealed record PendingSyncSummary(
    int TotalCount,
    IReadOnlyList<PendingSyncBucket> Buckets)
{
    public PendingSyncBucket? PrimaryBucket => Buckets
        .OrderByDescending(bucket => bucket.Count)
        .ThenBy(bucket => bucket.ScopeDisplayName, StringComparer.Ordinal)
        .ThenBy(bucket => bucket.EntityDisplayName, StringComparer.Ordinal)
        .FirstOrDefault();

    public string BuildWaitingMessage(string? prefix = null)
    {
        if (TotalCount <= 0)
            return string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim();

        var primary = PrimaryBucket;
        var waitingMessage = primary is null
            ? $"서버 반영 대기 데이터 {TotalCount:N0}건이 남아 있습니다."
            : TotalCount == primary.Count
                ? $"{primary.ScopeDisplayName} {primary.EntityDisplayName} {primary.Count:N0}건이 서버 반영 대기 중입니다."
                : $"{primary.ScopeDisplayName} {primary.EntityDisplayName} {primary.Count:N0}건 포함 총 {TotalCount:N0}건이 서버 반영 대기 중입니다.";

        return string.IsNullOrWhiteSpace(prefix)
            ? waitingMessage
            : $"{prefix.Trim()} {waitingMessage}".Trim();
    }
}

public sealed record PendingSyncBlockingReason(
    string ScopeKey,
    string ScopeDisplayName,
    string EntityDisplayName,
    int PendingCount,
    string Message,
    string RequiredOfficeCode,
    bool HasStoredCredential,
    bool IsCurrentScope);

public sealed record StoredSyncCredential(
    string OfficeCode,
    string TenantCode,
    string Username,
    string Password,
    DateTime SavedAtUtc);

public sealed record DirtyOfficeSummary(
    string OfficeCode,
    string TenantCode,
    int Count);

public sealed class SyncOutboxListItem
{
    public Guid Id { get; init; }
    public string MutationId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string EntityName { get; init; } = string.Empty;
    public Guid EntityId { get; init; }
    public long ExpectedRevision { get; init; }
    public string TenantCode { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
    public string Status { get; init; } = "Prepared";
    public string ErrorMessage { get; init; } = string.Empty;
    public DateTime PreparedAtUtc { get; init; }
    public DateTime? SentAtUtc { get; init; }
    public DateTime? AcknowledgedAtUtc { get; init; }

    public bool IsAcknowledged => string.Equals(Status, "Acknowledged", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "Failed", StringComparison.OrdinalIgnoreCase);
    public string EntityIdText => EntityId == Guid.Empty ? "-" : EntityId.ToString("N");
    public string ShortMutationId => MutationId.Length <= 36 ? MutationId : MutationId[..36] + "...";
}

public sealed record SyncOutboxSummary(
    int TotalCount,
    int PendingCount,
    int FailedCount,
    int AcknowledgedCount);

public sealed record SyncOutboxRecoveryResult(
    int ResetSentCount,
    int FailedCount,
    int PendingCount)
{
    public bool RecoveredAny => ResetSentCount > 0;

    public string BuildSummaryText()
    {
        var parts = new List<string>();
        if (ResetSentCount > 0)
            parts.Add($"중단된 sync outbox {ResetSentCount:N0}건을 재시도 대기 상태로 복구했습니다.");
        if (FailedCount > 0)
            parts.Add($"실패 상태 sync outbox {FailedCount:N0}건은 동기화 진단에서 추가 확인이 필요합니다.");
        if (PendingCount > 0)
            parts.Add($"현재 미정리 sync outbox는 {PendingCount:N0}건입니다.");

        return string.Join(" ", parts);
    }
}

public sealed record InventoryIntegrityRepairResult(
    int RemovedCrossTenantTransferCount,
    bool RebuiltInventorySnapshots)
{
    public bool RepairedAny => RemovedCrossTenantTransferCount > 0 || RebuiltInventorySnapshots;

    public string BuildSummaryText(string? backupFileName = null)
    {
        var parts = new List<string>();
        if (RemovedCrossTenantTransferCount > 0)
            parts.Add($"업체 간 직접 재고이동 문서 {RemovedCrossTenantTransferCount:N0}건을 자동 삭제했습니다.");
        if (RebuiltInventorySnapshots)
            parts.Add("재고 원장/스냅샷 불일치를 자동 재계산했습니다.");

        var message = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(backupFileName))
            message = string.IsNullOrWhiteSpace(message)
                ? $"백업: {backupFileName}"
                : $"{message} 백업: {backupFileName}";

        return message;
    }
}
