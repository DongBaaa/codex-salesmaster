using System.IO;

namespace SalesMaster.Desktop.App.Infrastructure;

public static class AppPaths
{
    private static readonly string _base = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SalesMaster");

    public static string DataDir { get; } = Path.Combine(_base, "data");
    public static string BackupDir { get; } = Path.Combine(_base, "backup");
    public static string TempDir { get; } = Path.Combine(_base, "temp");
    public static string LocalDbFile { get; } = Path.Combine(DataDir, "salesmaster.db");

    static AppPaths()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(TempDir);
    }
}
