using System.IO;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public sealed record ServerClockCheckResult(
    DateOnly ServerToday,
    bool WarningRequired,
    string WarningMessage,
    TimeSpan ClockSkew);

public sealed record PeriodicIntegrityMonitorResult(
    bool Executed,
    bool WarningRequired,
    string StatusMessage,
    string WarningMessage);

public sealed class RuntimeSafetyMonitorService
{
    private const string ClockWarningSettingKey = "Runtime.ClockSkewWarning.LastShownAtUtc";
    private const string PeriodicIntegrityRunSettingKey = "Runtime.PeriodicIntegrity.LastRunAtUtc";
    private const string PeriodicIntegrityWarningSettingKey = "Runtime.PeriodicIntegrity.LastWarningAtUtc";
    private static readonly TimeSpan ClockSkewThreshold = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ClockWarningCooldown = TimeSpan.FromHours(12);
    private static readonly TimeSpan PeriodicIntegrityInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan PeriodicIntegrityWarningCooldown = TimeSpan.FromHours(6);

    private readonly LocalStateService _local;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly SessionState _session;
    private readonly ErpApiClient _api;
    private readonly SyncDiagnosticsService _diagnostics;

    public RuntimeSafetyMonitorService(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        SessionState session,
        ErpApiClient api,
        SyncDiagnosticsService diagnostics)
    {
        _local = local;
        _sync = sync;
        _backup = backup;
        _session = session;
        _api = api;
        _diagnostics = diagnostics;
    }

    public async Task<ServerClockCheckResult> ResolveServerTodayAsync(CancellationToken ct = default)
    {
        if (_session.IsOfflineMode)
        {
            return new ServerClockCheckResult(
                DateOnly.FromDateTime(DateTime.Today),
                WarningRequired: false,
                WarningMessage: string.Empty,
                ClockSkew: TimeSpan.Zero);
        }

        try
        {
            var syncStatus = await _api.GetSyncStatusAsync(ct);
            if (syncStatus is null)
            {
                return new ServerClockCheckResult(
                    DateOnly.FromDateTime(DateTime.Today),
                    WarningRequired: false,
                    WarningMessage: string.Empty,
                    ClockSkew: TimeSpan.Zero);
            }

            var serverUtc = syncStatus.ServerUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(syncStatus.ServerUtc, DateTimeKind.Utc)
                : syncStatus.ServerUtc.ToUniversalTime();
            var serverLocal = TimeZoneInfo.ConvertTimeFromUtc(serverUtc, TimeZoneInfo.Local);
            var clockSkew = (DateTime.UtcNow - serverUtc).Duration();
            var warningRequired = false;
            var warningMessage = string.Empty;

            if (clockSkew >= ClockSkewThreshold &&
                await TryMarkWarningAsync(ClockWarningSettingKey, ClockWarningCooldown, ct))
            {
                warningRequired = true;
                warningMessage =
                    $"현재 PC 시간과 서버 시간 차이가 약 {Math.Round(clockSkew.TotalMinutes, 1):0.#}분입니다.{Environment.NewLine}{Environment.NewLine}" +
                    "PC 시간 설정을 확인하세요. 시간 차이가 크면 청구일/계약일/동기화 판단이 어긋날 수 있습니다.";

                await _diagnostics.RecordIssueAsync(
                    phase: "runtime-clock-skew",
                    rawMessage: warningMessage,
                    severity: "Warning",
                    ct: ct);
            }

            return new ServerClockCheckResult(
                DateOnly.FromDateTime(serverLocal),
                warningRequired,
                warningMessage,
                clockSkew);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RUNTIME", $"서버 시간 점검 실패: {ex.Message}");
            return new ServerClockCheckResult(
                DateOnly.FromDateTime(DateTime.Today),
                WarningRequired: false,
                WarningMessage: string.Empty,
                ClockSkew: TimeSpan.Zero);
        }
    }

    public async Task<PeriodicIntegrityMonitorResult> RunPeriodicIntegrityAsync(bool force = false, CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn || _session.IsOfflineMode)
        {
            return new PeriodicIntegrityMonitorResult(
                Executed: false,
                WarningRequired: false,
                StatusMessage: string.Empty,
                WarningMessage: string.Empty);
        }

        if (!force && !await IsPeriodicRunDueAsync(ct))
        {
            return new PeriodicIntegrityMonitorResult(
                Executed: false,
                WarningRequired: false,
                StatusMessage: string.Empty,
                WarningMessage: string.Empty);
        }

        await _local.SetSettingAsync(PeriodicIntegrityRunSettingKey, DateTime.UtcNow.ToString("O"), ct);

        var report = await _local.BuildIntegrityReportAsync(_session, ct);
        var autoRecoveryAttempted = false;
        var autoRecoverySucceeded = false;
        string? backupPath = null;

        if (report.RequiresFullMirrorRefresh &&
            !_sync.HasActiveOrQueuedSync &&
            !await _local.HasPendingSyncChangesAsync(ct))
        {
            autoRecoveryAttempted = true;
            backupPath = await _backup.BackupNowWithPathAsync(ct);
            autoRecoverySucceeded = await _sync.RefreshSharedMirrorFromServerAsync(ct);
            if (autoRecoverySucceeded)
                report = await _local.BuildIntegrityReportAsync(_session, ct);
        }

        if (!report.HasIssues)
        {
            var successMessage = autoRecoveryAttempted
                ? string.IsNullOrWhiteSpace(backupPath)
                    ? "주기 무결성 점검에서 자동 재동기화까지 완료했습니다."
                    : $"주기 무결성 점검에서 자동 재동기화까지 완료했습니다. 백업: {Path.GetFileName(backupPath)}"
                : "주기 무결성 점검 결과 이상이 없습니다.";

            return new PeriodicIntegrityMonitorResult(
                Executed: true,
                WarningRequired: false,
                StatusMessage: successMessage,
                WarningMessage: string.Empty);
        }

        var warningMessage = autoRecoveryAttempted && !autoRecoverySucceeded
            ? "주기 무결성 점검 중 자동 재동기화를 시도했지만 완료하지 못했습니다."
            : "주기 무결성 점검에서 수동 확인이 필요한 항목이 남아 있습니다.";
        warningMessage += Environment.NewLine + Environment.NewLine + report.BuildSummaryText();

        await _diagnostics.RecordIssueAsync(
            phase: "runtime-periodic-integrity",
            rawMessage: warningMessage,
            severity: "Warning",
            recoveryAttempted: autoRecoveryAttempted,
            recoverySucceeded: autoRecoverySucceeded,
            ct: ct);

        var warningRequired = await TryMarkWarningAsync(
            PeriodicIntegrityWarningSettingKey,
            PeriodicIntegrityWarningCooldown,
            ct);

        var statusMessage = autoRecoveryAttempted && autoRecoverySucceeded
            ? "주기 무결성 점검에서 일부 자동 복구를 완료했습니다. 남은 항목을 확인하세요."
            : "주기 무결성 점검에서 확인이 필요한 항목이 감지되었습니다.";

        return new PeriodicIntegrityMonitorResult(
            Executed: true,
            WarningRequired: warningRequired,
            StatusMessage: statusMessage,
            WarningMessage: warningMessage);
    }

    private async Task<bool> IsPeriodicRunDueAsync(CancellationToken ct)
    {
        var lastRunRaw = await _local.GetSettingAsync(PeriodicIntegrityRunSettingKey, ct);
        if (!DateTime.TryParse(lastRunRaw, out var lastRunUtc))
            return true;

        return DateTime.UtcNow - lastRunUtc.ToUniversalTime() >= PeriodicIntegrityInterval;
    }

    private async Task<bool> TryMarkWarningAsync(string settingKey, TimeSpan cooldown, CancellationToken ct)
    {
        var lastShownRaw = await _local.GetSettingAsync(settingKey, ct);
        if (DateTime.TryParse(lastShownRaw, out var lastShownUtc) &&
            DateTime.UtcNow - lastShownUtc.ToUniversalTime() < cooldown)
        {
            return false;
        }

        await _local.SetSettingAsync(settingKey, DateTime.UtcNow.ToString("O"), ct);
        return true;
    }
}
