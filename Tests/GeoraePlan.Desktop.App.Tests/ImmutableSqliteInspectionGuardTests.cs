using System.Diagnostics;
using System.Runtime.CompilerServices;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ImmutableSqliteInspectionGuardTests
{
    [Fact]
    public void Guard_AllowsImmutableRead_BlocksWriter_AndReleasesCleanly()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan.ImmutableInspectionGuard.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "inspection.db");
        Directory.CreateDirectory(root);

        try
        {
            CreateDatabase(databasePath);
            var hashBefore = ComputeSha256(databasePath);
            var sidecarsBefore = SnapshotSidecars(databasePath);

            using (var guard =
                   ImmutableSqliteInspectionGuard.Acquire(databasePath))
            {
                using var readConnection = new SqliteConnection(
                    BuildConnectionString(
                        databasePath,
                        SqliteOpenMode.ReadOnly,
                        immutable: true));
                readConnection.Open();
                using var readCommand = readConnection.CreateCommand();
                readCommand.CommandText = "SELECT Value FROM Probe WHERE Id = 1;";
                Assert.Equal(
                    "ready",
                    Convert.ToString(readCommand.ExecuteScalar()));

                Assert.Throws<SqliteException>(
                    () =>
                    {
                        using var writeConnection = new SqliteConnection(
                            BuildConnectionString(
                                databasePath,
                                SqliteOpenMode.ReadWrite,
                                immutable: false));
                        writeConnection.Open();
                        using var blockedWriteCommand =
                            writeConnection.CreateCommand();
                        blockedWriteCommand.CommandText =
                            "UPDATE Probe SET Value = 'blocked' WHERE Id = 1;";
                        blockedWriteCommand.ExecuteNonQuery();
                    });

                guard.AssertStableSidecarFree();
                Assert.Equal(
                    sidecarsBefore,
                    SnapshotSidecars(databasePath));
                Assert.Equal(hashBefore, ComputeSha256(databasePath));
            }

            using var releasedConnection = new SqliteConnection(
                BuildConnectionString(
                    databasePath,
                    SqliteOpenMode.ReadWrite,
                    immutable: false));
            releasedConnection.Open();
            using var releasedCommand = releasedConnection.CreateCommand();
            releasedCommand.CommandText =
                "UPDATE Probe SET Value = 'released' WHERE Id = 1;";
            Assert.Equal(1, releasedCommand.ExecuteNonQuery());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Guard_FailsClosed_WhenWriterAlreadyOwnsDatabase()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan.ImmutableInspectionGuard.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "inspection.db");
        Directory.CreateDirectory(root);

        try
        {
            CreateDatabase(databasePath);
            using var writer = new FileStream(
                databasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);

            var exception = Assert.Throws<InvalidOperationException>(
                () => ImmutableSqliteInspectionGuard.Acquire(
                    databasePath));
            Assert.Contains(
                "no active writer",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(SnapshotSidecars(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Guard_FailsClosed_WhenDatabasePathTraversesDirectoryJunction()
    {
        var root = CreateTestRoot();
        var physicalRoot = Path.Combine(root, "physical");
        var junctionPath = Path.Combine(root, "junction");
        var databasePath = Path.Combine(physicalRoot, "inspection.db");
        Directory.CreateDirectory(physicalRoot);

        try
        {
            CreateDatabase(databasePath);
            CreateLinkWithCommand(
                junctionPath,
                physicalRoot,
                OperatingSystem.IsWindows() ? "/J" : "-s");

            ImmutableSqliteInspectionGuard? acquiredGuard = null;
            var exception = Record.Exception(
                () => acquiredGuard =
                    ImmutableSqliteInspectionGuard.Acquire(
                        Path.Combine(junctionPath, "inspection.db")));
            acquiredGuard?.Dispose();

            Assert.IsType<InvalidOperationException>(exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteLink(junctionPath, directoryLink: true);
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Guard_FailsClosed_WhenDatabasePathIsHardLinkAlias()
    {
        var root = CreateTestRoot();
        var databasePath = Path.Combine(root, "inspection.db");
        var aliasPath = Path.Combine(root, "inspection-alias.db");
        Directory.CreateDirectory(root);

        try
        {
            CreateDatabase(databasePath);
            CreateLinkWithCommand(
                aliasPath,
                databasePath,
                OperatingSystem.IsWindows() ? "/H" : string.Empty);

            ImmutableSqliteInspectionGuard? acquiredGuard = null;
            var exception = Record.Exception(
                () => acquiredGuard =
                    ImmutableSqliteInspectionGuard.Acquire(aliasPath));
            acquiredGuard?.Dispose();

            Assert.IsType<InvalidOperationException>(exception);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteLink(aliasPath, directoryLink: false);
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public void Guard_FailsClosed_WhenDatabaseFileIsSymbolicLink()
    {
        var root = CreateTestRoot();
        var databasePath = Path.Combine(root, "inspection.db");
        var symbolicLinkPath = Path.Combine(root, "inspection-link.db");
        Directory.CreateDirectory(root);

        try
        {
            CreateDatabase(databasePath);
            if (TryCreateFileSymbolicLink(symbolicLinkPath, databasePath))
            {
                ImmutableSqliteInspectionGuard? acquiredGuard = null;
                var exception = Record.Exception(
                    () => acquiredGuard =
                        ImmutableSqliteInspectionGuard.Acquire(
                            symbolicLinkPath));
                acquiredGuard?.Dispose();

                Assert.IsType<InvalidOperationException>(exception);
                return;
            }

            var guardSource = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "SyncDiag",
                "ImmutableSqliteInspectionGuard.cs"));
            Assert.Contains(
                "FileAttributes.ReparsePoint",
                guardSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "File.GetAttributes",
                guardSource,
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteLink(symbolicLinkPath, directoryLink: false);
            DeleteTestRoot(root);
        }
    }

    private static void CreateDatabase(string databasePath)
    {
        using var connection = new SqliteConnection(
            BuildConnectionString(
                databasePath,
                SqliteOpenMode.ReadWriteCreate,
                immutable: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=DELETE;
            CREATE TABLE Probe (Id INTEGER PRIMARY KEY, Value TEXT NOT NULL);
            INSERT INTO Probe (Id, Value) VALUES (1, 'ready');
            """;
        command.ExecuteNonQuery();
        connection.Close();
        SqliteConnection.ClearAllPools();
    }

    private static string BuildConnectionString(
        string databasePath,
        SqliteOpenMode mode,
        bool immutable)
        => new SqliteConnectionStringBuilder
        {
            DataSource = immutable
                ? new Uri(databasePath).AbsoluteUri + "?immutable=1"
                : databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

    private static string ComputeSha256(string path)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(path)));

    private static string[] SnapshotSidecars(string databasePath)
        => new[] { "-wal", "-shm", "-journal" }
            .Select(suffix => databasePath + suffix)
            .Where(File.Exists)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string CreateTestRoot()
        => Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan.ImmutableInspectionGuard.Tests",
            Guid.NewGuid().ToString("N"));

    private static bool TryCreateFileSymbolicLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or
            PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void CreateLinkWithCommand(
        string linkPath,
        string targetPath,
        string windowsLinkType)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add(windowsLinkType);
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
        }
        else
        {
            startInfo = new ProcessStartInfo("ln");
            if (!string.IsNullOrWhiteSpace(windowsLinkType))
                startInfo.ArgumentList.Add(windowsLinkType);
            startInfo.ArgumentList.Add(targetPath);
            startInfo.ArgumentList.Add(linkPath);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the filesystem-link creation process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create the isolated test link. " +
                $"{standardOutput} {standardError}");
        }
    }

    private static void DeleteLink(string linkPath, bool directoryLink)
    {
        try
        {
            if (directoryLink && Directory.Exists(linkPath))
                Directory.Delete(linkPath);
            else if (File.Exists(linkPath))
                File.Delete(linkPath);
        }
        catch
        {
            // Best-effort cleanup of the isolated test link.
        }
    }

    private static void DeleteTestRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
