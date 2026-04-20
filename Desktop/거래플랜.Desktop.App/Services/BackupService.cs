using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

public sealed record BackupSnapshotInfo(
    string FilePath,
    string FileName,
    DateTime LastWriteTime,
    long SizeBytes,
    string DisplayName,
    string SizeText);

/// <summary>
/// 로컬 SQLite DB 백업/복원 예약을 관리합니다.
/// 실제 DB 복원은 앱 시작 시 DbContext가 열리기 전에 적용합니다.
/// </summary>
public sealed class BackupService
{
    private const string PendingRestoreMarkerFileName = "pending-db-restore.txt";
    private const int MaxManagedBackupCount = 12;

    public async Task<bool> BackupNowAsync(CancellationToken ct = default)
        => await BackupNowWithPathAsync(ct) is not null;

    public async Task<string?> BackupNowWithPathAsync(CancellationToken ct = default)
    {
        try
        {
            var src = AppPaths.LocalDbFile;
            if (!File.Exists(src))
                return null;

            Directory.CreateDirectory(AppPaths.BackupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var dest = Path.Combine(AppPaths.BackupDir, $"거래플랜_{stamp}.db");

            await CreateConsistentSqliteBackupAsync(src, dest, ct);

            TrimManagedBackups();
            return dest;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<BackupSnapshotInfo> GetBackupSnapshots()
    {
        Directory.CreateDirectory(AppPaths.BackupDir);

        return Directory.EnumerateFiles(AppPaths.BackupDir, "*.db", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
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

        Directory.CreateDirectory(AppPaths.TempDir);
        File.WriteAllText(GetPendingRestoreMarkerPath(), validatedPath);
        message = $"선택한 백업을 다음 실행 시 복원하도록 예약했습니다.{Environment.NewLine}앱을 완전히 종료한 뒤 다시 실행하세요.";
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
    {
        try
        {
            var markerPath = GetPendingRestoreMarkerPath();
            if (!File.Exists(markerPath))
                return null;

            var requestedPath = File.ReadAllText(markerPath).Trim();
            File.Delete(markerPath);

            if (string.IsNullOrWhiteSpace(requestedPath))
                return "예약된 백업 복원 정보를 확인하지 못해 작업을 건너뛰었습니다.";

            var validatedBackupPath = ValidateBackupPath(requestedPath);
            if (validatedBackupPath is null || !File.Exists(validatedBackupPath))
                return "예약된 백업 파일을 찾지 못해 복원을 건너뛰었습니다.";

            Directory.CreateDirectory(AppPaths.DataDir);
            Directory.CreateDirectory(AppPaths.BackupDir);

            if (File.Exists(AppPaths.LocalDbFile))
            {
                var currentBackupPath = Path.Combine(
                    AppPaths.BackupDir,
                    $"거래플랜_before_restore_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");
                CreateConsistentSqliteBackup(AppPaths.LocalDbFile, currentBackupPath);
            }

            DeleteSqliteSidecarFiles(AppPaths.LocalDbFile);
            File.Copy(validatedBackupPath, AppPaths.LocalDbFile, overwrite: true);
            DeleteSqliteSidecarFiles(AppPaths.LocalDbFile);
            TrimManagedBackups();
            return $"백업 복원이 적용되었습니다: {Path.GetFileName(validatedBackupPath)}";
        }
        catch (Exception ex)
        {
            return $"백업 복원 적용 중 오류가 발생했습니다: {ex.Message}";
        }
    }

    private static string GetPendingRestoreMarkerPath()
        => Path.Combine(AppPaths.TempDir, PendingRestoreMarkerFileName);

    private static string? ValidateBackupPath(string backupPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(backupPath);
            var backupRoot = Path.GetFullPath(AppPaths.BackupDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)
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

    private static async Task CreateConsistentSqliteBackupAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("sourcePath가 비어 있습니다.", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("destinationPath가 비어 있습니다.", nameof(destinationPath));

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);

        await using var connection = new SqliteConnection(BuildSqliteConnectionString(sourcePath));
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO {BuildSqliteStringLiteral(destinationPath)};";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void CreateConsistentSqliteBackup(string sourcePath, string destinationPath)
        => CreateConsistentSqliteBackupAsync(sourcePath, destinationPath, CancellationToken.None).GetAwaiter().GetResult();

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
            if (!File.Exists(sidecarPath))
                continue;

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

    private static string BuildSqliteConnectionString(string sourcePath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

    private static string BuildSqliteStringLiteral(string value)
        => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    public static void TrimManagedBackups()
    {
        var backups = Directory.EnumerateFiles(AppPaths.BackupDir, "*.db", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => IsManagedBackupFileName(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxManagedBackupCount)
            .ToList();

        foreach (var old in backups)
        {
            try
            {
                old.Delete();
            }
            catch
            {
                // 오래된 백업 정리 실패는 전체 백업 성공을 막지 않음
            }
        }
    }

    private static bool IsManagedBackupFileName(string fileName)
        => fileName.StartsWith("거래플랜_", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("거래플랜-", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("salesmaster_", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith("salesmaster-", StringComparison.OrdinalIgnoreCase);
}
