using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace GeoraePlan.Tools.SyncDiag;

public sealed record IsolatedTestServerSqliteFinalizationResult(
    string DatabasePath,
    long DatabaseLength,
    string DatabaseSha256,
    int CheckpointBusy,
    int CheckpointLogFrames,
    int CheckpointedFrames,
    string JournalMode,
    string QuickCheck,
    int SidecarCount);

internal sealed record IsolatedTestServerSqliteFinalizationTestHooks(
    Action? AfterTargetValidated = null,
    Action? BeforeResidualSidecarRemoval = null);

public static class IsolatedTestServerSqliteFinalizer
{
    public const string DatabaseFileName = "\uac70\ub798\ud50c\ub79c-local.db";
    public const string ServerRootMarkerFileName = ".georaeplan-isolated-server-root";

    public static IsolatedTestServerSqliteFinalizationResult FinalizeDatabase(
        string databasePath)
        => FinalizeDatabase(databasePath, testHooks: null);

#if !GEORAEPLAN_SERVER
    public static IsolatedTestServerSqliteFinalizationResult FinalizeAppDatabase(
        IsolatedPreparationDatabaseLease preparationLease)
        => FinalizeAppDatabase(preparationLease, testHooks: null);

    internal static IsolatedTestServerSqliteFinalizationResult FinalizeAppDatabase(
        IsolatedPreparationDatabaseLease preparationLease,
        IsolatedTestServerSqliteFinalizationTestHooks? testHooks)
    {
        ArgumentNullException.ThrowIfNull(preparationLease);
        AssertTestFinalizationEnvironment("App SQLite finalization");
        preparationLease.AssertStable();

        var appRoot = NormalizeDirectoryPath(preparationLease.GuardedRoot);
        var databasePath = preparationLease.DatabasePath
            ?? throw new InvalidOperationException(
                "The isolated AppData preparation lease did not provide a database path.");
        var expectedDatabasePath = Path.GetFullPath(
            Path.Combine(
                appRoot,
                "data",
                IsolatedPreparationDatabaseLease.LocalDatabaseFileName));
        if (!string.Equals(
                Path.GetFullPath(databasePath),
                expectedDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "App SQLite finalization only accepts the database derived from the isolated AppData preparation lease.");
        }

        ValidateAppTargetFiles(appRoot, expectedDatabasePath);
        var target = new ValidatedTarget(appRoot, expectedDatabasePath);
        var result = FinalizeValidatedDatabase(
            target,
            () =>
            {
                preparationLease.AssertStable();
                AssertMarkerMatchesAppRoot(appRoot);
            },
            requireExclusiveFileProbe: false,
            testHooks);
        preparationLease.AssertStable();
        return result;
    }
#endif

    internal static IsolatedTestServerSqliteFinalizationResult FinalizeDatabase(
        string databasePath,
        IsolatedTestServerSqliteFinalizationTestHooks? testHooks)
    {
        var target = ValidateTarget(databasePath);
        return FinalizeValidatedDatabase(
            target,
            () => AssertMarkerMatchesServerRoot(target),
            requireExclusiveFileProbe: true,
            testHooks);
    }

    private static IsolatedTestServerSqliteFinalizationResult
        FinalizeValidatedDatabase(
            ValidatedTarget target,
            Action assertMarkerMatchesRoot,
            bool requireExclusiveFileProbe,
            IsolatedTestServerSqliteFinalizationTestHooks? testHooks)
    {
        testHooks?.AfterTargetValidated?.Invoke();

        using var serverRootLease =
            NativePathLease.OpenServerRoot(target.ServerRoot);
        AssertServerRootLease(target, serverRootLease);
        assertMarkerMatchesRoot();

        if (requireExclusiveFileProbe)
            AssertDatabaseIsNotInUse(target.DatabasePath);
        using var databaseLease =
            NativePathLease.OpenDatabase(target.DatabasePath);
        AssertDatabaseLease(target, databaseLease);

        using var writerExclusion = CheckpointAndDisableWal(
            target.DatabasePath,
            databaseLease.Identity);
        SqliteConnection.ClearAllPools();

        testHooks?.BeforeResidualSidecarRemoval?.Invoke();
        RemoveResidualSidecars(target.DatabasePath);
        var verification = VerifyStandaloneSnapshot(
            target,
            serverRootLease,
            databaseLease);
        AssertServerRootLease(target, serverRootLease);
        assertMarkerMatchesRoot();
        AssertDatabaseLease(target, databaseLease);
        var sidecarCount = GetExistingSidecarPaths(target.DatabasePath).Count;
        if (sidecarCount != 0)
        {
            throw new InvalidOperationException(
                "A SQLite sidecar appeared after standalone snapshot verification.");
        }

        return new IsolatedTestServerSqliteFinalizationResult(
            target.DatabasePath,
            verification.DatabaseLength,
            verification.DatabaseSha256,
            writerExclusion.Result.Busy,
            writerExclusion.Result.LogFrames,
            writerExclusion.Result.CheckpointedFrames,
            writerExclusion.Result.JournalMode,
            verification.QuickCheck,
            sidecarCount);
    }

#if !GEORAEPLAN_SERVER
    private static void ValidateAppTargetFiles(
        string appRoot,
        string databasePath)
    {
        if (!Directory.Exists(appRoot))
        {
            throw new InvalidOperationException(
                $"The isolated AppData root does not exist: {appRoot}");
        }

        AssertNoReparsePointAncestors(appRoot);
        AssertDifferentVolumeFromNormalApplicationData(appRoot);
        AssertMarkerMatchesAppRoot(appRoot);

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "The isolated AppData SQLite database was not found.",
                databasePath);
        }

        AssertNotReparsePoint(
            databasePath,
            "The isolated AppData database");
        foreach (var sidecarPath in GetSidecarPaths(databasePath))
        {
            if (Directory.Exists(sidecarPath))
            {
                throw new InvalidOperationException(
                    $"A SQLite sidecar path is a directory: {sidecarPath}");
            }

            if (File.Exists(sidecarPath))
                AssertNotReparsePoint(sidecarPath, "A SQLite sidecar");
        }
    }

    private static void AssertMarkerMatchesAppRoot(string appRoot)
    {
        var markerPath = Path.Combine(
            appRoot,
            IsolatedPreparationDatabaseLease.IsolatedSeedMarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"The isolated AppData marker is missing: {markerPath}");
        }

        AssertNotReparsePoint(markerPath, "The isolated AppData marker");
        var markerRoot = NormalizeDirectoryPath(
            File.ReadAllText(markerPath).Trim());
        if (!string.Equals(
                markerRoot,
                appRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated AppData marker does not match the guarded AppData root.");
        }
    }
#endif

    private static ValidatedTarget ValidateTarget(string databasePath)
    {
        AssertTestFinalizationEnvironment("Server SQLite finalization");

        var serverRootValue =
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SERVER_ROOT");
        if (string.IsNullOrWhiteSpace(serverRootValue))
        {
            throw new InvalidOperationException(
                "Server SQLite finalization requires an explicit GEORAEPLAN_TEST_SERVER_ROOT.");
        }

        var serverRoot = NormalizeDirectoryPath(serverRootValue);
        if (!Directory.Exists(serverRoot))
        {
            throw new InvalidOperationException(
                $"The isolated test server root does not exist: {serverRoot}");
        }

        var volumeRoot = NormalizeDirectoryPath(
            Path.GetPathRoot(serverRoot)
            ?? throw new InvalidOperationException(
                "The isolated test server root must be an absolute path."));
        if (string.Equals(serverRoot, volumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated test server root must be below the volume root.");
        }

        AssertNoReparsePointAncestors(serverRoot);
        AssertDifferentVolumeFromNormalApplicationData(serverRoot);

        var markerPath = Path.Combine(serverRoot, ServerRootMarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"The isolated test server marker is missing: {markerPath}");
        }

        AssertNotReparsePoint(markerPath, "The isolated test server marker");
        var markerRoot = NormalizeDirectoryPath(File.ReadAllText(markerPath).Trim());
        if (!string.Equals(markerRoot, serverRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated test server marker does not match GEORAEPLAN_TEST_SERVER_ROOT.");
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException(
                "A server SQLite database path is required.",
                nameof(databasePath));
        }

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var expectedDatabasePath = Path.GetFullPath(
            Path.Combine(serverRoot, DatabaseFileName));
        if (!string.Equals(
                fullDatabasePath,
                expectedDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Server SQLite finalization only accepts the canonical database directly inside the marked isolated server root.");
        }

        if (!File.Exists(fullDatabasePath))
        {
            throw new FileNotFoundException(
                "The isolated test server SQLite database was not found.",
                fullDatabasePath);
        }

        AssertNotReparsePoint(fullDatabasePath, "The isolated test server database");
        foreach (var sidecarPath in GetSidecarPaths(fullDatabasePath))
        {
            if (Directory.Exists(sidecarPath))
            {
                throw new InvalidOperationException(
                    $"A SQLite sidecar path is a directory: {sidecarPath}");
            }

            if (File.Exists(sidecarPath))
                AssertNotReparsePoint(sidecarPath, "A SQLite sidecar");
        }

        return new ValidatedTarget(serverRoot, fullDatabasePath);
    }

    private static void AssertTestFinalizationEnvironment(string operation)
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_MODE")) ||
            !IsTruthy(Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_MODE")))
        {
            throw new InvalidOperationException(
                $"{operation} requires GEORAEPLAN_TEST_MODE=1 and GEORAEPLAN_TEST_SEED_MODE=1.");
        }
    }

    private static void AssertServerRootLease(
        ValidatedTarget target,
        NativePathLease serverRootLease)
    {
        serverRootLease.AssertIdentityUnchanged();
        if (!serverRootLease.IsDirectory ||
            serverRootLease.IsReparsePoint ||
            !string.Equals(
                NormalizeDirectoryPath(serverRootLease.FinalPath),
                target.ServerRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated server root handle does not match the validated non-reparse directory.");
        }
    }

    private static void AssertMarkerMatchesServerRoot(ValidatedTarget target)
    {
        var markerPath = Path.Combine(
            target.ServerRoot,
            ServerRootMarkerFileName);
        if (!File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                $"The isolated test server marker disappeared: {markerPath}");
        }

        AssertNotReparsePoint(markerPath, "The isolated test server marker");
        var markerRoot = NormalizeDirectoryPath(
            File.ReadAllText(markerPath).Trim());
        if (!string.Equals(
                markerRoot,
                target.ServerRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated test server marker changed after validation.");
        }
    }

    private static void AssertDatabaseLease(
        ValidatedTarget target,
        NativePathLease databaseLease)
    {
        databaseLease.AssertIdentityUnchanged();
        if (databaseLease.IsDirectory ||
            databaseLease.IsReparsePoint ||
            databaseLease.NumberOfLinks != 1 ||
            !string.Equals(
                Path.GetFullPath(databaseLease.FinalPath),
                target.DatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated server database handle is not the canonical single-link database file.");
        }
    }

    private static void AssertSqliteMainDatabaseIdentity(
        SqliteConnection connection,
        string expectedDatabasePath,
        NativeFileIdentity expectedIdentity)
    {
        string? sqliteMainPath = null;
        using (var databaseList = connection.CreateCommand())
        {
            databaseList.CommandText = "PRAGMA database_list;";
            using var reader = databaseList.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(
                        reader.GetString(1),
                        "main",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                sqliteMainPath = reader.GetString(2);
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(sqliteMainPath))
        {
            throw new InvalidOperationException(
                "SQLite did not report the main database path.");
        }

        var fullSqliteMainPath = Path.GetFullPath(sqliteMainPath);
        if (!string.Equals(
                fullSqliteMainPath,
                expectedDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SQLite opened a main database path different from the guarded database.");
        }

        using var sqliteIdentityLease =
            NativePathLease.OpenIdentity(fullSqliteMainPath);
        sqliteIdentityLease.AssertIdentityUnchanged();
        if (sqliteIdentityLease.IsDirectory ||
            sqliteIdentityLease.IsReparsePoint ||
            sqliteIdentityLease.NumberOfLinks != 1 ||
            sqliteIdentityLease.Identity != expectedIdentity ||
            !string.Equals(
                Path.GetFullPath(sqliteIdentityLease.FinalPath),
                expectedDatabasePath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SQLite main database identity does not match the guarded database handle.");
        }
    }

    private static void DeleteSidecarByIdentity(string sidecarPath)
    {
        NativePathLease? sidecarLease = null;
        try
        {
            sidecarLease = NativePathLease.OpenSidecarForDeletion(sidecarPath);
            sidecarLease.AssertIdentityUnchanged();
            if (sidecarLease.IsDirectory ||
                sidecarLease.IsReparsePoint ||
                sidecarLease.NumberOfLinks != 1 ||
                !string.Equals(
                    Path.GetFullPath(sidecarLease.FinalPath),
                    Path.GetFullPath(sidecarPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite sidecar identity is unsafe for deletion: {sidecarPath}");
            }

            sidecarLease.MarkForDeletion();
        }
        finally
        {
            sidecarLease?.Dispose();
        }

        if (File.Exists(sidecarPath) || Directory.Exists(sidecarPath))
        {
            throw new InvalidOperationException(
                $"A SQLite sidecar was replaced while its original identity was being deleted: {sidecarPath}");
        }
    }

    private static DatabaseWriterExclusionLease CheckpointAndDisableWal(
        string databasePath,
        NativeFileIdentity expectedIdentity)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            AssertSqliteMainDatabaseIdentity(
                connection,
                databasePath,
                expectedIdentity);

            using (var busyTimeout = connection.CreateCommand())
            {
                busyTimeout.CommandText = "PRAGMA busy_timeout=5000;";
                busyTimeout.ExecuteNonQuery();
            }

            int busy;
            int logFrames;
            int checkpointedFrames;
            using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var reader = checkpoint.ExecuteReader();
                if (!reader.Read() || reader.FieldCount < 3)
                {
                    throw new InvalidOperationException(
                        "SQLite did not return a WAL checkpoint result.");
                }

                busy = reader.GetInt32(0);
                logFrames = reader.GetInt32(1);
                checkpointedFrames = reader.GetInt32(2);
            }

            if (busy != 0 || logFrames != checkpointedFrames)
            {
                throw new InvalidOperationException(
                    $"The isolated server WAL checkpoint was incomplete: busy={busy}, log_frames={logFrames}, checkpointed_frames={checkpointedFrames}.");
            }

            var quickCheck = ReadQuickCheck(connection);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The isolated server database failed quick_check before WAL removal: {quickCheck}");
            }

            string journalMode;
            using (var journalModeCommand = connection.CreateCommand())
            {
                journalModeCommand.CommandText = "PRAGMA journal_mode=DELETE;";
                journalMode = Convert.ToString(
                        journalModeCommand.ExecuteScalar(),
                        System.Globalization.CultureInfo.InvariantCulture)
                    ?.Trim()
                    ?? string.Empty;
            }

            if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite did not leave WAL mode: journal_mode={journalMode}");
            }

            quickCheck = ReadQuickCheck(connection);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The isolated server database failed quick_check after WAL removal: {quickCheck}");
            }

            AssertSqliteMainDatabaseIdentity(
                connection,
                databasePath,
                expectedIdentity);

            using (var writerExclusion = connection.CreateCommand())
            {
                writerExclusion.CommandText = "BEGIN EXCLUSIVE;";
                writerExclusion.ExecuteNonQuery();
            }

            return new DatabaseWriterExclusionLease(
                connection,
                new CheckpointResult(
                    busy,
                    logFrames,
                    checkpointedFrames,
                    journalMode.ToLowerInvariant()));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SnapshotVerification VerifyStandaloneSnapshot(
        ValidatedTarget target,
        NativePathLease serverRootLease,
        NativePathLease databaseLease)
    {
        AssertServerRootLease(target, serverRootLease);
        AssertDatabaseLease(target, databaseLease);

        var databasePath = target.DatabasePath;
        var sidecarPaths = GetSidecarPaths(databasePath);
        if (sidecarPaths.Any(path => File.Exists(path) || Directory.Exists(path)))
        {
            throw new InvalidOperationException(
                "The finalized server database still has a WAL/SHM/journal sidecar.");
        }

        var databaseLength = databaseLease.GetLength();
        var lastWriteUtc = File.GetLastWriteTimeUtc(databasePath);

        var immutableDataSource = new Uri(databasePath).AbsoluteUri + "?immutable=1";
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = immutableDataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        string quickCheck;
        string sha256;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            AssertSqliteMainDatabaseIdentity(
                connection,
                databasePath,
                databaseLease.Identity);
            using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only=ON;";
                queryOnly.ExecuteNonQuery();
            }

            quickCheck = ReadQuickCheck(connection);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The standalone server database failed immutable quick_check: {quickCheck}");
            }

            if (sidecarPaths.Any(path => File.Exists(path) || Directory.Exists(path)))
            {
                throw new InvalidOperationException(
                    "Immutable verification created or detected a SQLite sidecar.");
            }

            if (databaseLease.GetLength() != databaseLength ||
                File.GetLastWriteTimeUtc(databasePath) != lastWriteUtc)
            {
                throw new InvalidOperationException(
                    "The finalized server database changed during immutable verification.");
            }

            AssertServerRootLease(target, serverRootLease);
            AssertDatabaseLease(target, databaseLease);
            sha256 = databaseLease.ComputeSha256();
            if (sidecarPaths.Any(path => File.Exists(path) || Directory.Exists(path)))
            {
                throw new InvalidOperationException(
                    "A SQLite sidecar appeared while hashing the standalone snapshot.");
            }

        }

        AssertServerRootLease(target, serverRootLease);
        AssertDatabaseLease(target, databaseLease);
        if (databaseLease.GetLength() != databaseLength ||
            File.GetLastWriteTimeUtc(databasePath) != lastWriteUtc ||
            sidecarPaths.Any(path => File.Exists(path) || Directory.Exists(path)))
        {
            throw new InvalidOperationException(
                "The standalone server snapshot changed after immutable verification.");
        }

        return new SnapshotVerification(
            databaseLength,
            sha256,
            quickCheck.ToLowerInvariant());
    }

    private static void RemoveResidualSidecars(string databasePath)
    {
        foreach (var sidecarPath in GetSidecarPaths(databasePath))
        {
            if (Directory.Exists(sidecarPath))
            {
                throw new InvalidOperationException(
                    $"A SQLite sidecar path is a directory: {sidecarPath}");
            }

            if (!File.Exists(sidecarPath))
                continue;

            DeleteSidecarByIdentity(sidecarPath);
        }
    }

    private static void AssertDatabaseIsNotInUse(string databasePath)
    {
        try
        {
            using var databaseProbe =
                NativePathLease.OpenExclusiveProbe(databasePath);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InvalidOperationException(
                "The isolated test server database is still in use. Stop the server before finalizing SQLite.",
                ex);
        }
    }

    private static string ReadQuickCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        using var reader = command.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));

        return results.Count == 1 ? results[0].Trim() : string.Join(" | ", results);
    }

    private static IReadOnlyList<string> GetSidecarPaths(string databasePath)
        =>
        [
            databasePath + "-wal",
            databasePath + "-shm",
            databasePath + "-journal"
        ];

    private static IReadOnlyList<string> GetExistingSidecarPaths(string databasePath)
        => GetSidecarPaths(databasePath)
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();

    private static void AssertDifferentVolumeFromNormalApplicationData(
        string serverRoot)
    {
        var localAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The normal local application data root could not be resolved.");
        }

        var productionRoot = Path.GetFullPath(
            Path.Combine(localAppData, "\uac70\ub798\ud50c\ub79c"));
        var serverVolume = Path.GetPathRoot(serverRoot);
        var productionVolume = Path.GetPathRoot(productionRoot);
        if (string.IsNullOrWhiteSpace(serverVolume) ||
            string.IsNullOrWhiteSpace(productionVolume) ||
            string.Equals(
                serverVolume,
                productionVolume,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The isolated test server root must be on a different volume from normal V1 application data.");
        }
    }

    private static void AssertNoReparsePointAncestors(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (!current.Exists)
            {
                throw new InvalidOperationException(
                    $"The isolated test server path does not exist: {current.FullName}");
            }

            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Server SQLite finalization rejects reparse-point paths: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static void AssertNotReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} cannot be a reparse point: {path}");
        }
    }

    private static string NormalizeDirectoryPath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private readonly record struct NativeFileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex);

    private sealed class NativePathLease : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly string _openedPath;
        private bool _disposed;

        private NativePathLease(
            SafeFileHandle handle,
            string openedPath,
            NativeMethods.ByHandleFileInformation information,
            string finalPath)
        {
            _handle = handle;
            _openedPath = openedPath;
            Identity = ToIdentity(information);
            Attributes = (FileAttributes)information.FileAttributes;
            NumberOfLinks = information.NumberOfLinks;
            FinalPath = finalPath;
        }

        public NativeFileIdentity Identity { get; }

        public FileAttributes Attributes { get; }

        public uint NumberOfLinks { get; }

        public string FinalPath { get; }

        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

        public bool IsReparsePoint =>
            (Attributes & FileAttributes.ReparsePoint) != 0;

        public static NativePathLease OpenServerRoot(string path)
            => Open(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                NativeMethods.FileFlagBackupSemantics |
                NativeMethods.FileFlagOpenReparsePoint);

        public static NativePathLease OpenDatabase(string path)
            => Open(
                path,
                NativeMethods.GenericRead,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                NativeMethods.FileFlagOpenReparsePoint);

        public static NativePathLease OpenIdentity(string path)
            => Open(
                path,
                NativeMethods.FileReadAttributes,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                NativeMethods.FileFlagOpenReparsePoint);

        public static NativePathLease OpenExclusiveProbe(string path)
            => Open(
                path,
                NativeMethods.GenericRead,
                shareMode: (uint)FileShare.None,
                flagsAndAttributes: NativeMethods.FileFlagOpenReparsePoint);

        public static NativePathLease OpenSidecarForDeletion(string path)
            => Open(
                path,
                NativeMethods.FileReadAttributes | NativeMethods.DeleteAccess,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                NativeMethods.FileFlagOpenReparsePoint);

        public long GetLength()
        {
            ThrowIfDisposed();
            return RandomAccess.GetLength(_handle);
        }

        public string ComputeSha256()
        {
            ThrowIfDisposed();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long offset = 0;
            while (true)
            {
                var read = RandomAccess.Read(_handle, buffer, offset);
                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public void AssertIdentityUnchanged()
        {
            ThrowIfDisposed();
            var currentInformation = ReadInformation(_handle, _openedPath);
            var currentFinalPath = ReadFinalPath(_handle, _openedPath);
            if (ToIdentity(currentInformation) != Identity ||
                currentInformation.NumberOfLinks != NumberOfLinks ||
                (FileAttributes)currentInformation.FileAttributes != Attributes ||
                !string.Equals(
                    currentFinalPath,
                    FinalPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A guarded filesystem identity changed during SQLite finalization: {_openedPath}");
            }
        }

        public void MarkForDeletion()
        {
            ThrowIfDisposed();
            var disposition = new NativeMethods.FileDispositionInformation
            {
                DeleteFile = true
            };
            if (!NativeMethods.SetFileInformationByHandle(
                    _handle,
                    NativeMethods.FileInfoByHandleClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<NativeMethods.FileDispositionInformation>()))
            {
                throw CreateWin32Exception(
                    $"Could not mark the guarded SQLite sidecar for deletion: {_openedPath}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _handle.Dispose();
        }

        private static NativePathLease Open(
            string path,
            uint desiredAccess,
            uint shareMode,
            uint flagsAndAttributes)
        {
            var fullPath = Path.GetFullPath(path);
            var handle = NativeMethods.CreateFileW(
                fullPath,
                desiredAccess,
                shareMode,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                flagsAndAttributes,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw CreateWin32Exception(
                    $"Could not acquire a guarded filesystem handle: {fullPath}");
            }

            try
            {
                var information = ReadInformation(handle, fullPath);
                var finalPath = ReadFinalPath(handle, fullPath);
                return new NativePathLease(
                    handle,
                    fullPath,
                    information,
                    finalPath);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static NativeMethods.ByHandleFileInformation ReadInformation(
            SafeFileHandle handle,
            string path)
        {
            if (!NativeMethods.GetFileInformationByHandle(
                    handle,
                    out var information))
            {
                throw CreateWin32Exception(
                    $"Could not read guarded filesystem identity: {path}");
            }

            return information;
        }

        private static string ReadFinalPath(
            SafeFileHandle handle,
            string path)
        {
            var capacity = 512;
            while (true)
            {
                var buffer = new StringBuilder(capacity);
                var length = NativeMethods.GetFinalPathNameByHandleW(
                    handle,
                    buffer,
                    (uint)buffer.Capacity,
                    flags: 0);
                if (length == 0)
                {
                    throw CreateWin32Exception(
                        $"Could not resolve guarded filesystem path: {path}");
                }

                if (length < buffer.Capacity)
                    return NormalizeNativeFinalPath(buffer.ToString());

                capacity = checked((int)length + 1);
            }
        }

        private static NativeFileIdentity ToIdentity(
            NativeMethods.ByHandleFileInformation information)
            => new(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) |
                information.FileIndexLow);

        private static string NormalizeNativeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string devicePrefix = @"\\?\";
            var normalized = path.StartsWith(
                    uncPrefix,
                    StringComparison.OrdinalIgnoreCase)
                ? @"\\" + path[uncPrefix.Length..]
                : path.StartsWith(
                    devicePrefix,
                    StringComparison.OrdinalIgnoreCase)
                    ? path[devicePrefix.Length..]
                    : path;
            return Path.GetFullPath(normalized);
        }

        private static Win32Exception CreateWin32Exception(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, $"{message} Win32Error={error}.");
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint DeleteAccess = 0x00010000;
        public const uint FileReadAttributes = 0x00000080;
        public const uint FileShareRead = 0x00000001;
        public const uint FileShareWrite = 0x00000002;
        public const uint OpenExisting = 3;
        public const uint FileFlagOpenReparsePoint = 0x00200000;
        public const uint FileFlagBackupSemantics = 0x02000000;

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        public static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetFileInformationByHandle(
            SafeFileHandle file,
            FileInfoByHandleClass fileInformationClass,
            ref FileDispositionInformation fileInformation,
            uint bufferSize);

        public enum FileInfoByHandleClass
        {
            FileDispositionInfo = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public FileTime CreationTime;
            public FileTime LastAccessTime;
            public FileTime LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FileDispositionInformation
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool DeleteFile;
        }
    }

    private sealed record ValidatedTarget(
        string ServerRoot,
        string DatabasePath);

    private sealed class DatabaseWriterExclusionLease : IDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _disposed;

        public DatabaseWriterExclusionLease(
            SqliteConnection connection,
            CheckpointResult result)
        {
            _connection = connection;
            Result = result;
        }

        public CheckpointResult Result { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                using var rollback = _connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                rollback.ExecuteNonQuery();
            }
            finally
            {
                _connection.Dispose();
            }
        }
    }

    private sealed record CheckpointResult(
        int Busy,
        int LogFrames,
        int CheckpointedFrames,
        string JournalMode);

    private sealed record SnapshotVerification(
        long DatabaseLength,
        string DatabaseSha256,
        string QuickCheck);
}
