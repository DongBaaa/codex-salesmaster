using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ProcessSafetyGuardTests
{
    [Fact]
    public void AppPaths_StayInsideTheIsolatedTestRoot()
    {
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.DataDir, TestProcessIsolation.AppRoot));
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.BackupDir, TestProcessIsolation.AppRoot));
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.AttachmentsDir, TestProcessIsolation.AppRoot));
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.LocalDbFile, TestProcessIsolation.AppRoot));
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.UserDownloadsDir, TestProcessIsolation.DownloadsRoot));
        Assert.True(TestProcessIsolation.IsWithin(AppPaths.TempDir, TestProcessIsolation.TempRoot));
        Assert.False(TestProcessIsolation.IsWithin(
            AppPaths.LocalDbFile,
            TestProcessIsolation.OriginalUserAppRoot));
    }

    [Fact]
    public void AppPaths_FailClosedWhenATestProcessHasNoIsolatedRoot()
    {
        var previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var loadContext = new AssemblyLoadContext(
            $"app-paths-fail-closed-{Guid.NewGuid():N}",
            isCollectible: true);

        try
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            var isolatedAssembly = loadContext.LoadFromAssemblyPath(
                typeof(AppPaths).Assembly.Location);
            var isolatedAppPaths = isolatedAssembly.GetType(
                "\uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure.AppPaths",
                throwOnError: true)!;

            var exception = Assert.Throws<TargetInvocationException>(
                () => isolatedAppPaths.GetProperty(
                        nameof(AppPaths.LocalDbFile),
                        BindingFlags.Public | BindingFlags.Static)!
                    .GetValue(null));

            Assert.Contains(
                "GEORAEPLAN_APP_ROOT is required for test processes",
                FlattenExceptionMessages(exception),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousAppRoot);
            loadContext.Unload();
        }
    }

    [Fact]
    public void ConfiguredAndTestRoots_RejectExistingReparsePoints()
    {
        var testRoot = Path.Combine(
            TestProcessIsolation.AppRoot,
            "configured-root-reparse-tests",
            Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(testRoot, "junction");
        var targetPath = Path.Combine(
            Path.GetDirectoryName(TestProcessIsolation.AppRoot)!,
            $"configured-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        Directory.CreateDirectory(targetPath);

        try
        {
            CreateDirectoryJunction(linkPath, targetPath);
            var escapedChild = Path.Combine(linkPath, "child");

            Assert.False(AppPaths.HasNoExistingReparsePointInPathChain(escapedChild));
            Assert.False(TestProcessIsolation.HasNoExistingReparsePointInPathChain(escapedChild));

            foreach (var settingName in new[]
                     {
                         "GEORAEPLAN_APP_ROOT",
                         "GEORAEPLAN_TEMP_ROOT",
                         "GEORAEPLAN_DOWNLOADS_ROOT"
                     })
            {
                var exception = Assert.Throws<InvalidOperationException>(
                    () => AppPaths.EnsureNoExistingReparsePointInPathChain(
                        escapedChild,
                        settingName));
                Assert.Contains("reparse point", exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteDirectoryLink(linkPath);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
            if (Directory.Exists(targetPath))
                Directory.Delete(targetPath, recursive: true);
        }
    }

    [Fact]
    public void SingleInstanceGuard_BlocksASecondOwnerForTheSameAppRoot()
    {
        var appRoot = Path.Combine(TestProcessIsolation.AppRoot, "single-instance-same-root");

        Assert.True(SingleInstanceGuard.TryAcquire(
            appRoot,
            out var first,
            out var acquiredIdentity));
        Assert.NotNull(first);
        try
        {
            Assert.False(SingleInstanceGuard.TryAcquire(
                appRoot,
                out var second,
                out var rejectedIdentity));
            Assert.Null(second);
            Assert.Equal(acquiredIdentity, rejectedIdentity);
            Assert.StartsWith("sha256:", acquiredIdentity, StringComparison.Ordinal);
            Assert.DoesNotContain(appRoot, acquiredIdentity, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            first!.Dispose();
        }
    }

    [Fact]
    public void SingleInstanceGuard_UsesCrossSessionGlobalNamespaceWithoutExposingPath()
    {
        var appRoot = Path.Combine(
            TestProcessIsolation.AppRoot,
            "single-instance-global-root");

        var mutexName = SingleInstanceGuard.BuildMutexName(appRoot);

        Assert.StartsWith(
            @"Global\GeoraePlan.Desktop.",
            mutexName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(appRoot, mutexName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleInstanceGuard_AppRootIdentity_IsStableForEquivalentNormalizedRoots()
    {
        var appRoot = Path.Combine(
            TestProcessIsolation.AppRoot,
            "single-instance-identity-root");
        var equivalentRoot = appRoot + Path.DirectorySeparatorChar;

        var identity = SingleInstanceGuard.BuildAppRootIdentity(appRoot);
        var equivalentIdentity =
            SingleInstanceGuard.BuildAppRootIdentity(equivalentRoot);

        Assert.Equal(identity, equivalentIdentity);
        Assert.Matches("^sha256:[0-9A-F]{64}$", identity);
        Assert.DoesNotContain(appRoot, identity, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleInstanceGuard_AllowsDifferentAppRootsAndReacquisitionAfterRelease()
    {
        var firstRoot = Path.Combine(TestProcessIsolation.AppRoot, "single-instance-root-a");
        var secondRoot = Path.Combine(TestProcessIsolation.AppRoot, "single-instance-root-b");

        Assert.True(SingleInstanceGuard.TryAcquire(firstRoot, out var first));
        Assert.True(SingleInstanceGuard.TryAcquire(secondRoot, out var second));
        Assert.NotEqual(
            SingleInstanceGuard.BuildMutexName(firstRoot),
            SingleInstanceGuard.BuildMutexName(secondRoot));

        first!.Dispose();
        second!.Dispose();

        Assert.True(SingleInstanceGuard.TryAcquire(firstRoot, out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public void InstallRootUpdateGate_BlocksDesktopRelaunchUntilReleased()
    {
        var installRoot = Path.Combine(
            TestProcessIsolation.AppRoot,
            "install-root-update-gate");

        Assert.True(InstallRootUpdateGate.TryAcquire(installRoot, out var first));
        Assert.False(InstallRootUpdateGate.TryAcquire(installRoot, out var blocked));
        Assert.Null(blocked);

        first!.Dispose();

        Assert.True(InstallRootUpdateGate.TryAcquire(installRoot, out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public void DesktopApp_AcquiresAndRetainsTheInstallRootGate()
    {
        var appSourcePath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs");
        var source = File.ReadAllText(appSourcePath);

        var acquire = source.IndexOf(
            "InstallRootUpdateGate.TryAcquire(AppContext.BaseDirectory",
            StringComparison.Ordinal);
        var startupCleanup = source.IndexOf(
            "DesktopAppUpdateService.TryCleanupStaleUpdateArtifacts();",
            StringComparison.Ordinal);
        var singleInstance = source.IndexOf(
            "SingleInstanceGuard.TryAcquireForCurrentAppRoot",
            StringComparison.Ordinal);
        var singleInstanceDiagnostic = source.IndexOf(
            "Single-instance acquisition {(singleInstanceAcquired ? \"succeeded\" : \"rejected\")}. appRootIdentity={appRootIdentity}",
            singleInstance,
            StringComparison.Ordinal);
        var singleInstanceRejectionBranch = source.IndexOf(
            "if (!singleInstanceAcquired)",
            singleInstance,
            StringComparison.Ordinal);
        var onExit = source.IndexOf(
            "protected override void OnExit",
            StringComparison.Ordinal);
        var release = source.IndexOf(
            "_installRootUpdateGate?.Dispose();",
            onExit,
            StringComparison.Ordinal);

        Assert.True(acquire >= 0);
        Assert.True(
            singleInstance > acquire,
            "The update gate must be checked before taking the single-instance mutex.");
        Assert.True(
            singleInstanceDiagnostic > singleInstance &&
            singleInstanceDiagnostic < singleInstanceRejectionBranch,
            "The root-safe acquisition result must be logged before the rejection branch exits.");
        Assert.True(startupCleanup > acquire);
        Assert.True(release > onExit);
        Assert.Contains(
            "out var appRootIdentity",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SyncDiag_HelpAndReadOnlySummaryBypassMutableDatabaseInitialization()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs");
        var source = File.ReadAllText(sourcePath);

        var helpBranch = source.IndexOf(
            "string.Equals(command, \"--help\"",
            StringComparison.Ordinal);
        var readOnlyBranch = source.IndexOf(
            "string.Equals(command, \"read-only-summary\"",
            StringComparison.Ordinal);
        var mutableDatabaseInitialization = source.IndexOf(
            "await using var db = new LocalDbContext();",
            StringComparison.Ordinal);

        Assert.True(helpBranch >= 0);
        Assert.True(readOnlyBranch > helpBranch);
        Assert.True(
            mutableDatabaseInitialization > readOnlyBranch,
            "Help and read-only inspection must return before the mutable LocalDbContext is created.");
        Assert.Contains("fullPath + \"-wal\"", source, StringComparison.Ordinal);
        Assert.Contains("fullPath + \"-shm\"", source, StringComparison.Ordinal);
        Assert.Contains("fullPath + \"-journal\"", source, StringComparison.Ordinal);
        Assert.Contains("\"?immutable=1\"", source, StringComparison.Ordinal);
        Assert.Contains("Mode = SqliteOpenMode.ReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("Pooling = false", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncDiag_ReadOnlyIntegrityReportBypassesMutableDatabaseInitializationAndUsesImmutableGuard()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs");
        var source = File.ReadAllText(sourcePath);

        var readOnlyDispatch = source.IndexOf(
            "return await PrintReadOnlyIntegrityReportAsync(",
            StringComparison.Ordinal);
        var readOnlyImplementation = source.IndexOf(
            "static async Task<int> PrintReadOnlyIntegrityReportAsync(",
            StringComparison.Ordinal);
        var immutableGuard = source.IndexOf(
            "ImmutableSqliteInspectionGuard.Acquire(databasePath)",
            readOnlyImplementation,
            StringComparison.Ordinal);
        var immutableConnection = source.IndexOf(
            "BuildImmutableInspectionConnectionString(",
            immutableGuard,
            StringComparison.Ordinal);
        var inspectionDb = source.IndexOf(
            "new LocalDbContext(inspectionOptions)",
            immutableConnection,
            StringComparison.Ordinal);
        var inspectionDbClose = source.IndexOf(
            "await inspectionDb.Database.CloseConnectionAsync();",
            inspectionDb,
            StringComparison.Ordinal);
        var inspectionDbDispose = source.IndexOf(
            "await inspectionDb.DisposeAsync();",
            inspectionDb,
            StringComparison.Ordinal);
        var sqlitePoolClear = source.IndexOf(
            "SqliteConnection.ClearAllPools();",
            inspectionDb,
            StringComparison.Ordinal);
        var stableAssertion = source.IndexOf(
            "inspectionGuard.AssertStableSidecarFree();",
            inspectionDb,
            StringComparison.Ordinal);
        var mutableDatabaseInitialization = source.IndexOf(
            "await using var db = new LocalDbContext();",
            StringComparison.Ordinal);

        Assert.True(readOnlyDispatch >= 0);
        Assert.True(
            mutableDatabaseInitialization > readOnlyDispatch,
            "The read-only integrity command must return before the mutable LocalDbContext is created.");
        Assert.True(readOnlyImplementation > mutableDatabaseInitialization);
        Assert.True(immutableGuard > readOnlyImplementation);
        Assert.True(immutableConnection > immutableGuard);
        Assert.True(inspectionDb > immutableConnection);
        Assert.True(
            inspectionDbClose > inspectionDb,
            "The EF SQLite connection must be closed before source stability is asserted.");
        Assert.True(
            inspectionDbDispose > inspectionDbClose,
            "The read-only DbContext must be disposed before source stability is asserted.");
        Assert.True(
            sqlitePoolClear > inspectionDbDispose,
            "SQLite pools must be cleared after disposal and before source stability is asserted.");
        Assert.True(
            stableAssertion > sqlitePoolClear,
            "Source stability must be asserted only after the DbContext and SQLite handles are released.");
    }

    private static string FlattenExceptionMessages(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            messages.Add(current.Message);

        return string.Join(Environment.NewLine, messages);
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(junctionPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
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
            ?? throw new InvalidOperationException("Could not start the junction creation process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create a D-drive test junction. {standardOutput} {standardError}");
        }
    }

    private static void DeleteDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
        }
        catch
        {
            // Best-effort cleanup of the isolated D-drive test link.
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
