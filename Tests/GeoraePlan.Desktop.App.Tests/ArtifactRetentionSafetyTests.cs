using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ArtifactRetentionSafetyTests
{
    private const string SelfSourceSha256 = "037A20702248CC4ECAB13DE2F772919B91D3E08DA65660E87610B8AE095587DF";

    [Fact]
    public async Task OwnedGateInventoryAndNormalizedSourceHashAreExact()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(OwnedGateInventoryAndNormalizedSourceHashAreExact)] = 1,
            [nameof(Script_IsPowerShell51Parsable_AndDefaultsToDryRunWithStrictGuards)] = 1,
            [nameof(PathNormalizerPreservesDriveAndUncShareRootsWithoutNetworkIo)] = 2,
            [nameof(DryRunPreservesEligiblePrivateGuidArtifact_ApplyDeletesOnlyItAndPreservesEvidence)] = 1,
            [nameof(ApplyFailsClosedWhenCompletionGateIsNotSuccessful)] = 4,
            [nameof(ApplyFailsClosedForUnexpectedEntryRuntimeMarkerAndPhysicalIdentityMismatch)] = 1,
            [nameof(ApplyFailsClosedForEvidenceInsideRootHardLinkAndActiveFileLease)] = 1,
            [nameof(ApplyPreflightsEveryGuidCandidateBeforeDeletingAnyCandidate)] = 1,
            [nameof(ApplyRequiresPhysicallyBoundRetentionParentOwnerMarker)] = 1,
            [nameof(ApplyRejectsValidArtifactMetadataWhenCandidateOverlapsExplicitProtectedRoot)] = 1,
            [nameof(ApplyRejectsEvidenceInsideAnyOtherPlannedArtifactRoot)] = 1,
            [nameof(PurgeFailureAfterOneDeletionLeavesPartialQuarantineAndNeverRestoresOriginalName)] = 1,
            [nameof(ApplyFailsClosedWhileSecondProcessHoldsProducerRetentionLease)] = 1,
            [nameof(CandidatePathSwapAfterValidationQuarantinesButNeverPurgesReplacement)] = 1,
            [nameof(PostFinalValidationQuarantineSwapIsBlockedByHeldPurgeBoundary)] = 1,
            [nameof(WholeDriveProtectedRootKeepsExactVolumeRootSemantics)] = 1
        };
        var actual = typeof(ArtifactRetentionSafetyTests)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes(typeof(FactAttribute), false).Length != 0 ||
                             method.GetCustomAttributes(typeof(TheoryAttribute), false).Length != 0)
            .ToDictionary(
                method => method.Name,
                method => method.GetCustomAttributes(typeof(TheoryAttribute), false).Length == 0
                    ? 1
                    : method.GetCustomAttributes(typeof(InlineDataAttribute), false).Length,
                StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        Assert.Equal(20, actual.Values.Sum());

        var sourcePath = Path.Combine(
            FindRepositoryRoot(), "Tests", "GeoraePlan.Desktop.App.Tests", "ArtifactRetentionSafetyTests.cs");
        var source = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8);
        var normalized = Regex.Replace(
            source,
            @"SelfSourceSha256 = ""[0-9A-F]{64}""",
            "SelfSourceSha256 = \"" + new string('0', 64) + "\"",
            RegexOptions.CultureInvariant);
        Assert.Equal(SelfSourceSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    [Fact]
    public async Task Script_IsPowerShell51Parsable_AndDefaultsToDryRunWithStrictGuards()
    {
        var scriptPath = GetScriptPath();
        var source = await File.ReadAllTextAsync(scriptPath);

        Assert.Contains("[switch]$Apply", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $Apply)", source, StringComparison.Ordinal);
        Assert.Contains("[string]$AllowedParent = 'D:\\DevCaches\\georaeplan-private-artifacts'", source, StringComparison.Ordinal);
        Assert.Contains(".georaeplan-retention-parent.json", source, StringComparison.Ordinal);
        Assert.Contains(".georaeplan-retention-parent.lease", source, StringComparison.Ordinal);
        Assert.Contains("OpenParentDirectoryLease", source, StringComparison.Ordinal);
        Assert.Contains("OpenCoordinatorLease", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoProtectedRootOverlap", source, StringComparison.Ordinal);
        Assert.Contains("Assert-EvidenceOutsideAllPlannedRoots", source, StringComparison.Ordinal);
        Assert.Contains("another planned artifact root", source, StringComparison.Ordinal);
        Assert.Contains("\\A[0-9A-Fa-f]{32}\\z", source, StringComparison.Ordinal);
        Assert.Contains("Artifact root must be a direct child of AllowedParent.", source, StringComparison.Ordinal);
        Assert.Contains("rootPhysicalPath", source, StringComparison.Ordinal);
        Assert.Contains("rootVolumeSerialNumber", source, StringComparison.Ordinal);
        Assert.Contains("rootFileId", source, StringComparison.Ordinal);
        Assert.Contains("Evidence bundle must be outside the artifact root.", source, StringComparison.Ordinal);
        Assert.Contains("Artifact retention refuses a reparse point.", source, StringComparison.Ordinal);
        Assert.Contains("Artifact retention refuses a multiply-linked file.", source, StringComparison.Ordinal);
        Assert.Contains("Artifact retention found an active/process/runtime marker.", source, StringComparison.Ordinal);
        Assert.Contains("Artifact tree contains a missing or unexpected entry.", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($fresh.RootPath, $tombstonePath)", source, StringComparison.Ordinal);
        Assert.Contains("OpenPurgeLease", source, StringComparison.Ordinal);
        Assert.Contains("SetFileInformationByHandle", source, StringComparison.Ordinal);
        Assert.Contains("$volumeRoot = [IO.Path]::GetPathRoot($fullPath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.Directory]::Delete($tombstonePath, $true)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[IO.Directory]::Move($tombstonePath, $fresh.RootPath)", source, StringComparison.Ordinal);

        var parseResult = await RunPowerShellAsync(
            TimeSpan.FromSeconds(30),
            "-NoProfile",
            "-Command",
            "& { $tokens=$null; $errors=$null; " +
            "[void][Management.Automation.Language.Parser]::ParseFile(" +
            "$args[0],[ref]$tokens,[ref]$errors); " +
            "if($errors.Count){$errors | % Message | Write-Error; exit 1} }",
            scriptPath);

        Assert.True(
            parseResult.ExitCode == 0,
            $"PowerShell 5.1 parse failed. stdout={parseResult.Stdout} stderr={parseResult.Stderr}");
    }

    [Theory]
    [InlineData("D:\\", "D:\\")]
    [InlineData("\\\\retention.invalid\\private-share\\", "\\\\retention.invalid\\private-share\\")]
    public async Task PathNormalizerPreservesDriveAndUncShareRootsWithoutNetworkIo(
        string input,
        string expected)
    {
        var environment = new Dictionary<string, string?>
        {
            ["GEORAEPLAN_RETENTION_SCRIPT"] = GetScriptPath(),
            ["GEORAEPLAN_RETENTION_ROOT_INPUT"] = input
        };
        var result = await RunPowerShellAsync(
            TimeSpan.FromSeconds(30),
            environment,
            "-NoProfile",
            "-Command",
            "& { $tokens=$null; $errors=$null; " +
            "$ast=[Management.Automation.Language.Parser]::ParseFile(" +
            "$env:GEORAEPLAN_RETENTION_SCRIPT,[ref]$tokens,[ref]$errors); " +
            "$function=$ast.Find({param($node) " +
            "$node -is [Management.Automation.Language.FunctionDefinitionAst] " +
            "-and $node.Name -eq 'Get-NormalizedFullPath'},$true); " +
            "Invoke-Expression $function.Extent.Text; " +
            "[Console]::Out.Write((Get-NormalizedFullPath " +
            "-Path $env:GEORAEPLAN_RETENTION_ROOT_INPUT)) }");

        Assert.True(
            result.ExitCode == 0,
            $"Root normalization failed. stdout={result.Stdout} stderr={result.Stderr}");
        Assert.Equal(expected, result.Stdout);
    }

    [Fact]
    public async Task DryRunPreservesEligiblePrivateGuidArtifact_ApplyDeletesOnlyItAndPreservesEvidence()
    {
        using var fixture = ArtifactFixture.Create();
        var legacyRoot = Directory.CreateDirectory(
            Path.Combine(fixture.AllowedParent, "legacy-runtime-do-not-delete"));
        await File.WriteAllTextAsync(
            Path.Combine(legacyRoot.FullName, "keep.txt"),
            "legacy");

        var dryRun = await fixture.RunAsync(apply: false);

        Assert.True(
            dryRun.ExitCode == 0,
            $"Dry run failed. stdout={dryRun.Stdout} stderr={dryRun.Stderr}");
        Assert.Contains("artifact_retention=DRY_RUN", dryRun.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
        Assert.True(File.Exists(fixture.EvidencePath));
        Assert.True(Directory.Exists(legacyRoot.FullName));

        var apply = await fixture.RunAsync(apply: true);

        Assert.True(
            apply.ExitCode == 0,
            $"Apply failed. stdout={apply.Stdout} stderr={apply.Stderr}");
        Assert.Contains("artifact_retention=APPLIED", apply.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.ArtifactRoot));
        Assert.True(File.Exists(fixture.EvidencePath));
        Assert.True(Directory.Exists(legacyRoot.FullName));
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("test")]
    [InlineData("gitPush")]
    [InlineData("postflight")]
    public async Task ApplyFailsClosedWhenCompletionGateIsNotSuccessful(string failedGate)
    {
        using var fixture = ArtifactFixture.Create();
        fixture.WriteCompletion(
            outcome: failedGate == "outcome" ? "failed" : "succeeded",
            testPassed: failedGate != "test",
            gitPushPassed: failedGate != "gitPush",
            postflightPassed: failedGate != "postflight");

        var result = await fixture.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
        Assert.True(File.Exists(fixture.PayloadPath));
        Assert.True(File.Exists(fixture.EvidencePath));
    }

    [Fact]
    public async Task ApplyFailsClosedForUnexpectedEntryRuntimeMarkerAndPhysicalIdentityMismatch()
    {
        using (var unexpected = ArtifactFixture.Create())
        {
            await File.WriteAllTextAsync(
                Path.Combine(unexpected.ArtifactRoot, "unexpected.txt"),
                "must block deletion");
            var result = await unexpected.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(Directory.Exists(unexpected.ArtifactRoot));
        }

        using (var runtime = ArtifactFixture.Create())
        {
            var marker = Path.Combine(runtime.ArtifactRoot, ".georaeplan-runtime-ready");
            await File.WriteAllTextAsync(marker, "current runtime");
            runtime.AddExpectedFile(marker);
            runtime.WriteCompletion();
            var result = await runtime.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("active/process/runtime marker", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(runtime.ArtifactRoot));
        }

        using (var identity = ArtifactFixture.Create())
        {
            identity.WriteOwner(rootFileId: "0000000000000000");
            var result = await identity.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("physical identity", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(identity.ArtifactRoot));
        }
    }

    [Fact]
    public async Task ApplyFailsClosedForEvidenceInsideRootHardLinkAndActiveFileLease()
    {
        using (var insideEvidence = ArtifactFixture.Create())
        {
            var internalEvidence = Path.Combine(insideEvidence.ArtifactRoot, "evidence.zip");
            File.Copy(insideEvidence.EvidencePath, internalEvidence);
            insideEvidence.AddExpectedFile(internalEvidence);
            insideEvidence.WriteCompletion(evidencePath: internalEvidence);
            var result = await insideEvidence.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("outside the artifact root", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(insideEvidence.ArtifactRoot));
        }

        using (var hardLink = ArtifactFixture.Create())
        {
            var linkedPath = Path.Combine(hardLink.ArtifactRoot, "payload-hardlink.txt");
            CreateHardLink(linkedPath, hardLink.PayloadPath);
            hardLink.AddExpectedFile(linkedPath);
            hardLink.WriteCompletion();
            var result = await hardLink.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("multiply-linked", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(hardLink.ArtifactRoot));
        }

        using (var activeLease = ArtifactFixture.Create())
        await using (var lease = new FileStream(
            activeLease.PayloadPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            var result = await activeLease.RunAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("active lease", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(activeLease.ArtifactRoot));
        }
    }

    [Fact]
    public async Task ApplyPreflightsEveryGuidCandidateBeforeDeletingAnyCandidate()
    {
        using var fixture = ArtifactFixture.Create();
        var unownedGuidRoot = Directory.CreateDirectory(
            Path.Combine(fixture.AllowedParent, Guid.NewGuid().ToString("N")));
        await File.WriteAllTextAsync(
            Path.Combine(unownedGuidRoot.FullName, "unknown.txt"),
            "not owned by retention");

        var result = await fixture.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
        Assert.True(Directory.Exists(unownedGuidRoot.FullName));
    }

    [Fact]
    public async Task ApplyRequiresPhysicallyBoundRetentionParentOwnerMarker()
    {
        using var fixture = ArtifactFixture.Create();
        fixture.WriteParentOwner(parentFileId: "0000000000000000");

        var result = await fixture.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("retention parent", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physical identity", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
    }

    [Fact]
    public async Task ApplyRejectsValidArtifactMetadataWhenCandidateOverlapsExplicitProtectedRoot()
    {
        using var fixture = ArtifactFixture.Create();

        var result = await fixture.RunAsync(
            apply: true,
            extraArguments: ["-ProtectedRoot", fixture.ArtifactRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("overlaps a protected root", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
        Assert.True(File.Exists(fixture.PayloadPath));
    }

    [Fact]
    public async Task ApplyRejectsEvidenceInsideAnyOtherPlannedArtifactRoot()
    {
        using var primary = ArtifactFixture.Create();
        using var sibling = primary.CreateSibling();
        primary.WriteCompletion(evidencePath: sibling.PayloadPath);

        var result = await primary.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("another planned artifact root", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(primary.ArtifactRoot));
        Assert.True(Directory.Exists(sibling.ArtifactRoot));
    }

    [Fact]
    public async Task PurgeFailureAfterOneDeletionLeavesPartialQuarantineAndNeverRestoresOriginalName()
    {
        using var fixture = ArtifactFixture.Create();

        var result = await fixture.RunAsync(
            apply: true,
            extraArguments: ["-TestFaultInjection", "AfterOnePurgeEntry"],
            testFaultInjectionEnabled: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("injected purge failure", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.ArtifactRoot));
        var quarantines = Directory.GetDirectories(
            fixture.AllowedParent,
            ".georaeplan-retention-*.quarantine",
            SearchOption.TopDirectoryOnly);
        var quarantine = Assert.Single(quarantines);
        Assert.True(File.Exists(Path.Combine(
            quarantine,
            ".georaeplan-artifact-owner.json")));
        Assert.False(File.Exists(Path.Combine(quarantine, "payload", "result.txt")));
        Assert.True(File.Exists(fixture.EvidencePath));
    }

    [Fact]
    public async Task ApplyFailsClosedWhileSecondProcessHoldsProducerRetentionLease()
    {
        using var fixture = ArtifactFixture.Create();
        await using var holder = await ProducerLeaseHolder.StartAsync(
            Path.Combine(
                fixture.AllowedParent,
                ".georaeplan-retention-parent.lease"));
        Assert.Throws<IOException>(() => File.Open(
            Path.Combine(
                fixture.AllowedParent,
                ".georaeplan-retention-parent.lease"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None));

        var result = await fixture.RunAsync(apply: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("parent/producer retention lease", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
    }

    [Fact]
    public async Task CandidatePathSwapAfterValidationQuarantinesButNeverPurgesReplacement()
    {
        using var fixture = ArtifactFixture.Create();

        var result = await fixture.RunWithCandidateSwapAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("identity does not match", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(fixture.SwappedOriginalPath);
        Assert.True(Directory.Exists(fixture.SwappedOriginalPath));
        Assert.True(File.Exists(Path.Combine(
            fixture.SwappedOriginalPath!,
            "payload",
            "result.txt")));
        var quarantine = Assert.Single(Directory.GetDirectories(
            fixture.AllowedParent,
            ".georaeplan-retention-*.quarantine",
            SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(Path.Combine(quarantine, "replacement-sentinel.txt")));
    }

    [Fact]
    public async Task PostFinalValidationQuarantineSwapIsBlockedByHeldPurgeBoundary()
    {
        using var fixture = ArtifactFixture.Create();

        var result = await fixture.RunWithPostValidationSwapAttemptAsync();

        Assert.True(fixture.PostValidationSwapBlocked);
        Assert.True(
            result.ExitCode == 0,
            $"Handle-bound purge failed. stdout={result.Stdout} stderr={result.Stderr}");
        Assert.False(Directory.Exists(fixture.ArtifactRoot));
        Assert.Empty(Directory.GetDirectories(
            fixture.AllowedParent,
            ".georaeplan-retention-*.quarantine",
            SearchOption.TopDirectoryOnly));
        Assert.True(File.Exists(fixture.EvidencePath));
    }

    [Fact]
    public async Task WholeDriveProtectedRootKeepsExactVolumeRootSemantics()
    {
        const string volumeRoot = "D:\\";
        Assert.True(Directory.Exists(volumeRoot));
        using var fixture = ArtifactFixture.Create(
            Path.Combine(volumeRoot, "DevCaches"));

        var result = await fixture.RunAsync(
            apply: true,
            extraArguments: ["-ProtectedRoot", volumeRoot]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("overlaps a protected root", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(fixture.ArtifactRoot));
    }

    private static string GetScriptPath() => Path.Combine(
        FindRepositoryRoot(),
        "tools",
        "maintenance",
        "Invoke-GeoraePlanArtifactRetention.ps1");

    private static async Task<PowerShellResult> RunPowerShellAsync(
        TimeSpan timeout,
        params string[] arguments)
        => await RunPowerShellAsync(timeout, null, arguments);

    private static async Task<PowerShellResult> RunPowerShellAsync(
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new PowerShellResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
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
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static NativeIdentity GetIdentity(string path)
    {
        var handle = CreateFileW(
            path,
            0x80,
            0x1 | 0x2 | 0x4,
            IntPtr.Zero,
            3,
            0x02000000 | 0x00200000,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Fixture identity handle could not be opened.");
        }
        using (handle)
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Fixture identity could not be read.");
            return new NativeIdentity(
                information.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture),
                (((ulong)information.FileIndexHigh << 32) |
                    information.FileIndexLow).ToString("X16", CultureInfo.InvariantCulture));
        }
    }

    private static void CreateHardLink(string linkPath, string targetPath)
    {
        if (!CreateHardLinkW(linkPath, targetPath, IntPtr.Zero))
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Hard-link fixture could not be created.");
    }

    private sealed class ArtifactFixture : IDisposable
    {
        private readonly List<ExpectedEntry> expectedEntries = [];
        private readonly bool ownsTestRoot;
        private bool disposed;

        private ArtifactFixture(
            string testRoot,
            string allowedParent,
            string artifactId,
            string artifactRoot,
            string payloadPath,
            string evidencePath,
            bool ownsTestRoot)
        {
            TestRoot = testRoot;
            AllowedParent = allowedParent;
            ArtifactId = artifactId;
            ArtifactRoot = artifactRoot;
            PayloadPath = payloadPath;
            EvidencePath = evidencePath;
            this.ownsTestRoot = ownsTestRoot;
        }

        public string TestRoot { get; }
        public string AllowedParent { get; }
        public string ArtifactId { get; }
        public string ArtifactRoot { get; }
        public string PayloadPath { get; }
        public string EvidencePath { get; }
        public string? SwappedOriginalPath { get; private set; }
        public bool PostValidationSwapBlocked { get; private set; }

        public static ArtifactFixture Create(string? fixtureBase = null)
        {
            fixtureBase ??= Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            var testRoot = Path.Combine(
                fixtureBase,
                "georaeplan-artifact-retention-tests",
                Guid.NewGuid().ToString("N"));
            var allowedParent = Directory.CreateDirectory(
                Path.Combine(testRoot, "private-artifacts")).FullName;
            var artifactId = Guid.NewGuid().ToString("N");
            var artifactRoot = Directory.CreateDirectory(
                Path.Combine(allowedParent, artifactId)).FullName;
            var payloadDirectory = Directory.CreateDirectory(
                Path.Combine(artifactRoot, "payload")).FullName;
            var payloadPath = Path.Combine(payloadDirectory, "result.txt");
            File.WriteAllText(payloadPath, "verified fixture artifact", new UTF8Encoding(false));
            var evidencePath = Path.Combine(testRoot, "evidence.zip");
            File.WriteAllText(evidencePath, "preserved fixture evidence", new UTF8Encoding(false));

            var fixture = new ArtifactFixture(
                testRoot,
                allowedParent,
                artifactId,
                artifactRoot,
                payloadPath,
                evidencePath,
                ownsTestRoot: true);
            fixture.expectedEntries.Add(new ExpectedEntry("payload", "directory", null));
            fixture.expectedEntries.Add(new ExpectedEntry(
                "payload/result.txt",
                "file",
                Sha256(payloadPath)));
            fixture.WriteOwner();
            fixture.WriteParentOwner();
            fixture.WriteCompletion();
            return fixture;
        }

        public ArtifactFixture CreateSibling()
        {
            var artifactId = Guid.NewGuid().ToString("N");
            var artifactRoot = Directory.CreateDirectory(
                Path.Combine(AllowedParent, artifactId)).FullName;
            var payloadDirectory = Directory.CreateDirectory(
                Path.Combine(artifactRoot, "payload")).FullName;
            var payloadPath = Path.Combine(payloadDirectory, "result.txt");
            File.WriteAllText(
                payloadPath,
                "verified sibling fixture artifact",
                new UTF8Encoding(false));
            var evidencePath = Path.Combine(
                TestRoot,
                $"evidence-{artifactId}.zip");
            File.WriteAllText(
                evidencePath,
                "preserved sibling fixture evidence",
                new UTF8Encoding(false));
            var sibling = new ArtifactFixture(
                TestRoot,
                AllowedParent,
                artifactId,
                artifactRoot,
                payloadPath,
                evidencePath,
                ownsTestRoot: false);
            sibling.expectedEntries.Add(
                new ExpectedEntry("payload", "directory", null));
            sibling.expectedEntries.Add(new ExpectedEntry(
                "payload/result.txt",
                "file",
                Sha256(payloadPath)));
            sibling.WriteOwner();
            sibling.WriteCompletion();
            return sibling;
        }

        public void WriteParentOwner(string? parentFileId = null)
        {
            var identity = GetIdentity(AllowedParent);
            var leasePath = Path.Combine(
                AllowedParent,
                ".georaeplan-retention-parent.lease");
            if (!File.Exists(leasePath))
                File.WriteAllBytes(leasePath, []);
            WriteJson(
                Path.Combine(
                    AllowedParent,
                    ".georaeplan-retention-parent.json"),
                new
                {
                    schemaVersion = 1,
                    owner = "georaeplan-artifact-retention-parent",
                    parentId = Guid.NewGuid().ToString("N"),
                    parentPath = Path.GetFullPath(AllowedParent),
                    parentPhysicalPath = Path.GetFullPath(AllowedParent),
                    parentVolumeSerialNumber = identity.VolumeSerialNumber,
                    parentFileId = parentFileId ?? identity.FileId
                });
        }

        public void AddExpectedFile(string path)
        {
            expectedEntries.Add(new ExpectedEntry(
                Path.GetRelativePath(ArtifactRoot, path).Replace('\\', '/'),
                "file",
                Sha256(path)));
        }

        public void WriteOwner(string? rootFileId = null)
        {
            var identity = GetIdentity(ArtifactRoot);
            WriteJson(
                Path.Combine(ArtifactRoot, ".georaeplan-artifact-owner.json"),
                new
                {
                    schemaVersion = 1,
                    owner = "georaeplan-private-guid-artifact",
                    artifactId = ArtifactId,
                    createdAtUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    rootPath = Path.GetFullPath(ArtifactRoot),
                    rootPhysicalPath = Path.GetFullPath(ArtifactRoot),
                    rootVolumeSerialNumber = identity.VolumeSerialNumber,
                    rootFileId = rootFileId ?? identity.FileId
                });
        }

        public void WriteCompletion(
            string outcome = "succeeded",
            bool testPassed = true,
            bool gitPushPassed = true,
            bool postflightPassed = true,
            string? evidencePath = null)
        {
            var actualEvidencePath = evidencePath ?? EvidencePath;
            WriteJson(
                Path.Combine(ArtifactRoot, ".georaeplan-artifact-completion.json"),
                new
                {
                    schemaVersion = 1,
                    artifactId = ArtifactId,
                    outcome,
                    testGate = new { passed = testPassed },
                    gitPushGate = new
                    {
                        passed = gitPushPassed,
                        commitSha = new string('a', 40),
                        remote = "origin"
                    },
                    postflightGate = new { passed = postflightPassed },
                    evidenceBundle = new
                    {
                        path = Path.GetFullPath(actualEvidencePath),
                        sha256 = Sha256(actualEvidencePath)
                    },
                    expectedEntries = expectedEntries.OrderBy(entry => entry.relativePath).ToArray()
                });
        }

        public Task<PowerShellResult> RunAsync(
            bool apply,
            string[]? extraArguments = null,
            bool testFaultInjectionEnabled = false)
        {
            var arguments = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                GetScriptPath(),
                "-AllowedParent",
                AllowedParent
            };
            if (apply)
                arguments.Add("-Apply");
            if (extraArguments is not null)
                arguments.AddRange(extraArguments);
            var environment = testFaultInjectionEnabled
                ? new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_ARTIFACT_RETENTION_TEST_MODE"] = "1"
                }
                : null;
            return RunPowerShellAsync(
                TimeSpan.FromSeconds(45),
                environment,
                arguments.ToArray());
        }

        public async Task<PowerShellResult> RunWithCandidateSwapAsync()
        {
            var executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                GetScriptPath(), "-AllowedParent", AllowedParent, "-Apply",
                "-TestFaultInjection", "BeforeCandidateMove"
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["GEORAEPLAN_ARTIFACT_RETENTION_TEST_MODE"] = "1";
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Swap fixture process did not start.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = new StringBuilder();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            var hookObserved = false;
            while (!hookObserved && DateTime.UtcNow < deadline)
            {
                var lineTask = process.StandardOutput.ReadLineAsync();
                var completed = await Task.WhenAny(
                    lineTask,
                    Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed != lineTask)
                    continue;
                var line = await lineTask;
                if (line is null)
                    break;
                output.AppendLine(line);
                hookObserved = line.Contains(
                    "artifact_retention_test_hook=before_candidate_move",
                    StringComparison.Ordinal);
            }
            if (!hookObserved)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new InvalidOperationException(
                    "Candidate swap hook was not observed. stderr=" +
                    await stderrTask);
            }

            SwappedOriginalPath = Path.Combine(
                TestRoot,
                "validated-original-" + ArtifactId);
            Directory.Move(ArtifactRoot, SwappedOriginalPath);
            Directory.CreateDirectory(ArtifactRoot);
            await File.WriteAllTextAsync(
                Path.Combine(ArtifactRoot, "replacement-sentinel.txt"),
                "must never be purged");

            output.Append(await process.StandardOutput.ReadToEndAsync());
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            return new PowerShellResult(
                process.ExitCode,
                output.ToString(),
                await stderrTask);
        }

        public async Task<PowerShellResult> RunWithPostValidationSwapAttemptAsync()
        {
            var startInfo = CreateFaultProcessStartInfo(
                "AfterFinalQuarantineValidation");
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Post-validation swap fixture process did not start.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            var output = new StringBuilder();
            var hookObserved = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!hookObserved && DateTime.UtcNow < deadline)
            {
                var lineTask = process.StandardOutput.ReadLineAsync();
                var completed = await Task.WhenAny(
                    lineTask,
                    Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed != lineTask)
                    continue;
                var line = await lineTask;
                if (line is null)
                    break;
                output.AppendLine(line);
                hookObserved = line.Contains(
                    "artifact_retention_test_hook=after_final_quarantine_validation",
                    StringComparison.Ordinal);
            }
            if (!hookObserved)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new InvalidOperationException(
                    "Post-validation swap hook was not observed. stderr=" +
                    await stderrTask);
            }

            var quarantine = Assert.Single(Directory.GetDirectories(
                AllowedParent,
                ".georaeplan-retention-*.quarantine",
                SearchOption.TopDirectoryOnly));
            var escaped = Path.Combine(
                TestRoot,
                "escaped-quarantine-" + ArtifactId);
            try
            {
                Directory.Move(quarantine, escaped);
                Directory.CreateDirectory(quarantine);
                await File.WriteAllTextAsync(
                    Path.Combine(quarantine, "post-validation-replacement.txt"),
                    "must not be purged");
                PostValidationSwapBlocked = false;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                PostValidationSwapBlocked = true;
            }

            output.Append(await process.StandardOutput.ReadToEndAsync());
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            return new PowerShellResult(
                process.ExitCode,
                output.ToString(),
                await stderrTask);
        }

        private ProcessStartInfo CreateFaultProcessStartInfo(string fault)
        {
            var executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[]
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                GetScriptPath(), "-AllowedParent", AllowedParent, "-Apply",
                "-TestFaultInjection", fault
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.Environment["GEORAEPLAN_ARTIFACT_RETENTION_TEST_MODE"] = "1";
            return startInfo;
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                if (ownsTestRoot && Directory.Exists(TestRoot))
                    Directory.Delete(TestRoot, recursive: true);
            }
            catch
            {
                // A failed product assertion must remain the primary test result.
            }
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    value,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
    }

    private sealed class ProducerLeaseHolder : IAsyncDisposable
    {
        private readonly Process process;

        private ProducerLeaseHolder(Process process) => this.process = process;

        public static async Task<ProducerLeaseHolder> StartAsync(string leasePath)
        {
            var executablePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$path=[Environment]::GetEnvironmentVariable(" +
                "'GEORAEPLAN_TEST_RETENTION_LEASE_PATH');" +
                "$lease=[IO.File]::Open($path,[IO.FileMode]::Open," +
                "[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);" +
                "[Console]::Out.WriteLine('READY');[Console]::Out.Flush();" +
                "Start-Sleep -Seconds 30");
            startInfo.Environment["GEORAEPLAN_TEST_RETENTION_LEASE_PATH"] = leasePath;
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Producer lease holder did not start.");
            var readyTask = process.StandardOutput.ReadLineAsync();
            var completed = await Task.WhenAny(
                readyTask,
                Task.Delay(TimeSpan.FromSeconds(10)));
            if (completed != readyTask || await readyTask != "READY")
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    "Producer lease holder did not become ready: " +
                    await process.StandardError.ReadToEndAsync());
            }
            return new ProducerLeaseHolder(process);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
    }

    private sealed record ExpectedEntry(
        string relativePath,
        string kind,
        string? sha256);

    private sealed record NativeIdentity(
        string VolumeSerialNumber,
        string FileId);

    private sealed record PowerShellResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
