using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Infrastructure;

[assembly: InternalsVisibleTo("GeoraePlan.Desktop.App.Tests")]

namespace 거래플랜.Desktop.App.Services;

public sealed record BackupSnapshotInfo(
    string FilePath,
    string FileName,
    DateTime LastWriteTime,
    long SizeBytes,
    string DisplayName,
    string SizeText);

public sealed record BackupRestoreStartupResult(string? Message, bool StartupBlocked);

/// <summary>
/// 로컬 SQLite DB와 거래 첨부파일의 같은 세대 백업/복원 예약을 관리합니다.
/// 실제 복원은 앱 시작 시 DbContext가 열리기 전에 적용합니다.
/// </summary>
public sealed class BackupService
{
    private const string PendingRestoreMarkerFileName = "pending-db-restore.txt";
    private const string BackupPackageExtension = ".gpbackup";
    private const string BackupManifestEntryName = "manifest.json";
    private const string BackupDatabaseEntryName = "database.db";
    private const string BackupAttachmentsEntryPrefix = "attachments/";
    private const int BackupManifestSchemaVersion = 2;
    private const int BackupGenerationAttemptCount = 3;
    private const int MaxBackupArchiveEntryCount = 25_000;
    private const long MaxBackupManifestBytes = 4L * 1024 * 1024;
    private const long MaxBackupDatabaseBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxBackupAttachmentBytes = 64L * 1024 * 1024;
    private const long MaxBackupArchiveTotalBytes = 4L * 1024 * 1024 * 1024;
    private const double MaxBackupEntryCompressionRatio = 500d;
    private const int MaxBackupArchivePathLength = 512;
    private const int TradePlanApplicationId = 0x47504C4E; // "GPLN"
    private const int DailyManagedBackupRetentionDays = 30;
    private const string BackupStagingToken = ".backup-staging-";
    private const string RestoreStagingToken = ".restore-staging-";
    private const string RestoreRollbackToken = ".restore-rollback-";
    private const string RestoreFailedToken = ".restore-failed-";
    private const string RawRestoreRecoveryDirectoryPrefix = "복원격리-";
    private const string MarkerProcessingSuffix = ".processing";
    private const string MarkerStatePending = "Pending";
    private const string MarkerStateApplying = "Applying";
    private const string MarkerStateCompleted = "Completed";
    private const string RestorePhasePrepared = "Prepared";
    private const string RestorePhaseSwitchingDatabase = "SwitchingDatabase";
    private const string RestorePhaseDatabaseSwitched = "DatabaseSwitched";
    private const string RestorePhaseSwitchingAttachments = "SwitchingAttachments";
    private const string RestorePhaseAttachmentsOriginalMoved = "AttachmentsOriginalMoved";
    private const string RestorePhaseAttachmentsSwitched = "AttachmentsSwitched";
    private const string RestorePhaseValidated = "Validated";
    private const string RestorePhaseRolledBack = "RolledBack";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredTradePlanSchema =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Settings"] = ["Key", "Value"],
            ["Customers"] = ["Id"],
            ["Items"] = ["Id"],
            ["Invoices"] = ["Id"],
            ["Payments"] = ["Id"],
            ["Transactions"] = ["Id"],
            ["TransactionAttachments"] =
                ["Id", "TransactionId", "StoredPath", "FileSize", "FileHash", "IsDeleted"]
        };

    public async Task<bool> BackupNowAsync(CancellationToken ct = default)
        => await BackupNowWithPathAsync(ct) is not null;

    public async Task<string?> BackupNowWithPathAsync(CancellationToken ct = default)
    {
        try
        {
            return await RunBackupWorkOffUiThreadAsync(
                async () =>
                {
                    if (!File.Exists(AppPaths.LocalDbFile))
                        return null;

                    Directory.CreateDirectory(AppPaths.BackupDir);
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                    var destinationPath = Path.Combine(
                        AppPaths.BackupDir,
                        $"거래플랜_{stamp}{BackupPackageExtension}");

                    await CreateConsistentBackupPackageAsync(
                            AppPaths.LocalDbFile,
                            AppPaths.TransactionAttachmentsDir,
                            destinationPath,
                            ct)
                        .ConfigureAwait(false);

                    TrimManagedBackups();
                    return destinationPath;
                },
                ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    internal static Task<T> RunBackupWorkOffUiThreadAsync<T>(
        Func<Task<T>> work,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Task.Run(work, ct);
    }

    public Task<IReadOnlyList<BackupSnapshotInfo>> GetBackupSnapshotsAsync(
        CancellationToken ct = default)
        => RunBackupWorkOffUiThreadAsync(
            () => Task.FromResult(GetBackupSnapshots()),
            ct);

    public IReadOnlyList<BackupSnapshotInfo> GetBackupSnapshots()
    {
        Directory.CreateDirectory(AppPaths.BackupDir);
        TrimManagedBackups();

        return GetVerifiedPublishedBackupFiles(AppPaths.BackupDir)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new BackupSnapshotInfo(
                file.FullName,
                file.Name,
                file.LastWriteTime,
                file.Length,
                BuildDisplayName(file),
                FormatBytes(file.Length)))
            .ToList();
    }

    public bool ScheduleRestoreOnNextStartup(string backupPath, out string message)
    {
        message = string.Empty;
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            message = "복원할 백업 파일을 선택하세요.";
            return false;
        }

        var validatedPath = ValidateBackupPath(backupPath);
        if (validatedPath is null)
        {
            message = "선택한 백업 파일이 백업 폴더에 없거나 접근할 수 없습니다.";
            return false;
        }

        if (!IsPublishedBackupCandidate(validatedPath) ||
            !IsVerifiedBackupArtifact(validatedPath))
        {
            message = "선택한 파일이 거래플랜 백업인지 또는 백업 세대의 무결성이 올바른지 확인하지 못했습니다.";
            return false;
        }

        WritePendingRestoreMarker(validatedPath);
        var legacyNotice = IsLegacyDatabaseBackup(validatedPath)
            ? $"{Environment.NewLine}이 백업은 첨부파일이 없는 기존 .db 호환 백업으로 제한 복원됩니다."
            : string.Empty;
        message =
            $"선택한 백업을 다음 실행 시 복원하도록 예약했습니다.{legacyNotice}{Environment.NewLine}" +
            "앱을 완전히 종료한 뒤 다시 실행하세요.";
        return true;
    }

    public void OpenBackupFolder()
    {
        Directory.CreateDirectory(AppPaths.BackupDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(AppPaths.BackupDir),
            UseShellExecute = true
        });
    }

    public static string? TryApplyPendingRestoreOnStartup()
        => ApplyPendingRestoreOnStartup().Message;

    public static BackupRestoreStartupResult ApplyPendingRestoreOnStartup()
        => ApplyPendingRestoreOnStartup(
            GetPendingRestoreMarkerPath(),
            AppPaths.BackupDir,
            AppPaths.LocalDbFile,
            AppPaths.TransactionAttachmentsDir);

    internal static string? TryApplyPendingRestoreOnStartup(
        string markerPath,
        string backupDirectory,
        string currentDatabasePath)
        => ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabasePath,
            Path.Combine(
                Path.GetDirectoryName(currentDatabasePath)
                    ?? throw new InvalidOperationException("현재 데이터 폴더를 확인할 수 없습니다."),
                "transaction-attachments")).Message;

    internal static BackupRestoreStartupResult ApplyPendingRestoreOnStartup(
        string markerPath,
        string backupDirectory,
        string currentDatabasePath,
        string currentAttachmentsDirectory)
    {
        var processingMarkerPath = markerPath + MarkerProcessingSuffix;
        try
        {
            if (File.Exists(processingMarkerPath))
            {
                var processingMarker = ReadRestoreMarker(processingMarkerPath);
                if (processingMarker is null)
                {
                    return Blocked(
                        "복원 처리 상태 파일을 판독할 수 없어 안전을 위해 시작을 중단합니다. " +
                        "데이터를 변경하지 말고 복원 상태를 확인하세요.");
                }

                if (!string.Equals(
                        processingMarker.State,
                        MarkerStateCompleted,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return TryRecoverInterruptedRestore(
                        processingMarkerPath,
                        processingMarker,
                        currentDatabasePath,
                        currentAttachmentsDirectory);
                }

                var recoveryCleanupFailures = CleanupRestoreRecoveryArtifacts(
                    processingMarker,
                    currentDatabasePath,
                    currentAttachmentsDirectory);
                if (recoveryCleanupFailures.Count > 0)
                {
                    return new BackupRestoreStartupResult(
                        "이전에 완료된 백업 복원을 확인했습니다. 남은 복구 자료를 정리하지 못해 " +
                        "완료 상태 파일을 보존했으며 다음 시작 때 다시 정리합니다: " +
                        string.Join("; ", recoveryCleanupFailures),
                        StartupBlocked: false);
                }

                var completedMarkerCleanupError = TryDeleteFile(processingMarkerPath);
                if (File.Exists(markerPath))
                {
                    return Blocked(
                        "완료된 복원 상태 파일과 새 복원 예약이 함께 남아 있어 시작을 중단합니다. " +
                        "복원 예약 상태를 확인하세요.");
                }

                return new BackupRestoreStartupResult(
                    completedMarkerCleanupError is null
                        ? "이전에 완료된 백업 복원을 확인했습니다. 같은 백업은 다시 적용하지 않았습니다."
                        : "이전에 완료된 백업 복원을 확인했습니다. 상태 파일 정리는 실패했지만 같은 백업은 다시 적용하지 않았습니다.",
                    StartupBlocked: false);
            }

            if (!File.Exists(markerPath))
                return new BackupRestoreStartupResult(null, StartupBlocked: false);

            var pendingMarker = ReadRestoreMarker(markerPath);
            if (pendingMarker is null)
            {
                return new BackupRestoreStartupResult(
                    "예약된 백업 복원 정보를 확인하지 못했습니다. 현재 데이터와 예약 정보는 변경하지 않았습니다.",
                    StartupBlocked: false);
            }

            if (!string.Equals(
                    pendingMarker.State,
                    MarkerStatePending,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(
                        pendingMarker.State,
                        MarkerStateApplying,
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(markerPath, processingMarkerPath, overwrite: false);
                    return TryRecoverInterruptedRestore(
                        processingMarkerPath,
                        pendingMarker,
                        currentDatabasePath,
                        currentAttachmentsDirectory);
                }

                return Blocked(
                    "복원 예약 파일이 이미 적용 단계로 전환되었으나 완료 여부를 확인할 수 없어 " +
                    "같은 백업의 재적용을 막기 위해 시작을 중단합니다.");
            }

            var validatedBackupPath = ValidateBackupPath(pendingMarker.BackupPath, backupDirectory);
            if (validatedBackupPath is null || !File.Exists(validatedBackupPath))
            {
                return new BackupRestoreStartupResult(
                    "예약된 백업 파일을 찾지 못했습니다. 현재 데이터와 예약 정보는 변경하지 않았습니다.",
                    StartupBlocked: false);
            }

            if (!IsPublishedBackupCandidate(validatedBackupPath) ||
                !IsVerifiedBackupArtifact(validatedBackupPath))
            {
                return new BackupRestoreStartupResult(
                    "예약된 파일이 거래플랜 백업인지 또는 백업 세대의 무결성이 올바른지 확인하지 못했습니다. " +
                    "현재 데이터와 복원 예약은 변경하지 않았습니다.",
                    StartupBlocked: false);
            }

            var applyingMarker = pendingMarker with
            {
                State = MarkerStateApplying,
                OperationId = Guid.NewGuid().ToString("N"),
                Phase = RestorePhasePrepared,
                HadCurrentDatabase = File.Exists(currentDatabasePath),
                HadCurrentAttachments = Directory.Exists(currentAttachmentsDirectory)
            };
            WriteRestoreMarkerAtomically(markerPath, applyingMarker);
            File.Move(markerPath, processingMarkerPath, overwrite: false);

            try
            {
                void PersistRestorePhase(string phase)
                {
                    applyingMarker = applyingMarker with
                    {
                        State = string.Equals(
                            phase,
                            MarkerStateCompleted,
                            StringComparison.OrdinalIgnoreCase)
                                ? MarkerStateCompleted
                                : MarkerStateApplying,
                        Phase = phase
                    };
                    WriteRestoreMarkerAtomically(processingMarkerPath, applyingMarker);
                }

                RestoreBackupArtifact(
                    validatedBackupPath,
                    currentDatabasePath,
                    currentAttachmentsDirectory,
                    backupDirectory,
                    restoreOperationId: applyingMarker.OperationId,
                    onRestorePhaseChanged: PersistRestorePhase);
            }
            catch (Exception ex)
            {
                return Blocked(
                    "백업 복원 중 오류가 발생했습니다. 같은 백업의 자동 재적용을 막기 위해 시작을 중단합니다: " +
                    ex.Message);
            }

            var completedMarker = applyingMarker with
            {
                State = MarkerStateCompleted,
                Phase = MarkerStateCompleted
            };
            try
            {
                WriteRestoreMarkerAtomically(
                    processingMarkerPath,
                    completedMarker);
            }
            catch (Exception ex)
            {
                return Blocked(
                    "백업 복원은 적용되었지만 완료 상태를 기록하지 못했습니다. " +
                    "같은 백업의 재적용을 막기 위해 시작을 중단합니다: " + ex.Message);
            }

            var completedRecoveryCleanupFailures = CleanupRestoreRecoveryArtifacts(
                completedMarker,
                currentDatabasePath,
                currentAttachmentsDirectory);
            if (completedRecoveryCleanupFailures.Count > 0)
            {
                return new BackupRestoreStartupResult(
                    "백업 복원은 적용되었지만 남은 복구 자료를 정리하지 못했습니다. " +
                    "완료 상태 파일을 보존했으며 다음 시작 때 다시 정리합니다: " +
                    string.Join("; ", completedRecoveryCleanupFailures),
                    StartupBlocked: false);
            }

            var markerCleanupError = TryDeleteFile(processingMarkerPath);
            try
            {
                TrimManagedBackups(backupDirectory, markerPath);
            }
            catch
            {
                // 복원 세대 설치가 끝난 뒤의 보존 정리 실패는 설치본을 바꾸지 않습니다.
            }

            return new BackupRestoreStartupResult(
                markerCleanupError is null
                    ? $"백업 복원이 적용되었습니다: {Path.GetFileName(validatedBackupPath)}"
                    : "백업 복원은 적용되었고 완료 상태도 기록했습니다. 상태 파일 정리는 실패했지만 같은 백업은 다시 적용되지 않습니다.",
                StartupBlocked: false);
        }
        catch (Exception ex)
        {
            return Blocked(
                "백업 복원 상태 처리 중 오류가 발생해 안전을 위해 시작을 중단합니다: " + ex.Message);
        }
    }

    private static BackupRestoreStartupResult TryRecoverInterruptedRestore(
        string processingMarkerPath,
        RestoreMarker marker,
        string currentDatabasePath,
        string currentAttachmentsDirectory)
    {
        if (string.IsNullOrWhiteSpace(marker.OperationId) ||
            marker.HadCurrentDatabase is null ||
            marker.HadCurrentAttachments is null)
        {
            return Blocked(
                "이전 복원 상태에는 자동 롤백에 필요한 작업 식별 정보가 없습니다. " +
                $"상태 파일을 보존했습니다: {processingMarkerPath}");
        }

        RestoreRecoveryPaths recoveryPaths;
        try
        {
            recoveryPaths = BuildRestoreRecoveryPaths(
                currentDatabasePath,
                currentAttachmentsDirectory,
                marker.OperationId);
        }
        catch (Exception ex)
        {
            return Blocked(
                "이전 복원 상태의 작업 식별 정보가 올바르지 않아 자동 롤백할 수 없습니다. " +
                $"상태 파일을 보존했습니다: {processingMarkerPath}. {ex.Message}");
        }

        try
        {
            RecoverAttachmentsAfterInterruptedRestore(
                marker,
                currentAttachmentsDirectory,
                recoveryPaths);
            RecoverDatabaseAfterInterruptedRestore(
                marker,
                currentDatabasePath,
                recoveryPaths);
            ValidateRecoveredOriginalGenerationOrThrow(
                marker,
                currentDatabasePath,
                currentAttachmentsDirectory);

            var completedMarker = marker with
            {
                State = MarkerStateCompleted,
                Phase = RestorePhaseRolledBack
            };
            WriteRestoreMarkerAtomically(processingMarkerPath, completedMarker);
            var recoveryCleanupFailures = CleanupRestoreRecoveryArtifacts(
                completedMarker,
                currentDatabasePath,
                currentAttachmentsDirectory);
            if (recoveryCleanupFailures.Count > 0)
            {
                return new BackupRestoreStartupResult(
                    "중단된 백업 복원을 이전 세대로 자동 롤백했지만 남은 복구 자료를 정리하지 못했습니다. " +
                    "완료 상태 파일을 보존했으며 다음 시작 때 다시 정리합니다: " +
                    string.Join("; ", recoveryCleanupFailures),
                    StartupBlocked: false);
            }

            var markerCleanupError = TryDeleteFile(processingMarkerPath);

            return new BackupRestoreStartupResult(
                markerCleanupError is null
                    ? "중단된 백업 복원을 감지해 이전 DB와 첨부파일 세대로 자동 롤백했습니다."
                    : "중단된 백업 복원을 이전 세대로 자동 롤백했습니다. 완료 상태 파일은 남아 있지만 같은 백업은 다시 적용되지 않습니다.",
                StartupBlocked: false);
        }
        catch (Exception ex)
        {
            return Blocked(
                "중단된 백업 복원을 안전하게 롤백하지 못해 시작을 중단합니다. " +
                "현재 파일과 다음 복구 자료를 보존했습니다: " +
                BuildRecoveryArtifactSummary(processingMarkerPath, recoveryPaths) +
                ". " + ex.Message);
        }
    }

    private static void RecoverAttachmentsAfterInterruptedRestore(
        RestoreMarker marker,
        string currentAttachmentsDirectory,
        RestoreRecoveryPaths recoveryPaths)
    {
        var hadCurrentAttachments = marker.HadCurrentAttachments == true;
        if (Directory.Exists(recoveryPaths.AttachmentsRollbackDirectory))
        {
            if (Directory.Exists(currentAttachmentsDirectory))
            {
                if (Directory.Exists(recoveryPaths.AttachmentsFailedDirectory))
                {
                    throw new IOException(
                        "현재 첨부파일과 이미 보존된 실패 첨부파일이 함께 있어 자동으로 덮어쓸 수 없습니다.");
                }

                Directory.Move(
                    currentAttachmentsDirectory,
                    recoveryPaths.AttachmentsFailedDirectory);
            }

            Directory.Move(
                recoveryPaths.AttachmentsRollbackDirectory,
                currentAttachmentsDirectory);
            return;
        }

        if (!hadCurrentAttachments)
        {
            if (PhaseIsAtOrAfter(marker.Phase, RestorePhaseSwitchingAttachments) &&
                Directory.Exists(currentAttachmentsDirectory))
            {
                if (Directory.Exists(recoveryPaths.AttachmentsFailedDirectory))
                {
                    throw new IOException(
                        "실패 첨부파일 보존 폴더가 이미 있어 새 첨부파일 세대를 보존할 수 없습니다.");
                }

                Directory.Move(
                    currentAttachmentsDirectory,
                    recoveryPaths.AttachmentsFailedDirectory);
            }

            return;
        }

        if (PhaseIsAtOrAfter(marker.Phase, RestorePhaseAttachmentsOriginalMoved) &&
            !Directory.Exists(recoveryPaths.AttachmentsFailedDirectory) &&
            !string.Equals(
                marker.Phase,
                RestorePhaseRolledBack,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "원래 첨부파일 롤백 폴더를 찾지 못했습니다.",
                recoveryPaths.AttachmentsRollbackDirectory);
        }
    }

    private static void RecoverDatabaseAfterInterruptedRestore(
        RestoreMarker marker,
        string currentDatabasePath,
        RestoreRecoveryPaths recoveryPaths)
    {
        var hadCurrentDatabase = marker.HadCurrentDatabase == true;
        if (File.Exists(recoveryPaths.DatabaseRollbackPath))
        {
            DeleteSqliteSidecarFiles(currentDatabasePath);
            if (File.Exists(currentDatabasePath))
            {
                if (File.Exists(recoveryPaths.DatabaseFailedPath))
                {
                    throw new IOException(
                        "현재 DB와 이미 보존된 실패 DB가 함께 있어 자동으로 덮어쓸 수 없습니다.");
                }

                File.Replace(
                    recoveryPaths.DatabaseRollbackPath,
                    currentDatabasePath,
                    recoveryPaths.DatabaseFailedPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(recoveryPaths.DatabaseRollbackPath, currentDatabasePath);
            }

            DeleteSqliteSidecarFiles(currentDatabasePath);
            return;
        }

        if (!hadCurrentDatabase)
        {
            if (PhaseIsAtOrAfter(marker.Phase, RestorePhaseSwitchingDatabase) &&
                File.Exists(currentDatabasePath))
            {
                if (File.Exists(recoveryPaths.DatabaseFailedPath))
                {
                    throw new IOException(
                        "실패 DB 보존 파일이 이미 있어 새 DB 세대를 보존할 수 없습니다.");
                }

                DeleteSqliteSidecarFiles(currentDatabasePath);
                File.Move(currentDatabasePath, recoveryPaths.DatabaseFailedPath);
            }

            return;
        }

        if (PhaseIsAtOrAfter(marker.Phase, RestorePhaseDatabaseSwitched) &&
            !File.Exists(recoveryPaths.DatabaseFailedPath) &&
            !string.Equals(
                marker.Phase,
                RestorePhaseRolledBack,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "원래 DB 롤백 파일을 찾지 못했습니다.",
                recoveryPaths.DatabaseRollbackPath);
        }
    }

    private static void ValidateRecoveredOriginalGenerationOrThrow(
        RestoreMarker marker,
        string currentDatabasePath,
        string currentAttachmentsDirectory)
    {
        if (marker.HadCurrentDatabase == true)
        {
            ValidateTradePlanDatabaseOrThrow(
                currentDatabasePath,
                requireApplicationId: false);
        }
        else if (File.Exists(currentDatabasePath))
        {
            throw new InvalidDataException(
                "복원 전에는 DB가 없었지만 중단 복구 후 현재 DB가 남아 있습니다.");
        }

        if (marker.HadCurrentAttachments == true &&
            !Directory.Exists(currentAttachmentsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"원래 첨부파일 폴더를 복구하지 못했습니다: {currentAttachmentsDirectory}");
        }

        if (marker.HadCurrentAttachments == false &&
            Directory.Exists(currentAttachmentsDirectory))
        {
            throw new InvalidDataException(
                "복원 전에는 첨부파일 폴더가 없었지만 중단 복구 후 현재 폴더가 남아 있습니다.");
        }
    }

    private static bool PhaseIsAtOrAfter(string? actualPhase, string expectedPhase)
    {
        var phases = new[]
        {
            RestorePhasePrepared,
            RestorePhaseSwitchingDatabase,
            RestorePhaseDatabaseSwitched,
            RestorePhaseSwitchingAttachments,
            RestorePhaseAttachmentsOriginalMoved,
            RestorePhaseAttachmentsSwitched,
            RestorePhaseValidated,
            RestorePhaseRolledBack,
            MarkerStateCompleted
        };
        var actualIndex = Array.FindIndex(
            phases,
            phase => string.Equals(
                phase,
                actualPhase,
                StringComparison.OrdinalIgnoreCase));
        var expectedIndex = Array.FindIndex(
            phases,
            phase => string.Equals(
                phase,
                expectedPhase,
                StringComparison.OrdinalIgnoreCase));
        return actualIndex >= 0 && expectedIndex >= 0 && actualIndex >= expectedIndex;
    }

    private static string BuildRecoveryArtifactSummary(
        string processingMarkerPath,
        RestoreRecoveryPaths recoveryPaths)
        => string.Join(
            "; ",
            new[]
            {
                processingMarkerPath,
                recoveryPaths.DatabaseRollbackPath,
                recoveryPaths.AttachmentsRollbackDirectory,
                recoveryPaths.DatabaseFailedPath,
                recoveryPaths.AttachmentsFailedDirectory
            });

    private static BackupRestoreStartupResult Blocked(string message)
        => new(message, StartupBlocked: true);

    private static string GetPendingRestoreMarkerPath()
        => Path.Combine(AppPaths.TempDir, PendingRestoreMarkerFileName);

    private static string? ValidateBackupPath(string backupPath)
        => ValidateBackupPath(backupPath, AppPaths.BackupDir);

    private static string? ValidateBackupPath(string backupPath, string backupDirectory)
    {
        try
        {
            var fullPath = Path.GetFullPath(backupPath);
            var backupRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
            var candidateDirectory = Path.GetDirectoryName(fullPath);

            return string.Equals(
                    Path.TrimEndingDirectorySeparator(candidateDirectory ?? string.Empty),
                    backupRoot,
                    StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDisplayName(FileInfo file)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
        if (nameWithoutExtension.StartsWith("거래플랜_before_restore_", StringComparison.OrdinalIgnoreCase))
            return "복원 전 자동 백업 " + nameWithoutExtension["거래플랜_before_restore_".Length..].Replace('_', ' ');

        if (nameWithoutExtension.StartsWith("거래플랜_", StringComparison.OrdinalIgnoreCase))
            return nameWithoutExtension["거래플랜_".Length..].Replace('_', ' ');

        return nameWithoutExtension.Replace('_', ' ');
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static string QuoteArgument(string value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    internal static async Task CreateConsistentBackupPackageAsync(
        string sourceDatabasePath,
        string sourceAttachmentsDirectory,
        string destinationPackagePath,
        CancellationToken ct,
        Func<int, Task>? afterDatabaseSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAttachmentsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPackagePath);
        if (!string.Equals(
                Path.GetExtension(destinationPackagePath),
                BackupPackageExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"백업 패키지 확장자는 {BackupPackageExtension}이어야 합니다.",
                nameof(destinationPackagePath));
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPackagePath)
            ?? throw new InvalidOperationException("백업 대상 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationPackagePath))
            throw new IOException($"이미 존재하는 백업 경로에는 새 백업을 게시할 수 없습니다: {destinationPackagePath}");

        BackupGenerationMismatchException? lastGenerationMismatch = null;
        for (var attempt = 1; attempt <= BackupGenerationAttemptCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var stagingDirectory = BuildSiblingTransientPath(
                destinationPackagePath,
                BackupStagingToken + "dir-");
            var stagingPackagePath = BuildSiblingTransientPath(
                destinationPackagePath,
                BackupStagingToken);
            try
            {
                Directory.CreateDirectory(stagingDirectory);
                var stagedDatabasePath = Path.Combine(stagingDirectory, BackupDatabaseEntryName);
                var stagedAttachmentsDirectory = Path.Combine(stagingDirectory, "attachments");

                await CreateConsistentSqliteBackupAsync(
                    sourceDatabasePath,
                    stagedDatabasePath,
                    ct);
                SetTradePlanApplicationId(stagedDatabasePath);
                ValidateTradePlanDatabaseOrThrow(
                    stagedDatabasePath,
                    requireApplicationId: true);

                var attachmentReferences = ReadAttachmentReferences(
                    stagedDatabasePath,
                    sourceAttachmentsDirectory);
                if (afterDatabaseSnapshot is not null)
                    await afterDatabaseSnapshot(attempt);

                await CopyReferencedAttachmentGenerationAsync(
                    sourceAttachmentsDirectory,
                    stagedAttachmentsDirectory,
                    attachmentReferences,
                    ct);

                var manifest = await BuildManifestAsync(
                    stagedDatabasePath,
                    sourceAttachmentsDirectory,
                    stagedAttachmentsDirectory,
                    ct);
                ValidateDatabaseAttachmentGenerationOrThrow(
                    stagedDatabasePath,
                    stagedAttachmentsDirectory,
                    manifest.SourceAttachmentRoot,
                    manifest);
                await File.WriteAllTextAsync(
                    Path.Combine(stagingDirectory, BackupManifestEntryName),
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    ct);

                ZipFile.CreateFromDirectory(
                    stagingDirectory,
                    stagingPackagePath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false);
                ValidateBackupPackageOrThrow(stagingPackagePath);
                File.Move(stagingPackagePath, destinationPackagePath);
                return;
            }
            catch (BackupGenerationMismatchException ex)
            {
                lastGenerationMismatch = ex;
                if (attempt == BackupGenerationAttemptCount)
                    throw;
            }
            finally
            {
                TryDeleteFile(stagingPackagePath);
                TryDeleteDirectory(stagingDirectory);
            }
        }

        throw lastGenerationMismatch
              ?? new BackupGenerationMismatchException(
                  "DB와 첨부파일의 같은 백업 세대를 만들지 못했습니다.");
    }

    internal static async Task CreateConsistentSqliteBackupAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath가 비어 있습니다.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("destinationPath가 비어 있습니다.", nameof(destinationPath));

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationPath))
            throw new IOException($"이미 존재하는 백업 경로에는 새 백업을 게시할 수 없습니다: {destinationPath}");

        var stagingPath = BuildSiblingTransientPath(destinationPath, BackupStagingToken);
        try
        {
            await using (var connection = new SqliteConnection(BuildSqliteConnectionString(
                             sourcePath,
                             SqliteOpenMode.ReadWrite)))
            {
                await connection.OpenAsync(ct);
                await using var command = connection.CreateCommand();
                command.CommandText = $"VACUUM INTO {BuildSqliteStringLiteral(stagingPath)};";
                await command.ExecuteNonQueryAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            ValidateSqliteDatabaseOrThrow(stagingPath);
            File.Move(stagingPath, destinationPath);
        }
        finally
        {
            TryDeleteFile(stagingPath);
        }
    }

    private static void CreateConsistentBackupPackage(
        string sourceDatabasePath,
        string sourceAttachmentsDirectory,
        string destinationPackagePath)
        => CreateConsistentBackupPackageAsync(
                sourceDatabasePath,
                sourceAttachmentsDirectory,
                destinationPackagePath,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    internal static void RestoreDatabaseFromVerifiedBackup(
        string backupPath,
        string currentDatabasePath,
        string backupDirectory)
        => RestoreBackupArtifact(
            backupPath,
            currentDatabasePath,
            Path.Combine(
                Path.GetDirectoryName(currentDatabasePath)
                    ?? throw new InvalidOperationException("현재 데이터 폴더를 확인할 수 없습니다."),
                "transaction-attachments"),
            backupDirectory);

    internal static void RestoreBackupArtifact(
        string backupPath,
        string currentDatabasePath,
        string currentAttachmentsDirectory,
        string backupDirectory,
        Action? afterSwitchBeforeValidation = null,
        string? restoreOperationId = null,
        Action<string>? onRestorePhaseChanged = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentAttachmentsDirectory);

        var dataDirectory = Path.GetDirectoryName(currentDatabasePath)
            ?? throw new InvalidOperationException("현재 데이터베이스 폴더를 확인할 수 없습니다.");
        var attachmentsParentDirectory = Path.GetDirectoryName(currentAttachmentsDirectory)
            ?? throw new InvalidOperationException("첨부파일 폴더의 상위 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(attachmentsParentDirectory);
        Directory.CreateDirectory(backupDirectory);

        var recoveryPaths = string.IsNullOrWhiteSpace(restoreOperationId)
            ? null
            : BuildRestoreRecoveryPaths(
                currentDatabasePath,
                currentAttachmentsDirectory,
                restoreOperationId);
        var extractionDirectory = recoveryPaths?.ExtractionDirectory
                                  ?? BuildSiblingTransientPath(
                                      currentDatabasePath,
                                      RestoreStagingToken + "package-");
        var stagedDatabasePath = recoveryPaths?.StagedDatabasePath
                                 ?? BuildSiblingTransientPath(
                                     currentDatabasePath,
                                     RestoreStagingToken);
        var stagedAttachmentsDirectory = recoveryPaths?.StagedAttachmentsDirectory
                                         ?? BuildSiblingTransientPath(
                                             currentAttachmentsDirectory,
                                             RestoreStagingToken);
        var databaseRollbackPath = recoveryPaths?.DatabaseRollbackPath
                                   ?? BuildSiblingTransientPath(
                                       currentDatabasePath,
                                       RestoreRollbackToken);
        var databaseFailedPath = recoveryPaths?.DatabaseFailedPath
                                 ?? BuildSiblingTransientPath(
                                     currentDatabasePath,
                                     RestoreFailedToken);
        var attachmentsRollbackDirectory = recoveryPaths?.AttachmentsRollbackDirectory
                                           ?? BuildSiblingTransientPath(
                                               currentAttachmentsDirectory,
                                               RestoreRollbackToken);
        var attachmentsFailedDirectory = recoveryPaths?.AttachmentsFailedDirectory
                                         ?? BuildSiblingTransientPath(
                                             currentAttachmentsDirectory,
                                             RestoreFailedToken);

        var hadCurrentDatabase = File.Exists(currentDatabasePath);
        var hadCurrentAttachments = Directory.Exists(currentAttachmentsDirectory);
        var databaseSwitched = false;
        var attachmentsOriginalMoved = false;
        var attachmentsSwitched = false;
        var preserveRecoveryArtifacts = false;
        BackupPackageManifest? manifest = null;

        try
        {
            if (IsLegacyDatabaseBackup(backupPath))
            {
                ValidateLegacyDatabaseBackupOrThrow(backupPath);
                File.Copy(backupPath, stagedDatabasePath, overwrite: false);
                Directory.CreateDirectory(stagedAttachmentsDirectory);
            }
            else
            {
                manifest = ExtractAndValidateBackupPackage(
                    backupPath,
                    extractionDirectory);
                File.Copy(
                    Path.Combine(extractionDirectory, BackupDatabaseEntryName),
                    stagedDatabasePath,
                    overwrite: false);
                MoveDirectoryContents(
                    Path.Combine(extractionDirectory, "attachments"),
                    stagedAttachmentsDirectory);
                RewriteAttachmentStoredPaths(
                    stagedDatabasePath,
                    manifest.SourceAttachmentRoot,
                    currentAttachmentsDirectory);
            }

            ValidateTradePlanDatabaseOrThrow(
                stagedDatabasePath,
                requireApplicationId: manifest is not null,
                allowMissingTransactionAttachments: manifest is null);
            if (manifest is not null)
            {
                ValidateExtractedAttachmentGenerationOrThrow(
                    stagedAttachmentsDirectory,
                    manifest);
                ValidateDatabaseAttachmentGenerationOrThrow(
                    stagedDatabasePath,
                    stagedAttachmentsDirectory,
                    currentAttachmentsDirectory,
                    manifest);
            }

            if (!hadCurrentDatabase &&
                hadCurrentAttachments &&
                Directory.EnumerateFileSystemEntries(currentAttachmentsDirectory).Any())
            {
                PreserveRawCurrentAttachmentsForRecovery(
                    currentAttachmentsDirectory,
                    backupDirectory);
            }

            if (hadCurrentDatabase)
            {
                var currentBackupPath = Path.Combine(
                    backupDirectory,
                    $"거래플랜_before_restore_{DateTime.Now:yyyyMMdd_HHmmss_fff}{BackupPackageExtension}");
                try
                {
                    CreateConsistentBackupPackage(
                        currentDatabasePath,
                        currentAttachmentsDirectory,
                        currentBackupPath);
                    PreserveUnreferencedCurrentAttachmentsForRecovery(
                        currentDatabasePath,
                        currentAttachmentsDirectory,
                        backupDirectory);
                }
                catch (BackupGenerationMismatchException)
                {
                    PreserveRawCurrentGenerationForRecovery(
                        currentDatabasePath,
                        currentAttachmentsDirectory,
                        backupDirectory);
                }

                CheckpointSqliteDatabase(currentDatabasePath);
                DeleteSqliteSidecarFiles(currentDatabasePath);
                onRestorePhaseChanged?.Invoke(RestorePhaseSwitchingDatabase);
                File.Replace(
                    stagedDatabasePath,
                    currentDatabasePath,
                    databaseRollbackPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                onRestorePhaseChanged?.Invoke(RestorePhaseSwitchingDatabase);
                File.Move(stagedDatabasePath, currentDatabasePath);
            }

            databaseSwitched = true;
            onRestorePhaseChanged?.Invoke(RestorePhaseDatabaseSwitched);

            if (hadCurrentAttachments)
            {
                onRestorePhaseChanged?.Invoke(RestorePhaseSwitchingAttachments);
                Directory.Move(currentAttachmentsDirectory, attachmentsRollbackDirectory);
                attachmentsOriginalMoved = true;
                onRestorePhaseChanged?.Invoke(RestorePhaseAttachmentsOriginalMoved);
            }
            else
            {
                onRestorePhaseChanged?.Invoke(RestorePhaseSwitchingAttachments);
            }

            Directory.Move(stagedAttachmentsDirectory, currentAttachmentsDirectory);
            attachmentsSwitched = true;
            onRestorePhaseChanged?.Invoke(RestorePhaseAttachmentsSwitched);

            afterSwitchBeforeValidation?.Invoke();

            DeleteSqliteSidecarFiles(currentDatabasePath);
            ValidateTradePlanDatabaseOrThrow(
                currentDatabasePath,
                requireApplicationId: manifest is not null,
                allowMissingTransactionAttachments: manifest is null);
            if (manifest is not null)
            {
                ValidateExtractedAttachmentGenerationOrThrow(
                    currentAttachmentsDirectory,
                    manifest);
                ValidateDatabaseAttachmentGenerationOrThrow(
                    currentDatabasePath,
                    currentAttachmentsDirectory,
                    currentAttachmentsDirectory,
                    manifest);
            }
            else if (Directory.EnumerateFileSystemEntries(currentAttachmentsDirectory).Any())
            {
                throw new InvalidDataException(
                    "기존 .db 제한 복원은 빈 첨부파일 세대만 허용합니다.");
            }

            onRestorePhaseChanged?.Invoke(RestorePhaseValidated);
            onRestorePhaseChanged?.Invoke(MarkerStateCompleted);
            TryDeleteFile(databaseRollbackPath);
            TryDeleteDirectory(attachmentsRollbackDirectory);
        }
        catch (Exception restoreException)
        {
            try
            {
                if (attachmentsSwitched && Directory.Exists(currentAttachmentsDirectory))
                    Directory.Move(currentAttachmentsDirectory, attachmentsFailedDirectory);

                if (attachmentsOriginalMoved && Directory.Exists(attachmentsRollbackDirectory))
                    Directory.Move(attachmentsRollbackDirectory, currentAttachmentsDirectory);

                if (databaseSwitched)
                {
                    DeleteSqliteSidecarFiles(currentDatabasePath);
                    if (hadCurrentDatabase)
                    {
                        if (!File.Exists(databaseRollbackPath))
                            throw new FileNotFoundException("복원 롤백 DB를 찾지 못했습니다.", databaseRollbackPath);

                        if (File.Exists(currentDatabasePath))
                        {
                            File.Replace(
                                databaseRollbackPath,
                                currentDatabasePath,
                                databaseFailedPath,
                                ignoreMetadataErrors: true);
                        }
                        else
                        {
                            File.Move(databaseRollbackPath, currentDatabasePath);
                        }

                        ValidateTradePlanDatabaseOrThrow(
                            currentDatabasePath,
                            requireApplicationId: false);
                    }
                    else
                    {
                        var cleanupError = TryDeleteFile(currentDatabasePath);
                        if (cleanupError is not null)
                            throw new IOException("실패한 신규 데이터베이스를 정리하지 못했습니다.", cleanupError);
                    }
                }

                onRestorePhaseChanged?.Invoke(RestorePhaseRolledBack);
            }
            catch (Exception rollbackException)
            {
                preserveRecoveryArtifacts = true;
                throw new InvalidOperationException(
                    "복원 실패 후 DB와 첨부파일 세대의 롤백도 완료하지 못했습니다. 롤백 자료를 보존했습니다.",
                    new AggregateException(restoreException, rollbackException));
            }

            throw;
        }
        finally
        {
            TryDeleteFile(stagedDatabasePath);
            TryDeleteDirectory(stagedAttachmentsDirectory);
            TryDeleteDirectory(extractionDirectory);
            if (!preserveRecoveryArtifacts)
            {
                TryDeleteFile(databaseFailedPath);
                TryDeleteDirectory(attachmentsFailedDirectory);
            }
        }
    }

    private static void PreserveUnreferencedCurrentAttachmentsForRecovery(
        string currentDatabasePath,
        string currentAttachmentsDirectory,
        string backupDirectory)
    {
        if (!Directory.Exists(currentAttachmentsDirectory))
            return;

        var normalizedAttachmentsRoot = NormalizeAttachmentRoot(
            currentAttachmentsDirectory);
        ThrowIfReparsePoint(normalizedAttachmentsRoot);
        var referencedPaths = ReadAttachmentReferences(
                currentDatabasePath,
                normalizedAttachmentsRoot)
            .Select(reference => reference.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanFiles = Directory.EnumerateFiles(
                normalizedAttachmentsRoot,
                "*",
                SearchOption.AllDirectories)
            .Select(sourcePath => new
            {
                SourcePath = sourcePath,
                RelativePath = NormalizeManifestPath(
                    Path.GetRelativePath(normalizedAttachmentsRoot, sourcePath))
            })
            .Where(file => !referencedPaths.Contains(file.RelativePath))
            .ToList();
        if (orphanFiles.Count == 0)
            return;

        var recoveryDirectory = Path.Combine(
            backupDirectory,
            RawRestoreRecoveryDirectoryPrefix +
            Guid.NewGuid().ToString("N")[..12]);
        var recoveryAttachmentsDirectory = Path.Combine(
            recoveryDirectory,
            "attachments");

        Directory.CreateDirectory(recoveryDirectory);
        try
        {
            foreach (var orphanFile in orphanFiles)
            {
                EnsureExistingPathChainHasNoReparsePoint(
                    normalizedAttachmentsRoot,
                    orphanFile.SourcePath);
                var destinationPath = Path.GetFullPath(
                    Path.Combine(
                        recoveryAttachmentsDirectory,
                        orphanFile.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                if (!IsWithin(destinationPath, recoveryAttachmentsDirectory))
                {
                    throw new InvalidDataException(
                        "고아 첨부파일 복구 격리 경로가 대상 루트 밖에 있습니다.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var source = new FileStream(
                    orphanFile.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.SequentialScan);
                using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.WriteThrough);
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            var metadata = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    capturedAtUtc = DateTime.UtcNow,
                    sourceDatabasePath = Path.GetFullPath(currentDatabasePath),
                    sourceAttachmentsPath = normalizedAttachmentsRoot,
                    hadAttachmentsDirectory = true,
                    reason = "UnreferencedCurrentAttachments",
                    preservedAttachmentCount = orphanFiles.Count
                },
                JsonOptions);
            WriteFileDurably(
                Path.Combine(recoveryDirectory, "recovery.json"),
                metadata,
                FileMode.CreateNew);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "참조되지 않은 현재 첨부파일의 복구 격리본을 만들던 중 오류가 발생했습니다. " +
                $"부분 복구 자료를 보존했습니다: {recoveryDirectory}",
                ex);
        }
    }

    private static void PreserveRawCurrentGenerationForRecovery(
        string currentDatabasePath,
        string currentAttachmentsDirectory,
        string backupDirectory)
    {
        var recoveryDirectory = Path.Combine(
            backupDirectory,
            RawRestoreRecoveryDirectoryPrefix +
            Guid.NewGuid().ToString("N")[..12]);
        var recoveryDatabasePath = Path.Combine(
            recoveryDirectory,
            BackupDatabaseEntryName);
        var recoveryAttachmentsDirectory = Path.Combine(
            recoveryDirectory,
            "attachments");

        Directory.CreateDirectory(recoveryDirectory);
        try
        {
            CreateConsistentSqliteBackupAsync(
                    currentDatabasePath,
                    recoveryDatabasePath,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            CopyRawAttachmentTreeForRecovery(
                currentAttachmentsDirectory,
                recoveryAttachmentsDirectory);

            var metadata = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    capturedAtUtc = DateTime.UtcNow,
                    sourceDatabasePath = Path.GetFullPath(currentDatabasePath),
                    sourceAttachmentsPath = Path.GetFullPath(currentAttachmentsDirectory),
                    hadAttachmentsDirectory = Directory.Exists(currentAttachmentsDirectory),
                    reason = "CurrentAttachmentGenerationMismatch"
                },
                JsonOptions);
            WriteFileDurably(
                Path.Combine(recoveryDirectory, "recovery.json"),
                metadata,
                FileMode.CreateNew);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "현재 세대가 불완전해 원시 복구 격리본을 만들던 중 오류가 발생했습니다. " +
                $"부분 복구 자료를 보존했습니다: {recoveryDirectory}",
                ex);
        }
    }

    private static void PreserveRawCurrentAttachmentsForRecovery(
        string currentAttachmentsDirectory,
        string backupDirectory)
    {
        var recoveryDirectory = Path.Combine(
            backupDirectory,
            RawRestoreRecoveryDirectoryPrefix +
            Guid.NewGuid().ToString("N")[..12]);
        var recoveryAttachmentsDirectory = Path.Combine(
            recoveryDirectory,
            "attachments");

        Directory.CreateDirectory(recoveryDirectory);
        try
        {
            CopyRawAttachmentTreeForRecovery(
                currentAttachmentsDirectory,
                recoveryAttachmentsDirectory);

            var metadata = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schemaVersion = 1,
                    capturedAtUtc = DateTime.UtcNow,
                    sourceDatabasePath = (string?)null,
                    sourceAttachmentsPath = Path.GetFullPath(currentAttachmentsDirectory),
                    hadAttachmentsDirectory = true,
                    reason = "CurrentDatabaseMissing"
                },
                JsonOptions);
            WriteFileDurably(
                Path.Combine(recoveryDirectory, "recovery.json"),
                metadata,
                FileMode.CreateNew);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "현재 DB는 없지만 첨부파일이 남아 있어 원시 복구 격리본을 만들던 중 오류가 발생했습니다. " +
                $"부분 복구 자료를 보존했습니다: {recoveryDirectory}",
                ex);
        }
    }

    private static void CopyRawAttachmentTreeForRecovery(
        string sourceDirectory,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (!Directory.Exists(sourceDirectory))
            return;

        var normalizedSourceRoot = NormalizeAttachmentRoot(sourceDirectory);
        ThrowIfReparsePoint(normalizedSourceRoot);
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(
                     normalizedSourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            ThrowIfReparsePoint(sourcePath);
            var relativePath = Path.GetRelativePath(normalizedSourceRoot, sourcePath);
            var destinationPath = Path.GetFullPath(
                Path.Combine(destinationDirectory, relativePath));
            if (!IsWithin(destinationPath, destinationDirectory))
                throw new InvalidDataException("원시 복구 첨부파일 경로가 대상 루트 밖에 있습니다.");

            if (Directory.Exists(sourcePath))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.WriteThrough);
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }
    }

    private static IReadOnlyList<DatabaseAttachmentReference> ReadAttachmentReferences(
        string databasePath,
        string declaredAttachmentRoot,
        string? physicalAttachmentRoot = null)
    {
        var normalizedRoot = NormalizeAttachmentRoot(declaredAttachmentRoot);
        var normalizedPhysicalRoot = NormalizeAttachmentRoot(
            string.IsNullOrWhiteSpace(physicalAttachmentRoot)
                ? declaredAttachmentRoot
                : physicalAttachmentRoot);
        var references = new List<DatabaseAttachmentReference>();
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "Id", "StoredPath", "FileSize", "FileHash"
            FROM "TransactionAttachments"
            WHERE "IsDeleted" = 0
            ORDER BY "Id";
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)?.ToString() ?? string.Empty;
            var storedPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var fileSize = reader.IsDBNull(2) ? -1 : reader.GetInt64(2);
            var fileHash = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
            if (string.IsNullOrWhiteSpace(id) ||
                fileSize < 0 ||
                !IsValidSha256(fileHash))
            {
                throw new BackupGenerationMismatchException(
                    $"첨부파일 DB 메타데이터가 불완전합니다: {id}");
            }

            var relativePath = ResolveAttachmentRelativePath(
                storedPath,
                normalizedRoot);
            if (!relativePaths.Add(relativePath))
            {
                throw new BackupGenerationMismatchException(
                    $"여러 첨부 레코드가 같은 저장 파일을 참조합니다: {relativePath}");
            }

            references.Add(new DatabaseAttachmentReference(
                id,
                Path.GetFullPath(storedPath),
                relativePath,
                fileSize,
                fileHash.ToLowerInvariant()));
        }
        reader.Close();

        AddInventoryTransferEvidenceReferences(
            connection,
            normalizedRoot,
            normalizedPhysicalRoot,
            references,
            relativePaths);

        return references;
    }

    private static void AddInventoryTransferEvidenceReferences(
        SqliteConnection connection,
        string normalizedAttachmentRoot,
        string normalizedPhysicalAttachmentRoot,
        ICollection<DatabaseAttachmentReference> references,
        ISet<string> relativePaths)
    {
        AddFilePathReferences(
            connection,
            normalizedAttachmentRoot,
            normalizedPhysicalAttachmentRoot,
            references,
            relativePaths,
            "InventoryTransfers",
            "Id",
            "ReceiveEvidencePath",
            "inventory-transfer");
        AddFilePathReferences(
            connection,
            normalizedAttachmentRoot,
            normalizedPhysicalAttachmentRoot,
            references,
            relativePaths,
            "InventoryTransferTombstoneConflicts",
            "TransferId",
            "ArchivedReceiveEvidencePath",
            "inventory-transfer-conflict");
    }

    private static void AddFilePathReferences(
        SqliteConnection connection,
        string normalizedAttachmentRoot,
        string normalizedPhysicalAttachmentRoot,
        ICollection<DatabaseAttachmentReference> references,
        ISet<string> relativePaths,
        string tableName,
        string idColumnName,
        string pathColumnName,
        string referencePrefix)
    {
        if (!SqliteTableHasColumns(
                connection,
                tableName,
                idColumnName,
                pathColumnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {BuildSqliteIdentifier(idColumnName)}, " +
            $"{BuildSqliteIdentifier(pathColumnName)} " +
            $"FROM {BuildSqliteIdentifier(tableName)} " +
            $"WHERE TRIM(COALESCE({BuildSqliteIdentifier(pathColumnName)}, '')) <> '' " +
            $"ORDER BY {BuildSqliteIdentifier(idColumnName)};";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.IsDBNull(0)
                ? string.Empty
                : reader.GetValue(0)?.ToString() ?? string.Empty;
            var storedPath = reader.IsDBNull(1)
                ? string.Empty
                : reader.GetString(1);
            if (string.IsNullOrWhiteSpace(id) ||
                string.IsNullOrWhiteSpace(storedPath))
            {
                throw new BackupGenerationMismatchException(
                    $"Attachment evidence metadata is incomplete: {referencePrefix}/{id}");
            }

            var relativePath = ResolveAttachmentRelativePath(
                storedPath,
                normalizedAttachmentRoot);
            if (!relativePaths.Add(relativePath))
                continue;

            var physicalPath = Path.GetFullPath(
                Path.Combine(
                    normalizedPhysicalAttachmentRoot,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            if (!IsWithin(
                    physicalPath,
                    normalizedPhysicalAttachmentRoot) ||
                !File.Exists(physicalPath))
            {
                throw new BackupGenerationMismatchException(
                    $"A referenced attachment evidence file is missing: {relativePath}");
            }

            var fileSize = new FileInfo(physicalPath).Length;
            var fileHash = ComputeSha256(physicalPath).ToLowerInvariant();
            references.Add(new DatabaseAttachmentReference(
                $"{referencePrefix}:{id}",
                physicalPath,
                relativePath,
                fileSize,
                fileHash));
        }
    }

    private static bool SqliteTableHasColumns(
        SqliteConnection connection,
        string tableName,
        params string[] requiredColumns)
        => SqliteTableHasColumns(
            connection,
            null,
            tableName,
            requiredColumns);

    private static bool SqliteTableHasColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        params string[] requiredColumns)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.Transaction = transaction;
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$tableName;";
        tableCommand.Parameters.AddWithValue("$tableName", tableName);
        if (Convert.ToInt32(tableCommand.ExecuteScalar() ?? 0) != 1)
            return false;

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var columnCommand = connection.CreateCommand();
        columnCommand.Transaction = transaction;
        columnCommand.CommandText =
            $"PRAGMA table_info({BuildSqliteStringLiteral(tableName)});";
        using var reader = columnCommand.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        return requiredColumns.All(columns.Contains);
    }

    private static string BuildSqliteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static async Task CopyReferencedAttachmentGenerationAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<DatabaseAttachmentReference> references,
        CancellationToken ct)
    {
        var normalizedSourceRoot = NormalizeAttachmentRoot(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        if (Directory.Exists(normalizedSourceRoot))
            ThrowIfReparsePoint(normalizedSourceRoot);
        else if (references.Count > 0)
            throw new BackupGenerationMismatchException("DB가 참조하는 첨부파일 루트가 없습니다.");

        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(reference.SourcePath);
            if (!IsWithin(sourcePath, normalizedSourceRoot))
            {
                throw new BackupGenerationMismatchException(
                    $"첨부파일이 허용된 루트 밖에 있습니다: {reference.RelativePath}");
            }

            try
            {
                EnsureExistingPathChainHasNoReparsePoint(
                    normalizedSourceRoot,
                    sourcePath);
                if (!File.Exists(sourcePath))
                {
                    throw new BackupGenerationMismatchException(
                        $"DB가 참조하는 첨부파일이 없습니다: {reference.RelativePath}");
                }

                var sourceInfo = new FileInfo(sourcePath);
                if (sourceInfo.Length != reference.FileSize)
                {
                    throw new BackupGenerationMismatchException(
                        $"DB와 첨부파일 크기가 다릅니다: {reference.RelativePath}");
                }

                var destinationPath = Path.GetFullPath(
                    Path.Combine(
                        destinationDirectory,
                        reference.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(destinationPath, destinationDirectory))
                {
                    throw new BackupGenerationMismatchException(
                        $"첨부파일 상대 경로가 안전하지 않습니다: {reference.RelativePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 destinationPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, ct);
                    await destination.FlushAsync(ct);
                }

                var copiedInfo = new FileInfo(destinationPath);
                if (copiedInfo.Length != reference.FileSize ||
                    !FixedTimeEqualsHex(
                        await ComputeSha256Async(destinationPath, ct),
                        reference.FileHash))
                {
                    throw new BackupGenerationMismatchException(
                        $"DB와 첨부파일 해시가 다릅니다: {reference.RelativePath}");
                }
            }
            catch (BackupGenerationMismatchException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new BackupGenerationMismatchException(
                    $"첨부파일 세대가 복사 중 변경되었습니다: {reference.RelativePath}",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new BackupGenerationMismatchException(
                    $"첨부파일 세대를 읽을 수 없습니다: {reference.RelativePath}",
                    ex);
            }
        }
    }

    private static async Task<BackupPackageManifest> BuildManifestAsync(
        string databasePath,
        string sourceAttachmentsDirectory,
        string attachmentsDirectory,
        CancellationToken ct)
    {
        var attachments = new List<BackupManifestFile>();
        if (Directory.Exists(attachmentsDirectory))
        {
            foreach (var filePath in Directory.EnumerateFiles(
                         attachmentsDirectory,
                         "*",
                         SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = NormalizeManifestPath(
                    Path.GetRelativePath(attachmentsDirectory, filePath));
                attachments.Add(new BackupManifestFile(
                    relativePath,
                    new FileInfo(filePath).Length,
                    await ComputeSha256Async(filePath, ct)));
            }
        }

        return new BackupPackageManifest(
            BackupManifestSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTime.UtcNow,
            BackupDatabaseEntryName,
            new FileInfo(databasePath).Length,
            await ComputeSha256Async(databasePath, ct),
            NormalizeAttachmentRoot(sourceAttachmentsDirectory),
            attachments);
    }

    private static void ValidateBackupPackageOrThrow(string packagePath)
    {
        var extractionDirectory = BuildSiblingTransientPath(packagePath, ".validate-");
        try
        {
            ExtractAndValidateBackupPackage(packagePath, extractionDirectory);
        }
        finally
        {
            TryDeleteDirectory(extractionDirectory);
        }
    }

    internal static bool IsBackupPackageArchiveWithinBounds(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            ValidateBackupArchivePreflight(archive);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateBackupArchivePreflight(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 ||
            archive.Entries.Count > MaxBackupArchiveEntryCount)
        {
            throw new InvalidDataException(
                $"백업 패키지 항목 수가 허용 범위를 벗어납니다: {archive.Entries.Count:N0}");
        }

        var normalizedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalizedEntryName = ValidateAndNormalizeArchiveEntryName(entry);
            var duplicateKey = normalizedEntryName.TrimEnd('/');
            if (!normalizedEntryNames.Add(duplicateKey))
            {
                throw new InvalidDataException(
                    $"백업 패키지에 중복 경로가 있습니다: {normalizedEntryName}");
            }

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
            if (isDirectory)
            {
                if (entry.Length != 0)
                    throw new InvalidDataException("백업 패키지 디렉터리 항목이 비어 있지 않습니다.");
                continue;
            }

            var maxEntryBytes = GetMaxArchiveEntryBytes(normalizedEntryName);
            if (entry.Length < 0 || entry.Length > maxEntryBytes)
            {
                throw new InvalidDataException(
                    $"백업 패키지 항목 크기가 한도를 초과합니다: {normalizedEntryName}");
            }

            if (entry.Length > MaxBackupArchiveTotalBytes - totalUncompressedBytes)
                throw new InvalidDataException("백업 패키지 전체 압축 해제 크기가 한도를 초과합니다.");
            totalUncompressedBytes += entry.Length;

            if (entry.Length > 0)
            {
                if (entry.CompressedLength <= 0)
                {
                    throw new InvalidDataException(
                        $"백업 패키지 압축 크기를 확인할 수 없습니다: {normalizedEntryName}");
                }

                var compressionRatio = (double)entry.Length / entry.CompressedLength;
                if (!double.IsFinite(compressionRatio) ||
                    compressionRatio > MaxBackupEntryCompressionRatio)
                {
                    throw new InvalidDataException(
                        $"백업 패키지 압축률이 안전 한도를 초과합니다: {normalizedEntryName}");
                }
            }
        }
    }

    private static string ValidateAndNormalizeArchiveEntryName(ZipArchiveEntry entry)
    {
        var rawName = entry.FullName;
        if (string.IsNullOrWhiteSpace(rawName) ||
            rawName.IndexOf('\0') >= 0 ||
            Path.IsPathRooted(rawName) ||
            rawName.StartsWith("/", StringComparison.Ordinal) ||
            rawName.StartsWith("\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("백업 패키지에 안전하지 않은 경로가 있습니다.");
        }

        var normalizedName = NormalizeManifestPath(rawName);
        var pathForValidation = normalizedName.TrimEnd('/');
        var pathSegments = pathForValidation.Split('/');
        if (normalizedName.Length > MaxBackupArchivePathLength ||
            !IsSafeRelativeManifestPath(pathForValidation) ||
            pathSegments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                string.Equals(segment, ".", StringComparison.Ordinal) ||
                string.Equals(segment, "..", StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.') ||
                IsWindowsReservedDeviceName(segment)))
        {
            throw new InvalidDataException(
                $"백업 패키지 경로가 안전하지 않습니다: {normalizedName}");
        }

        var isAllowed =
            string.Equals(
                pathForValidation,
                BackupManifestEntryName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                pathForValidation,
                BackupDatabaseEntryName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                pathForValidation,
                "attachments",
                StringComparison.OrdinalIgnoreCase) ||
            pathForValidation.StartsWith(
                BackupAttachmentsEntryPrefix,
                StringComparison.OrdinalIgnoreCase);
        if (!isAllowed)
        {
            throw new InvalidDataException(
                $"manifest에 포함될 수 없는 백업 패키지 경로입니다: {normalizedName}");
        }

        return normalizedName;
    }

    private static bool IsWindowsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.', 2)[0];
        return string.Equals(stem, "CON", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stem, "PRN", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stem, "AUX", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stem, "NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static long GetMaxArchiveEntryBytes(string normalizedEntryName)
    {
        var path = normalizedEntryName.TrimEnd('/');
        if (string.Equals(path, BackupManifestEntryName, StringComparison.OrdinalIgnoreCase))
            return MaxBackupManifestBytes;
        if (string.Equals(path, BackupDatabaseEntryName, StringComparison.OrdinalIgnoreCase))
            return MaxBackupDatabaseBytes;
        if (path.StartsWith(BackupAttachmentsEntryPrefix, StringComparison.OrdinalIgnoreCase))
            return MaxBackupAttachmentBytes;
        throw new InvalidDataException($"지원하지 않는 백업 패키지 파일입니다: {normalizedEntryName}");
    }

    private static void ExtractArchiveEntryWithBounds(
        ZipArchiveEntry entry,
        Stream destination,
        ref long extractedTotalBytes)
    {
        var normalizedEntryName = ValidateAndNormalizeArchiveEntryName(entry);
        var maxEntryBytes = GetMaxArchiveEntryBytes(normalizedEntryName);
        long extractedEntryBytes = 0;
        var buffer = new byte[81920];
        using var source = entry.Open();
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            if (read > maxEntryBytes - extractedEntryBytes ||
                read > MaxBackupArchiveTotalBytes - extractedTotalBytes)
            {
                throw new InvalidDataException(
                    $"백업 패키지 압축 해제 크기가 스트리밍 한도를 초과합니다: {normalizedEntryName}");
            }

            destination.Write(buffer, 0, read);
            extractedEntryBytes += read;
            extractedTotalBytes += read;
        }

        if (extractedEntryBytes != entry.Length)
        {
            throw new InvalidDataException(
                $"백업 패키지 항목의 선언 크기와 실제 크기가 다릅니다: {normalizedEntryName}");
        }
    }

    private static BackupPackageManifest ExtractAndValidateBackupPackage(
        string packagePath,
        string extractionDirectory)
    {
        if (!File.Exists(packagePath) || new FileInfo(packagePath).Length == 0)
            throw new InvalidDataException("백업 패키지가 없거나 비어 있습니다.");

        Directory.CreateDirectory(extractionDirectory);
        var extractionRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory));
        using (var archive = ZipFile.OpenRead(packagePath))
        {
            ValidateBackupArchivePreflight(archive);
            var normalizedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedTotalBytes = 0;
            foreach (var entry in archive.Entries)
            {
                if (Path.IsPathRooted(entry.FullName) ||
                    entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.StartsWith("\\", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("백업 패키지에 절대 경로가 있습니다.");
                }

                var normalizedEntryName = NormalizeManifestPath(entry.FullName);
                if (string.IsNullOrWhiteSpace(normalizedEntryName))
                    continue;
                if (!normalizedEntryNames.Add(normalizedEntryName))
                    throw new InvalidDataException($"백업 패키지에 중복 경로가 있습니다: {normalizedEntryName}");
                if (!string.Equals(
                        normalizedEntryName,
                        BackupManifestEntryName,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        normalizedEntryName,
                        BackupDatabaseEntryName,
                        StringComparison.OrdinalIgnoreCase) &&
                    !normalizedEntryName.StartsWith(
                        BackupAttachmentsEntryPrefix,
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        normalizedEntryName,
                        "attachments",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"manifest에 포함되지 않는 백업 패키지 경로가 있습니다: {normalizedEntryName}");
                }

                var destinationPath = Path.GetFullPath(
                    Path.Combine(extractionRoot, normalizedEntryName.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithin(destinationPath, extractionRoot))
                    throw new InvalidDataException("백업 패키지에 허용되지 않은 경로가 있습니다.");

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                ExtractArchiveEntryWithBounds(
                    entry,
                    destination,
                    ref extractedTotalBytes);
                destination.Flush(flushToDisk: true);
            }
        }

        var manifestPath = Path.Combine(extractionRoot, BackupManifestEntryName);
        var databasePath = Path.Combine(extractionRoot, BackupDatabaseEntryName);
        if (!File.Exists(manifestPath) || !File.Exists(databasePath))
            throw new InvalidDataException("백업 패키지의 manifest 또는 DB를 찾지 못했습니다.");

        var manifest = JsonSerializer.Deserialize<BackupPackageManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions)
            ?? throw new InvalidDataException("백업 manifest를 판독하지 못했습니다.");
        if (manifest.SchemaVersion != BackupManifestSchemaVersion ||
            string.IsNullOrWhiteSpace(manifest.GenerationId) ||
            string.IsNullOrWhiteSpace(manifest.SourceAttachmentRoot) ||
            !Path.IsPathFullyQualified(manifest.SourceAttachmentRoot) ||
            !string.Equals(
                NormalizeManifestPath(manifest.DatabasePath),
                BackupDatabaseEntryName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("지원하지 않거나 불완전한 백업 manifest입니다.");
        }

        if (new FileInfo(databasePath).Length != manifest.DatabaseSize ||
            !FixedTimeEqualsHex(ComputeSha256(databasePath), manifest.DatabaseSha256))
        {
            throw new InvalidDataException("백업 manifest의 DB 크기 또는 해시가 일치하지 않습니다.");
        }

        ValidateTradePlanDatabaseOrThrow(databasePath, requireApplicationId: true);
        ValidateExtractedAttachmentGenerationOrThrow(
            Path.Combine(extractionRoot, "attachments"),
            manifest);
        ValidateDatabaseAttachmentGenerationOrThrow(
            databasePath,
            Path.Combine(extractionRoot, "attachments"),
            manifest.SourceAttachmentRoot,
            manifest);
        return manifest;
    }

    private static void ValidateExtractedAttachmentGenerationOrThrow(
        string attachmentsDirectory,
        BackupPackageManifest manifest)
    {
        Directory.CreateDirectory(attachmentsDirectory);
        var expectedPaths = new HashSet<string>(
            manifest.Attachments.Select(file => NormalizeManifestPath(file.RelativePath)),
            StringComparer.OrdinalIgnoreCase);
        if (expectedPaths.Count != manifest.Attachments.Count ||
            expectedPaths.Any(path => !IsSafeRelativeManifestPath(path)))
        {
            throw new InvalidDataException("백업 manifest의 첨부파일 경로가 올바르지 않습니다.");
        }

        var actualPaths = Directory.EnumerateFiles(
                attachmentsDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => NormalizeManifestPath(Path.GetRelativePath(attachmentsDirectory, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(expectedPaths))
            throw new InvalidDataException("백업 세대의 첨부파일 목록이 manifest와 일치하지 않습니다.");

        foreach (var expected in manifest.Attachments)
        {
            var relativePath = NormalizeManifestPath(expected.RelativePath);
            var fullPath = Path.GetFullPath(
                Path.Combine(
                    attachmentsDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(fullPath, Path.GetFullPath(attachmentsDirectory)) ||
                new FileInfo(fullPath).Length != expected.Size ||
                !FixedTimeEqualsHex(ComputeSha256(fullPath), expected.Sha256))
            {
                throw new InvalidDataException(
                    $"백업 세대의 첨부파일 크기 또는 해시가 일치하지 않습니다: {relativePath}");
            }
        }
    }

    private static void ValidateDatabaseAttachmentGenerationOrThrow(
        string databasePath,
        string attachmentsDirectory,
        string declaredAttachmentRoot,
        BackupPackageManifest manifest)
    {
        var references = ReadAttachmentReferences(
            databasePath,
            declaredAttachmentRoot,
            attachmentsDirectory);
        var manifestFiles = manifest.Attachments.ToDictionary(
            file => NormalizeManifestPath(file.RelativePath),
            StringComparer.OrdinalIgnoreCase);
        if (manifestFiles.Count != references.Count)
        {
            throw new BackupGenerationMismatchException(
                "DB 첨부 레코드와 manifest 파일 수가 일치하지 않습니다.");
        }

        foreach (var reference in references)
        {
            if (!manifestFiles.TryGetValue(reference.RelativePath, out var manifestFile) ||
                manifestFile.Size != reference.FileSize ||
                !FixedTimeEqualsHex(manifestFile.Sha256, reference.FileHash))
            {
                throw new BackupGenerationMismatchException(
                    $"DB 첨부 메타데이터와 manifest가 일치하지 않습니다: {reference.RelativePath}");
            }

            var extractedPath = Path.GetFullPath(
                Path.Combine(
                    attachmentsDirectory,
                    reference.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(extractedPath, attachmentsDirectory) ||
                !File.Exists(extractedPath) ||
                new FileInfo(extractedPath).Length != reference.FileSize ||
                !FixedTimeEqualsHex(ComputeSha256(extractedPath), reference.FileHash))
            {
                throw new BackupGenerationMismatchException(
                    $"DB 첨부 메타데이터와 백업 파일이 일치하지 않습니다: {reference.RelativePath}");
            }
        }
    }

    private static void RewriteAttachmentStoredPaths(
        string databasePath,
        string sourceAttachmentRoot,
        string destinationAttachmentRoot)
    {
        var normalizedSourceRoot = NormalizeAttachmentRoot(sourceAttachmentRoot);
        var normalizedDestinationRoot = NormalizeAttachmentRoot(destinationAttachmentRoot);
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadWrite));
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var sanitizeDeleted = connection.CreateCommand())
        {
            sanitizeDeleted.Transaction = transaction;
            sanitizeDeleted.CommandText =
                """
                UPDATE "TransactionAttachments"
                SET "StoredPath" = '',
                    "FileSize" = 0,
                    "FileHash" = ''
                WHERE "IsDeleted" <> 0;
                """;
            sanitizeDeleted.ExecuteNonQuery();
        }

        var storedPaths = new List<(string Id, string StoredPath)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                """
                SELECT "Id", "StoredPath"
                FROM "TransactionAttachments"
                WHERE "IsDeleted" = 0
                  AND TRIM(COALESCE("StoredPath", '')) <> ''
                ORDER BY "Id";
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                storedPaths.Add((
                    reader.GetValue(0)?.ToString() ?? string.Empty,
                    reader.GetString(1)));
            }
        }

        foreach (var stored in storedPaths)
        {
            if (string.IsNullOrWhiteSpace(stored.Id))
                throw new InvalidDataException("경로를 재작성할 첨부 레코드 ID가 없습니다.");

            var relativePath = ResolveAttachmentRelativePath(
                stored.StoredPath,
                normalizedSourceRoot);
            var rewrittenPath = Path.GetFullPath(
                Path.Combine(
                    normalizedDestinationRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(rewrittenPath, normalizedDestinationRoot))
                throw new InvalidDataException("첨부파일 복원 경로가 현재 루트 밖으로 벗어납니다.");

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE "TransactionAttachments"
                SET "StoredPath" = $storedPath
                WHERE "Id" = $id;
                """;
            update.Parameters.AddWithValue("$storedPath", rewrittenPath);
            update.Parameters.AddWithValue("$id", stored.Id);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidDataException("첨부파일 저장 경로를 정확히 한 건 갱신하지 못했습니다.");
        }

        RewriteFilePathColumn(
            connection,
            transaction,
            "InventoryTransfers",
            "ReceiveEvidencePath",
            normalizedSourceRoot,
            normalizedDestinationRoot);
        RewriteFilePathColumn(
            connection,
            transaction,
            "InventoryTransferTombstoneConflicts",
            "ArchivedReceiveEvidencePath",
            normalizedSourceRoot,
            normalizedDestinationRoot);

        transaction.Commit();
    }

    private static void RewriteFilePathColumn(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string pathColumnName,
        string normalizedSourceRoot,
        string normalizedDestinationRoot)
    {
        if (!SqliteTableHasColumns(
                connection,
                transaction,
                tableName,
                pathColumnName))
        {
            return;
        }

        var rows = new List<(long RowId, string StoredPath)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText =
                $"SELECT rowid, {BuildSqliteIdentifier(pathColumnName)} " +
                $"FROM {BuildSqliteIdentifier(tableName)} " +
                $"WHERE TRIM(COALESCE({BuildSqliteIdentifier(pathColumnName)}, '')) <> '' " +
                "ORDER BY rowid;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        foreach (var row in rows)
        {
            var relativePath = ResolveAttachmentRelativePath(
                row.StoredPath,
                normalizedSourceRoot);
            var rewrittenPath = Path.GetFullPath(
                Path.Combine(
                    normalizedDestinationRoot,
                    relativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            if (!IsWithin(rewrittenPath, normalizedDestinationRoot))
            {
                throw new InvalidDataException(
                    "An attachment evidence restore path escaped the current root.");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                $"UPDATE {BuildSqliteIdentifier(tableName)} " +
                $"SET {BuildSqliteIdentifier(pathColumnName)} = $storedPath " +
                "WHERE rowid = $rowId;";
            update.Parameters.AddWithValue("$storedPath", rewrittenPath);
            update.Parameters.AddWithValue("$rowId", row.RowId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException(
                    "An attachment evidence restore path could not be rewritten exactly once.");
            }
        }
    }

    private static void ValidateLegacyDatabaseBackupOrThrow(string databasePath)
    {
        if (!IsManagedBackupFileName(Path.GetFileName(databasePath)))
        {
            throw new InvalidDataException(
                "기존 .db 호환 복원은 거래플랜이 관리한 백업 파일명만 허용합니다.");
        }

        // 과거 버전은 application_id를 기록하지 않았습니다. 따라서 기존 .db는
        // 관리 파일명 + 필수 거래플랜 schema + 첨부 레코드 없음 조건을 모두
        // 만족하는 DB에 한해서만 제한 호환합니다.
        ValidateTradePlanDatabaseOrThrow(
            databasePath,
            requireApplicationId: false,
            allowMissingTransactionAttachments: true);
        var applicationId = ReadSqliteApplicationId(databasePath);
        if (applicationId != 0 && applicationId != TradePlanApplicationId)
        {
            throw new InvalidDataException(
                "기존 .db 백업의 application_id가 거래플랜과 호환되지 않습니다.");
        }

        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadOnly));
        connection.Open();
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='TransactionAttachments';";
        var hasAttachmentTable = Convert.ToInt32(tableCommand.ExecuteScalar() ?? 0) == 1;
        if (hasAttachmentTable)
        {
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM \"TransactionAttachments\";";
            var attachmentCount = Convert.ToInt64(countCommand.ExecuteScalar() ?? 0);
            if (attachmentCount != 0)
            {
                throw new InvalidDataException(
                    "기존 .db 백업은 첨부파일 레코드가 없는 경우에만 제한 복원할 수 있습니다.");
            }
        }
    }

    private static void ValidateTradePlanDatabaseOrThrow(
        string databasePath,
        bool requireApplicationId,
        bool allowMissingTransactionAttachments = false)
    {
        ValidateSqliteDatabaseOrThrow(databasePath);
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadOnly));
        connection.Open();

        using (var applicationIdCommand = connection.CreateCommand())
        {
            applicationIdCommand.CommandText = "PRAGMA application_id;";
            var applicationId = Convert.ToInt32(applicationIdCommand.ExecuteScalar() ?? 0);
            if (applicationId != TradePlanApplicationId && requireApplicationId)
            {
                throw new InvalidDataException(
                    "백업 패키지 DB의 거래플랜 application_id가 올바르지 않습니다.");
            }
        }

        foreach (var requiredTable in RequiredTradePlanSchema)
        {
            using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$tableName;";
            tableCommand.Parameters.AddWithValue("$tableName", requiredTable.Key);
            if (Convert.ToInt32(tableCommand.ExecuteScalar() ?? 0) != 1)
            {
                if (allowMissingTransactionAttachments &&
                    string.Equals(
                        requiredTable.Key,
                        "TransactionAttachments",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw new InvalidDataException(
                    $"거래플랜 필수 테이블이 없습니다: {requiredTable.Key}");
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var columnCommand = connection.CreateCommand();
            columnCommand.CommandText =
                $"PRAGMA table_info({BuildSqliteStringLiteral(requiredTable.Key)});";
            using var reader = columnCommand.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));

            foreach (var requiredColumn in requiredTable.Value)
            {
                if (!columns.Contains(requiredColumn))
                {
                    throw new InvalidDataException(
                        $"거래플랜 필수 열이 없습니다: {requiredTable.Key}.{requiredColumn}");
                }
            }
        }
    }

    private static void SetTradePlanApplicationId(string databasePath)
    {
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadWrite));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA application_id={TradePlanApplicationId};";
        command.ExecuteNonQuery();
    }

    private static int ReadSqliteApplicationId(string databasePath)
    {
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void CheckpointSqliteDatabase(string databasePath)
    {
        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadWrite));
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = command.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0) && reader.GetInt64(0) != 0)
            throw new IOException("현재 데이터베이스 WAL 체크포인트가 사용 중이어서 복원을 시작하지 못했습니다.");
    }

    internal static void ValidateSqliteDatabaseOrThrow(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath) ||
            !File.Exists(databasePath) ||
            new FileInfo(databasePath).Length == 0)
        {
            throw new InvalidDataException("SQLite 데이터베이스 파일이 없거나 비어 있습니다.");
        }

        using var connection = new SqliteConnection(BuildSqliteConnectionString(
            databasePath,
            SqliteOpenMode.ReadOnly));
        connection.Open();

        using (var integrityCommand = connection.CreateCommand())
        {
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            using var reader = integrityCommand.ExecuteReader();
            var hasResult = false;
            while (reader.Read())
            {
                hasResult = true;
                var result = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SQLite 무결성 검사에 실패했습니다: {result}");
            }

            if (!hasResult)
                throw new InvalidDataException("SQLite 무결성 검사 결과를 확인하지 못했습니다.");
        }

        using (var foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeyCommand.ExecuteReader();
            if (reader.Read())
            {
                var table = reader.IsDBNull(0) ? "(알 수 없음)" : reader.GetString(0);
                var rowId = reader.IsDBNull(1) ? "(알 수 없음)" : reader.GetValue(1)?.ToString();
                throw new InvalidDataException(
                    $"SQLite 외래 키 검사에 실패했습니다: 테이블={table}, 행={rowId}");
            }
        }
    }

    internal static bool IsVerifiedSqliteDatabase(string databasePath)
    {
        try
        {
            ValidateSqliteDatabaseOrThrow(databasePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsVerifiedBackupArtifact(string backupPath)
        => GetBackupArtifactVerificationStatus(backupPath) ==
           BackupArtifactVerificationStatus.Verified;

    private static BackupArtifactVerificationStatus GetBackupArtifactVerificationStatus(
        string backupPath)
    {
        try
        {
            if (IsLegacyDatabaseBackup(backupPath))
                ValidateLegacyDatabaseBackupOrThrow(backupPath);
            else
                ValidateBackupPackageOrThrow(backupPath);
            return BackupArtifactVerificationStatus.Verified;
        }
        catch (BackupGenerationMismatchException)
        {
            return BackupArtifactVerificationStatus.Invalid;
        }
        catch (InvalidDataException)
        {
            return BackupArtifactVerificationStatus.Invalid;
        }
        catch (JsonException)
        {
            return BackupArtifactVerificationStatus.Invalid;
        }
        catch (CryptographicException)
        {
            return BackupArtifactVerificationStatus.Invalid;
        }
        catch (SqliteException ex)
        {
            return ex.SqliteErrorCode is 11 or 26
                ? BackupArtifactVerificationStatus.Invalid
                : BackupArtifactVerificationStatus.Indeterminate;
        }
        catch (UnauthorizedAccessException)
        {
            return BackupArtifactVerificationStatus.Indeterminate;
        }
        catch (IOException)
        {
            return BackupArtifactVerificationStatus.Indeterminate;
        }
        catch
        {
            return BackupArtifactVerificationStatus.Indeterminate;
        }
    }

    private static void DeleteSqliteSidecarFiles(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            return;

        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = databasePath + suffix;
            if (File.Exists(sidecarPath))
                DeleteFileWithRetry(sidecarPath);
        }
    }

    private static void DeleteFileWithRetry(string path)
    {
        Exception? lastException = null;
        foreach (var delayMilliseconds in new[] { 0, 100, 250, 500 })
        {
            try
            {
                if (delayMilliseconds > 0)
                    Thread.Sleep(delayMilliseconds);
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (lastException is not null)
            throw lastException;
    }

    private static Exception? TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                DeleteFileWithRetry(path);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string BuildSqliteConnectionString(string sourcePath, SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = mode,
            Pooling = false
        }.ToString();

    private static string BuildSqliteStringLiteral(string value)
        => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    private static string BuildSiblingTransientPath(string destinationPath, string token)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("대상 파일의 폴더를 확인할 수 없습니다.");
        var kind = token.Contains("rollback", StringComparison.OrdinalIgnoreCase)
            ? "rollback"
            : token.Contains("failed", StringComparison.OrdinalIgnoreCase)
                ? "failed"
                : token.Contains("marker", StringComparison.OrdinalIgnoreCase)
                    ? "marker"
                    : token.Contains("validate", StringComparison.OrdinalIgnoreCase)
                        ? "validate"
                        : "stage";
        return Path.Combine(directory, $".gp-{kind}-{Guid.NewGuid():N}");
    }

    private static RestoreRecoveryPaths BuildRestoreRecoveryPaths(
        string currentDatabasePath,
        string currentAttachmentsDirectory,
        string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
            throw new InvalidDataException("복원 작업 식별자가 올바르지 않습니다.");

        var databaseDirectory = Path.GetDirectoryName(currentDatabasePath)
            ?? throw new InvalidOperationException("현재 DB 폴더를 확인할 수 없습니다.");
        var attachmentsParentDirectory = Path.GetDirectoryName(currentAttachmentsDirectory)
            ?? throw new InvalidOperationException("현재 첨부파일 상위 폴더를 확인할 수 없습니다.");
        var prefix = $".gp-restore-{operationId}";
        return new RestoreRecoveryPaths(
            Path.Combine(databaseDirectory, prefix + "-extract"),
            Path.Combine(databaseDirectory, prefix + "-database-stage"),
            Path.Combine(
                attachmentsParentDirectory,
                prefix + "-attachments-stage"),
            Path.Combine(databaseDirectory, prefix + "-database-rollback"),
            Path.Combine(databaseDirectory, prefix + "-database-failed"),
            Path.Combine(
                attachmentsParentDirectory,
                prefix + "-attachments-rollback"),
            Path.Combine(
                attachmentsParentDirectory,
                prefix + "-attachments-failed"));
    }

    private static IReadOnlyList<string> CleanupRestoreRecoveryArtifacts(
        RestoreMarker marker,
        string currentDatabasePath,
        string currentAttachmentsDirectory)
    {
        if (string.IsNullOrWhiteSpace(marker.OperationId))
            return [];

        RestoreRecoveryPaths paths;
        try
        {
            paths = BuildRestoreRecoveryPaths(
                currentDatabasePath,
                currentAttachmentsDirectory,
                marker.OperationId);
        }
        catch
        {
            return ["복원 복구 경로 확인 실패"];
        }

        var failures = new List<string>();
        AddCleanupFailure(
            failures,
            paths.StagedDatabasePath,
            TryDeleteFile(paths.StagedDatabasePath));
        AddCleanupFailure(
            failures,
            paths.StagedAttachmentsDirectory,
            TryDeleteDirectory(paths.StagedAttachmentsDirectory));
        AddCleanupFailure(
            failures,
            paths.ExtractionDirectory,
            TryDeleteDirectory(paths.ExtractionDirectory));
        AddCleanupFailure(
            failures,
            paths.DatabaseRollbackPath,
            TryDeleteFile(paths.DatabaseRollbackPath));
        AddCleanupFailure(
            failures,
            paths.AttachmentsRollbackDirectory,
            TryDeleteDirectory(paths.AttachmentsRollbackDirectory));
        AddCleanupFailure(
            failures,
            paths.DatabaseFailedPath,
            TryDeleteFile(paths.DatabaseFailedPath));
        AddCleanupFailure(
            failures,
            paths.AttachmentsFailedDirectory,
            TryDeleteDirectory(paths.AttachmentsFailedDirectory));
        return failures;
    }

    private static void AddCleanupFailure(
        ICollection<string> failures,
        string path,
        Exception? error)
    {
        if (error is not null)
            failures.Add(path);
    }

    private static void WritePendingRestoreMarker(string validatedPath)
    {
        Directory.CreateDirectory(AppPaths.TempDir);
        WriteRestoreMarkerAtomically(
            GetPendingRestoreMarkerPath(),
            new RestoreMarker(validatedPath, MarkerStatePending));
    }

    private static void WriteRestoreMarkerAtomically(string markerPath, RestoreMarker marker)
    {
        var markerDirectory = Path.GetDirectoryName(markerPath)
            ?? throw new InvalidOperationException("복원 상태 폴더를 확인할 수 없습니다.");
        Directory.CreateDirectory(markerDirectory);
        var stagingPath = BuildSiblingTransientPath(markerPath, ".marker-staging-");
        try
        {
            WriteFileDurably(
                stagingPath,
                JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions),
                FileMode.CreateNew);
            File.Move(stagingPath, markerPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(stagingPath);
        }
    }

    private static void WriteFileDurably(
        string path,
        ReadOnlySpan<byte> content,
        FileMode mode)
    {
        using var stream = new FileStream(
            path,
            mode,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static RestoreMarker? ReadRestoreMarker(string markerPath)
    {
        try
        {
            var raw = File.ReadAllText(markerPath).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (!raw.StartsWith('{'))
                return new RestoreMarker(raw, MarkerStatePending);
            return JsonSerializer.Deserialize<RestoreMarker>(raw, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<FileInfo> GetVerifiedPublishedBackupFiles(string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory))
            return [];

        return EnumeratePublishedBackupCandidates(backupDirectory)
            .Select(path => new FileInfo(path))
            .Where(file => IsVerifiedBackupArtifact(file.FullName))
            .ToList();
    }

    private static IEnumerable<string> EnumeratePublishedBackupCandidates(string backupDirectory)
        => Directory.EnumerateFiles(backupDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsPublishedBackupCandidate);

    private static bool IsPublishedBackupCandidate(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, ".db", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, BackupPackageExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !fileName.StartsWith(".", StringComparison.Ordinal) &&
               !fileName.EndsWith(".partial.db", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".staging.db", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".tmp.db", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(BackupStagingToken, StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(RestoreStagingToken, StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(RestoreRollbackToken, StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(RestoreFailedToken, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyDatabaseBackup(string path)
        => string.Equals(Path.GetExtension(path), ".db", StringComparison.OrdinalIgnoreCase);

    public static void TrimManagedBackups()
        => TrimManagedBackups(AppPaths.BackupDir, GetPendingRestoreMarkerPath());

    internal static void TrimManagedBackups(string backupDirectory, string markerPath)
    {
        Directory.CreateDirectory(backupDirectory);

        var protectedBackupPaths = GetProtectedBackupPaths(markerPath, backupDirectory);
        var backups = EnumeratePublishedBackupCandidates(backupDirectory)
            .Select(path => new FileInfo(path))
            .Where(file => IsManagedBackupFileName(file.Name))
            .ToList();

        var classifiedBackups = backups
            .Select(file => (
                File: file,
                Status: GetBackupArtifactVerificationStatus(file.FullName)))
            .ToList();
        var verifiedBackups = classifiedBackups
            .Where(candidate =>
                candidate.Status == BackupArtifactVerificationStatus.Verified)
            .Select(candidate => candidate.File)
            .ToList();
        var newestVerifiedBackup = verifiedBackups
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newestVerifiedBackup is not null)
            protectedBackupPaths.Add(Path.GetFullPath(newestVerifiedBackup.FullName));

        var now = DateTime.Now;
        var invalidPastBackups = classifiedBackups
            .Where(candidate =>
                candidate.Status == BackupArtifactVerificationStatus.Invalid)
            .Select(candidate => candidate.File)
            .Where(file => file.LastWriteTime.Date < now.Date)
            .Where(file => !IsProtectedBackup(file, protectedBackupPaths))
            .ToList();

        foreach (var old in SelectManagedBackupsToDeleteForRetention(
                     verifiedBackups,
                     now,
                     protectedBackupPaths)
                 .Concat(invalidPastBackups)
                 .DistinctBy(file => Path.GetFullPath(file.FullName)))
        {
            try
            {
                old.Delete();
            }
            catch
            {
                // 오래된 백업 정리 실패는 전체 백업 성공을 막지 않습니다.
            }
        }
    }

    private static bool IsManagedBackupFileName(string fileName)
        => fileName.StartsWith("거래플랜_", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("거래플랜-", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("salesmaster_", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("salesmaster-", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> GetProtectedBackupPaths(
        string markerPath,
        string backupDirectory)
    {
        var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidateMarkerPath in new[] { markerPath, markerPath + MarkerProcessingSuffix })
        {
            try
            {
                if (!File.Exists(candidateMarkerPath))
                    continue;
                var marker = ReadRestoreMarker(candidateMarkerPath);
                var pendingBackupPath = marker is null
                    ? null
                    : ValidateBackupPath(marker.BackupPath, backupDirectory);
                if (!string.IsNullOrWhiteSpace(pendingBackupPath))
                    protectedPaths.Add(Path.GetFullPath(pendingBackupPath));
            }
            catch
            {
                // 복원 상태 확인 실패가 백업 정리를 막지는 않습니다.
            }
        }

        return protectedPaths;
    }

    private static IReadOnlyList<FileInfo> SelectManagedBackupsToDeleteForRetention(
        IEnumerable<FileInfo> managedBackups,
        DateTime now,
        ISet<string>? protectedBackupPaths = null)
    {
        var today = now.Date;
        var retentionStartDate = today.AddDays(-DailyManagedBackupRetentionDays);
        var deleteTargets = new List<FileInfo>();

        foreach (var group in managedBackups
                     .GroupBy(file => file.LastWriteTime.Date))
        {
            var backupDate = group.Key;
            var ordered = group
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            if (backupDate >= today)
                continue;
            if (backupDate < retentionStartDate)
            {
                deleteTargets.AddRange(
                    ordered.Where(file =>
                        !IsProtectedBackup(file, protectedBackupPaths)));
                continue;
            }

            deleteTargets.AddRange(
                ordered.Skip(1).Where(file =>
                    !IsProtectedBackup(file, protectedBackupPaths)));
        }

        return deleteTargets;
    }

    private static bool IsProtectedBackup(FileInfo file, ISet<string>? protectedBackupPaths)
        => protectedBackupPaths is not null &&
           protectedBackupPaths.Contains(Path.GetFullPath(file.FullName));

    private static string NormalizeManifestPath(string path)
        => (path ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static bool IsSafeRelativeManifestPath(string path)
        => !string.IsNullOrWhiteSpace(path) &&
           !Path.IsPathRooted(path) &&
           !string.Equals(path, "..", StringComparison.Ordinal) &&
           !path.StartsWith("../", StringComparison.Ordinal) &&
           !path.Contains("/../", StringComparison.Ordinal);

    private static bool IsWithin(string candidatePath, string parentPath)
    {
        var candidate = Path.GetFullPath(candidatePath);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        return candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAttachmentRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) ||
            !Path.IsPathFullyQualified(rootPath))
        {
            throw new InvalidDataException("첨부파일 루트는 완전한 절대 경로여야 합니다.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    private static string ResolveAttachmentRelativePath(
        string storedPath,
        string declaredAttachmentRoot)
    {
        if (string.IsNullOrWhiteSpace(storedPath) ||
            !Path.IsPathFullyQualified(storedPath))
        {
            throw new BackupGenerationMismatchException(
                "DB 첨부파일 StoredPath가 절대 경로가 아닙니다.");
        }

        var normalizedRoot = NormalizeAttachmentRoot(declaredAttachmentRoot);
        var fullStoredPath = Path.GetFullPath(storedPath);
        if (!IsWithin(fullStoredPath, normalizedRoot))
        {
            throw new BackupGenerationMismatchException(
                "DB 첨부파일 StoredPath가 선언된 첨부파일 루트 밖에 있습니다.");
        }

        var relativePath = NormalizeManifestPath(
            Path.GetRelativePath(normalizedRoot, fullStoredPath));
        if (!IsSafeRelativeManifestPath(relativePath))
        {
            throw new BackupGenerationMismatchException(
                "DB 첨부파일 StoredPath에서 안전한 상대 경로를 만들 수 없습니다.");
        }

        return relativePath;
    }

    private static void EnsureExistingPathChainHasNoReparsePoint(
        string rootPath,
        string filePath)
    {
        var normalizedRoot = NormalizeAttachmentRoot(rootPath);
        var fullFilePath = Path.GetFullPath(filePath);
        if (!IsWithin(fullFilePath, normalizedRoot))
            throw new InvalidDataException("첨부파일 경로가 허용된 루트 밖에 있습니다.");

        ThrowIfReparsePoint(normalizedRoot);
        var relativePath = Path.GetRelativePath(normalizedRoot, fullFilePath);
        var currentPath = normalizedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
                break;
            ThrowIfReparsePoint(currentPath);
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"재분석 지점은 백업할 수 없습니다: {path}");
    }

    private static void MoveDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (!Directory.Exists(sourceDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
            Directory.Move(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
            File.Move(file, Path.Combine(destinationDirectory, Path.GetFileName(file)));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsHex(string actual, string expected)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(expected));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            return false;
        try
        {
            return Convert.FromHexString(value).Length == SHA256.HashSizeInBytes;
        }
        catch
        {
            return false;
        }
    }

    private sealed record RestoreMarker(
        string BackupPath,
        string State,
        string? OperationId = null,
        string? Phase = null,
        bool? HadCurrentDatabase = null,
        bool? HadCurrentAttachments = null);

    private sealed record RestoreRecoveryPaths(
        string ExtractionDirectory,
        string StagedDatabasePath,
        string StagedAttachmentsDirectory,
        string DatabaseRollbackPath,
        string DatabaseFailedPath,
        string AttachmentsRollbackDirectory,
        string AttachmentsFailedDirectory);

    private sealed record DatabaseAttachmentReference(
        string Id,
        string SourcePath,
        string RelativePath,
        long FileSize,
        string FileHash);

    private sealed class BackupGenerationMismatchException : IOException
    {
        public BackupGenerationMismatchException(string message)
            : base(message)
        {
        }

        public BackupGenerationMismatchException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed record BackupPackageManifest(
        int SchemaVersion,
        string GenerationId,
        DateTime CreatedAtUtc,
        string DatabasePath,
        long DatabaseSize,
        string DatabaseSha256,
        string SourceAttachmentRoot,
        IReadOnlyList<BackupManifestFile> Attachments);

    private sealed record BackupManifestFile(
        string RelativePath,
        long Size,
        string Sha256);

    private enum BackupArtifactVerificationStatus
    {
        Verified,
        Invalid,
        Indeterminate
    }
}
