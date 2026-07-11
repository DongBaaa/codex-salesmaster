using System.Text;
using System.Text.RegularExpressions;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileDiagnosticExportService
{
    private const int MaxRecentLogLines = 20;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly Regex TimestampedLogLinePattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}\.\d{3})\s\[(?<level>WARN|ERROR)\]\s\[(?<category>[A-Za-z0-9_-]{1,32})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly JsonSyncStateStore _syncStateStore;

    public MobileDiagnosticExportService(
        SettingsService settings,
        SessionStore sessionStore,
        JsonSyncStateStore syncStateStore)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _syncStateStore = syncStateStore;
    }

    public async Task<string> ExportAndShareAsync(CancellationToken ct = default)
    {
        var session = _sessionStore.GetSnapshot();
        var state = await _syncStateStore.LoadAsync(ct);
        var pendingSummary = MobilePendingScopeFilter.CreateSummary(session, state);
        var content = await BuildDiagnosticContentAsync(session, state, pendingSummary, ct);

        string exportPath;
        try
        {
            exportPath = await SaveDiagnosticFileAsync(content, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "진단 파일을 저장하지 못했습니다. 저장 공간을 확인한 뒤 다시 시도해 주세요.",
                ex);
        }

        try
        {
            await ShareFileAsync(exportPath);
            return exportPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "공유 화면을 열지 못했습니다. 공유 가능한 앱이 있는지 확인한 뒤 다시 시도해 주세요.",
                ex);
        }
    }

    private async Task<string> BuildDiagnosticContentAsync(
        SessionSnapshot session,
        MobileSyncState state,
        MobilePendingScopeSummary pendingSummary,
        CancellationToken ct)
    {
        var recentLogMetadata = await ReadRecentErrorLogMetadataAsync(ct);
        var builder = new StringBuilder();

        builder.AppendLine("거래플랜 모바일 진단 정보");
        builder.AppendLine($"생성 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        builder.AppendLine("[앱]");
        builder.AppendLine($"앱 버전: {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString})");
        builder.AppendLine($"플랫폼: {DeviceInfo.Current.Platform}");
        builder.AppendLine($"OS 버전: {DeviceInfo.Current.VersionString}");
        builder.AppendLine($"기기: {MobileDiagnosticTextRedactor.SanitizeFreeText(DeviceInfo.Current.Manufacturer)} / {MobileDiagnosticTextRedactor.SanitizeFreeText(DeviceInfo.Current.Model)}");
        builder.AppendLine($"기기 유형: {DeviceInfo.Current.Idiom} / {DeviceInfo.Current.DeviceType}");
        builder.AppendLine();

        builder.AppendLine("[연결]");
        builder.AppendLine($"BaseUrl: {_settings.GetSanitizedBaseUrlForDiagnostics()}");
        builder.AppendLine("모바일 기능 차이: 판매·구매·수금/지급 입력 가능, 재고이동·렌탈 조회 전용");
        builder.AppendLine();

        builder.AppendLine("[로그인 사용자]");
        builder.AppendLine($"로그인 상태: {(session.IsAuthenticated ? "로그인됨" : "로그인 전")}");
        builder.AppendLine($"사용자명: {ValueOrDash(session.Username)}");
        builder.AppendLine($"Tenant: {ValueOrDash(session.TenantCode)}");
        builder.AppendLine($"Office: {ValueOrDash(session.OfficeCode)}");
        builder.AppendLine($"Scope: {ValueOrDash(session.ScopeType)}");
        builder.AppendLine();

        builder.AppendLine("[동기화 상태]");
        builder.AppendLine($"상태: {BuildSyncStatus(state)}");
        builder.AppendLine($"마지막 성공 시각: {FormatLocalDateTime(state.LastSuccessUtc)}");
        builder.AppendLine($"마지막 시도 시각: {FormatLocalDateTime(state.LastAttemptUtc)}");
        builder.AppendLine($"마지막 백그라운드 확인: {FormatLocalDateTime(state.LastBackgroundSyncUtc)}");
        builder.AppendLine($"마지막 오류 요약: {MobileDiagnosticTextRedactor.SanitizeFreeText(state.LastError)}");
        builder.AppendLine($"현재 Revision: {state.LastRevision:N0}");
        builder.AppendLine($"연속 실패 횟수: {state.ConsecutiveFailureCount:N0}");
        builder.AppendLine($"캐시 표시 허용: {(state.LastFailureAllowsCachedDisplay ? "예" : "아니오")}");
        builder.AppendLine();

        builder.AppendLine("[저장 대기 / Dirty 요약]");
        builder.AppendLine($"현재 계정 기준 대기 건수: {pendingSummary.PendingTotalCount:N0}");
        AppendSummaryLine(builder, "환경설정", pendingSummary.PendingSettingCount);
        AppendSummaryLine(builder, "거래처 기준", pendingSummary.PendingCustomerMasterCount);
        AppendSummaryLine(builder, "거래처", pendingSummary.PendingCustomerCount);
        AppendSummaryLine(builder, "계약", pendingSummary.PendingCustomerContractCount);
        AppendSummaryLine(builder, "품목", pendingSummary.PendingItemCount);
        AppendSummaryLine(builder, "재고", pendingSummary.PendingItemWarehouseStockCount);
        AppendSummaryLine(builder, "전표", pendingSummary.PendingInvoiceCount);
        AppendSummaryLine(builder, "수금/지급", pendingSummary.PendingPaymentCount);
        AppendSummaryLine(builder, "수금첨부", pendingSummary.PendingPaymentAttachmentCount);
        AppendSummaryLine(builder, "거래", pendingSummary.PendingTransactionCount);
        AppendSummaryLine(builder, "거래첨부", pendingSummary.PendingTransactionAttachmentCount);
        AppendSummaryLine(builder, "재고이동", pendingSummary.PendingInventoryTransferCount);
        AppendSummaryLine(builder, "렌탈", pendingSummary.PendingRentalCount);
        builder.AppendLine();

        builder.AppendLine("[마지막 동기화 수신 요약]");
        AppendSummaryLine(builder, "거래처", state.LastPulledCustomerCount);
        AppendSummaryLine(builder, "품목", state.LastPulledItemCount);
        AppendSummaryLine(builder, "재고", state.LastPulledItemWarehouseStockCount);
        AppendSummaryLine(builder, "전표", state.LastPulledInvoiceCount);
        AppendSummaryLine(builder, "수금/지급", state.LastPulledPaymentCount);
        AppendSummaryLine(builder, "거래", state.LastPulledTransactionCount);
        AppendSummaryLine(builder, "거래첨부", state.LastPulledTransactionAttachmentCount);
        AppendSummaryLine(builder, "재고이동", state.LastPulledInventoryTransferCount);
        AppendSummaryLine(
            builder,
            "렌탈",
            state.LastPulledRentalManagementCompanyCount +
            state.LastPulledRentalBillingProfileCount +
            state.LastPulledRentalAssetCount +
            state.LastPulledRentalAssetAssignmentHistoryCount +
            state.LastPulledRentalBillingLogCount);
        builder.AppendLine();

        builder.AppendLine("[최근 경고/오류 발생 메타데이터]");
        if (recentLogMetadata.Count == 0)
        {
            builder.AppendLine("- 최근 경고/오류 기록이 없습니다.");
        }
        else
        {
            foreach (var metadata in recentLogMetadata)
                builder.AppendLine($"- {metadata}");
        }

        builder.AppendLine();
        builder.AppendLine("※ 보안 정책상 토큰, 비밀번호, 개인 첨부 원문·파일명, 로그 메시지 원문은 포함하지 않습니다.");
        return builder.ToString();
    }

    private static async Task ShareFileAsync(string exportPath)
    {
        var request = new ShareFileRequest(
            "거래플랜 진단 정보",
            new ReadOnlyFile(exportPath, "text/plain"));

        if (MainThread.IsMainThread)
        {
            await Share.Default.RequestAsync(request);
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(async () => await Share.Default.RequestAsync(request));
    }

    private static void AppendSummaryLine(StringBuilder builder, string label, int count)
        => builder.AppendLine($"- {label}: {count:N0}건");

    private static string BuildSyncStatus(MobileSyncState state)
    {
        if (!string.IsNullOrWhiteSpace(state.LastError))
            return "오류 또는 주의 필요";

        if (state.LastSuccessUtc.HasValue)
            return "정상";

        if (state.LastAttemptUtc.HasValue)
            return "시도 기록 있음";

        return "동기화 이력 없음";
    }

    private static string FormatLocalDateTime(DateTime? utc)
        => utc.HasValue
            ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "-";

    private async Task<string> SaveDiagnosticFileAsync(string content, CancellationToken ct)
    {
        var directory = Path.Combine(FileSystem.Current.CacheDirectory, "diagnostics");
        Directory.CreateDirectory(directory);

        foreach (var previousFile in Directory.EnumerateFiles(
                     directory,
                     "georaeplan-mobile-diagnostics-*.txt",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(previousFile);
            }
            catch
            {
                // 캐시 정리 실패는 새 진단 파일 생성을 막지 않습니다.
            }
        }

        var fileName = $"georaeplan-mobile-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var exportPath = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(exportPath, content, Utf8WithoutBom, ct);
        return exportPath;
    }

    private async Task<IReadOnlyList<string>> ReadRecentErrorLogMetadataAsync(CancellationToken ct)
    {
        var logDirectory = Path.Combine(FileSystem.Current.AppDataDirectory, "logs");
        if (!Directory.Exists(logDirectory))
            return [];

        var candidates = Directory.EnumerateFiles(logDirectory, "*.log", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(3)
            .ToList();

        if (candidates.Count == 0)
            return [];

        var collected = new List<string>();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file.FullName, ct);
            }
            catch
            {
                continue;
            }

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index].Trim();
                var match = TimestampedLogLinePattern.Match(line);
                if (!match.Success)
                    continue;

                collected.Add($"{match.Groups["timestamp"].Value} [{match.Groups["level"].Value}] [{match.Groups["category"].Value.ToUpperInvariant()}]");
                if (collected.Count >= MaxRecentLogLines)
                    return collected;
            }
        }

        return collected;
    }

    private static string ValueOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
