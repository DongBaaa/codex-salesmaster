using System.Text;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record LocalIntegrityIssue(
    string Code,
    string Severity,
    int Count,
    string Message,
    IReadOnlyList<string>? DetailRows = null)
{
    public string SuggestedAction => IntegrityIssueGuidance.GetSuggestedAction(Code, Message);
    public IReadOnlyList<string> EffectiveDetailRows => DetailRows ?? Array.Empty<string>();
}

public static class IntegrityIssueGuidance
{
    public static string GetSuggestedAction(string? code, string? message = null)
    {
        var normalizedCode = Normalize(code);
        var normalizedMessage = message ?? string.Empty;

        if (normalizedCode.Contains("sync_outbox_failed", StringComparison.Ordinal) ||
            normalizedCode.Contains("sync_outbox_sent_stuck", StringComparison.Ordinal))
        {
            return "동기화 진단에서 실패/대기 outbox를 확인한 뒤 재시도하고, 계속 실패하면 진단 리포트를 저장해 서버 오류 원인을 확인하세요.";
        }

        if (normalizedCode.StartsWith("out_of_scope_", StringComparison.Ordinal))
        {
            return "현재 계정 범위와 다른 캐시가 남은 상태입니다. 미동기화 변경을 먼저 서버에 반영한 뒤 공유 캐시 재구성 또는 재로그인으로 중앙 서버 기준 데이터를 다시 받으세요.";
        }

        if (normalizedCode.Contains("inventory_current_stock_snapshot_mismatch", StringComparison.Ordinal))
        {
            return "품목/재고 관리에서 해당 품목의 현재고와 창고별 재고를 비교하고, 전표·재고이동 반영 후 재고 재계산/동기화를 실행하세요.";
        }

        if (normalizedCode.Contains("inventory_nonstock_snapshot_residue", StringComparison.Ordinal))
        {
            return "재고 미관리 품목에 남은 현재고/창고재고를 0으로 정리하거나 재고관리 품목으로 전환한 뒤 저장하세요.";
        }

        if (normalizedCode.Contains("warehouse_stock", StringComparison.Ordinal) ||
            normalizedCode.Contains("stock_layer", StringComparison.Ordinal) ||
            normalizedCode.Contains("inventory_movement", StringComparison.Ordinal) ||
            normalizedCode.Contains("serial_ledger", StringComparison.Ordinal) ||
            normalizedCode.Contains("inventory_transfer_line", StringComparison.Ordinal))
        {
            return "품목 마스터에서 참조 품목이 실제로 삭제되었는지 확인하고, 필요한 품목을 복구하거나 관련 재고 원장/이동 내역을 올바른 품목으로 재연결하세요.";
        }

        if (normalizedCode.Contains("cross_tenant_inventory_transfers", StringComparison.Ordinal))
        {
            return "서로 다른 업체 간 직접 재고이동 문서입니다. 동일 업체/지점 범위의 정상 이동 문서로 재작성하거나 운영자 점검 후 정리하세요.";
        }

        if (normalizedCode.Contains("duplicate_rental_profile", StringComparison.Ordinal))
        {
            return "렌탈 청구관리에서 중복 프로필을 비교해 유지할 프로필 하나만 남기고, 포함 장비·청구 이력·미수금 연결을 정리한 뒤 중복 프로필을 보류/삭제하세요.";
        }

        if (normalizedCode.Contains("duplicate_rental_asset", StringComparison.Ordinal))
        {
            return "렌탈 자산 목록에서 같은 자산키/시리얼의 중복 자산을 비교하고, 실제 장비 1대당 자산 1건만 남기도록 청구 프로필 연결을 정리하세요.";
        }

        if (normalizedCode.Contains("orphan_rental_profile_customer", StringComparison.Ordinal))
        {
            return "렌탈 청구관리에서 해당 프로필의 거래처를 현재 거래처 마스터에 존재하는 거래처로 다시 연결하세요. 거래처가 삭제된 것이 맞다면 프로필 사용 여부를 확인해 보류/삭제하세요.";
        }

        if (normalizedCode.Contains("orphan_rental_asset_customer", StringComparison.Ordinal))
        {
            return "렌탈 자산 연결 화면에서 해당 장비의 거래처를 현재 거래처 마스터에 존재하는 거래처로 다시 선택해 저장하세요. 회수 장비라면 연결 해제/회수 처리 후 이력만 남기세요.";
        }

        if (normalizedCode.Contains("orphan_rental_asset_profile", StringComparison.Ordinal))
        {
            return "렌탈 자산 연결 화면에서 장비를 정상 렌탈 청구 프로필에 다시 연결하거나, 이미 회수된 장비라면 청구 프로필 연결을 해제하세요.";
        }

        if (normalizedCode.Contains("orphan_rental_asset_item", StringComparison.Ordinal))
        {
            return "렌탈 자산의 품목이 품목 마스터에 없습니다. 품목 마스터에서 누락 품목을 복구하거나, 자산의 품목을 현재 존재하는 품목으로 다시 선택해 저장하세요.";
        }

        if (normalizedCode.Contains("customer_master_category", StringComparison.Ordinal) ||
            normalizedCode.Contains("customer_category", StringComparison.Ordinal))
        {
            return "거래처 마스터/거래처 분류에서 삭제·누락된 분류를 복구하거나 거래처의 분류를 현재 존재하는 분류로 다시 선택해 저장하세요.";
        }

        if (normalizedCode.Contains("customer_master", StringComparison.Ordinal))
        {
            return "거래처 마스터에서 기준 거래처가 존재하는지 확인하고, 개별 거래처를 올바른 기준 거래처에 다시 연결하거나 기준 거래처를 복구하세요.";
        }

        if (normalizedCode.Contains("invoice_customer", StringComparison.Ordinal))
        {
            return "전표의 거래처가 거래처 마스터에 없습니다. 거래처를 복구하거나 전표 거래처를 현재 존재하는 거래처로 수정한 뒤 저장하세요.";
        }

        if (normalizedCode.Contains("transaction_invoice", StringComparison.Ordinal) ||
            normalizedCode.Contains("payment_invoice", StringComparison.Ordinal))
        {
            return "거래/수금 내역이 참조하는 전표를 확인해 전표를 복구하거나, 해당 거래/수금 내역의 전표 연결을 정상 전표로 다시 지정하세요.";
        }

        if (normalizedCode.Contains("attachment_transaction", StringComparison.Ordinal))
        {
            return "증빙 첨부가 참조하는 거래내역을 복구하거나, 필요 없는 고아 첨부라면 증빙 목록에서 정리하세요.";
        }

        if (normalizedCode.Contains("missing_attachment", StringComparison.Ordinal))
        {
            return "첨부 파일이 로컬에 없습니다. 동기화로 다시 내려받거나 원본 파일을 재첨부하세요.";
        }

        if (normalizedMessage.Contains("렌탈 자산", StringComparison.CurrentCultureIgnoreCase) &&
            normalizedMessage.Contains("품목", StringComparison.CurrentCultureIgnoreCase))
        {
            return "렌탈 자산의 품목 연결을 품목 마스터에 존재하는 품목으로 다시 저장하거나 누락 품목을 복구하세요.";
        }

        if (normalizedMessage.Contains("렌탈", StringComparison.CurrentCultureIgnoreCase) &&
            normalizedMessage.Contains("거래처", StringComparison.CurrentCultureIgnoreCase))
        {
            return "렌탈 청구관리/자산 연결에서 거래처를 현재 거래처 마스터에 존재하는 거래처로 다시 연결하세요.";
        }

        return "동기화 진단의 상세 리포트와 원본 화면을 함께 확인한 뒤, 삭제·누락된 기준 데이터를 복구하거나 현재 존재하는 기준 데이터로 다시 연결하세요.";
    }

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}

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

        builder.AppendLine("## 권장 조치 순서");
        builder.AppendLine();
        if (DirtyCount > 0)
            builder.AppendLine("- 미동기화 변경이 남아 있으므로 먼저 동기화를 완료한 뒤 정리 작업을 진행하세요.");
        if (PendingServerMirrorRefresh)
            builder.AppendLine("- 버전 변경 후 중앙 서버 기준 전체 재동기화가 대기 중이면 공유 캐시 재구성 또는 재로그인으로 중앙 데이터를 먼저 다시 받으세요.");
        builder.AppendLine("- 아래 표의 `수정 방법`을 기준으로 기준 데이터 복구 또는 재연결을 진행하세요.");
        builder.AppendLine("- `상세 내역`에 대상 행이 표시되는 항목은 해당 키/장비/거래처명을 검색해 원본 화면에서 수정하세요.");
        builder.AppendLine();

        builder.AppendLine("## 점검 항목");
        builder.AppendLine();
        builder.AppendLine("| 심각도 | 코드 | 건수 | 내용 | 수정 방법 |");
        builder.AppendLine("| --- | --- | ---: | --- | --- |");
        foreach (var issue in Issues
                     .OrderByDescending(current => GetSeverityWeight(current.Severity))
                     .ThenByDescending(current => current.Count)
                     .ThenBy(current => current.Message, StringComparer.CurrentCulture))
        {
            builder.AppendLine($"| {issue.Severity} | {issue.Code} | {issue.Count:N0} | {EscapeMarkdown(issue.Message)} | {EscapeMarkdown(issue.SuggestedAction)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## 상세 내역");
        builder.AppendLine();
        var issuesWithDetails = Issues
            .OrderByDescending(current => GetSeverityWeight(current.Severity))
            .ThenByDescending(current => current.Count)
            .ThenBy(current => current.Message, StringComparer.CurrentCulture)
            .Where(issue => issue.EffectiveDetailRows.Count > 0)
            .ToList();
        if (issuesWithDetails.Count == 0)
        {
            builder.AppendLine("- 이 리포트에서 개별 대상 행을 수집하지 못한 항목입니다. 위 점검 항목의 코드와 내용을 기준으로 동기화 진단/원본 화면에서 확인하세요.");
        }
        else
        {
            foreach (var issue in issuesWithDetails)
            {
                builder.AppendLine($"### {issue.Code} - {issue.Message}");
                builder.AppendLine();
                builder.AppendLine($"- 건수: {issue.Count:N0}건");
                builder.AppendLine($"- 수정 방법: {issue.SuggestedAction}");
                builder.AppendLine("- 대상:");
                foreach (var detailRow in issue.EffectiveDetailRows)
                    builder.AppendLine($"  - {EscapeMarkdown(detailRow)}");
                builder.AppendLine();
            }
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
