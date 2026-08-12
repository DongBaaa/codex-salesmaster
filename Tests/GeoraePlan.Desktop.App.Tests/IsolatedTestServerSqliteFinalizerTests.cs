using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedTestServerSqliteFinalizerTests
{
    [Fact]
    public async Task FinalizeDatabase_CheckpointsWalAndProducesPortableDatabase()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        var sourceRoot = Path.Combine(testRoot, "wal-source");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(sourceRoot);

        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        var sourceDatabasePath = Path.Combine(
            sourceRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);

        try
        {
            await CreateWalSnapshotAsync(sourceDatabasePath, databasePath);
            Assert.True(File.Exists(databasePath + "-wal"));
            Assert.True(new FileInfo(databasePath + "-wal").Length > 0);

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));

            var result =
                IsolatedTestServerSqliteFinalizer.FinalizeDatabase(databasePath);

            Assert.Equal("ok", result.QuickCheck);
            Assert.Equal("delete", result.JournalMode);
            Assert.Equal(0, result.CheckpointBusy);
            Assert.Equal(
                result.CheckpointLogFrames,
                result.CheckpointedFrames);
            Assert.Equal(0, result.SidecarCount);
            Assert.True(result.DatabaseLength > 0);
            Assert.Equal(64, result.DatabaseSha256.Length);
            Assert.False(File.Exists(databasePath + "-wal"));
            Assert.False(File.Exists(databasePath + "-shm"));
            Assert.False(File.Exists(databasePath + "-journal"));

            await using var verificationConnection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verificationConnection.OpenAsync();
            await using var rowCount = verificationConnection.CreateCommand();
            rowCount.CommandText = "SELECT COUNT(*) FROM SeedRows;";
            Assert.Equal(2L, Convert.ToInt64(await rowCount.ExecuteScalarAsync()));

            await using var journalMode = verificationConnection.CreateCommand();
            journalMode.CommandText = "PRAGMA journal_mode;";
            Assert.Equal(
                "delete",
                Convert.ToString(await journalMode.ExecuteScalarAsync())?
                    .ToLowerInvariant());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_RejectsDatabaseOutsideMarkedServerRoot()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        var outsideRoot = Path.Combine(testRoot, "outside");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(outsideRoot);

        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);
        var outsideDatabase = Path.Combine(
            outsideRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);

        try
        {
            await using (var connection = new SqliteConnection(
                             $"Data Source={outsideDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE GuardProbe (Id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));

            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    outsideDatabase));
            Assert.Contains(
                "canonical database directly inside",
                error.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_RejectsDatabaseThatIsStillOpen()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);
        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);

        try
        {
            await using var openConnection = new SqliteConnection(
                $"Data Source={databasePath};Pooling=False");
            await openConnection.OpenAsync();
            await using (var command = openConnection.CreateCommand())
            {
                command.CommandText =
                    "CREATE TABLE RunningServerProbe (Id INTEGER PRIMARY KEY);";
                await command.ExecuteNonQueryAsync();
            }

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));

            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    databasePath));
            Assert.Contains(
                "still in use",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_RejectsDatabaseHardLinkReplacementAfterValidation()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        var externalRoot = Path.Combine(testRoot, "external");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);

        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        var displacedDatabasePath = databasePath + ".validated-original";
        var externalDatabasePath = Path.Combine(
            externalRoot,
            "external.db");

        try
        {
            await CreateSimpleDatabaseAsync(databasePath, "validated-target");
            await CreateSimpleDatabaseAsync(externalDatabasePath, "external-must-survive");
            var externalHashBefore = ComputeSha256(externalDatabasePath);

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));
            var hooks = new IsolatedTestServerSqliteFinalizationTestHooks(
                AfterTargetValidated: () =>
                {
                    File.Move(databasePath, displacedDatabasePath);
                    CreateHardLink(databasePath, externalDatabasePath);
                });

            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    databasePath,
                    hooks));

            Assert.Contains(
                "single-link database",
                error.Message,
                StringComparison.Ordinal);
            Assert.True(File.Exists(externalDatabasePath));
            Assert.Equal(
                externalHashBefore,
                ComputeSha256(externalDatabasePath));
            Assert.Equal(
                "external-must-survive",
                await ReadStateValueAsync(externalDatabasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_RejectsSidecarHardLinkReplacementWithoutDeletingExternalFile()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        var externalRoot = Path.Combine(testRoot, "external");
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);

        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        var sidecarPath = databasePath + "-journal";
        var externalFilePath = Path.Combine(
            externalRoot,
            "external-sidecar-source.bin");

        try
        {
            await CreateSimpleDatabaseAsync(databasePath, "portable");
            await File.WriteAllBytesAsync(
                externalFilePath,
                Enumerable.Range(0, 4096)
                    .Select(index => (byte)(index % 251))
                    .ToArray());
            var externalHashBefore = ComputeSha256(externalFilePath);

            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));
            var hooks = new IsolatedTestServerSqliteFinalizationTestHooks(
                BeforeResidualSidecarRemoval: () =>
                    CreateHardLink(sidecarPath, externalFilePath));

            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    databasePath,
                    hooks));

            Assert.Contains(
                "sidecar identity is unsafe",
                error.Message,
                StringComparison.Ordinal);
            Assert.True(File.Exists(externalFilePath));
            Assert.Equal(
                externalHashBefore,
                ComputeSha256(externalFilePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_RemovesSingleLinkResidualSidecarByHandle()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);

        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        var sidecarPath = databasePath + "-journal";

        try
        {
            await CreateSimpleDatabaseAsync(databasePath, "portable");
            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));
            var hooks = new IsolatedTestServerSqliteFinalizationTestHooks(
                BeforeResidualSidecarRemoval: () =>
                    File.WriteAllBytes(
                        sidecarPath,
                        Enumerable.Range(0, 1024)
                            .Select(index => (byte)(index % 239))
                            .ToArray()));

            var result =
                IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    databasePath,
                    hooks);

            Assert.Equal(0, result.SidecarCount);
            Assert.False(File.Exists(sidecarPath));
            Assert.Equal(
                "portable",
                await ReadStateValueAsync(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FinalizeDatabase_HoldsRootAndDatabaseAgainstRenameUntilVerificationEnds()
    {
        var testRoot = CreateTestRoot();
        var serverRoot = Path.Combine(testRoot, "server");
        var movedServerRoot = Path.Combine(testRoot, "moved-server");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(
            Path.Combine(
                serverRoot,
                IsolatedTestServerSqliteFinalizer.ServerRootMarkerFileName),
            serverRoot);

        var databasePath = Path.Combine(
            serverRoot,
            IsolatedTestServerSqliteFinalizer.DatabaseFileName);
        var movedDatabasePath = databasePath + ".moved";

        try
        {
            await CreateSimpleDatabaseAsync(databasePath, "lease-held");
            using var environment = new EnvironmentScope(
                ("GEORAEPLAN_TEST_MODE", "1"),
                ("GEORAEPLAN_TEST_SEED_MODE", "1"),
                ("GEORAEPLAN_TEST_SERVER_ROOT", serverRoot));
            var hooks = new IsolatedTestServerSqliteFinalizationTestHooks(
                BeforeResidualSidecarRemoval: () =>
                {
                    Assert.ThrowsAny<IOException>(
                        () => File.Move(databasePath, movedDatabasePath));
                    Assert.ThrowsAny<IOException>(
                        () => Directory.Move(serverRoot, movedServerRoot));
                });

            var result =
                IsolatedTestServerSqliteFinalizer.FinalizeDatabase(
                    databasePath,
                    hooks);

            Assert.Equal("ok", result.QuickCheck);
            Assert.True(File.Exists(databasePath));
            Assert.False(File.Exists(movedDatabasePath));
            Assert.False(Directory.Exists(movedServerRoot));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

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

        File.Copy(sourceDatabasePath, targetDatabasePath);
        File.Copy(sourceDatabasePath + "-wal", targetDatabasePath + "-wal");
        if (File.Exists(sourceDatabasePath + "-shm"))
            File.Copy(sourceDatabasePath + "-shm", targetDatabasePath + "-shm");
    }

    private static async Task CreateSimpleDatabaseAsync(
        string databasePath,
        string value)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE State (Value TEXT NOT NULL);
            INSERT INTO State (Value) VALUES ($value);
            """;
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadStateValueAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM State LIMIT 1;";
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void CreateHardLink(
        string linkPath,
        string existingFilePath)
    {
        if (!CreateHardLinkW(
                linkPath,
                existingFilePath,
                IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"CreateHardLinkW failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static string CreateTestRoot()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"server-sqlite-finalizer-{Guid.NewGuid():N}");
        Assert.Equal(
            "D:\\",
            Path.GetPathRoot(Path.GetFullPath(testRoot)),
            ignoreCase: true);
        Directory.CreateDirectory(testRoot);
        return testRoot;
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
}
