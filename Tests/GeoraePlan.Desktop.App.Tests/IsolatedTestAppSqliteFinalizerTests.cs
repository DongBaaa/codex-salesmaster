using System.Diagnostics;
using System.Runtime.InteropServices;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedTestAppSqliteFinalizerTests
{
    [Fact]
    public async Task FinalizeAppDatabase_CheckpointsLeaseDerivedWalDatabase()
    {
        var fixture = CreateFixture();
        var sourceRoot = Path.Combine(fixture.OutputRoot, "wal-source");
        Directory.CreateDirectory(sourceRoot);
        var sourceDatabase = Path.Combine(
            sourceRoot,
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);

        try
        {
            await CreateWalSnapshotAsync(sourceDatabase, fixture.DatabasePath);
            Assert.True(File.Exists(fixture.DatabasePath + "-wal"));

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"));
            using var parentLease = OpenParentLease(fixture.LockPath);
            using var appLease =
                IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot);

            var result =
                IsolatedTestServerSqliteFinalizer.FinalizeAppDatabase(appLease);

            appLease.AssertStable();
            Assert.Equal(fixture.DatabasePath, result.DatabasePath, ignoreCase: true);
            Assert.Equal("ok", result.QuickCheck);
            Assert.Equal("delete", result.JournalMode);
            Assert.Equal(0, result.CheckpointBusy);
            Assert.Equal(result.CheckpointLogFrames, result.CheckpointedFrames);
            Assert.Equal(0, result.SidecarCount);
            Assert.Equal(64, result.DatabaseSha256.Length);
            Assert.False(File.Exists(fixture.DatabasePath + "-wal"));
            Assert.False(File.Exists(fixture.DatabasePath + "-shm"));
            Assert.False(File.Exists(fixture.DatabasePath + "-journal"));

            await using var verification = new SqliteConnection(
                $"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
            await verification.OpenAsync();
            await using var count = verification.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM SeedRows;";
            Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public async Task FinalizeAppDatabase_FailsClosedWhileExternalWriterOwnsTransaction()
    {
        var fixture = CreateFixture();

        try
        {
            File.Delete(fixture.DatabasePath);
            await using (var setup = new SqliteConnection(
                             $"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await setup.OpenAsync();
                await using var create = setup.CreateCommand();
                create.CommandText =
                    """
                    CREATE TABLE WriterRows (
                        Id INTEGER PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                    INSERT INTO WriterRows (Id, Value) VALUES (1, 'committed');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"));
            using var parentLease = OpenParentLease(fixture.LockPath);
            using var appLease =
                IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot);
            await using var writer = new SqliteConnection(
                $"Data Source={fixture.DatabasePath};Pooling=False");
            await writer.OpenAsync();
            using var writerTransaction = writer.BeginTransaction();
            await using (var insert = writer.CreateCommand())
            {
                insert.Transaction = writerTransaction;
                insert.CommandText =
                    "INSERT INTO WriterRows (Id, Value) VALUES (2, 'uncommitted');";
                await insert.ExecuteNonQueryAsync();
            }

            Assert.ThrowsAny<Exception>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeAppDatabase(
                    appLease));

            await using (var count = writer.CreateCommand())
            {
                count.Transaction = writerTransaction;
                count.CommandText = "SELECT COUNT(*) FROM WriterRows;";
                Assert.Equal(
                    2L,
                    Convert.ToInt64(await count.ExecuteScalarAsync()));
            }
            writerTransaction.Rollback();
            appLease.AssertStable();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public async Task FinalizeAppDatabase_BlocksWriterDuringFinalizationWindow()
    {
        var fixture = CreateFixture();
        var sourceRoot = Path.Combine(fixture.OutputRoot, "writer-source");
        Directory.CreateDirectory(sourceRoot);
        var sourceDatabase = Path.Combine(
            sourceRoot,
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
        var writerRejected = false;

        try
        {
            await CreateWalSnapshotAsync(sourceDatabase, fixture.DatabasePath);
            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"));
            using var parentLease = OpenParentLease(fixture.LockPath);
            using var appLease =
                IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot);

            var result =
                IsolatedTestServerSqliteFinalizer.FinalizeAppDatabase(
                    appLease,
                    new IsolatedTestServerSqliteFinalizationTestHooks(
                        BeforeResidualSidecarRemoval: () =>
                        {
                            using var writer = new SqliteConnection(
                                $"Data Source={fixture.DatabasePath};Pooling=False");
                            writer.Open();
                            using var noWait = writer.CreateCommand();
                            noWait.CommandText = "PRAGMA busy_timeout=0;";
                            noWait.ExecuteNonQuery();
                            using var insert = writer.CreateCommand();
                            insert.CommandText =
                                "INSERT INTO SeedRows (Id, Value) VALUES (3, 'blocked');";
                            var error = Assert.Throws<SqliteException>(
                                () => insert.ExecuteNonQuery());
                            Assert.Equal(5, error.SqliteErrorCode);
                            writerRejected = true;
                        }));

            Assert.True(writerRejected);
            Assert.Equal("ok", result.QuickCheck);
            Assert.Equal(0, result.SidecarCount);
            await using var verification = new SqliteConnection(
                $"Data Source={fixture.DatabasePath};Mode=ReadOnly;Pooling=False");
            await verification.OpenAsync();
            await using var count = verification.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM SeedRows;";
            Assert.Equal(2L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void FinalizeAppDatabase_RejectsMissingTestModeBeforeSqliteAccess()
    {
        var fixture = CreateFixture(databaseBytes: [9, 8, 7, 6]);

        try
        {
            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "0"));
            using var parentLease = OpenParentLease(fixture.LockPath);
            using var appLease =
                IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot);

            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeAppDatabase(
                    appLease));

            Assert.Contains(
                "GEORAEPLAN_TEST_SEED_MODE=1",
                error.Message,
                StringComparison.Ordinal);
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(fixture.DatabasePath));
            AssertNoSidecars(fixture.DatabasePath);
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AppFinalizationPipeline_RejectsMissingParentLeaseBeforeSqliteAccess()
    {
        var fixture = CreateFixture(databaseBytes: [4, 3, 2, 1]);

        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains(
                "parent preparation lease is not held",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(fixture.DatabasePath));
            AssertNoSidecars(fixture.DatabasePath);
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AppFinalizationPipeline_RejectsMissingMarkerBeforeSqliteAccess()
    {
        var fixture = CreateFixture(databaseBytes: [1, 3, 3, 7]);
        File.Delete(fixture.MarkerPath);

        try
        {
            using var parentLease = OpenParentLease(fixture.LockPath);
            Assert.ThrowsAny<Exception>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Equal(new byte[] { 1, 3, 3, 7 }, File.ReadAllBytes(fixture.DatabasePath));
            AssertNoSidecars(fixture.DatabasePath);
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AppFinalizationPipeline_RejectsCPathBeforeSqliteAccess()
    {
        var productionRoot = TestProcessIsolation.OriginalUserAppRoot;
        var productionDatabase = Path.Combine(
            productionRoot,
            "data",
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
        var originalSidecarState = GetSidecarState(productionDatabase);

        var error = Assert.Throws<InvalidOperationException>(
            () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                productionRoot,
                productionRoot));

        Assert.Contains("D: volume", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalSidecarState, GetSidecarState(productionDatabase));
    }

    [Fact]
    public void AppFinalizationPipeline_RejectsHardLinkedDatabaseBeforeSqliteAccess()
    {
        var fixture = CreateFixture(databaseBytes: [6, 5, 4, 3]);
        var externalFile = Path.Combine(fixture.OutputRoot, "external.db");
        File.WriteAllBytes(externalFile, [7, 7, 7, 7]);
        File.Delete(fixture.DatabasePath);
        CreateHardLink(fixture.DatabasePath, externalFile);

        try
        {
            using var parentLease = OpenParentLease(fixture.LockPath);
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains("hard link", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, File.ReadAllBytes(externalFile));
            AssertNoSidecars(fixture.DatabasePath);
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AppFinalizationPipeline_RejectsReparseDataBeforeSqliteAccess()
    {
        var fixture = CreateFixture();
        Directory.Delete(fixture.DataDirectory, recursive: true);
        var cTarget = Environment.GetEnvironmentVariable("SystemRoot")
            ?? @"C:\Windows";
        CreateDirectoryJunction(fixture.DataDirectory, cTarget);
        var cSentinel = Path.Combine(cTarget, "win.ini");
        var originalSidecarState = GetSidecarState(cSentinel);

        try
        {
            using var parentLease = OpenParentLease(fixture.LockPath);
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains(
                "reparse point",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(originalSidecarState, GetSidecarState(cSentinel));
        }
        finally
        {
            RemoveDirectoryJunction(fixture.DataDirectory);
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void SyncDiagAppFinalizationCommand_AcceptsNoDatabasePath()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs");
        var source = File.ReadAllText(programPath);
        var commandStart = source.IndexOf(
            "\"finalize-test-app-sqlite\"",
            StringComparison.Ordinal);
        var finalizerCall = source.IndexOf(
            "FinalizeAppDatabase(",
            commandStart,
            StringComparison.Ordinal);

        Assert.True(commandStart >= 0);
        Assert.True(finalizerCall > commandStart);
        Assert.Contains("if (args.Length != 1)", source[commandStart..finalizerCall]);
        Assert.Contains(
            "FinalizeAppDatabase(\n                appPreparationLease)",
            source[finalizerCall..],
            StringComparison.Ordinal);
    }

    private static AppFixture CreateFixture(byte[]? databaseBytes = null)
    {
        var outputRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"app-sqlite-finalizer-{Guid.NewGuid():N}");
        Assert.Equal(
            @"D:\",
            Path.GetPathRoot(Path.GetFullPath(outputRoot)),
            ignoreCase: true);
        var appRoot = Path.Combine(outputRoot, "AppData");
        var dataDirectory = Path.Combine(appRoot, "data");
        var databasePath = Path.Combine(
            dataDirectory,
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
        var markerPath = Path.Combine(
            appRoot,
            IsolatedPreparationDatabaseLease.IsolatedSeedMarkerFileName);
        var lockPath = Path.Combine(
            outputRoot,
            IsolatedPreparationDatabaseLease.PreparationLockFileName);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllBytes(databasePath, databaseBytes ?? [0]);
        File.WriteAllText(markerPath, appRoot);
        File.WriteAllText(lockPath, string.Empty);
        return new AppFixture(
            outputRoot,
            appRoot,
            dataDirectory,
            databasePath,
            markerPath,
            lockPath);
    }

    private static FileStream OpenParentLease(string lockPath)
        => File.Open(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);

    private static async Task CreateWalSnapshotAsync(
        string sourceDatabasePath,
        string targetDatabasePath)
    {
        await using var sourceConnection = new SqliteConnection(
            $"Data Source={sourceDatabasePath};Pooling=False");
        await sourceConnection.OpenAsync();
        await using (var journalMode = sourceConnection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal(
                "wal",
                Convert.ToString(await journalMode.ExecuteScalarAsync())?
                    .ToLowerInvariant());
        }

        await using (var disableAutoCheckpoint = sourceConnection.CreateCommand())
        {
            disableAutoCheckpoint.CommandText = "PRAGMA wal_autocheckpoint=0;";
            await disableAutoCheckpoint.ExecuteNonQueryAsync();
        }

        await using (var seed = sourceConnection.CreateCommand())
        {
            seed.CommandText =
                """
                CREATE TABLE SeedRows (
                    Id INTEGER PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                INSERT INTO SeedRows (Id, Value) VALUES (1, 'first');
                INSERT INTO SeedRows (Id, Value) VALUES (2, 'second');
                """;
            await seed.ExecuteNonQueryAsync();
        }

        File.Copy(sourceDatabasePath, targetDatabasePath, overwrite: true);
        File.Copy(
            sourceDatabasePath + "-wal",
            targetDatabasePath + "-wal",
            overwrite: true);
        if (File.Exists(sourceDatabasePath + "-shm"))
        {
            File.Copy(
                sourceDatabasePath + "-shm",
                targetDatabasePath + "-shm",
                overwrite: true);
        }
    }

    private static void AssertNoSidecars(string databasePath)
    {
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.False(File.Exists(databasePath + "-journal"));
    }

    private static bool[] GetSidecarState(string databasePath)
        =>
        [
            File.Exists(databasePath + "-wal"),
            File.Exists(databasePath + "-shm"),
            File.Exists(databasePath + "-journal")
        ];

    private static void CreateHardLink(string linkPath, string existingFilePath)
    {
        Assert.True(
            CreateHardLinkW(linkPath, existingFilePath, IntPtr.Zero),
            $"CreateHardLinkW failed. Win32Error={Marshal.GetLastWin32Error()}");
    }

    private static void CreateDirectoryJunction(
        string junctionPath,
        string targetPath)
    {
        var commandPath = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void RemoveDirectoryJunction(string junctionPath)
    {
        if (!Directory.Exists(junctionPath))
            return;

        Directory.Delete(junctionPath);
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            ".."));

    private static void DeleteFixture(string outputRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(outputRoot))
            Directory.Delete(outputRoot, recursive: true);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly IReadOnlyList<(string Name, string? Value)> _original;

        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            _original = values
                .Select(value =>
                    (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _original)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed record AppFixture(
        string OutputRoot,
        string AppRoot,
        string DataDirectory,
        string DatabasePath,
        string MarkerPath,
        string LockPath);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
