using System.Reflection;
using System.Diagnostics;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LegacyMigrationTestIsolationTests
{
    [Fact]
    public void AutomaticLegacyDatabaseProbe_UsesOnlyIsolatedAppRootDuringTests()
    {
        Assert.True(AppPaths.IsTestEnvironment);
        var isolatedLegacyPath = Path.Combine(
            AppPaths.AppRoot,
            "legacy-probe",
            "local-app-data",
            "SalesMaster",
            "data",
            "salesmaster.db");
        Directory.CreateDirectory(Path.GetDirectoryName(isolatedLegacyPath)!);
        File.WriteAllBytes(isolatedLegacyPath, [0x53, 0x51, 0x4C]);

        try
        {
            var method = typeof(LegacyDataMigrationService).GetMethod(
                "ResolveLegacyLocalDbPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var resolved = Assert.IsType<string>(method!.Invoke(null, null));

            Assert.Equal(
                Path.GetFullPath(isolatedLegacyPath),
                Path.GetFullPath(resolved),
                ignoreCase: true);
            Assert.True(AppPaths.IsWithinAppRoot(resolved));
        }
        finally
        {
            try
            {
                var probeRoot = Path.Combine(AppPaths.AppRoot, "legacy-probe");
                if (Directory.Exists(probeRoot))
                    Directory.Delete(probeRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated D-drive app root.
            }
        }
    }

    [Fact]
    public void AutomaticLegacyDatabaseProbe_RejectsJunctionEscape()
    {
        var localAppDataProbe = Path.Combine(
            AppPaths.AppRoot,
            "legacy-probe",
            "local-app-data");
        var salesMasterLink = Path.Combine(localAppDataProbe, "SalesMaster");
        var targetDirectory = Path.Combine(
            Path.GetDirectoryName(AppPaths.AppRoot)!,
            $"legacy-db-reparse-target-{Guid.NewGuid():N}");
        var targetDataDirectory = Path.Combine(targetDirectory, "data");
        Directory.CreateDirectory(localAppDataProbe);
        Directory.CreateDirectory(targetDataDirectory);
        File.WriteAllBytes(Path.Combine(targetDataDirectory, "salesmaster.db"), [0x53, 0x51, 0x4C]);

        try
        {
            CreateDirectoryJunction(salesMasterLink, targetDirectory);

            var method = typeof(LegacyDataMigrationService).GetMethod(
                "ResolveLegacyLocalDbPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            Assert.Null(method!.Invoke(null, null));
        }
        finally
        {
            DeleteDirectoryLink(salesMasterLink);
            var probeRoot = Path.Combine(AppPaths.AppRoot, "legacy-probe");
            if (Directory.Exists(probeRoot))
                Directory.Delete(probeRoot, recursive: true);
            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);
        }
    }

    [Fact]
    public void AutomaticLegacyExcelProbe_DoesNotWalkOutsideIsolatedAppRootDuringTests()
    {
        var method = typeof(LegacyDataMigrationService).GetMethod(
            "EnumerateLegacyProbeRoots",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var roots = Assert.IsAssignableFrom<IEnumerable<string>>(
                method!.Invoke(null, null))
            .ToList();

        var root = Assert.Single(roots);
        Assert.True(AppPaths.IsWithinAppRoot(root));
        Assert.Equal(
            Path.Combine(AppPaths.AppRoot, "legacy-probe"),
            root,
            ignoreCase: true);
    }

    [Fact]
    public void ConfiguredLegacyExcelPath_OutsideAppRoot_IsDiscardedInTests()
    {
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(AppPaths.AppRoot)!,
            $"outside-legacy-{Guid.NewGuid():N}.xlsx");

        var sanitized = InvokePrivateStatic<string>(
            "SanitizeAutomaticLegacyProbePath",
            outsidePath);

        Assert.Equal(string.Empty, sanitized);
    }

    [Fact]
    public void AutomaticLegacyExcelImport_RechecksContainmentAndRejectsJunctionEscape()
    {
        var testRoot = Path.Combine(
            AppPaths.AppRoot,
            "legacy-reparse-tests",
            Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(testRoot, "source");
        var targetDirectory = Path.Combine(
            Path.GetDirectoryName(AppPaths.AppRoot)!,
            $"legacy-reparse-target-{Guid.NewGuid():N}");
        var customerPath = Path.Combine(sourceDirectory, "customers.xlsx");
        var itemPath = Path.Combine(sourceDirectory, "items.xlsx");

        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllBytes(customerPath, [0x01]);
        File.WriteAllBytes(itemPath, [0x02]);

        try
        {
            Assert.True(InvokePrivateStatic<bool>(
                "AreAutomaticLegacyExcelPathsSafeForImport",
                customerPath,
                itemPath));

            File.Delete(customerPath);
            File.Delete(itemPath);
            Directory.Delete(sourceDirectory);
            File.WriteAllBytes(Path.Combine(targetDirectory, "customers.xlsx"), [0x03]);
            File.WriteAllBytes(Path.Combine(targetDirectory, "items.xlsx"), [0x04]);
            CreateDirectoryJunction(sourceDirectory, targetDirectory);

            Assert.False(AppPaths.IsWithinAppRoot(customerPath));
            Assert.False(InvokePrivateStatic<bool>(
                "AreAutomaticLegacyExcelPathsSafeForImport",
                customerPath,
                itemPath));

            var exception = Assert.Throws<TargetInvocationException>(
                () => InvokePrivateStatic<object?>(
                    "EnsureAutomaticLegacyExcelPathsSafeForImport",
                    customerPath,
                    itemPath));
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }
        finally
        {
            DeleteDirectoryLink(sourceDirectory);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
            if (Directory.Exists(targetDirectory))
                Directory.Delete(targetDirectory, recursive: true);
        }
    }

    private static T InvokePrivateStatic<T>(string methodName, params object[] arguments)
    {
        var method = typeof(LegacyDataMigrationService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, arguments)!;
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
}
