using System.IO;

namespace 거래플랜.Desktop.App.Services;

public sealed record StartupIntegrityRunResult(
    LocalIntegrityReport Report,
    bool RefreshAttempted,
    bool RefreshSucceeded,
    bool RequiresUserAttention,
    string Message,
    string? BackupPath);

public sealed class StartupIntegrityService
{
    private static readonly HashSet<string> AutoRepairableInventoryIssueCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inventory_current_stock_snapshot_mismatch",
        "inventory_nonstock_snapshot_residue",
        "inventory_deleted_item_stock_residue",
        "cross_tenant_inventory_transfers",
        "orphan_item_warehouse_stock_refs",
        "orphan_stock_layer_item_refs",
        "orphan_inventory_movement_item_refs",
        "orphan_serial_ledger_item_refs"
    };

    private readonly LocalStateService _local;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly SessionState _session;

    public StartupIntegrityService(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        SessionState session)
    {
        _local = local;
        _sync = sync;
        _backup = backup;
        _session = session;
    }

    public async Task<StartupIntegrityRunResult> RunAsync(CancellationToken ct = default)
    {
        var autoRepairMessages = new List<string>();
        string? autoRepairBackupPath = null;
        var outboxRecovery = await _local.RecoverStaleSyncOutboxEntriesAsync(ct);
        if (outboxRecovery.RecoveredAny)
            autoRepairMessages.Add(outboxRecovery.BuildSummaryText());

        var clearedInvalidCredentialCount = await _local.ClearInvalidOfficeSyncCredentialsAsync(ct);
        if (clearedInvalidCredentialCount > 0)
            autoRepairMessages.Add($"잘못 저장된 지점 동기화 자격정보 {clearedInvalidCredentialCount:N0}건을 자동 정리했습니다.");

        var normalizedSharedOptionIdCount = await _local.NormalizeSharedOptionIdCasingAsync(ct);
        if (normalizedSharedOptionIdCount > 0)
            autoRepairMessages.Add($"공유 선택옵션 ID 표기 {normalizedSharedOptionIdCount:N0}건을 자동 정리했습니다.");

        var report = await _local.BuildIntegrityReportAsync(_session, ct);
        if (ShouldAutoRepairInventoryIssues(report, _session))
        {
            autoRepairBackupPath = await _backup.BackupNowWithPathAsync(ct);
            var repairResult = await _local.RepairInventoryIntegrityForStartupAsync(_session, ct);
            if (repairResult.RepairedAny)
            {
                autoRepairMessages.Add(repairResult.BuildSummaryText(
                    string.IsNullOrWhiteSpace(autoRepairBackupPath)
                        ? null
                        : Path.GetFileName(autoRepairBackupPath)));
                report = await _local.BuildIntegrityReportAsync(_session, ct);
            }
        }

        var autoRepairSummary = string.Join(" ", autoRepairMessages);
        if (!report.RequiresFullMirrorRefresh)
        {
            return new StartupIntegrityRunResult(
                report,
                RefreshAttempted: false,
                RefreshSucceeded: false,
                RequiresUserAttention: false,
                Message: autoRepairSummary,
                BackupPath: autoRepairBackupPath);
        }

        if (_session.IsOfflineMode)
        {
            return new StartupIntegrityRunResult(
                report,
                RefreshAttempted: false,
                RefreshSucceeded: false,
                RequiresUserAttention: true,
                Message: CombineMessages(autoRepairSummary, "시작 시 무결성 점검에서 중앙 서버 기준 전체 재동기화가 필요하다고 판단했지만 현재는 오프라인 모드입니다.\n환경설정 > 동기화에서 온라인 접속 후 전체 재동기화를 실행하세요.\n\n" + report.BuildSummaryText()),
                BackupPath: null);
        }

        if (report.DirtyCount > 0)
        {
            return new StartupIntegrityRunResult(
                report,
                RefreshAttempted: false,
                RefreshSucceeded: false,
                RequiresUserAttention: true,
                Message: CombineMessages(autoRepairSummary, $"시작 시 무결성 점검에서 중앙 서버 기준 전체 재동기화가 필요하다고 판단했지만 미동기화 변경 {report.DirtyCount:N0}건이 있어 자동 실행하지 않았습니다.\n먼저 동기화를 완료한 뒤 환경설정 > 동기화에서 전체 재동기화를 실행하세요.\n\n{report.BuildSummaryText()}"),
                BackupPath: null);
        }

        var backupPath = await _backup.BackupNowWithPathAsync(ct);
        var refreshSucceeded = await _sync.RefreshSharedMirrorFromServerAsync(ct);
        if (!refreshSucceeded)
        {
            return new StartupIntegrityRunResult(
                report,
                RefreshAttempted: true,
                RefreshSucceeded: false,
                RequiresUserAttention: true,
                Message: CombineMessages(autoRepairSummary, "시작 시 중앙 서버 기준 전체 재동기화를 자동 실행했지만 실패했습니다.\n환경설정 > 동기화에서 전체 재동기화를 다시 실행하고, 필요하면 동기화 진단 리포트를 저장하세요."),
                BackupPath: backupPath ?? autoRepairBackupPath);
        }

        var refreshedReport = await _local.BuildIntegrityReportAsync(_session, ct);
        if (refreshedReport.HasIssues)
        {
            return new StartupIntegrityRunResult(
                refreshedReport,
                RefreshAttempted: true,
                RefreshSucceeded: true,
                RequiresUserAttention: true,
                Message: CombineMessages(autoRepairSummary, "시작 시 중앙 서버 기준 전체 재동기화를 자동 실행했지만 일부 점검 항목이 남아 있습니다.\n환경설정 > 동기화 > 무결성 리포트로 세부 내용을 확인하세요.\n\n" + refreshedReport.BuildSummaryText()),
                BackupPath: backupPath ?? autoRepairBackupPath);
        }

        var successMessage = string.IsNullOrWhiteSpace(backupPath)
            ? "시작 시 중앙 서버 기준 전체 재동기화를 자동 완료했습니다."
            : $"시작 시 중앙 서버 기준 전체 재동기화를 자동 완료했습니다. 백업: {Path.GetFileName(backupPath)}";

        return new StartupIntegrityRunResult(
            refreshedReport,
            RefreshAttempted: true,
            RefreshSucceeded: true,
            RequiresUserAttention: false,
            Message: CombineMessages(autoRepairSummary, successMessage),
            BackupPath: backupPath ?? autoRepairBackupPath);
    }

    private static string CombineMessages(string primary, string secondary)
    {
        if (string.IsNullOrWhiteSpace(primary))
            return secondary;
        if (string.IsNullOrWhiteSpace(secondary))
            return primary;

        return primary + Environment.NewLine + Environment.NewLine + secondary;
    }

    private static bool ShouldAutoRepairInventoryIssues(LocalIntegrityReport report, SessionState session)
    {
        if (report.PendingServerMirrorRefresh || report.DirtyCount > 0 || report.Issues.Count == 0)
            return false;
        if (!session.IsLoggedIn || session.IsOfflineMode || !session.HasAdministrativePrivileges)
            return false;

        return report.Issues.All(issue => AutoRepairableInventoryIssueCodes.Contains(issue.Code));
    }
}
