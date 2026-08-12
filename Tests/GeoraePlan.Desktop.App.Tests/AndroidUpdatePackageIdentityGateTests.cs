using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidUpdatePackageIdentityGateTests
{
    private static readonly object NativeInstallerFixtureLock = new();
    private static string? s_nativeInstallerFixtureVersion;
    private static byte[]? s_nativeExeFixtureBytes;
    private static byte[]? s_nativeMsiFixtureBytes;

    [Fact]
    public void UpdateAssetPublisher_InspectsApkIdentityBeforeCreatingPublishOutputs()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("AndroidApkMetadata.ps1", source, StringComparison.Ordinal);
        Assert.Contains(
            "-PropertyName 'ApplicationId'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-PropertyName 'ApplicationDisplayVersion'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "-PropertyName 'ApplicationVersion'",
            source,
            StringComparison.Ordinal);
        AssertInOrder(
            source,
            "New-GeoraePlanAndroidApkSnapshot",
            "Assert-GeoraePlanAndroidApkMetadata",
            "$manifestRoot = Join-Path $OutputRoot 'manifest'",
            "$manifest.android = Copy-PackageWithMetadata");
    }

    [Fact]
    public void UpdateAssetPublisher_CommitsAndroidBeforeDesktopPackages()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        var androidCommit = source.IndexOf(
            "$manifest.android = Copy-PackageWithMetadata",
            StringComparison.Ordinal);
        var desktopCommit = source.IndexOf(
            "$manifest.desktop = Copy-PackageWithMetadata",
            StringComparison.Ordinal);

        Assert.True(androidCommit >= 0);
        Assert.True(
            desktopCommit > androidCommit,
            "Desktop packages are committed before the Android identity is fixed.");
    }

    [Fact]
    public void TestEnvironmentPreparation_ValidatesApkAndWritesRuntimeSidecarBeforeCertification()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "테스트-환경-준비.ps1");

        Assert.Contains("[string]$AndroidPackagePath", source, StringComparison.Ordinal);
        Assert.Contains("[string]$ApkAnalyzerPath", source, StringComparison.Ordinal);
        Assert.Contains("[string]$JavaSdkDirectory", source, StringComparison.Ordinal);
        Assert.Contains("AndroidApkMetadata.ps1", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "New-GeoraePlanAndroidApkSnapshot",
            "Assert-GeoraePlanAndroidApkMetadata",
            "android-package.metadata.json",
            "$readyMarkerTempPath = Join-Path $OutputRoot");
    }

    [Fact]
    public void TestEnvironmentPreparation_InspectsAndroidAfterLeaseBeforeRuntimeMutation()
    {
        var source = ReadRepositoryFile(
            "\uD14C\uC2A4\uD2B8 \uC2DC\uD589",
            "\uD14C\uC2A4\uD2B8-\uD658\uACBD-\uC900\uBE44.ps1");
        var lease = source.LastIndexOf(
            "$preparationLease = [IO.File]::Open(",
            StringComparison.Ordinal);
        var firstInspection = source.IndexOf(
            "-InspectOnly",
            lease,
            StringComparison.Ordinal);
        var secondInspection = source.IndexOf(
            "-InspectOnly",
            firstInspection + 1,
            StringComparison.Ordinal);
        var markerRemoval = source.IndexOf(
            "if (Test-Path -LiteralPath $runtimeReadyMarkerPath)",
            lease,
            StringComparison.Ordinal);

        Assert.True(lease >= 0 && firstInspection > lease);
        Assert.True(
            secondInspection > firstInspection,
            "Android absence was not confirmed by a second pre-mutation inspection.");
        Assert.True(
            markerRemoval > secondInspection,
            "Android identity/absence was not fixed before the old ready marker/runtime mutation.");
    }

    [Fact]
    public async Task UpdateAssetPublisher_PublishesOnlyTheInspectedAndroidIdentity()
    {
        using var fixture = new PublisherFixture("0.2.81");

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        Assert.True(File.Exists(manifestPath));
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var android = document.RootElement.GetProperty("android");
        Assert.Equal("0.2.81", android.GetProperty("version").GetString());
        Assert.Equal(
            "tradeplan-android-v0.2.81.apk",
            android.GetProperty("fileName").GetString());
        Assert.Equal(
            fixture.ApkBytes.LongLength,
            android.GetProperty("fileSize").GetInt64());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fixture.ApkBytes)),
            android.GetProperty("sha256").GetString(),
            ignoreCase: true);
        Assert.Equal(
            fixture.ApkBytes,
            File.ReadAllBytes(Path.Combine(
                fixture.OutputRoot,
                "downloads",
                "android",
                "tradeplan-android-v0.2.81.apk")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsMismatchedApkBeforeCreatingOutputs()
    {
        using var fixture = new PublisherFixture("0.2.80");

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "versionName mismatch",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputRoot));
        Assert.False(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "배포",
            "stable.json")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDifferentBytesForExistingVersion()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var canonicalPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
        var existingBytes = Encoding.UTF8.GetBytes("different existing bytes");
        File.WriteAllBytes(canonicalPath, existingBytes);

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Existing canonical Android package conflicts",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingBytes, File.ReadAllBytes(canonicalPath));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
    }

    [Theory]
    [InlineData("mismatched-file")]
    [InlineData("directory")]
    [InlineData("junction")]
    public async Task UpdateAssetPublisher_RejectsMismatchedCanonicalBeforeAnyMutation(
        string canonicalShape)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var canonicalAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        var desktopDestinationRoot = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop");
        var desktopZipPath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-installer-v{fixture.DesktopVersion}.zip");
        var desktopExePath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-setup-v{fixture.DesktopVersion}.exe");
        var desktopMsiPath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-admin-v{fixture.DesktopVersion}.msi");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        var deliveryManifestPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalAndroidPath)!);
        Directory.CreateDirectory(desktopDestinationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryManifestPath)!);

        var androidSentinel = Encoding.UTF8.GetBytes(
            "mismatched canonical android sentinel");
        var externalMarker = Path.Combine(
            fixture.ProjectRoot,
            "canonical-junction-target",
            "preserve.txt");
        var desktopZipSentinel = Encoding.UTF8.GetBytes(
            "desktop zip destination sentinel");
        var desktopExeSentinel = Encoding.UTF8.GetBytes(
            "desktop exe destination sentinel");
        var desktopMsiSentinel = Encoding.UTF8.GetBytes(
            "desktop msi destination sentinel");
        if (canonicalShape == "mismatched-file")
        {
            File.WriteAllBytes(canonicalAndroidPath, androidSentinel);
        }
        else if (canonicalShape == "directory")
        {
            Directory.CreateDirectory(canonicalAndroidPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(externalMarker)!);
            File.WriteAllText(externalMarker, "preserve external target");
            CreateJunction(
                canonicalAndroidPath,
                Path.GetDirectoryName(externalMarker)!);
        }
        File.WriteAllBytes(desktopZipPath, desktopZipSentinel);
        File.WriteAllBytes(desktopExePath, desktopExeSentinel);
        File.WriteAllBytes(desktopMsiPath, desktopMsiSentinel);
        var existingManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.81",
                fileName = "tradeplan-android-v0.2.81.apk",
                sha256 = Convert.ToHexString(SHA256.HashData(fixture.ApkBytes)),
                fileSize = fixture.ApkBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, existingManifest);
        File.WriteAllText(deliveryManifestPath, existingManifest);

        try
        {
            var result = await fixture.RunPublisherAsync(includeDesktop: true);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "Existing canonical Android package",
                result.StdOut + result.StdErr,
                StringComparison.OrdinalIgnoreCase);
            if (canonicalShape == "mismatched-file")
                Assert.Equal(
                    androidSentinel,
                    File.ReadAllBytes(canonicalAndroidPath));
            else
                Assert.True(Directory.Exists(canonicalAndroidPath));
            if (canonicalShape == "junction")
            {
                Assert.True(
                    (
                    File.GetAttributes(canonicalAndroidPath) &
                    FileAttributes.ReparsePoint) != (FileAttributes)0);
                Assert.Equal(
                    "preserve external target",
                    File.ReadAllText(externalMarker));
            }
            Assert.Equal(desktopZipSentinel, File.ReadAllBytes(desktopZipPath));
            Assert.Equal(desktopExeSentinel, File.ReadAllBytes(desktopExePath));
            Assert.Equal(desktopMsiSentinel, File.ReadAllBytes(desktopMsiPath));
            Assert.Equal(existingManifest, File.ReadAllText(manifestPath));
            Assert.Equal(existingManifest, File.ReadAllText(deliveryManifestPath));
        }
        finally
        {
            if (
                canonicalShape == "junction" &&
                Directory.Exists(canonicalAndroidPath))
            {
                Directory.Delete(canonicalAndroidPath, recursive: false);
            }
        }
    }

    [Fact]
    public async Task UpdateAssetPublisher_AllowsIdenticalCanonicalReaderWithoutRewrite()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var canonicalAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        var desktopDestinationRoot = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop");
        var desktopZipPath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-installer-v{fixture.DesktopVersion}.zip");
        var desktopExePath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-setup-v{fixture.DesktopVersion}.exe");
        var desktopMsiPath = Path.Combine(
            desktopDestinationRoot,
            $"tradeplan-pc-admin-v{fixture.DesktopVersion}.msi");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        var deliveryManifestPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalAndroidPath)!);
        Directory.CreateDirectory(desktopDestinationRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryManifestPath)!);

        File.WriteAllBytes(canonicalAndroidPath, fixture.ApkBytes);
        var canonicalWriteTime = File.GetLastWriteTimeUtc(canonicalAndroidPath);
        File.Copy(fixture.DesktopPackagePath, desktopZipPath);
        File.Copy(fixture.DesktopExeInstallerPath, desktopExePath);
        File.Copy(fixture.DesktopMsiInstallerPath, desktopMsiPath);
        var existingManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.81",
                fileName = "tradeplan-android-v0.2.81.apk",
                sha256 = Convert.ToHexString(SHA256.HashData(fixture.ApkBytes)),
                fileSize = fixture.ApkBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, existingManifest);
        File.WriteAllText(deliveryManifestPath, existingManifest);

        var publishTask = fixture.RunPublisherAsync(
            includeDesktop: true,
            analyzerDelayMilliseconds: 500);
        var deadline = DateTime.UtcNow.AddSeconds(25);
        string? snapshotPath = null;
        while (snapshotPath is null && DateTime.UtcNow < deadline)
        {
            snapshotPath = Directory
                .EnumerateDirectories(
                    fixture.SnapshotTempRoot,
                    "georaeplan-android-apk-*")
                .Select(path => Path.Combine(path, "candidate.apk"))
                .FirstOrDefault(File.Exists);
            if (snapshotPath is null)
                await Task.Delay(10);
        }
        Assert.NotNull(snapshotPath);

        ProcessResult result;
        using (File.Open(
                   canonicalAndroidPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            result = await publishTask;
        }

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Equal(fixture.ApkBytes, File.ReadAllBytes(canonicalAndroidPath));
        Assert.Equal(
            canonicalWriteTime,
            File.GetLastWriteTimeUtc(canonicalAndroidPath));
        Assert.Equal(
            File.ReadAllBytes(fixture.DesktopPackagePath),
            File.ReadAllBytes(desktopZipPath));
        Assert.Equal(
            File.ReadAllBytes(fixture.DesktopExeInstallerPath),
            File.ReadAllBytes(desktopExePath));
        Assert.Equal(
            File.ReadAllBytes(fixture.DesktopMsiInstallerPath),
            File.ReadAllBytes(desktopMsiPath));
        Assert.NotEqual(existingManifest, File.ReadAllText(manifestPath));
        Assert.Equal(
            File.ReadAllText(manifestPath),
            File.ReadAllText(deliveryManifestPath));
    }

    [Fact]
    public async Task UpdateAssetPublisher_HoldsCanonicalLeaseThroughDesktopAndManifestCommits()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var canonicalAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        var desktopZipPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            $"tradeplan-pc-installer-v{fixture.DesktopVersion}.zip");
        using (var stream = File.Open(
                   fixture.DesktopExeInstallerPath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(64L * 1024 * 1024);
        }

        var publishTask = fixture.RunPublisherAsync(includeDesktop: true);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (
            !File.Exists(desktopZipPath) &&
            !publishTask.IsCompleted &&
            DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }

        if (!File.Exists(desktopZipPath))
        {
            var prematureResult = await publishTask;
            Assert.Fail(
                "Publisher did not reach the desktop commit while the Android " +
                "lease was held." + Environment.NewLine +
                prematureResult.StdOut + Environment.NewLine +
                prematureResult.StdErr);
        }
        Assert.False(
            publishTask.IsCompleted,
            "Publisher completed before the canonical lease could be probed.");
        Assert.Throws<IOException>(() =>
        {
            using var _ = File.Open(
                canonicalAndroidPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.Throws<IOException>(() => File.Delete(canonicalAndroidPath));

        var result = await publishTask;

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Equal(fixture.ApkBytes, File.ReadAllBytes(canonicalAndroidPath));
        Assert.True(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RollsBackPackagesWhenManifestPairCommitFails()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        var deliveryManifestPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        var previousManifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.previous.json");
        var oldAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.80.apk");
        var oldDesktopPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            "tradeplan-pc-installer-v0.2.80.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryManifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldAndroidPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oldDesktopPath)!);
        var oldAndroidBytes = Encoding.UTF8.GetBytes("old referenced android");
        var oldDesktopBytes = Encoding.UTF8.GetBytes("old referenced desktop");
        File.WriteAllBytes(oldAndroidPath, oldAndroidBytes);
        File.WriteAllBytes(oldDesktopPath, oldDesktopBytes);
        var oldManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            desktop = new
            {
                version = "0.2.80",
                fileName = Path.GetFileName(oldDesktopPath),
                sha256 = Convert.ToHexString(SHA256.HashData(oldDesktopBytes)),
                fileSize = oldDesktopBytes.LongLength,
            },
            android = new
            {
                version = "0.2.80",
                fileName = Path.GetFileName(oldAndroidPath),
                sha256 = Convert.ToHexString(SHA256.HashData(oldAndroidBytes)),
                fileSize = oldAndroidBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, oldManifest);
        File.WriteAllText(deliveryManifestPath, oldManifest);

        ProcessResult result;
        using (File.Open(
                   deliveryManifestPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            result = await fixture.RunPublisherAsync(includeDesktop: true);
        }

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(oldManifest, File.ReadAllText(manifestPath));
        Assert.Equal(oldManifest, File.ReadAllText(deliveryManifestPath));
        Assert.Equal(oldAndroidBytes, File.ReadAllBytes(oldAndroidPath));
        Assert.Equal(oldDesktopBytes, File.ReadAllBytes(oldDesktopPath));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk")));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            $"tradeplan-pc-installer-v{fixture.DesktopVersion}.zip")));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            $"tradeplan-pc-setup-v{fixture.DesktopVersion}.exe")));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            $"tradeplan-pc-admin-v{fixture.DesktopVersion}.msi")));
        Assert.False(File.Exists(previousManifestPath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RecoversOwnedInterruptedTransactionAndPreservesDecoys()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        var deliveryManifestPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        var interruptedAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.82.apk");
        var referencedAndroidPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.80.apk");
        var decoyPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "preserve-decoy.apk");
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryManifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(interruptedAndroidPath)!);

        var referencedBytes = Encoding.UTF8.GetBytes("old referenced package");
        var interruptedBytes = Encoding.UTF8.GetBytes("owned interrupted package");
        var decoyBytes = Encoding.UTF8.GetBytes("unowned decoy package");
        File.WriteAllBytes(referencedAndroidPath, referencedBytes);
        File.WriteAllBytes(interruptedAndroidPath, interruptedBytes);
        File.WriteAllBytes(decoyPath, decoyBytes);
        var oldManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.80",
                fileName = Path.GetFileName(referencedAndroidPath),
                sha256 = Convert.ToHexString(SHA256.HashData(referencedBytes)),
                fileSize = referencedBytes.LongLength,
            },
        });
        var interruptedManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.82",
                fileName = Path.GetFileName(interruptedAndroidPath),
                sha256 = Convert.ToHexString(SHA256.HashData(interruptedBytes)),
                fileSize = interruptedBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, interruptedManifest);
        File.WriteAllText(deliveryManifestPath, oldManifest);
        var interruptedStagedPath = Path.Combine(
            stagingRoot,
            $"000-{Path.GetFileName(interruptedAndroidPath)}.stage");
        var mainStagedPath = Path.Combine(stagingRoot, "001-stable.json.stage");
        var deliveryStagedPath = Path.Combine(
            stagingRoot,
            "002-stable.json.stage");
        var mainBackupPath = Path.Combine(backupRoot, "001-stable.json.bak");
        var deliveryBackupPath = Path.Combine(
            backupRoot,
            "002-stable.json.bak");
        File.WriteAllBytes(interruptedStagedPath, interruptedBytes);
        File.WriteAllText(mainStagedPath, interruptedManifest);
        File.WriteAllText(deliveryStagedPath, interruptedManifest);
        File.WriteAllText(mainBackupPath, oldManifest);
        File.WriteAllText(deliveryBackupPath, oldManifest);
        var interruptedManifestBytes = File.ReadAllBytes(manifestPath);
        var oldManifestBytes = File.ReadAllBytes(mainBackupPath);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "CommitPending",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new object[]
                {
                    new
                    {
                        targetPath = interruptedAndroidPath,
                        stagedPath = interruptedStagedPath,
                        backupPath = (string?)null,
                        targetExisted = false,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(interruptedBytes)),
                        backupSha256 = (string?)null,
                        stagedFileSize = interruptedBytes.LongLength,
                        backupFileSize = (long?)null,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(
                                interruptedAndroidPath),
                    },
                    new
                    {
                        targetPath = manifestPath,
                        stagedPath = mainStagedPath,
                        backupPath = mainBackupPath,
                        targetExisted = true,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(interruptedManifestBytes)),
                        backupSha256 = Convert.ToHexString(
                            SHA256.HashData(oldManifestBytes)),
                        stagedFileSize = interruptedManifestBytes.LongLength,
                        backupFileSize = oldManifestBytes.LongLength,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(manifestPath),
                    },
                    new
                    {
                        targetPath = deliveryManifestPath,
                        stagedPath = deliveryStagedPath,
                        backupPath = deliveryBackupPath,
                        targetExisted = true,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(interruptedManifestBytes)),
                        backupSha256 = Convert.ToHexString(
                            SHA256.HashData(oldManifestBytes)),
                        stagedFileSize = interruptedManifestBytes.LongLength,
                        backupFileSize = oldManifestBytes.LongLength,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(
                                deliveryManifestPath),
                    },
                },
            }));

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.False(File.Exists(interruptedAndroidPath));
        Assert.Equal(referencedBytes, File.ReadAllBytes(referencedAndroidPath));
        Assert.Equal(decoyBytes, File.ReadAllBytes(decoyPath));
        Assert.False(Directory.Exists(transactionRoot));
        Assert.Equal(
            File.ReadAllText(manifestPath),
            File.ReadAllText(deliveryManifestPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAssetPublisher_PreservesTransactionEvidenceWithoutValidJournal(
        bool writeMalformedJournal)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var evidencePath = Path.Combine(transactionRoot, "preserve-evidence.bin");
        Directory.CreateDirectory(transactionRoot);
        File.WriteAllText(evidencePath, "preserve invalid transaction evidence");
        if (writeMalformedJournal)
        {
            File.WriteAllText(
                Path.Combine(transactionRoot, "journal.json"),
                """{"schemaVersion":1,"owner":"wrong-owner","entries":[]}""");
        }

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(transactionRoot));
        Assert.Equal(
            "preserve invalid transaction evidence",
            File.ReadAllText(evidencePath));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDuplicateKnownJournalProperty()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var evidencePath = Path.Combine(transactionRoot, "preserve-duplicate.bin");
        Directory.CreateDirectory(transactionRoot);
        File.WriteAllText(evidencePath, "preserve duplicate journal evidence");
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            $$"""
            {
              "schemaVersion": 1,
              "owner": "georaeplan-release-transaction",
              "owner": "georaeplan-release-transaction",
              "channel": "stable",
              "phase": "Staging",
              "projectRoot": {{JsonSerializer.Serialize(fixture.ProjectRoot)}},
              "outputRoot": {{JsonSerializer.Serialize(fixture.OutputRoot)}},
              "transactionRoot": {{JsonSerializer.Serialize(transactionRoot)}},
              "entries": []
            }
            """);

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(transactionRoot));
        Assert.Equal(
            "preserve duplicate journal evidence",
            File.ReadAllText(evidencePath));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("case")]
    [InlineData("wrong-type")]
    [InlineData("trailing")]
    [InlineData("entry-duplicate")]
    public async Task UpdateAssetPublisher_RejectsNonExactJournalJson(
        string mutation)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var evidencePath = Path.Combine(
            transactionRoot,
            $"preserve-{mutation}.bin");
        Directory.CreateDirectory(transactionRoot);
        File.WriteAllText(evidencePath, $"preserve {mutation} evidence");
        var journal =
            $$"""{"schemaVersion":3,"owner":"georaeplan-release-transaction","channel":"stable","phase":"Staging","projectRoot":{{JsonSerializer.Serialize(fixture.ProjectRoot)}},"outputRoot":{{JsonSerializer.Serialize(fixture.OutputRoot)}},"transactionRoot":{{JsonSerializer.Serialize(transactionRoot)}},"entries":[]}""";
        journal = mutation switch
        {
            "missing" => journal.Replace(
                "\"channel\":\"stable\",",
                string.Empty,
                StringComparison.Ordinal),
            "unknown" => journal.Replace(
                "\"entries\":[]",
                "\"unknown\":null,\"entries\":[]",
                StringComparison.Ordinal),
            "case" => journal.Replace(
                "\"owner\":",
                "\"Owner\":",
                StringComparison.Ordinal),
            "wrong-type" => journal.Replace(
                "\"schemaVersion\":3",
                "\"schemaVersion\":\"1\"",
                StringComparison.Ordinal),
            "trailing" => journal + " true",
            "entry-duplicate" => journal.Replace(
                "\"entries\":[]",
                "\"entries\":[{\"targetPath\":\"unused\",\"targetPath\":\"unused\",\"stagedPath\":\"unused\",\"backupPath\":null,\"targetExisted\":false,\"stagedSha256\":\"unused\",\"backupSha256\":null,\"stagedFileSize\":1,\"backupFileSize\":null}]",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            journal);

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(transactionRoot));
        Assert.Equal(
            $"preserve {mutation} evidence",
            File.ReadAllText(evidencePath));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_ValidatesEveryRecoveryEntryBeforeMutation()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var firstTarget = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "owned-first.apk");
        var secondTarget = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "unowned-second.apk");
        var firstStaged = Path.Combine(
            stagingRoot,
            "000-owned-first.apk.stage");
        var secondStaged = Path.Combine(
            stagingRoot,
            "001-unowned-second.apk.stage");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(firstTarget)!);
        var firstBytes = Encoding.UTF8.GetBytes("owned first bytes");
        var expectedSecondBytes = Encoding.UTF8.GetBytes("expected second bytes");
        var decoySecondBytes = Encoding.UTF8.GetBytes("unowned second bytes");
        File.WriteAllBytes(firstStaged, firstBytes);
        File.WriteAllBytes(secondStaged, expectedSecondBytes);
        File.WriteAllBytes(firstTarget, firstBytes);
        File.WriteAllBytes(secondTarget, decoySecondBytes);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "CommitPending",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new object[]
                {
                    new
                    {
                        targetPath = firstTarget,
                        stagedPath = firstStaged,
                        backupPath = (string?)null,
                        targetExisted = false,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(firstBytes)),
                        backupSha256 = (string?)null,
                        stagedFileSize = firstBytes.LongLength,
                        backupFileSize = (long?)null,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(firstTarget),
                    },
                    new
                    {
                        targetPath = secondTarget,
                        stagedPath = secondStaged,
                        backupPath = (string?)null,
                        targetExisted = false,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(expectedSecondBytes)),
                        backupSha256 = (string?)null,
                        stagedFileSize = expectedSecondBytes.LongLength,
                        backupFileSize = (long?)null,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(secondTarget),
                    },
                },
            }));

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(firstBytes, File.ReadAllBytes(firstTarget));
        Assert.Equal(decoySecondBytes, File.ReadAllBytes(secondTarget));
        Assert.True(Directory.Exists(transactionRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsManifestHashConflictWhenCanonicalIsMissing()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var existingManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.81",
                fileName = "tradeplan-android-v0.2.81.apk",
                sha256 = new string('A', 64),
                fileSize = fixture.ApkBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, existingManifest);
        var canonicalPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        Assert.False(File.Exists(canonicalPath));

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "existing manifest",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingManifest, File.ReadAllText(manifestPath));
        Assert.False(File.Exists(canonicalPath));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDeliveryManifestConflictBeforeOutputMutation()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var deliveryManifestPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryManifestPath)!);
        var existingManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            android = new
            {
                version = "0.2.81",
                fileName = "tradeplan-android-v0.2.81.apk",
                sha256 = new string('B', 64),
                fileSize = fixture.ApkBytes.LongLength,
            },
        });
        File.WriteAllText(deliveryManifestPath, existingManifest);

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "existing manifest",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existingManifest, File.ReadAllText(deliveryManifestPath));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk")));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_PublishesAuthenticatedSnapshotWhenSourceChanges()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var originalBytes = fixture.ApkBytes.ToArray();

        var existingSnapshots = Directory
            .EnumerateDirectories(
                fixture.SnapshotTempRoot,
                "georaeplan-android-apk-*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var publishTask = fixture.RunPublisherAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string? snapshotPath = null;
        while (snapshotPath is null && DateTime.UtcNow < deadline)
        {
            snapshotPath = Directory
                .EnumerateDirectories(
                    fixture.SnapshotTempRoot,
                    "georaeplan-android-apk-*")
                .Where(path => !existingSnapshots.Contains(path))
                .Select(path => Path.Combine(path, "candidate.apk"))
                .FirstOrDefault(path =>
                    File.Exists(path) &&
                    new FileInfo(path).Length == originalBytes.Length);
            if (snapshotPath is null)
                await Task.Delay(10);
        }
        Assert.NotNull(snapshotPath);
        var sourceChanged = false;
        while (!sourceChanged && DateTime.UtcNow < deadline)
        {
            try
            {
                File.WriteAllText(
                    fixture.ApkPath,
                    "source changed after snapshot");
                sourceChanged = true;
            }
            catch (IOException)
            {
                await Task.Delay(10);
            }
        }
        Assert.True(sourceChanged);
        var result = await publishTask;

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.NotEqual(originalBytes, File.ReadAllBytes(fixture.ApkPath));
        Assert.Equal(
            originalBytes,
            File.ReadAllBytes(Path.Combine(
                fixture.OutputRoot,
                "downloads",
                "android",
                "tradeplan-android-v0.2.81.apk")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RequiresExplicitSkipForDesktopOnlyManifest()
    {
        using var fixture = new PublisherFixture("0.2.81");

        var skipped = await fixture.RunPublisherAsync(
            skipAndroid: true,
            includeAndroidPath: false);
        Assert.True(
            skipped.ExitCode == 0,
            skipped.StdOut + Environment.NewLine + skipped.StdErr);
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                   fixture.OutputRoot,
                   "manifest",
                   "stable.json"))))
        {
            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("android").ValueKind);
        }

        var conflict = await fixture.RunPublisherAsync(
            skipAndroid: true,
            includeAndroidPath: true);
        Assert.NotEqual(0, conflict.ExitCode);
        Assert.Contains(
            "cannot be combined",
            conflict.StdOut + conflict.StdErr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAssetPublisher_ReplacesSameBytesRepeatedlyAndPreservesOnFailure()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var first = await fixture.RunPublisherAsync();
        Assert.True(first.ExitCode == 0, first.StdOut + first.StdErr);
        var canonicalPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "tradeplan-android-v0.2.81.apk");
        var before = File.ReadAllBytes(canonicalPath);

        var second = await fixture.RunPublisherAsync();
        Assert.True(second.ExitCode == 0, second.StdOut + second.StdErr);
        Assert.Equal(before, File.ReadAllBytes(canonicalPath));

        using (File.Open(
                   canonicalPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var failed = await fixture.RunPublisherAsync();
            Assert.NotEqual(0, failed.ExitCode);
        }
        Assert.Equal(before, File.ReadAllBytes(canonicalPath));
    }

    [Fact]
    public async Task GeneratedTestManifest_UsesValidatedSidecarAndRejectsTamperedApk()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"android-test-manifest-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var mobileRoot = Path.Combine(runtimeRoot, "Mobile");
        var generationScript = Path.Combine(testRoot, "generate-launchers.ps1");
        var manifestHarness = Path.Combine(testRoot, "manifest-harness.ps1");
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var sourceScript = Path.Combine(
            repositoryRoot,
            "테스트 시행",
            "테스트-환경-준비.ps1");
        Directory.CreateDirectory(mobileRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(
                generationScript,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$PowerShellPath
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'New-Utf8BomEncoding',
                    'Write-Utf8File',
                    'Set-RuntimeInvalidationMarker',
                    'Write-TestRunScripts'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                Write-TestRunScripts `
                    -OutputRoot $OutputRoot `
                    -DefaultBaseUrl 'http://127.0.0.1:19081' `
                    -DotnetExe $PowerShellPath `
                    -CertificationId 'android-test-manifest' `
                    -CertificationMode 'test' `
                    -PasswordResetCount 0
                """,
                utf8NoBom);
            File.WriteAllText(
                manifestHarness,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
                    [Parameter(Mandatory = $true)][string]$ServerDataRoot
                )

                $ErrorActionPreference = 'Stop'
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }
                foreach ($functionName in @(
                    'Test-DesktopUpdatePackageContract',
                    'Publish-TestFileAtomically',
                    'Initialize-TestUpdateManifest'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                function Write-Log { param([string]$Message) }
                Initialize-TestUpdateManifest `
                    -ServerDataRoot $ServerDataRoot `
                    -RuntimeRoot $RuntimeRoot
                """,
                utf8NoBom);

            var generation = await RunPowerShellAsync(
                powerShellPath,
                generationScript,
                "-SourceScript",
                sourceScript,
                "-OutputRoot",
                runtimeRoot,
                "-PowerShellPath",
                powerShellPath);
            Assert.True(
                generation.ExitCode == 0,
                generation.StdOut + Environment.NewLine + generation.StdErr);

            const string apkName = "tradeplan-android-test-v9.8.7.apk";
            var apkPath = Path.Combine(mobileRoot, apkName);
            var apkBytes = Encoding.UTF8.GetBytes(
                "validated runtime Android package bytes");
            File.WriteAllBytes(apkPath, apkBytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(apkBytes));
            File.WriteAllText(
                Path.Combine(mobileRoot, "android-package.metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    fileName = apkName,
                    applicationId = "kr.georaeplan.mobile",
                    versionName = "9.8.7",
                    versionCode = 987,
                    sha256 = expectedHash,
                    fileSize = apkBytes.LongLength,
                }),
                utf8NoBom);

            var validServerData = Path.Combine(testRoot, "valid-server-data");
            var valid = await RunPowerShellAsync(
                powerShellPath,
                manifestHarness,
                "-SourceScript",
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                "-RuntimeRoot",
                runtimeRoot,
                "-ServerDataRoot",
                validServerData);
            Assert.True(
                valid.ExitCode == 0,
                valid.StdOut + Environment.NewLine + valid.StdErr);
            using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                       validServerData,
                       "updates",
                       "manifest",
                       "test.json"))))
            {
                var android = document.RootElement.GetProperty("android");
                Assert.Equal("9.8.7", android.GetProperty("version").GetString());
                Assert.Equal(
                    "9.8.7",
                    android.GetProperty("minimumSupportedVersion").GetString());
                Assert.Equal(apkName, android.GetProperty("fileName").GetString());
                Assert.Equal(
                    expectedHash,
                    android.GetProperty("sha256").GetString(),
                    ignoreCase: true);
                Assert.Equal(
                    apkBytes.LongLength,
                    android.GetProperty("fileSize").GetInt64());
            }

            File.AppendAllText(apkPath, "tampered", utf8NoBom);
            var tamperedServerData = Path.Combine(testRoot, "tampered-server-data");
            var tampered = await RunPowerShellAsync(
                powerShellPath,
                manifestHarness,
                "-SourceScript",
                Path.Combine(runtimeRoot, "Run-All.ps1"),
                "-RuntimeRoot",
                runtimeRoot,
                "-ServerDataRoot",
                tamperedServerData);
            Assert.NotEqual(0, tampered.ExitCode);
            Assert.Contains(
                "does not match its validated metadata sidecar",
                tampered.StdOut + tampered.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(
                tamperedServerData,
                "updates",
                "manifest",
                "test.json")));
        }
        finally
        {
            DeleteDirectoryWithRetries(testRoot);
        }
    }

    [Fact]
    public async Task TestEnvironmentPreparation_WritesSidecarFromInspectedApk()
    {
        using var fixture = new PublisherFixture("0.2.81");

        var result = await fixture.RunPreparationHelperAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        var sidecarPath = Path.Combine(
            fixture.RuntimeRoot,
            "Mobile",
            "android-package.metadata.json");
        Assert.True(File.Exists(sidecarPath));
        using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        var sidecar = document.RootElement;
        Assert.Equal(
            "tradeplan-android-test-v0.2.81.apk",
            sidecar.GetProperty("fileName").GetString());
        Assert.Equal(
            "kr.georaeplan.mobile",
            sidecar.GetProperty("applicationId").GetString());
        Assert.Equal("0.2.81", sidecar.GetProperty("versionName").GetString());
        Assert.Equal(192, sidecar.GetProperty("versionCode").GetInt64());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fixture.ApkBytes)),
            sidecar.GetProperty("sha256").GetString(),
            ignoreCase: true);
        Assert.Equal(
            fixture.ApkBytes,
            File.ReadAllBytes(Path.Combine(
                fixture.RuntimeRoot,
                "Mobile",
                "tradeplan-android-test-v0.2.81.apk")));
    }

    [Fact]
    public async Task TestEnvironmentPreparation_RejectsMismatchedApkWithoutSidecar()
    {
        using var fixture = new PublisherFixture("0.2.80");

        var result = await fixture.RunPreparationHelperAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "versionName mismatch",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-android-apk-*"));
        Assert.False(File.Exists(Path.Combine(
            fixture.RuntimeRoot,
            "Mobile",
            "android-package.metadata.json")));
    }

    [Fact]
    public async Task TestEnvironmentPreparation_ReplacesRepeatedlyAndPreservesOnFailure()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var first = await fixture.RunPreparationHelperAsync();
        Assert.True(first.ExitCode == 0, first.StdOut + first.StdErr);
        var runtimeApk = Path.Combine(
            fixture.RuntimeRoot,
            "Mobile",
            "tradeplan-android-test-v0.2.81.apk");
        var sidecar = Path.Combine(
            fixture.RuntimeRoot,
            "Mobile",
            "android-package.metadata.json");
        var apkBefore = File.ReadAllBytes(runtimeApk);
        var sidecarBefore = File.ReadAllBytes(sidecar);

        var second = await fixture.RunPreparationHelperAsync();
        Assert.True(second.ExitCode == 0, second.StdOut + second.StdErr);
        Assert.Equal(apkBefore, File.ReadAllBytes(runtimeApk));

        using (File.Open(
                   runtimeApk,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            var failed = await fixture.RunPreparationHelperAsync();
            Assert.NotEqual(0, failed.ExitCode);
        }
        Assert.Equal(apkBefore, File.ReadAllBytes(runtimeApk));
        Assert.Equal(sidecarBefore, File.ReadAllBytes(sidecar));
    }

    [Fact]
    public async Task TestEnvironmentPreparation_ConfirmsAbsentAndroidBeforeMutation()
    {
        using var fixture = new PublisherFixture("0.2.81");
        Directory.CreateDirectory(fixture.RuntimeRoot);
        var mutationSentinel = Path.Combine(
            fixture.RuntimeRoot,
            "preflight-mutation-sentinel.txt");
        File.WriteAllText(mutationSentinel, "preserve-before-runtime-mutation");
        var sentinelBytes = File.ReadAllBytes(mutationSentinel);
        var sentinelWriteTime = File.GetLastWriteTimeUtc(mutationSentinel);

        var result = await fixture.RunPreparationAbsenceInspectionHelperAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Contains("android_absence=PASS", result.StdOut);
        Assert.Equal(sentinelBytes, File.ReadAllBytes(mutationSentinel));
        Assert.Equal(sentinelWriteTime, File.GetLastWriteTimeUtc(mutationSentinel));
        Assert.False(File.Exists(Path.Combine(
            fixture.RuntimeRoot,
            "Mobile",
            "android-package.metadata.json")));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-android-apk-*"));
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("manifest")]
    [InlineData("delivery")]
    public async Task UpdateAssetPublisher_RejectsReparsePointInEveryPublishParentChain(
        string parentKind)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var externalRoot = Path.Combine(
            fixture.Root,
            $"external-{parentKind}");
        Directory.CreateDirectory(externalRoot);
        var externalSentinel = Path.Combine(externalRoot, "preserve.txt");
        File.WriteAllText(externalSentinel, "preserve external parent");

        string junctionPath;
        if (parentKind == "desktop")
        {
            Directory.CreateDirectory(Path.Combine(
                fixture.OutputRoot,
                "downloads"));
            junctionPath = Path.Combine(
                fixture.OutputRoot,
                "downloads",
                "desktop");
        }
        else if (parentKind == "manifest")
        {
            Directory.CreateDirectory(fixture.OutputRoot);
            junctionPath = Path.Combine(fixture.OutputRoot, "manifest");
        }
        else
        {
            junctionPath = Path.Combine(
                fixture.ProjectRoot,
                "\uBC30\uD3EC");
        }
        CreateJunction(junctionPath, externalRoot);
        try
        {
            var result = await fixture.RunPublisherAsync(includeDesktop: true);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "reparse point",
                result.StdOut + result.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "preserve external parent",
                File.ReadAllText(externalSentinel));
            Assert.True(
                (File.GetAttributes(junctionPath) &
                 FileAttributes.ReparsePoint) != 0);
        }
        finally
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath, recursive: false);
        }
    }

    [Fact]
    public async Task UpdateAssetPublisher_HoldsEveryPublishParentIdentityAgainstSwap()
    {
        using var fixture = new PublisherFixture("0.2.81");
        using (var stream = File.Open(
                   fixture.ApkPath,
                   FileMode.Open,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(16L * 1024 * 1024);
        }

        var publishTask = fixture.RunPublisherAsync();
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (
            !Directory.Exists(transactionRoot) &&
            !publishTask.IsCompleted &&
            DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        Assert.False(
            publishTask.IsCompleted,
            "Publisher completed before parent identity leases could be probed.");
        foreach (var parentPath in new[]
        {
            Path.Combine(fixture.OutputRoot, "downloads", "desktop"),
            Path.Combine(fixture.OutputRoot, "manifest"),
            Path.Combine(fixture.ProjectRoot, "\uBC30\uD3EC"),
        })
        {
            var swapError = Record.Exception(() => Directory.Move(
                parentPath,
                parentPath + ".swapped"));
            Assert.True(
                swapError is IOException or UnauthorizedAccessException,
                $"Publish parent swap was not denied: {parentPath}. " +
                swapError?.Message);
            Assert.True(Directory.Exists(parentPath));
            Assert.False(Directory.Exists(parentPath + ".swapped"));
        }

        var result = await publishTask;

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
    }

    [Fact]
    public async Task UpdateAssetPublisher_HoldsAllDesktopSourceLeasesUntilTerminalCleanup()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var eventPrefix =
            $"GeoraePlanReleaseTest_{Guid.NewGuid():N}";
        using var readyEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Ready");
        using var continueEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Continue");
        var publishTask = fixture.RunPublisherAsync(
            includeDesktop: true,
            pausePoint: "BeforeTerminalSnapshotCleanup",
            pauseReadyEventName: eventPrefix + "_Ready",
            pauseContinueEventName: eventPrefix + "_Continue");
        var readyTask = Task.Run(
            () => readyEvent.WaitOne(TimeSpan.FromSeconds(25)));
        Exception? probeError = null;
        try
        {
            var firstCompletion =
                await Task.WhenAny(readyTask, publishTask);
            if (firstCompletion == publishTask)
            {
                var prematureResult = await publishTask;
                Assert.Fail(
                    "Publisher exited before the terminal snapshot cleanup " +
                    "pause." + Environment.NewLine +
                    prematureResult.StdOut + Environment.NewLine +
                    prematureResult.StdErr);
            }
            Assert.True(
                await readyTask,
                "Publisher did not reach the terminal snapshot cleanup pause.");
            Assert.Equal(
                3,
                Directory.EnumerateDirectories(
                    fixture.SnapshotTempRoot,
                    "georaeplan-release-file-*").Count());
            foreach (var sourcePath in new[]
            {
                fixture.DesktopPackagePath,
                fixture.DesktopExeInstallerPath,
                fixture.DesktopMsiInstallerPath,
            })
            {
                Assert.Throws<IOException>(() =>
                {
                    using var _ = File.Open(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete);
                });
                Assert.Throws<IOException>(() => File.Delete(sourcePath));
            }
        }
        catch (Exception ex)
        {
            probeError = ex;
        }
        finally
        {
            continueEvent.Set();
        }

        var result = await publishTask;

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.True(probeError is null, probeError?.ToString());
        foreach (var sourcePath in new[]
        {
            fixture.DesktopPackagePath,
            fixture.DesktopExeInstallerPath,
            fixture.DesktopMsiInstallerPath,
        })
        {
            using var _ = File.Open(
                sourcePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-release-file-*"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsReplacedDesktopStagingBeforeTransactionBinding()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var baseline = await fixture.RunPublisherAsync(includeDesktop: false);
        Assert.True(
            baseline.ExitCode == 0,
            baseline.StdOut + Environment.NewLine + baseline.StdErr);
        var before = CaptureTreeHashes(fixture.OutputRoot);
        var eventPrefix =
            $"GeoraePlanReleaseTest_{Guid.NewGuid():N}";
        using var readyEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Ready");
        using var continueEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Continue");
        var publishTask = fixture.RunPublisherAsync(
            includeDesktop: true,
            pausePoint: "BeforeReleaseTransactionStageBinding",
            pauseReadyEventName: eventPrefix + "_Ready",
            pauseContinueEventName: eventPrefix + "_Continue");
        var readyTask = Task.Run(
            () => readyEvent.WaitOne(TimeSpan.FromSeconds(25)));
        Exception? probeError = null;
        try
        {
            var firstCompletion = await Task.WhenAny(readyTask, publishTask);
            if (firstCompletion == publishTask)
            {
                var prematureResult = await publishTask;
                Assert.Fail(
                    "Publisher exited before transaction stage binding pause." +
                    Environment.NewLine + prematureResult.StdOut +
                    Environment.NewLine + prematureResult.StdErr);
            }
            Assert.True(
                await readyTask,
                "Publisher did not reach transaction stage binding pause.");
            var stagedZip = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(
                    fixture.OutputRoot,
                    ".georaeplan-release-transaction-stable",
                    "staging",
                    "desktop"),
                "*.zip",
                SearchOption.TopDirectoryOnly));
            var replacementPath = stagedZip + ".replacement";
            File.WriteAllText(replacementPath, "unowned replacement bytes");
            File.Move(replacementPath, stagedZip, overwrite: true);
        }
        catch (Exception ex)
        {
            probeError = ex;
        }
        finally
        {
            continueEvent.Set();
        }

        var result = await publishTask;

        if (result.ExitCode == 0)
        {
            Assert.IsType<IOException>(probeError);
        }
        else
        {
            Assert.Contains(
                "immutable snapshot identity",
                result.StdErr + result.StdOut,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(probeError is null, probeError?.ToString());
        }
        Assert.Equal(
            before.ToArray(),
            CaptureTreeHashes(fixture.OutputRoot).ToArray());
    }

    [Fact]
    public async Task UpdateAssetPublisher_HoldsExtractedDesktopExecutablesThroughIdentityInspection()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var eventPrefix =
            $"GeoraePlanReleaseTest_{Guid.NewGuid():N}";
        using var readyEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Ready");
        using var continueEvent = new EventWaitHandle(
            false,
            EventResetMode.ManualReset,
            eventPrefix + "_Continue");
        var publishTask = fixture.RunPublisherAsync(
            includeDesktop: true,
            pausePoint: "DuringDesktopArchiveExecutableInspection",
            pauseReadyEventName: eventPrefix + "_Ready",
            pauseContinueEventName: eventPrefix + "_Continue");
        var readyTask = Task.Run(
            () => readyEvent.WaitOne(TimeSpan.FromSeconds(25)));
        Exception? probeError = null;
        try
        {
            var firstCompletion = await Task.WhenAny(readyTask, publishTask);
            if (firstCompletion == publishTask)
            {
                var prematureResult = await publishTask;
                Assert.Fail(
                    "Publisher exited before archive executable inspection pause." +
                    Environment.NewLine + prematureResult.StdOut +
                    Environment.NewLine + prematureResult.StdErr);
            }
            Assert.True(
                await readyTask,
                "Publisher did not reach archive executable inspection pause.");
            var inspectionFiles = Directory.EnumerateFiles(
                    fixture.SnapshotTempRoot,
                    "*.exe",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(Path.GetDirectoryName(path))
                    ?.StartsWith(
                        "georaeplan-desktop-package-version-",
                        StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(2, inspectionFiles.Length);
            foreach (var inspectionPath in inspectionFiles)
            {
                Assert.Throws<IOException>(() => File.WriteAllText(
                    inspectionPath,
                    "replacement bytes"));
                Assert.Throws<IOException>(() => File.Move(
                    inspectionPath,
                    inspectionPath + ".replaced"));
            }
        }
        catch (Exception ex)
        {
            probeError = ex;
        }
        finally
        {
            continueEvent.Set();
        }

        var result = await publishTask;

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.True(probeError is null, probeError?.ToString());
    }

    [Fact]
    public async Task UpdateAssetPublisher_CleansSnapshotsWhenTerminalPauseHandlesAreMissing()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var eventPrefix =
            $"GeoraePlanReleaseTest_{Guid.NewGuid():N}";

        var result = await fixture.RunPublisherAsync(
            includeDesktop: true,
            pausePoint: "BeforeTerminalSnapshotCleanup",
            pauseReadyEventName: eventPrefix + "_Ready",
            pauseContinueEventName: eventPrefix + "_Continue");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-android-apk-*"));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-release-file-*"));
    }

    [Fact]
    public async Task RedirectedProcessRunner_KillsTimedOutProcessTreeAndDrainsPipes()
    {
        var root = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"release-process-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var harnessPath = Path.Combine(root, "timeout-harness.ps1");
        var pidPath = Path.Combine(root, "process-ids.txt");
        File.WriteAllText(
            harnessPath,
            """
            [CmdletBinding()]
            param([Parameter(Mandatory = $true)][string]$PidPath)
            $ErrorActionPreference = 'Stop'
            $child = Start-Process `
                -FilePath (Join-Path $PSHOME 'powershell.exe') `
                -ArgumentList @(
                    '-NoProfile',
                    '-NonInteractive',
                    '-Command',
                    'Start-Sleep -Seconds 30'
                ) `
                -WindowStyle Hidden `
                -PassThru
            $parent = Get-Process -Id $PID
            $child.Refresh()
            $parentIdentity = @(
                [string]$parent.Id,
                [string]$parent.StartTime.ToUniversalTime().Ticks,
                [string]$parent.Path
            ) -join '|'
            $childIdentity = @(
                [string]$child.Id,
                [string]$child.StartTime.ToUniversalTime().Ticks,
                [string]$child.Path
            ) -join '|'
            [IO.File]::WriteAllLines(
                $PidPath,
                @($parentIdentity, $childIdentity))
            $child.WaitForExit()
            """,
            new UTF8Encoding(false));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            harnessPath,
            "-PidPath",
            pidPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            TimeoutException? timeoutError = null;
            ProcessResult? prematureResult = null;
            try
            {
                prematureResult = await RunRedirectedProcessAsync(
                    startInfo,
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(10),
                    "release process timeout regression");
            }
            catch (TimeoutException error)
            {
                timeoutError = error;
            }
            Assert.True(
                timeoutError is not null,
                "Timeout harness exited before the timeout." +
                Environment.NewLine +
                $"exit_code={prematureResult?.ExitCode}" +
                Environment.NewLine +
                prematureResult?.StdOut +
                Environment.NewLine +
                prematureResult?.StdErr);
            Assert.Contains(
                "timed out",
                timeoutError.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(pidPath));
            foreach (var identity in File
                         .ReadAllLines(pidPath)
                         .Select(ParseOwnedProcessIdentity))
            {
                Assert.False(
                    IsOwnedProcessRunning(identity),
                    $"Timed-out process remained alive: {identity.ProcessId}");
            }
        }
        finally
        {
            if (File.Exists(pidPath))
            {
                foreach (var value in File.ReadAllLines(pidPath))
                {
                    try
                    {
                        var identity = ParseOwnedProcessIdentity(value);
                        if (TryOpenOwnedProcess(identity, out var process))
                        {
                            using (process)
                            {
                                process.Kill();
                                process.WaitForExit(
                                    (int)TimeSpan
                                        .FromSeconds(5)
                                        .TotalMilliseconds);
                            }
                        }
                    }
                    catch (Exception cleanupError)
                        when (cleanupError is ArgumentException or
                              InvalidOperationException or
                              System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("msi")]
    public async Task UpdateAssetPublisher_CleansSnapshotsWhenNativeInstallerResolutionFails(
        string format)
    {
        using var fixture = new PublisherFixture("0.2.81");
        File.Delete(
            string.Equals(format, "exe", StringComparison.Ordinal)
                ? fixture.DesktopExeInstallerPath
                : fixture.DesktopMsiInstallerPath);

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            format,
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputRoot));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-android-apk-*"));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-release-file-*"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDesktopPackageMissingUpdaterInstallContract()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var installScript = fixture.ReadDesktopArchiveTextEntry(
            "Install-GeoraePlan.ps1");
        fixture.ReplaceDesktopArchiveEntry(
            "Install-GeoraePlan.ps1",
            Encoding.UTF8.GetBytes(installScript.Replace(
                "    [string]$InstallRootGateOwnerProcessPath,\r\n",
                string.Empty,
                StringComparison.Ordinal)));

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "InstallRootGateOwnerProcessPath",
            result.StdOut + result.StdErr,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDesktopZipWhoseActualEntryLengthExceedsMetadata()
    {
        using var fixture = new PublisherFixture("0.2.81");
        fixture.AddDesktopArchiveEntryWithUnderreportedLength(
            "App/hidden-payload.bin",
            Enumerable.Repeat((byte)0x5A, 4096).ToArray(),
            declaredLength: 1);

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "length does not match ZIP metadata",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputRoot));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-release-file-*"));
        Assert.Empty(Directory.EnumerateDirectories(
            fixture.SnapshotTempRoot,
            "georaeplan-desktop-package-version-*"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDivergentCanonicalAndDisplayNameExecutables()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var aliasBytes = fixture.ReadDesktopArchiveEntry(
            "App/거래플랜.exe");
        aliasBytes[^1] ^= 0x01;
        fixture.ReplaceDesktopArchiveEntry(
            "App/거래플랜.exe",
            aliasBytes);

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "SHA-256",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsDifferentDesktopBytesForSameVersion()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var first = await fixture.RunPublisherAsync(includeDesktop: true);
        Assert.True(first.ExitCode == 0, first.StdOut + first.StdErr);
        var canonicalExe = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            $"tradeplan-pc-setup-v{fixture.DesktopVersion}.exe");
        var committedBytes = File.ReadAllBytes(canonicalExe);
        File.AppendAllText(
            fixture.DesktopExeInstallerPath,
            "different immutable EXE bytes for the same version");

        var second = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains(
            "already exists",
            second.StdOut + second.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(committedBytes, File.ReadAllBytes(canonicalExe));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("msi")]
    public async Task UpdateAssetPublisher_RejectsNativeInstallerProductVersionMismatch(
        string format)
    {
        using var fixture = new PublisherFixture("0.2.81");
        fixture.ReplaceNativeInstallerWithVersion(format, "9.9.9");

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "ProductVersion",
            result.StdOut + result.StdErr,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsNativeExeFileVersionMismatch()
    {
        using var fixture = new PublisherFixture("0.2.81");
        fixture.ReplaceNativeExeWithFileVersion("9.9.9.0");

        var result = await fixture.RunPublisherAsync(includeDesktop: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "FileVersion",
            result.StdOut + result.StdErr,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    [Theory]
    [InlineData("Stable")]
    [InlineData("foo")]
    public async Task UpdateAssetPublisher_RejectsNonCanonicalChannel(
        string channel)
    {
        using var fixture = new PublisherFixture("0.2.81");

        var result = await fixture.RunPublisherAsync(channel: channel);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "canonical lowercase",
            result.StdOut + result.StdErr,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.OutputRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_CrossChannelNewerVersionDoesNotBlockTargetChannel()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var manifestRoot = Path.Combine(fixture.OutputRoot, "manifest");
        Directory.CreateDirectory(manifestRoot);
        var betaManifestPath = Path.Combine(manifestRoot, "beta.json");
        var betaBytes = Encoding.UTF8.GetBytes("beta newer bytes");
        File.WriteAllText(
            betaManifestPath,
            JsonSerializer.Serialize(new
            {
                channel = "beta",
                desktop = (object?)null,
                android = new
                {
                    version = "0.2.82",
                    fileName = "tradeplan-android-v0.2.82.apk",
                    sha256 = Convert.ToHexString(SHA256.HashData(betaBytes)),
                    fileSize = betaBytes.LongLength,
                },
            }));

        var allowed = await fixture.RunPublisherAsync();

        Assert.True(
            allowed.ExitCode == 0,
            allowed.StdOut + Environment.NewLine + allowed.StdErr);

        var conflictingBytes = Encoding.UTF8.GetBytes(
            "same version but conflicting cross-channel bytes");
        File.WriteAllText(
            betaManifestPath,
            JsonSerializer.Serialize(new
            {
                channel = "beta",
                desktop = (object?)null,
                android = new
                {
                    version = "0.2.81",
                    fileName = "tradeplan-android-v0.2.81.apk",
                    sha256 = Convert.ToHexString(
                        SHA256.HashData(conflictingBytes)),
                    fileSize = conflictingBytes.LongLength,
                },
            }));

        var rejected = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains(
            "conflicts",
            rejected.StdOut + rejected.StdErr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("AfterPreparationOwnerTempFlush")]
    [InlineData("AfterPreparationRootBeforeJournal")]
    [InlineData("AfterInitialJournalTempFlush")]
    [InlineData("AfterCommitTemporaryFlushBeforeReplace")]
    [InlineData("AfterDeliveryManifestCommitBeforePointer")]
    [InlineData("AfterCleanupMarkerTempFlush")]
    public async Task UpdateAssetPublisher_RecoversAfterDurabilityKillPoint(
        string killPoint)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var decoyPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            ".unrelated.stable.georaeplan-release-tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(decoyPath)!);
        File.WriteAllText(decoyPath, "unrelated decoy");

        var interrupted = await fixture.RunPublisherAsync(killPoint: killPoint);

        Assert.NotEqual(0, interrupted.ExitCode);
        var recovered = await fixture.RunPublisherAsync();
        Assert.True(
            recovered.ExitCode == 0,
            recovered.StdOut + Environment.NewLine + recovered.StdErr);
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable.prepare-*",
            SearchOption.TopDirectoryOnly));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable")));
        Assert.False(File.Exists(Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable.cleanup-marker.json")));
        var sidecars = Directory.EnumerateFiles(
            fixture.OutputRoot,
            "*.georaeplan-release-tmp",
            SearchOption.AllDirectories).ToArray();
        Assert.Equal(new[] { decoyPath }, sidecars);
        Assert.Equal("unrelated decoy", File.ReadAllText(decoyPath));
        Assert.Empty(Directory.EnumerateFiles(
            fixture.OutputRoot,
            "*.georaeplan-release-bak",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("BeforePointerReplaceAfterCommitIntent")]
    [InlineData("AfterPointerReplaceBeforeCommitJournal")]
    [InlineData("DuringJournalSidecarCleanupAfterPointerCommitPending")]
    public async Task UpdateAssetPublisher_CommitIntentFailureReturnsCommittedSuccess(
        string failurePoint)
    {
        using var fixture = new PublisherFixture("0.2.81");

        var result = await fixture.RunPublisherAsync(
            failurePoint: failurePoint);

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Contains(
            "release_commit_recovered=committed",
            result.StdOut,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.current.json")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task UpdateAssetPublisher_PointerBindsRuntimeAndDeliveryGeneration()
    {
        using var fixture = new PublisherFixture("0.2.81");

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        var pointerPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.current.json");
        using var pointer = JsonDocument.Parse(File.ReadAllBytes(pointerPath));
        var generationId = pointer.RootElement
            .GetProperty("generationId")
            .GetString();
        Assert.Matches("^[0-9a-f]{32}$", generationId);
        var runtimeGenerationPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "generations",
            "stable",
            generationId + ".json");
        var deliveryGenerationPath = pointer.RootElement
            .GetProperty("deliveryManifestPath")
            .GetString();
        Assert.True(File.Exists(runtimeGenerationPath));
        Assert.True(File.Exists(deliveryGenerationPath));
        var runtimeBytes = File.ReadAllBytes(runtimeGenerationPath);
        var deliveryBytes = File.ReadAllBytes(deliveryGenerationPath!);
        Assert.Equal(runtimeBytes, deliveryBytes);
        var expectedHash = pointer.RootElement
            .GetProperty("manifestSha256")
            .GetString();
        Assert.Equal(
            expectedHash,
            pointer.RootElement
                .GetProperty("deliveryManifestSha256")
                .GetString(),
            ignoreCase: true);
        Assert.Equal(
            pointer.RootElement
                .GetProperty("manifestFileSize")
                .GetString(),
            pointer.RootElement
                .GetProperty("deliveryManifestFileSize")
                .GetString());
        Assert.Equal(
            expectedHash,
            Convert.ToHexString(SHA256.HashData(runtimeBytes)),
            ignoreCase: true);
        using var manifest = JsonDocument.Parse(runtimeBytes);
        Assert.Equal(
            generationId,
            manifest.RootElement.GetProperty("generationId").GetString());
        Assert.Equal(
            "stable",
            manifest.RootElement.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsPointerWhoseEvidencePairDiffers()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var first = await fixture.RunPublisherAsync();
        Assert.True(first.ExitCode == 0, first.StdOut + first.StdErr);
        var pointerPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.current.json");
        var pointer = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(pointerPath))!;
        pointer["deliveryManifestSha256"] = new string('F', 64);
        File.WriteAllText(pointerPath, JsonSerializer.Serialize(pointer));

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "pointer values are invalid",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAssetPublisher_RejectsPointerSelectedManifestChannelMismatch()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var first = await fixture.RunPublisherAsync();
        Assert.True(first.ExitCode == 0, first.StdOut + first.StdErr);
        var pointerPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.current.json");
        var pointer = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(pointerPath))!;
        var generationId = pointer["generationId"];
        var runtimeGenerationPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "generations",
            "stable",
            generationId + ".json");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(
            File.ReadAllBytes(runtimeGenerationPath))!;
        manifest["channel"] = "beta";
        var mismatchedBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        File.WriteAllBytes(runtimeGenerationPath, mismatchedBytes);
        File.WriteAllBytes(
            pointer["deliveryManifestPath"],
            mismatchedBytes);
        var mismatchedHash = Convert.ToHexString(
            SHA256.HashData(mismatchedBytes));
        pointer["manifestSha256"] = mismatchedHash;
        pointer["deliveryManifestSha256"] = mismatchedHash;
        pointer["manifestFileSize"] =
            mismatchedBytes.LongLength.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        pointer["deliveryManifestFileSize"] =
            pointer["manifestFileSize"];
        File.WriteAllText(pointerPath, JsonSerializer.Serialize(pointer));

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "generation binding",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAssetPublisher_RequiresExplicitApprovalForDowngrade()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var manifestPath = Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json");
        var deliveryPath = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deliveryPath)!);
        var newerBytes = Encoding.UTF8.GetBytes("newer android bytes");
        var newerManifest = JsonSerializer.Serialize(new
        {
            channel = "stable",
            desktop = (object?)null,
            android = new
            {
                version = "0.2.82",
                fileName = "tradeplan-android-v0.2.82.apk",
                sha256 = Convert.ToHexString(SHA256.HashData(newerBytes)),
                fileSize = newerBytes.LongLength,
            },
        });
        File.WriteAllText(manifestPath, newerManifest);
        File.WriteAllText(deliveryPath, newerManifest);

        var rejected = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains(
            "-AllowDowngrade",
            rejected.StdOut + rejected.StdErr,
            StringComparison.Ordinal);
        Assert.Equal(newerManifest, File.ReadAllText(manifestPath));

        var approved = await fixture.RunPublisherAsync(allowDowngrade: true);

        Assert.True(
            approved.ExitCode == 0,
            approved.StdOut + Environment.NewLine + approved.StdErr);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Assert.Equal(
            "0.2.81",
            document.RootElement
                .GetProperty("android")
                .GetProperty("version")
                .GetString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("backup")]
    public async Task UpdateAssetPublisher_ReplaysCommittedJournalBeforeDeletingEvidence(
        string targetState)
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var targetPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "committed-replay.apk");
        var stagedPath = Path.Combine(
            stagingRoot,
            "000-committed-replay.apk.stage");
        var backupPath = Path.Combine(
            backupRoot,
            "000-committed-replay.apk.bak");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var stagedBytes = Encoding.UTF8.GetBytes("committed generation");
        var backupBytes = Encoding.UTF8.GetBytes("previous generation");
        File.WriteAllBytes(stagedPath, stagedBytes);
        File.WriteAllBytes(backupPath, backupBytes);
        if (targetState == "backup")
            File.WriteAllBytes(targetPath, backupBytes);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "Committed",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new[]
                {
                    new
                    {
                        targetPath,
                        stagedPath,
                        backupPath,
                        targetExisted = true,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(stagedBytes)),
                        backupSha256 = Convert.ToHexString(
                            SHA256.HashData(backupBytes)),
                        stagedFileSize = stagedBytes.LongLength,
                        backupFileSize = (long?)backupBytes.LongLength,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(targetPath),
                    },
                },
            }));

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Equal(stagedBytes, File.ReadAllBytes(targetPath));
        Assert.False(Directory.Exists(transactionRoot));
        Assert.False(File.Exists(transactionRoot + ".cleanup-marker.json"));
        Assert.False(Directory.Exists(
            transactionRoot + ".cleanup-pending"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_PreservesCommittedJournalForUnownedTarget()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var targetPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "committed-unowned.apk");
        var stagedPath = Path.Combine(
            stagingRoot,
            "000-committed-unowned.apk.stage");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var stagedBytes = Encoding.UTF8.GetBytes("committed generation");
        var unownedBytes = Encoding.UTF8.GetBytes("unowned replacement");
        File.WriteAllBytes(stagedPath, stagedBytes);
        File.WriteAllBytes(targetPath, unownedBytes);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "Committed",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new[]
                {
                    new
                    {
                        targetPath,
                        stagedPath,
                        backupPath = (string?)null,
                        targetExisted = false,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(stagedBytes)),
                        backupSha256 = (string?)null,
                        stagedFileSize = stagedBytes.LongLength,
                        backupFileSize = (long?)null,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(targetPath),
                    },
                },
            }));

        var result = await fixture.RunPublisherAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "Committed release target contains unowned",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unownedBytes, File.ReadAllBytes(targetPath));
        Assert.True(Directory.Exists(transactionRoot));
    }

    [Fact]
    public async Task UpdateAssetPublisher_RevalidatesCleanupPendingJournalBeforeTombstoning()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var targetPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            "cleanup-pending-replay.bin");
        var stagedPath = Path.Combine(
            stagingRoot,
            "000-cleanup-pending-replay.bin.stage");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        var stagedBytes = Encoding.UTF8.GetBytes(
            "cleanup pending committed generation");
        File.WriteAllBytes(stagedPath, stagedBytes);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "CleanupPending",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new[]
                {
                    new
                    {
                        targetPath,
                        stagedPath,
                        backupPath = (string?)null,
                        targetExisted = false,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(stagedBytes)),
                        backupSha256 = (string?)null,
                        stagedFileSize = stagedBytes.LongLength,
                        backupFileSize = (long?)null,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(targetPath),
                    },
                },
            }));
        var cleanupRoot = transactionRoot + ".cleanup-pending";
        File.WriteAllText(
            transactionRoot + ".cleanup-marker.json",
            JsonSerializer.Serialize(new
            {
                owner = "georaeplan-release-cleanup-marker",
                channel = "stable",
                projectRoot = fixture.ProjectRoot.ToUpperInvariant(),
                outputRoot = fixture.OutputRoot.ToUpperInvariant(),
                transactionRoot = transactionRoot.ToUpperInvariant(),
                cleanupRoot = cleanupRoot.ToUpperInvariant(),
            }),
            new UTF8Encoding(false));

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Equal(stagedBytes, File.ReadAllBytes(targetPath));
        Assert.False(Directory.Exists(transactionRoot));
        Assert.False(Directory.Exists(cleanupRoot));
        Assert.False(File.Exists(
            transactionRoot + ".cleanup-marker.json"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_ResumesRollbackCleanupWithoutReplayingCommit()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var stagingRoot = Path.Combine(transactionRoot, "staging");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        var targetPath = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "desktop",
            "rollback-cleanup.bin");
        var stagedPath = Path.Combine(
            stagingRoot,
            "000-rollback-cleanup.bin.stage");
        var backupPath = Path.Combine(
            backupRoot,
            "000-rollback-cleanup.bin.bak");
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var stagedBytes = Encoding.UTF8.GetBytes(
            "generation that must stay rolled back");
        var backupBytes = Encoding.UTF8.GetBytes(
            "previous generation restored by rollback");
        File.WriteAllBytes(stagedPath, stagedBytes);
        File.WriteAllBytes(backupPath, backupBytes);
        File.WriteAllBytes(targetPath, backupBytes);
        File.WriteAllText(
            Path.Combine(transactionRoot, "journal.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 3,
                owner = "georaeplan-release-transaction",
                channel = "stable",
                phase = "RollbackCleanupPending",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                entries = new[]
                {
                    new
                    {
                        targetPath,
                        stagedPath,
                        backupPath,
                        targetExisted = true,
                        stagedSha256 = Convert.ToHexString(
                            SHA256.HashData(stagedBytes)),
                        backupSha256 = Convert.ToHexString(
                            SHA256.HashData(backupBytes)),
                        stagedFileSize = stagedBytes.LongLength,
                        backupFileSize = (long?)backupBytes.LongLength,
                        commitTemporaryPath =
                            GetJournalCommitTemporaryPath(targetPath),
                    },
                },
            }));
        var cleanupRoot = transactionRoot + ".cleanup-pending";
        File.WriteAllText(
            transactionRoot + ".cleanup-marker.json",
            JsonSerializer.Serialize(new
            {
                owner = "georaeplan-release-cleanup-marker",
                channel = "stable",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                cleanupRoot,
            }),
            new UTF8Encoding(false));

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.Equal(backupBytes, File.ReadAllBytes(targetPath));
        Assert.False(Directory.Exists(transactionRoot));
        Assert.False(Directory.Exists(cleanupRoot));
        Assert.False(File.Exists(
            transactionRoot + ".cleanup-marker.json"));
    }

    [Fact]
    public async Task UpdateAssetPublisher_ResumesCleanupTombstoneAfterJournalLoss()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var transactionRoot = Path.Combine(
            fixture.OutputRoot,
            ".georaeplan-release-transaction-stable");
        var cleanupRoot = transactionRoot + ".cleanup-pending";
        var markerPath = transactionRoot + ".cleanup-marker.json";
        Directory.CreateDirectory(cleanupRoot);
        File.WriteAllText(
            Path.Combine(cleanupRoot, "partially-deleted.bin"),
            "owned cleanup remainder");
        File.WriteAllText(
            markerPath,
            JsonSerializer.Serialize(new
            {
                owner = "georaeplan-release-cleanup-marker",
                channel = "stable",
                projectRoot = fixture.ProjectRoot,
                outputRoot = fixture.OutputRoot,
                transactionRoot,
                cleanupRoot,
            }),
            new UTF8Encoding(false));

        var result = await fixture.RunPublisherAsync();

        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine + result.StdErr);
        Assert.False(Directory.Exists(cleanupRoot));
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task UpdateAssetPublisher_MalformedSiblingManifestBlocksPruneBeforeMutation()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var manifestRoot = Path.Combine(fixture.OutputRoot, "manifest");
        var oldPackage = Path.Combine(
            fixture.OutputRoot,
            "downloads",
            "android",
            "old-unreferenced.apk");
        Directory.CreateDirectory(manifestRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(oldPackage)!);
        File.WriteAllText(
            Path.Combine(manifestRoot, "stable.previous.json"),
            "{\"channel\":\"stable\",\"android\":");
        File.WriteAllText(oldPackage, "preserve malformed-manifest package");

        var result = await fixture.RunPublisherAsync(
            skipPackagePrune: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "cannot be read safely",
            result.StdOut + result.StdErr,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "preserve malformed-manifest package",
            File.ReadAllText(oldPackage));
        Assert.False(File.Exists(Path.Combine(
            manifestRoot,
            "stable.json")));
    }

    [Fact]
    public async Task UpdateAssetPublisher_DifferentOutputRootsShareDeliveryLock()
    {
        using var fixture = new PublisherFixture("0.2.81");
        var deliveryRoot = Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC");
        Directory.CreateDirectory(deliveryRoot);
        var sharedLockPath = Path.Combine(
            deliveryRoot,
            ".georaeplan-release-publish-stable.lock");
        var secondOutputRoot = Path.Combine(
            fixture.Root,
            "publish-output-second");
        Task<ProcessResult> firstTask;
        Task<ProcessResult> secondTask;
        using (File.Open(
                   sharedLockPath,
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            firstTask = fixture.RunPublisherAsync();
            secondTask = fixture.RunPublisherAsync(
                outputRootOverride: secondOutputRoot);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (
                Directory.EnumerateDirectories(
                    fixture.SnapshotTempRoot,
                    "georaeplan-android-apk-*").Count() < 2 &&
                !firstTask.IsCompleted &&
                !secondTask.IsCompleted &&
                DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            Assert.False(
                firstTask.IsCompleted,
                "First output-root publisher bypassed the shared delivery lock.");
            Assert.False(
                secondTask.IsCompleted,
                "Second output-root publisher bypassed the shared delivery lock.");
            Assert.False(Directory.Exists(fixture.OutputRoot));
            Assert.False(Directory.Exists(secondOutputRoot));
        }

        var results = await Task.WhenAll(firstTask, secondTask);

        foreach (var result in results)
        {
            Assert.True(
                result.ExitCode == 0,
                result.StdOut + Environment.NewLine + result.StdErr);
        }
        Assert.True(File.Exists(Path.Combine(
            fixture.OutputRoot,
            "manifest",
            "stable.json")));
        Assert.True(File.Exists(Path.Combine(
            secondOutputRoot,
            "manifest",
            "stable.json")));
        Assert.True(File.Exists(Path.Combine(
            fixture.ProjectRoot,
            "\uBC30\uD3EC",
            "stable.json")));
    }

    private static string ReadRepositoryFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(segments)));

    private static string GetJournalCommitTemporaryPath(
        string targetPath,
        string channel = "stable")
        => Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{channel}.georaeplan-release-tmp");

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "/d",
            "/c",
            "mklink",
            "/J",
            junctionPath,
            targetPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            stdout + Environment.NewLine + stderr);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string callerFilePath = "")
        => Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(callerFilePath)!,
            "..",
            ".."));

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previousIndex = -1;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(
                fragment,
                previousIndex + 1,
                StringComparison.Ordinal);
            Assert.True(
                index > previousIndex,
                $"Expected fragment after index {previousIndex}: {fragment}");
            previousIndex = index;
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string powerShellPath,
        string scriptPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
        }.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await RunRedirectedProcessAsync(
            startInfo,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(10),
            $"PowerShell harness: {scriptPath}");
    }

    private static async Task<ProcessResult> RunRedirectedProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan executionTimeout,
        TimeSpan cleanupTimeout,
        string operationName)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"{operationName} did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var completionTask = Task.WhenAll(
            process.WaitForExitAsync(),
            stdoutTask,
            stderrTask);
        try
        {
            await completionTask.WaitAsync(executionTimeout);
        }
        catch (TimeoutException timeoutError)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception cleanupError)
            {
                cleanupFailures.Add(cleanupError);
            }

            try
            {
                await completionTask.WaitAsync(cleanupTimeout);
            }
            catch (Exception cleanupError)
            {
                cleanupFailures.Add(cleanupError);
            }

            var message =
                $"{operationName} timed out after {executionTimeout}.";
            if (cleanupFailures.Count == 0)
            {
                throw new TimeoutException(
                    message + " Its process tree was terminated.",
                    timeoutError);
            }
            throw new AggregateException(
                message + " Process cleanup also failed.",
                new Exception[] { timeoutError }.Concat(cleanupFailures));
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static OwnedProcessIdentity ParseOwnedProcessIdentity(
        string value)
    {
        var parts = value.Split(
            ['|'],
            3,
            StringSplitOptions.None);
        if (
            parts.Length != 3 ||
            !int.TryParse(parts[0], out var processId) ||
            !long.TryParse(parts[1], out var startTimeUtcTicks) ||
            string.IsNullOrWhiteSpace(parts[2])
        ) {
            throw new InvalidOperationException(
                $"Owned process identity is invalid: {value}");
        }
        return new OwnedProcessIdentity(
            processId,
            startTimeUtcTicks,
            Path.GetFullPath(parts[2]));
    }

    private static bool IsOwnedProcessRunning(
        OwnedProcessIdentity identity)
    {
        if (!TryOpenOwnedProcess(identity, out var process))
            return false;
        process.Dispose();
        return true;
    }

    private static bool TryOpenOwnedProcess(
        OwnedProcessIdentity identity,
        out Process process)
    {
        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(identity.ProcessId);
            var safeHandle = candidate.SafeHandle;
            if (safeHandle.IsInvalid || safeHandle.IsClosed)
            {
                candidate.Dispose();
                process = null!;
                return false;
            }
            candidate.Refresh();
            if (
                candidate.HasExited ||
                candidate.StartTime.ToUniversalTime().Ticks !=
                    identity.StartTimeUtcTicks ||
                !string.Equals(
                    candidate.MainModule?.FileName,
                    identity.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
            ) {
                candidate.Dispose();
                process = null!;
                return false;
            }
            process = candidate;
            return true;
        }
        catch (Exception error)
            when (error is ArgumentException or
                  InvalidOperationException)
        {
            candidate?.Dispose();
            process = null!;
            return false;
        }
    }

    private sealed record OwnedProcessIdentity(
        int ProcessId,
        long StartTimeUtcTicks,
        string ExecutablePath);

    private static void CreateNativeInstallerFixtures(
        string powerShellPath,
        string fixtureRoot,
        string version,
        string exePath,
        string msiPath)
    {
        lock (NativeInstallerFixtureLock)
        {
            if (string.Equals(
                    s_nativeInstallerFixtureVersion,
                    version,
                    StringComparison.Ordinal) &&
                s_nativeExeFixtureBytes is not null &&
                s_nativeMsiFixtureBytes is not null)
            {
                File.WriteAllBytes(exePath, s_nativeExeFixtureBytes);
                File.WriteAllBytes(msiPath, s_nativeMsiFixtureBytes);
                return;
            }

            var assemblyVersion = version + ".0";
            CreateVersionedExeFixture(
                powerShellPath,
                fixtureRoot,
                version,
                assemblyVersion,
                exePath);

            CreateVersionedMsiFixture(msiPath, version);
            var exeVersionInfo = FileVersionInfo.GetVersionInfo(exePath);
            var exeProductVersion = exeVersionInfo.ProductVersion;
            if (!string.Equals(
                    exeProductVersion,
                    version,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Versioned EXE fixture ProductVersion is not exact.");
            }
            if (!string.Equals(
                    exeVersionInfo.FileVersion,
                    assemblyVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Versioned EXE fixture FileVersion is not exact.");
            }

            s_nativeInstallerFixtureVersion = version;
            s_nativeExeFixtureBytes = File.ReadAllBytes(exePath);
            s_nativeMsiFixtureBytes = File.ReadAllBytes(msiPath);
        }
    }

    private static void CreateVersionedExeFixture(
        string powerShellPath,
        string fixtureRoot,
        string productVersion,
        string fileVersion,
        string exePath)
    {
        if (!Version.TryParse(productVersion, out _) ||
            !Version.TryParse(fileVersion, out _) ||
            productVersion.Any(character =>
                character is not (>= '0' and <= '9') and not '.') ||
            fileVersion.Any(character =>
                character is not (>= '0' and <= '9') and not '.'))
        {
            throw new InvalidOperationException(
                "Native EXE fixture version is not safe.");
        }

        var compilerScriptPath = Path.Combine(
            fixtureRoot,
            "create-versioned-installer-exe.ps1");
        File.WriteAllText(
            compilerScriptPath,
            $$"""
            [CmdletBinding()]
            param([Parameter(Mandatory = $true)][string]$OutputPath)
            $ErrorActionPreference = 'Stop'
            $source = @'
            using System;
            using System.Reflection;
            [assembly: AssemblyTitle("GeoraePlan native installer fixture")]
            [assembly: AssemblyProduct("GeoraePlan native installer fixture")]
            [assembly: AssemblyVersion("{{fileVersion}}")]
            [assembly: AssemblyFileVersion("{{fileVersion}}")]
            [assembly: AssemblyInformationalVersion("{{productVersion}}")]
            public static class Program
            {
                [STAThread]
                public static void Main() { }
            }
            '@
            Add-Type `
                -TypeDefinition $source `
                -Language CSharp `
                -OutputAssembly $OutputPath `
                -OutputType ConsoleApplication `
                -ErrorAction Stop
            """,
            new UTF8Encoding(false));
        var compiler = new ProcessStartInfo
        {
            FileName = powerShellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            compilerScriptPath,
            "-OutputPath",
            exePath
        })
        {
            compiler.ArgumentList.Add(argument);
        }
        using var process = Process.Start(compiler)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(
                (int)TimeSpan.FromSeconds(30).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "Versioned EXE fixture compilation timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Versioned EXE fixture compilation failed." +
                Environment.NewLine +
                stdout +
                Environment.NewLine +
                stderr);
        }
    }

    private static void CreateVersionedMsiFixture(
        string msiPath,
        string version)
    {
        var installerType = Type.GetTypeFromProgID(
            "WindowsInstaller.Installer",
            throwOnError: true)!;
        object? installer = null;
        object? database = null;
        try
        {
            installer = Activator.CreateInstance(installerType)
                ?? throw new InvalidOperationException(
                    "Windows Installer COM activation failed.");
            database = installerType.InvokeMember(
                "OpenDatabase",
                BindingFlags.InvokeMethod,
                binder: null,
                target: installer,
                args: [msiPath, 3])
                ?? throw new InvalidOperationException(
                    "Windows Installer database activation failed.");
            ExecuteMsiSql(
                database,
                "CREATE TABLE `Property` " +
                "(`Property` CHAR(72) NOT NULL, " +
                "`Value` CHAR(0) NOT NULL PRIMARY KEY `Property`)");
            ExecuteMsiSql(
                database,
                "INSERT INTO `Property` (`Property`, `Value`) " +
                $"VALUES ('ProductVersion', '{version}')");
            database.GetType().InvokeMember(
                "Commit",
                BindingFlags.InvokeMethod,
                binder: null,
                target: database,
                args: null);
        }
        finally
        {
            ReleaseComObject(database);
            ReleaseComObject(installer);
        }
    }

    private static void ExecuteMsiSql(object database, string sql)
    {
        object? view = null;
        try
        {
            view = database.GetType().InvokeMember(
                "OpenView",
                BindingFlags.InvokeMethod,
                binder: null,
                target: database,
                args: [sql])
                ?? throw new InvalidOperationException(
                    "Windows Installer SQL view activation failed.");
            view.GetType().InvokeMember(
                "Execute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: view,
                args: null);
        }
        finally
        {
            ReleaseComObject(view);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void DeleteDirectoryWithRetries(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static SortedDictionary<string, string> CaptureTreeHashes(
        string root)
    {
        return new SortedDictionary<string, string>(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => Path.GetRelativePath(root, path),
                    path => Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(path))),
                    StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PublisherFixture : IDisposable
    {
        private readonly string _publisherPath;
        private readonly string _preparationScriptPath;
        private readonly string _powerShellPath;

        public PublisherFixture(string analyzerVersionName)
        {
            var repositoryRoot = FindRepositoryRoot();
            Root = Path.Combine(
                TestProcessIsolation.TempRoot,
                $"android-update-publisher-{Guid.NewGuid():N}");
            ProjectRoot = Path.Combine(Root, "project");
            OutputRoot = Path.Combine(Root, "publish-output");
            RuntimeRoot = Path.Combine(Root, "runtime");
            ApkPath = Path.Combine(Root, "candidate.apk");
            AnalyzerPath = Path.Combine(Root, "fake-apkanalyzer.cmd");
            JavaRoot = Path.Combine(Root, "fake-java");
            SnapshotTempRoot = Path.Combine(Root, "snapshot-temp");
            _publisherPath = Path.Combine(
                repositoryRoot,
                "tools",
                "release",
                "Publish-GeoraePlanUpdateAssets.ps1");
            _preparationScriptPath = Path.Combine(
                repositoryRoot,
                "테스트 시행",
                "테스트-환경-준비.ps1");
            _powerShellPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");

            Directory.CreateDirectory(Path.Combine(
                ProjectRoot,
                "Mobile",
                "GeoraePlan.Mobile.App"));
            Directory.CreateDirectory(Path.Combine(
                ProjectRoot,
                "tools",
                "mobile"));
            Directory.CreateDirectory(Path.Combine(JavaRoot, "bin"));
            Directory.CreateDirectory(SnapshotTempRoot);
            File.Copy(
                Path.Combine(
                    repositoryRoot,
                    "tools",
                    "mobile",
                    "AndroidApkMetadata.ps1"),
                Path.Combine(
                    ProjectRoot,
                    "tools",
                    "mobile",
                    "AndroidApkMetadata.ps1"));
            File.WriteAllText(
                Path.Combine(
                    ProjectRoot,
                    "Mobile",
                    "GeoraePlan.Mobile.App",
                    "GeoraePlan.Mobile.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <ApplicationId>kr.georaeplan.mobile</ApplicationId>
                    <ApplicationDisplayVersion>0.2.81</ApplicationDisplayVersion>
                    <ApplicationVersion>192</ApplicationVersion>
                  </PropertyGroup>
                </Project>
                """,
                new UTF8Encoding(false));
            ApkBytes = Encoding.UTF8.GetBytes(
                "dummy apk bytes for publisher identity verification");
            File.WriteAllBytes(ApkPath, ApkBytes);
            File.WriteAllText(
                AnalyzerPath,
                $$"""
                @echo off
                if defined FAKE_APK_ANALYZER_DELAY_MS (
                  powershell -NoProfile -NonInteractive -Command "Start-Sleep -Milliseconds %FAKE_APK_ANALYZER_DELAY_MS%" >nul
                )
                if /I "%~1" neq "manifest" exit /b 91
                if /I "%~2"=="application-id" (
                  echo kr.georaeplan.mobile
                  exit /b 0
                )
                if /I "%~2"=="version-name" (
                  echo {{analyzerVersionName}}
                  exit /b 0
                )
                if /I "%~2"=="version-code" (
                  echo 192
                  exit /b 0
                )
                exit /b 92
                """,
                Encoding.ASCII);
            File.WriteAllBytes(
                Path.Combine(JavaRoot, "bin", "java.exe"),
                [0x4D, 0x5A]);

            var desktopExecutable = Path.Combine(
                AppContext.BaseDirectory,
                "\uAC70\uB798\uD50C\uB79C.Desktop.App.exe");
            if (!File.Exists(desktopExecutable))
                throw new FileNotFoundException(
                    "Desktop fixture executable was not built.",
                    desktopExecutable);
            DesktopVersion = (
                FileVersionInfo
                    .GetVersionInfo(desktopExecutable)
                    .ProductVersion
                ?? throw new InvalidOperationException(
                    "Desktop fixture executable has no ProductVersion."))
                .Split('+')[0]
                .Trim();
            DesktopPackagePath = Path.Combine(Root, "desktop-package.zip");
            DesktopExeInstallerPath = Path.Combine(Root, "desktop-installer.exe");
            DesktopMsiInstallerPath = Path.Combine(Root, "desktop-installer.msi");
            using (var archive = ZipFile.Open(
                       DesktopPackagePath,
                       ZipArchiveMode.Create))
            {
                WriteArchiveEntry(
                    archive,
                    "Install-GeoraePlan.ps1",
                    Encoding.UTF8.GetBytes(
                        "[CmdletBinding()]\r\n" +
                        "param(\r\n" +
                        "    [string]$InstallRoot,\r\n" +
                        "    [switch]$NoLaunch,\r\n" +
                        "    [switch]$SuppressUi,\r\n" +
                        "    [int]$WorkerTimeoutSeconds = 1,\r\n" +
                        "    [string]$LogPath,\r\n" +
                        "    [switch]$RecoveryOnly,\r\n" +
                        "    [switch]$LegacyBridgeCopy,\r\n" +
                        "    [switch]$UpdaterOwnsInstallRootGate,\r\n" +
                        "    [int]$InstallRootGateOwnerProcessId = 0,\r\n" +
                        "    [string]$InstallRootGateOwnerProcessPath,\r\n" +
                        "    [long]$InstallRootGateOwnerProcessStartTimeUtcTicks = 0\r\n" +
                        ")\r\n" +
                        "# GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1\r\n" +
                        "# GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1\r\n" +
                        "Write-Host fixture\r\n"));
                WriteArchiveEntry(
                    archive,
                    "Install-GeoraePlan.cmd",
                    Encoding.UTF8.GetBytes(
                        "@echo off\r\n" +
                        "powershell -ExecutionPolicy Bypass -File \"%~dp0Install-GeoraePlan.ps1\"\r\n"));
                WriteArchiveEntry(
                    archive,
                    "App/\uAC70\uB798\uD50C\uB79C.Desktop.App.exe",
                    File.ReadAllBytes(desktopExecutable));
                WriteArchiveEntry(
                    archive,
                    "App/\uAC70\uB798\uD50C\uB79C.exe",
                    File.ReadAllBytes(desktopExecutable));
                WriteArchiveEntry(
                    archive,
                    "App/appsettings.json",
                    Encoding.UTF8.GetBytes("{}"));
                WriteArchiveEntry(
                    archive,
                    "App/Updater/\uAC70\uB798\uD50C\uB79C.Updater.exe",
                    Encoding.UTF8.GetBytes("fixture updater"));
                WriteArchiveEntry(
                    archive,
                    "App/\uC571\uC2E4\uD589.cmd",
                    Encoding.UTF8.GetBytes(
                        "@echo off\r\n" +
                        "set \"APP_EXE=\"\r\n" +
                        "for %%I in (\"%~dp0*.Desktop.App.exe\") do set \"APP_EXE=%%~fI\"\r\n" +
                        "if not defined APP_EXE exit /b 1\r\n" +
                        "start \"\" \"%APP_EXE%\"\r\n"));
            }
            CreateNativeInstallerFixtures(
                _powerShellPath,
                Root,
                DesktopVersion,
                DesktopExeInstallerPath,
                DesktopMsiInstallerPath);
        }

        public string Root { get; }
        public string ProjectRoot { get; }
        public string OutputRoot { get; }
        public string RuntimeRoot { get; }
        public string ApkPath { get; }
        public string AnalyzerPath { get; }
        public string JavaRoot { get; }
        public string SnapshotTempRoot { get; }
        public string DesktopVersion { get; }
        public string DesktopPackagePath { get; }
        public string DesktopExeInstallerPath { get; }
        public string DesktopMsiInstallerPath { get; }
        public byte[] ApkBytes { get; }

        public byte[] ReadDesktopArchiveEntry(string entryName)
        {
            using var archive = ZipFile.OpenRead(DesktopPackagePath);
            var entry = archive.GetEntry(entryName)
                ?? throw new InvalidOperationException(
                    $"Desktop fixture entry was not found: {entryName}");
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            return buffer.ToArray();
        }

        public string ReadDesktopArchiveTextEntry(string entryName)
            => Encoding.UTF8.GetString(ReadDesktopArchiveEntry(entryName));

        public void ReplaceDesktopArchiveEntry(
            string entryName,
            byte[] bytes)
        {
            using var archive = ZipFile.Open(
                DesktopPackagePath,
                ZipArchiveMode.Update);
            var existing = archive.GetEntry(entryName)
                ?? throw new InvalidOperationException(
                    $"Desktop fixture entry was not found: {entryName}");
            existing.Delete();
            WriteArchiveEntry(archive, entryName, bytes);
        }

        public void AddDesktopArchiveEntryWithUnderreportedLength(
            string entryName,
            byte[] bytes,
            uint declaredLength)
        {
            using (var archive = ZipFile.Open(
                       DesktopPackagePath,
                       ZipArchiveMode.Update))
            {
                WriteArchiveEntry(archive, entryName, bytes);
            }

            var packageBytes = File.ReadAllBytes(DesktopPackagePath);
            var eocdOffset = FindSignatureFromEnd(packageBytes, 0x06054B50u);
            var entryCount = BitConverter.ToUInt16(packageBytes, eocdOffset + 10);
            var centralDirectoryOffset = checked((int)BitConverter.ToUInt32(
                packageBytes,
                eocdOffset + 16));
            var currentOffset = centralDirectoryOffset;
            var patched = false;
            for (var index = 0; index < entryCount; index++)
            {
                if (BitConverter.ToUInt32(packageBytes, currentOffset) !=
                    0x02014B50u)
                {
                    throw new InvalidDataException(
                        "Invalid central directory entry signature.");
                }
                var nameLength = BitConverter.ToUInt16(
                    packageBytes,
                    currentOffset + 28);
                var extraLength = BitConverter.ToUInt16(
                    packageBytes,
                    currentOffset + 30);
                var commentLength = BitConverter.ToUInt16(
                    packageBytes,
                    currentOffset + 32);
                var name = Encoding.UTF8.GetString(
                    packageBytes,
                    currentOffset + 46,
                    nameLength);
                if (string.Equals(name, entryName, StringComparison.Ordinal))
                {
                    Array.Copy(
                        BitConverter.GetBytes(declaredLength),
                        0,
                        packageBytes,
                        currentOffset + 24,
                        sizeof(uint));
                    patched = true;
                    break;
                }
                currentOffset = checked(
                    currentOffset + 46 + nameLength + extraLength +
                    commentLength);
            }
            if (!patched)
                throw new InvalidOperationException(
                    $"Desktop ZIP central directory entry was not found: {entryName}");
            File.WriteAllBytes(DesktopPackagePath, packageBytes);
        }

        private static int FindSignatureFromEnd(byte[] bytes, uint signature)
        {
            for (var index = bytes.Length - sizeof(uint); index >= 0; index--)
            {
                if (BitConverter.ToUInt32(bytes, index) == signature)
                    return index;
            }
            throw new InvalidDataException(
                $"ZIP signature was not found: 0x{signature:X8}");
        }

        public async Task<ProcessResult> RunPublisherAsync(
            bool skipAndroid = false,
            bool includeAndroidPath = true,
            bool includeDesktop = false,
            int analyzerDelayMilliseconds = 0,
            string? outputRootOverride = null,
            bool allowDowngrade = false,
            bool skipPackagePrune = true,
            string? killPoint = null,
            string? failurePoint = null,
            string? pausePoint = null,
            string? pauseReadyEventName = null,
            string? pauseContinueEventName = null,
            string channel = "stable")
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _powerShellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.Environment["GEORAEPLAN_TEMP_ROOT"] = SnapshotTempRoot;
            startInfo.Environment["TEMP"] = SnapshotTempRoot;
            startInfo.Environment["TMP"] = SnapshotTempRoot;
            if (analyzerDelayMilliseconds > 0)
            {
                startInfo.Environment["FAKE_APK_ANALYZER_DELAY_MS"] =
                    analyzerDelayMilliseconds.ToString();
            }
            if (!string.IsNullOrWhiteSpace(killPoint))
            {
                startInfo.Environment["GEORAEPLAN_RELEASE_TEST_KILL_POINT"] =
                    killPoint;
            }
            if (!string.IsNullOrWhiteSpace(failurePoint))
            {
                startInfo.Environment[
                    "GEORAEPLAN_RELEASE_TEST_FAILURE_POINT"] =
                    failurePoint;
            }
            if (!string.IsNullOrWhiteSpace(pausePoint))
            {
                if (string.IsNullOrWhiteSpace(pauseReadyEventName) ||
                    string.IsNullOrWhiteSpace(pauseContinueEventName))
                {
                    throw new ArgumentException(
                        "Release test pause event names are required.");
                }
                startInfo.Environment["GEORAEPLAN_RELEASE_TEST_PAUSE_POINT"] =
                    pausePoint;
                startInfo.Environment[
                    "GEORAEPLAN_RELEASE_TEST_PAUSE_READY_EVENT"] =
                    pauseReadyEventName;
                startInfo.Environment[
                    "GEORAEPLAN_RELEASE_TEST_PAUSE_CONTINUE_EVENT"] =
                    pauseContinueEventName;
            }
            var arguments = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                _publisherPath,
                "-ProjectRoot",
                ProjectRoot,
                "-OutputRoot",
                outputRootOverride ?? OutputRoot,
                "-Channel",
                channel,
                "-DesktopVersion",
                DesktopVersion,
                "-AndroidVersion",
                "0.2.81",
                "-ApkAnalyzerPath",
                AnalyzerPath,
                "-JavaSdkDirectory",
                JavaRoot,
            };
            if (skipPackagePrune)
                arguments.Add("-SkipPackagePrune");
            if (allowDowngrade)
                arguments.Add("-AllowDowngrade");
            if (includeAndroidPath)
            {
                arguments.Add("-AndroidPackagePath");
                arguments.Add(ApkPath);
            }
            if (skipAndroid)
                arguments.Add("-SkipAndroid");
            if (includeDesktop)
            {
                arguments.Add("-DesktopPackagePath");
                arguments.Add(DesktopPackagePath);
                arguments.Add("-DesktopExeInstallerPath");
                arguments.Add(DesktopExeInstallerPath);
                arguments.Add("-DesktopMsiInstallerPath");
                arguments.Add(DesktopMsiInstallerPath);
            }
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return await AndroidUpdatePackageIdentityGateTests
                .RunRedirectedProcessAsync(
                    startInfo,
                    string.IsNullOrWhiteSpace(pausePoint)
                        ? TimeSpan.FromSeconds(60)
                        : TimeSpan.FromSeconds(75),
                    TimeSpan.FromSeconds(10),
                    "Update asset publisher");
        }

        public void ReplaceNativeInstallerWithVersion(
            string format,
            string version)
        {
            var mismatchRoot = Path.Combine(
                Root,
                "native-version-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mismatchRoot);
            var mismatchExe = Path.Combine(mismatchRoot, "installer.exe");
            var mismatchMsi = Path.Combine(mismatchRoot, "installer.msi");
            CreateNativeInstallerFixtures(
                _powerShellPath,
                mismatchRoot,
                version,
                mismatchExe,
                mismatchMsi);
            File.Copy(
                string.Equals(format, "exe", StringComparison.Ordinal)
                    ? mismatchExe
                    : mismatchMsi,
                string.Equals(format, "exe", StringComparison.Ordinal)
                    ? DesktopExeInstallerPath
                    : DesktopMsiInstallerPath,
                overwrite: true);
        }

        public void ReplaceNativeExeWithFileVersion(string fileVersion)
        {
            var mismatchRoot = Path.Combine(
                Root,
                "native-file-version-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mismatchRoot);
            var mismatchExe = Path.Combine(mismatchRoot, "installer.exe");
            CreateVersionedExeFixture(
                _powerShellPath,
                mismatchRoot,
                DesktopVersion,
                fileVersion,
                mismatchExe);
            File.Copy(
                mismatchExe,
                DesktopExeInstallerPath,
                overwrite: true);
        }

        public async Task<ProcessResult> RunPreparationHelperAsync()
        {
            var harnessPath = Path.Combine(
                Root,
                "initialize-test-android-package.ps1");
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$HelperPath,
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$MobileProject,
                    [Parameter(Mandatory = $true)][string]$AndroidPackagePath,
                    [Parameter(Mandatory = $true)][string]$ApkAnalyzerPath,
                    [Parameter(Mandatory = $true)][string]$JavaSdkDirectory,
                    [Parameter(Mandatory = $true)][string]$SnapshotTempRoot
                )

                $ErrorActionPreference = 'Stop'
                $env:TEMP = $SnapshotTempRoot
                $env:TMP = $SnapshotTempRoot
                . $HelperPath
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'New-Utf8BomEncoding',
                    'Write-Utf8File',
                    'Publish-TestFileAtomically',
                    'Get-TestCsprojPropertyValue',
                    'Initialize-TestAndroidPackageMetadata'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }
                Initialize-TestAndroidPackageMetadata `
                    -ProjectRoot $ProjectRoot `
                    -OutputRoot $OutputRoot `
                    -MobileProject $MobileProject `
                    -AndroidPackagePath $AndroidPackagePath `
                    -ApkAnalyzerPath $ApkAnalyzerPath `
                    -JavaSdkDirectory $JavaSdkDirectory |
                    ConvertTo-Json -Compress
                """,
                new UTF8Encoding(false));

            return await RunPowerShellAsync(
                _powerShellPath,
                harnessPath,
                "-SourceScript",
                _preparationScriptPath,
                "-HelperPath",
                Path.Combine(
                    ProjectRoot,
                    "tools",
                    "mobile",
                    "AndroidApkMetadata.ps1"),
                "-ProjectRoot",
                ProjectRoot,
                "-OutputRoot",
                RuntimeRoot,
                "-MobileProject",
                Path.Combine(
                    ProjectRoot,
                    "Mobile",
                    "GeoraePlan.Mobile.App",
                    "GeoraePlan.Mobile.App.csproj"),
                "-AndroidPackagePath",
                ApkPath,
                "-ApkAnalyzerPath",
                AnalyzerPath,
                "-JavaSdkDirectory",
                JavaRoot,
                "-SnapshotTempRoot",
                SnapshotTempRoot);
        }

        public async Task<ProcessResult> RunPreparationAbsenceInspectionHelperAsync()
        {
            var harnessPath = Path.Combine(
                Root,
                "inspect-test-android-absence.ps1");
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$HelperPath,
                    [Parameter(Mandatory = $true)][string]$ProjectRoot,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$MobileProject,
                    [Parameter(Mandatory = $true)][string]$SnapshotTempRoot
                )

                $ErrorActionPreference = 'Stop'
                $env:TEMP = $SnapshotTempRoot
                $env:TMP = $SnapshotTempRoot
                . $HelperPath
                $tokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $SourceScript,
                    [ref]$tokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join [Environment]::NewLine)
                }
                foreach ($functionName in @(
                    'Get-TestCsprojPropertyValue',
                    'Initialize-TestAndroidPackageMetadata'
                )) {
                    $functionAst = $ast.Find({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $functionName
                    }, $true)
                    . ([scriptblock]::Create($functionAst.Extent.Text))
                }

                $inspection = $null
                Initialize-TestAndroidPackageMetadata `
                    -ProjectRoot $ProjectRoot `
                    -OutputRoot $OutputRoot `
                    -MobileProject $MobileProject `
                    -InspectOnly `
                    -SnapshotReference ([ref]$inspection)
                if ($null -ne $inspection) {
                    throw 'Absent Android inspection returned a snapshot object.'
                }
                Initialize-TestAndroidPackageMetadata `
                    -ProjectRoot $ProjectRoot `
                    -OutputRoot $OutputRoot `
                    -MobileProject $MobileProject `
                    -ValidatedSnapshot $inspection `
                    -InspectOnly `
                    -SnapshotReference ([ref]$inspection)
                if ($null -ne $inspection) {
                    throw 'Repeated absent Android inspection returned a snapshot object.'
                }
                if (Test-Path -LiteralPath (
                    Join-Path $OutputRoot 'Mobile\android-package.metadata.json'
                )) {
                    throw 'Absent Android inspection wrote metadata.'
                }
                $snapshotRoots = @(
                    Get-ChildItem `
                        -LiteralPath $SnapshotTempRoot `
                        -Directory `
                        -Filter 'georaeplan-android-apk-*' `
                        -Force)
                if ($snapshotRoots.Count -ne 0) {
                    throw 'Absent Android inspection leaked a snapshot.'
                }
                Write-Output 'android_absence=PASS'
                """,
                new UTF8Encoding(false));

            return await RunPowerShellAsync(
                _powerShellPath,
                harnessPath,
                "-SourceScript",
                _preparationScriptPath,
                "-HelperPath",
                Path.Combine(
                    ProjectRoot,
                    "tools",
                    "mobile",
                    "AndroidApkMetadata.ps1"),
                "-ProjectRoot",
                ProjectRoot,
                "-OutputRoot",
                RuntimeRoot,
                "-MobileProject",
                Path.Combine(
                    ProjectRoot,
                    "Mobile",
                    "GeoraePlan.Mobile.App",
                    "GeoraePlan.Mobile.App.csproj"),
                "-SnapshotTempRoot",
                SnapshotTempRoot);
        }

        private static void WriteArchiveEntry(
            ZipArchive archive,
            string entryName,
            byte[] bytes)
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr);
}
