using System.IO;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public sealed record VersionChangeMaintenanceResult(
    bool Ran,
    bool BackupCreated,
    int ClearedSettingCount,
    int DeletedTempFileCount,
    string Message);

public static class VersionChangeMaintenanceService
{
    private const string LastProcessedVersionSettingKey = "System.LastPostUpdateMaintenanceVersion";
    private static readonly string[] TransientSettingPrefixes =
    [
        "Rental.BillingEditorDraft",
        "Rental.OnboardingDraft"
    ];

    public static async Task<VersionChangeMaintenanceResult> RunAsync(
        LocalStateService local,
        BackupService backup,
        string currentVersion,
        CancellationToken ct = default)
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(currentVersion)
            ? "0.0.0"
            : currentVersion.Trim();

        var lastProcessedVersion = await local.GetSettingAsync(LastProcessedVersionSettingKey, ct);
        if (string.Equals(lastProcessedVersion, normalizedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return new VersionChangeMaintenanceResult(
                Ran: false,
                BackupCreated: false,
                ClearedSettingCount: 0,
                DeletedTempFileCount: 0,
                Message: $"버전 변경 후 정비는 이미 완료되었습니다. ({normalizedVersion})");
        }

        var backupCreated = await backup.BackupNowAsync(ct);
        var clearedSettingCount = 0;
        foreach (var prefix in TransientSettingPrefixes)
            clearedSettingCount += await local.DeleteSettingsByPrefixAsync(prefix, ct);

        await local.ClearInvalidOfficeSyncCredentialsAsync();
        var normalizedSharedOptionIdCount = await local.NormalizeSharedOptionIdCasingAsync(ct);
        await local.MarkServerMirrorRefreshRequiredAsync(ct);

        var deletedTempFileCount = 0;
        deletedTempFileCount += CleanupFilesOlderThan(AppPaths.CustomerContractPreviewDir, TimeSpan.FromDays(7));
        deletedTempFileCount += CleanupFilesOlderThan(AppPaths.DiagnosticsDir, TimeSpan.FromDays(30));
        deletedTempFileCount += CleanupFilesOlderThan(AppPaths.LogDir, TimeSpan.FromDays(30));
        DesktopAppUpdateService.TryCleanupStaleUpdateArtifacts();

        await local.SetSettingAsync(LastProcessedVersionSettingKey, normalizedVersion, ct);

        var message = $"버전 {normalizedVersion} 기준 1회 정비를 완료했습니다."
            + (backupCreated ? " 시작 전 DB 백업도 생성했습니다." : " 시작 전 DB 백업은 생성하지 못했습니다.")
            + (clearedSettingCount > 0 ? $" 임시 draft 설정 {clearedSettingCount:N0}건을 정리했습니다." : string.Empty)
            + (normalizedSharedOptionIdCount > 0 ? $" 공유 선택옵션 ID 표기 {normalizedSharedOptionIdCount:N0}건을 정리했습니다." : string.Empty)
            + " 다음 동기화에서 중앙 서버 기준 전체 캐시를 1회 다시 받아 범위 불일치 데이터를 정리합니다."
            + (deletedTempFileCount > 0 ? $" 오래된 임시 파일 {deletedTempFileCount:N0}건을 정리했습니다." : string.Empty);

        return new VersionChangeMaintenanceResult(
            Ran: true,
            BackupCreated: backupCreated,
            ClearedSettingCount: clearedSettingCount,
            DeletedTempFileCount: deletedTempFileCount,
            Message: message);
    }

    private static int CleanupFilesOlderThan(string rootPath, TimeSpan retention)
    {
        if (!Directory.Exists(rootPath))
            return 0;

        var cutoffUtc = DateTime.UtcNow - retention;
        var deletedCount = 0;

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= cutoffUtc)
                    continue;

                info.Delete();
                deletedCount++;
            }
            catch
            {
                // 일부 임시 파일 정리 실패는 정비 전체를 막지 않음
            }
        }

        return deletedCount;
    }
}
