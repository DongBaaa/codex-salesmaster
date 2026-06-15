using System.IO;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class AppPaths
{
    private const string AppRootOverrideEnvironmentKey = "GEORAEPLAN_APP_ROOT";
    private const string TempRootOverrideEnvironmentKey = "GEORAEPLAN_TEMP_ROOT";
    private static readonly string _base = ResolveBaseDirectory();
    private static readonly string _tempRoot = ResolveTempRootDirectory();

    public static string DataDir { get; } = Path.Combine(_base, "data");
    public static string BackupDir { get; } = Path.Combine(_base, "backup");
    public static string TempRoot { get; } = _tempRoot;
    public static string TempDir { get; } = Path.Combine(TempRoot, "desktop");
    public static string LogDir { get; } = Path.Combine(_base, "logs");
    public static string DiagnosticsDir { get; } = Path.Combine(_base, "diagnostics");
    public static string AttachmentsDir { get; } = Path.Combine(_base, "attachments");
    public static string UserDownloadsDir { get; } = ResolveUserDownloadsDirectory();
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
        Directory.CreateDirectory(UserDownloadsDir);
        Directory.CreateDirectory(CustomerContractPreviewDir);
        Directory.CreateDirectory(TransactionAttachmentsDir);

        Environment.SetEnvironmentVariable(TempRootOverrideEnvironmentKey, TempRoot);
        Environment.SetEnvironmentVariable("TEMP", TempRoot);
        Environment.SetEnvironmentVariable("TMP", TempRoot);
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

    private static string ResolveTempRootDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey),
            Path.Combine("D:\\", "거래플랜", "temp"),
            Path.Combine(_base, "temp")
        };

        foreach (var candidate in candidates)
        {
            if (TryPrepareWritableDirectory(candidate, out var resolvedPath))
                return resolvedPath;
        }

        return Path.Combine(_base, "temp");
    }

    private static bool TryPrepareWritableDirectory(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            resolvedPath = Path.GetFullPath(path);
            Directory.CreateDirectory(resolvedPath);

            var probePath = Path.Combine(resolvedPath, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static string ResolveUserDownloadsDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            return Path.Combine(userProfile, "Downloads");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            return documents;

        return _base;
    }
}
