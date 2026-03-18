using System.IO;
using SalesMaster.Desktop.App.Infrastructure;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Copies the local SQLite database file to AppData\Local\SalesMaster\Backups\.
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
            var dest = Path.Combine(AppPaths.BackupDir, $"salesmaster_{stamp}.db");
            Directory.CreateDirectory(AppPaths.BackupDir);

            await using var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write);
            await srcStream.CopyToAsync(dstStream, ct);

            // Keep only the 10 most recent backups
            var backups = Directory.GetFiles(AppPaths.BackupDir, "salesmaster_*.db")
                .OrderByDescending(f => f)
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
