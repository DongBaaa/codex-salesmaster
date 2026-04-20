using System.IO;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class AppPaths
{
    private const string AppRootOverrideEnvironmentKey = "GEORAEPLAN_APP_ROOT";
    private static readonly string _base = ResolveBaseDirectory();

    public static string DataDir { get; } = Path.Combine(_base, "data");
    public static string BackupDir { get; } = Path.Combine(_base, "backup");
    public static string TempDir { get; } = Path.Combine(_base, "temp");
    public static string LogDir { get; } = Path.Combine(_base, "logs");
    public static string DiagnosticsDir { get; } = Path.Combine(_base, "diagnostics");
    public static string AttachmentsDir { get; } = Path.Combine(_base, "attachments");
    public static string CustomerContractPreviewDir { get; } = Path.Combine(TempDir, "customer-contracts");
    public static string TransactionAttachmentsDir { get; } = Path.Combine(AttachmentsDir, "transactions");
    public static string LocalDbFile { get; } = Path.Combine(DataDir, "거래플랜.db");

    static AppPaths()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(DiagnosticsDir);
        Directory.CreateDirectory(AttachmentsDir);
        Directory.CreateDirectory(CustomerContractPreviewDir);
        Directory.CreateDirectory(TransactionAttachmentsDir);
    }

    private static string ResolveBaseDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(AppRootOverrideEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "거래플랜");
    }
}
