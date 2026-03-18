using System.IO;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Copies the local SQLite database file to AppData\Local\거래플랜\backup\.
/// No path is shown to the user; only confirmation or error is returned.
/// </summary>
public sealed class BackupService
{
    public async Task<bool> BackupNowAsync(CancellationToken ct = default)
    {
        try
        {
            var src = AppPaths.LocalDbFile;
            if (!File.Exists(src)) return false;

            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(AppPaths.BackupDir, $"거래플랜_{stamp}.db");
            Directory.CreateDirectory(AppPaths.BackupDir);

            await using var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write);
            await srcStream.CopyToAsync(dstStream, ct);

            // Keep only the 10 most recent backups. Include legacy backup names during migration.
            var backups = Directory.EnumerateFiles(AppPaths.BackupDir, "*.db")
                .Where(path =>
                {
                    var fileName = Path.GetFileName(path);
                    return fileName.StartsWith("거래플랜_", StringComparison.OrdinalIgnoreCase) ||
                           fileName.StartsWith("salesmaster_", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .Skip(10)
                .ToList();
            foreach (var old in backups) File.Delete(old);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
