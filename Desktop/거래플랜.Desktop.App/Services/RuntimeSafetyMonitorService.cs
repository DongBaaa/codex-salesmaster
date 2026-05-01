using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
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
    string WarningMessage,
    string DetailReportPath = "");

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
    private readonly IServiceScopeFactory? _scopeFactory;

    public RuntimeSafetyMonitorService(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        SessionState session,
        ErpApiClient api,
        SyncDiagnosticsService diagnostics,
        IServiceScopeFactory? scopeFactory = null)
    {
        _local = local;
        _sync = sync;
        _backup = backup;
        _session = session;
        _api = api;
        _diagnostics = diagnostics;
        _scopeFactory = scopeFactory;
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
                await WithScopedRuntimeServicesAsync(
                    (local, _, _) => TryMarkWarningAsync(local, ClockWarningSettingKey, ClockWarningCooldown, ct)))
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

        return await WithScopedRuntimeServicesAsync(
            (local, sync, backup) => RunPeriodicIntegrityCoreAsync(local, sync, backup, force, ct));
    }

    private async Task<PeriodicIntegrityMonitorResult> RunPeriodicIntegrityCoreAsync(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        bool force,
        CancellationToken ct)
    {
        if (!force && !await IsPeriodicRunDueAsync(local, ct))
        {
            return new PeriodicIntegrityMonitorResult(
                Executed: false,
                WarningRequired: false,
                StatusMessage: string.Empty,
                WarningMessage: string.Empty);
        }

        await local.SetSettingAsync(PeriodicIntegrityRunSettingKey, DateTime.UtcNow.ToString("O"), ct);

        var report = await local.BuildIntegrityReportAsync(_session, ct);
        var autoRecoveryAttempted = false;
        var autoRecoverySucceeded = false;
        string? backupPath = null;

        if (report.RequiresFullMirrorRefresh &&
            !_sync.HasActiveOrQueuedSync &&
            !sync.HasActiveOrQueuedSync &&
            !await local.HasPendingSyncChangesAsync(ct))
        {
            autoRecoveryAttempted = true;
            backupPath = await backup.BackupNowWithPathAsync(ct);
            autoRecoverySucceeded = await sync.RefreshSharedMirrorFromServerAsync(ct);
            if (autoRecoverySucceeded)
                report = await local.BuildIntegrityReportAsync(_session, ct);
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

        var detailReportPath = await WritePeriodicIntegrityReportAsync(
            report,
            autoRecoveryAttempted,
            autoRecoverySucceeded,
            backupPath,
            ct);

        var warningMessage = autoRecoveryAttempted && !autoRecoverySucceeded
            ? "주기 무결성 점검 중 자동 재동기화를 시도했지만 완료하지 못했습니다."
            : "주기 무결성 점검에서 수동 확인이 필요한 항목이 남아 있습니다.";
        warningMessage += Environment.NewLine + Environment.NewLine + report.BuildSummaryText();
        if (!string.IsNullOrWhiteSpace(detailReportPath))
        {
            warningMessage +=
                Environment.NewLine + Environment.NewLine +
                "상세 내역과 수정 방법 리포트를 저장했습니다." +
                Environment.NewLine +
                detailReportPath;
        }

        await _diagnostics.RecordIssueAsync(
            phase: "runtime-periodic-integrity",
            rawMessage: warningMessage,
            severity: "Warning",
            recoveryAttempted: autoRecoveryAttempted,
            recoverySucceeded: autoRecoverySucceeded,
            ct: ct);

        var warningRequired = await TryMarkWarningAsync(
            local,
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
            WarningMessage: warningMessage,
            DetailReportPath: detailReportPath);
    }

    private static async Task<string> WritePeriodicIntegrityReportAsync(
        LocalIntegrityReport report,
        bool autoRecoveryAttempted,
        bool autoRecoverySucceeded,
        string? backupPath,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(AppPaths.DiagnosticsDir, $"periodic-integrity-{stamp}.md");
            var builder = new StringBuilder();
            builder.AppendLine("# 주기 무결성 점검 상세 내역");
            builder.AppendLine();
            builder.AppendLine($"- 생성시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"- 자동 재동기화 시도: {(autoRecoveryAttempted ? "예" : "아니오")}");
            builder.AppendLine($"- 자동 재동기화 완료: {(autoRecoverySucceeded ? "예" : "아니오")}");
            if (!string.IsNullOrWhiteSpace(backupPath))
                builder.AppendLine($"- 자동 재동기화 전 백업: {backupPath}");
            builder.AppendLine();
            builder.AppendLine("이 리포트는 주기 무결성 점검 팝업에서 자동 저장되었습니다. `수정 방법`과 `상세 내역`을 기준으로 원본 화면에서 정리하세요.");
            builder.AppendLine();
            builder.AppendLine(report.ToMarkdown().Trim());
            builder.AppendLine();

            await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, ct);
            return path;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RUNTIME", $"주기 무결성 상세 리포트 저장 실패: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<bool> IsPeriodicRunDueAsync(LocalStateService local, CancellationToken ct)
    {
        var lastRunRaw = await local.GetSettingAsync(PeriodicIntegrityRunSettingKey, ct);
        if (!DateTime.TryParse(lastRunRaw, out var lastRunUtc))
            return true;

        return DateTime.UtcNow - lastRunUtc.ToUniversalTime() >= PeriodicIntegrityInterval;
    }

    private async Task<bool> TryMarkWarningAsync(LocalStateService local, string settingKey, TimeSpan cooldown, CancellationToken ct)
    {
        var lastShownRaw = await local.GetSettingAsync(settingKey, ct);
        if (DateTime.TryParse(lastShownRaw, out var lastShownUtc) &&
            DateTime.UtcNow - lastShownUtc.ToUniversalTime() < cooldown)
        {
            return false;
        }

        await local.SetSettingAsync(settingKey, DateTime.UtcNow.ToString("O"), ct);
        return true;
    }

    private async Task<T> WithScopedRuntimeServicesAsync<T>(
        Func<LocalStateService, SyncService, BackupService, Task<T>> action)
    {
        if (_scopeFactory is null)
            return await action(_local, _sync, _backup);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var local = scope.ServiceProvider.GetRequiredService<LocalStateService>();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        return await action(local, sync, backup);
    }
}
