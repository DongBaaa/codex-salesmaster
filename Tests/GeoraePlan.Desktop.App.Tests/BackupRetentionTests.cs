using System.Reflection;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackupRetentionTests
{
    [Fact]
    public void BackupRetention_KeepsTodayBackupsAndLatestBackupPerPastDay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"georaeplan-backup-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var now = new DateTime(2026, 4, 29, 17, 0, 0, DateTimeKind.Local);
            var todayMorning = CreateBackupFile(root, "거래플랜_20260429_090000_000.db", now.Date.AddHours(9));
            var todayAfternoon = CreateBackupFile(root, "거래플랜_20260429_150000_000.db", now.Date.AddHours(15));
            var yesterdayEarly = CreateBackupFile(root, "거래플랜_20260428_090000_000.db", now.Date.AddDays(-1).AddHours(9));
            var yesterdayLate = CreateBackupFile(root, "거래플랜_20260428_230000_000.db", now.Date.AddDays(-1).AddHours(23));
            var expired = CreateBackupFile(root, "거래플랜_20260320_230000_000.db", now.Date.AddDays(-40).AddHours(23));
            var protectedExpired = CreateBackupFile(root, "거래플랜_20260319_230000_000.db", now.Date.AddDays(-41).AddHours(23));

            var deleteTargets = SelectDeleteTargets(
                [todayMorning, todayAfternoon, yesterdayEarly, yesterdayLate, expired, protectedExpired],
                now,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { protectedExpired.FullName });

            Assert.DoesNotContain(deleteTargets, file => file.FullName == todayMorning.FullName);
            Assert.DoesNotContain(deleteTargets, file => file.FullName == todayAfternoon.FullName);
            Assert.Contains(deleteTargets, file => file.FullName == yesterdayEarly.FullName);
            Assert.DoesNotContain(deleteTargets, file => file.FullName == yesterdayLate.FullName);
            Assert.Contains(deleteTargets, file => file.FullName == expired.FullName);
            Assert.DoesNotContain(deleteTargets, file => file.FullName == protectedExpired.FullName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FileInfo CreateBackupFile(string root, string fileName, DateTime lastWriteTime)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, "backup");
        File.SetLastWriteTime(path, lastWriteTime);
        File.SetLastWriteTimeUtc(path, lastWriteTime.ToUniversalTime());
        return new FileInfo(path);
    }

    private static IReadOnlyList<FileInfo> SelectDeleteTargets(
        IReadOnlyList<FileInfo> files,
        DateTime now,
        ISet<string> protectedPaths)
    {
        var method = typeof(BackupService).GetMethod(
            "SelectManagedBackupsToDeleteForRetention",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [files, now, protectedPaths]);
        return Assert.IsAssignableFrom<IReadOnlyList<FileInfo>>(result);
    }
}
