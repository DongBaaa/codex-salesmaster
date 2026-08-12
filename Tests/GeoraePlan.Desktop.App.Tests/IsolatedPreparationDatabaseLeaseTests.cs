using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedPreparationDatabaseLeaseTests
{
    [Fact]
    public void AcquireForAppData_RequiresHeldParentPreparationLease()
    {
        var fixture = CreateAppDataFixture();
        File.WriteAllText(fixture.LockPath, string.Empty);

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
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AcquireForAppData_HoldsCompatibleChildLeaseAndDatabaseIdentity()
    {
        var fixture = CreateAppDataFixture();
        FileStream? parentLease = null;
        IsolatedPreparationDatabaseLease? childLease = null;

        try
        {
            parentLease = OpenParentPreparationLease(fixture.LockPath);
            childLease =
                IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot);

            Assert.Equal(
                fixture.DatabasePath,
                childLease.DatabasePath,
                ignoreCase: true);
            childLease.AssertStable();

            parentLease.Dispose();
            parentLease = null;
            Assert.Throws<IOException>(
                () => File.Open(
                    fixture.LockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None));

            childLease.Dispose();
            childLease = null;
            using var exclusiveRuntimeLease = File.Open(
                fixture.LockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            childLease?.Dispose();
            parentLease?.Dispose();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AcquireForAppData_RejectsMarkerRootMismatch()
    {
        var fixture = CreateAppDataFixture();
        File.WriteAllText(
            fixture.MarkerPath,
            Path.Combine(fixture.OutputRoot, "different-AppData"));

        try
        {
            using var parentLease =
                OpenParentPreparationLease(fixture.LockPath);
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains(
                "marker does not match",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AcquireForAppData_RejectsHardLinkedDatabase()
    {
        var fixture = CreateAppDataFixture();
        var externalDatabase = Path.Combine(
            fixture.OutputRoot,
            "external-database.bin");
        File.WriteAllBytes(externalDatabase, [9, 8, 7, 6, 5, 4]);
        File.Delete(fixture.DatabasePath);
        CreateHardLink(fixture.DatabasePath, externalDatabase);

        try
        {
            using var parentLease =
                OpenParentPreparationLease(fixture.LockPath);
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains(
                "hard link",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new byte[] { 9, 8, 7, 6, 5, 4 },
                File.ReadAllBytes(externalDatabase));
        }
        finally
        {
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AcquireForAppData_RejectsDataJunctionToCBeforeDatabaseUse()
    {
        var fixture = CreateAppDataFixture();
        Directory.Delete(fixture.DataDirectory, recursive: true);
        var cTarget = Environment.GetEnvironmentVariable("SystemRoot")
            ?? @"C:\Windows";
        CreateDirectoryJunction(fixture.DataDirectory, cTarget);
        var cSentinel = Path.Combine(cTarget, "win.ini");
        var cSentinelLength = File.Exists(cSentinel)
            ? new FileInfo(cSentinel).Length
            : (long?)null;

        try
        {
            using var parentLease =
                OpenParentPreparationLease(fixture.LockPath);
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireForAppData(
                    fixture.AppRoot,
                    fixture.AppRoot));

            Assert.Contains(
                "reparse point",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            if (cSentinelLength.HasValue)
            {
                Assert.Equal(
                    cSentinelLength.Value,
                    new FileInfo(cSentinel).Length);
            }
            Assert.False(File.Exists(cSentinel + "-wal"));
            Assert.False(File.Exists(cSentinel + "-shm"));
            Assert.False(File.Exists(cSentinel + "-journal"));
        }
        finally
        {
            RemoveDirectoryJunction(fixture.DataDirectory);
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public void AcquireForServerRoot_RequiresAndHoldsPreparationLease()
    {
        var outputRoot = CreateOutputRoot();
        var serverRoot = Path.Combine(outputRoot, "Server");
        var lockPath = Path.Combine(
            outputRoot,
            IsolatedPreparationDatabaseLease.PreparationLockFileName);
        Directory.CreateDirectory(serverRoot);

        File.WriteAllText(lockPath, string.Empty);
        var missingError = Assert.Throws<InvalidOperationException>(
            () => IsolatedPreparationDatabaseLease.AcquireForServerRoot(
                serverRoot));
        Assert.Contains(
            "parent preparation lease is not held",
            missingError.Message,
            StringComparison.OrdinalIgnoreCase);

        FileStream? parentLease = null;
        IsolatedPreparationDatabaseLease? childLease = null;
        try
        {
            parentLease = OpenParentPreparationLease(lockPath);
            childLease =
                IsolatedPreparationDatabaseLease.AcquireForServerRoot(
                    serverRoot);
            parentLease.Dispose();
            parentLease = null;

            Assert.Throws<IOException>(
                () => File.Open(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None));
            childLease.AssertStable();
        }
        finally
        {
            childLease?.Dispose();
            parentLease?.Dispose();
            DeleteFixture(outputRoot);
        }
    }

    [Fact]
    public void AcquireReadOnlyDatabase_RejectsNormalCApplicationDataBeforeOpen()
    {
        var productionDatabase = Path.Combine(
            TestProcessIsolation.OriginalUserAppRoot,
            "data",
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);

        var error = Assert.Throws<InvalidOperationException>(
            () => IsolatedPreparationDatabaseLease.AcquireReadOnlyDatabase(
                productionDatabase));

        Assert.Contains(
            "D: volume",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcquireReadOnlyDatabase_RejectsDJunctionResolvingToC()
    {
        var outputRoot = CreateOutputRoot();
        var junctionPath = Path.Combine(outputRoot, "c-target");
        var cTarget = Environment.GetEnvironmentVariable("SystemRoot")
            ?? @"C:\Windows";
        CreateDirectoryJunction(junctionPath, cTarget);
        var databasePath = Path.Combine(junctionPath, "win.ini");

        try
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => IsolatedPreparationDatabaseLease.AcquireReadOnlyDatabase(
                    databasePath));

            Assert.Contains(
                "reparse point",
                error.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(databasePath + "-wal"));
            Assert.False(File.Exists(databasePath + "-shm"));
            Assert.False(File.Exists(databasePath + "-journal"));
        }
        finally
        {
            RemoveDirectoryJunction(junctionPath);
            DeleteFixture(outputRoot);
        }
    }

    [Fact]
    public void AcquireReadOnlyDatabase_HoldsSingleLinkDDatabase()
    {
        var outputRoot = CreateOutputRoot();
        var dataDirectory = Path.Combine(outputRoot, "snapshot", "data");
        var databasePath = Path.Combine(
            dataDirectory,
            IsolatedPreparationDatabaseLease.LocalDatabaseFileName);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllBytes(databasePath, [1, 3, 3, 7]);

        try
        {
            using var lease =
                IsolatedPreparationDatabaseLease.AcquireReadOnlyDatabase(
                    databasePath);
            lease.AssertStable();
            Assert.Equal(
                databasePath,
                lease.DatabasePath,
                ignoreCase: true);

            Assert.Throws<IOException>(
                () => File.Move(
                    databasePath,
                    databasePath + ".moved"));
        }
        finally
        {
            DeleteFixture(outputRoot);
        }
    }

    [Fact]
    public void SyncDiag_AcquiresDatabaseLeaseBeforeMutableContext()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs");
        var source = File.ReadAllText(programPath);

        var leaseAcquisition = source.IndexOf(
            "IsolatedPreparationDatabaseLease.AcquireForAppData(",
            StringComparison.Ordinal);
        var credentialRead = source.IndexOf(
            "PrintStoredCredentialEnvelopesAsync(",
            leaseAcquisition,
            StringComparison.Ordinal);
        var mutableContext = source.IndexOf(
            "await using var db = new LocalDbContext();",
            StringComparison.Ordinal);
        var inspectionDispatch = source.IndexOf(
            "if (string.Equals(command, \"inspect\", StringComparison.Ordinal))",
            StringComparison.Ordinal);
        var inspectionGuardAcquire = source.IndexOf(
            "ImmutableSqliteInspectionGuard.Acquire(databasePath)",
            inspectionDispatch >= 0 ? inspectionDispatch : 0,
            StringComparison.Ordinal);
        var inspectionReadOnlyFactoryCall = source.IndexOf(
            "BuildImmutableInspectionConnectionString(",
            inspectionGuardAcquire >= 0
                ? inspectionGuardAcquire
                : 0,
            StringComparison.Ordinal);
        var inspectionGuardFinalAssert = source.IndexOf(
            "inspectionGuard.AssertStableSidecarFree();",
            inspectionReadOnlyFactoryCall >= 0
                ? inspectionReadOnlyFactoryCall
                : 0,
            StringComparison.Ordinal);
        var inspectionReturn = source.IndexOf(
            "return 0;",
            inspectionGuardFinalAssert >= 0
                ? inspectionGuardFinalAssert
                : 0,
            StringComparison.Ordinal);
        var inspectionReadOnlyFactory = source.IndexOf(
            "static string BuildImmutableInspectionConnectionString",
            StringComparison.Ordinal);
        var inspectionReadOnlyMode = source.IndexOf(
            "Mode = SqliteOpenMode.ReadOnly",
            inspectionReadOnlyFactory >= 0
                ? inspectionReadOnlyFactory
                : 0,
            StringComparison.Ordinal);
        var readOnlyGuard = source.IndexOf(
            "IsolatedPreparationDatabaseLease.AcquireReadOnlyDatabase(",
            StringComparison.Ordinal);

        Assert.True(leaseAcquisition >= 0);
        Assert.True(credentialRead > leaseAcquisition);
        Assert.True(mutableContext > leaseAcquisition);
        Assert.True(
            inspectionDispatch > leaseAcquisition &&
            inspectionGuardAcquire > inspectionDispatch &&
            inspectionReadOnlyFactoryCall > inspectionDispatch &&
            inspectionGuardFinalAssert > inspectionReadOnlyFactoryCall &&
            inspectionReturn > inspectionGuardFinalAssert &&
            inspectionReturn < mutableContext,
            "inspect must hold and revalidate the immutable read guard before the mutable initializer path.");
        Assert.True(
            inspectionReadOnlyFactory > mutableContext &&
            inspectionReadOnlyMode > inspectionReadOnlyFactory,
            "inspect must build an immutable read-only SQLite connection.");
        Assert.True(readOnlyGuard > mutableContext);
        Assert.Contains(
            "IsolatedPreparationDatabaseLease.AcquireForServerRoot(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(value, \"preseed-sync\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(value, \"sync\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(value, \"maintenance-sync\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(value, \"inspect\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "const int maxDetailRows = 25;",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.Split(
                ".Take(maxDetailRows)",
                StringSplitOptions.None).Length - 1 >= 4,
            "inspect detail output must remain deterministically bounded.");
    }

    [Fact]
    public async Task SyncDiag_Inspect_ReadsDirtySystemDefaultWithoutChangingDatabase()
    {
        var fixture = CreateAppDataFixture();
        FileStream? parentLease = null;

        try
        {
            File.Delete(fixture.DatabasePath);
            var writableOptions =
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite($"Data Source={fixture.DatabasePath}")
                    .Options;
            await using (var db = new LocalDbContext(writableOptions))
            {
                await db.Database.EnsureCreatedAsync();
                db.RentalManagementCompanies.Add(
                    new LocalRentalManagementCompany
                    {
                        Id = Guid.NewGuid(),
                        Code = OfficeCodeCatalog.Usenet,
                        Name = OfficeCodeCatalog.Usenet,
                        IsSystemDefault = true,
                        IsActive = true,
                        IsDirty = true,
                        CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
                        UpdatedAtUtc = DateTime.UtcNow
                    });
                await db.SaveChangesAsync();
            }
            SqliteConnection.ClearAllPools();

            var hashBefore = Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        fixture.DatabasePath)));
            var sidecarsBefore = SnapshotSidecars(fixture.DatabasePath);

            parentLease = OpenParentPreparationLease(fixture.LockPath);
            var result = await RunSyncDiagInspectAsync(fixture);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Contains(
                "inspection_mode=read_only",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "all_scope_rental_management_companies_dirty=1",
                result.Stdout,
                StringComparison.Ordinal);
            Assert.Contains(
                "dirty_rental_management_company_detail_count=1",
                result.Stdout,
                StringComparison.Ordinal);

            var hashAfter = Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        fixture.DatabasePath)));
            Assert.Equal(hashBefore, hashAfter);
            Assert.Equal(
                sidecarsBefore,
                SnapshotSidecars(fixture.DatabasePath));

            var readOnlyOptions =
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource =
                                new Uri(fixture.DatabasePath).AbsoluteUri +
                                "?immutable=1",
                            Mode = SqliteOpenMode.ReadOnly,
                            Cache = SqliteCacheMode.Private,
                            Pooling = false
                        }.ToString())
                    .Options;
            await using var verificationDb =
                new LocalDbContext(readOnlyOptions);
            Assert.True(
                await verificationDb.RentalManagementCompanies
                    .IgnoreQueryFilters()
                    .Select(company => company.IsDirty)
                    .SingleAsync());
        }
        finally
        {
            parentLease?.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    [Fact]
    public async Task SyncDiag_Inspect_RejectsSidecarDatabaseWithoutOpeningIt()
    {
        var fixture = CreateAppDataFixture();
        FileStream? parentLease = null;

        try
        {
            var walPath = fixture.DatabasePath + "-wal";
            await File.WriteAllBytesAsync(
                walPath,
                [7, 3, 1, 9]);
            var databaseHashBefore = Convert.ToHexString(
                SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        fixture.DatabasePath)));
            var sidecarsBefore =
                SnapshotSidecars(fixture.DatabasePath);

            parentLease =
                OpenParentPreparationLease(fixture.LockPath);
            var result = await RunSyncDiagInspectAsync(fixture);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains(
                "requires a finalized sidecar-free database",
                result.Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                databaseHashBefore,
                Convert.ToHexString(
                    SHA256.HashData(
                        await File.ReadAllBytesAsync(
                            fixture.DatabasePath))));
            Assert.Equal(
                sidecarsBefore,
                SnapshotSidecars(fixture.DatabasePath));
            Assert.False(
                File.Exists(fixture.DatabasePath + "-shm"));
            Assert.False(
                File.Exists(fixture.DatabasePath + "-journal"));
        }
        finally
        {
            parentLease?.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteFixture(fixture.OutputRoot);
        }
    }

    private static string SnapshotSidecars(string databasePath)
        => string.Join(
            Environment.NewLine,
            new[] { "-wal", "-shm", "-journal" }
                .Select(suffix => databasePath + suffix)
                .Where(File.Exists)
                .Select(path =>
                    $"{Path.GetFileName(path)}|" +
                    $"{new FileInfo(path).Length}|" +
                    $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
                .OrderBy(value => value, StringComparer.Ordinal));

    private static async Task<SyncDiagProcessResult>
        RunSyncDiagInspectAsync(AppDataFixture fixture)
    {
        var syncDiagPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "bin",
            "Debug",
            "net8.0-windows",
            "SyncDiag.dll");
        Assert.True(
            File.Exists(syncDiagPath),
            $"SyncDiag was not built: {syncDiagPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(syncDiagPath);
        startInfo.ArgumentList.Add("inspect");
        startInfo.Environment["GEORAEPLAN_TEST_MODE"] = "1";
        startInfo.Environment["GEORAEPLAN_TEST_SEED_MODE"] = "1";
        startInfo.Environment["GEORAEPLAN_APP_ROOT"] =
            fixture.AppRoot;
        startInfo.Environment["GEORAEPLAN_TEST_SEED_ROOT"] =
            fixture.AppRoot;
        startInfo.Environment.Remove("GEORAEPLAN_SYNC_USERNAME");
        startInfo.Environment.Remove("GEORAEPLAN_SYNC_PASSWORD");
        startInfo.Environment.Remove("GEORAEPLAN_SYNC_BASEURL");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync()
            .WaitAsync(TimeSpan.FromSeconds(30));
        return new SyncDiagProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static AppDataFixture CreateAppDataFixture()
    {
        var outputRoot = CreateOutputRoot();
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
        File.WriteAllBytes(databasePath, [1, 2, 3, 4]);
        File.WriteAllText(markerPath, appRoot);
        return new AppDataFixture(
            outputRoot,
            appRoot,
            dataDirectory,
            databasePath,
            markerPath,
            lockPath);
    }

    private static string CreateOutputRoot()
    {
        var root = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"isolated-preparation-lease-{Guid.NewGuid():N}");
        Assert.Equal(
            @"D:\",
            Path.GetPathRoot(Path.GetFullPath(root)),
            ignoreCase: true);
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileStream OpenParentPreparationLease(string lockPath)
        => File.Open(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

    private static void CreateHardLink(
        string hardLinkPath,
        string existingFilePath)
    {
        Assert.True(
            NativeMethods.CreateHardLinkW(
                hardLinkPath,
                existingFilePath,
                IntPtr.Zero),
            $"Failed to create hard link. Win32Error={Marshal.GetLastWin32Error()}");
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
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The junction creation process did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0 &&
            Directory.Exists(junctionPath) &&
            (File.GetAttributes(junctionPath) &
             FileAttributes.ReparsePoint) != 0,
            "Failed to create the D-to-C junction fixture." +
            Environment.NewLine +
            stdout +
            Environment.NewLine +
            stderr);
    }

    private static void RemoveDirectoryJunction(string junctionPath)
    {
        if (Directory.Exists(junctionPath))
            Directory.Delete(junctionPath);
    }

    private static void DeleteFixture(string outputRoot)
    {
        if (Directory.Exists(outputRoot))
            Directory.Delete(outputRoot, recursive: true);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath)
            ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }

    private sealed record SyncDiagProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private sealed record AppDataFixture(
        string OutputRoot,
        string AppRoot,
        string DataDirectory,
        string DatabasePath,
        string MarkerPath,
        string LockPath);

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
