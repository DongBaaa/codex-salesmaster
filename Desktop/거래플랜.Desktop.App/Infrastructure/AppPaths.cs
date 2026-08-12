using System.IO;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class AppPaths
{
    private const string AppRootOverrideEnvironmentKey = "GEORAEPLAN_APP_ROOT";
    private const string TempRootOverrideEnvironmentKey = "GEORAEPLAN_TEMP_ROOT";
    private const string DownloadsRootOverrideEnvironmentKey = "GEORAEPLAN_DOWNLOADS_ROOT";
    private const string TestModeEnvironmentKey = "GEORAEPLAN_TEST_MODE";
    private static readonly string _base = ResolveBaseDirectory();
    private static readonly string _tempRoot = ResolveTempRootDirectory();

    public static string DataDir { get; } = Path.Combine(_base, "data");
    public static string BackupDir { get; } = Path.Combine(_base, "backup");
    public static string TempRoot { get; } = _tempRoot;
    public static string TempDir { get; } = Path.Combine(TempRoot, "desktop");
    public static string LogDir { get; } = Path.Combine(_base, "logs");
    public static string DiagnosticsDir { get; } = Path.Combine(_base, "diagnostics");
    public static string CompatibilityDir { get; } = Path.Combine(_base, "compatibility");
    public static string AttachmentsDir { get; } = Path.Combine(_base, "attachments");
    public static string AttachmentFileJournalDir { get; } = Path.Combine(AttachmentsDir, ".file-journals");
    public static string UserDownloadsDir { get; } = ResolveUserDownloadsDirectory();
    public static string CustomerContractPreviewDir { get; } = Path.Combine(TempDir, "customer-contracts");
    public static string TransactionAttachmentsDir { get; } = Path.Combine(AttachmentsDir, "transactions");
    public static string InventoryTransferConflictEvidenceDir { get; } =
        Path.Combine(TransactionAttachmentsDir, ".inventory-transfer-conflicts");
    public static string LocalDbFile { get; } = Path.Combine(DataDir, "거래플랜.db");
    internal static string AppRoot => _base;
    internal static bool IsTestEnvironment => IsTestProcess();

    static AppPaths()
    {
        EnsureConfiguredAndTestRootsAreReparseSafe();

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(DiagnosticsDir);
        Directory.CreateDirectory(CompatibilityDir);
        Directory.CreateDirectory(AttachmentsDir);
        Directory.CreateDirectory(AttachmentFileJournalDir);
        Directory.CreateDirectory(UserDownloadsDir);
        Directory.CreateDirectory(CustomerContractPreviewDir);
        Directory.CreateDirectory(TransactionAttachmentsDir);
        Directory.CreateDirectory(InventoryTransferConflictEvidenceDir);

        // Directory creation is a separate filesystem operation. Re-check the
        // complete existing chain so a configured test root cannot become a
        // junction between resolution and first use.
        EnsureConfiguredAndTestRootsAreReparseSafe();

        Environment.SetEnvironmentVariable(TempRootOverrideEnvironmentKey, TempRoot);
        Environment.SetEnvironmentVariable("TEMP", TempRoot);
        Environment.SetEnvironmentVariable("TMP", TempRoot);
    }

    private static string ResolveBaseDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(AppRootOverrideEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverridePath = Path.GetFullPath(overridePath);
            if (IsTestProcess() && OverlapsDefaultUserAppRoot(resolvedOverridePath))
            {
                throw new InvalidOperationException(
                    "A test process cannot use the real user application data directory. "
                    + "Set GEORAEPLAN_APP_ROOT to an isolated test directory.");
            }

            EnsureNoExistingReparsePointInPathChain(
                resolvedOverridePath,
                AppRootOverrideEnvironmentKey);
            return resolvedOverridePath;
        }

        if (IsTestProcess())
        {
            throw new InvalidOperationException(
                "GEORAEPLAN_APP_ROOT is required for test processes so tests cannot access real user data.");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "거래플랜");
    }

    private static string ResolveTempRootDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            return PrepareConfiguredWritableDirectory(
                configuredRoot,
                TempRootOverrideEnvironmentKey);
        }

        var candidates = new[]
        {
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

    private static string PrepareConfiguredWritableDirectory(string path, string settingName)
    {
        var resolvedPath = Path.GetFullPath(path);
        EnsureNoExistingReparsePointInPathChain(resolvedPath, settingName);
        Directory.CreateDirectory(resolvedPath);
        EnsureNoExistingReparsePointInPathChain(resolvedPath, settingName);

        var probePath = Path.Combine(
            resolvedPath,
            $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return resolvedPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{settingName} must identify a writable directory.",
                ex);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch
            {
                // Preserve the original validation failure.
            }
        }
    }

    private static bool TryPrepareWritableDirectory(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            resolvedPath = Path.GetFullPath(path);
            if (IsTestProcess() && !HasNoExistingReparsePointInPathChain(resolvedPath))
            {
                resolvedPath = string.Empty;
                return false;
            }

            Directory.CreateDirectory(resolvedPath);
            if (IsTestProcess() && !HasNoExistingReparsePointInPathChain(resolvedPath))
            {
                resolvedPath = string.Empty;
                return false;
            }

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
        var overridePath = Environment.GetEnvironmentVariable(DownloadsRootOverrideEnvironmentKey);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var resolvedOverridePath = Path.GetFullPath(overridePath);
            EnsureNoExistingReparsePointInPathChain(
                resolvedOverridePath,
                DownloadsRootOverrideEnvironmentKey);
            return resolvedOverridePath;
        }

        if (IsTestProcess())
            return Path.Combine(_base, "downloads");

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            return Path.Combine(userProfile, "Downloads");

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents))
            return documents;

        return _base;
    }

    private static bool OverlapsDefaultUserAppRoot(string path)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return false;

        var defaultPath = Path.GetFullPath(Path.Combine(localAppData, "거래플랜"));
        return PathsOverlap(path, defaultPath);
    }

    private static bool IsTestProcess()
    {
        if (IsTruthy(Environment.GetEnvironmentVariable(TestModeEnvironmentKey)))
            return true;

        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
        return processName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("vstest", StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith("xunit", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEndingDirectorySeparator(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsOverlap(string left, string right)
    {
        var normalizedLeft = TrimEndingDirectorySeparator(left);
        var normalizedRight = TrimEndingDirectorySeparator(right);
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsWithin(normalizedLeft, normalizedRight)
             || IsWithin(normalizedRight, normalizedLeft);
    }

    internal static bool IsWithinAppRoot(string path)
    {
        try
        {
            var normalizedPath = TrimEndingDirectorySeparator(path);
            var normalizedRoot = TrimEndingDirectorySeparator(_base);
            var isLexicallyContained = string.Equals(
                                           normalizedPath,
                                           normalizedRoot,
                                           StringComparison.OrdinalIgnoreCase)
                                       || IsWithin(normalizedPath, normalizedRoot);
            return isLexicallyContained
                   && (!IsTestProcess()
                       || (HasNoExistingReparsePointInPathChain(normalizedRoot)
                           && HasNoExistingReparsePointInPathChain(normalizedPath)));
        }
        catch
        {
            return false;
        }
    }

    internal static string CreateInventoryTransferConflictEvidenceArchivePath(
        Guid transferId,
        string sourcePath)
    {
        if (transferId == Guid.Empty)
            throw new ArgumentException("A transfer ID is required.", nameof(transferId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var extension = Path.GetExtension(sourcePath);
        if (extension.Length > 16 ||
            extension.Any(character =>
                character != '.' && !char.IsLetterOrDigit(character)))
        {
            extension = string.Empty;
        }

        return Path.Combine(
            InventoryTransferConflictEvidenceDir,
            transferId.ToString("N"),
            $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
    }

    internal static bool IsInventoryTransferConflictEvidencePath(string? path)
        => IsPathWithinDirectory(path, InventoryTransferConflictEvidenceDir);

    internal static bool IsTransactionAttachmentPath(string? path)
        => IsPathWithinDirectory(path, TransactionAttachmentsDir);

    private static bool IsPathWithinDirectory(
        string? path,
        string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var normalizedPath = TrimEndingDirectorySeparator(path);
            var normalizedRoot = TrimEndingDirectorySeparator(
                directory);
            return IsWithin(normalizedPath, normalizedRoot) &&
                   (!IsTestProcess() ||
                    (HasNoExistingReparsePointInPathChain(normalizedRoot) &&
                     HasNoExistingReparsePointInPathChain(normalizedPath)));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWithin(string candidate, string parent)
        => candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static bool HasNoExistingReparsePointInPathChain(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var relativePath = fullPath[root.Length..];
            var current = root;
            foreach (var segment in relativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current))
                    break;

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void EnsureNoExistingReparsePointInPathChain(
        string path,
        string settingName)
    {
        if (!HasNoExistingReparsePointInPathChain(path))
        {
            throw new InvalidOperationException(
                $"{settingName} cannot use a symbolic link, junction, or other reparse point.");
        }
    }

    private static void EnsureConfiguredAndTestRootsAreReparseSafe()
    {
        var isTestProcess = IsTestProcess();
        if (isTestProcess ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppRootOverrideEnvironmentKey)))
        {
            EnsureNoExistingReparsePointInPathChain(_base, AppRootOverrideEnvironmentKey);
        }

        if (isTestProcess ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)))
        {
            EnsureNoExistingReparsePointInPathChain(TempRoot, TempRootOverrideEnvironmentKey);
        }

        if (isTestProcess ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DownloadsRootOverrideEnvironmentKey)))
        {
            EnsureNoExistingReparsePointInPathChain(
                UserDownloadsDir,
                DownloadsRootOverrideEnvironmentKey);
        }
    }

    private static bool IsTruthy(string? raw)
        => string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
}
