using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ReleaseTempPathGuardTests
{
    [Fact]
    public void DesktopAppPaths_PrefersDDriveTempAndOverridesProcessTempVariables()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "AppPaths.cs");

        Assert.Contains("private const string TempRootOverrideEnvironmentKey = \"GEORAEPLAN_TEMP_ROOT\";", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", TempRoot);", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", TempRoot);", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.Combine(_base, \"temp\")");
    }

    [Fact]
    public void DesktopUpdater_UsesAppTempDirectoryForDownloadedAndPreparedArtifacts()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");

        Assert.Contains("var tempRoot = AppPaths.TempDir;", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppPaths.TempDir, \"GeoraePlan\")", source, StringComparison.Ordinal);
        Assert.Contains("var packageDirectory = GetPreparedPackageDirectory(package);", source, StringComparison.Ordinal);
        Assert.Contains("var targetPath = Path.GetFullPath(Path.Combine(packageDirectory, safePackageFileName));", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdater_VerifiesManifestFileSizeBeforeMarkingPackageReadyOrApplyingIt()
    {
        var serviceSource = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var updaterSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");
        var transportSource = ReadRepositoryFile(
            "Shared",
            "GeoraePlan.UpdateTransport",
            "ResumableUpdatePackageDownloader.cs");

        Assert.Contains("PackageDownloader.DownloadAsync(", serviceSource, StringComparison.Ordinal);
        AssertInOrder(
            serviceSource,
            "package.FileSize,",
            "package.Sha256,",
            "PackageDownloadHttp.SendAsync(");
        AssertInOrder(
            transportSource,
            "IsExactFileAsync(",
            "ValidateResponseLength(",
            "IsExactFileAsync(",
            "Publish(partialPath, finalPath);");
        Assert.Contains("expectedFileSize", transportSource, StringComparison.Ordinal);
        Assert.Contains("SHA256.Create()", transportSource, StringComparison.Ordinal);

        Assert.Contains("VerifyExpectedPackageFileSize(packagePath, options.FileSize);", updaterSource, StringComparison.Ordinal);
        Assert.Contains("private static void VerifyExpectedPackageFileSize(string filePath, long expectedFileSize)", updaterSource, StringComparison.Ordinal);
        AssertInOrder(
            updaterSource,
            "await WaitForProcessExitAsync(options);",
            "await VerifySha256Async(packagePath, options.Sha256);",
            "VerifyExpectedPackageFileSize(packagePath, options.FileSize);",
            "await ExtractVerifiedPackageAsync(",
            "await ExecuteInstallWithRollbackAsync(");
    }

    [Fact]
    public void UpdateAssetPublisher_WritesAtomicManifestAndPreservesReferencedDownloads()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'", source, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", source, StringComparison.Ordinal);
        Assert.Contains("function Write-JsonFileAtomically", source, StringComparison.Ordinal);
        Assert.Contains("$json = $json.Replace(\"`r`n\", \"`n\").Replace(\"`r\", \"`n\")", source, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)", source, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Move($tempPath, $TargetPath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force", source, StringComparison.Ordinal);
        Assert.Contains("function Write-DurableGeoraePlanReleaseJournal", source, StringComparison.Ordinal);
        Assert.Contains("function Copy-GeoraePlanReleaseTransactionFileAtomically", source, StringComparison.Ordinal);
        Assert.Contains("[int]$KeepDesktopPackageCount = 2", source, StringComparison.Ordinal);
        Assert.Contains("[int]$KeepAndroidPackageCount = 2", source, StringComparison.Ordinal);
        Assert.Contains("$releaseJournalTypeDefinition = @'", source, StringComparison.Ordinal);
        Assert.Contains("if ($PSVersionTable.PSEdition -eq 'Core')", source, StringComparison.Ordinal);
        Assert.Contains("Add-Type -TypeDefinition $releaseJournalTypeDefinition", source, StringComparison.Ordinal);
        Assert.Contains("-ReferencedAssemblies System.Runtime.Serialization", source, StringComparison.Ordinal);
        Assert.Contains("if ($PlatformNode -is [Collections.IDictionary])", source, StringComparison.Ordinal);
        Assert.Contains("$installers = $PlatformNode['installers']", source, StringComparison.Ordinal);
        Assert.Contains("$previousManifestPath = Join-Path $manifestRoot ($Channel + '.previous.json')", source, StringComparison.Ordinal);
        Assert.Contains("$deliveryManifestPath = Join-Path $ProjectRoot (\"배포\\\" + $Channel + '.json')", source, StringComparison.Ordinal);

        Assert.Contains("Test-DesktopUpdatePackage `", source, StringComparison.Ordinal);
        Assert.Contains("-SourceSnapshot $SourceSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("'App/Updater/거래플랜.Updater.exe'", source, StringComparison.Ordinal);
        Assert.Contains("'App/appsettings.json'", source, StringComparison.Ordinal);
        Assert.Contains("sha256 = $hash.Hash", source, StringComparison.Ordinal);
        Assert.Contains("fileSize = [int64]$fileInfo.Length", source, StringComparison.Ordinal);

        Assert.Contains("$preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'", source, StringComparison.Ordinal);
        Assert.Contains("$preservedAndroidFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'android'", source, StringComparison.Ordinal);
        Assert.Contains("-PreserveFileNames $preservedDesktopFiles", source, StringComparison.Ordinal);
        Assert.Contains("-PreserveFileNames $preservedAndroidFiles", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "-SourcePath $previousTransactionEntry.stagedPath",
            "-SourcePath $deliveryManifestTransactionEntry.stagedPath",
            "-SourcePath $mainManifestTransactionEntry.stagedPath",
            "$journal.phase = 'Committed'",
            "$preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'",
            "$removedDesktopPackages = Remove-OldPackages");
    }

    [Fact]
    public void UpdateAssetPublisher_UsesRecoverableReleaseTransactionAndStrictTempCleanup()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains(
            "function Restore-GeoraePlanReleaseTransaction",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Open-GeoraePlanReleasePublishLock",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Read-ValidatedExistingReleaseManifest",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'Existing manifest is not an exact regular file: '",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Read-StrictGeoraePlanReleaseJournal",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StrictJournalJsonValidator]::Validate(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new HashSet<string>(StringComparer.Ordinal)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExtensionData",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Write-DurableGeoraePlanReleaseJournal",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileOptions]::WriteThrough",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-InspectOnly",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "owner = 'georaeplan-release-transaction'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$plans = $recoveryPlan.ToArray()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$journal.entries = $transactionEntries.ToArray()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$journal.phase = 'Committed'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$journal.Phase = 'CleanupPending'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$journal.Phase = 'RollbackCleanupPending'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Resume-GeoraePlanReleaseTransactionCleanup",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Open-GeoraePlanReleaseDeliveryPublishLock",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Resume-GeoraePlanReleasePreparations",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterInitialJournalTempFlush",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "commitTemporaryPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-release-tmp",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$journal.phase = 'PointerCommitPending'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$manifestPointerPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-DesktopNativeInstallerProductVersion",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "StrictDirectoryChainLease",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "throw [AggregateException]::new(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[bool]$entry.targetExisted",
            source,
            StringComparison.OrdinalIgnoreCase);
        AssertInOrder(
            source,
            "Open-GeoraePlanReleaseDeliveryPublishLock `",
            "Open-GeoraePlanReleasePublishLock -OutputRoot $OutputRoot",
            "Open-GeoraePlanReleaseDirectoryChainLease -DirectoryPaths @(",
            "Resume-GeoraePlanReleasePreparations `",
            "Restore-GeoraePlanReleaseTransaction `",
            "Resume-GeoraePlanReleaseTransactionCleanup `",
            "Read-ValidatedExistingReleaseManifest `",
            "$stagingRoot = Join-Path $transactionRoot 'staging'",
            "$manifest.android = Copy-PackageWithMetadata",
            "$manifest.desktop = Copy-PackageWithMetadata",
            "$journal.phase = 'CommitPending'",
            "-SourcePath $androidTransactionEntry.stagedPath",
            "-SourcePath $runtimeGenerationTransactionEntry.stagedPath",
            "-SourcePath $deliveryGenerationTransactionEntry.stagedPath",
            "-SourcePath $deliveryManifestTransactionEntry.stagedPath",
            "-SourcePath $mainManifestTransactionEntry.stagedPath",
            "$journal.phase = 'PointerCommitPending'",
            "-SourcePath $manifestPointerTransactionEntry.stagedPath",
            "$journal.phase = 'Committed'");
    }

    [Fact]
    public void UpdateAssetPublisher_ValidatesZipBeforeBoundedUniqueExtractionAndMatchesExactProductVersionCore()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");
        var boundedCopy = ExtractPowerShellScriptSection(
            source,
            "function Copy-DesktopArchiveEntryToBoundedFile {",
            "function Test-DesktopUpdatePackage {");
        var boundedScripts = ExtractPowerShellScriptSection(
            source,
            "function Read-DesktopArchiveEntryTextBounded {",
            "function Copy-DesktopArchiveEntryToBoundedFile {");
        var packageValidation = ExtractPowerShellScriptSection(
            source,
            "function Test-DesktopUpdatePackage {",
            "function Get-ManifestReferencedFileNames {");
        var versionValidation = ExtractPowerShellScriptSection(
            packageValidation,
            "        foreach ($executablePath in @(",
            "    finally {");
        var productVersionValidation = ExtractPowerShellScriptSection(
            versionValidation,
            "$actualVersionPrefix = $actualVersionText.Split('+')[0].Trim()",
            "        foreach ($identity in @(");

        Assert.Contains("$source = $Entry.Open()", boundedCopy, StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", boundedCopy, StringComparison.Ordinal);
        Assert.Contains("$MaximumLength - $written", boundedCopy, StringComparison.Ordinal);
        Assert.Contains("$written -ne $Entry.Length", boundedCopy, StringComparison.Ordinal);
        Assert.Contains("$destination.Flush($true)", boundedCopy, StringComparison.Ordinal);

        Assert.Contains("$MaximumBytes - $written", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("$written -ne $Entry.Length", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("-MaximumBytes 1MB", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("-MaximumBytes 64KB", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("*.Desktop.App.exe", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("start \"\" \"%APP_EXE%\"", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("if not defined APP_EXE for %%I in (\"%~dp0*.exe\")", boundedScripts, StringComparison.Ordinal);
        Assert.Contains("powershell -ExecutionPolicy Bypass -File \"%~dp0Install-GeoraePlan.ps1\"", boundedScripts, StringComparison.Ordinal);

        Assert.Contains("[Guid]::NewGuid().ToString('N')", packageValidation, StringComparison.Ordinal);
        Assert.Contains("New-Item -ItemType Directory -Path $tempDirectory -ErrorAction Stop", packageValidation, StringComparison.Ordinal);
        Assert.Contains("Get-ValidatedDesktopArchiveEntryMap `", packageValidation, StringComparison.Ordinal);
        Assert.Contains("Assert-DesktopArchiveScriptContract -Entries $entries", packageValidation, StringComparison.Ordinal);
        Assert.Contains("Copy-DesktopArchiveEntryToBoundedFile `", packageValidation, StringComparison.Ordinal);
        Assert.Contains("[StringComparison]::Ordinal", packageValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractToFile", packageValidation, StringComparison.Ordinal);
        Assert.Contains("$actualVersionText.Split('+')[0].Trim()", versionValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionInfo.FileVersion", versionValidation, StringComparison.Ordinal);
        Assert.DoesNotContain("[StringComparison]::OrdinalIgnoreCase", productVersionValidation, StringComparison.Ordinal);
        AssertInOrder(
            packageValidation,
            "Get-ValidatedDesktopArchiveEntryMap `",
            "Assert-DesktopArchiveScriptContract -Entries $entries",
            "Copy-DesktopArchiveEntryToBoundedFile `",
            "VersionInfo.ProductVersion",
            "$actualVersionPrefix,",
            "$ExpectedVersion,",
            "[StringComparison]::Ordinal");
    }

    [Fact]
    public async Task UpdateAssetPublisher_DeduplicatesCanonicalCompletedRequestAndSeparatesDifferentRetry()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-request-receipt-tests",
            Guid.NewGuid().ToString("N"));

        static string ReadGeneration(string outputRoot)
        {
            using var pointer = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(
                    outputRoot,
                    "manifest",
                    "stable.current.json")));
            return pointer.RootElement
                .GetProperty("generationId")
                .GetString()!;
        }

        try
        {
            var exactProject = Path.Combine(testRoot, "exact-project");
            var exactOutput = Path.Combine(testRoot, "exact-updates");
            Directory.CreateDirectory(Path.Combine(
                exactProject,
                "\uBC30\uD3EC"));
            var interruptedAfterCleanup = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_RELEASE_TEST_KILL_POINT",
                    "AfterTransactionCleanupBeforePrune"),
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-A"),
                ("-KeepDesktopPackageCount", "1"));
            Assert.NotEqual(0, interruptedAfterCleanup.ExitCode);
            var committedGeneration = ReadGeneration(exactOutput);
            var desktopDownloadRoot = Path.Combine(
                exactOutput,
                "downloads",
                "desktop");
            var oldPruneCandidate = Path.Combine(
                desktopDownloadRoot,
                "old-prune-candidate.zip");
            var newPruneCandidate = Path.Combine(
                desktopDownloadRoot,
                "new-prune-candidate.zip");
            File.WriteAllText(oldPruneCandidate, "old");
            File.WriteAllText(newPruneCandidate, "new");
            File.SetLastWriteTimeUtc(
                oldPruneCandidate,
                DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(
                newPruneCandidate,
                DateTime.UtcNow.AddMinutes(-5));

            var receiptRetry = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-A"),
                ("-KeepDesktopPackageCount", "1"));
            Assert.Equal(0, receiptRetry.ExitCode);
            Assert.Contains(
                "release_request_receipt=matched",
                receiptRetry.StdOut,
                StringComparison.Ordinal);
            Assert.Equal(
                committedGeneration,
                ReadGeneration(exactOutput));
            Assert.False(File.Exists(oldPruneCandidate));
            Assert.True(File.Exists(newPruneCandidate));

            var absentPlatformMetadataRetry = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"),
                ("-KeepDesktopPackageCount", "1"));
            Assert.Equal(0, absentPlatformMetadataRetry.ExitCode);
            Assert.Equal(
                committedGeneration,
                ReadGeneration(exactOutput));

            var differentCompletedRequest = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"),
                ("-KeepDesktopPackageCount", "2"));
            Assert.Equal(0, differentCompletedRequest.ExitCode);
            var differentGeneration = ReadGeneration(exactOutput);
            Assert.NotEqual(committedGeneration, differentGeneration);

            var receiptPath = Path.Combine(
                exactOutput,
                "manifest",
                "stable.request-receipt.json");
            var validReceiptBytes = File.ReadAllBytes(receiptPath);
            var corruptReceipt = JsonSerializer.Deserialize<
                Dictionary<string, object?>>(
                    validReceiptBytes);
            Assert.NotNull(corruptReceipt);
            corruptReceipt!["unexpected"] = "rejected";
            File.WriteAllText(
                receiptPath,
                JsonSerializer.Serialize(corruptReceipt));
            var corruptReceiptRetry = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"),
                ("-KeepDesktopPackageCount", "2"));
            Assert.NotEqual(0, corruptReceiptRetry.ExitCode);
            Assert.Equal(differentGeneration, ReadGeneration(exactOutput));

            File.WriteAllBytes(receiptPath, validReceiptBytes);
            var repairedReceiptRetry = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactProject),
                ("-OutputRoot", exactOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"),
                ("-KeepDesktopPackageCount", "2"));
            Assert.Equal(0, repairedReceiptRetry.ExitCode);
            Assert.Equal(differentGeneration, ReadGeneration(exactOutput));

            var pendingProject = Path.Combine(testRoot, "pending-project");
            var pendingOutput = Path.Combine(testRoot, "pending-updates");
            Directory.CreateDirectory(Path.Combine(
                pendingProject,
                "\uBC30\uD3EC"));
            var interruptedBeforePointer = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_RELEASE_TEST_KILL_POINT",
                    "BeforePointerReplaceAfterCommitIntent"),
                ("-ProjectRoot", pendingProject),
                ("-OutputRoot", pendingOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-A"),
                ("-SkipPackagePrune", null));
            Assert.NotEqual(0, interruptedBeforePointer.ExitCode);

            var mismatchedRecovery = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", pendingProject),
                ("-OutputRoot", pendingOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"));
            Assert.NotEqual(0, mismatchedRecovery.ExitCode);
            Assert.Contains(
                "does not exactly match",
                mismatchedRecovery.StdOut + mismatchedRecovery.StdErr,
                StringComparison.Ordinal);
            var recoveredGeneration = ReadGeneration(pendingOutput);

            var freshDifferentRequest = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", pendingProject),
                ("-OutputRoot", pendingOutput),
                ("-Channel", "stable"),
                ("-SkipAndroid", null),
                ("-DesktopVersion", "1.0.0"),
                ("-DesktopNotes", "request-B"));
            Assert.Equal(0, freshDifferentRequest.ExitCode);
            Assert.NotEqual(
                recoveredGeneration,
                ReadGeneration(pendingOutput));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateAssetPublisher_RequestFingerprintUsesSemanticArtifactIdentity()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");
        var fingerprintSection = ExtractPowerShellScriptSection(
            source,
            "function Get-GeoraePlanReleaseRequestFingerprint",
            "function Initialize-GeoraePlanReleaseJournalTypes");

        Assert.Contains(
            "artifactIdentity",
            fingerprintSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "$assetSha256",
            fingerprintSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "$assetFileSize",
            fingerprintSection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sourcePath",
            fingerprintSection,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "sourceFileName",
            fingerprintSection,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            ".request-receipt.json",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterTransactionCleanupBeforePrune",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$desktopNode = if ($HasDesktopPackage)",
            fingerprintSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "$androidNode = if ($HasAndroidPackage)",
            fingerprintSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-GeoraePlanManifestAssetsPresent",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[string]$requestReceipt.requestFingerprint,",
            "Assert-GeoraePlanManifestAssetsPresent `",
            "release_request_receipt=matched",
            "return");

        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-request-fingerprint-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testRoot);
            var harnessPath = Path.Combine(
                testRoot,
                "fingerprint-harness.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                $ErrorActionPreference = 'Stop'
                {{fingerprintSection}}
                $snapshot = [pscustomobject]@{
                    Sha256 = ('A' * 64)
                    FileSize = 3
                }
                $present = @{
                    Channel = 'stable'
                    HasDesktopPackage = $true
                    HasAndroidPackage = $false
                    DesktopPackageSnapshot = $snapshot
                    DesktopExeInstallerSnapshot = $snapshot
                    DesktopMsiInstallerSnapshot = $snapshot
                    DesktopVersion = '1.0.0'
                    SkipAndroid = $true
                    KeepDesktopPackageCount = 1
                    KeepAndroidPackageCount = 1
                }
                $present.DesktopNotes = 'output-A'
                $presentA = Get-GeoraePlanReleaseRequestFingerprint @present
                $present.DesktopNotes = 'output-B'
                $presentB = Get-GeoraePlanReleaseRequestFingerprint @present
                if ($presentA -ceq $presentB) {
                    throw 'Present-platform output metadata was normalized away.'
                }
                $absent = @{
                    Channel = 'stable'
                    HasDesktopPackage = $false
                    HasAndroidPackage = $false
                    DesktopVersion = '1.0.0'
                    SkipAndroid = $true
                    KeepDesktopPackageCount = 1
                    KeepAndroidPackageCount = 1
                }
                $absent.DesktopNotes = 'no-output-A'
                $absentA = Get-GeoraePlanReleaseRequestFingerprint @absent
                $absent.DesktopNotes = 'no-output-B'
                $absentB = Get-GeoraePlanReleaseRequestFingerprint @absent
                if ($absentA -cne $absentB) {
                    throw 'Absent-platform metadata changed the fingerprint.'
                }
                Write-Output 'fingerprint_semantics=ok'
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));
            var result = await RunPowerShellAsync(harnessPath);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(
                "fingerprint_semantics=ok",
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
    public async Task UpdateAssetPublisher_ReceiptRetryValidatesManifestReferencedAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");
        var validationFunctions = ExtractPowerShellScriptSection(
            source,
            "function Get-GeoraePlanManifestAssetBindings",
            "function Assert-GeoraePlanReleaseVersionPolicy");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-receipt-asset-validation-tests",
            Guid.NewGuid().ToString("N"));
        var downloadsRoot = Path.Combine(testRoot, "downloads");
        var desktopRoot = Path.Combine(downloadsRoot, "desktop");
        var assetPath = Path.Combine(desktopRoot, "desktop.zip");

        try
        {
            Directory.CreateDirectory(desktopRoot);
            Directory.CreateDirectory(Path.Combine(downloadsRoot, "android"));
            File.WriteAllText(assetPath, "expected bytes");
            var expectedHash = ComputeSha256(assetPath);
            var expectedSize = new FileInfo(assetPath).Length;
            var harnessPath = Path.Combine(
                testRoot,
                "asset-validation-harness.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                param(
                    [string]$DownloadsRoot,
                    [string]$ExpectedSha256,
                    [long]$ExpectedFileSize)
                $ErrorActionPreference = 'Stop'
                function Assert-GeoraePlanReleaseRegularDirectoryChain {
                    param(
                        [string]$Path,
                        [switch]$LeafMayBeFile)
                }
                $script:releaseDirectoryLease = $null
                {{validationFunctions}}
                $manifest = [pscustomobject]@{
                    desktop = [pscustomobject]@{
                        fileName = 'desktop.zip'
                        sha256 = $ExpectedSha256
                        fileSize = [string]$ExpectedFileSize
                        installers = @()
                    }
                    android = $null
                }
                Assert-GeoraePlanManifestAssetsPresent `
                    -Manifest $manifest `
                    -DownloadsRoot $DownloadsRoot
                Write-Output 'manifest_assets=valid'
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            var valid = await RunPowerShellAsync(
                harnessPath,
                ("-DownloadsRoot", downloadsRoot),
                ("-ExpectedSha256", expectedHash),
                ("-ExpectedFileSize", expectedSize.ToString()));
            Assert.Equal(0, valid.ExitCode);

            File.WriteAllText(assetPath, "tampered bytes");
            Assert.Equal(
                expectedSize,
                new FileInfo(assetPath).Length);
            var corrupt = await RunPowerShellAsync(
                harnessPath,
                ("-DownloadsRoot", downloadsRoot),
                ("-ExpectedSha256", expectedHash),
                ("-ExpectedFileSize", expectedSize.ToString()));
            Assert.NotEqual(0, corrupt.ExitCode);
            Assert.Contains(
                "hash/size",
                corrupt.StdOut + Environment.NewLine + corrupt.StdErr,
                StringComparison.OrdinalIgnoreCase);

            File.Delete(assetPath);
            var missing = await RunPowerShellAsync(
                harnessPath,
                ("-DownloadsRoot", downloadsRoot),
                ("-ExpectedSha256", expectedHash),
                ("-ExpectedFileSize", expectedSize.ToString()));
            Assert.NotEqual(0, missing.ExitCode);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void LinuxPcRelease_UsesVerifiedLiveBaselineForMirrorToLiveAndKeepsLocalFallbackForStaging()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Get-GeoraePlanLinuxFileSha256", source, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.SHA256]::Create()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", source, StringComparison.Ordinal);
        Assert.Contains("$null = $copyTask.GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$gateArgs += @(",
            "'-AllowedIntegrityWarningCodes',",
            "($AllowedIntegrityWarningCodes -join ','))");
        Assert.DoesNotContain(
            "$gateArgs += $AllowedIntegrityWarningCodes",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[switch]$PreserveLiveUpdateAssets", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$PreserveLiveAndroidUpdate", source, StringComparison.Ordinal);
        Assert.Contains("PreserveExistingAndroid = $true", source, StringComparison.Ordinal);
        Assert.Contains("reason=verified-live-android-update-preserved", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "if ($MirrorToLive -and $PreserveLiveUpdateAssets)",
            "pre-deploy_android_signing_continuity=not-applicable reason=verified-live-update-assets-preserved",
            "elseif ($MirrorToLive -and $PreserveLiveAndroidUpdate)",
            "pre-deploy_android_signing_continuity=not-applicable reason=verified-live-android-update-preserved",
            "elseif ($MirrorToLive -and -not $SkipAndroidSigningContinuityCheck.IsPresent)",
            "Invoke-AndroidSigningContinuityGate `");
        var uploadFunction = ExtractPowerShellScriptSection(
            source,
            "function Invoke-SshTarUpload",
            "function Get-RemoteEnvMap");
        Assert.DoesNotContain("georaeplan-linux-upload-", uploadFunction, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd /c", uploadFunction, StringComparison.Ordinal);
        Assert.Contains("$tarStartInfo.RedirectStandardOutput = $true", uploadFunction, StringComparison.Ordinal);
        Assert.Contains("$sshStartInfo.RedirectStandardInput = $true", uploadFunction, StringComparison.Ordinal);
        AssertInOrder(
            uploadFunction,
            "$sshProcess.Start()",
            "$tarProcess.Start()",
            "$tarProcess.StandardOutput.BaseStream.CopyToAsync($sshProcess.StandardInput.BaseStream)",
            "$sshProcess.StandardInput.Close()",
            "$tarProcess.WaitForExit()",
            "$sshProcess.WaitForExit()");
        Assert.Contains("[switch]$AllowMissingLiveUpdateBaseline", source, StringComparison.Ordinal);
        Assert.Contains("function Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-SshFileDownload", source, StringComparison.Ordinal);
        Assert.Contains(
            "function Copy-RemoteUpdatePointerEvidenceToStaging",
            source,
            StringComparison.Ordinal);
        var pointerCopyFunction = ExtractPowerShellScriptSection(
            source,
            "function Copy-RemoteUpdatePointerEvidenceToStaging",
            "function Copy-LiveUpdateRollbackBaseline");
        Assert.Contains(
            "Invoke-SshFileDownload `",
            pointerCopyFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DestinationPath $stagingPointerPath `",
            pointerCopyFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$pointerJson = [string]$pointerResult.StdOut",
            pointerCopyFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Set-Content `",
            pointerCopyFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Copy-GeoraePlanPointerRollbackEvidence",
            source,
            StringComparison.Ordinal);
        Assert.Contains("function Copy-LiveUpdateRollbackBaseline", source, StringComparison.Ordinal);
        Assert.Contains("$remoteUpdatesRoot = $Config.RemoteRoot + '/app/live/updates'", source, StringComparison.Ordinal);
        Assert.Contains("exit 44", source, StringComparison.Ordinal);
        var liveBaselineCopyFunction = ExtractPowerShellScriptSection(
            source,
            "function Copy-LiveUpdateRollbackBaseline",
            "function Copy-LocalUpdateRollbackBaseline");
        Assert.Contains("$remoteManifestReadCommand =", liveBaselineCopyFunction, StringComparison.Ordinal);
        AssertInOrder(
            liveBaselineCopyFunction,
            "if [ -L $quotedRemoteManifestPath ]; then exit 45;",
            "elif [ ! -e $quotedRemoteManifestPath ]; then exit 44;",
            "elif [ ! -f $quotedRemoteManifestPath ]; then exit 45;",
            "else exit 0; fi");
        Assert.Contains(
            "Invoke-SshFileDownload `",
            liveBaselineCopyFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "-DestinationPath $stagingManifestPath `",
            liveBaselineCopyFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$manifestJson = [string]$manifestResult.StdOut",
            liveBaselineCopyFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Set-Content -LiteralPath $stagingManifestPath",
            liveBaselineCopyFunction,
            StringComparison.Ordinal);
        Assert.Contains("Invoke-SshFileDownload -RemotePath $remotePackagePath -DestinationPath $localPackagePath -Config $Config", source, StringComparison.Ordinal);
        Assert.Contains("same-origin", source, StringComparison.Ordinal);
        Assert.Contains("live_update_rollback_baseline_seeded", source, StringComparison.Ordinal);
        Assert.Contains("live_update_rollback_baseline=initial_release manifest_status=missing channel=$Channel", source, StringComparison.Ordinal);
        Assert.Contains("$Channel + '.previous.json'", source, StringComparison.Ordinal);
        Assert.Contains("$Channel + '.current.json'", source, StringComparison.Ordinal);
        Assert.Contains(
            "'delivery-generations'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Invoke-GeoraePlanDurableUpdateAssetPublish",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'release-temp\\linux-update-assets-stable'",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "if ($MirrorToLive) {",
            "Copy-LiveUpdateRollbackBaseline `",
            "Copy-LocalUpdateRollbackBaseline -Root $ProjectRoot -PublishRoot $tempPublishRoot -Channel 'stable'",
            "$updateAssetScript = Join-Path $ProjectRoot 'tools\\release\\Publish-GeoraePlanUpdateAssets.ps1'",
            "Invoke-GeoraePlanDurableUpdateAssetPublish `");

        var verifiedCopyFunction = ExtractPowerShellScriptSection(
            source,
            "function Copy-GeoraePlanUpdateEvidenceFileAtomically",
            "function Get-GeoraePlanUpdatePointerEvidence");
        AssertInOrder(
            verifiedCopyFunction,
            "$sourceStream.CopyTo($targetStream)",
            "$temporaryHash =",
            "Get-GeoraePlanLinuxFileSha256 -Path $temporaryPath",
            "'Copied update evidence hash/size does not match the '",
            "[IO.File]::Move($temporaryPath, $TargetPath)");
        Assert.Contains(
            "Copy-GeoraePlanUpdateEvidenceFileAtomically `",
            ExtractPowerShellScriptSection(
                source,
                "function Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot",
                "function Invoke-SshFileDownload"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Copy-GeoraePlanUpdateEvidenceFileAtomically `",
            ExtractPowerShellScriptSection(
                source,
                "function Copy-LocalUpdateRollbackBaseline",
                "Assert-SafeReleaseId -Value $ReleaseId"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("directory", false)]
    [InlineData("normal-symlink", false)]
    [InlineData("dangling-symlink", false)]
    public async Task LinuxPcRelease_RemoteLiveManifestAllowsOnlyTrueAbsence(
        string fixtureKind,
        bool expectedSuccess)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-remote-live-manifest-type-tests",
            fixtureKind + "-" + Guid.NewGuid().ToString("N"));
        var fixturePath = Path.Combine(
            testRoot,
            "remote",
            "app",
            "live",
            "updates",
            "manifest",
            "stable.json");
        var targetPath = Path.Combine(testRoot, "external-stable.json");
        var publishRoot = Path.Combine(testRoot, "publish");
        var projectRoot = Path.Combine(testRoot, "project");
        var stagingRoot = Path.Combine(testRoot, "staging");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            Directory.CreateDirectory(publishRoot);
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(stagingRoot);

            if (fixtureKind == "directory")
            {
                Directory.CreateDirectory(fixturePath);
            }
            else if (fixtureKind is "normal-symlink" or "dangling-symlink")
            {
                if (!TryCreateFileSymbolicLinkFixture(
                        Path.Combine(testRoot, "symlink-capability"),
                        out var unavailableReason))
                {
                    _ = unavailableReason;
                    return;
                }

                if (fixtureKind == "normal-symlink")
                    File.WriteAllText(targetPath, "{}");
                File.CreateSymbolicLink(fixturePath, targetPath);
            }

            var linuxSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var liveCopyFunction = ExtractPowerShellScriptSection(
                linuxSource,
                "function Copy-LiveUpdateRollbackBaseline",
                "function Copy-LocalUpdateRollbackBaseline");
            var harnessPath = Path.Combine(
                testRoot,
                "classify-remote-live-manifest.ps1");
            var harness =
                "param([string]$FixturePath,[string]$PublishRoot," +
                "[string]$ProjectRoot,[string]$StagingRoot)" +
                Environment.NewLine +
                "function Resolve-LiveUpdateRollbackBaselineTempRoot " +
                "{ return $StagingRoot }" +
                Environment.NewLine +
                "function Convert-ToSingleQuotedShellLiteral " +
                "{ param([string]$Value) return \"'fixture'\" }" +
                Environment.NewLine +
                "function Invoke-SshCommand {" +
                Environment.NewLine +
                "  param($Config,[string]$Command,[switch]$IgnoreExitCode," +
                "[switch]$BatchMode)" +
                Environment.NewLine +
                "  $parent = Split-Path -Parent $FixturePath" +
                Environment.NewLine +
                "  $leaf = Split-Path -Leaf $FixturePath" +
                Environment.NewLine +
                "  $item = @(Get-ChildItem -LiteralPath $parent -Force " +
                "-ErrorAction SilentlyContinue | Where-Object " +
                "{ [string]::Equals($_.Name,$leaf," +
                "[StringComparison]::Ordinal) }) | Select-Object -First 1" +
                Environment.NewLine +
                "  if ($null -eq $item) { return [pscustomobject]@{" +
                "ExitCode=44;StdOut='';StdErr=''} }" +
                Environment.NewLine +
                "  if ($item.PSIsContainer -or (($item.Attributes -band " +
                "[IO.FileAttributes]::ReparsePoint) -ne 0)) " +
                "{ return [pscustomobject]@{" +
                "ExitCode=45;StdOut='';StdErr=''} }" +
                Environment.NewLine +
                "  return [pscustomobject]@{ExitCode=0;" +
                "StdOut=(Get-Content -LiteralPath $FixturePath -Raw);" +
                "StdErr=''}" +
                Environment.NewLine +
                "}" +
                Environment.NewLine +
                liveCopyFunction +
                Environment.NewLine +
                "$config = [pscustomobject]@{ RemoteRoot = '/srv/georaeplan' }" +
                Environment.NewLine +
                "Copy-LiveUpdateRollbackBaseline -BaseUrl " +
                "'https://trade.2884.kr' -PublishRoot $PublishRoot " +
                "-ProjectRoot $ProjectRoot -Config $config -Channel stable " +
                "-AllowMissingManifest" +
                Environment.NewLine;
            File.WriteAllText(
                harnessPath,
                harness,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(
                harnessPath,
                ("-FixturePath", fixturePath),
                ("-PublishRoot", publishRoot),
                ("-ProjectRoot", projectRoot),
                ("-StagingRoot", stagingRoot));

            if (expectedSuccess)
            {
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(
                    "manifest_status=missing",
                    result.StdOut,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.NotEqual(0, result.ExitCode);
                Assert.Contains(
                    "정규 파일",
                    result.StdOut + Environment.NewLine + result.StdErr,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            try
            {
                File.Delete(fixturePath);
            }
            catch
            {
                // The recursive fixture cleanup below handles directories.
            }
            if (Directory.Exists(fixturePath))
                Directory.Delete(fixturePath, recursive: true);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_RejectsReleaseTempJunctionAndLeasesPerReleaseChildIdentity()
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var durableFunctions = ExtractLinuxReleaseHarnessSection(
            linuxSource,
            "function Assert-GeoraePlanLinuxRegularDirectoryChain",
            "function Assert-SafeReleaseId");
        Assert.Contains(
            "Get-GeoraePlanLinuxUpdateCopyDirectoryPaths",
            durableFunctions,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-GeoraePlanLinuxDirectoryPathSet",
            durableFunctions,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileMode]::CreateNew",
            durableFunctions,
            StringComparison.Ordinal);
        Assert.Contains(
            "[ref]$DestinationLease",
            durableFunctions,
            StringComparison.Ordinal);
        Assert.Contains(
            "Assert-GeoraePlanLinuxManifestReferencedAssets",
            durableFunctions,
            StringComparison.Ordinal);
        AssertInOrder(
            durableFunctions,
            "$finalDestinationDirectoryPaths =",
            "Assert-GeoraePlanLinuxDirectoryPathSet `",
            "Assert-GeoraePlanLinuxManifestReferencedAssets `",
            "$DestinationLease.Value = $destinationDirectoryLease",
            "$destinationDirectoryLease = $null");
        Assert.Contains(
            "-RootLease $tempPublishDirectoryLease `",
            linuxSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "-PreserveRoot",
            linuxSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Copy-Item `",
            durableFunctions,
            StringComparison.Ordinal);
        AssertInOrder(
            linuxSource,
            "Copy-GeoraePlanDurableUpdateAssets `",
            "-DestinationLease ([ref]$preparationDirectoryLease) `",
            "Assert-GeoraePlanLinuxDirectoryChainLease `",
            "Assert-GeoraePlanLinuxManifestReferencedAssets `",
            "-Phase 'Ready' `");
        AssertInOrder(
            linuxSource,
            "-DestinationLease ([ref]$publishUpdatesDirectoryLease) `",
            "Assert-GeoraePlanLinuxDirectoryChainLease `",
            "Assert-GeoraePlanLinuxManifestReferencedAssets `",
            "$copiedPointerSha256 =");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-release-directory-lease-tests",
            Guid.NewGuid().ToString("N"));
        var junctionProject = Path.Combine(testRoot, "junction-project");
        var externalRoot = Path.Combine(testRoot, "external");
        var junctionPath = Path.Combine(
            junctionProject,
            "release-temp");

        try
        {
            Directory.CreateDirectory(junctionProject);
            Directory.CreateDirectory(externalRoot);
            var sentinelPath = Path.Combine(externalRoot, "sentinel.txt");
            File.WriteAllText(sentinelPath, "must remain");
            var junctionMaker = Path.Combine(
                testRoot,
                "create-junction.ps1");
            File.WriteAllText(
                junctionMaker,
                """
                param([string]$Link, [string]$Target)
                New-Item -ItemType Junction -Path $Link -Target $Target -ErrorAction Stop | Out-Null
                """);
            var junctionResult = await RunPowerShellAsync(
                junctionMaker,
                ("-Link", junctionPath),
                ("-Target", externalRoot));
            Assert.Equal(0, junctionResult.ExitCode);

            var junctionHarness = Path.Combine(
                testRoot,
                "junction-harness.ps1");
            File.WriteAllText(
                junctionHarness,
                $$"""
                param(
                    [string]$ProjectRoot,
                    [string]$DurableUpdatesRoot,
                    [string]$PublishRoot,
                    [string]$UpdateAssetScript)
                $ErrorActionPreference = 'Stop'
                {{durableFunctions}}
                Invoke-GeoraePlanDurableUpdateAssetPublish `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $DurableUpdatesRoot `
                    -PublishRoot $PublishRoot `
                    -UpdateAssetScript $UpdateAssetScript `
                    -UpdateAssetArguments @{ SkipAndroid = $true } `
                    -Channel 'stable'
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));
            var junctionRejected = await RunPowerShellAsync(
                junctionHarness,
                ("-ProjectRoot", junctionProject),
                (
                    "-DurableUpdatesRoot",
                    Path.Combine(
                        junctionPath,
                        "linux-update-assets-stable")),
                ("-PublishRoot", externalRoot),
                (
                    "-UpdateAssetScript",
                    Path.Combine(
                        repositoryRoot,
                        "tools",
                        "release",
                        "Publish-GeoraePlanUpdateAssets.ps1")));
            Assert.NotEqual(0, junctionRejected.ExitCode);
            Assert.Contains(
                "non-regular directory ancestor",
                junctionRejected.StdOut + junctionRejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("must remain", File.ReadAllText(sentinelPath));

            Directory.Delete(junctionPath);
            var leaseProject = Path.Combine(testRoot, "lease-project");
            var releaseTempRoot = Path.Combine(
                leaseProject,
                "release-temp");
            var publishRoot = Path.Combine(
                releaseTempRoot,
                "linux-test");
            var movedPublishRoot = publishRoot + ".moved";
            Directory.CreateDirectory(publishRoot);
            var readyPath = Path.Combine(testRoot, "lease-ready");
            var releasePath = Path.Combine(testRoot, "lease-release");
            var leaseHarness = Path.Combine(
                testRoot,
                "lease-harness.ps1");
            File.WriteAllText(
                leaseHarness,
                $$"""
                param(
                    [string]$ProjectRoot,
                    [string]$ReleaseTempRoot,
                    [string]$PublishRoot,
                    [string]$ReadyPath,
                    [string]$ReleasePath)
                $ErrorActionPreference = 'Stop'
                {{durableFunctions}}
                $lease = Open-GeoraePlanLinuxDirectoryChainLease `
                    -DirectoryPaths @(
                        $ProjectRoot,
                        $ReleaseTempRoot,
                        $PublishRoot)
                try {
                    [IO.File]::WriteAllText($ReadyPath, 'ready')
                    $deadline = [DateTime]::UtcNow.AddSeconds(10)
                    while (
                        -not (Test-Path -LiteralPath $ReleasePath) -and
                        [DateTime]::UtcNow -lt $deadline
                    ) {
                        Start-Sleep -Milliseconds 25
                    }
                    if (-not (Test-Path -LiteralPath $ReleasePath)) {
                        throw 'lease release signal timed out'
                    }
                    Assert-GeoraePlanLinuxDirectoryChainLease -Lease $lease
                }
                finally {
                    $lease.Dispose()
                }
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));
            using var leaseProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            leaseProcess.StartInfo.ArgumentList.Add("-NoProfile");
            leaseProcess.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            leaseProcess.StartInfo.ArgumentList.Add("Bypass");
            leaseProcess.StartInfo.ArgumentList.Add("-File");
            leaseProcess.StartInfo.ArgumentList.Add(leaseHarness);
            foreach (var value in new[]
            {
                "-ProjectRoot", leaseProject,
                "-ReleaseTempRoot", releaseTempRoot,
                "-PublishRoot", publishRoot,
                "-ReadyPath", readyPath,
                "-ReleasePath", releasePath
            })
            {
                leaseProcess.StartInfo.ArgumentList.Add(value);
            }
            leaseProcess.Start();
            var leaseStdOut = leaseProcess.StandardOutput.ReadToEndAsync();
            var leaseStdErr = leaseProcess.StandardError.ReadToEndAsync();
            for (var attempt = 0; attempt < 200 && !File.Exists(readyPath); attempt++)
                await Task.Delay(25);
            Assert.True(File.Exists(readyPath));

            var moveError = Record.Exception(
                () => Directory.Move(publishRoot, movedPublishRoot));
            Assert.True(
                moveError is IOException or UnauthorizedAccessException,
                $"Expected leased child move rejection, got: {moveError}");
            Assert.True(Directory.Exists(publishRoot));
            Assert.False(Directory.Exists(movedPublishRoot));

            File.WriteAllText(releasePath, "release");
            Assert.True(leaseProcess.WaitForExit(10_000));
            Assert.Equal(
                0,
                leaseProcess.ExitCode);
            Assert.Empty(await leaseStdErr);
            _ = await leaseStdOut;
            Directory.Move(publishRoot, movedPublishRoot);
            Assert.True(Directory.Exists(movedPublishRoot));
        }
        finally
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("AfterPreparationReadyBeforePromote", true)]
    [InlineData("AfterPreparationPromoteBeforeSeededState", false)]
    public async Task LinuxPcRelease_RecoveryRejectsReparsePointerDuringPreparationAndAfterPromotion(
        string killPoint,
        bool pointerRemainsInPreparationRoot)
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var pointerGuards = ExtractPowerShellScriptSection(
            linuxSource,
            "function Get-GeoraePlanLinuxRegularPointerItem",
            "function Write-GeoraePlanUpdatePointerDeliveryPath");
        Assert.Contains(
            "Get-ChildItem `",
            pointerGuards,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileAttributes]::ReparsePoint",
            pointerGuards,
            StringComparison.Ordinal);
        AssertInOrder(
            pointerGuards,
            "function Get-GeoraePlanLinuxRegularPointerItem",
            "$item.PSIsContainer",
            "[IO.FileAttributes]::ReparsePoint",
            "function Get-GeoraePlanUpdatePointerEvidence",
            "Get-GeoraePlanLinuxRegularPointerItem `",
            "$pointer = Get-Content -LiteralPath $pointerPath");

        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-update-pointer-reparse-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var releaseTempRoot = Path.Combine(projectRoot, "release-temp");
        var firstPublishRoot = Path.Combine(releaseTempRoot, "linux-A");
        var secondPublishRoot = Path.Combine(releaseTempRoot, "linux-B");
        var durableUpdatesRoot = Path.Combine(
            releaseTempRoot,
            "linux-update-assets-stable");
        var pointerPath = string.Empty;
        var externalPointerPath = Path.Combine(
            testRoot,
            "outside-pointer.json");
        var updateAssetScript = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        try
        {
            Directory.CreateDirectory(firstPublishRoot);
            Directory.CreateDirectory(secondPublishRoot);
            if (!TryCreateFileSymbolicLinkFixture(
                testRoot,
                out var symlinkUnavailableReason))
            {
                _ = symlinkUnavailableReason;
                return;
            }

            WriteMinimalLinuxUpdatePointerFixture(
                projectRoot,
                firstPublishRoot);
            WriteMinimalLinuxUpdatePointerFixture(
                projectRoot,
                secondPublishRoot);
            var harnessPath = WriteDurableWrapperHarness(
                testRoot,
                repositoryRoot);
            var interrupted = await RunPowerShellAsync(
                harnessPath,
                (
                    "GEORAEPLAN_LINUX_UPDATE_WRAPPER_TEST_KILL_POINT",
                    killPoint),
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", firstPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));
            Assert.NotEqual(0, interrupted.ExitCode);

            var pointerRoot = pointerRemainsInPreparationRoot
                ? durableUpdatesRoot + ".preparing"
                : durableUpdatesRoot;
            pointerPath = Path.Combine(
                pointerRoot,
                "manifest",
                "stable.current.json");
            Assert.True(
                File.Exists(pointerPath),
                interrupted.StdOut + Environment.NewLine + interrupted.StdErr);
            var pointerBytes = File.ReadAllBytes(pointerPath);
            File.WriteAllBytes(externalPointerPath, pointerBytes);
            File.Delete(pointerPath);
            File.CreateSymbolicLink(pointerPath, externalPointerPath);
            Assert.True(
                (File.GetAttributes(pointerPath) &
                    FileAttributes.ReparsePoint) != 0);

            var recovered = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", secondPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.NotEqual(0, recovered.ExitCode);
            Assert.Contains(
                "Update manifest pointer is not a regular file",
                recovered.StdOut + recovered.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                pointerBytes,
                File.ReadAllBytes(externalPointerPath));
            Assert.True(
                (File.GetAttributes(pointerPath) &
                    FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pointerPath) &&
                File.Exists(pointerPath))
            {
                File.Delete(pointerPath);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_DurablePointerHelperDistinguishesTrueAbsenceFromDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var pointerFunctions = ExtractLinuxReleaseHarnessSection(
            linuxSource,
            "function Get-GeoraePlanLinuxRegularPointerItem",
            "function Read-GeoraePlanDurableUpdateWrapperStateFile");
        Assert.Contains(
            "Get-ChildItem `",
            pointerFunctions,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileAttributes]::ReparsePoint",
            pointerFunctions,
            StringComparison.Ordinal);

        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-pointer-absence-tests",
            Guid.NewGuid().ToString("N"));
        var durableRoot = Path.Combine(testRoot, "updates");
        var pointerPath = Path.Combine(
            durableRoot,
            "manifest",
            "stable.current.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
            var harnessPath = Path.Combine(testRoot, "pointer-hash.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                param([string]$DurableUpdatesRoot)
                $ErrorActionPreference = 'Stop'
                {{pointerFunctions}}
                $hash = Get-GeoraePlanDurableUpdatePointerSha256 `
                    -DurableUpdatesRoot $DurableUpdatesRoot `
                    -Channel 'stable'
                Write-Output ('pointer_hash=' + $hash)
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var absent = await RunPowerShellAsync(
                harnessPath,
                ("-DurableUpdatesRoot", durableRoot));
            Assert.Equal(0, absent.ExitCode);
            Assert.Contains(
                "pointer_hash=",
                absent.StdOut,
                StringComparison.Ordinal);

            Directory.CreateDirectory(pointerPath);
            var directory = await RunPowerShellAsync(
                harnessPath,
                ("-DurableUpdatesRoot", durableRoot));
            Assert.NotEqual(0, directory.ExitCode);
            Assert.Contains(
                "not a regular file",
                directory.StdOut + Environment.NewLine + directory.StdErr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxPcRelease_DurablePointerHelperRejectsNormalAndDanglingSymbolicLinks(
        bool dangling)
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var pointerFunctions = ExtractLinuxReleaseHarnessSection(
            linuxSource,
            "function Get-GeoraePlanLinuxRegularPointerItem",
            "function Read-GeoraePlanDurableUpdateWrapperStateFile");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-pointer-symlink-tests",
            Guid.NewGuid().ToString("N"));
        var durableRoot = Path.Combine(testRoot, "updates");
        var manifestRoot = Path.Combine(durableRoot, "manifest");
        var pointerPath = Path.Combine(
            manifestRoot,
            "stable.current.json");
        var targetPath = Path.Combine(testRoot, "outside-pointer.json");
        try
        {
            Directory.CreateDirectory(manifestRoot);
            if (!TryCreateFileSymbolicLinkFixture(
                testRoot,
                out var symlinkUnavailableReason))
            {
                _ = symlinkUnavailableReason;
                return;
            }
            if (!dangling)
                File.WriteAllText(targetPath, "{}");
            File.CreateSymbolicLink(pointerPath, targetPath);

            var harnessPath = Path.Combine(testRoot, "pointer-hash.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                param([string]$DurableUpdatesRoot)
                $ErrorActionPreference = 'Stop'
                {{pointerFunctions}}
                Get-GeoraePlanDurableUpdatePointerSha256 `
                    -DurableUpdatesRoot $DurableUpdatesRoot `
                    -Channel 'stable'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var rejected = await RunPowerShellAsync(
                harnessPath,
                ("-DurableUpdatesRoot", durableRoot));

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "not a regular file",
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                File.Delete(pointerPath);
            }
            catch (IOException)
            {
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("main-blank", "platform main asset fileName is required")]
    [InlineData("main-missing-size", "asset size/binding is invalid")]
    [InlineData("main-missing-sha", "asset size/binding is invalid")]
    [InlineData("installer-null", "installer entry cannot be null")]
    [InlineData("installer-blank", "installer fileName is required")]
    [InlineData("installer-missing-size", "asset size/binding is invalid")]
    [InlineData("installer-missing-sha", "asset size/binding is invalid")]
    public async Task LinuxPcRelease_ManifestAssetValidationFailsClosedForMalformedRequiredMetadata(
        string malformedShape,
        string expectedError)
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var durableFunctions = ExtractLinuxReleaseHarnessSection(
            linuxSource,
            "function Assert-GeoraePlanLinuxRegularDirectoryChain",
            "function Assert-SafeReleaseId");
        var manifestAssetFunction = ExtractPowerShellScriptSection(
            linuxSource,
            "function Assert-GeoraePlanLinuxManifestReferencedAssets",
            "function Copy-GeoraePlanDurableUpdateAssets");
        Assert.Contains(
            "platform main asset fileName is",
            manifestAssetFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "installer entry cannot be",
            manifestAssetFunction,
            StringComparison.Ordinal);
        Assert.Contains(
            "installer fileName is",
            manifestAssetFunction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$null -ne $installer -and",
            manifestAssetFunction,
            StringComparison.Ordinal);

        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-manifest-required-metadata-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var updatesRoot = Path.Combine(projectRoot, "updates");
        var manifestRoot = Path.Combine(updatesRoot, "manifest");
        var desktopRoot = Path.Combine(
            updatesRoot,
            "downloads",
            "desktop");
        try
        {
            Directory.CreateDirectory(manifestRoot);
            Directory.CreateDirectory(desktopRoot);
            var mainPath = Path.Combine(desktopRoot, "desktop.zip");
            var installerPath = Path.Combine(
                desktopRoot,
                "desktop-installer.exe");
            File.WriteAllText(mainPath, "main-package");
            File.WriteAllText(installerPath, "installer-package");
            var mainNode = new Dictionary<string, object?>
            {
                ["platform"] = "desktop",
                ["fileName"] = malformedShape == "main-blank"
                    ? " "
                    : Path.GetFileName(mainPath),
                ["fileSize"] = new FileInfo(mainPath).Length,
                ["sha256"] = ComputeSha256(mainPath)
            };
            if (malformedShape == "main-missing-size")
                mainNode.Remove("fileSize");
            if (malformedShape == "main-missing-sha")
                mainNode.Remove("sha256");

            var installerNode = new Dictionary<string, object?>
            {
                ["fileName"] = malformedShape == "installer-blank"
                    ? "\t"
                    : Path.GetFileName(installerPath),
                ["fileSize"] = new FileInfo(installerPath).Length,
                ["sha256"] = ComputeSha256(installerPath)
            };
            if (malformedShape == "installer-missing-size")
                installerNode.Remove("fileSize");
            if (malformedShape == "installer-missing-sha")
                installerNode.Remove("sha256");
            mainNode["installers"] = malformedShape == "installer-null"
                ? new object?[] { null }
                : new object?[] { installerNode };
            File.WriteAllText(
                Path.Combine(manifestRoot, "stable.json"),
                JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["channel"] = "stable",
                        ["desktop"] = mainNode,
                        ["android"] = null
                    }));

            var harnessPath = Path.Combine(
                testRoot,
                "manifest-required-metadata-harness.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                param(
                    [string]$ProjectRoot,
                    [string]$UpdatesRoot)
                $ErrorActionPreference = 'Stop'
                {{durableFunctions}}
                Assert-GeoraePlanLinuxManifestReferencedAssets `
                    -UpdatesRoot $UpdatesRoot `
                    -ProjectRoot $ProjectRoot `
                    -Channel 'stable'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var rejected = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-UpdatesRoot", updatesRoot));

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                expectedError,
                rejected.StdOut + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_DurableUpdateWrapperRecoversAcrossReleaseIdsAfterHardKill()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-update-durable-wrapper-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var releaseWorkRoot = Path.Combine(projectRoot, "release-temp");
        var firstPublishRoot = Path.Combine(releaseWorkRoot, "linux-A");
        var secondPublishRoot = Path.Combine(releaseWorkRoot, "linux-B");
        var durableUpdatesRoot = Path.Combine(
            releaseWorkRoot,
            "linux-update-assets-stable");
        var updateAssetScript = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        try
        {
            Directory.CreateDirectory(firstPublishRoot);
            Directory.CreateDirectory(secondPublishRoot);
            WriteMinimalLinuxUpdatePointerFixture(
                projectRoot,
                firstPublishRoot);
            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var durableFunctions = ExtractLinuxReleaseHarnessSection(
                linuxReleaseSource,
                "function Assert-GeoraePlanLinuxRegularDirectoryChain",
                "function Assert-SafeReleaseId");
            var harnessPath = Path.Combine(testRoot, "durable-wrapper.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
                    [Parameter(Mandatory = $true)][string]$PublishRoot,
                    [Parameter(Mandatory = $true)][string]$UpdateAssetScript
                )
                $ErrorActionPreference = 'Stop'
                {{durableFunctions}}
                $arguments = @{
                    SkipAndroid = $true
                    DesktopVersion = '1.0.0'
                    SkipPackagePrune = $true
                }
                Invoke-GeoraePlanDurableUpdateAssetPublish `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $DurableUpdatesRoot `
                    -PublishRoot $PublishRoot `
                    -UpdateAssetScript $UpdateAssetScript `
                    -UpdateAssetArguments $arguments `
                    -Channel 'stable'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var interrupted = await RunPowerShellAsync(
                harnessPath,
                (
                    "GEORAEPLAN_RELEASE_TEST_KILL_POINT",
                    "AfterPreparationRootBeforeJournal"),
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", firstPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.NotEqual(0, interrupted.ExitCode);
            Assert.True(
                Directory.Exists(durableUpdatesRoot),
                interrupted.StdOut + Environment.NewLine + interrupted.StdErr);
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(
                durableUpdatesRoot,
                ".georaeplan-release-transaction-stable*",
                SearchOption.TopDirectoryOnly));

            var recovered = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", secondPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.True(
                recovered.ExitCode == 0,
                recovered.StdOut + Environment.NewLine + recovered.StdErr);
            Assert.Contains(
                "linux_update_recovery_root=reused",
                recovered.StdOut,
                StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(
                secondPublishRoot,
                "updates",
                "manifest",
                "stable.current.json")));
            Assert.False(Directory.Exists(durableUpdatesRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("AfterPublisherBeforePublishedState", "Seeded")]
    [InlineData("AfterPublishedStateBeforeCopy", "Published")]
    [InlineData("AfterDurableCopyBeforeCopiedState", "Published")]
    [InlineData("AfterCopiedStateBeforeCleanup", "Copied")]
    public async Task LinuxPcRelease_DurableUpdateWrapperReusesExactCommittedGenerationAcrossStateBoundaryKill(
        string killPoint,
        string expectedPersistedPhase)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
                repositoryRoot,
                "temp",
                "linux-wrapper-state-tests",
                Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var releaseWorkRoot = Path.Combine(projectRoot, "release-temp");
        var firstPublishRoot = Path.Combine(releaseWorkRoot, "linux-release-A");
        var secondPublishRoot = Path.Combine(releaseWorkRoot, "linux-release-B");
        var durableUpdatesRoot = Path.Combine(
            releaseWorkRoot,
            "linux-update-assets-stable");
        var updateAssetScript = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        try
        {
            Directory.CreateDirectory(firstPublishRoot);
            Directory.CreateDirectory(secondPublishRoot);
            WriteMinimalLinuxUpdatePointerFixture(
                projectRoot,
                firstPublishRoot);
            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var durableFunctions = ExtractLinuxReleaseHarnessSection(
                linuxReleaseSource,
                "function Assert-GeoraePlanLinuxRegularDirectoryChain",
                "function Assert-SafeReleaseId");
            var harnessPath = Path.Combine(testRoot, "durable-wrapper.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
                    [Parameter(Mandatory = $true)][string]$PublishRoot,
                    [Parameter(Mandatory = $true)][string]$UpdateAssetScript
                )
                $ErrorActionPreference = 'Stop'
                {{durableFunctions}}
                $arguments = @{
                    SkipAndroid = $true
                    DesktopVersion = '1.0.0'
                    SkipPackagePrune = $true
                }
                Invoke-GeoraePlanDurableUpdateAssetPublish `
                    -ProjectRoot $ProjectRoot `
                    -DurableUpdatesRoot $DurableUpdatesRoot `
                    -PublishRoot $PublishRoot `
                    -UpdateAssetScript $UpdateAssetScript `
                    -UpdateAssetArguments $arguments `
                    -Channel 'stable'
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var interrupted = await RunPowerShellAsync(
                harnessPath,
                (
                    "GEORAEPLAN_LINUX_UPDATE_WRAPPER_TEST_KILL_POINT",
                    killPoint),
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", firstPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.NotEqual(0, interrupted.ExitCode);
            var durablePointerPath = Path.Combine(
                durableUpdatesRoot,
                "manifest",
                "stable.current.json");
            Assert.True(
                File.Exists(durablePointerPath),
                interrupted.StdOut + Environment.NewLine + interrupted.StdErr);
            Assert.Empty(Directory.EnumerateFileSystemEntries(
                durableUpdatesRoot,
                ".georaeplan-release-transaction-stable*",
                SearchOption.TopDirectoryOnly));
            var committedPointerHash = ComputeSha256(durablePointerPath);
            using var committedPointer =
                JsonDocument.Parse(File.ReadAllText(durablePointerPath));
            var committedGenerationId = committedPointer.RootElement
                .GetProperty("generationId")
                .GetString();
            Assert.Matches("^[0-9a-f]{32}$", committedGenerationId);
            var statePath = Path.Combine(
                durableUpdatesRoot,
                ".georaeplan-linux-update-wrapper-state.json");
            using (var state = JsonDocument.Parse(File.ReadAllText(statePath)))
                Assert.Equal(
                    expectedPersistedPhase,
                    state.RootElement.GetProperty("phase").GetString());

            var recovered = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", secondPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.True(
                recovered.ExitCode == 0,
                recovered.StdOut + Environment.NewLine + recovered.StdErr);
            if (expectedPersistedPhase == "Seeded")
            {
                Assert.Contains(
                    "linux_update_wrapper_recovered=Published",
                    recovered.StdOut,
                    StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(
                    $"phase={expectedPersistedPhase}",
                    recovered.StdOut,
                    StringComparison.Ordinal);
            }
            var copiedPointerPath = Path.Combine(
                secondPublishRoot,
                "updates",
                "manifest",
                "stable.current.json");
            Assert.Equal(
                committedPointerHash,
                ComputeSha256(copiedPointerPath));
            using var copiedPointer =
                JsonDocument.Parse(File.ReadAllText(copiedPointerPath));
            Assert.Equal(
                committedGenerationId,
                copiedPointer.RootElement
                    .GetProperty("generationId")
                    .GetString());
            Assert.False(Directory.Exists(durableUpdatesRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("BeforePointerReplaceAfterCommitIntent")]
    [InlineData("AfterPointerReplaceBeforeCommitJournal")]
    public async Task LinuxPcRelease_DurableUpdateWrapperReusesPublisherCandidateAcrossPointerHardKill(
        string killPoint)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-wrapper-pointer-kill-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var releaseWorkRoot = Path.Combine(projectRoot, "release-temp");
        var firstPublishRoot =
            Path.Combine(releaseWorkRoot, "linux-release-A");
        var secondPublishRoot =
            Path.Combine(releaseWorkRoot, "linux-release-B");
        var durableUpdatesRoot = Path.Combine(
            releaseWorkRoot,
            "linux-update-assets-stable");
        var updateAssetScript = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        try
        {
            Directory.CreateDirectory(firstPublishRoot);
            Directory.CreateDirectory(secondPublishRoot);
            WriteMinimalLinuxUpdatePointerFixture(
                projectRoot,
                firstPublishRoot);
            var harnessPath = WriteDurableWrapperHarness(
                testRoot,
                repositoryRoot);

            var interrupted = await RunPowerShellAsync(
                harnessPath,
                ("GEORAEPLAN_RELEASE_TEST_KILL_POINT", killPoint),
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", firstPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.NotEqual(0, interrupted.ExitCode);
            var stagedPointerPath = Path.Combine(
                durableUpdatesRoot,
                ".georaeplan-release-transaction-stable",
                "staging",
                "manifest-pointer.json");
            Assert.True(
                File.Exists(stagedPointerPath),
                interrupted.StdOut + Environment.NewLine + interrupted.StdErr);
            var candidatePointerHash =
                ComputeSha256(stagedPointerPath);
            using var candidatePointer =
                JsonDocument.Parse(File.ReadAllText(stagedPointerPath));
            var candidateGeneration = candidatePointer.RootElement
                .GetProperty("generationId")
                .GetString();
            Assert.Matches("^[0-9a-f]{32}$", candidateGeneration);

            var recovered = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", secondPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.True(
                recovered.ExitCode == 0,
                recovered.StdOut + Environment.NewLine + recovered.StdErr);
            Assert.Contains(
                "release_startup_recovery=Committed",
                recovered.StdOut,
                StringComparison.Ordinal);
            var copiedPointerPath = Path.Combine(
                secondPublishRoot,
                "updates",
                "manifest",
                "stable.current.json");
            Assert.Equal(
                candidatePointerHash,
                ComputeSha256(copiedPointerPath));
            using var copiedPointer =
                JsonDocument.Parse(File.ReadAllText(copiedPointerPath));
            Assert.Equal(
                candidateGeneration,
                copiedPointer.RootElement
                    .GetProperty("generationId")
                    .GetString());
            Assert.False(Directory.Exists(durableUpdatesRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("DuringInitialBaselineCopy")]
    [InlineData("AfterPreparationPromoteBeforeSeededState")]
    [InlineData("DuringDurableCleanupDelete")]
    public async Task LinuxPcRelease_DurablePreparationAndCleanupResumeAcrossHardKill(
        string killPoint)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-wrapper-owner-kill-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var releaseWorkRoot = Path.Combine(projectRoot, "release-temp");
        var firstPublishRoot =
            Path.Combine(releaseWorkRoot, "linux-release-A");
        var secondPublishRoot =
            Path.Combine(releaseWorkRoot, "linux-release-B");
        var durableUpdatesRoot = Path.Combine(
            releaseWorkRoot,
            "linux-update-assets-stable");
        var preparationRoot =
            durableUpdatesRoot + ".preparing";
        var preparationOwnerPath =
            durableUpdatesRoot + ".preparing-owner.json";
        var cleanupRoot =
            durableUpdatesRoot + ".cleanup";
        var cleanupOwnerPath =
            durableUpdatesRoot + ".cleanup-owner.json";
        var updateAssetScript = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        try
        {
            foreach (var publishRoot in new[]
                     {
                         firstPublishRoot,
                         secondPublishRoot
                     })
            {
                var baselineManifestRoot = Path.Combine(
                    publishRoot,
                    "updates",
                    "manifest");
                Directory.CreateDirectory(baselineManifestRoot);
                File.WriteAllText(
                    Path.Combine(
                        baselineManifestRoot,
                        "stable.json"),
                    JsonSerializer.Serialize(new
                    {
                        channel = "stable",
                        desktop = (object?)null,
                        android = (object?)null
                    }));
            }
            var harnessPath = WriteDurableWrapperHarness(
                testRoot,
                repositoryRoot);
            var interrupted = await RunPowerShellAsync(
                harnessPath,
                (
                    "GEORAEPLAN_LINUX_UPDATE_WRAPPER_TEST_KILL_POINT",
                    killPoint),
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", firstPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.NotEqual(0, interrupted.ExitCode);
            if (killPoint == "DuringInitialBaselineCopy")
            {
                Assert.True(File.Exists(preparationOwnerPath));
                Assert.True(Directory.Exists(preparationRoot));
                Assert.False(Directory.Exists(durableUpdatesRoot));
            }
            else if (
                killPoint ==
                "AfterPreparationPromoteBeforeSeededState")
            {
                Assert.True(File.Exists(preparationOwnerPath));
                Assert.False(Directory.Exists(preparationRoot));
                Assert.True(Directory.Exists(durableUpdatesRoot));
            }
            else
            {
                Assert.True(File.Exists(cleanupOwnerPath));
                Assert.True(Directory.Exists(cleanupRoot));
                Assert.False(Directory.Exists(durableUpdatesRoot));
            }

            var recovered = await RunPowerShellAsync(
                harnessPath,
                ("-ProjectRoot", projectRoot),
                ("-DurableUpdatesRoot", durableUpdatesRoot),
                ("-PublishRoot", secondPublishRoot),
                ("-UpdateAssetScript", updateAssetScript));

            Assert.True(
                recovered.ExitCode == 0,
                recovered.StdOut + Environment.NewLine + recovered.StdErr);
            Assert.True(File.Exists(Path.Combine(
                secondPublishRoot,
                "updates",
                "manifest",
                "stable.current.json")));
            Assert.False(Directory.Exists(durableUpdatesRoot));
            Assert.False(Directory.Exists(preparationRoot));
            Assert.False(File.Exists(preparationOwnerPath));
            Assert.False(Directory.Exists(cleanupRoot));
            Assert.False(File.Exists(cleanupOwnerPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_RollbackBaselineFunction_CopiesBothVerifiedManifestsAndPackages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-update-baseline-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var publishRoot = Path.Combine(testRoot, "publish");
        var sourceManifestRoot = Path.Combine(projectRoot, "배포", "업데이트", "manifest");
        var sourceDesktopRoot = Path.Combine(projectRoot, "배포", "업데이트", "downloads", "desktop");

        try
        {
            Directory.CreateDirectory(sourceManifestRoot);
            Directory.CreateDirectory(sourceDesktopRoot);
            Directory.CreateDirectory(publishRoot);

            var currentPackage = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-current.zip", "current baseline");
            var currentInstaller = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-current.exe", "current installer");
            var previousPackage = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-previous.zip", "previous baseline");
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.json"),
                CreateRollbackTestManifestJson(
                    "1.2.0",
                    currentPackage,
                    currentInstaller));
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.previous.json"),
                CreateRollbackTestManifestJson("1.1.0", previousPackage));

            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var testScriptPath = Path.Combine(testRoot, "run-baseline-copy.ps1");
            var script = ExtractLinuxReleaseHarnessSection(
                             linuxReleaseSource,
                             "function Get-GeoraePlanLinuxRegularPointerItem",
                             "Assert-SafeReleaseId -Value $ReleaseId") + Environment.NewLine +
                         $"Copy-LocalUpdateRollbackBaseline -Root '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                         Environment.NewLine;
            File.WriteAllText(testScriptPath, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.True(
                result.ExitCode == 0,
                $"Continuity local guard harness failed.{Environment.NewLine}{result.StdOut}{Environment.NewLine}{result.StdErr}");
            Assert.Contains("update_rollback_baseline_seeded manifests=2 packages=3", result.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(publishRoot, "updates", "manifest", "stable.json")));
            Assert.True(File.Exists(Path.Combine(publishRoot, "updates", "manifest", "stable.previous.json")));
            Assert.True(File.Exists(Path.Combine(publishRoot, "updates", "downloads", "desktop", currentPackage.Name)));
            var copiedInstallerPath = Path.Combine(
                publishRoot,
                "updates",
                "downloads",
                "desktop",
                currentInstaller.Name);
            Assert.True(File.Exists(copiedInstallerPath));
            Assert.Equal(
                ComputeSha256(currentInstaller.FullName),
                ComputeSha256(copiedInstallerPath));
            Assert.True(File.Exists(Path.Combine(publishRoot, "updates", "downloads", "desktop", previousPackage.Name)));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("main-blank", "주 패키지")]
    [InlineData("main-missing-size", "주 패키지")]
    [InlineData("main-missing-sha", "주 패키지")]
    [InlineData("installer-null", "installer entry")]
    [InlineData("installer-blank", "installer")]
    [InlineData("installer-missing-size", "installer")]
    [InlineData("installer-missing-sha", "installer")]
    public async Task LinuxPcRelease_LocalRollbackBaselineRejectsMalformedRealManifestArtifacts(
        string malformedShape,
        string expectedError)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-local-baseline-metadata-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var publishRoot = Path.Combine(testRoot, "publish");
        var sourceUpdatesRoot = Path.Combine(
            projectRoot,
            "\uBC30\uD3EC",
            "\uC5C5\uB370\uC774\uD2B8");
        var sourceManifestRoot = Path.Combine(
            sourceUpdatesRoot,
            "manifest");
        var sourceDesktopRoot = Path.Combine(
            sourceUpdatesRoot,
            "downloads",
            "desktop");
        try
        {
            Directory.CreateDirectory(sourceManifestRoot);
            Directory.CreateDirectory(sourceDesktopRoot);
            Directory.CreateDirectory(publishRoot);
            var mainPackage = WriteRollbackTestPackage(
                sourceDesktopRoot,
                "desktop.zip",
                "main-package");
            var installerPackage = WriteRollbackTestPackage(
                sourceDesktopRoot,
                "desktop-installer.exe",
                "installer-package");
            var mainNode = new Dictionary<string, object?>
            {
                ["platform"] = "desktop",
                ["version"] = "1.2.0",
                ["packageUrl"] =
                    "/updates/download/desktop/desktop.zip",
                ["fileName"] = malformedShape == "main-blank"
                    ? " "
                    : mainPackage.Name,
                ["fileSize"] = mainPackage.Length,
                ["sha256"] = ComputeSha256(mainPackage.FullName)
            };
            if (malformedShape == "main-missing-size")
                mainNode.Remove("fileSize");
            if (malformedShape == "main-missing-sha")
                mainNode.Remove("sha256");

            var installerNode = new Dictionary<string, object?>
            {
                ["packageUrl"] =
                    "/updates/download/desktop/desktop-installer.exe",
                ["fileName"] = malformedShape == "installer-blank"
                    ? "\t"
                    : installerPackage.Name,
                ["fileSize"] = installerPackage.Length,
                ["sha256"] = ComputeSha256(installerPackage.FullName)
            };
            if (malformedShape == "installer-missing-size")
                installerNode.Remove("fileSize");
            if (malformedShape == "installer-missing-sha")
                installerNode.Remove("sha256");
            mainNode["installers"] = malformedShape == "installer-null"
                ? new object?[] { null }
                : new object?[] { installerNode };
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.json"),
                JsonSerializer.Serialize(
                    new Dictionary<string, object?>
                    {
                        ["channel"] = "stable",
                        ["desktop"] = mainNode,
                        ["android"] = null
                    }));

            var linuxSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var functions = ExtractLinuxReleaseHarnessSection(
                linuxSource,
                "function Get-GeoraePlanLinuxRegularPointerItem",
                "Assert-SafeReleaseId -Value $ReleaseId");
            var harnessPath = Path.Combine(
                testRoot,
                "copy-malformed-local-baseline.ps1");
            File.WriteAllText(
                harnessPath,
                functions + Environment.NewLine +
                $"Copy-LocalUpdateRollbackBaseline -Root '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var rejected = await RunPowerShellAsync(harnessPath);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                expectedError,
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(
                publishRoot,
                "updates",
                "downloads",
                "desktop",
                mainPackage.Name)));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxPcRelease_LocalRollbackBaselineRejectsSameLengthMainOrInstallerShaMismatch(
        bool tamperInstaller)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-local-baseline-sha-tests",
            (tamperInstaller ? "installer-" : "main-") +
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var publishRoot = Path.Combine(testRoot, "publish");
        var sourceUpdatesRoot = Path.Combine(
            projectRoot,
            "\uBC30\uD3EC",
            "\uC5C5\uB370\uC774\uD2B8");
        var sourceManifestRoot = Path.Combine(
            sourceUpdatesRoot,
            "manifest");
        var sourceDesktopRoot = Path.Combine(
            sourceUpdatesRoot,
            "downloads",
            "desktop");
        try
        {
            Directory.CreateDirectory(sourceManifestRoot);
            Directory.CreateDirectory(sourceDesktopRoot);
            Directory.CreateDirectory(publishRoot);
            var package = WriteRollbackTestPackage(
                sourceDesktopRoot,
                "desktop.zip",
                "baseline-bytes");
            var installer = WriteRollbackTestPackage(
                sourceDesktopRoot,
                "desktop-installer.exe",
                "installer-seed");
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.json"),
                CreateRollbackTestManifestJson("1.2.0", package, installer));
            var tamperedPath = tamperInstaller
                ? installer.FullName
                : package.FullName;
            var originalLength = new FileInfo(tamperedPath).Length;
            File.WriteAllText(tamperedPath, "tampered-bytes");
            Assert.Equal(originalLength, new FileInfo(tamperedPath).Length);

            var linuxSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var functions = ExtractLinuxReleaseHarnessSection(
                linuxSource,
                "function Get-GeoraePlanLinuxRegularPointerItem",
                "Assert-SafeReleaseId -Value $ReleaseId");
            var harnessPath = Path.Combine(
                testRoot,
                "copy-corrupt-local-baseline.ps1");
            File.WriteAllText(
                harnessPath,
                functions + Environment.NewLine +
                $"Copy-LocalUpdateRollbackBaseline -Root '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var rejected = await RunPowerShellAsync(harnessPath);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "SHA256",
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_LocalRollbackBaselinePreservesPointerSelectedGenerationEvidence()
    {
        const string generationId =
            "abcdef0123456789abcdef0123456789";
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-update-pointer-baseline-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var publishRoot = Path.Combine(testRoot, "publish");
        var deploymentRoot = Path.Combine(projectRoot, "\uBC30\uD3EC");
        var sourceUpdatesRoot = Path.Combine(
            deploymentRoot,
            "\uC5C5\uB370\uC774\uD2B8");
        var sourceManifestRoot =
            Path.Combine(sourceUpdatesRoot, "manifest");
        var runtimePath = Path.Combine(
            sourceManifestRoot,
            "generations",
            "stable",
            generationId + ".json");
        var deliveryPath = Path.Combine(
            deploymentRoot,
            ".georaeplan-release-generations",
            "stable",
            generationId + ".json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(deliveryPath)!);
            Directory.CreateDirectory(publishRoot);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    channel = "stable",
                    generationId,
                    desktop = (object?)null,
                    android = (object?)null
                });
            var previousBytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    channel = "stable",
                    generationId =
                        "0123456789abcdef0123456789abcdef",
                    desktop = (object?)null,
                    android = (object?)null
                });
            File.WriteAllBytes(runtimePath, manifestBytes);
            File.WriteAllBytes(deliveryPath, manifestBytes);
            File.WriteAllBytes(
                Path.Combine(sourceManifestRoot, "stable.json"),
                manifestBytes);
            File.WriteAllBytes(
                Path.Combine(sourceManifestRoot, "stable.previous.json"),
                previousBytes);
            var manifestHash =
                Convert.ToHexString(SHA256.HashData(manifestBytes));
            var pointer = new Dictionary<string, string>
            {
                ["owner"] = "georaeplan-release-manifest-pointer",
                ["schemaVersion"] = "1",
                ["channel"] = "stable",
                ["generationId"] = generationId,
                ["manifestRelativePath"] =
                    $"generations/stable/{generationId}.json",
                ["manifestSha256"] = manifestHash,
                ["manifestFileSize"] =
                    manifestBytes.LongLength.ToString(
                        CultureInfo.InvariantCulture),
                ["deliveryManifestPath"] = deliveryPath,
                ["deliveryManifestSha256"] = manifestHash,
                ["deliveryManifestFileSize"] =
                    manifestBytes.LongLength.ToString(
                        CultureInfo.InvariantCulture)
            };
            File.WriteAllBytes(
                Path.Combine(
                    sourceManifestRoot,
                    "stable.current.json"),
                JsonSerializer.SerializeToUtf8Bytes(pointer));

            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var script = ExtractLinuxReleaseHarnessSection(
                             linuxReleaseSource,
                             "function Assert-GeoraePlanLinuxRegularDirectoryChain",
                             "Assert-SafeReleaseId -Value $ReleaseId") +
                         Environment.NewLine +
                         $"Copy-LocalUpdateRollbackBaseline -Root '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                         Environment.NewLine;
            var harnessPath =
                Path.Combine(testRoot, "copy-pointer-baseline.ps1");
            File.WriteAllText(
                harnessPath,
                script,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(harnessPath);

            Assert.True(
                result.ExitCode == 0,
                result.StdOut + Environment.NewLine + result.StdErr);
            Assert.Contains(
                $"generation={generationId}",
                result.StdOut,
                StringComparison.Ordinal);
            var targetManifestRoot = Path.Combine(
                publishRoot,
                "updates",
                "manifest");
            Assert.Equal(
                manifestHash,
                ComputeSha256(Path.Combine(
                    targetManifestRoot,
                    "generations",
                    "stable",
                    generationId + ".json")));
            Assert.Equal(
                manifestHash,
                ComputeSha256(Path.Combine(
                    targetManifestRoot,
                    "delivery-generations",
                    "stable",
                    generationId + ".json")));
            Assert.True(File.Exists(Path.Combine(
                targetManifestRoot,
                "stable.current.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_VerifiedLiveRollbackBaselineHelper_CopiesVerifiedManifestAndPackages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-live-update-baseline-helper-tests",
            Guid.NewGuid().ToString("N"));
        var sourceUpdatesRoot = Path.Combine(testRoot, "source", "updates");
        var sourceManifestRoot = Path.Combine(sourceUpdatesRoot, "manifest");
        var sourceDesktopRoot = Path.Combine(sourceUpdatesRoot, "downloads", "desktop");
        var sourceAndroidRoot = Path.Combine(sourceUpdatesRoot, "downloads", "android");
        var publishRoot = Path.Combine(testRoot, "publish");

        try
        {
            Directory.CreateDirectory(sourceManifestRoot);
            Directory.CreateDirectory(sourceDesktopRoot);
            Directory.CreateDirectory(sourceAndroidRoot);
            Directory.CreateDirectory(publishRoot);

            var desktopPackage = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-live.zip", "desktop live baseline");
            var desktopInstaller = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-live.exe", "desktop live installer");
            var androidPackage = WriteRollbackTestPackage(sourceAndroidRoot, "android-live.apk", "android live baseline");
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.json"),
                CreateRollbackTestManifestJson(
                    new RollbackManifestPackage(
                        "desktop",
                        "1.2.0",
                        desktopPackage,
                        $"/updates/download/desktop/{Uri.EscapeDataString(desktopPackage.Name)}",
                        new[] { desktopInstaller }),
                    new RollbackManifestPackage(
                        "android",
                        "0.2.59",
                        androidPackage,
                        $"/updates/download/android/{Uri.EscapeDataString(androidPackage.Name)}")));

            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var testScriptPath = Path.Combine(testRoot, "run-verified-live-baseline-copy.ps1");
            var script = ExtractLinuxReleaseHarnessSection(
                             linuxReleaseSource,
                             "function Get-GeoraePlanLinuxRegularPointerItem",
                             "Assert-SafeReleaseId -Value $ReleaseId") + Environment.NewLine +
                         $"Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot -SourceUpdatesRoot '{EscapePowerShellSingleQuotedLiteral(sourceUpdatesRoot)}' -BaseUrl 'https://trade.2884.kr' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                         Environment.NewLine;
            File.WriteAllText(testScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("live_update_rollback_baseline_seeded manifests=1 packages=3", result.StdOut, StringComparison.Ordinal);
            var publishedManifestPath = Path.Combine(publishRoot, "updates", "manifest", "stable.json");
            var publishedDesktopPath = Path.Combine(publishRoot, "updates", "downloads", "desktop", desktopPackage.Name);
            var publishedInstallerPath = Path.Combine(publishRoot, "updates", "downloads", "desktop", desktopInstaller.Name);
            var publishedAndroidPath = Path.Combine(publishRoot, "updates", "downloads", "android", androidPackage.Name);
            Assert.True(File.Exists(publishedManifestPath));
            Assert.True(File.Exists(publishedDesktopPath));
            Assert.True(File.Exists(publishedInstallerPath));
            Assert.True(File.Exists(publishedAndroidPath));
            Assert.Equal("1.2.0", ReadManifestPlatformVersion(publishedManifestPath, "desktop"));
            Assert.Equal("0.2.59", ReadManifestPlatformVersion(publishedManifestPath, "android"));
            Assert.Equal(ComputeSha256(desktopPackage.FullName), ComputeSha256(publishedDesktopPath));
            Assert.Equal(ComputeSha256(desktopInstaller.FullName), ComputeSha256(publishedInstallerPath));
            Assert.Equal(ComputeSha256(androidPackage.FullName), ComputeSha256(publishedAndroidPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LinuxPcRelease_VerifiedLiveRollbackBaselineHelper_RejectsPackageUrlOutsideSameOriginOrAllowedBaseUrls()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "linux-live-update-baseline-helper-block-tests",
            Guid.NewGuid().ToString("N"));
        var sourceUpdatesRoot = Path.Combine(testRoot, "source", "updates");
        var sourceManifestRoot = Path.Combine(sourceUpdatesRoot, "manifest");
        var sourceDesktopRoot = Path.Combine(sourceUpdatesRoot, "downloads", "desktop");
        var publishRoot = Path.Combine(testRoot, "publish");

        try
        {
            Directory.CreateDirectory(sourceManifestRoot);
            Directory.CreateDirectory(sourceDesktopRoot);
            Directory.CreateDirectory(publishRoot);

            var desktopPackage = WriteRollbackTestPackage(sourceDesktopRoot, "desktop-cross-origin.zip", "desktop cross origin baseline");
            File.WriteAllText(
                Path.Combine(sourceManifestRoot, "stable.json"),
                CreateRollbackTestManifestJson(
                    new RollbackManifestPackage(
                        "desktop",
                        "1.2.0",
                        desktopPackage,
                        $"https://updates.evil.example.com/updates/download/desktop/{Uri.EscapeDataString(desktopPackage.Name)}")));

            var linuxReleaseSource = ReadRepositoryFile(
                "tools",
                "linux",
                "Publish-GeoraeplanLinuxPcRelease.ps1");
            var testScriptPath = Path.Combine(testRoot, "run-verified-live-baseline-block.ps1");
            var script = ExtractLinuxReleaseHarnessSection(
                             linuxReleaseSource,
                             "function Get-GeoraePlanLinuxRegularPointerItem",
                             "Assert-SafeReleaseId -Value $ReleaseId") + Environment.NewLine +
                         $"Copy-VerifiedLiveUpdateRollbackBaselineFromSourceUpdatesRoot -SourceUpdatesRoot '{EscapePowerShellSingleQuotedLiteral(sourceUpdatesRoot)}' -BaseUrl 'https://trade.2884.kr' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -Channel 'stable'" +
                         Environment.NewLine;
            File.WriteAllText(testScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "same-origin",
                result.StdOut + Environment.NewLine + result.StdErr,
                StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(publishRoot, "updates", "manifest", "stable.json")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
    [Fact]
    public void UpdateRollbackScript_VerifiesPackagesAndRequiresExplicitApplyBeforeAtomicSwap()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");

        Assert.Contains("[switch]$Apply", source, StringComparison.Ordinal);
        Assert.Contains("Test-ManifestPackage -Package $package", source, StringComparison.Ordinal);
        Assert.Contains("function Get-RollbackFileSha256", source, StringComparison.Ordinal);
        Assert.Contains("[Security.Cryptography.SHA256]::Create()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $Apply)", source, StringComparison.Ordinal);
        Assert.Contains("rollback_manifest=PREVIEW_OK", source, StringComparison.Ordinal);
        Assert.Contains(
            "function Read-VerifiedManifestPointer",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Get-RollbackRegularManifestPointerItem",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.FileAttributes]::ReparsePoint",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)",
            "$preflightPointerPath =",
            "Get-RollbackRegularManifestPointerItem `",
            "-AllowMissing",
            "$rollbackDeliveryPublishLease = $null");
        AssertInOrder(
            source,
            "$pointerItem =",
            "Get-RollbackRegularManifestPointerItem `",
            "-AllowMissing",
            "if ($null -ne $pointerItem)",
            "Read-VerifiedManifestPointer `");
        Assert.Contains(
            "function Get-VerifiedRollbackGeneration",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Read-RollbackTransactionJournal",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Resume-RollbackTransaction",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_ROLLBACK_TEST_KILL_POINT",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterRollbackPointerWrite",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($Channel -cnotin @('stable', 'test', 'beta'))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-RollbackPathWithinRoot",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Assert-RollbackPathHasNoReparsePoint",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "if (-not $Apply)",
            "$null = New-RollbackTransactionJournal `",
            "Resume-RollbackTransaction `");
    }

    [Fact]
    public async Task UpdateRollbackScript_PreviewDoesNotMutateAndApplySwapsVerifiedManifest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var scriptBytes = File.ReadAllBytes(scriptPath);
        Assert.True(
            scriptBytes.Length >= 3 &&
            scriptBytes[0] == 0xEF &&
            scriptBytes[1] == 0xBB &&
            scriptBytes[2] == 0xBF,
            "한글 배포 경로를 사용하는 rollback 스크립트는 Windows PowerShell 5.1용 UTF-8 BOM이 필요합니다.");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-rollback-tests",
            Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(testRoot, "updates");
        var manifestRoot = Path.Combine(outputRoot, "manifest");
        var desktopRoot = Path.Combine(outputRoot, "downloads", "desktop");
        var deliveryRoot = Path.Combine(testRoot, "배포");

        try
        {
            Directory.CreateDirectory(manifestRoot);
            Directory.CreateDirectory(desktopRoot);
            Directory.CreateDirectory(deliveryRoot);

            var currentPackage = WriteRollbackTestPackage(desktopRoot, "desktop-current.zip", "current package");
            var previousPackage = WriteRollbackTestPackage(desktopRoot, "desktop-previous.zip", "previous package");
            var currentJson = CreateRollbackTestManifestJson("1.2.0", currentPackage);
            var previousJson = CreateRollbackTestManifestJson("1.1.0", previousPackage);
            var currentManifestPath = Path.Combine(manifestRoot, "stable.json");
            var previousManifestPath = Path.Combine(manifestRoot, "stable.previous.json");
            var deliveryManifestPath = Path.Combine(deliveryRoot, "stable.json");
            File.WriteAllText(currentManifestPath, currentJson);
            File.WriteAllText(previousManifestPath, previousJson);
            File.WriteAllText(deliveryManifestPath, currentJson);

            var preview = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-OutputRoot", outputRoot));

            Assert.Equal(0, preview.ExitCode);
            Assert.Contains("rollback_manifest=PREVIEW_OK", preview.StdOut, StringComparison.Ordinal);
            Assert.Equal("1.2.0", ReadDesktopManifestVersion(currentManifestPath));
            Assert.Equal("1.1.0", ReadDesktopManifestVersion(previousManifestPath));

            var apply = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-OutputRoot", outputRoot),
                ("-Apply", null));

            Assert.Equal(0, apply.ExitCode);
            Assert.Contains("rollback_manifest=SWAPPED", apply.StdOut, StringComparison.Ordinal);
            Assert.Equal("1.1.0", ReadDesktopManifestVersion(currentManifestPath));
            Assert.Equal("1.2.0", ReadDesktopManifestVersion(previousManifestPath));
            Assert.Equal("1.1.0", ReadDesktopManifestVersion(deliveryManifestPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_RejectsPointerDirectoryBeforeLegacyFallback()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-pointer-directory-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = WritePointerRollbackFixture(testRoot);
            var currentHash = ComputeSha256(
                fixture.CurrentManifestPath);
            var previousHash = ComputeSha256(
                fixture.PreviousManifestPath);
            File.Delete(fixture.PointerPath);
            Directory.CreateDirectory(fixture.PointerPath);

            var rejected = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot));

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "not a regular file",
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(fixture.PointerPath));
            Assert.Equal(
                currentHash,
                ComputeSha256(fixture.CurrentManifestPath));
            Assert.Equal(
                previousHash,
                ComputeSha256(fixture.PreviousManifestPath));
            Assert.False(Directory.Exists(fixture.TransactionRoot));
            Assert.False(File.Exists(Path.Combine(
                fixture.OutputRoot,
                ".georaeplan-release-publish.lock")));
            Assert.False(File.Exists(Path.Combine(
                fixture.ProjectRoot,
                "\uBC30\uD3EC",
                ".georaeplan-release-publish-stable.lock")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateRollbackScript_RejectsNormalAndDanglingPointerSymlinksBeforeMutation(
        bool dangling)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-pointer-symlink-tests",
            Guid.NewGuid().ToString("N"));
        string pointerPath = string.Empty;
        try
        {
            Directory.CreateDirectory(testRoot);
            if (!TryCreateFileSymbolicLinkFixture(
                testRoot,
                out var symlinkUnavailableReason))
            {
                _ = symlinkUnavailableReason;
                return;
            }
            var fixture = WritePointerRollbackFixture(testRoot);
            pointerPath = fixture.PointerPath;
            var pointerBytes = File.ReadAllBytes(pointerPath);
            var currentHash = ComputeSha256(
                fixture.CurrentManifestPath);
            var previousHash = ComputeSha256(
                fixture.PreviousManifestPath);
            var externalPointerPath = Path.Combine(
                testRoot,
                "outside-pointer.json");
            File.Delete(pointerPath);
            if (!dangling)
                File.WriteAllBytes(externalPointerPath, pointerBytes);
            File.CreateSymbolicLink(
                pointerPath,
                externalPointerPath);

            var rejected = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot));

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "not a regular file",
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                currentHash,
                ComputeSha256(fixture.CurrentManifestPath));
            Assert.Equal(
                previousHash,
                ComputeSha256(fixture.PreviousManifestPath));
            Assert.False(Directory.Exists(fixture.TransactionRoot));
            Assert.False(File.Exists(Path.Combine(
                fixture.OutputRoot,
                ".georaeplan-release-publish.lock")));
            Assert.False(File.Exists(Path.Combine(
                fixture.ProjectRoot,
                "\uBC30\uD3EC",
                ".georaeplan-release-publish-stable.lock")));
            if (!dangling)
                Assert.Equal(pointerBytes, File.ReadAllBytes(externalPointerPath));
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(pointerPath))
                    File.Delete(pointerPath);
            }
            catch (IOException)
            {
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("AfterRollbackDeliveryGenerationWrite")]
    [InlineData("AfterRollbackCurrentWrite")]
    [InlineData("AfterRollbackPreviousWrite")]
    [InlineData("AfterRollbackDeliveryWrite")]
    [InlineData("AfterRollbackPointerWrite")]
    public async Task UpdateRollbackScript_ResumesExactPointerRollbackAcrossEveryWriteBoundaryHardKill(
        string killPoint)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-pointer-rollback-kill-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = WritePointerRollbackFixture(testRoot);

            var interrupted = await RunPowerShellAsync(
                scriptPath,
                ("GEORAEPLAN_ROLLBACK_TEST_KILL_POINT", killPoint),
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot),
                ("-Apply", null));

            Assert.NotEqual(0, interrupted.ExitCode);
            Assert.True(Directory.Exists(fixture.TransactionRoot));
            Assert.True(File.Exists(Path.Combine(
                fixture.TransactionRoot,
                "journal.json")));

            var recovered = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot),
                ("-Apply", null));

            Assert.True(
                recovered.ExitCode == 0,
                recovered.StdOut + Environment.NewLine + recovered.StdErr);
            Assert.Contains(
                $"rollback_manifest=SWAPPED generation={fixture.PreviousGenerationId}",
                recovered.StdOut,
                StringComparison.Ordinal);
            using (var pointer = JsonDocument.Parse(
                       File.ReadAllText(fixture.PointerPath)))
            {
                Assert.Equal(
                    fixture.PreviousGenerationId,
                    pointer.RootElement
                        .GetProperty("generationId")
                        .GetString());
            }
            Assert.Equal(
                fixture.PreviousManifestHash,
                ComputeSha256(fixture.CurrentManifestPath));
            Assert.Equal(
                fixture.CurrentManifestHash,
                ComputeSha256(fixture.PreviousManifestPath));
            Assert.Equal(
                fixture.PreviousManifestHash,
                ComputeSha256(fixture.DeliveryManifestPath));
            Assert.Equal(
                fixture.PreviousManifestHash,
                ComputeSha256(fixture.PreviousDeliveryGenerationPath));
            Assert.False(Directory.Exists(fixture.TransactionRoot));
            Assert.False(File.Exists(fixture.TransactionOwnerPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_RejectsTargetChangedAfterJournalPreImageWasCaptured()
    {
        var repositoryRoot = FindRepositoryRoot();
        var rollbackSource = ReadRepositoryFile(
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        AssertInOrder(
            rollbackSource,
            "$preserveBackup = $false",
            "$currentTargetState =",
            "$stagedTargetLease = [IO.File]::Open(",
            "[IO.File]::Replace(",
            "$restoreDiscardState =",
            "$preserveRestoreDiscard = $true");
        Assert.Contains(
            "if (-not $preserveBackup)",
            rollbackSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (-not $preserveRestoreDiscard)",
            rollbackSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$copySucceeded = Copy-RollbackStagedFileAtomically `",
            rollbackSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (-not $copySucceeded)",
            rollbackSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$resumeSucceeded = Resume-RollbackTransaction `",
            rollbackSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "rollback_commit_conflict_hard_stop",
            rollbackSource,
            StringComparison.Ordinal);
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-rollback-preimage-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = WritePointerRollbackFixture(testRoot);
            var interrupted = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_ROLLBACK_TEST_KILL_POINT",
                    "AfterRollbackCurrentWrite"),
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot),
                ("-Apply", null));

            Assert.NotEqual(0, interrupted.ExitCode);
            File.WriteAllText(
                fixture.PreviousManifestPath,
                "third-party replacement bytes");

            var rejected = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", fixture.ProjectRoot),
                ("-OutputRoot", fixture.OutputRoot),
                ("-Apply", null));

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "pre-image",
                rejected.StdOut + Environment.NewLine + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(
                fixture.TransactionRoot,
                "journal.json")));
            Assert.Equal(
                "third-party replacement bytes",
                File.ReadAllText(fixture.PreviousManifestPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_PreservesConcurrentCommitConflictEvidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var rollbackSource = ReadRepositoryFile(
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var rollbackFunctions = ExtractPowerShellScriptSection(
            rollbackSource,
            "function Get-RollbackFileSha256",
            "function New-RollbackTransactionJournal");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "rollback-commit-conflict-tests",
            Guid.NewGuid().ToString("N"));
        var stagedPath = Path.Combine(testRoot, "staged.json");
        var targetPath = Path.Combine(testRoot, "target.json");
        var originalMovedPath = Path.Combine(testRoot, "original-moved.json");
        var stagedMovedPath = Path.Combine(testRoot, "staged-moved.json");
        var readyPath = Path.Combine(testRoot, "pause.ready");
        var releasePath = Path.Combine(testRoot, "pause.release");

        static async Task WaitForPauseAsync(
            string readyPath,
            string expectedPoint)
        {
            for (var attempt = 0; attempt < 600; attempt++)
            {
                if (File.Exists(readyPath))
                {
                    try
                    {
                        if (string.Equals(
                            File.ReadAllText(readyPath),
                            expectedPoint,
                            StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"Rollback pause point was not reached: {expectedPoint}");
        }

        Process? process = null;
        try
        {
            Directory.CreateDirectory(testRoot);
            File.WriteAllText(stagedPath, "staged-A");
            File.WriteAllText(targetPath, "original-X");
            var stagedHash = ComputeSha256(stagedPath);
            var originalHash = ComputeSha256(targetPath);
            var harnessPath = Path.Combine(
                testRoot,
                "rollback-conflict-harness.ps1");
            File.WriteAllText(
                harnessPath,
                $$"""
                param(
                    [string]$SourcePath,
                    [string]$TargetPath,
                    [string]$StagedSha256,
                    [long]$StagedFileSize,
                    [string]$PreImageSha256,
                    [long]$PreImageFileSize)
                Set-StrictMode -Version Latest
                $ErrorActionPreference = 'Stop'
                {{rollbackFunctions}}
                try {
                    $copySucceeded = Copy-RollbackStagedFileAtomically `
                        -SourcePath $SourcePath `
                        -TargetPath $TargetPath `
                        -ExpectedSha256 $StagedSha256 `
                        -ExpectedFileSize $StagedFileSize `
                        -ExpectedPreImageExists $true `
                        -ExpectedPreImageSha256 $PreImageSha256 `
                        -ExpectedPreImageFileSize $PreImageFileSize
                    if (-not $copySucceeded) {
                        [Console]::Error.WriteLine(
                            'rollback_commit_conflict: ' +
                            [string]$script:rollbackCommitConflictMessage)
                        exit 92
                    }
                }
                catch {
                    [Console]::Error.WriteLine([string]$_)
                    exit 91
                }
                """,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_POINTS"] =
                "BeforeRollbackTargetReplace;" +
                "BeforeRollbackConflictRestoreCheck";
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_READY_PATH"] = readyPath;
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_RELEASE_PATH"] = releasePath;
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                harnessPath,
                "-SourcePath",
                stagedPath,
                "-TargetPath",
                targetPath,
                "-StagedSha256",
                stagedHash,
                "-StagedFileSize",
                new FileInfo(stagedPath).Length.ToString(),
                "-PreImageSha256",
                originalHash,
                "-PreImageFileSize",
                new FileInfo(targetPath).Length.ToString()
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await WaitForPauseAsync(
                readyPath,
                "BeforeRollbackTargetReplace");
            File.Move(targetPath, originalMovedPath);
            File.WriteAllText(targetPath, "concurrent-B");
            File.WriteAllText(
                releasePath,
                "BeforeRollbackTargetReplace");

            await WaitForPauseAsync(
                readyPath,
                "BeforeRollbackConflictRestoreCheck");
            File.Move(targetPath, stagedMovedPath);
            File.WriteAllText(targetPath, "concurrent-C");
            File.WriteAllText(
                releasePath,
                "BeforeRollbackConflictRestoreCheck");

            Assert.True(
                process.WaitForExit(15_000),
                "Rollback conflict harness timed out.");
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(92, process.ExitCode);
            Assert.Contains(
                "rollback_commit_conflict",
                stdout + Environment.NewLine + stderr,
                StringComparison.Ordinal);
            Assert.Equal("concurrent-C", File.ReadAllText(targetPath));
            Assert.Equal("original-X", File.ReadAllText(originalMovedPath));
            Assert.Equal("staged-A", File.ReadAllText(stagedMovedPath));
            var preservedBackups = Directory.GetFiles(
                testRoot,
                ".*.backup",
                SearchOption.TopDirectoryOnly);
            var preservedBackup = Assert.Single(preservedBackups);
            Assert.Equal(
                "concurrent-B",
                File.ReadAllText(preservedBackup));
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_PropagatesCommitConflictToTopLevelExitOne()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "rollback-top-level-conflict-tests",
            Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(testRoot, "updates");
        var manifestRoot = Path.Combine(outputRoot, "manifest");
        var desktopRoot = Path.Combine(
            outputRoot,
            "downloads",
            "desktop");
        var deliveryRoot = Path.Combine(testRoot, "\uBC30\uD3EC");
        var currentPath = Path.Combine(manifestRoot, "stable.json");
        var previousPath = Path.Combine(
            manifestRoot,
            "stable.previous.json");
        var deliveryPath = Path.Combine(deliveryRoot, "stable.json");
        var originalMovedPath = Path.Combine(
            testRoot,
            "original-moved.json");
        var stagedMovedPath = Path.Combine(
            testRoot,
            "staged-moved.json");
        var readyPath = Path.Combine(testRoot, "pause.ready");
        var releasePath = Path.Combine(testRoot, "pause.release");

        static async Task WaitForPauseAsync(
            string readyPath,
            string expectedPoint)
        {
            for (var attempt = 0; attempt < 600; attempt++)
            {
                if (File.Exists(readyPath))
                {
                    try
                    {
                        if (string.Equals(
                            File.ReadAllText(readyPath),
                            expectedPoint,
                            StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                    catch (IOException)
                    {
                    }
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"Rollback pause point was not reached: {expectedPoint}");
        }

        Process? process = null;
        try
        {
            Directory.CreateDirectory(manifestRoot);
            Directory.CreateDirectory(desktopRoot);
            Directory.CreateDirectory(deliveryRoot);
            var currentPackage = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-current.zip",
                "current package");
            var previousPackage = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-previous.zip",
                "previous package");
            var currentJson =
                CreateRollbackTestManifestJson("1.2.0", currentPackage);
            var previousJson =
                CreateRollbackTestManifestJson("1.1.0", previousPackage);
            File.WriteAllText(currentPath, currentJson);
            File.WriteAllText(previousPath, previousJson);
            File.WriteAllText(deliveryPath, currentJson);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_POINTS"] =
                "BeforeRollbackTargetReplace;" +
                "BeforeRollbackConflictRestoreCheck";
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_READY_PATH"] = readyPath;
            process.StartInfo.Environment[
                "GEORAEPLAN_ROLLBACK_TEST_PAUSE_RELEASE_PATH"] = releasePath;
            foreach (var argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "-ProjectRoot",
                testRoot,
                "-OutputRoot",
                outputRoot,
                "-Apply"
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await WaitForPauseAsync(
                readyPath,
                "BeforeRollbackTargetReplace");
            File.Move(currentPath, originalMovedPath);
            File.WriteAllText(currentPath, "concurrent-B");
            File.WriteAllText(
                releasePath,
                "BeforeRollbackTargetReplace");

            await WaitForPauseAsync(
                readyPath,
                "BeforeRollbackConflictRestoreCheck");
            File.Move(currentPath, stagedMovedPath);
            File.WriteAllText(currentPath, "concurrent-C");
            File.WriteAllText(
                releasePath,
                "BeforeRollbackConflictRestoreCheck");

            Assert.True(
                process.WaitForExit(15_000),
                "Top-level rollback conflict process timed out.");
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(1, process.ExitCode);
            Assert.Contains(
                "rollback_commit_conflict",
                stdout + Environment.NewLine + stderr,
                StringComparison.Ordinal);
            Assert.Equal("concurrent-C", File.ReadAllText(currentPath));
            Assert.Equal(
                "1.2.0",
                ReadDesktopManifestVersion(originalMovedPath));
            Assert.Equal(
                "1.1.0",
                ReadDesktopManifestVersion(stagedMovedPath));
            var preservedBackup = Assert.Single(Directory.GetFiles(
                manifestRoot,
                ".*.backup",
                SearchOption.TopDirectoryOnly));
            Assert.Equal(
                "concurrent-B",
                File.ReadAllText(preservedBackup));
            Assert.True(File.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-update-rollback-stable",
                "journal.json")));

            using var deliveryLease = new FileStream(
                Path.Combine(
                    deliveryRoot,
                    ".georaeplan-release-publish-stable.lock"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            using var outputLease = new FileStream(
                Path.Combine(
                    outputRoot,
                    ".georaeplan-release-publish.lock"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_CompletedJournalAndCleanupOwnerFailClosedOnCorruption()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-rollback-completed-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            var indexFixture = WritePointerRollbackFixture(
                Path.Combine(testRoot, "bad-index"));
            var completedKill = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_ROLLBACK_TEST_KILL_POINT",
                    "AfterRollbackCompletedJournal"),
                ("-ProjectRoot", indexFixture.ProjectRoot),
                ("-OutputRoot", indexFixture.OutputRoot),
                ("-Apply", null));
            Assert.NotEqual(0, completedKill.ExitCode);

            var journalPath = Path.Combine(
                indexFixture.TransactionRoot,
                "journal.json");
            var journal = JsonSerializer.Deserialize<
                Dictionary<string, object?>>(
                    File.ReadAllText(journalPath));
            Assert.NotNull(journal);
            journal!["nextIndex"] = "0";
            File.WriteAllText(
                journalPath,
                JsonSerializer.Serialize(journal));

            var badIndex = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", indexFixture.ProjectRoot),
                ("-OutputRoot", indexFixture.OutputRoot),
                ("-Apply", null));
            Assert.NotEqual(0, badIndex.ExitCode);
            Assert.True(File.Exists(journalPath));

            var cleanupFixture = WritePointerRollbackFixture(
                Path.Combine(testRoot, "cleanup-target"));
            var cleanupKill = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_ROLLBACK_TEST_KILL_POINT",
                    "AfterRollbackCleanupRootDelete"),
                ("-ProjectRoot", cleanupFixture.ProjectRoot),
                ("-OutputRoot", cleanupFixture.OutputRoot),
                ("-Apply", null));
            Assert.NotEqual(0, cleanupKill.ExitCode);
            Assert.False(Directory.Exists(cleanupFixture.TransactionRoot));
            Assert.True(File.Exists(cleanupFixture.TransactionOwnerPath));

            File.WriteAllText(
                cleanupFixture.CurrentManifestPath,
                "later publisher bytes");
            var changedFinalTarget = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", cleanupFixture.ProjectRoot),
                ("-OutputRoot", cleanupFixture.OutputRoot),
                ("-Apply", null));
            Assert.NotEqual(0, changedFinalTarget.ExitCode);
            Assert.Contains(
                "Completed",
                changedFinalTarget.StdOut + Environment.NewLine +
                changedFinalTarget.StdErr,
                StringComparison.Ordinal);
            Assert.True(File.Exists(cleanupFixture.TransactionOwnerPath));

            var exactCleanupFixture = WritePointerRollbackFixture(
                Path.Combine(testRoot, "cleanup-exact"));
            var exactCleanupKill = await RunPowerShellAsync(
                scriptPath,
                (
                    "GEORAEPLAN_ROLLBACK_TEST_KILL_POINT",
                    "AfterRollbackCleanupRootDelete"),
                ("-ProjectRoot", exactCleanupFixture.ProjectRoot),
                ("-OutputRoot", exactCleanupFixture.OutputRoot),
                ("-Apply", null));
            Assert.NotEqual(0, exactCleanupKill.ExitCode);
            var exactCleanup = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", exactCleanupFixture.ProjectRoot),
                ("-OutputRoot", exactCleanupFixture.OutputRoot),
                ("-Apply", null));
            Assert.Equal(0, exactCleanup.ExitCode);
            Assert.Contains(
                "rollback_cleanup_recovery=complete",
                exactCleanup.StdOut,
                StringComparison.Ordinal);
            Assert.False(File.Exists(
                exactCleanupFixture.TransactionOwnerPath));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateRollbackScript_SharesPublisherLockOrderAndReleasesAfterTimeout()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-rollback-lock-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "project");
        var outputRoot = Path.Combine(testRoot, "updates");
        var deliveryRoot = Path.Combine(projectRoot, "\uBC30\uD3EC");
        var deliveryLockPath = Path.Combine(
            deliveryRoot,
            ".georaeplan-release-publish-stable.lock");
        var outputLockPath = Path.Combine(
            outputRoot,
            ".georaeplan-release-publish.lock");

        try
        {
            Directory.CreateDirectory(deliveryRoot);
            Directory.CreateDirectory(outputRoot);
            using var heldOutputLock = new FileStream(
                outputLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add("-ProjectRoot");
            process.StartInfo.ArgumentList.Add(projectRoot);
            process.StartInfo.ArgumentList.Add("-OutputRoot");
            process.StartInfo.ArgumentList.Add(outputRoot);
            process.StartInfo.ArgumentList.Add("-LockTimeoutSeconds");
            process.StartInfo.ArgumentList.Add("2");
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var deliveryWasHeldFirst = false;
            for (var attempt = 0; attempt < 60 && !deliveryWasHeldFirst; attempt++)
            {
                if (File.Exists(deliveryLockPath))
                {
                    try
                    {
                        using var competingDeliveryLock = new FileStream(
                            deliveryLockPath,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None);
                    }
                    catch (IOException)
                    {
                        deliveryWasHeldFirst = true;
                    }
                }

                if (!deliveryWasHeldFirst)
                    await Task.Delay(25);
            }

            Assert.True(
                process.WaitForExit(10_000),
                "Rollback lock contention process timed out.");
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(deliveryWasHeldFirst);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "Timed out waiting for the shared release publish lock",
                stdout + Environment.NewLine + stderr,
                StringComparison.Ordinal);

            heldOutputLock.Dispose();
            using var releasedDeliveryLock = new FileStream(
                deliveryLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            using var releasedOutputLock = new FileStream(
                outputLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("tampered")]
    public async Task UpdateRollbackScript_RejectsMissingOrTamperedDesktopInstaller(
        string mutation)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "manifest-rollback-installer-tests",
            Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(testRoot, "updates");
        var manifestRoot = Path.Combine(outputRoot, "manifest");
        var desktopRoot =
            Path.Combine(outputRoot, "downloads", "desktop");
        var deliveryRoot = Path.Combine(testRoot, "\uBC30\uD3EC");

        try
        {
            Directory.CreateDirectory(manifestRoot);
            Directory.CreateDirectory(desktopRoot);
            Directory.CreateDirectory(deliveryRoot);
            var currentPackage = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-current.zip",
                "current package");
            var previousPackage = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-previous.zip",
                "previous package");
            var previousExe = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-previous.exe",
                "previous exe");
            var previousMsi = WriteRollbackTestPackage(
                desktopRoot,
                "desktop-previous.msi",
                "previous msi");
            var currentJson =
                CreateRollbackTestManifestJson("2.0.0", currentPackage);
            var previousJson = JsonSerializer.Serialize(new
            {
                channel = "stable",
                desktop = new
                {
                    platform = "desktop",
                    version = "1.0.0",
                    fileName = previousPackage.Name,
                    fileSize = previousPackage.Length,
                    sha256 = ComputeSha256(previousPackage.FullName),
                    installers = new[]
                    {
                        new
                        {
                            fileName = previousExe.Name,
                            fileSize = previousExe.Length,
                            sha256 = ComputeSha256(previousExe.FullName)
                        },
                        new
                        {
                            fileName = previousMsi.Name,
                            fileSize = previousMsi.Length,
                            sha256 = ComputeSha256(previousMsi.FullName)
                        }
                    }
                },
                android = (object?)null
            });
            var currentManifestPath =
                Path.Combine(manifestRoot, "stable.json");
            var currentHash = ComputeSha256AfterWrite(
                currentManifestPath,
                currentJson);
            File.WriteAllText(
                Path.Combine(manifestRoot, "stable.previous.json"),
                previousJson);
            File.WriteAllText(
                Path.Combine(deliveryRoot, "stable.json"),
                currentJson);

            if (mutation == "missing")
                File.Delete(previousExe.FullName);
            else
                File.WriteAllText(previousExe.FullName, "tampered installer");

            var result = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-OutputRoot", outputRoot));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "installer",
                result.StdOut + Environment.NewLine + result.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                currentHash,
                ComputeSha256(currentManifestPath));
            Assert.False(Directory.Exists(Path.Combine(
                outputRoot,
                ".georaeplan-update-rollback-stable")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("Stable")]
    [InlineData(" stable")]
    [InlineData("preview")]
    [InlineData("../stable")]
    public async Task UpdateRollbackScript_RejectsNonCanonicalChannelBeforePathUse(
        string channel)
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Restore-GeoraePlanPreviousUpdateManifest.ps1");
        var result = await RunPowerShellAsync(
            scriptPath,
            ("-ProjectRoot", repositoryRoot),
            ("-OutputRoot", Path.Combine(repositoryRoot, "temp")),
            ("-Channel", channel));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "lowercase stable",
            result.StdOut + Environment.NewLine + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PaidDeliveryPackaging_RemovesSymbolsAndProvidesOptionalAuthenticodeGate()
    {
        var installerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var signingSource = ReadRepositoryFile(
            "tools",
            "release",
            "Test-GeoraePlanWindowsSigning.ps1");
        var fullReleaseSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("Get-ChildItem -LiteralPath $appRoot -Recurse -File -Filter '*.pdb'", installerSource, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Force -ErrorAction Stop", installerSource, StringComparison.Ordinal);
        Assert.Contains("Microsoft.PowerShell.Security\\Get-AuthenticodeSignature", signingSource, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireSigned", signingSource, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode=PASS", signingSource, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode=WARNING_UNSIGNED", signingSource, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireWindowsAuthenticode", fullReleaseSource, StringComparison.Ordinal);
        Assert.Contains("Test-GeoraePlanWindowsSigning.ps1", fullReleaseSource, StringComparison.Ordinal);
        Assert.Contains("$windowsSigningCheckArgs += '-RequireSigned'", fullReleaseSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdaterFailureWindow_ProvidesCopyableLogDiagnosticsAndStaysOpen()
    {
        var windowSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "UpdateProgressWindow.cs");
        var programSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("public void ShowFailure(string title, string detail, string? logPath = null)", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"로그 복사\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"로그 위치 열기\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"닫기\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetClipboardTextWithRetry(content.ToString())", windowSource, StringComparison.Ordinal);
        Assert.Contains("private static void SetClipboardTextWithRetry(string text)", windowSource, StringComparison.Ordinal);
        Assert.Contains("CanOpenClipboardForProbe()", windowSource, StringComparison.Ordinal);
        Assert.Contains("File.ReadAllText(_failureLogPath!, Encoding.UTF8)", windowSource, StringComparison.Ordinal);
        Assert.Contains("FileName = \"explorer.exe\"", windowSource, StringComparison.Ordinal);
        Assert.Contains("Arguments = \"/select,\" + QuoteExplorerArgument(_failureLogPath!)", windowSource, StringComparison.Ordinal);
        Assert.Contains("app.ShutdownMode = ShutdownMode.OnExplicitShutdown;", programSource, StringComparison.Ordinal);
        Assert.Contains("window.Closed += (_, _) => app.Shutdown(1);", programSource, StringComparison.Ordinal);
        Assert.Contains("window.ShowFailure(\"업데이트를 완료하지 못했습니다.\", message, _sessionLogPath);", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxButton.OK", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdater_CreatesRequestMetadataOnlyForDownloadPathAndDeletesItWhenLaunchFails()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");

        Assert.Contains("var requestMetadataPath = string.IsNullOrWhiteSpace(preparedPackageFullPath)", source, StringComparison.Ordinal);
        Assert.Contains("? CreateUpdaterRequestMetadataFile(stagedUpdaterPath, packageUri)", source, StringComparison.Ordinal);
        Assert.Contains(": null;", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteSensitiveFile(requestMetadataPath);", source, StringComparison.Ordinal);
        var startUpdateStart = source.IndexOf("private async Task StartUpdateCoreAsync", StringComparison.Ordinal);
        var startUpdateEnd = source.IndexOf("private static async Task<string?> ValidatePreparedPackagePathAsync", StringComparison.Ordinal);
        Assert.True(startUpdateStart >= 0 && startUpdateEnd > startUpdateStart);
        var startUpdateSource = source[startUpdateStart..startUpdateEnd];
        AssertInOrder(
            startUpdateSource,
            "var preparedPackageFullPath = await ValidatePreparedPackagePathAsync(preparedPackagePath, package)",
            "var stagedUpdaterPath = StageUpdaterForExecution(updaterPath);",
            "EnsureSufficientDiskSpace(package.FileSize, installRoot);",
            "TryCleanupStaleUpdateArtifacts();",
            "var requestMetadataPath = string.IsNullOrWhiteSpace(preparedPackageFullPath)",
            "var startInfo = new ProcessStartInfo",
            "using var updaterProcess = Process.Start(startInfo)",
            "TryDeleteSensitiveFile(requestMetadataPath);");
    }

    [Fact]
    public void DesktopUpdater_RequestMetadataFileDoesNotPersistPlaintextAuthorization()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "metadata-acl-tests",
            Guid.NewGuid().ToString("N"));
        var metadataPath = Path.Combine(tempRoot, "request-metadata.json");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var method = typeof(DesktopAppUpdateService).GetMethod(
                "WriteSensitiveUpdaterMetadataFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var protectMethod = typeof(DesktopAppUpdateService).GetMethod(
                "ProtectUpdaterMetadataValue",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(protectMethod);

            var protectedValue = Assert.IsType<string>(protectMethod!.Invoke(null, ["Bearer secret-token"]));
            Assert.False(string.IsNullOrWhiteSpace(protectedValue));
            Assert.DoesNotContain("secret-token", protectedValue, StringComparison.Ordinal);

            method!.Invoke(null, [metadataPath, $"{{\"ProtectedHeaders\":{{\"Authorization\":\"{protectedValue}\"}}}}"]);

            Assert.True(File.Exists(metadataPath));
            var json = File.ReadAllText(metadataPath);
            Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", json, StringComparison.Ordinal);
            Assert.Contains("ProtectedHeaders", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopAndUpdater_ShareDpapiProtectedRequestMetadataContract()
    {
        var desktopSource = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var updaterSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("ProtectedHeaders = headers.ToDictionary", desktopSource, StringComparison.Ordinal);
        Assert.Contains("ProtectedData.Protect", desktopSource, StringComparison.Ordinal);
        Assert.Contains("DataProtectionScope.CurrentUser", desktopSource, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(plainBytes);", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new UpdaterRequestMetadata\r\n        {\r\n            Headers =", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new UpdaterRequestMetadata\n        {\n            Headers =", desktopSource, StringComparison.Ordinal);

        Assert.Contains("public Dictionary<string, string> Headers", updaterSource, StringComparison.Ordinal);
        Assert.Contains("public Dictionary<string, string> ProtectedHeaders", updaterSource, StringComparison.Ordinal);
        Assert.Contains("ProtectedData.Unprotect", updaterSource, StringComparison.Ordinal);
        Assert.Contains("DataProtectionScope.CurrentUser", updaterSource, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(plainBytes);", updaterSource, StringComparison.Ordinal);
        AssertInOrder(
            updaterSource,
            "foreach (var header in Headers)",
            "ApplyHeader(request, header.Key, header.Value);",
            "foreach (var header in ProtectedHeaders)",
            "ApplyHeader(request, header.Key, UnprotectMetadataValue(header.Value));");
    }

    [Fact]
    public void DesktopUpdater_AcceptsOnlySameAuthorityDesktopZipDownloadPackageUri()
    {
        var baseUri = new Uri("https://trade.example.com");

        var accepted = InvokeValidatePackageUri(
            "https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip",
            baseUri);

        Assert.Equal("https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip", accepted.ToString());

        AssertValidatePackageUriRejected(
            "https://trade.example.com:444/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/android/tradeplan-android-v0.2.65.apk",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/desktop/%2e%2e%2ftradeplan-pc-installer-v1.1.552.zip",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.exe",
            baseUri);
    }

    [Fact]
    public void ReleasePackagingScripts_PreferProjectOrDDriveTempBeforeSystemTempFallback()
    {
        var initializeTempSource = ReadRepositoryFile(
            "tools",
            "common",
            "Initialize-GeoraePlanTemp.ps1");

        Assert.Contains("$env:GEORAEPLAN_TEMP_ROOT = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TEMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$effectiveProjectRoot = $ProjectRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path 'D:\\", initializeTempSource, StringComparison.Ordinal);
        AssertInOrder(
            initializeTempSource,
            "Join-Path $effectiveProjectRoot 'temp'",
            "$env:TEMP");

        var updateAssetsSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains("function Resolve-GeoraePlanScriptTempDirectory", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains("@($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains("$tempDirectory = Join-Path (Resolve-GeoraePlanScriptTempDirectory)", updateAssetsSource, StringComparison.Ordinal);

        var desktopInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopNativeInstallers.ps1");

        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        AssertInOrder(
            desktopInstallerSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "$stagingRoot = Join-Path ([System.IO.Path]::GetPathRoot($ProjectRoot)) 'GeoraePlanInstallerBuild'");
        AssertInOrder(
            desktopInstallerSource,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.GetTempPath()");

        var desktopZipInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");

        AssertInOrder(
            desktopZipInstallerSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "& powershell @nativeInstallerArguments");

        var androidBuildSource = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", androidBuildSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", androidBuildSource, StringComparison.Ordinal);
        AssertInOrder(
            androidBuildSource,
            "$ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "$resolvedDotNetPath = Get-ResolvedDotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath",
            "$publishResult = Invoke-DotnetPublishAndRelay");

        var fullReleaseSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        AssertInOrder(
            fullReleaseSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "& powershell @desktopArgs");

        var linuxReleaseSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Resolve-GeoraePlanScriptTempDirectory", linuxReleaseSource, StringComparison.Ordinal);
        Assert.Contains("@($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())", linuxReleaseSource, StringComparison.Ordinal);
        Assert.Contains(
            "$tarProcess.StandardOutput.BaseStream.CopyToAsync($sshProcess.StandardInput.BaseStream)",
            linuxReleaseSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$archiveDirectory = Resolve-GeoraePlanScriptTempDirectory",
            linuxReleaseSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopInstallerBuild_FailsFastWhenUpdaterPublishFails()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-failfast-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"), "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(testRoot, "Desktop", "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>9.9.999</Version>
                  </PropertyGroup>
                </Project>
                """);

            var updaterProjectDirectory = Path.Combine(testRoot, "Updater", "거래플랜.Updater");
            Directory.CreateDirectory(updaterProjectDirectory);
            File.WriteAllText(
                Path.Combine(updaterProjectDirectory, "거래플랜.Updater.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid\"}}");
            File.WriteAllText(Path.Combine(sourceFolder, "거래플랜.exe"), "fake desktop exe");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                echo %*>>"%~dp0dotnet-args.txt"
                exit /b 37
                """);

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
            process.StartInfo.ArgumentList.Add("-ProjectRoot");
            process.StartInfo.ArgumentList.Add(testRoot);
            process.StartInfo.ArgumentList.Add("-SourceFolder");
            process.StartInfo.ArgumentList.Add(sourceFolder);
            process.StartInfo.ArgumentList.Add("-OutputRoot");
            process.StartInfo.ArgumentList.Add(Path.Combine(testRoot, "output"));
            process.StartInfo.ArgumentList.Add("-SkipNativeInstallers");
            process.StartInfo.Environment["DOTNET_EXE"] = fakeDotnetPath;
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(60_000);
            Assert.True(exited, "Desktop installer build fail-fast test timed out.");

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "Failed to publish updater for desktop package.",
                stdout + stderr,
                StringComparison.Ordinal);
            Assert.True(
                File.Exists(Path.Combine(testRoot, "dotnet-args.txt")),
                "Fake dotnet should have been invoked for updater publish.");
            Assert.False(
                File.Exists(Path.Combine(testRoot, "output", "관리자용", "거래플랜-PC-설치패키지.zip")),
                "Installer package must not be created when updater publish fails.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopInstallerBuild_CanonicalizesExplicitSourceFolderBeforeCompressing()
    {
        var versionedDesktopFixture = GetVersionedDesktopFixture();
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "installer-explicit-source-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(
                Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(
                testRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(
                    desktopProjectDirectory,
                    "거래플랜.Desktop.App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>{versionedDesktopFixture.Version}</Version>
                  </PropertyGroup>
                </Project>
                """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(
                Path.Combine(sourceFolder, "Updater"));
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://source.example.invalid\"}}");
            File.Copy(
                versionedDesktopFixture.Path,
                Path.Combine(sourceFolder, "거래플랜.Desktop.App.exe"),
                overwrite: true);
            File.WriteAllText(
                Path.Combine(sourceFolder, "거래플랜.exe"),
                "stale display alias");
            File.WriteAllText(
                Path.Combine(
                    sourceFolder,
                    "Updater",
                    "거래플랜.Updater.exe"),
                "updater executable");

            var fakeDotnetPath = Path.Combine(
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

            var outputRoot = Path.Combine(testRoot, "output");
            var tempRoot = Path.Combine(testRoot, "temp");
            Directory.CreateDirectory(tempRoot);
            var buildResult = await RunPowerShellAsync(
                scriptPath,
                ("DOTNET_EXE", fakeDotnetPath),
                ("TEMP", tempRoot),
                ("TMP", tempRoot),
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", outputRoot),
                ("-ApiBaseUrl", "https://package.example.invalid"),
                ("-SkipNativeInstallers", null));

            Assert.Equal(
                0,
                buildResult.ExitCode);

            var packageRoot = Path.Combine(
                outputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지");
            var appRoot = Path.Combine(packageRoot, "App");
            Assert.Equal(
                File.ReadAllBytes(versionedDesktopFixture.Path),
                File.ReadAllBytes(
                    Path.Combine(appRoot, "거래플랜.exe")));
            Assert.Equal(
                "stale display alias",
                File.ReadAllText(
                    Path.Combine(sourceFolder, "거래플랜.exe")));
            Assert.Contains(
                "*.Desktop.App.exe",
                File.ReadAllText(
                    Path.Combine(appRoot, "앱실행.cmd")),
                StringComparison.Ordinal);
            Assert.Contains(
                "for %%I in (\"%~dp0*.Desktop.App.exe\") do if exist \"%%~fI\"",
                File.ReadAllText(
                    Path.Combine(appRoot, "앱실행.cmd")),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\"%~dp0*.exe\"",
                File.ReadAllText(
                    Path.Combine(appRoot, "앱실행.cmd")),
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                "start \"\" \"%APP_EXE%\"",
                File.ReadAllText(
                    Path.Combine(appRoot, "앱실행.cmd")),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "????",
                File.ReadAllText(
                    Path.Combine(appRoot, "앱실행.cmd")),
                StringComparison.Ordinal);
            Assert.True(
                File.Exists(
                    Path.Combine(
                        appRoot,
                        "Updater",
                        "거래플랜.Updater.exe")));

            using (var appSettings = JsonDocument.Parse(
                       File.ReadAllText(
                           Path.Combine(appRoot, "appsettings.json"))))
            {
                Assert.Equal(
                    "https://package.example.invalid",
                    appSettings.RootElement
                        .GetProperty("Api")
                        .GetProperty("BaseUrl")
                        .GetString());
            }

            Assert.Contains(
                "https://package.example.invalid",
                File.ReadAllText(
                    Path.Combine(packageRoot, "README.txt")),
                StringComparison.Ordinal);
            var generatedInstaller = File.ReadAllText(
                Path.Combine(
                    packageRoot,
                    "Install-GeoraePlan.ps1"));
            Assert.Contains(
                "$workerStartInfo.StandardOutputEncoding =",
                generatedInstaller,
                StringComparison.Ordinal);
            Assert.Contains(
                "$workerStartInfo.StandardErrorEncoding =",
                generatedInstaller,
                StringComparison.Ordinal);
            Assert.Contains(
                "[System.Text.Encoding]::Default",
                generatedInstaller,
                StringComparison.Ordinal);

            var zipPath = Path.Combine(
                outputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지.zip");
            using var archive = ZipFile.OpenRead(zipPath);
            var entryNames = archive.Entries
                .Select(static entry => entry.FullName.Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("App/거래플랜.exe", entryNames);
            Assert.Contains("App/앱실행.cmd", entryNames);
            Assert.Contains(
                "App/Updater/거래플랜.Updater.exe",
                entryNames);
            Assert.Contains("App/appsettings.json", entryNames);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopInstallerScript_RestoresPreviousInstallRootWhenValidationFailsAfterCopy()
    {
        var versionedDesktopFixture = GetVersionedDesktopFixture();
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "install-rollback-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"), "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(testRoot, "Desktop", "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>{versionedDesktopFixture.Version}</Version>
                  </PropertyGroup>
                </Project>
                """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/new\"}}");
            File.WriteAllText(Path.Combine(sourceFolder, "거래플랜.exe"), "new invalid executable without version");
            var updaterDirectory = Path.Combine(sourceFolder, "Updater");
            Directory.CreateDirectory(updaterDirectory);
            File.WriteAllText(
                Path.Combine(updaterDirectory, "거래플랜.Updater.exe"),
                "test updater marker");
            var versionedSourceExecutablePath = Directory.EnumerateFiles(
                    sourceFolder,
                    "*.exe",
                    SearchOption.TopDirectoryOnly)
                .Single();
            File.Copy(
                versionedDesktopFixture.Path,
                versionedSourceExecutablePath,
                overwrite: true);
            File.WriteAllText(Path.Combine(sourceFolder, "new-only.txt"), "new file that must disappear after rollback");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", Path.Combine(testRoot, "output")),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(0, buildResult.ExitCode);

            var packageRoot = Path.Combine(testRoot, "output", "관리자용", "거래플랜-PC-설치패키지");
            var installScriptPath = Path.Combine(packageRoot, "Install-GeoraePlan.ps1");
            Assert.True(File.Exists(installScriptPath), "Generated install script was not found.");
            var packagedDisplayExecutablePath = Directory.EnumerateFiles(
                    Path.Combine(packageRoot, "App"),
                    "*.exe",
                    SearchOption.TopDirectoryOnly)
                .Single(path => !Path.GetFileName(path).Contains(
                    ".Desktop.App.",
                    StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(
                packagedDisplayExecutablePath,
                "new invalid executable without version");
            var installRoot = Path.Combine(testRoot, "install-root");
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(Path.Combine(installRoot, "거래플랜.exe"), "old executable content");
            File.WriteAllText(Path.Combine(installRoot, "appsettings.json"), "{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}");
            File.WriteAllText(Path.Combine(installRoot, "old-only.txt"), "old file that must remain after rollback");

            var installResult = await RunPowerShellAsync(
                installScriptPath,
                ("-InstallRoot", installRoot),
                ("-NoLaunch", null),
                ("-NoShortcuts", null),
                ("-SuppressUi", null),
                ("-LogPath", Path.Combine(testRoot, "install.log")),
                ("DOTNET_EXE", fakeDotnetPath));

            Assert.NotEqual(0, installResult.ExitCode);
            Assert.Contains("설치된 실행 파일 버전을 확인하지 못했습니다:", installResult.StdOut + installResult.StdErr);
            Assert.Equal("old executable content", File.ReadAllText(Path.Combine(installRoot, "거래플랜.exe")));
            Assert.Equal("{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}", File.ReadAllText(Path.Combine(installRoot, "appsettings.json")));
            Assert.True(File.Exists(Path.Combine(installRoot, "old-only.txt")));
            Assert.False(File.Exists(Path.Combine(installRoot, "new-only.txt")));
            Assert.Empty(Directory.EnumerateDirectories(testRoot, ".tradeplan-install-rollback-*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopInstallerScript_PreservesSnapshotWhenRollbackRestoreFails()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");

        Assert.Contains("`$installFailure = `$_.Exception", source, StringComparison.Ordinal);
        Assert.Contains("`$rollbackRestored = `$false", source, StringComparison.Ordinal);
        Assert.Contains(
            "rollback 복구 실패; 복구 snapshot을 보존합니다.",
            source,
            StringComparison.Ordinal);
        var rollbackFailureSection = ExtractPowerShellScriptSection(
            source,
            "    `$installFailure = `$_.Exception",
            "    Show-InstallError (`$installFailure.Message)");
        AssertInOrder(
            rollbackFailureSection,
            "Restore-InstallRollbackSnapshot -Snapshot `$snapshot",
            "`$rollbackRestored = `$true",
            "if (`$rollbackRestored)",
            "Remove-InstallRollbackSnapshot -Snapshot `$snapshot");
        Assert.DoesNotContain(
            """
    foreach (`$snapshot in @(`$installRollbackSnapshots)) {
        Remove-InstallRollbackSnapshot -Snapshot `$snapshot
    }
    Show-InstallError
""",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopInstallerScript_RetainsSupervisorUntilRobocopyRollbackRecovers()
    {
        var versionedDesktopFixture = GetVersionedDesktopFixture();
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "install-rollback-preservation-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(
                Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(
                testRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>{versionedDesktopFixture.Version}</Version>
                  </PropertyGroup>
                </Project>
                """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/new\"}}");
            File.WriteAllText(
                Path.Combine(sourceFolder, "거래플랜.exe"),
                "new invalid executable without version");
            var versionedSourceExecutablePath = Directory.EnumerateFiles(
                    sourceFolder,
                    "*.exe",
                    SearchOption.TopDirectoryOnly)
                .Single();
            File.Copy(
                versionedDesktopFixture.Path,
                versionedSourceExecutablePath,
                overwrite: true);
            var updaterDirectory = Path.Combine(
                sourceFolder,
                "Updater");
            Directory.CreateDirectory(updaterDirectory);
            File.WriteAllText(
                Path.Combine(
                    updaterDirectory,
                    "거래플랜.Updater.exe"),
                "test updater marker");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", Path.Combine(testRoot, "output")),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(0, buildResult.ExitCode);

            var packagedDisplayExecutablePath = Directory.EnumerateFiles(
                    Path.Combine(testRoot, "output"),
                    "*.exe",
                    SearchOption.AllDirectories)
                .Single(path => string.Equals(
                        Path.GetFileName(Path.GetDirectoryName(path)),
                        "App",
                        StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(path).Contains(
                        ".Desktop.App.",
                        StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(
                packagedDisplayExecutablePath,
                "new invalid executable without version");
            var installRoot = Path.Combine(testRoot, "install-root");
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(
                Path.Combine(installRoot, "거래플랜.exe"),
                "old executable content");
            File.WriteAllText(
                Path.Combine(installRoot, "old-only.txt"),
                "recoverable old content");

            var fakeToolsRoot = Path.Combine(testRoot, "fake-tools");
            Directory.CreateDirectory(fakeToolsRoot);
            var robocopyCountPath = Path.Combine(testRoot, "robocopy-count.txt");
            File.WriteAllText(robocopyCountPath, "0");
            File.WriteAllText(
                Path.Combine(fakeToolsRoot, "robocopy.cmd"),
                """
                @echo off
                set /p callCount=<"%FAKE_ROBOCOPY_COUNT%"
                set /a callCount+=1
                >"%FAKE_ROBOCOPY_COUNT%" echo %callCount%
                if %callCount% GEQ 3 if %callCount% LEQ 7 exit /b 8
                "%SystemRoot%\System32\robocopy.exe" %*
                exit /b %errorlevel%
                """);

            var installScriptPath = Path.Combine(
                testRoot,
                "output",
                "관리자용",
                "거래플랜-PC-설치패키지",
                "Install-GeoraePlan.ps1");
            var installResult = await RunPowerShellAsync(
                installScriptPath,
                ("-InstallRoot", installRoot),
                ("-NoLaunch", null),
                ("-NoShortcuts", null),
                ("-SuppressUi", null),
                ("-LogPath", Path.Combine(testRoot, "install.log")),
                ("FAKE_ROBOCOPY_COUNT", robocopyCountPath),
                ("PATH", fakeToolsRoot + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")));

            Assert.NotEqual(0, installResult.ExitCode);
            Assert.Contains(
                "rollback/cleanup-pending",
                installResult.StdOut + installResult.StdErr,
                StringComparison.Ordinal);

            var rollbackResidues = Directory.EnumerateDirectories(
                    testRoot,
                    ".tradeplan-install-rollback-*",
                    SearchOption.TopDirectoryOnly)
                .ToArray();
            Assert.Empty(rollbackResidues);
            Assert.Empty(
                Directory.EnumerateDirectories(
                    testRoot,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            Assert.Equal(
                "recoverable old content",
                File.ReadAllText(Path.Combine(installRoot, "old-only.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopInstallerScript_ValidatesUpdaterContractBeforeDiscardingRollbackSnapshot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var builderPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "install-required-file-contract-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(
                Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");

            var builtDesktopExe = Environment.ProcessPath;
            Assert.True(
                !string.IsNullOrWhiteSpace(builtDesktopExe) &&
                File.Exists(builtDesktopExe),
                $"Built desktop host was not found: {builtDesktopExe}");

            var productVersion = FileVersionInfo
                .GetVersionInfo(builtDesktopExe!)
                .ProductVersion;
            Assert.False(string.IsNullOrWhiteSpace(productVersion));
            var expectedVersion = productVersion!.Split('+')[0];

            var desktopProjectDirectory = Path.Combine(
                testRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                $"""
                 <Project Sdk="Microsoft.NET.Sdk">
                   <PropertyGroup>
                     <Version>{expectedVersion}</Version>
                   </PropertyGroup>
                 </Project>
                 """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.Copy(
                builtDesktopExe!,
                Path.Combine(sourceFolder, "거래플랜.exe"));
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/test\"}}");
            var sourceUpdaterDirectory = Path.Combine(sourceFolder, "Updater");
            Directory.CreateDirectory(sourceUpdaterDirectory);
            File.WriteAllText(
                Path.Combine(sourceUpdaterDirectory, "거래플랜.Updater.exe"),
                "test updater marker");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                builderPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", Path.Combine(testRoot, "output")),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(0, buildResult.ExitCode);

            var packageRoot = Path.Combine(
                testRoot,
                "output",
                "관리자용",
                "거래플랜-PC-설치패키지");
            var packageAppRoot = Path.Combine(packageRoot, "App");
            var installScriptPath = Path.Combine(
                packageRoot,
                "Install-GeoraePlan.ps1");

            var normalInstallRoot = Path.Combine(testRoot, "normal-install");
            var normalInstall = await RunPowerShellAsync(
                installScriptPath,
                ("-InstallRoot", normalInstallRoot),
                ("-NoLaunch", null),
                ("-NoShortcuts", null),
                ("-SuppressUi", null),
                ("-LogPath", Path.Combine(testRoot, "normal-install.log")));
            Assert.Equal(0, normalInstall.ExitCode);
            Assert.True(File.Exists(Path.Combine(normalInstallRoot, "거래플랜.exe")));
            Assert.True(File.Exists(Path.Combine(normalInstallRoot, "appsettings.json")));
            Assert.True(File.Exists(Path.Combine(
                normalInstallRoot,
                "Updater",
                "거래플랜.Updater.exe")));
            Assert.Empty(Directory.EnumerateDirectories(
                testRoot,
                ".tradeplan-install-rollback-*",
                SearchOption.TopDirectoryOnly));

            var requiredPackagePaths = new[]
            {
                Path.Combine(packageAppRoot, "appsettings.json"),
                Path.Combine(
                    packageAppRoot,
                    "Updater",
                    "거래플랜.Updater.exe")
            };
            foreach (var requiredPackagePath in requiredPackagePaths)
            {
                var requiredBytes = File.ReadAllBytes(requiredPackagePath);
                File.Delete(requiredPackagePath);
                try
                {
                    var missingName = Path.GetFileName(requiredPackagePath);
                    var failedInstallRoot = Path.Combine(
                        testRoot,
                        "missing-" + missingName.Replace('.', '-'));
                    Directory.CreateDirectory(failedInstallRoot);
                    File.WriteAllText(
                        Path.Combine(failedInstallRoot, "old-only.txt"),
                        "old installation marker");

                    var failedInstall = await RunPowerShellAsync(
                        installScriptPath,
                        ("-InstallRoot", failedInstallRoot),
                        ("-NoLaunch", null),
                        ("-NoShortcuts", null),
                        ("-SuppressUi", null),
                        ("-LogPath", Path.Combine(
                            testRoot,
                            "missing-" + missingName + ".log")));

                    Assert.NotEqual(0, failedInstall.ExitCode);
                    Assert.Contains(
                        "설치 후 필수 파일이 누락되었습니다:",
                        failedInstall.StdOut + failedInstall.StdErr,
                        StringComparison.Ordinal);
                    Assert.Contains(
                        missingName,
                        failedInstall.StdOut + failedInstall.StdErr,
                        StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(
                        "old installation marker",
                        File.ReadAllText(Path.Combine(
                            failedInstallRoot,
                            "old-only.txt")));
                    Assert.Empty(Directory.EnumerateDirectories(
                        testRoot,
                        ".tradeplan-install-rollback-*",
                        SearchOption.TopDirectoryOnly));
                }
                finally
                {
                    File.WriteAllBytes(requiredPackagePath, requiredBytes);
                }
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopInstallerAndUpdater_RequireTheSamePostInstallFilesBeforeSuccess()
    {
        var installerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var updaterSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("appsettings.json", installerSource, StringComparison.Ordinal);
        Assert.Contains("appsettings.json", updaterSource, StringComparison.Ordinal);
        Assert.Contains(
            "Updater\\__APP_DISPLAY_NAME__.Updater.exe",
            installerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Updater\\거래플랜.Updater.exe",
            installerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "'거래플랜.Updater.exe'",
            installerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "$canonicalUpdaterHash",
            installerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Updater\", \"거래플랜.Updater.exe",
            updaterSource,
            StringComparison.Ordinal);

        AssertInOrder(
            installerSource,
            "`$validationRoots = @(`$InstallRoot)",
            "`$requiredInstalledFiles = @(",
            "`$installedVersion =",
            "(Get-Item -LiteralPath `$validationExecutable",
            "Remove-InstallRollbackSnapshot -Snapshot `$snapshot");
    }

    [Fact]
    public async Task DesktopInstallerScript_CommitsCanonicalAndPreservesLegacyOnPostCommitShortcutFailure()
    {
        var repositoryRoot = FindRepositoryRoot();
        var builderPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "install-legacy-shortcut-rollback-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(
                Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");

            var versionedHost = Environment.ProcessPath;
            Assert.True(
                !string.IsNullOrWhiteSpace(versionedHost) &&
                File.Exists(versionedHost));
            var productVersion = FileVersionInfo
                .GetVersionInfo(versionedHost!)
                .ProductVersion;
            Assert.False(string.IsNullOrWhiteSpace(productVersion));
            var expectedVersion = productVersion!.Split('+')[0];

            var desktopProjectDirectory = Path.Combine(
                testRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                $"""
                 <Project Sdk="Microsoft.NET.Sdk">
                   <PropertyGroup>
                     <Version>{expectedVersion}</Version>
                   </PropertyGroup>
                 </Project>
                 """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.Copy(
                versionedHost!,
                Path.Combine(sourceFolder, "거래플랜.exe"));
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/test\"}}");
            File.WriteAllText(
                Path.Combine(sourceFolder, "new-only.txt"),
                "new installation marker");
            var sourceUpdaterDirectory = Path.Combine(sourceFolder, "Updater");
            Directory.CreateDirectory(sourceUpdaterDirectory);
            File.WriteAllText(
                Path.Combine(sourceUpdaterDirectory, "거래플랜.Updater.exe"),
                "test updater marker");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                builderPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", Path.Combine(testRoot, "output")),
                ("-EnableTestHooks", null),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(0, buildResult.ExitCode);

            var canonicalRoot = Path.Combine(testRoot, "canonical-install");
            Directory.CreateDirectory(canonicalRoot);
            File.WriteAllText(
                Path.Combine(canonicalRoot, "canonical-old-only.txt"),
                "canonical original marker");

            var isolatedLocalAppData = Path.Combine(
                testRoot,
                "isolated-local-app-data");
            var legacyRoot = Path.Combine(
                isolatedLocalAppData,
                "Programs",
                "거래플랜");
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(
                Path.Combine(legacyRoot, "legacy-old-only.txt"),
                "legacy original marker");

            var installScriptPath = Path.Combine(
                testRoot,
                "output",
                "관리자용",
                "거래플랜-PC-설치패키지",
                "Install-GeoraePlan.ps1");
            var testCapability = File.ReadAllText(
                Path.Combine(
                    Path.GetDirectoryName(installScriptPath)
                    ?? throw new InvalidOperationException(
                        "Generated package root was not resolved."),
                    ".georaeplan-installer-test-capability"));
            var installResult = await RunPowerShellAsync(
                installScriptPath,
                ("-InstallRoot", canonicalRoot),
                ("-NoLaunch", null),
                ("-SuppressUi", null),
                ("-LogPath", Path.Combine(testRoot, "shortcut-failure.log")),
                ("LOCALAPPDATA", isolatedLocalAppData),
                ("GEORAEPLAN_INSTALLER_TEST_FAIL_SHORTCUTS", "1"),
                ("GEORAEPLAN_INSTALLER_TEST_CAPABILITY", testCapability));

            Assert.NotEqual(0, installResult.ExitCode);
            Assert.Contains(
                "Injected post-commit shortcut failure",
                installResult.StdOut + installResult.StdErr,
                StringComparison.Ordinal);
            Assert.Contains(
                "shortcut repair pending state를 유지하고 설치를 실패로 종료합니다",
                installResult.StdOut + installResult.StdErr,
                StringComparison.Ordinal);

            Assert.False(File.Exists(Path.Combine(
                canonicalRoot,
                "canonical-old-only.txt")));
            Assert.Equal(
                "new installation marker",
                File.ReadAllText(Path.Combine(
                    canonicalRoot,
                    "new-only.txt")));
            Assert.Equal(
                "legacy original marker",
                File.ReadAllText(Path.Combine(
                    legacyRoot,
                    "legacy-old-only.txt")));
            Assert.False(File.Exists(Path.Combine(
                legacyRoot,
                "new-only.txt")));
            Assert.Empty(Directory.EnumerateDirectories(
                testRoot,
                ".tradeplan-install-rollback-*",
                SearchOption.TopDirectoryOnly));
            Assert.Single(Directory.EnumerateDirectories(
                testRoot,
                ".tradeplan-update-supervisor-state-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopInstallerScript_DurablyRepairsCommonSetBeforeLegacyShortcutCleanup()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var shortcutRepair = ExtractPowerShellScriptSection(
            source,
            "function Invoke-PendingShortcutRepair {",
            "function New-ShortcutRepairPendingException {");
        var supervisor = ExtractPowerShellScriptSection(
            source,
            "function Invoke-WorkerUnderRollbackSupervisor {",
            "`$ErrorActionPreference = 'Stop'");

        AssertInOrder(
            shortcutRepair,
            "Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryDesktopShortcutPath",
            "Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryApplicationShortcutPath",
            "Set-AtomicShellShortcut -Shell `$shell -ShortcutPath `$Repair.PrimaryRemoveShortcutPath",
            "GEORAEPLAN_INSTALLER_TEST_FAIL_SHORTCUTS_AFTER_COMMON",
            "Remove-LegacyManagedShortcut -Shell `$shell -ShortcutPath `$Repair.AlternateDesktopShortcutPath",
            "Remove-LegacyManagedShortcut -Shell `$shell -ShortcutPath `$Repair.AlternateRemoveShortcutPath");
        AssertInOrder(
            supervisor,
            "if (`$worker.ExitCode -ne 0)",
            "`$journal.Phase = 'ShortcutRepairPending'",
            "Assert-ShortcutRepairJournalBinding -Journal `$journal",
            "Write-SupervisorJournal -Journal `$journal -JournalPath `$journalPath",
            "Invoke-PendingShortcutRepair -Repair `$journal.ShortcutRepair",
            "`$journal.Phase = 'CommittedCleanupPending'",
            "Remove-CompletedSupervisorState -Journal `$journal -JournalPath `$journalPath");
    }

    [Fact]
    public void DesktopUninstallers_PreserveLocalDatabaseAttachmentsAndBackups()
    {
        var zipInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var uninstallSection = ExtractPowerShellScriptSection(
            zipInstallerSource,
            "$uninstallScriptBody = @\"",
            "\"@");

        Assert.Contains(
            "Remove-Item -LiteralPath `$InstallRoot -Recurse -Force -ErrorAction Stop",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "Local database, attachments, and backups were preserved.",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "제거 스크립트가 설치된 폴더와 InstallRoot가 일치하지 않습니다.",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "로컬 데이터 폴더와 겹치는 경로는 제거할 수 없습니다.",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "설치된 거래플랜 실행 파일을 확인하지 못해 제거를 중단합니다.",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Resolve-PhysicalDirectoryPath",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.FileAttributes]::ReparsePoint",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "[GeoraePlanUninstallPathNative]::GetFinalPathNameByHandle",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.Path]::GetPathRoot(`$InstallRoot)",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "볼륨 또는 주요 시스템 폴더는 제거할 수 없습니다:",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "`$item.FullName",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.Contains(
            "StartsWith('\\\\?\\'",
            uninstallSection,
            StringComparison.Ordinal);
        AssertInOrder(
            uninstallSection,
            "function Resolve-PhysicalDirectoryPath",
            "`$scriptRoot = Resolve-PhysicalDirectoryPath",
            "`$localDataRootCandidate = Join-Path",
            "`$installedExecutable = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'",
            "Set-Location -LiteralPath `$installParent",
            "Remove-Item -LiteralPath `$InstallRoot -Recurse -Force -ErrorAction Stop");
        Assert.DoesNotContain(
            "Join-Path `$env:LOCALAPPDATA '__APP_DISPLAY_NAME__'",
            uninstallSection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Remove-Item -LiteralPath `$localAppDataRoot",
            uninstallSection,
            StringComparison.Ordinal);

        var nativeInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopNativeInstallers.ps1");

        Assert.DoesNotContain(
            "GEORAEPLANLOCALAPPDATAROOT",
            nativeInstallerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RemoveFolderEx",
            nativeInstallerSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "-InstallRoot ``\"`$(`$Repair.InstallRoot)``\"",
            zipInstallerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedDesktopUninstaller_RemovesOnlyPhysicalCustomInstallRootOnDDrive()
    {
        var versionedDesktopFixture = GetVersionedDesktopFixture();
        var repositoryRoot = FindRepositoryRoot();
        var builderPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "generated-uninstall-physical-path-tests",
            Guid.NewGuid().ToString("N"));
        var appDisplayName = "TradePlanUninstallTest" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(
                Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(
                testRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>{versionedDesktopFixture.Version}</Version>
                  </PropertyGroup>
                </Project>
                """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, appDisplayName + ".exe"),
                "generated uninstaller test executable marker");
            File.Copy(
                versionedDesktopFixture.Path,
                Path.Combine(sourceFolder, appDisplayName + ".exe"),
                overwrite: true);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/test\"}}");
            var updaterDirectory = Path.Combine(sourceFolder, "Updater");
            Directory.CreateDirectory(updaterDirectory);
            File.WriteAllText(
                Path.Combine(updaterDirectory, appDisplayName + ".Updater.exe"),
                "generated uninstaller test updater marker");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var outputRoot = Path.Combine(testRoot, "output");
            var buildResult = await RunPowerShellAsync(
                builderPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", outputRoot),
                ("-AppDisplayName", appDisplayName),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(
                0,
                buildResult.ExitCode);

            var packageRoot = Path.Combine(
                outputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지");
            Assert.True(File.Exists(Path.Combine(
                packageRoot,
                "App",
                "Updater",
                "거래플랜.Updater.exe")));
            Assert.True(File.Exists(Path.Combine(
                packageRoot,
                "App",
                "Updater",
                appDisplayName + ".Updater.exe")));
            Assert.Equal(
                "generated uninstaller test updater marker",
                File.ReadAllText(Path.Combine(
                    packageRoot,
                    "App",
                    "Updater",
                    "거래플랜.Updater.exe")));
            Assert.Equal(
                "generated uninstaller test updater marker",
                File.ReadAllText(Path.Combine(
                    packageRoot,
                    "App",
                    "Updater",
                    appDisplayName + ".Updater.exe")));
            var launchCommandPath = Path.Combine(
                packageRoot,
                "App",
                "앱실행.cmd");
            var launchCommandSource = File.ReadAllText(
                launchCommandPath,
                Encoding.ASCII);
            Assert.Contains(
                """
                for %%I in ("%~dp0*.Desktop.App.exe") do if exist "%%~fI" if not defined APP_EXE set "APP_EXE=%%~fI"
                """,
                launchCommandSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\"%~dp0*.exe\"",
                launchCommandSource,
                StringComparison.OrdinalIgnoreCase);
            var launchProbePath = Path.Combine(
                packageRoot,
                "App",
                "앱실행-probe.cmd");
            var launchProbeMarker = Path.Combine(
                packageRoot,
                "App",
                "launch-probe-ok.txt");
            var launchProbeSource = launchCommandSource
                .Replace(
                    "start \"\" \"%APP_EXE%\"",
                    "if exist \"%APP_EXE%\" >\"%~dp0launch-probe-ok.txt\" echo ok",
                    StringComparison.Ordinal);
            Assert.NotEqual(
                launchCommandSource,
                launchProbeSource);
            File.WriteAllText(
                launchProbePath,
                launchProbeSource,
                Encoding.ASCII);
            using (var launchProbe = Process.Start(
                       new ProcessStartInfo
                       {
                           FileName =
                               Environment.GetEnvironmentVariable("ComSpec")
                               ?? "cmd.exe",
                           Arguments =
                               $"/d /c \"\"{launchProbePath}\"\"",
                           UseShellExecute = false,
                           CreateNoWindow = true
                       }))
            {
                Assert.NotNull(launchProbe);
                await launchProbe!.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, launchProbe.ExitCode);
            }
            Assert.True(
                File.Exists(launchProbeMarker),
                "The generated launch command did not resolve its fallback executable.");
            var packageZipPath = Path.Combine(
                outputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지.zip");
            using (var packageArchive = ZipFile.OpenRead(packageZipPath))
            {
                var packageEntryNames = packageArchive.Entries
                    .Select(static entry => entry.FullName.Replace('\\', '/'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.Contains(
                    "App/Updater/거래플랜.Updater.exe",
                    packageEntryNames);
                Assert.Contains(
                    $"App/Updater/{appDisplayName}.Updater.exe",
                    packageEntryNames);
            }
            var generatedInstallScript = File.ReadAllText(Path.Combine(
                packageRoot,
                "Install-GeoraePlan.ps1"));
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
            var uninstallScriptBody = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    generatedInstallScript[base64Start..base64End]));

            var preservedDataRoot = Path.Combine(testRoot, "preserved-local-data");
            Directory.CreateDirectory(preservedDataRoot);
            var preservedSentinel = Path.Combine(
                preservedDataRoot,
                "database-attachments-backups.sentinel");
            File.WriteAllText(preservedSentinel, "must remain");

            var installRoot = Path.Combine(testRoot, "custom-install-root");
            Directory.CreateDirectory(installRoot);
            var uninstallScriptPath = Path.Combine(
                installRoot,
                "Uninstall-GeoraePlan.ps1");
            File.WriteAllText(
                uninstallScriptPath,
                uninstallScriptBody,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(
                Path.Combine(installRoot, appDisplayName + ".exe"),
                "installed application marker");

            var extendedInstallRoot = @"\\?\" + installRoot;
            var uninstallResult = await RunPowerShellAsync(
                uninstallScriptPath,
                ("-InstallRoot", extendedInstallRoot),
                ("-NoShortcutCleanup", null));

            Assert.Equal(0, uninstallResult.ExitCode);
            Assert.False(Directory.Exists(installRoot));
            Assert.Equal("must remain", File.ReadAllText(preservedSentinel));
            Assert.Contains(
                "Local database, attachments, and backups were preserved.",
                uninstallResult.StdOut + uninstallResult.StdErr,
                StringComparison.Ordinal);

            var junctionTarget = Path.Combine(testRoot, "junction-target");
            Directory.CreateDirectory(junctionTarget);
            File.WriteAllText(
                Path.Combine(junctionTarget, "Uninstall-GeoraePlan.ps1"),
                uninstallScriptBody,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllText(
                Path.Combine(junctionTarget, appDisplayName + ".exe"),
                "junction application marker");
            var junctionPath = Path.Combine(testRoot, "junction-install-root");
            var junctionMakerPath = Path.Combine(testRoot, "create-junction.ps1");
            File.WriteAllText(
                junctionMakerPath,
                """
                param([string]$Link, [string]$Target)
                New-Item -ItemType Junction -Path $Link -Target $Target -ErrorAction Stop | Out-Null
                """);
            var junctionResult = await RunPowerShellAsync(
                junctionMakerPath,
                ("-Link", junctionPath),
                ("-Target", junctionTarget));
            Assert.Equal(0, junctionResult.ExitCode);

            var rejectedResult = await RunPowerShellAsync(
                Path.Combine(junctionPath, "Uninstall-GeoraePlan.ps1"),
                ("-InstallRoot", junctionPath));
            Assert.NotEqual(0, rejectedResult.ExitCode);
            Assert.Contains(
                "재분석 지점을 통과하는 설치 경로는 제거할 수 없습니다:",
                rejectedResult.StdOut + rejectedResult.StdErr,
                StringComparison.Ordinal);
            Assert.True(Directory.Exists(junctionTarget));
            Assert.True(File.Exists(Path.Combine(
                junctionTarget,
                appDisplayName + ".exe")));
            Assert.Equal("must remain", File.ReadAllText(preservedSentinel));
        }
        finally
        {
            var junctionPath = Path.Combine(testRoot, "junction-install-root");
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath, recursive: false);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void AndroidBuildScript_DisableAotOverridesProjectAotDefaults()
    {
        var source = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("$DisableAot.IsPresent", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:RunAOTCompilation=false'", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:AndroidEnableProfiledAot=false'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$shouldEnableAot = $isReleaseBuild -and -not $DisableAot.IsPresent",
            "$arguments += '-p:RunAOTCompilation=true'",
            "elseif ($DisableAot.IsPresent)",
            "$arguments += '-p:RunAOTCompilation=false'",
            "$shouldDisableTrimming = $DisableTrimming.IsPresent");
    }

    [Fact]
    public void OperationalGate_ValidatesUpdatePackageHeadAndGetHeadersWithoutDownloadingPackages()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("function Invoke-UpdatePackageHeaderProbe", source, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead", source, StringComparison.Ordinal);
        Assert.Contains("function Test-UpdatePackageDownloadHeaders", source, StringComparison.Ordinal);
        Assert.Contains("HEAD Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("GET Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("manifest fileSize", source, StringComparison.Ordinal);
        Assert.Contains("update-downloads.md", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentLength.HasValue", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$manifest = Invoke-TextProbe",
            "$updateDownloadReportPath = Join-Path $OutputDirectory 'update-downloads.md'",
            "Add-Check -Checks $checks -Name 'update package downloads'",
            "$liveObservationScript = Join-Path $resolvedRoot");
    }

    [Fact]
    public void OperationalGate_ChecksReadinessBeforeManifestAndDatabaseDependentChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("Add-Check -Checks $checks -Name 'live healthz'", source, StringComparison.Ordinal);
        Assert.Contains("Add-Check -Checks $checks -Name 'live readyz'", source, StringComparison.Ordinal);
        Assert.Contains("readyz status={0} error={1} body={2}", source, StringComparison.Ordinal);
        Assert.Contains("function Test-ReadyProbeSemantic", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-ReadyProbeWithRetry", source, StringComparison.Ordinal);
        Assert.Contains("readyz attempt={0} semantic={1}", source, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds $DelaySec", source, StringComparison.Ordinal);
        Assert.Contains("$status -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("$dbStarted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbCompleted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbFailed -eq $false", source, StringComparison.Ordinal);
        Assert.Contains("200 OK but readiness body is not ready", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$health = Invoke-TextProbe -Uri ($BaseUrl + '/healthz')",
            "Invoke-ReadyProbeWithRetry `",
            "$readySemanticResult = $readyProbeResult.SemanticResult",
            "$manifest = Invoke-TextProbe -Uri ($BaseUrl + \"/updates/manifest?channel=$Channel\")");
    }

    [Fact]
    public void DeployAfterTest_ForwardsIntegrityWarningCodesAsOneNormalizedArgument()
    {
        var source = ReadRepositoryFile("테스트 시행", "Deploy-After-Test.ps1");

        AssertInOrder(
            source,
            "if ($AllowedIntegrityWarningCodes.Count -gt 0)",
            "$arguments += @(",
            "'-AllowedIntegrityWarningCodes',",
            "($AllowedIntegrityWarningCodes -join ','))");
        Assert.DoesNotContain(
            "$arguments += $AllowedIntegrityWarningCodes",
            source,
            StringComparison.Ordinal);

        AssertInOrder(
            source,
            "if ($PreDeployAllowedIntegrityWarningCodes.Count -gt 0)",
            "$linuxArgs += @(",
            "'-PreDeployAllowedIntegrityWarningCodes',",
            "($PreDeployAllowedIntegrityWarningCodes -join ','))");
        AssertInOrder(
            source,
            "if ($PostDeployAllowedIntegrityWarningCodes.Count -gt 0)",
            "$linuxArgs += @(",
            "'-PostDeployAllowedIntegrityWarningCodes',",
            "($PostDeployAllowedIntegrityWarningCodes -join ','))");
        Assert.DoesNotContain(
            "$linuxArgs += $PreDeployAllowedIntegrityWarningCodes",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$linuxArgs += $PostDeployAllowedIntegrityWarningCodes",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OperationalGate_RequiresCurrentTenantIntegrityAccountsButTreatsLegacyAdminAsOptional()
    {
        var source = ReadRepositoryFile("tools", "ops", "Invoke-GeoraePlanOperationalGate.ps1");
        var function = ExtractPowerShellScriptSection(
            source,
            "function Test-RequiredIntegrityAccount",
            "$resolvedRoot = Resolve-ProjectRoot");

        Assert.Contains("'ITWORLD'", function, StringComparison.Ordinal);
        Assert.Contains("'USENET'", function, StringComparison.Ordinal);
        Assert.DoesNotContain("'ADMIN'", function, StringComparison.Ordinal);
        Assert.Contains(
            "$status = if (Test-RequiredIntegrityAccount -Alias $alias) { 'FAIL' } else { 'SKIP' }",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RentalTemplateCandidateExportScript_IsSelectOnlyAndRedactsSensitiveRowsByDefault()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1");

        Assert.Contains("[switch]$IncludeSensitiveCandidateRows", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(") to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifacts\\rental-template-item-reference-candidates", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"ProfileKey\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CustomerName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"DisplayItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"OriginalItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("coalesce(elem->>'CatalogItemId', elem->>'catalogItemId')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(elem->>'ItemId', elem->>'itemId')", source, StringComparison.Ordinal);
        Assert.Contains("single_active_item_from_included_assets", source, StringComparison.Ordinal);
        Assert.Contains("ambiguous_multiple_candidates", source, StringComparison.Ordinal);
        Assert.Contains("proposed_item_id as \"ProposedItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_source as \"ProposedSource\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_confidence as \"ProposedConfidence\"", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemCount", source, StringComparison.Ordinal);
        Assert.Contains("review_required_asset_based", source, StringComparison.Ordinal);
        Assert.Contains("At least one database name is required.", source, StringComparison.Ordinal);
        Assert.Contains("([string]$_) -split ','", source, StringComparison.Ordinal);
        Assert.Contains("function Get-ManualReviewDetailSql", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("name_or_identifier_candidate", source, StringComparison.Ordinal);
        Assert.Contains("included_asset_item_candidate", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CandidateItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("CandidateStatus", source, StringComparison.Ordinal);
        Assert.Contains("DistinctCandidateItemCount", source, StringComparison.Ordinal);

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateItemReferenceGate_BlocksUnresolvedCandidatesWithReadOnlyExport()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateItemReferenceGate.ps1");

        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("summary-by-database.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_item_reference_gate_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Unresolved rental billing template item references remain", source, StringComparison.Ordinal);
        Assert.Contains("'-Databases', ($Databases -join ',')", source, StringComparison.Ordinal);
        Assert.Contains("AllowUnresolved", source, StringComparison.Ordinal);

        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxPcReleaseRunsRentalTemplateItemReferenceGateWithOperationalGate()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("Test-GeoraePlanRentalTemplateItemReferenceGate.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-RentalTemplateItemReferenceGate", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_start", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_done", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data_rental_template_item_reference_gate=skipped risk=accepted", source, StringComparison.Ordinal);
        Assert.Contains("known operating data candidates are intentionally excluded", source, StringComparison.Ordinal);
        Assert.Contains("'-RemoteOpsDirectory', $script:LinuxRemoteOpsPath", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$rentalTemplateItemReferenceGateScript = Join-Path $Root 'tools\\linux\\Test-GeoraePlanRentalTemplateItemReferenceGate.ps1'",
            "& powershell @rentalTemplateItemReferenceGateArgs");
        AssertInOrder(
            source,
            "function Invoke-RentalTemplateItemReferenceGate",
            "function Update-PublishedAppSettings");
        AssertInOrder(
            source,
            "$resolvedPreDeploySecretPath =",
            "if ($MirrorToLive -and -not $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "Invoke-RentalTemplateItemReferenceGate `",
            "elseif ($MirrorToLive -and $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent)");
    }

    [Fact]
    public void LinuxPcReleaseSshCommandPreservesRemoteCommandQuotingAndFailsPrunePipelines()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Invoke-SshCommand", source, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.ProcessStartInfo]::new($sshExe)", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.RedirectStandardOutput = $true", source, StringComparison.Ordinal);
        Assert.Contains("$process.Start()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $sshExe -ArgumentList $arguments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$startInfo.ArgumentList.Add($argument)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "function Invoke-SshCommand",
            "[System.Diagnostics.ProcessStartInfo]::new($sshExe)",
            "$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '",
            "$process.Start()");

        Assert.Contains("set -o pipefail", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "set -e",
            "set -o pipefail",
            "find \"`$real_root\" -mindepth 1 -maxdepth 1 -type d -name \"`$pattern\"");
    }

    [Fact]
    public void LinuxPcReleaseChecksDiskFreeSpaceAfterPruneBeforeUpload()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("[int64]$MinimumLinuxFreeBytes", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-LinuxPcDiskPreflight", source, StringComparison.Ordinal);
        Assert.Contains("$minimumFreeKilobytes = [int64][Math]::Ceiling($MinimumFreeBytes / 1024.0)", source, StringComparison.Ordinal);
        Assert.Contains("df -Pk \"`$path\"", source, StringComparison.Ordinal);
        Assert.Contains("minimum_kb=$minimumFreeKilobytes", source, StringComparison.Ordinal);
        Assert.Contains("if [ \"`$available_kb\" -lt \"`$minimum_kb\" ]; then", source, StringComparison.Ordinal);
        Assert.Contains("linux_pc_disk_preflight_ok", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC free disk space is below the required threshold", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups'",
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases'",
            "Invoke-LinuxPcDiskPreflight -Config $linuxConfig -Path $linuxConfig.RemoteRoot -MinimumFreeBytes $MinimumLinuxFreeBytes -Label 'pre-upload'",
            "Write-Host \"linux_pc_upload_start");
    }

    [Fact]
    public void PreLiveVerificationUsesLinuxPcUpdateManifestStepLabels()
    {
        var source = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanPreLiveVerification.ps1");

        Assert.Contains("function Invoke-LinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("SkipLinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-Step -Name 'linux-pc-update-manifest-check'", source, StringComparison.Ordinal);
        Assert.Contains("Add-StepResult -Name 'linux-pc-update-manifest-check' -Passed $true -Detail 'SKIP'", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC update manifest 확인", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nas-update-manifest-check", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveObservationChecksBothDesktopAndAndroidPackagesFromManifest()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Invoke-LiveObservationCheck.ps1");

        Assert.Contains("$desktopPackageUrl = [string]$manifest.desktop.packageUrl", source, StringComparison.Ordinal);
        Assert.Contains("$androidPackageUrl = [string]$manifest.android.packageUrl", source, StringComparison.Ordinal);
        Assert.Contains("$desktopPackageResult = if ($SkipPackageProbe)", source, StringComparison.Ordinal);
        Assert.Contains("$androidPackageResult = if ($SkipPackageProbe)", source, StringComparison.Ordinal);
        Assert.Contains("Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $desktopPackageUrl", source, StringComparison.Ordinal);
        Assert.Contains("Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $androidPackageUrl", source, StringComparison.Ordinal);
        Assert.Contains("DesktopPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("AndroidPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("-not $_.DesktopPackageOk -or -not $_.AndroidPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("desktop package | android package", source, StringComparison.Ordinal);
        Assert.Contains("desktop/android package 다운로드 경로", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageOk = $packageResult.Success", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-not $_.PackageOk", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveObservationReportsAndroidApkSigningCertificateAndDebugRisk()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Invoke-LiveObservationCheck.ps1");

        Assert.Contains("[switch]$SkipAndroidSigningProbe", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("function Resolve-ApkSignerPath", source, StringComparison.Ordinal);
        Assert.Contains("function Resolve-JavaHomeForApkSigner", source, StringComparison.Ordinal);
        Assert.Contains("function Test-AndroidApkSigningProbe", source, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $javaHome", source, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $previousJavaHome", source, StringComparison.Ordinal);
        Assert.Contains("verify --print-certs", source, StringComparison.Ordinal);
        Assert.Contains("Signer\\s+#1\\s+certificate\\s+DN", source, StringComparison.Ordinal);
        Assert.Contains("Signer\\s+#1\\s+certificate\\s+SHA-256\\s+digest", source, StringComparison.Ordinal);
        Assert.Contains("CN=Android Debug", source, StringComparison.Ordinal);
        Assert.Contains("Android APK signing 점검", source, StringComparison.Ordinal);
        Assert.Contains("Android APK가 debug signing 인증서로 서명되어 있습니다", source, StringComparison.Ordinal);
        Assert.Contains("$androidSigningFailure = $FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("elseif ($warningMessages.Count -gt 0)", source, StringComparison.Ordinal);
        Assert.Contains("if ($overallStatus -eq \"PASS\")", source, StringComparison.Ordinal);
        Assert.Contains("elseif ($overallStatus -eq \"WARN\")", source, StringComparison.Ordinal);
        Assert.Contains("if ($overallStatus -eq \"FAIL\")", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$androidSigningResult = if ($SkipPackageProbe -or $SkipManifestProbe)",
            "Test-AndroidApkSigningProbe -ProjectRoot $ProjectRoot",
            "$androidSigningFailure = $FailOnAndroidDebugSigning",
            "$overallStatus = if ($failedSamples.Count -gt 0",
            "$lines.Add(\"- Android APK signing 점검: $androidSigningSummary\")");
    }

    [Fact]
    public void OperationalAndPreLiveGatesDoNotSwallowLiveObservationWarnAsPass()
    {
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");
        var preLive = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanPreLiveVerification.ps1");

        Assert.Contains("function Resolve-MarkdownResultStatus", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationStatus = Resolve-MarkdownResultStatus -ReportPath $liveObservationReport", operationalGate, StringComparison.Ordinal);
        Assert.Contains("Add-Check -Checks $checks -Name 'live observation' -Status $liveObservationStatus", operationalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-Check -Checks $checks -Name 'live observation' -Status 'PASS' -Detail $liveObservationReport", operationalGate, StringComparison.Ordinal);

        Assert.Contains("$status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus 'PASS'", preLive, StringComparison.Ordinal);
        Assert.Contains("Detail = $status", preLive, StringComparison.Ordinal);
        Assert.Contains("$warnings = @($Results | Where-Object { $_.Passed -and [string]::Equals([string]$_.Detail, 'WARN'", preLive, StringComparison.Ordinal);
        Assert.Contains("$overall = if ($failed.Count -gt 0) { 'FAIL' } elseif ($warnings.Count -gt 0) { 'WARN' } else { 'PASS' }", preLive, StringComparison.Ordinal);
        Assert.Contains("elseif ([string]::Equals([string]$row.Detail, 'WARN'", preLive, StringComparison.Ordinal);
        Assert.Contains("## 경고 항목", preLive, StringComparison.Ordinal);
        AssertInOrder(
            preLive,
            "$status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus 'PASS'",
            "Detail = $status",
            "$warnings = @($Results | Where-Object",
            "$overall = if ($failed.Count -gt 0)",
            "## 경고 항목");
    }

    [Fact]
    public void ReleaseWrappersCanFailDeploymentOnOperationalWarnings()
    {
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");
        var linuxRelease = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var fullRelease = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");
        var deployAfterTest = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");
        var verificationDeploy = ReadRepositoryFile(
            "테스트 시행",
            "검증완료-반영.ps1");

        Assert.Contains("[switch]$FailOnOperationalWarnings", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$warningChecks = @($checks | Where-Object { $_.Status -eq 'WARN' })", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$overallStatus = if ($FailOnOperationalWarnings) { 'FAIL' } else { 'WARN' }", operationalGate, StringComparison.Ordinal);
        Assert.Contains("운영 Warning 실패 처리", operationalGate, StringComparison.Ordinal);

        Assert.Contains("[switch]$FailOnOperationalWarnings", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("[bool]$FailOnOperationalWarnings = $false", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$gateArgs += '-FailOnOperationalWarnings'", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("-FailOnOperationalWarnings ([bool]$FailOnOperationalWarnings)", linuxRelease, StringComparison.Ordinal);

        Assert.Contains("[switch]$FailOnOperationalWarnings", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnOperationalWarnings'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnOperationalWarnings", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnOperationalWarnings'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnOperationalWarnings", verificationDeploy, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployAfterTest_DelegatesStableAssetPublishingToTheDurableLinuxPublisherOnly()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");

        Assert.Contains(
            "$updateAssetsScript = Join-Path $ProjectRoot 'tools\\release\\Publish-GeoraePlanUpdateAssets.ps1'",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Invoke-PowerShellFile -FilePath $updateAssetsScript",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Invoke-PowerShellFile -FilePath $buildInstallerScript",
            "$linuxArgs = @('-ProjectRoot', $ProjectRoot, '-MirrorToLive')",
            "Invoke-PowerShellFile -FilePath $linuxPublishScript -Arguments $linuxArgs");
    }

    [Fact]
    public void LiveReadiness_PostModeValidatesTheExactPublicManifestInsteadOfStaleLocalAssets()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Invoke-LiveReleaseReadinessCheck.ps1");

        Assert.Contains(
            "[string]$BaseUrl = 'https://trade.2884.kr'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "function Resolve-ExactLiveBaseUrl",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-MaximumRedirection 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$baseResponse.RequestMessage.RequestUri",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Live readiness request URI could not be determined.",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$contentLengthHeader = @(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$downloadLengthParsed = [long]::TryParse(",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "else {",
            "$liveManifestUri =",
            "Invoke-ExactLiveRequest -Uri $liveManifestUri -Method Get",
            "'manifest desktop 버전 일치'",
            "Invoke-ExactLiveRequest `",
            "-Method Head");
        var postMode = source[source.IndexOf("else {", source.IndexOf("if ($Mode -eq 'Pre')", StringComparison.Ordinal), StringComparison.Ordinal)..];
        Assert.DoesNotContain(
            "Get-Content -LiteralPath $manifestPath",
            postMode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxPcRelease_PassesExpectedClientCompatibilityToBothGatesAndLegacyAllowanceOnlyToPreGate()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        Assert.Contains(
            "[string]$ExpectedClientCompatibilityMode = 'AuditOnly'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[int]$ExpectedClientCompatibilityEnabledPolicyCount = 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$AllowLegacyPreDeployCompatibilitySummary",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ExpectedClientCompatibilityMode -cnotin @('AuditOnly', 'StrictBlock')",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ExpectedClientCompatibilityEnabledPolicyCount -gt 2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'-ExpectedClientCompatibilityMode'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'-ExpectedClientCompatibilityEnabledPolicyCount'",
            source,
            StringComparison.Ordinal);

        var preGate = ExtractPowerShellScriptSection(
            source,
            "if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent)",
            "elseif ($MirrorToLive -and $SkipPreDeployOperationalGate.IsPresent)");
        Assert.Contains(
            "-ExpectedClientCompatibilityMode $ExpectedClientCompatibilityMode",
            preGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedClientCompatibilityEnabledPolicyCount `",
            preGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-AllowMissingClientCompatibilitySummary `",
            preGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "([bool]$AllowLegacyPreDeployCompatibilitySummary)",
            preGate,
            StringComparison.Ordinal);

        var postGate = ExtractPowerShellScriptSection(
            source,
            "if (-not $SkipPostDeployOperationalGate.IsPresent)",
            "else {");
        Assert.Contains(
            "-ExpectedClientCompatibilityMode `",
            postGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedClientCompatibilityEnabledPolicyCount `",
            postGate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AllowMissingClientCompatibilitySummary",
            postGate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AllowLegacyPreDeployCompatibilitySummary",
            postGate,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocumentationRecommendsOperationalWarningFailForPaidDelivery()
    {
        var readme = ReadRepositoryFile("README.md");
        var linuxRunbook = ReadRepositoryFile("infra", "LinuxPC-운영-런북.md");
        var updateGuide = ReadRepositoryFile("수정_업데이트_가이드_2026-03-20.md");
        var testReadme = ReadRepositoryFile("테스트 시행", "README.md");

        foreach (var source in new[] { readme, linuxRunbook, updateGuide, testReadme })
        {
            Assert.Contains("-FailOnOperationalWarnings", source, StringComparison.Ordinal);
            Assert.Contains("유료 납품", source, StringComparison.Ordinal);
            Assert.Contains("AcceptAndroidSigningCertificateChange", source, StringComparison.Ordinal);
        }

        Assert.Contains("operational warning을 배포 차단", readme, StringComparison.Ordinal);
        Assert.Contains("signing certificate SHA-256", readme, StringComparison.Ordinal);
        Assert.Contains("live 전/후 operational gate를 생략하지 않고", linuxRunbook, StringComparison.Ordinal);
        Assert.Contains("live 전/후 operational gate를 생략하지 않고", updateGuide, StringComparison.Ordinal);
        Assert.Contains("live 관찰/운영 게이트 warning도 배포 차단", testReadme, StringComparison.Ordinal);
    }

    [Fact]
    public void StrictReleaseCanRequireLocalCacheConsistencyObservation()
    {
        var liveObservation = ReadRepositoryFile("테스트 시행", "Invoke-LiveObservationCheck.ps1");
        var operationalGate = ReadRepositoryFile("tools", "ops", "Invoke-GeoraePlanOperationalGate.ps1");
        var linuxRelease = ReadRepositoryFile("tools", "linux", "Publish-GeoraeplanLinuxPcRelease.ps1");
        var fullRelease = ReadRepositoryFile("tools", "release", "Publish-GeoraePlanFullRelease.ps1");
        var deployAfterTest = ReadRepositoryFile("테스트 시행", "Deploy-After-Test.ps1");
        var verificationDeploy = ReadRepositoryFile("테스트 시행", "검증완료-반영.ps1");

        Assert.Contains("[switch]$RequireLocalCacheConsistencyCheck", liveObservation, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnLocalCacheWarning", liveObservation, StringComparison.Ordinal);
        Assert.Contains("로컬 캐시 검증이 필수로 지정되었지만 실행되지 않았습니다", liveObservation, StringComparison.Ordinal);
        Assert.Contains("로컬 캐시 일치 검증 경고가 확인되었습니다", liveObservation, StringComparison.Ordinal);
        Assert.Contains("$localCacheWarningFailure = $FailOnLocalCacheWarning", liveObservation, StringComparison.Ordinal);
        Assert.Contains("- 로컬 캐시 필수 점검:", liveObservation, StringComparison.Ordinal);
        Assert.Contains("- 로컬 캐시 Warning 실패 처리:", liveObservation, StringComparison.Ordinal);

        Assert.Contains("[string]$LocalCacheAppDataRoot = \"\"", operationalGate, StringComparison.Ordinal);
        Assert.Contains("[string]$LocalCacheEvidenceDirectory = \"\"", operationalGate, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireLocalCacheConsistencyCheck", operationalGate, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnLocalCacheWarning", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationArgs += @('-LocalCacheAppDataRoot', $LocalCacheAppDataRoot)", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationArgs += @('-LocalCacheEvidenceDirectory', $LocalCacheEvidenceDirectory)", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationArgs += '-RequireLocalCacheConsistencyCheck'", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationArgs += '-FailOnLocalCacheWarning'", operationalGate, StringComparison.Ordinal);

        foreach (var source in new[] { linuxRelease, fullRelease, deployAfterTest, verificationDeploy })
        {
            Assert.Contains("[string]$LocalCacheAppDataRoot", source, StringComparison.Ordinal);
            Assert.Contains("[string]$LocalCacheEvidenceDirectory", source, StringComparison.Ordinal);
            Assert.Contains("[switch]$RequireLocalCacheConsistencyCheck", source, StringComparison.Ordinal);
            Assert.Contains("[switch]$FailOnLocalCacheWarning", source, StringComparison.Ordinal);
        }

        Assert.Contains("-RequireLocalCacheConsistencyCheck ([bool]$RequireLocalCacheConsistencyCheck)", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("-FailOnLocalCacheWarning ([bool]$FailOnLocalCacheWarning)", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-RequireLocalCacheConsistencyCheck'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnLocalCacheWarning'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-RequireLocalCacheConsistencyCheck'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnLocalCacheWarning'", deployAfterTest, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiVisibilitySmokeCapturesIntegrityDetailSamples()
    {
        var source = ReadRepositoryFile("tools", "verification", "Invoke-GeoraePlanApiVisibilitySmoke.ps1");
        var gateSource = ReadRepositoryFile("tools", "verification", "Invoke-GeoraePlanPaidDeliveryGate.ps1");

        Assert.Contains("[int]$IntegrityDetailSampleLimit", source, StringComparison.Ordinal);
        Assert.Contains("Get-IntegrityIssueDetailPath", source, StringComparison.Ordinal);
        Assert.Contains("/integrity/report/details?code=", source, StringComparison.Ordinal);
        Assert.Contains("[Uri]::EscapeDataString($Code)", source, StringComparison.Ordinal);
        Assert.Contains("IntegrityDetails = @($integrityDetails.ToArray())", source, StringComparison.Ordinal);
        Assert.Contains("SampleRows", source, StringComparison.Ordinal);
        Assert.Contains("## Integrity detail samples", source, StringComparison.Ordinal);
        Assert.Contains("DetailSamples:", gateSource, StringComparison.Ordinal);
        Assert.Contains("$data.IntegrityDetails", gateSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PaidDeliveryGateAggregatesStrictLocalCachePrintAndAndroidEvidence()
    {
        var source = ReadRepositoryFile("tools", "verification", "Invoke-GeoraePlanPaidDeliveryGate.ps1");
        var readme = ReadRepositoryFile("README.md");
        var testReadme = ReadRepositoryFile("테스트 시행", "README.md");

        Assert.Contains("[switch]$Strict", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnWarnings", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnIntegrityWarnings", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipApiVisibilitySmoke", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireLocalCache", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequirePrinter", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireOnlinePrinter", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireAndroidUpdateInPlaceSmoke", source, StringComparison.Ordinal);
        Assert.Contains("Resolve-ProjectScriptByName", source, StringComparison.Ordinal);
        Assert.Contains("Get-StepStatusFromOutput", source, StringComparison.Ordinal);
        Assert.Contains("Get-ApiVisibilitySummaryFromEvidence", source, StringComparison.Ordinal);
        Assert.Contains("api-visibility-smoke-*.json", source, StringComparison.Ordinal);
        Assert.Contains("API visibility integrity summary", source, StringComparison.Ordinal);
        Assert.Contains("integrityErrors", source, StringComparison.Ordinal);
        Assert.Contains("blockingIntegrityWarnings", source, StringComparison.Ordinal);
        Assert.Contains("$previousErrorActionPreference = $ErrorActionPreference", source, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Continue'", source, StringComparison.Ordinal);
        Assert.Contains("\\uACB0\\uACFC", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-GeoraePlanApiVisibilitySmoke.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-LiveObservationCheck.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Test-GeoraePlanPrintEnvironment.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-GeoraePlanAndroidSmoke.ps1", source, StringComparison.Ordinal);
        Assert.Contains("-MinCustomers", source, StringComparison.Ordinal);
        Assert.Contains("-MinItems", source, StringComparison.Ordinal);
        Assert.Contains("-MinInvoices", source, StringComparison.Ordinal);
        Assert.Contains("-FailOnIntegrityWarnings", source, StringComparison.Ordinal);
        Assert.Contains("-AllowedIntegrityWarningCodes", source, StringComparison.Ordinal);
        Assert.Contains("-RequireLocalCacheConsistencyCheck", source, StringComparison.Ordinal);
        Assert.Contains("-FailOnLocalCacheWarning", source, StringComparison.Ordinal);
        Assert.Contains("-FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("-RequirePrinter", source, StringComparison.Ordinal);
        Assert.Contains("-RequireOnlinePrinter", source, StringComparison.Ordinal);
        Assert.Contains("-FailOnWarnings", source, StringComparison.Ordinal);
        Assert.Contains("$effectiveFailOnWarnings = [bool]($Strict -or $FailOnWarnings)", source, StringComparison.Ordinal);
        Assert.Contains("$effectiveFailOnIntegrityWarnings = [bool]($Strict -or $FailOnIntegrityWarnings)", source, StringComparison.Ordinal);
        Assert.Contains("-RequireUpdateInPlace", source, StringComparison.Ordinal);
        Assert.Contains("Strict or RequireAndroidUpdateInPlaceSmoke was specified, but AndroidApkPath is empty.", source, StringComparison.Ordinal);
        Assert.Contains("api-visibility-smoke", source, StringComparison.Ordinal);
        Assert.Contains("## Strict mode skipped steps", source, StringComparison.Ordinal);
        Assert.Contains("## Warning steps", source, StringComparison.Ordinal);
        Assert.Contains("elseif ($warnings.Count -gt 0)", source, StringComparison.Ordinal);
        Assert.Contains("Warning steps are treated as FAIL", source, StringComparison.Ordinal);
        Assert.Contains("result=$overallStatus", source, StringComparison.Ordinal);
        Assert.Contains("paid_delivery_gate_report=", source, StringComparison.Ordinal);

        Assert.Contains("Invoke-GeoraePlanPaidDeliveryGate.ps1", readme, StringComparison.Ordinal);
        Assert.Contains("Invoke-GeoraePlanPaidDeliveryGate.ps1", testReadme, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalCacheConsistencyDetectsNonInventoryAndAssetWarehouseStockResidues()
    {
        var source = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanLocalCacheConsistency.ps1");

        Assert.Contains("itemWarehouseStocks = 'ItemWarehouseStocks'", source, StringComparison.Ordinal);
        Assert.Contains("result[\"inventoryResidues\"]", source, StringComparison.Ordinal);
        Assert.Contains("normalize_tracking", source, StringComparison.Ordinal);
        Assert.Contains("STOCK = \"\\uc7ac\\uace0\"", source, StringComparison.Ordinal);
        Assert.Contains("ASSET = \"\\uc790\\uc0b0\"", source, StringComparison.Ordinal);
        Assert.Contains("checkedNonInventoryItemCount", source, StringComparison.Ordinal);
        Assert.Contains("currentStockResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("warehouseStockResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("warehouseStockQuantityResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("${currentStockResidueCount}건", source, StringComparison.Ordinal);
        Assert.Contains("${warehouseStockResidueCount}건", source, StringComparison.Ordinal);
        Assert.Contains("비재고/자산/렌탈료 품목의 CurrentStock 잔여값", source, StringComparison.Ordinal);
        Assert.Contains("비재고/자산/렌탈료 품목에 연결된 로컬 ItemWarehouseStocks 잔여 row", source, StringComparison.Ordinal);
        Assert.Contains("## 비재고/자산 품목 재고 잔여 row 점검", source, StringComparison.Ordinal);
        Assert.Contains("WarehouseStockQuantityRows", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReleaseForwardsExplicitRentalTemplateRiskAcceptanceToLinuxDeploy()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("if ($AcceptRentalTemplateItemReferenceRisk)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AcceptRentalTemplateItemReferenceRisk",
            "if ($AcceptRentalTemplateItemReferenceRisk)",
            "$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'");
    }

    [Fact]
    public void DeployAfterTestForwardsExplicitRentalTemplateRiskAcceptanceToLinuxDeploy()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");

        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipPostDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PostDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("if ($AcceptRentalTemplateItemReferenceRisk)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'", source, StringComparison.Ordinal);
        Assert.Contains("if ($SkipPostDeployOperationalGate)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-SkipPostDeployOperationalGate'", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += @('-PostDeployBaseUrl', $PostDeployBaseUrl)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AcceptRentalTemplateItemReferenceRisk",
            "if ($AcceptRentalTemplateItemReferenceRisk)",
            "$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'");
    }

    [Fact]
    public void VerificationDeployWrapperExposesExplicitGateSkipAndRiskOptions()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "검증완료-반영.ps1");

        Assert.Contains("[switch]$SkipPreDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipPostDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PreDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PostDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("& $scriptPath @PSBoundParameters", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReleaseForwardsAndroidAotAndTrimmingOverridesToApkBuild()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$DisableAndroidAot", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$DisableAndroidTrimming", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidAot)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableAot'", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidTrimming)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableTrimming'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$androidArgs = @(",
            "if ($DisableAndroidAot)",
            "$androidArgs += '-DisableAot'",
            "if ($DisableAndroidTrimming)",
            "$androidArgs += '-DisableTrimming'",
            "& powershell @androidArgs");
    }

    [Fact]
    public void FullReleaseForwardsExplicitLegacyAndroidDebugSigningRiskAcceptanceToApkBuild()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$AllowLegacyAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("if ($AllowLegacyAndroidDebugSigning)", source, StringComparison.Ordinal);
        Assert.Contains("Legacy Android debug signing is explicitly allowed", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-AllowDebugSigning'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$androidArgs += '-AllowDebugSigning' # default", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AllowLegacyAndroidDebugSigning",
            "Write-Warning \"Legacy Android debug signing is explicitly allowed",
            "$androidArgs = @(",
            "$androidArgs += '-AllowDebugSigning'",
            "& powershell @androidArgs");
    }

    [Fact]
    public void FullReleasePreflightsAndroidReleaseSigningBeforeBuildingArtifacts()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("function Assert-AndroidReleaseSigningReady", source, StringComparison.Ordinal);
        Assert.Contains("Android signing config not found before release build", source, StringComparison.Ordinal);
        Assert.Contains("Android keystore not found before release build", source, StringComparison.Ordinal);
        Assert.Contains("Release Android package is using a debug signing key before release build", source, StringComparison.Ordinal);
        Assert.Contains("AllowLegacyAndroidDebugSigning:$AllowLegacyAndroidDebugSigning", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "if ($AllowLegacyAndroidDebugSigning)",
            "Assert-AndroidReleaseSigningReady -SigningConfigPath $SigningConfigPath",
            "$solution = Get-ChildItem",
            "& $dotnetExe build",
            "& powershell @androidArgs");
    }

    [Fact]
    public void FullReleaseBlocksAndroidSigningCertificateChangeBeforePublishingManifest()
    {
        var fullRelease = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");
        var continuityScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Test-GeoraePlanAndroidSigningContinuity.ps1");
        var linuxRelease = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var deployAfterTest = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");
        var verificationDeploy = ReadRepositoryFile(
            "테스트 시행",
            "검증완료-반영.ps1");

        Assert.Contains("[switch]$SkipAndroidSigningContinuityCheck", fullRelease, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptAndroidSigningCertificateChange", fullRelease, StringComparison.Ordinal);
        Assert.Contains("function Resolve-AndroidDeploymentPackage", fullRelease, StringComparison.Ordinal);
        Assert.Contains("Test-GeoraePlanAndroidSigningContinuity.ps1", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$continuityArgs += '-AcceptCertificateChange'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("Android signing certificate continuity check failed", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-SkipAndroidSigningContinuityCheck'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptAndroidSigningCertificateChange'", fullRelease, StringComparison.Ordinal);
        AssertInOrder(
            fullRelease,
            "& powershell @androidArgs",
            "Test-GeoraePlanAndroidSigningContinuity.ps1",
            "$updateAssetsScript = Join-Path");

        Assert.Contains("[switch]$SkipAndroidSigningContinuityCheck", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptAndroidSigningCertificateChange", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("function Invoke-AndroidSigningContinuityGate", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("pre-deploy_android_signing_continuity_start", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$manifestPath = Join-Path (Join-Path $PublishRoot 'updates\\manifest') ($Channel + '.json')", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$localAndroidFileName = [string]$publishedManifest.android.fileName", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$localAndroidPackagePath = Join-Path $androidDownloadsRoot $localAndroidFileName", linuxRelease, StringComparison.Ordinal);
        var continuityGateStart = linuxRelease.IndexOf("function Invoke-AndroidSigningContinuityGate", StringComparison.Ordinal);
        var continuityGateEnd = linuxRelease.IndexOf("function Update-PublishedAppSettings", continuityGateStart, StringComparison.Ordinal);
        Assert.True(continuityGateStart >= 0 && continuityGateEnd > continuityGateStart);
        var continuityGateSource = linuxRelease[continuityGateStart..continuityGateEnd];
        Assert.DoesNotContain("Sort-Object LastWriteTime", continuityGateSource, StringComparison.Ordinal);
        Assert.Contains("-AcceptCertificateChange ([bool]$AcceptAndroidSigningCertificateChange)", linuxRelease, StringComparison.Ordinal);
        AssertInOrder(
            linuxRelease,
            "Invoke-GeoraePlanDurableUpdateAssetPublish `",
            "elseif ($MirrorToLive -and $PreserveLiveAndroidUpdate)",
            "elseif ($MirrorToLive -and -not $SkipAndroidSigningContinuityCheck.IsPresent)",
            "Invoke-AndroidSigningContinuityGate `",
            "Update-PublishedAppSettings -PublishRoot $tempPublishRoot");

        Assert.Contains("[switch]$SkipAndroidSigningContinuityCheck", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("[switch]$PreserveLiveAndroidUpdate", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-PreserveLiveAndroidUpdate'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptAndroidSigningCertificateChange", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-SkipAndroidSigningContinuityCheck'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptAndroidSigningCertificateChange'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipAndroidSigningContinuityCheck", verificationDeploy, StringComparison.Ordinal);
        Assert.Contains("[switch]$PreserveLiveAndroidUpdate", verificationDeploy, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptAndroidSigningCertificateChange", verificationDeploy, StringComparison.Ordinal);

        Assert.Contains("[switch]$AcceptCertificateChange", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Release APK signing certificate differs from the currently published Android package", continuityScript, StringComparison.Ordinal);
        Assert.Contains("existing installed APK cannot be updated in place", continuityScript, StringComparison.Ordinal);
        Assert.Contains("remote_certificate_sha256", continuityScript, StringComparison.Ordinal);
        Assert.Contains("local_certificate_sha256", continuityScript, StringComparison.Ordinal);
        Assert.Contains("android_signing_continuity=FAIL", continuityScript, StringComparison.Ordinal);
        Assert.Contains("android_signing_continuity=ACCEPTED_CERTIFICATE_CHANGE", continuityScript, StringComparison.Ordinal);
        Assert.Contains("android_signing_continuity=PASS", continuityScript, StringComparison.Ordinal);
        Assert.Contains("[string]$ApkAnalyzerPath", continuityScript, StringComparison.Ordinal);
        Assert.Contains("manifest version-code $ApkPath", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$localVersionCode -le $publishedVersionCode", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Local APK versionCode must be greater than the published APK versionCode", continuityScript, StringComparison.Ordinal);
        Assert.Contains("local_version_code=$localVersionCode", continuityScript, StringComparison.Ordinal);
        Assert.Contains("published_version_code=$publishedVersionCode", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Join-Path $env:LOCALAPPDATA 'Android\\Sdk'", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$versionCode -le 0", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$previousJavaHome = $env:JAVA_HOME", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$previousPath = $env:PATH", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $JavaHome", continuityScript, StringComparison.Ordinal);
        Assert.Contains("[System.IO.Path]::PathSeparator", continuityScript, StringComparison.Ordinal);
        Assert.Contains("finally {", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $previousJavaHome", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$env:PATH = $previousPath", continuityScript, StringComparison.Ordinal);
        Assert.Contains("[string]$PackageName = \"kr.georaeplan.mobile\"", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$androidFileName = [string]$manifest.android.fileName", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Assert-AndroidManifestFileName -FileName $androidFileName", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$manifestSha256 -notmatch '^[0-9a-f]{64}$'", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Resolve-ValidatedAndroidPackageUri", continuityScript, StringComparison.Ordinal);
        Assert.Contains("'/updates/download/android/'", continuityScript, StringComparison.Ordinal);
        Assert.Contains("-MaximumRedirection 0", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$effectivePackageUri = Get-DownloadEffectiveUri", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$remoteApkPath = Join-Path $probeRunDirectory $androidFileName", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$downloadedSha256 = Assert-DownloadedAndroidApkHash", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Published Android APK SHA-256 does not match manifest android sha256.", continuityScript, StringComparison.Ordinal);
        Assert.Contains("manifest application-id $ApkPath", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$localMetadata = Get-ApkManifestMetadata", continuityScript, StringComparison.Ordinal);
        Assert.Contains("$publishedMetadata = Get-ApkManifestMetadata", continuityScript, StringComparison.Ordinal);
        Assert.Contains("Android APK applicationId does not match PackageName.", continuityScript, StringComparison.Ordinal);
        AssertInOrder(
            continuityScript,
            "$androidFileName = [string]$manifest.android.fileName",
            "$manifestSha256 = ([string]$manifest.android.sha256)",
            "$remotePackageUri = Resolve-ValidatedAndroidPackageUri",
            "$downloadResponse = Invoke-WebRequest",
            "$effectivePackageUri = Get-DownloadEffectiveUri",
            "$downloadedSha256 = Assert-DownloadedAndroidApkHash",
            "-ExpectedSha256 $manifestSha256",
            "$localMetadata = Get-ApkManifestMetadata",
            "$publishedMetadata = Get-ApkManifestMetadata",
            "Assert-AndroidPackageIdentity `",
            "if ($localVersionCode -le $publishedVersionCode)",
            "$localCertificate = Get-ApkSigningCertificate",
            "if (-not [string]::Equals($localCertificate.CertificateSha256",
            "if ($AcceptCertificateChange)");
    }

    [Fact]
    public async Task AndroidSigningContinuity_LocalIdentityAndHashGuardsFailClosed()
    {
        var continuityScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Test-GeoraePlanAndroidSigningContinuity.ps1");
        var guardFunctions = ExtractPowerShellScriptSection(
            continuityScript,
            "function Resolve-ValidatedAndroidPackageUri",
            "function Resolve-ApkSignerPath");
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-android-continuity-guard-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var apkPath = Path.Combine(testRoot, "published.apk");
        var harnessPath = Path.Combine(testRoot, "guard-harness.ps1");

        try
        {
            File.WriteAllText(apkPath, "published-apk-test-payload");
            var escapedApkPath =
                EscapePowerShellSingleQuotedLiteral(apkPath);
            File.WriteAllText(
                harnessPath,
                $$"""
                $ErrorActionPreference = 'Stop'
                {{guardFunctions}}

                function Assert-ExpectedFailure {
                    param(
                        [scriptblock]$Action,
                        [string]$ExpectedMessage
                    )

                    $failed = $false
                    try {
                        & $Action
                    }
                    catch {
                        if ($_.Exception.Message.IndexOf(
                                $ExpectedMessage,
                                [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                            throw
                        }
                        $failed = $true
                    }

                    if (-not $failed) {
                        throw "Expected failure was not raised: $ExpectedMessage"
                    }
                }

                $validUri = Resolve-ValidatedAndroidPackageUri `
                    -BaseUrl 'https://example.test' `
                    -PackageUrl '/updates/download/android/published.apk' `
                    -FileName 'published.apk'
                if ($validUri.AbsoluteUri -ne 'https://example.test/updates/download/android/published.apk') {
                    throw 'Valid Android package URI changed unexpectedly.'
                }

                Assert-ExpectedFailure `
                    -Action {
                        Resolve-ValidatedAndroidPackageUri `
                            -BaseUrl 'https://example.test' `
                            -PackageUrl '/updates/download/android/other.apk' `
                            -FileName 'published.apk'
                    } `
                    -ExpectedMessage 'expected same-origin Android download route'
                Assert-ExpectedFailure `
                    -Action {
                        Resolve-ValidatedAndroidPackageUri `
                            -BaseUrl 'https://example.test' `
                            -PackageUrl 'https://other.test/updates/download/android/published.apk' `
                            -FileName 'published.apk'
                    } `
                    -ExpectedMessage 'expected same-origin Android download route'
                Assert-ExpectedFailure `
                    -Action {
                        Assert-AndroidManifestFileName -FileName '../published.apk'
                    } `
                    -ExpectedMessage 'safe APK leaf name'

                $actualHash = (Get-FileHash -LiteralPath '{{escapedApkPath}}' -Algorithm SHA256).Hash.ToLowerInvariant()
                $validatedHash = Assert-DownloadedAndroidApkHash `
                    -ApkPath '{{escapedApkPath}}' `
                    -ExpectedSha256 $actualHash
                if ($validatedHash -ne $actualHash) {
                    throw 'Valid Android APK hash changed unexpectedly.'
                }
                Assert-ExpectedFailure `
                    -Action {
                        Assert-DownloadedAndroidApkHash `
                            -ApkPath '{{escapedApkPath}}' `
                            -ExpectedSha256 ('0' * 64)
                    } `
                    -ExpectedMessage 'does not match manifest'

                Assert-AndroidPackageIdentity `
                    -Metadata ([pscustomobject]@{ ApplicationId = 'kr.georaeplan.mobile' }) `
                    -PackageName 'kr.georaeplan.mobile' `
                    -SourceName 'published'
                Assert-ExpectedFailure `
                    -Action {
                        Assert-AndroidPackageIdentity `
                            -Metadata ([pscustomobject]@{ ApplicationId = 'kr.georaeplan.other' }) `
                            -PackageName 'kr.georaeplan.mobile' `
                            -SourceName 'published'
                    } `
                    -ExpectedMessage 'applicationId does not match PackageName'

                Write-Output 'android_continuity_local_guards=PASS'
                """,
                System.Text.Encoding.Unicode);

            var result = await RunPowerShellAsync(harnessPath);

            Assert.True(
                result.ExitCode == 0,
                $"Continuity local guard harness failed.{Environment.NewLine}{result.StdOut}{Environment.NewLine}{result.StdErr}");
            Assert.Contains(
                "android_continuity_local_guards=PASS",
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
    public async Task LinuxRelease_AndroidSigningContinuityUsesManifestPackageInsteadOfNewestFileTimestamp()
    {
        var repositoryRoot = FindRepositoryRoot();
        var linuxRelease = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var functionStart = linuxRelease.IndexOf("function Invoke-AndroidSigningContinuityGate", StringComparison.Ordinal);
        var functionEnd = linuxRelease.IndexOf("function Update-PublishedAppSettings", functionStart, StringComparison.Ordinal);
        Assert.True(functionStart >= 0 && functionEnd > functionStart);

        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "android-signing-selection-tests",
            Guid.NewGuid().ToString("N"));
        var publishRoot = Path.Combine(testRoot, "publish");
        var manifestRoot = Path.Combine(publishRoot, "updates", "manifest");
        var downloadsRoot = Path.Combine(publishRoot, "updates", "downloads", "android");
        var toolsRoot = Path.Combine(testRoot, "tools", "mobile");
        var targetFileName = "tradeplan-android-v0.2.81.apk";
        var staleFileName = "tradeplan-android-v0.2.80.apk";
        var targetPath = Path.Combine(downloadsRoot, targetFileName);
        var stalePath = Path.Combine(downloadsRoot, staleFileName);

        try
        {
            Directory.CreateDirectory(manifestRoot);
            Directory.CreateDirectory(downloadsRoot);
            Directory.CreateDirectory(toolsRoot);
            File.WriteAllText(targetPath, "new manifest package");
            File.WriteAllText(stalePath, "old rollback package");
            File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddMinutes(5));

            var manifestJson = JsonSerializer.Serialize(
                new { android = new { fileName = targetFileName } },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(
                Path.Combine(manifestRoot, "stable.json"),
                manifestJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var stubScriptPath = Path.Combine(toolsRoot, "Test-GeoraePlanAndroidSigningContinuity.ps1");
            File.WriteAllText(
                stubScriptPath,
                "param([string]$ProjectRoot,[string]$LocalApkPath,[string]$BaseUrl,[string]$Channel,[switch]$AcceptCertificateChange)" + Environment.NewLine +
                "Write-Host \"stub_local_apk=$LocalApkPath\"" + Environment.NewLine +
                "exit 0" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var testScriptPath = Path.Combine(testRoot, "invoke-continuity-selection.ps1");
            var functionSource = linuxRelease[functionStart..functionEnd];
            var testScript = functionSource + Environment.NewLine +
                             $"Invoke-AndroidSigningContinuityGate -Root '{EscapePowerShellSingleQuotedLiteral(testRoot)}' -PublishRoot '{EscapePowerShellSingleQuotedLiteral(publishRoot)}' -BaseUrl 'https://trade.2884.kr' -Channel 'stable'" +
                             Environment.NewLine;
            File.WriteAllText(
                testScriptPath,
                testScript,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"stub_local_apk={targetPath}", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"stub_local_apk={stalePath}", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void RentalTemplateRepairPlanScript_GeneratesRollbackPatchOnlyAfterSelectValidation()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1");

        Assert.Contains("[switch]$ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchMode = 'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("review-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings.normalized.csv", source, StringComparison.Ordinal);
        Assert.Contains("validation-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved_item_not_found", source, StringComparison.Ordinal);
        Assert.Contains("current_reference_is_valid_now", source, StringComparison.Ordinal);
        Assert.Contains("ValidationStatus -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemId = Get-CsvValue -Row $row -Names @('ProposedItemId')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedSource = Get-CsvValue -Row $row -Names @('ProposedSource')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedConfidence = Get-CsvValue -Row $row -Names @('ProposedConfidence')", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = Get-CsvValue -Row $row -Names @('ApprovedItemId', 'NewItemId', 'TargetItemId')", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedApprovedMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedReadyMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Approved mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Ready mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedReadyMappingCount requires -ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("repair_plan_gate_status=$repairPlanGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("Repair plan gate failed", source, StringComparison.Ordinal);
        Assert.Contains("create temporary table \"RentalBillingTemplateItemReferenceRepairCounts\" on commit drop as", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("select * from \"RentalBillingTemplateItemReferenceRepairCounts\"", source, StringComparison.Ordinal);
        Assert.Contains("transaction-time assertions for approved, target profile, backup, and updated profile counts", source, StringComparison.Ordinal);
        Assert.Contains("jsonb_set(x.elem, '{CatalogItemId}', to_jsonb(a.approved_item_id::text), true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("jsonb_set(x.elem, '{ItemId}', to_jsonb(a.approved_item_id::text), true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@('ApprovedItemId', 'ProposedItemId'", source, StringComparison.Ordinal);
        Assert.Contains("repair-<db>-rollback.sql", source, StringComparison.Ordinal);
        Assert.Contains("Run this SQL against a cloned/test database first.", source, StringComparison.Ordinal);
        Assert.Contains("$terminalStatement = if ($Mode -eq 'Commit') { 'commit;' } else { 'rollback;' }", source, StringComparison.Ordinal);
        Assert.Contains("patch_sql=none", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$csvText = Invoke-RemotePsqlCsv -Database $database -Sql $sql",
            "Where-Object { $_.ValidationStatus -eq 'ready' }",
            "$patchSql = New-PatchSql -Database $database");

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateRepairReadinessGate_ChainsApprovalAndSelectOnlyRepairPlanChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateRepairReadiness.ps1");

        Assert.Contains("New-GeoraePlanRentalTemplateApprovalIntakePack.ps1", source, StringComparison.Ordinal);
        Assert.Contains("New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("-RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("-ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedApprovedMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedReadyMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipCurrentCandidateKeyCheck", source, StringComparison.Ordinal);
        Assert.Contains("-PatchMode", source, StringComparison.Ordinal);
        Assert.Contains("'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("candidate-rows.csv", source, StringComparison.Ordinal);
        Assert.Contains("current-candidates", source, StringComparison.Ordinal);
        Assert.Contains("current-candidate-key-mismatches.csv", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-repair-readiness-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("Current unresolved candidate count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Approval mapping keys do not match current unresolved candidate keys", source, StringComparison.Ordinal);
        Assert.Contains("current_candidate_missing_from_approval", source, StringComparison.Ordinal);
        Assert.Contains("approval_key_not_in_current_candidates", source, StringComparison.Ordinal);
        Assert.Contains("Repair readiness gate failed", source, StringComparison.Ordinal);
        Assert.Contains("this script never executes SQL patches", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_repair_readiness_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is not rollback-only", source, StringComparison.Ordinal);
        Assert.Contains("Generated readiness SQL must not contain a standalone commit statement", source, StringComparison.Ordinal);
        Assert.Contains("do $repair_assert$", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is missing required safety assertion fragment", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "approval-intake-require-all",
            "current-candidate-key-check",
            "repair-plan-select-ready");

        Assert.DoesNotContain("PatchMode', 'Commit'", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateManualReviewPackScript_DoesNotPrefillApprovalsAndKeepsOutputLocal()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateManualReviewPack.ps1");

        Assert.Contains("manual-review-decision-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-option-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-decision-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("CandidateOptionCount", source, StringComparison.Ordinal);
        Assert.Contains("Option${optionNumber}ItemId", source, StringComparison.Ordinal);
        Assert.Contains("ManualReviewPriority", source, StringComparison.Ordinal);
        Assert.Contains("P1_asset_multi_small", source, StringComparison.Ordinal);
        Assert.Contains("choose_one_active_asset_item", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateApprovalIntakeScript_ClearsDryRunApprovalsAndValidatesFilledRowsLocally()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateApprovalIntakePack.ps1");

        Assert.Contains("approval-intake-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("proposed_ready_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("manual_review_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);
        Assert.Contains("OriginalReviewDecision", source, StringComparison.Ordinal);
        Assert.Contains("OriginalApprovedItemId", source, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovalDecision", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("validate_existing_approval_intake", source, StringComparison.Ordinal);
        Assert.Contains("Dry-run/system reviewer markers cannot be used as business approval.", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId is not in suggested/candidate option ids.", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Duplicate Database/ProfileId/TemplateOrdinal keys were found in approval intake rows", source, StringComparison.Ordinal);
        Assert.Contains("approved_input_valid", source, StringComparison.Ordinal);
        Assert.Contains("pending_approval", source, StringComparison.Ordinal);
        Assert.Contains("invalid_approval_input", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation-status-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("approval_input_gate_status=$approvalInputGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("valid approved rows for follow-up SELECT-only validation", source, StringComparison.Ordinal);
        Assert.Contains("Approval intake gate failed", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri InvokeValidatePackageUri(string packageUrl, Uri baseUri)
    {
        var method = typeof(DesktopAppUpdateService).GetMethod(
            "ValidatePackageUri",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [packageUrl, baseUri]);
        return Assert.IsType<Uri>(result);
    }

    private static void AssertValidatePackageUriRejected(string packageUrl, Uri baseUri)
    {
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeValidatePackageUri(packageUrl, baseUri));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        params (string Name, string? Value)[] argumentsAndEnvironment)
    {
        var windowsPowerShellHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0");
        var windowsPowerShellPath = Path.Combine(
            windowsPowerShellHome,
            "powershell.exe");
        Assert.True(
            File.Exists(windowsPowerShellPath),
            $"Windows PowerShell was not found: {windowsPowerShellPath}");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = windowsPowerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.Environment["PSModulePath"] = Path.Combine(
            windowsPowerShellHome,
            "Modules");
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
        var exited = process.WaitForExit(120_000);
        Assert.True(exited, $"PowerShell script timed out: {scriptPath}");

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
    private sealed record RollbackManifestPackage(
        string Platform,
        string Version,
        FileInfo Package,
        string PackageUrl,
        IReadOnlyList<FileInfo>? Installers = null);
    private sealed record PointerRollbackFixture(
        string ProjectRoot,
        string OutputRoot,
        string TransactionRoot,
        string TransactionOwnerPath,
        string CurrentManifestPath,
        string PreviousManifestPath,
        string DeliveryManifestPath,
        string PointerPath,
        string PreviousDeliveryGenerationPath,
        string CurrentGenerationId,
        string PreviousGenerationId,
        string CurrentManifestHash,
        string PreviousManifestHash);

    private static FileInfo WriteRollbackTestPackage(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }

    private static string CreateRollbackTestManifestJson(
        string version,
        FileInfo package,
        params FileInfo[] installers)
        => CreateRollbackTestManifestJson(
            new RollbackManifestPackage(
                "desktop",
                version,
                package,
                $"/updates/download/desktop/{Uri.EscapeDataString(package.Name)}",
                installers));

    private static string CreateRollbackTestManifestJson(params RollbackManifestPackage[] packages)
    {
        var desktop = packages.FirstOrDefault(static package => string.Equals(package.Platform, "desktop", StringComparison.OrdinalIgnoreCase));
        var android = packages.FirstOrDefault(static package => string.Equals(package.Platform, "android", StringComparison.OrdinalIgnoreCase));

        return JsonSerializer.Serialize(new
        {
            channel = "stable",
            generatedAtUtc = DateTimeOffset.UtcNow,
            desktop = CreateRollbackManifestPlatformNode(desktop),
            android = CreateRollbackManifestPlatformNode(android)
        });
    }

    private static string ReadDesktopManifestVersion(string path)
        => ReadManifestPlatformVersion(path, "desktop");

    private static string ReadManifestPlatformVersion(string path, string platform)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(platform).GetProperty("version").GetString() ?? string.Empty;
    }

    private static object? CreateRollbackManifestPlatformNode(RollbackManifestPackage? package)
    {
        if (package is null)
            return null;

        var node = new Dictionary<string, object?>
        {
            ["platform"] = package.Platform,
            ["version"] = package.Version,
            ["packageUrl"] = package.PackageUrl,
            ["fileName"] = package.Package.Name,
            ["fileSize"] = package.Package.Length,
            ["sha256"] = ComputeSha256(package.Package.FullName)
        };
        if (package.Installers is { Count: > 0 })
        {
            node["installers"] = package.Installers
                .Select(installer => new Dictionary<string, object?>
                {
                    ["packageUrl"] =
                        $"/updates/download/{package.Platform}/" +
                        Uri.EscapeDataString(installer.Name),
                    ["fileName"] = installer.Name,
                    ["fileSize"] = installer.Length,
                    ["sha256"] = ComputeSha256(installer.FullName)
                })
                .ToArray();
        }

        return node;
    }

    private static string ComputeSha256(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static string ComputeSha256AfterWrite(string path, string content)
    {
        File.WriteAllText(path, content);
        return ComputeSha256(path);
    }

    private static bool TryCreateFileSymbolicLinkFixture(
        string root,
        out string unavailableReason)
    {
        unavailableReason = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "the test requires Windows reparse points";
            return false;
        }

        var targetPath = Path.Combine(root, "symlink-probe-target.txt");
        var linkPath = Path.Combine(root, "symlink-probe-link.txt");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(targetPath, "probe");
            File.CreateSymbolicLink(linkPath, targetPath);
            if (!File.Exists(linkPath) ||
                (File.GetAttributes(linkPath) &
                    FileAttributes.ReparsePoint) == 0)
            {
                unavailableReason =
                    "the created probe was not a file reparse point";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is PlatformNotSupportedException or
            UnauthorizedAccessException or
            IOException)
        {
            unavailableReason =
                exception.GetType().Name + ": " + exception.Message;
            return false;
        }
        finally
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);
            if (File.Exists(targetPath))
                File.Delete(targetPath);
        }
    }

    private static void WriteMinimalLinuxUpdatePointerFixture(
        string projectRoot,
        string publishRoot)
    {
        const string generationId =
            "abcdef0123456789abcdef0123456789";
        var updatesRoot = Path.Combine(publishRoot, "updates");
        var manifestRoot = Path.Combine(updatesRoot, "manifest");
        var generationRoot = Path.Combine(
            manifestRoot,
            "generations",
            "stable");
        Directory.CreateDirectory(generationRoot);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>
            {
                ["channel"] = "stable",
                ["generationId"] = generationId,
                ["desktop"] = null,
                ["android"] = null
            });
        var manifestHash = Convert.ToHexString(
            SHA256.HashData(manifestBytes));
        File.WriteAllBytes(
            Path.Combine(generationRoot, generationId + ".json"),
            manifestBytes);
        File.WriteAllBytes(
            Path.Combine(manifestRoot, "stable.json"),
            manifestBytes);
        File.WriteAllBytes(
            Path.Combine(manifestRoot, "stable.current.json"),
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, string>
                {
                    ["owner"] =
                        "georaeplan-release-manifest-pointer",
                    ["schemaVersion"] = "1",
                    ["channel"] = "stable",
                    ["generationId"] = generationId,
                    ["manifestRelativePath"] =
                        $"generations/stable/{generationId}.json",
                    ["manifestSha256"] = manifestHash,
                    ["manifestFileSize"] =
                        manifestBytes.LongLength.ToString(
                            CultureInfo.InvariantCulture),
                    ["deliveryManifestPath"] = Path.Combine(
                        projectRoot,
                        "\uBC30\uD3EC",
                        ".georaeplan-release-generations",
                        "stable",
                        generationId + ".json"),
                    ["deliveryManifestSha256"] = manifestHash,
                    ["deliveryManifestFileSize"] =
                        manifestBytes.LongLength.ToString(
                            CultureInfo.InvariantCulture)
                }));
    }

    private static PointerRollbackFixture WritePointerRollbackFixture(
        string projectRoot)
    {
        const string currentGenerationId =
            "fedcba9876543210fedcba9876543210";
        const string previousGenerationId =
            "0123456789abcdef0123456789abcdef";
        var outputRoot = Path.Combine(projectRoot, "updates");
        var manifestRoot = Path.Combine(outputRoot, "manifest");
        var desktopRoot =
            Path.Combine(outputRoot, "downloads", "desktop");
        var deploymentRoot = Path.Combine(projectRoot, "\uBC30\uD3EC");
        var deliveryGenerationRoot = Path.Combine(
            deploymentRoot,
            ".georaeplan-release-generations",
            "stable");
        var stagedDeliveryGenerationRoot = Path.Combine(
            manifestRoot,
            "delivery-generations",
            "stable");
        var runtimeGenerationRoot = Path.Combine(
            manifestRoot,
            "generations",
            "stable");
        Directory.CreateDirectory(desktopRoot);
        Directory.CreateDirectory(deliveryGenerationRoot);
        Directory.CreateDirectory(stagedDeliveryGenerationRoot);
        Directory.CreateDirectory(runtimeGenerationRoot);

        var currentPackage = WriteRollbackTestPackage(
            desktopRoot,
            "desktop-current.zip",
            "current package");
        var previousPackage = WriteRollbackTestPackage(
            desktopRoot,
            "desktop-previous.zip",
            "previous package");
        static Dictionary<string, object?> CreatePackageNode(
            string version,
            FileInfo package)
            => new()
            {
                ["platform"] = "desktop",
                ["version"] = version,
                ["fileName"] = package.Name,
                ["fileSize"] = package.Length,
                ["sha256"] = ComputeSha256(package.FullName)
            };
        static byte[] CreateManifestBytes(
            string generationId,
            string version,
            FileInfo package)
            => JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, object?>
                {
                    ["channel"] = "stable",
                    ["generationId"] = generationId,
                    ["desktop"] = CreatePackageNode(version, package),
                    ["android"] = null
                });

        var currentManifestBytes = CreateManifestBytes(
            currentGenerationId,
            "2.0.0",
            currentPackage);
        var previousManifestBytes = CreateManifestBytes(
            previousGenerationId,
            "1.0.0",
            previousPackage);
        var currentManifestHash = Convert.ToHexString(
            SHA256.HashData(currentManifestBytes));
        var previousManifestHash = Convert.ToHexString(
            SHA256.HashData(previousManifestBytes));
        var currentRuntimePath = Path.Combine(
            runtimeGenerationRoot,
            currentGenerationId + ".json");
        var previousRuntimePath = Path.Combine(
            runtimeGenerationRoot,
            previousGenerationId + ".json");
        var currentDeliveryGenerationPath = Path.Combine(
            deliveryGenerationRoot,
            currentGenerationId + ".json");
        var previousDeliveryGenerationPath = Path.Combine(
            deliveryGenerationRoot,
            previousGenerationId + ".json");
        var previousStagedDeliveryPath = Path.Combine(
            stagedDeliveryGenerationRoot,
            previousGenerationId + ".json");
        var currentManifestPath =
            Path.Combine(manifestRoot, "stable.json");
        var previousManifestPath =
            Path.Combine(manifestRoot, "stable.previous.json");
        var deliveryManifestPath =
            Path.Combine(deploymentRoot, "stable.json");
        var pointerPath =
            Path.Combine(manifestRoot, "stable.current.json");

        File.WriteAllBytes(currentRuntimePath, currentManifestBytes);
        File.WriteAllBytes(previousRuntimePath, previousManifestBytes);
        File.WriteAllBytes(
            currentDeliveryGenerationPath,
            currentManifestBytes);
        File.WriteAllBytes(
            previousStagedDeliveryPath,
            previousManifestBytes);
        File.WriteAllBytes(currentManifestPath, currentManifestBytes);
        File.WriteAllBytes(previousManifestPath, previousManifestBytes);
        File.WriteAllBytes(deliveryManifestPath, currentManifestBytes);
        File.WriteAllBytes(
            pointerPath,
            JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, string>
                {
                    ["owner"] =
                        "georaeplan-release-manifest-pointer",
                    ["schemaVersion"] = "1",
                    ["channel"] = "stable",
                    ["generationId"] = currentGenerationId,
                    ["manifestRelativePath"] =
                        $"generations/stable/{currentGenerationId}.json",
                    ["manifestSha256"] = currentManifestHash,
                    ["manifestFileSize"] =
                        currentManifestBytes.LongLength.ToString(
                            CultureInfo.InvariantCulture),
                    ["deliveryManifestPath"] =
                        currentDeliveryGenerationPath,
                    ["deliveryManifestSha256"] = currentManifestHash,
                    ["deliveryManifestFileSize"] =
                        currentManifestBytes.LongLength.ToString(
                            CultureInfo.InvariantCulture)
                }));

        return new PointerRollbackFixture(
            projectRoot,
            outputRoot,
            Path.Combine(
                outputRoot,
                ".georaeplan-update-rollback-stable"),
            Path.Combine(
                outputRoot,
                ".georaeplan-update-rollback-stable.owner.json"),
            currentManifestPath,
            previousManifestPath,
            deliveryManifestPath,
            pointerPath,
            previousDeliveryGenerationPath,
            currentGenerationId,
            previousGenerationId,
            currentManifestHash,
            previousManifestHash);
    }

    private static string WriteDurableWrapperHarness(
        string testRoot,
        string repositoryRoot)
    {
        var linuxReleaseSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1"));
        var durableFunctions = ExtractLinuxReleaseHarnessSection(
            linuxReleaseSource,
            "function Assert-GeoraePlanLinuxRegularDirectoryChain",
            "function Assert-SafeReleaseId");
        var harnessPath =
            Path.Combine(testRoot, "durable-wrapper.ps1");
        File.WriteAllText(
            harnessPath,
            $$"""
            [CmdletBinding()]
            param(
                [Parameter(Mandatory = $true)][string]$ProjectRoot,
                [Parameter(Mandatory = $true)][string]$DurableUpdatesRoot,
                [Parameter(Mandatory = $true)][string]$PublishRoot,
                [Parameter(Mandatory = $true)][string]$UpdateAssetScript
            )
            $ErrorActionPreference = 'Stop'
            {{durableFunctions}}
            $arguments = @{
                SkipAndroid = $true
                DesktopVersion = '1.0.0'
                SkipPackagePrune = $true
            }
            Invoke-GeoraePlanDurableUpdateAssetPublish `
                -ProjectRoot $ProjectRoot `
                -DurableUpdatesRoot $DurableUpdatesRoot `
                -PublishRoot $PublishRoot `
                -UpdateAssetScript $UpdateAssetScript `
                -UpdateAssetArguments $arguments `
                -Channel 'stable'
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return harnessPath;
    }

    private static string ExtractPowerShellScriptSection(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start token was not found: {startToken}");
        Assert.True(end > start, $"End token was not found after start token: {endToken}");
        return source[start..end];
    }

    private static string ExtractLinuxReleaseHarnessSection(
        string source,
        string startToken,
        string endToken)
        => ExtractPowerShellScriptSection(
               source,
               "function Get-GeoraePlanLinuxFileSha256",
               "if ($ExpectedClientCompatibilityMode") +
           Environment.NewLine +
           ExtractPowerShellScriptSection(source, startToken, endToken);

    private static (string Path, string Version) GetVersionedDesktopFixture()
    {
        var path = typeof(ReleaseTempPathGuardTests).Assembly.Location;
        var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion
            ?? throw new InvalidOperationException(
                "Versioned desktop fixture has no ProductVersion.");
        var version = productVersion
            .Split('+')[0]
            .Split('-')[0]
            .Trim()
            .TrimStart('v', 'V');
        Assert.True(
            Version.TryParse(version, out _),
            $"Invalid desktop fixture ProductVersion: {productVersion}");
        return (path, version);
    }

    private static string EscapePowerShellSingleQuotedLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static void AssertInOrder(string source, params string[] tokens)
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
}
