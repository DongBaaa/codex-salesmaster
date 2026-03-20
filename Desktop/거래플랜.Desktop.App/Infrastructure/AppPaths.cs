using System.IO;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class AppPaths
{
    private static readonly string _legacyBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SalesMaster");

    private static readonly string _base = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "거래플랜");

    public static string DataDir { get; } = Path.Combine(_base, "data");
    public static string BackupDir { get; } = Path.Combine(_base, "backup");
    public static string TempDir { get; } = Path.Combine(_base, "temp");
    public static string LogDir { get; } = Path.Combine(_base, "logs");
    public static string AttachmentsDir { get; } = Path.Combine(_base, "attachments");
    public static string CustomerContractPreviewDir { get; } = Path.Combine(TempDir, "customer-contracts");
    public static string TransactionAttachmentsDir { get; } = Path.Combine(AttachmentsDir, "transactions");
    public static string LocalDbFile { get; } = Path.Combine(DataDir, "거래플랜.db");

    static AppPaths()
    {
        MergeLegacyDataIfNeeded();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(AttachmentsDir);
        Directory.CreateDirectory(CustomerContractPreviewDir);
        Directory.CreateDirectory(TransactionAttachmentsDir);
    }

    private static void MergeLegacyDataIfNeeded()
    {
        if (!Directory.Exists(_legacyBase))
            return;

        MergeDirectory(_legacyBase, _base);
    }

    private static void MergeDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destinationFile = Path.Combine(destinationDir, relative);
            var destinationParent = Path.GetDirectoryName(destinationFile);

            if (!string.IsNullOrWhiteSpace(destinationParent))
                Directory.CreateDirectory(destinationParent);

            if (!File.Exists(destinationFile))
                File.Copy(file, destinationFile);
        }
    }
}
