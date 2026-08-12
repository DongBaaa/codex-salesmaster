using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DesktopInstallerPackageBuilderSafetyTests
{
    [Fact]
    public async Task Builder_RejectsPathBearingNamesBeforeTheyCanDeleteOutsideOutput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("invalid-names");
        var outputRoot = Path.Combine(testRoot, "output");
        var escapedRoot = Path.Combine(testRoot, "escaped-package");
        var markerPath = Path.Combine(escapedRoot, "must-survive.txt");

        try
        {
            Directory.CreateDirectory(escapedRoot);
            File.WriteAllText(markerPath, "outside output marker");

            var invalidPackageResult = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-OutputRoot", outputRoot),
                ("-PackageName", @"..\escaped-package"),
                ("-SkipNativeInstallers", null));
            Assert.NotEqual(0, invalidPackageResult.ExitCode);
            Assert.Contains(
                "PackageName must be a single file name",
                invalidPackageResult.StdOut + invalidPackageResult.StdErr,
                StringComparison.Ordinal);
            Assert.Equal("outside output marker", File.ReadAllText(markerPath));

            var invalidDisplayNameResult = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-OutputRoot", outputRoot),
                ("-AppDisplayName", @"folder\escaped-app"),
                ("-SkipNativeInstallers", null));
            Assert.NotEqual(0, invalidDisplayNameResult.ExitCode);
            Assert.Contains(
                "AppDisplayName must be a single file name",
                invalidDisplayNameResult.StdOut + invalidDisplayNameResult.StdErr,
                StringComparison.Ordinal);
            Assert.Equal("outside output marker", File.ReadAllText(markerPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_DoesNotCertifyOrOverwriteLockedExistingZip()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("locked-zip");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            Assert.Contains(
                "package_ready",
                firstBuild.StdOut,
                StringComparison.Ordinal);

            var zipPath = Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarContent =
                ReadAndAssertSha256Sidecar(zipPath);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "changed-after-first-build.txt"),
                "the second archive must differ");

            ProcessResult lockedBuild;
            using (var zipLease = new FileStream(
                       zipPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                lockedBuild = await RunBuilderAsync(scriptPath, fixture);
                Assert.NotEqual(0, lockedBuild.ExitCode);
                Assert.DoesNotContain(
                    "package_ready",
                    lockedBuild.StdOut + lockedBuild.StdErr,
                    StringComparison.Ordinal);
                Assert.Equal(originalZipHash, ComputeSha256(zipPath));
                Assert.Equal(
                    originalSidecarContent,
                    ReadAndAssertSha256Sidecar(zipPath));
            }

            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.staged.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.previous.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.failed-publish.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.staged.sha256.txt",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.previous.sha256.txt",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.failed-publish.sha256.txt",
                    SearchOption.TopDirectoryOnly));

            var recoveredBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                recoveredBuild.ExitCode == 0,
                recoveredBuild.StdOut + Environment.NewLine +
                recoveredBuild.StdErr);
            Assert.Contains(
                "package_ready",
                recoveredBuild.StdOut,
                StringComparison.Ordinal);
            Assert.NotEqual(originalZipHash, ComputeSha256(zipPath));
            Assert.NotEqual(
                originalSidecarContent,
                ReadAndAssertSha256Sidecar(zipPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RestoresPreviousZipWhenPostReplacementVerificationFails()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("post-replace-rollback");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);

            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var previousZipHash = ComputeSha256(zipPath);
            var previousSidecarContent =
                ReadAndAssertSha256Sidecar(zipPath);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "post-replace-change.txt"),
                "the failed candidate must be rolled back");

            var failedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_FAIL_AFTER_ZIP_REPLACE",
                        "1")
                ]);
            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "Injected package archive post-replacement verification failure",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.Contains(
                "package_zip_restore=SUCCESS",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "package_ready",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(previousZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                previousSidecarContent,
                ReadAndAssertSha256Sidecar(zipPath));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.previous.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.failed-publish.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.staged.zip",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.staged.sha256.txt",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.previous.sha256.txt",
                    SearchOption.TopDirectoryOnly));
            Assert.Empty(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.failed-publish.sha256.txt",
                    SearchOption.TopDirectoryOnly));

            var recoveredBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, recoveredBuild.ExitCode);
            Assert.NotEqual(previousZipHash, ComputeSha256(zipPath));
            Assert.NotEqual(
                previousSidecarContent,
                ReadAndAssertSha256Sidecar(zipPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RecoversDurableTransactionAfterHardKill()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("hard-kill-recovery");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarContent =
                ReadAndAssertSha256Sidecar(zipPath);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "hard-kill-change.txt"),
                "replacement candidate");

            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_ZIP_REPLACE",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);
            Assert.True(File.Exists(Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json")));
            Assert.NotEqual(originalZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                originalSidecarContent,
                File.ReadAllText(
                    zipPath + ".sha256.txt",
                    Encoding.UTF8).TrimEnd('\r', '\n'));

            var recoveredBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.True(
                recoveredBuild.ExitCode == 0,
                recoveredBuild.StdOut + Environment.NewLine +
                recoveredBuild.StdErr);
            Assert.Contains(
                "package_zip_restore=SUCCESS",
                recoveredBuild.StdOut,
                StringComparison.Ordinal);
            Assert.Contains(
                "package_ready",
                recoveredBuild.StdOut,
                StringComparison.Ordinal);
            Assert.NotEqual(originalZipHash, ComputeSha256(zipPath));
            ReadAndAssertSha256Sidecar(zipPath);
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RecoversOwnedStagedZipAfterHardKillAndPreservesUnmarkedDecoy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("staged-owner-hard-kill");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);

            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarHash = ComputeSha256(
                zipPath + ".sha256.txt");
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "staged-owner-change.txt"),
                "candidate left after the owner marker");

            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_STAGED_ZIP_CREATE",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);

            var ownerMarkerPath = Directory.EnumerateFiles(
                adminOutputRoot,
                ".georaeplan-package-staged-owner.*.json",
                SearchOption.TopDirectoryOnly).Single();
            var ownedStagedZipPath = Directory.EnumerateFiles(
                adminOutputRoot,
                ".*.staged.zip",
                SearchOption.TopDirectoryOnly).Single();
            var decoyPath = Path.Combine(
                adminOutputRoot,
                ".unmarked-decoy.staged.zip");
            File.WriteAllText(decoyPath, "must survive owner recovery");

            var recoveryOnlyBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.True(
                recoveryOnlyBuild.ExitCode == 0,
                recoveryOnlyBuild.StdOut + Environment.NewLine +
                recoveryOnlyBuild.StdErr);
            Assert.Contains(
                "package_staged_zip_owner_recovery=RECOVERED",
                recoveryOnlyBuild.StdOut,
                StringComparison.Ordinal);
            Assert.False(File.Exists(ownerMarkerPath));
            Assert.False(File.Exists(ownedStagedZipPath));
            Assert.True(File.Exists(decoyPath));
            Assert.Equal(
                "must survive owner recovery",
                File.ReadAllText(decoyPath));
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                originalSidecarHash,
                ComputeSha256(zipPath + ".sha256.txt"));
            Assert.False(File.Exists(Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RemovesOwnedStagedZipAfterNormalPreJournalFailure()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("staged-owner-normal-failure");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarHash = ComputeSha256(
                zipPath + ".sha256.txt");
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "normal-failure.txt"),
                "candidate must be cleaned");

            var failedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_FAIL_AFTER_STAGED_ZIP_CREATE",
                        "1")
                ]);
            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "Injected staged package archive failure",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            AssertNoPackageTransactionResidue(adminOutputRoot);
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                originalSidecarHash,
                ComputeSha256(zipPath + ".sha256.txt"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_FailsClosedAndPreservesMalformedStagedOwnerEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("staged-owner-malformed");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);

            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarHash = ComputeSha256(
                zipPath + ".sha256.txt");
            var transactionId = Guid.NewGuid().ToString("N");
            var ownerMarkerPath = Path.Combine(
                adminOutputRoot,
                $".georaeplan-package-staged-owner.{transactionId}.json");
            var ownedName =
                $".거래플랜-PC-설치패키지.{transactionId}.staged.zip";
            var ownedStagedZipPath = Path.Combine(
                adminOutputRoot,
                ownedName);
            File.WriteAllText(ownerMarkerPath, "{not-json");
            File.WriteAllText(
                ownedStagedZipPath,
                "malformed marker evidence");

            var failedRecovery = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.NotEqual(0, failedRecovery.ExitCode);
            var recoveryOutput =
                failedRecovery.StdOut + failedRecovery.StdErr;
            Assert.True(
                recoveryOutput.Contains(
                    "staged ZIP owner marker is malformed",
                    StringComparison.OrdinalIgnoreCase),
                recoveryOutput);
            Assert.True(File.Exists(ownerMarkerPath));
            Assert.True(File.Exists(ownedStagedZipPath));
            Assert.Equal(
                "malformed marker evidence",
                File.ReadAllText(ownedStagedZipPath));
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                originalSidecarHash,
                ComputeSha256(zipPath + ".sha256.txt"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_HoldsOwnerMarkerLeaseUntilHardKillAndThenRecovers()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("staged-owner-lease");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, firstBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var killSignalPath = Path.Combine(testRoot, "kill.signal");
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "lease-change.txt"),
                "lease protected candidate");

            var killedBuildTask = RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_STAGED_ZIP_KILL_SIGNAL",
                        killSignalPath),
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_STAGED_ZIP_CREATE",
                        "1")
                ]);
            var ownerMarkerPath = await WaitForSingleFileAsync(
                adminOutputRoot,
                ".georaeplan-package-staged-owner.*.json",
                TimeSpan.FromSeconds(20));

            var writeFailure = Record.Exception(() =>
            {
                using var ignored = new FileStream(
                    ownerMarkerPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None);
            });
            var deleteFailure = Record.Exception(
                () => File.Delete(ownerMarkerPath));
            var markerSurvivedAccessAttempts =
                File.Exists(ownerMarkerPath);

            File.WriteAllText(killSignalPath, "kill");
            var killedBuild = await killedBuildTask;
            Assert.True(
                writeFailure is IOException or UnauthorizedAccessException,
                $"Owner marker write was not blocked: {writeFailure}");
            Assert.True(
                deleteFailure is IOException or UnauthorizedAccessException,
                $"Owner marker deletion was not blocked: {deleteFailure}");
            Assert.True(markerSurvivedAccessAttempts);
            Assert.NotEqual(0, killedBuild.ExitCode);
            Assert.True(File.Exists(ownerMarkerPath));

            var recovery = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.Equal(0, recovery.ExitCode);
            Assert.Contains(
                "package_staged_zip_owner_recovery=RECOVERED",
                recovery.StdOut,
                StringComparison.Ordinal);
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RecoversWhenDurableAndOwnerJournalsCoexistAfterHardKill()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("dual-journal-hard-kill");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, firstBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "dual-journal.txt"),
                "candidate before replacement");

            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_DURABLE_TRANSACTION_WRITE",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);
            Assert.True(File.Exists(Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json")));
            Assert.Single(Directory.EnumerateFiles(
                adminOutputRoot,
                ".georaeplan-package-staged-owner.*.json",
                SearchOption.TopDirectoryOnly));
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));

            var recovery = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.Equal(0, recovery.ExitCode);
            Assert.Contains(
                "package_staged_zip_owner_recovery=RECOVERED",
                recovery.StdOut,
                StringComparison.Ordinal);
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("schema-casing")]
    [InlineData("guid")]
    [InlineData("guid-casing")]
    [InlineData("package")]
    [InlineData("package-casing")]
    [InlineData("staged-name")]
    [InlineData("staged-name-casing")]
    [InlineData("marker-directory")]
    [InlineData("marker-reparse")]
    [InlineData("staged-directory")]
    [InlineData("staged-reparse")]
    public async Task Builder_FailsClosedForInvalidOwnerMarkerMatrix(
        string scenario)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot(
            $"staged-owner-invalid-{scenario}");
        string? reparsePath = null;

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, firstBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var originalZipHash = ComputeSha256(zipPath);
            var transactionId = Guid.NewGuid().ToString("N");
            var markerPath = Path.Combine(
                adminOutputRoot,
                $".georaeplan-package-staged-owner.{transactionId}.json");
            var stagedName =
                $".거래플랜-PC-설치패키지.{transactionId}.staged.zip";
            var stagedPath = Path.Combine(adminOutputRoot, stagedName);
            var markerJson = CreateOwnerMarkerJson(
                1,
                transactionId,
                "거래플랜-PC-설치패키지",
                stagedName);

            switch (scenario)
            {
                case "schema":
                    markerJson = CreateOwnerMarkerJson(
                        2,
                        transactionId,
                        "거래플랜-PC-설치패키지",
                        stagedName);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "schema evidence");
                    break;
                case "schema-casing":
                    markerJson = markerJson.Replace(
                        "\"SchemaVersion\"",
                        "\"schemaVersion\"",
                        StringComparison.Ordinal);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "schema casing evidence");
                    break;
                case "guid":
                    markerJson = CreateOwnerMarkerJson(
                        1,
                        Guid.NewGuid().ToString("N"),
                        "거래플랜-PC-설치패키지",
                        stagedName);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "guid evidence");
                    break;
                case "guid-casing":
                    markerJson = markerJson.Replace(
                        "\"TransactionId\"",
                        "\"transactionId\"",
                        StringComparison.Ordinal);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "guid casing evidence");
                    break;
                case "package":
                    markerJson = CreateOwnerMarkerJson(
                        1,
                        transactionId,
                        "다른-패키지",
                        stagedName);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "package evidence");
                    break;
                case "package-casing":
                    markerJson = markerJson.Replace(
                        "\"PackageName\"",
                        "\"packageName\"",
                        StringComparison.Ordinal);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "package casing evidence");
                    break;
                case "staged-name":
                    markerJson = CreateOwnerMarkerJson(
                        1,
                        transactionId,
                        "거래플랜-PC-설치패키지",
                        ".unexpected.staged.zip");
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "name evidence");
                    break;
                case "staged-name-casing":
                    markerJson = markerJson.Replace(
                        "\"StagedZipName\"",
                        "\"stagedZipName\"",
                        StringComparison.Ordinal);
                    File.WriteAllText(markerPath, markerJson);
                    File.WriteAllText(stagedPath, "name casing evidence");
                    break;
                case "marker-directory":
                    Directory.CreateDirectory(markerPath);
                    File.WriteAllText(stagedPath, "marker dir evidence");
                    break;
                case "marker-reparse":
                    var markerTarget = Path.Combine(
                        testRoot,
                        "marker-target");
                    Directory.CreateDirectory(markerTarget);
                    CreateDirectoryJunction(markerPath, markerTarget);
                    reparsePath = markerPath;
                    File.WriteAllText(stagedPath, "marker link evidence");
                    break;
                case "staged-directory":
                    File.WriteAllText(markerPath, markerJson);
                    Directory.CreateDirectory(stagedPath);
                    break;
                case "staged-reparse":
                    File.WriteAllText(markerPath, markerJson);
                    var stagedTarget = Path.Combine(
                        testRoot,
                        "staged-target");
                    Directory.CreateDirectory(stagedTarget);
                    CreateDirectoryJunction(stagedPath, stagedTarget);
                    reparsePath = stagedPath;
                    break;
                default:
                    Assert.Fail($"Unknown matrix scenario: {scenario}");
                    break;
            }

            var recovery = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.NotEqual(0, recovery.ExitCode);
            Assert.True(
                File.Exists(markerPath) || Directory.Exists(markerPath));
            Assert.True(
                File.Exists(stagedPath) || Directory.Exists(stagedPath));
            Assert.Equal(originalZipHash, ComputeSha256(zipPath));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(reparsePath) &&
                Directory.Exists(reparsePath))
            {
                Directory.Delete(reparsePath);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_PreservesAllOwnedStagedZipsWhenAnyOwnerIsMalformed()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("staged-owner-multiple-malformed");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, firstBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var validId = "00000000000040008000000000000001";
            var invalidId = "ffffffffffff4fff8fffffffffffffff";
            var validStagedName =
                $".거래플랜-PC-설치패키지.{validId}.staged.zip";
            var invalidStagedName =
                $".거래플랜-PC-설치패키지.{invalidId}.staged.zip";
            var validMarkerPath = Path.Combine(
                adminOutputRoot,
                $".georaeplan-package-staged-owner.{validId}.json");
            var invalidMarkerPath = Path.Combine(
                adminOutputRoot,
                $".georaeplan-package-staged-owner.{invalidId}.json");
            var validStagedPath = Path.Combine(
                adminOutputRoot,
                validStagedName);
            var invalidStagedPath = Path.Combine(
                adminOutputRoot,
                invalidStagedName);
            File.WriteAllText(
                validMarkerPath,
                CreateOwnerMarkerJson(
                    1,
                    validId,
                    "거래플랜-PC-설치패키지",
                    validStagedName));
            File.WriteAllText(invalidMarkerPath, "{malformed");
            File.WriteAllText(validStagedPath, "valid owner evidence");
            File.WriteAllText(invalidStagedPath, "invalid owner evidence");

            var recovery = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.NotEqual(0, recovery.ExitCode);
            Assert.True(File.Exists(validMarkerPath));
            Assert.True(File.Exists(invalidMarkerPath));
            Assert.Equal(
                "valid owner evidence",
                File.ReadAllText(validStagedPath));
            Assert.Equal(
                "invalid owner evidence",
                File.ReadAllText(invalidStagedPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RecoversFirstPublishAfterHardKill()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("first-hard-kill-recovery");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_ZIP_REPLACE",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            Assert.True(File.Exists(Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json")));

            var recoveredBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.True(
                recoveredBuild.ExitCode == 0,
                recoveredBuild.StdOut + Environment.NewLine +
                recoveredBuild.StdErr);
            Assert.Contains(
                "package_zip_restore=SUCCESS",
                recoveredBuild.StdOut,
                StringComparison.Ordinal);
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            ReadAndAssertSha256Sidecar(zipPath);
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_CommitsForwardAfterHardKillFollowingSidecarPublish()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("sidecar-hard-kill-commit-forward");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "commit-forward.txt"),
                "consistent replacement pair");

            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_SIDECAR_PUBLISH",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var committedZipHash = ComputeSha256(zipPath);
            var committedSidecar =
                ReadAndAssertSha256Sidecar(zipPath);
            Assert.True(File.Exists(Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json")));

            var recoveryOnlyBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_EXIT_AFTER_STARTUP_RECOVERY",
                        "1")
                ]);
            Assert.True(
                recoveryOnlyBuild.ExitCode == 0,
                recoveryOnlyBuild.StdOut + Environment.NewLine +
                recoveryOnlyBuild.StdErr);
            Assert.Contains(
                "package_publish_recovery=COMMIT_FORWARD",
                recoveryOnlyBuild.StdOut,
                StringComparison.Ordinal);
            Assert.Contains(
                "startup_recovery_only=COMMIT_FORWARD",
                recoveryOnlyBuild.StdOut,
                StringComparison.Ordinal);
            Assert.Equal(committedZipHash, ComputeSha256(zipPath));
            Assert.Equal(
                committedSidecar,
                ReadAndAssertSha256Sidecar(zipPath));
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_FailsClosedAndPreservesEvidenceWhenJournalIsTampered()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("journal-tamper");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "journal-tamper.txt"),
                "replacement candidate");
            var killedBuild = await RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_ZIP_REPLACE",
                        "1")
                ]);
            Assert.NotEqual(0, killedBuild.ExitCode);

            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var markerPath = Path.Combine(
                adminOutputRoot,
                ".georaeplan-package-publish-transaction.json");
            var markerJson = File.ReadAllText(markerPath, Encoding.UTF8);
            const string hashPrefix = "\"NewZipHash\":\"";
            var hashStart = markerJson.IndexOf(
                hashPrefix,
                StringComparison.Ordinal);
            Assert.True(hashStart >= 0);
            hashStart += hashPrefix.Length;
            var originalJournalHash = markerJson.Substring(hashStart, 64);
            var tamperedJournalHash =
                (originalJournalHash[0] == '0' ? "1" : "0") +
                originalJournalHash[1..];
            File.WriteAllText(
                markerPath,
                markerJson[..hashStart] +
                tamperedJournalHash +
                markerJson[(hashStart + 64)..],
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));

            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var sidecarPath = zipPath + ".sha256.txt";
            var previousZipPath = Assert.Single(
                Directory.EnumerateFiles(
                    adminOutputRoot,
                    ".*.previous.zip",
                    SearchOption.TopDirectoryOnly));
            var markerHashBeforeRecovery = ComputeSha256(markerPath);
            var zipHashBeforeRecovery = ComputeSha256(zipPath);
            var sidecarHashBeforeRecovery = ComputeSha256(sidecarPath);
            var backupHashBeforeRecovery =
                ComputeSha256(previousZipPath);

            var failedRecovery = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.NotEqual(0, failedRecovery.ExitCode);
            Assert.Contains(
                "is not journal-owned",
                failedRecovery.StdOut + failedRecovery.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(
                markerHashBeforeRecovery,
                ComputeSha256(markerPath));
            Assert.Equal(
                zipHashBeforeRecovery,
                ComputeSha256(zipPath));
            Assert.Equal(
                sidecarHashBeforeRecovery,
                ComputeSha256(sidecarPath));
            Assert.Equal(
                backupHashBeforeRecovery,
                ComputeSha256(previousZipPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RollsBackWhenExistingSidecarIsLocked()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("locked-sidecar");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            var adminOutputRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용");
            var zipPath = Path.Combine(
                adminOutputRoot,
                "거래플랜-PC-설치패키지.zip");
            var sidecarPath = zipPath + ".sha256.txt";
            var originalZipHash = ComputeSha256(zipPath);
            var originalSidecarContent =
                ReadAndAssertSha256Sidecar(zipPath);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "locked-sidecar-change.txt"),
                "replacement candidate");

            ProcessResult lockedBuild;
            using (var sidecarLease = new FileStream(
                       sidecarPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                lockedBuild = await RunBuilderAsync(
                    scriptPath,
                    fixture);
                Assert.NotEqual(0, lockedBuild.ExitCode);
                Assert.Contains(
                    "package_zip_restore=SUCCESS",
                    lockedBuild.StdOut + lockedBuild.StdErr,
                    StringComparison.Ordinal);
                Assert.Equal(originalZipHash, ComputeSha256(zipPath));
                Assert.Equal(
                    originalSidecarContent,
                    File.ReadAllText(
                        sidecarPath,
                        Encoding.UTF8).TrimEnd('\r', '\n'));
            }
            AssertNoPackageTransactionResidue(adminOutputRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RejectsExplicitSourceRootJunctionBeforeCreatingOutput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("source-root-junction");
        string? junctionPath = null;

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            junctionPath = fixture.SourceFolder;
            var sourceTarget = Path.Combine(testRoot, "source-target");
            Directory.Move(junctionPath, sourceTarget);
            CreateDirectoryJunction(junctionPath, sourceTarget);

            var failedBuild = await RunBuilderAsync(scriptPath, fixture);

            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "SourceFolder path chain must not contain a reparse point",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "package_ready",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(fixture.OutputRoot));
        }
        finally
        {
            if (junctionPath is not null &&
                Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RejectsNestedExplicitSourceJunctionBeforeMutatingOutput()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("nested-source-junction");
        string? junctionPath = null;

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var outsideRoot = Path.Combine(testRoot, "outside-source");
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(
                Path.Combine(outsideRoot, "outside.txt"),
                "must not be packaged");
            junctionPath = Path.Combine(
                fixture.SourceFolder,
                "linked-outside");
            CreateDirectoryJunction(junctionPath, outsideRoot);

            Directory.CreateDirectory(fixture.OutputRoot);
            var outputMarker = Path.Combine(
                fixture.OutputRoot,
                "must-survive.txt");
            File.WriteAllText(outputMarker, "existing output marker");

            var failedBuild = await RunBuilderAsync(scriptPath, fixture);

            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "SourceFolder directory tree must not contain a reparse point",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "package_ready",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(
                "existing output marker",
                File.ReadAllText(outputMarker));
            Assert.False(Directory.Exists(Path.Combine(
                fixture.OutputRoot,
                "관리자용")));
        }
        finally
        {
            if (junctionPath is not null &&
                Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RejectsOutputRootAncestorJunctionWithoutTouchingTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("output-ancestor-junction");
        var junctionPath = Path.Combine(testRoot, "output-link");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var outsideRoot = Path.Combine(testRoot, "outside");
            Directory.CreateDirectory(outsideRoot);
            var outsideMarker = Path.Combine(
                outsideRoot,
                "must-survive.txt");
            File.WriteAllText(outsideMarker, "outside marker");
            CreateDirectoryJunction(junctionPath, outsideRoot);
            fixture = fixture with
            {
                OutputRoot = Path.Combine(junctionPath, "output")
            };

            var failedBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "path chain must not contain a reparse point",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.Equal("outside marker", File.ReadAllText(outsideMarker));
            Assert.False(Directory.Exists(Path.Combine(
                outsideRoot,
                "output")));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_RejectsNestedPackageJunctionWithoutTouchingTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("nested-package-junction");
        string? junctionPath = null;

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuild = await RunBuilderAsync(scriptPath, fixture);
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            var outsideRoot = Path.Combine(testRoot, "outside");
            Directory.CreateDirectory(outsideRoot);
            var outsideMarker = Path.Combine(
                outsideRoot,
                "must-survive.txt");
            File.WriteAllText(outsideMarker, "outside marker");
            junctionPath = Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지",
                "linked-outside");
            CreateDirectoryJunction(junctionPath, outsideRoot);

            var failedBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.NotEqual(0, failedBuild.ExitCode);
            Assert.Contains(
                "contains a reparse point and cannot be removed recursively",
                failedBuild.StdOut + failedBuild.StdErr,
                StringComparison.Ordinal);
            Assert.Equal("outside marker", File.ReadAllText(outsideMarker));
        }
        finally
        {
            if (junctionPath is not null &&
                Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_FailsClosedWhenAnotherWriterOwnsTheOutputRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("concurrent-output-lock");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var firstBuildTask = RunBuilderAsync(
                scriptPath,
                fixture,
                enableTestHooks: true,
                extraArgumentsAndEnvironment:
                [
                    (
                        "GEORAEPLAN_PACKAGE_TEST_HOLD_LOCK_MILLISECONDS",
                        "5000")
                ]);
            var lockPath = Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                ".georaeplan-package-builder.lock");
            await WaitForExclusiveFileLockAsync(
                lockPath,
                TimeSpan.FromSeconds(10));

            var secondBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.NotEqual(0, secondBuild.ExitCode);
            Assert.Contains(
                "Another package builder already owns this output root",
                secondBuild.StdOut + secondBuild.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "package_ready",
                secondBuild.StdOut + secondBuild.StdErr,
                StringComparison.Ordinal);

            var firstBuild = await firstBuildTask;
            Assert.True(
                firstBuild.ExitCode == 0,
                firstBuild.StdOut + Environment.NewLine +
                firstBuild.StdErr);
            Assert.Contains(
                "package_builder_lock=ACQUIRED",
                firstBuild.StdOut,
                StringComparison.Ordinal);
            Assert.Contains(
                "package_builder_lock=RELEASED",
                firstBuild.StdOut,
                StringComparison.Ordinal);
            Assert.Contains(
                "package_ready",
                firstBuild.StdOut,
                StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지.zip")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_IsolatesUpdaterPublishAcrossDifferentOutputRoots()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot(
            "concurrent-updater-publish");

        try
        {
            var firstFixture = CreateBuildFixture(
                testRoot,
                includeUpdaterProject: true);
            var secondFixture = firstFixture with
            {
                OutputRoot = Path.Combine(testRoot, "output-b")
            };

            var firstBuildTask = RunBuilderAsync(
                scriptPath,
                firstFixture);
            var secondBuildTask = RunBuilderAsync(
                scriptPath,
                secondFixture);
            var buildResults = await Task.WhenAll(
                firstBuildTask,
                secondBuildTask);
            foreach (var result in buildResults)
            {
                Assert.True(
                    result.ExitCode == 0,
                    result.StdOut + Environment.NewLine +
                    result.StdErr);
            }

            static string ReadPublishedUpdater(
                BuildFixture fixture)
                => File.ReadAllText(Path.Combine(
                    fixture.OutputRoot,
                    "관리자용",
                    "거래플랜-PC-설치패키지",
                    "App",
                    "Updater",
                    "거래플랜.Updater.exe"));

            var firstUpdater = ReadPublishedUpdater(firstFixture);
            var secondUpdater = ReadPublishedUpdater(secondFixture);
            Assert.Contains(
                "georaeplan-updater-publish-",
                firstUpdater,
                StringComparison.Ordinal);
            Assert.Contains(
                "georaeplan-updater-publish-",
                secondUpdater,
                StringComparison.Ordinal);
            Assert.NotEqual(firstUpdater, secondUpdater);
            Assert.Empty(
                Directory.EnumerateDirectories(
                    firstFixture.TempRoot,
                    "georaeplan-updater-publish-*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_EscapesDisplayNameAndParsesGeneratedPowerShell()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("display-name-literal");
        const string appDisplayName =
            "O'Brien__EXPECTED_VERSION____REMOVE_SHORTCUT_SUFFIX__";

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var build = await RunBuilderAsync(
                scriptPath,
                fixture,
                appDisplayName: appDisplayName);
            Assert.True(
                build.ExitCode == 0,
                build.StdOut + Environment.NewLine + build.StdErr);

            var packageRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지");
            var appRoot = Path.Combine(packageRoot, "App");
            Assert.True(File.Exists(Path.Combine(
                appRoot,
                appDisplayName + ".exe")));
            Assert.True(File.Exists(Path.Combine(
                appRoot,
                "거래플랜.Desktop.App.exe")));
            Assert.True(File.Exists(Path.Combine(
                appRoot,
                "Updater",
                appDisplayName + ".Updater.exe")));
            Assert.True(File.Exists(Path.Combine(
                appRoot,
                "Updater",
                "거래플랜.Updater.exe")));

            var installScriptPath = Path.Combine(
                packageRoot,
                "Install-GeoraePlan.ps1");
            var installScript = File.ReadAllText(installScriptPath);
            const string escapedDisplayName =
                "O''Brien__EXPECTED_VERSION____REMOVE_SHORTCUT_SUFFIX__";
            Assert.Contains(
                escapedDisplayName,
                installScript,
                StringComparison.Ordinal);

            var uninstallScript = DecodeEmbeddedUninstaller(installScript);
            Assert.Contains(
                escapedDisplayName,
                uninstallScript,
                StringComparison.Ordinal);
            var uninstallScriptPath = Path.Combine(
                testRoot,
                "generated-uninstaller-parse-probe.ps1");
            File.WriteAllText(
                uninstallScriptPath,
                uninstallScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var installParse = await RunPowerShellParserAsync(
                installScriptPath,
                testRoot);
            Assert.True(
                installParse.ExitCode == 0,
                installParse.StdOut + Environment.NewLine +
                installParse.StdErr);
            var uninstallParse = await RunPowerShellParserAsync(
                uninstallScriptPath,
                testRoot);
            Assert.True(
                uninstallParse.ExitCode == 0,
                uninstallParse.StdOut + Environment.NewLine +
                uninstallParse.StdErr);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LaunchCommand_IgnoresUnrelatedRootExecutables()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("launcher-multiple-exes");

        try
        {
            var fixture = CreateBuildFixture(testRoot);
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "000-helper.exe"),
                "unrelated executable");
            File.WriteAllText(
                Path.Combine(fixture.SourceFolder, "MaintenanceTool.exe"),
                "another unrelated executable");
            var collidingDesktopAppPath = Path.Combine(
                fixture.SourceFolder,
                "000.Desktop.App.exe");
            File.WriteAllText(
                collidingDesktopAppPath,
                "unintended same-pattern executable");

            var collidingBuild = await RunBuilderAsync(
                scriptPath,
                fixture);
            Assert.NotEqual(0, collidingBuild.ExitCode);
            Assert.Contains(
                "exactly one canonical *.Desktop.App.exe",
                collidingBuild.StdOut + collidingBuild.StdErr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "package_ready",
                collidingBuild.StdOut + collidingBuild.StdErr,
                StringComparison.Ordinal);

            File.Delete(collidingDesktopAppPath);
            var build = await RunBuilderAsync(scriptPath, fixture);
            Assert.Equal(0, build.ExitCode);

            var appRoot = Path.Combine(
                fixture.OutputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지",
                "App");
            var launchCommandPath = Path.Combine(appRoot, "앱실행.cmd");
            var launchCommand = File.ReadAllText(
                launchCommandPath,
                Encoding.ASCII);
            Assert.DoesNotContain(
                "\"%~dp0*.exe\"",
                launchCommand,
                StringComparison.OrdinalIgnoreCase);

            var selectionProbePath = Path.Combine(
                appRoot,
                "launcher-selection-probe.cmd");
            var selectedPathOutput = Path.Combine(
                appRoot,
                "launcher-selected-path.txt");
            var selectionProbe = launchCommand.Replace(
                "start \"\" \"%APP_EXE%\"",
                ">\"%~dp0launcher-selected-path.txt\" echo %APP_EXE%",
                StringComparison.Ordinal);
            File.WriteAllText(
                selectionProbePath,
                selectionProbe,
                Encoding.ASCII);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName =
                    Environment.GetEnvironmentVariable("ComSpec") ??
                    "cmd.exe",
                Arguments = $"/d /c \"\"{selectionProbePath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Assert.NotNull(process);
            await process!.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            var selectedExecutablePath =
                File.ReadAllText(selectedPathOutput).Trim();
            Assert.True(
                selectedExecutablePath.EndsWith(
                    ".Desktop.App.exe",
                    StringComparison.OrdinalIgnoreCase),
                $"Unexpected launcher selection: {selectedExecutablePath}");
            Assert.DoesNotContain(
                "helper.exe",
                selectedExecutablePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "MaintenanceTool.exe",
                selectedExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void BuilderSource_UsesFailFastAndValidatedStagedArchiveReplacement()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1"));
        var launchCommandSource = ExtractPowerShellScriptSection(
            source,
            "function Ensure-DesktopLaunchCommand {",
            "function Ensure-DesktopPackageLaunchFiles {");

        Assert.Contains(
            "$ErrorActionPreference = 'Stop'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-SingleFileName",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Get-ContainedDirectChildPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-DesktopPackageArchive",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-PowerShellScriptSyntax",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConvertTo-PowerShellSingleQuotedLiteralContent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.File]::Replace(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'App/Updater/거래플랜.Updater.exe'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'for %%I in (\"%~dp0*.Desktop.App.exe\") do if exist \"%%~fI\"'",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if not defined APP_EXE for %%I in (\"%~dp0*.exe\")",
            launchCommandSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Updater\\거래플랜.Updater.exe'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "package_zip_restore=SUCCESS",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Publish-Sha256Sidecar",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Open-PackagePublishTransactionMarkerLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Invoke-PackageStagedZipOwnerRecovery",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Open-PackageStagedZipOwnerMarkerLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.FileShare]::Read)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-MarkerStream $markerLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedMarkerHash $expectedMarkerHash",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_DURABLE_TRANSACTION_WRITE",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Remove-DirectoryTreeWithoutFollowingReparsePoints",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-DirectoryTreeHasNoReparsePoints",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "/MIR /XJ /R:2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_PACKAGE_TEST_KILL_AFTER_SIDECAR_PUBLISH",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Write-Host \"package_zip_sha256=",
            source,
            StringComparison.Ordinal);

        var explicitSourceValidationIndex = source.IndexOf(
            "if ($hasExplicitSourceFolder) {",
            StringComparison.Ordinal);
        var outputMutationIndex = source.IndexOf(
            "New-Item -ItemType Directory -Force -Path $OutputRoot",
            StringComparison.Ordinal);
        Assert.True(explicitSourceValidationIndex >= 0);
        Assert.True(outputMutationIndex >= 0);
        Assert.True(
            explicitSourceValidationIndex < outputMutationIndex,
            "Explicit SourceFolder validation must run before output mutation.");

        var stageIndex = source.IndexOf(
            "Compress-Archive `",
            StringComparison.Ordinal);
        var ownerWriteIndex = source.IndexOf(
            "Write-PackageStagedZipOwnerMarker `",
            StringComparison.Ordinal);
        var validationIndex = source.IndexOf(
            "Assert-DesktopPackageArchive `",
            stageIndex,
            StringComparison.Ordinal);
        var durableTransactionWriteIndex = source.IndexOf(
            "Write-PackagePublishTransaction `",
            validationIndex,
            StringComparison.Ordinal);
        var ownerReleaseIndex = source.IndexOf(
            "Remove-PackageStagedZipOwnerMarker `",
            durableTransactionWriteIndex,
            StringComparison.Ordinal);
        var replacementIndex = source.IndexOf(
            "[System.IO.File]::Replace(",
            ownerReleaseIndex,
            StringComparison.Ordinal);
        var readyIndex = source.IndexOf(
            "Write-Host \"package_ready",
            replacementIndex,
            StringComparison.Ordinal);
        var durableStartupRecoveryIndex = source.IndexOf(
            "$startupRecoveryResult = Invoke-PackagePublishTransactionRecovery",
            StringComparison.Ordinal);
        var ownerStartupRecoveryIndex = source.IndexOf(
            "$startupOwnerRecoveryResult = Invoke-PackageStagedZipOwnerRecovery",
            StringComparison.Ordinal);
        Assert.True(ownerWriteIndex >= 0);
        Assert.True(stageIndex >= 0);
        Assert.True(ownerWriteIndex < stageIndex);
        Assert.True(validationIndex > stageIndex);
        Assert.True(durableTransactionWriteIndex > validationIndex);
        Assert.True(ownerReleaseIndex > durableTransactionWriteIndex);
        Assert.True(replacementIndex > validationIndex);
        Assert.True(replacementIndex > ownerReleaseIndex);
        Assert.True(readyIndex > replacementIndex);
        Assert.True(durableStartupRecoveryIndex >= 0);
        Assert.True(
            ownerStartupRecoveryIndex > durableStartupRecoveryIndex);
    }

    [Fact]
    public async Task ReleaseZipValidators_RejectCanonicalWindowsCollisionsAndResourceAbuse()
    {
        var repositoryRoot = FindRepositoryRoot();
        var builderSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1"));
        var publisherSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1"));
        var builderSha256StreamHelper = ExtractPowerShellScriptSection(
            builderSource,
            "function Get-Sha256FromStream {",
            "function Get-Sha256FromBytes {");
        var builderValidator = ExtractPowerShellScriptSection(
            builderSource,
            "function ConvertTo-SafeDesktopArchiveRelativePath {",
            "function ConvertTo-DesktopExecutableProductVersionCore {");
        var builderExecutableIdentityValidator =
            ExtractPowerShellScriptSection(
                builderSource,
                "function ConvertTo-DesktopExecutableProductVersionCore {",
                "function Assert-DesktopPackageArchive {");
        var publisherValidator = ExtractPowerShellScriptSection(
            publisherSource,
            "function ConvertTo-SafeDesktopArchiveRelativePath {",
            "function Copy-DesktopArchiveEntryToBoundedFile {");

        Assert.Equal(builderValidator, publisherValidator);
        Assert.Contains("$maximumPackageFileSize = 512MB", builderValidator, StringComparison.Ordinal);
        Assert.Contains("$maximumEntryCount = 10000", builderValidator, StringComparison.Ordinal);
        Assert.Contains("$maximumEntryUncompressedSize = 512MB", builderValidator, StringComparison.Ordinal);
        Assert.Contains("$maximumTotalUncompressedSize = 2GB", builderValidator, StringComparison.Ordinal);
        Assert.Contains("Read-DesktopArchiveEntryActualLengthBounded", builderValidator, StringComparison.Ordinal);
        Assert.Contains("length does not match ZIP metadata", builderValidator, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::OrdinalIgnoreCase", builderValidator, StringComparison.Ordinal);

        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-zip-validator-tests",
            Guid.NewGuid().ToString("N"));
        var harnessPath = Path.Combine(testRoot, "validate-release-zip.ps1");
        try
        {
            Directory.CreateDirectory(testRoot);
            File.WriteAllText(
                harnessPath,
                $$"""
                param(
                    [Parameter(Mandatory = $true)]
                    [string]$CanonicalExecutablePath,
                    [Parameter(Mandatory = $true)]
                    [string]$DifferentVersionExecutablePath
                )

                $ErrorActionPreference = 'Stop'
                {{builderSha256StreamHelper}}
                {{builderValidator}}
                {{builderExecutableIdentityValidator}}

                function New-TestEntry(
                    [string]$Name,
                    [long]$Length = 1,
                    [int]$ExternalAttributes = 0
                ) {
                    $entry = [pscustomobject]@{
                        FullName = $Name
                        Length = $Length
                        ExternalAttributes = $ExternalAttributes
                        Bytes = New-Object byte[] ([int][Math]::Min(
                            [Math]::Max($Length, 0),
                            1))
                    }
                    $entry | Add-Member ScriptMethod Open {
                        return [IO.MemoryStream]::new(
                            [byte[]]$this.Bytes,
                            $false)
                    }
                    return $entry
                }

                function New-TestActualLengthEntry(
                    [string]$Name,
                    [long]$DeclaredLength,
                    [byte[]]$Bytes
                ) {
                    $entry = [pscustomobject]@{
                        FullName = $Name
                        Length = $DeclaredLength
                        ExternalAttributes = 0
                        Bytes = $Bytes
                    }
                    $entry | Add-Member ScriptMethod Open {
                        return [IO.MemoryStream]::new(
                            [byte[]]$this.Bytes,
                            $false)
                    }
                    return $entry
                }

                function New-TestTextEntry([string]$Name, [string]$Content) {
                    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
                    $entry = [pscustomobject]@{
                        FullName = $Name
                        Length = [long]$bytes.Length
                        ExternalAttributes = 0
                        Bytes = $bytes
                    }
                    $entry | Add-Member ScriptMethod Open {
                        return [IO.MemoryStream]::new(
                            [byte[]]$this.Bytes,
                            $false)
                    }
                    return $entry
                }

                function New-TestBinaryEntry(
                    [string]$Name,
                    [byte[]]$Bytes
                ) {
                    $entry = [pscustomobject]@{
                        FullName = $Name
                        Length = [long]$Bytes.Length
                        ExternalAttributes = 0
                        Bytes = $Bytes
                    }
                    $entry | Add-Member ScriptMethod Open {
                        return [IO.MemoryStream]::new(
                            [byte[]]$this.Bytes,
                            $false)
                    }
                    return $entry
                }

                function Assert-Rejected(
                    [string]$Scenario,
                    [object[]]$Entries,
                    [long]$PackageFileSize = 1
                ) {
                    $archive = [pscustomobject]@{ Entries = $Entries }
                    try {
                        Get-ValidatedDesktopArchiveEntryMap `
                            -Archive $archive `
                            -PackageFileSize $PackageFileSize | Out-Null
                    }
                    catch {
                        return
                    }
                    throw "unsafe archive was accepted: $Scenario"
                }

                $validArchive = [pscustomobject]@{
                    Entries = @(
                        (New-TestEntry 'App/' 0),
                        (New-TestEntry 'App/file.exe' 1)
                    )
                }
                $valid = Get-ValidatedDesktopArchiveEntryMap `
                    -Archive $validArchive `
                    -PackageFileSize 1
                if ($valid.Count -ne 2) {
                    throw 'valid archive contract was rejected'
                }

                Assert-Rejected 'dot segment' @(
                    (New-TestEntry 'App/file.exe'),
                    (New-TestEntry 'App/./file.exe'))
                Assert-Rejected 'parent segment' @(
                    (New-TestEntry 'App/file.exe'),
                    (New-TestEntry 'App/sub/../file.exe'))
                Assert-Rejected 'separator alias' @(
                    (New-TestEntry 'App/file.exe'),
                    (New-TestEntry 'App\file.exe'))
                Assert-Rejected 'case alias' @(
                    (New-TestEntry 'App/file.exe'),
                    (New-TestEntry 'app/FILE.exe'))
                Assert-Rejected 'repeated separator' @(
                    (New-TestEntry 'App//file.exe'))
                Assert-Rejected 'rooted path' @(
                    (New-TestEntry '/App/file.exe'))
                Assert-Rejected 'colon' @(
                    (New-TestEntry 'App/C:file.exe'))
                Assert-Rejected 'trailing dot' @(
                    (New-TestEntry 'App/file.'))
                Assert-Rejected 'trailing space' @(
                    (New-TestEntry 'App/file '))
                Assert-Rejected 'reserved device' @(
                    (New-TestEntry 'App/CON.txt'))
                Assert-Rejected 'reserved clock device' @(
                    (New-TestEntry 'App/CLOCK$.txt'))
                Assert-Rejected 'reserved superscript device' @(
                    (New-TestEntry 'App/COM¹.txt'))
                Assert-Rejected 'Windows reparse attribute' @(
                    (New-TestEntry 'App/reparse.exe' 1 1024))
                Assert-Rejected 'Unix symbolic link mode' @(
                    (New-TestEntry 'App/symlink.exe' 1 -1610612736))
                Assert-Rejected 'file ancestor' @(
                    (New-TestEntry 'App'),
                    (New-TestEntry 'App/file.exe'))
                Assert-Rejected 'implicit directory replaced by file' @(
                    (New-TestEntry 'App/file.exe'),
                    (New-TestEntry 'App'))
                $tooLongSegment = (('a' * 256) -join '')
                $maximumSegment = (('a' * 255) -join '')
                $tooLongPath = ((1..5 | ForEach-Object { $maximumSegment }) -join '/')
                Assert-Rejected 'segment limit' @(
                    (New-TestEntry ($tooLongSegment + '/file')))
                Assert-Rejected 'path limit' @(
                    (New-TestEntry $tooLongPath))
                Assert-Rejected 'package size limit' @(
                    (New-TestEntry 'file')) 536870913
                Assert-Rejected 'entry size limit' @(
                    (New-TestEntry 'file' 536870913))
                Assert-Rejected 'underreported actual entry length' @(
                    (New-TestActualLengthEntry `
                        'App/hidden-payload.bin' `
                        1 `
                        ([byte[]](1, 2))))
                Assert-Rejected 'total size limit' @(
                    (New-TestEntry 'one' 524288000),
                    (New-TestEntry 'two' 524288000),
                    (New-TestEntry 'three' 524288000),
                    (New-TestEntry 'four' 524288000),
                    (New-TestEntry 'five' 524288000))
                $manyEntries = @(
                    for ($index = 0; $index -le 10000; $index++) {
                        New-TestEntry "files/$index"
                    }
                )
                Assert-Rejected 'entry count limit' $manyEntries

                $validInstallerScript = '[CmdletBinding()] param($InstallRoot,$NoLaunch,$SuppressUi,$WorkerTimeoutSeconds,$LogPath,$RecoveryOnly,$LegacyBridgeCopy,$UpdaterOwnsInstallRootGate,$InstallRootGateOwnerProcessId,$InstallRootGateOwnerProcessPath,$InstallRootGateOwnerProcessStartTimeUtcTicks) # GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1 GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1'
                $validScripts = @{
                    'Install-GeoraePlan.ps1' = New-TestTextEntry `
                        'Install-GeoraePlan.ps1' $validInstallerScript
                    'App/앱실행.cmd' = New-TestTextEntry `
                        'App/앱실행.cmd' `
                        'for %%I in ("%~dp0*.Desktop.App.exe") do echo %%I`r`nstart "" "%APP_EXE%"'
                    '거래플랜-설치.cmd' = New-TestTextEntry `
                        '거래플랜-설치.cmd' `
                        'powershell -ExecutionPolicy Bypass -File "%~dp0Install-GeoraePlan.ps1"'
                }
                Assert-DesktopArchiveScriptContract -Entries $validScripts

                $missingParameterScripts = @{} + $validScripts
                $missingParameterScripts['Install-GeoraePlan.ps1'] =
                    New-TestTextEntry `
                        'Install-GeoraePlan.ps1' `
                        $validInstallerScript.Replace(
                            ',$LegacyBridgeCopy',
                            '')
                try {
                    Assert-DesktopArchiveScriptContract `
                        -Entries $missingParameterScripts
                    throw 'installer missing updater parameter was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq
                        'installer missing updater parameter was accepted') {
                        throw
                    }
                    if (-not $_.Exception.Message.Contains(
                            'missing updater parameter: LegacyBridgeCopy')) {
                        throw
                    }
                }

                $missingMarkerScripts = @{} + $validScripts
                $missingMarkerScripts['Install-GeoraePlan.ps1'] =
                    New-TestTextEntry `
                        'Install-GeoraePlan.ps1' `
                        $validInstallerScript.Replace(
                            'GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1',
                            '')
                try {
                    Assert-DesktopArchiveScriptContract `
                        -Entries $missingMarkerScripts
                    throw 'installer missing updater marker was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq
                        'installer missing updater marker was accepted') {
                        throw
                    }
                    if (-not $_.Exception.Message.Contains(
                            'missing updater contract marker: GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1')) {
                        throw
                    }
                }

                $badLaunch = @{} + $validScripts
                $badLaunch['App/앱실행.cmd'] = New-TestTextEntry `
                    'App/앱실행.cmd' `
                    'for %%I in ("%~dp0*.Desktop.App.exe") do echo %%I`r`nstart "" "%APP_EXE%"`r`nif not defined APP_EXE for %%I in ("%~dp0*.exe") do echo %%I'
                try {
                    Assert-DesktopArchiveScriptContract -Entries $badLaunch
                    throw 'unsafe generic launcher fallback was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq 'unsafe generic launcher fallback was accepted') {
                        throw
                    }
                }

                $largeInstallScript = @{} + $validScripts
                $largeInstallScript['Install-GeoraePlan.ps1'] =
                    New-TestEntry 'Install-GeoraePlan.ps1' 1048577
                try {
                    Assert-DesktopArchiveScriptContract `
                        -Entries $largeInstallScript
                    throw 'oversized installer script was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq 'oversized installer script was accepted') {
                        throw
                    }
                }

                $canonicalBytes = [IO.File]::ReadAllBytes(
                    $CanonicalExecutablePath)
                $canonicalExpectedVersion =
                    ConvertTo-DesktopExecutableProductVersionCore `
                        -ProductVersion ([Diagnostics.FileVersionInfo]::GetVersionInfo(
                            $CanonicalExecutablePath).ProductVersion) `
                        -Description 'canonical fixture executable'
                $canonicalEntry = New-TestBinaryEntry `
                    'App/거래플랜.Desktop.App.exe' `
                    $canonicalBytes
                $matchingAliasEntry = New-TestBinaryEntry `
                    'App/거래플랜.exe' `
                    $canonicalBytes
                Assert-DesktopArchiveExecutableIdentity `
                    -CanonicalEntry $canonicalEntry `
                    -DisplayAliasEntry $matchingAliasEntry `
                    -ExpectedVersion $canonicalExpectedVersion
                try {
                    Assert-DesktopArchiveExecutableIdentity `
                        -CanonicalEntry $canonicalEntry `
                        -DisplayAliasEntry $matchingAliasEntry `
                        -ExpectedVersion '99.99.99'
                    throw 'package-version-mismatched desktop executable was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq
                        'package-version-mismatched desktop executable was accepted') {
                        throw
                    }
                    if (-not $_.Exception.Message.Contains(
                            'does not match the package version')) {
                        throw
                    }
                }

                $hashMismatchBytes = New-Object byte[] `
                    ($canonicalBytes.Length + 1)
                [Array]::Copy(
                    $canonicalBytes,
                    $hashMismatchBytes,
                    $canonicalBytes.Length)
                $hashMismatchBytes[$hashMismatchBytes.Length - 1] = 90
                try {
                    Assert-DesktopArchiveExecutableIdentity `
                        -CanonicalEntry $canonicalEntry `
                        -DisplayAliasEntry (New-TestBinaryEntry `
                            'App/거래플랜.exe' `
                            $hashMismatchBytes) `
                        -ExpectedVersion $canonicalExpectedVersion
                    throw 'SHA-256-mismatched desktop alias was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq
                        'SHA-256-mismatched desktop alias was accepted') {
                        throw
                    }
                    if (-not $_.Exception.Message.Contains(
                            'different SHA-256 hashes')) {
                        throw
                    }
                }

                $differentVersionBytes = [IO.File]::ReadAllBytes(
                    $DifferentVersionExecutablePath)
                try {
                    Assert-DesktopArchiveExecutableIdentity `
                        -CanonicalEntry $canonicalEntry `
                        -DisplayAliasEntry (New-TestBinaryEntry `
                            'App/거래플랜.exe' `
                            $differentVersionBytes) `
                        -ExpectedVersion $canonicalExpectedVersion
                    throw 'ProductVersion-mismatched desktop alias was accepted'
                }
                catch {
                    if ($_.Exception.Message -eq
                        'ProductVersion-mismatched desktop alias was accepted') {
                        throw
                    }
                    if (-not $_.Exception.Message.Contains(
                            'different ProductVersion core values')) {
                        throw
                    }
                }
                "release_zip_validator=PASS"
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var differentVersionExecutablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var result = await RunPowerShellAsync(
                harnessPath,
                (
                    "-CanonicalExecutablePath",
                    typeof(DesktopInstallerPackageBuilderSafetyTests)
                        .Assembly.Location),
                (
                    "-DifferentVersionExecutablePath",
                    differentVersionExecutablePath));
            Assert.True(
                result.ExitCode == 0,
                result.StdOut + Environment.NewLine + result.StdErr);
            Assert.Contains(
                "release_zip_validator=PASS",
                result.StdOut,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Builder_HoldsExtractedArchiveExecutableLeaseThroughIdentityInspection()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = CreateDDriveTestRoot("archive-exe-identity-lease");
        try
        {
            var fixture = CreateBuildFixture(testRoot);
            var readySignal = Path.Combine(testRoot, "inspection-ready.signal");
            var continueSignal = Path.Combine(testRoot, "inspection-continue.signal");
            var buildTask = RunBuilderAsync(
                scriptPath,
                fixture,
                null,
                true,
                (
                    "GEORAEPLAN_PACKAGE_TEST_ARCHIVE_EXE_INSPECTION_READY_SIGNAL",
                    readySignal),
                (
                    "GEORAEPLAN_PACKAGE_TEST_ARCHIVE_EXE_INSPECTION_CONTINUE_SIGNAL",
                    continueSignal));
            Exception? probeError = null;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (!File.Exists(readySignal) &&
                       !buildTask.IsCompleted &&
                       DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                if (buildTask.IsCompleted)
                {
                    var prematureResult = await buildTask;
                    Assert.Fail(
                        "Builder exited before the archive EXE identity lease pause." +
                        Environment.NewLine + prematureResult.StdOut +
                        Environment.NewLine + prematureResult.StdErr);
                }
                Assert.True(
                    File.Exists(readySignal),
                    "Builder did not reach the archive EXE identity lease pause.");
                var inspectionPath = Assert.Single(
                    Directory.EnumerateFiles(
                        fixture.TempRoot,
                        "entry.exe",
                        SearchOption.AllDirectories));
                Assert.Throws<IOException>(() => File.WriteAllText(
                    inspectionPath,
                    "replacement bytes"));
                Assert.Throws<IOException>(() => File.Move(
                    inspectionPath,
                    inspectionPath + ".replaced"));
            }
            catch (Exception ex)
            {
                probeError = ex;
            }
            finally
            {
                File.WriteAllText(continueSignal, "continue");
            }
            var result = await buildTask;

            Assert.True(
                result.ExitCode == 0,
                result.StdOut + Environment.NewLine + result.StdErr);
            Assert.True(probeError is null, probeError?.ToString());
            Assert.Contains(
                "package_ready root=",
                result.StdOut,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void BuilderSource_CreatesAndRetainsOwnerMarkerLeaseWithoutReopenGap()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1"));
        var ownerWriter = ExtractPowerShellScriptSection(
            source,
            "function Write-PackageStagedZipOwnerMarker {",
            "function Remove-PackageStagedZipOwnerMarker {");

        AssertInOrder(
            ownerWriter,
            "$markerLease = [System.IO.FileStream]::new(",
            "$markerPath,",
            "[System.IO.FileMode]::CreateNew,",
            "[System.IO.FileAccess]::ReadWrite,",
            "[System.IO.FileShare]::Read,",
            "[System.IO.FileOptions]::WriteThrough)",
            "$markerLease.Write($bytes, 0, $bytes.Length)",
            "$markerLease.Flush($true)",
            "$markerLease.Position = 0",
            "$validatedMarker = Get-PackageStagedZipOwnerMarker `",
            "-MarkerStream $markerLease `",
            "-ExpectedMarkerHash $expectedMarkerHash",
            "Lease = $markerLease");
        Assert.DoesNotContain(
            "Write-DurableBytes",
            ownerWriter,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Open-PackageStagedZipOwnerMarkerLease",
            ownerWriter,
            StringComparison.Ordinal);

        var leaseReturnIndex = ownerWriter.IndexOf(
            "Lease = $markerLease",
            StringComparison.Ordinal);
        var disposeIndex = ownerWriter.IndexOf(
            "$markerLease.Dispose()",
            StringComparison.Ordinal);
        Assert.True(leaseReturnIndex >= 0);
        Assert.True(
            disposeIndex < 0 || disposeIndex > leaseReturnIndex,
            "Owner marker lease must not be disposed before it is returned.");
    }

    private static BuildFixture CreateBuildFixture(
        string testRoot,
        bool includeUpdaterProject = false)
    {
        Directory.CreateDirectory(testRoot);
        var deploymentRoot = Path.Combine(testRoot, "deploy");
        Directory.CreateDirectory(deploymentRoot);
        File.WriteAllText(
            Path.Combine(deploymentRoot, "Set-ApiBaseUrl.ps1"),
            "# test deployment marker");

        var desktopProjectRoot = Path.Combine(
            testRoot,
            "Desktop",
            "거래플랜.Desktop.App");
        Directory.CreateDirectory(desktopProjectRoot);
        var versionedDesktopFixturePath =
            typeof(DesktopInstallerPackageBuilderSafetyTests)
                .Assembly.Location;
        var versionedDesktopFixtureProductVersion =
            FileVersionInfo.GetVersionInfo(
                versionedDesktopFixturePath).ProductVersion
            ?? throw new InvalidOperationException(
                "Versioned desktop fixture has no ProductVersion.");
        var desktopFixtureVersion =
            versionedDesktopFixtureProductVersion
                .Split('+')[0]
                .Split('-')[0]
                .Trim()
                .TrimStart('v', 'V');
        Assert.True(
            Version.TryParse(desktopFixtureVersion, out _),
            $"Invalid desktop fixture ProductVersion: {versionedDesktopFixtureProductVersion}");
        File.WriteAllText(
            Path.Combine(
                desktopProjectRoot,
                "거래플랜.Desktop.App.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Version>{desktopFixtureVersion}</Version>
              </PropertyGroup>
            </Project>
            """);

        var sourceFolder = Path.Combine(testRoot, "source");
        var updaterRoot = Path.Combine(sourceFolder, "Updater");
        Directory.CreateDirectory(updaterRoot);
        File.WriteAllText(
            Path.Combine(sourceFolder, "appsettings.json"),
            "{\"Api\":{\"BaseUrl\":\"https://source.example.invalid\"}}");
        File.Copy(
            versionedDesktopFixturePath,
            Path.Combine(sourceFolder, "거래플랜.Desktop.App.exe"));
        File.WriteAllText(
            Path.Combine(updaterRoot, "거래플랜.Updater.exe"),
            "updater executable");

        string fakeDotnetPath;
        if (includeUpdaterProject)
        {
            var updaterProjectRoot = Path.Combine(
                testRoot,
                "Updater",
                "거래플랜.Updater");
            Directory.CreateDirectory(updaterProjectRoot);
            File.WriteAllText(
                Path.Combine(
                    updaterProjectRoot,
                    "거래플랜.Updater.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            fakeDotnetPath = Path.Combine(
                testRoot,
                "fake-dotnet.ps1");
            File.WriteAllText(
                fakeDotnetPath,
                """
                if ($args.Count -eq 1 -and $args[0] -eq '--version') {
                    Write-Output '8.0.100'
                    $global:LASTEXITCODE = 0
                    return
                }
                $outputIndex = [Array]::IndexOf(
                    [object[]]$args,
                    '-o')
                if ($outputIndex -lt 0 -or
                    $outputIndex + 1 -ge $args.Count) {
                    $global:LASTEXITCODE = 91
                    return
                }
                $outputRoot = [string]$args[$outputIndex + 1]
                New-Item -ItemType Directory -Force -Path $outputRoot |
                    Out-Null
                Start-Sleep -Milliseconds 750
                $updaterName = [Text.Encoding]::UTF8.GetString(
                    [Convert]::FromBase64String(
                        '6rGw656Y7ZSM656cLlVwZGF0ZXIuZXhl'))
                [IO.File]::WriteAllText(
                    (Join-Path $outputRoot $updaterName),
                    "updater publish root=$outputRoot",
                    [Text.UTF8Encoding]::new($false))
                $global:LASTEXITCODE = 0
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false));
        }
        else
        {
            fakeDotnetPath = Path.Combine(
                testRoot,
                "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 91
                """);
        }

        var tempRoot = Path.Combine(testRoot, "temp");
        Directory.CreateDirectory(tempRoot);
        return new BuildFixture(
            testRoot,
            sourceFolder,
            Path.Combine(testRoot, "output"),
            fakeDotnetPath,
            tempRoot);
    }

    private static Task<ProcessResult> RunBuilderAsync(
        string scriptPath,
        BuildFixture fixture,
        string? appDisplayName = null,
        bool enableTestHooks = false,
        params (string Name, string? Value)[]
            extraArgumentsAndEnvironment)
    {
        var argumentsAndEnvironment =
            new List<(string Name, string? Value)>
            {
            ("DOTNET_EXE", fixture.FakeDotnetPath),
            ("TEMP", fixture.TempRoot),
            ("TMP", fixture.TempRoot),
            ("-ProjectRoot", fixture.ProjectRoot),
            ("-SourceFolder", fixture.SourceFolder),
            ("-OutputRoot", fixture.OutputRoot),
            ("-ApiBaseUrl", "https://package.example.invalid"),
            ("-SkipNativeInstallers", null)
        };
        if (!string.IsNullOrWhiteSpace(appDisplayName))
        {
            argumentsAndEnvironment.Add(
                ("-AppDisplayName", appDisplayName));
        }
        if (enableTestHooks)
            argumentsAndEnvironment.Add(("-EnableTestHooks", null));
        argumentsAndEnvironment.AddRange(
            extraArgumentsAndEnvironment);
        return RunPowerShellAsync(
            scriptPath,
            argumentsAndEnvironment.ToArray());
    }

    private static string DecodeEmbeddedUninstaller(
        string generatedInstallScript)
    {
        const string base64Prefix = "FromBase64String('";
        var base64Start = generatedInstallScript.IndexOf(
            base64Prefix,
            StringComparison.Ordinal);
        Assert.True(base64Start >= 0);
        base64Start += base64Prefix.Length;
        var base64End = generatedInstallScript.IndexOf(
            "')",
            base64Start,
            StringComparison.Ordinal);
        Assert.True(base64End > base64Start);
        return Encoding.UTF8.GetString(Convert.FromBase64String(
            generatedInstallScript[base64Start..base64End]));
    }

    private static Task<ProcessResult> RunPowerShellParserAsync(
        string scriptToParse,
        string testRoot)
    {
        var parserScriptPath = Path.Combine(
            testRoot,
            "parse-generated-script.ps1");
        File.WriteAllText(
            parserScriptPath,
            """
            param(
                [Parameter(Mandatory = $true)]
                [string]$Path
            )
            $tokens = $null
            $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile(
                $Path,
                [ref]$tokens,
                [ref]$errors)
            if (@($errors).Count -gt 0) {
                $errors | ForEach-Object {
                    [Console]::Error.WriteLine($_.ToString())
                }
                exit 1
            }
            Write-Host 'parse=PASS'
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return RunPowerShellAsync(
            parserScriptPath,
            ("-Path", scriptToParse));
    }

    private static async Task WaitForExclusiveFileLockAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var probe = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException)
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Exclusive package builder lock was not observed: {path}");
    }

    private static string ExtractPowerShellScriptSection(
        string source,
        string startToken,
        string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(
            endToken,
            Math.Max(start, 0),
            StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start token was not found: {startToken}");
        Assert.True(
            end > start,
            $"End token was not found after start token: {endToken}");
        return source[start..end];
    }

    private static void AssertInOrder(
        string source,
        params string[] tokens)
    {
        var previousIndex = -1;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(
                token,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(
                index >= 0,
                $"Token was not found after the previous token: {token}");
            previousIndex = index;
        }
    }

    private static async Task<string> WaitForSingleFileAsync(
        string directory,
        string pattern,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.EnumerateFiles(
                    directory,
                    pattern,
                    SearchOption.TopDirectoryOnly).ToArray();
                if (files.Length == 1)
                    return files[0];
            }
            await Task.Delay(50);
        }

        Assert.Fail(
            $"Expected one file matching '{pattern}' in '{directory}'.");
        return string.Empty;
    }

    private static string CreateOwnerMarkerJson(
        int schemaVersion,
        string transactionId,
        string packageName,
        string stagedZipName)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = schemaVersion,
            TransactionId = transactionId,
            PackageName = packageName,
            StagedZipName = stagedZipName
        });

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        params (string Name, string? Value)[] argumentsAndEnvironment)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);

        foreach (var (name, value) in argumentsAndEnvironment)
        {
            if (!name.StartsWith("-", StringComparison.Ordinal))
            {
                process.StartInfo.Environment[name] = value ?? string.Empty;
                continue;
            }

            process.StartInfo.ArgumentList.Add(name);
            if (value is not null)
                process.StartInfo.ArgumentList.Add(value);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (TimeoutException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Preserve the timeout as the primary failure.
            }
            Assert.Fail($"PowerShell script timed out: {scriptPath}");
        }
        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadAndAssertSha256Sidecar(string artifactPath)
    {
        var sidecarPath = artifactPath + ".sha256.txt";
        Assert.True(
            File.Exists(sidecarPath),
            $"SHA-256 sidecar was not published: {sidecarPath}");
        var content = File.ReadAllText(sidecarPath, Encoding.UTF8)
            .TrimEnd('\r', '\n');
        Assert.Equal(
            $"{ComputeSha256(artifactPath)} *{Path.GetFileName(artifactPath)}",
            content);
        return content;
    }

    private static void AssertNoPackageTransactionResidue(
        string adminOutputRoot)
    {
        Assert.False(File.Exists(Path.Combine(
            adminOutputRoot,
            ".georaeplan-package-publish-transaction.json")));
        Assert.False(File.Exists(Path.Combine(
            adminOutputRoot,
            ".georaeplan-package-publish-transaction.staged.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            adminOutputRoot,
            ".georaeplan-package-staged-owner.*.json",
            SearchOption.TopDirectoryOnly));
        foreach (var pattern in new[]
        {
            ".*.staged.zip",
            ".*.previous.zip",
            ".*.failed-publish.zip",
            ".*.staged.sha256.txt",
            ".*.previous.sha256.txt",
            ".*.failed-publish.sha256.txt"
        })
        {
            Assert.Empty(Directory.EnumerateFiles(
                adminOutputRoot,
                pattern,
                SearchOption.TopDirectoryOnly));
        }
    }

    private static void CreateDirectoryJunction(
        string junctionPath,
        string targetPath)
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
            ?? throw new InvalidOperationException(
                "Could not start the junction creation process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create a package safety junction. " +
                $"{standardOutput} {standardError}");
        }
    }

    private static string CreateDDriveTestRoot(string scenario)
    {
        var root = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-package-safety-tests",
            scenario,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record BuildFixture(
        string ProjectRoot,
        string SourceFolder,
        string OutputRoot,
        string FakeDotnetPath,
        string TempRoot);

    private sealed record ProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr);
}
