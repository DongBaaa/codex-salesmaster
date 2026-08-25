using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ArtifactRetentionProducerProvisioningTests
{
    [Fact]
    public async Task RedContract_ProducerScriptsRequirePinnedHandlesTypedResultsStagingAndManualOnlyQuarantine()
    {
        var common = await File.ReadAllTextAsync(Script("GeoraePlanArtifactRetentionProducer.Common.ps1"));
        var provisioner = await File.ReadAllTextAsync(Script("Initialize-GeoraePlanArtifactRetentionParent.ps1"));
        var candidate = await File.ReadAllTextAsync(Script("New-GeoraePlanArtifactRetentionCandidate.ps1"));
        var finalizer = await File.ReadAllTextAsync(Script("Complete-GeoraePlanArtifactRetentionCandidate.ps1"));
        var inspector = await File.ReadAllTextAsync(Script("Get-GeoraePlanArtifactRetentionQuarantine.ps1"));

        foreach (var path in new[]
        {
            Script("GeoraePlanArtifactRetentionProducer.Common.ps1"),
            Script("Initialize-GeoraePlanArtifactRetentionParent.ps1"),
            Script("New-GeoraePlanArtifactRetentionCandidate.ps1"),
            Script("Complete-GeoraePlanArtifactRetentionCandidate.ps1"),
            Script("Get-GeoraePlanArtifactRetentionQuarantine.ps1")
        })
            Assert.True((await ParsePowerShell51Async(path)).ExitCode == 0, path);

        Assert.Contains("CreateNew", provisioner, StringComparison.Ordinal);
        Assert.Contains("producer-stage", candidate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OpenStableDirectory", common, StringComparison.Ordinal);
        Assert.Contains("NtQueryDirectoryFile", common, StringComparison.Ordinal);
        Assert.DoesNotContain("ShareDelete", common, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenForPublish", common, StringComparison.Ordinal);
        Assert.DoesNotContain(".georaeplan.pending", common + provisioner + candidate + finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("RenameRelative", common, StringComparison.Ordinal);
        Assert.Contains("MutateStageMarkerBeforeHandleBoundPublish", candidate, StringComparison.Ordinal);
        Assert.Contains("FinalizeJournal", finalizer, StringComparison.Ordinal);
        Assert.Contains("CreateNew", finalizer, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory=$true)][string]$StagePath", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory]::Move", candidate, StringComparison.Ordinal);
        Assert.Contains("SetOwner", common, StringComparison.Ordinal);
        Assert.Contains("git check-ref-format", finalizer, StringComparison.Ordinal);
        Assert.Contains("git ls-remote --exit-code", finalizer, StringComparison.Ordinal);
        Assert.Contains("GIT_CONFIG_NOSYSTEM", finalizer, StringComparison.Ordinal);
        Assert.Contains("Environment]::SystemDirectory", finalizer, StringComparison.Ordinal);
        Assert.Contains("ProgramFiles", finalizer, StringComparison.Ordinal);
        Assert.Contains("total -ne 20", finalizer, StringComparison.Ordinal);
        Assert.Contains("@('vstest'", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("@('test'", finalizer, StringComparison.Ordinal);
        Assert.Contains("ArtifactRetentionSafetyTests.cs", finalizer, StringComparison.Ordinal);
        Assert.Contains("extensions.worktreeConfig=false", finalizer, StringComparison.Ordinal);
        Assert.Contains("closureManifestSha256", finalizer, StringComparison.Ordinal);
        var producerTestSource = await File.ReadAllTextAsync(GetType().Assembly.Location.Replace("bin\\Release\\net8.0-windows\\GeoraePlan.Desktop.App.Tests.dll", "ArtifactRetentionProducerProvisioningTests.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Create" + "JobObject", producerTestSource, StringComparison.Ordinal);
        Assert.Contains("JOB_OBJECT_LIMIT_" + "KILL_ON_JOB_CLOSE", producerTestSource, StringComparison.Ordinal);
        Assert.Contains("QueryInformation" + "JobObject", producerTestSource, StringComparison.Ordinal);
        Assert.Contains("Create" + "ProcessW", producerTestSource, StringComparison.Ordinal);
        Assert.Contains("CREATE_" + "SUSPENDED", producerTestSource, StringComparison.Ordinal);
        Assert.Contains("Resume" + "Thread", producerTestSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Process." + "Start(start)", producerTestSource, StringComparison.Ordinal);
        var closureManifestPath = Script("GeoraePlanArtifactRetentionTestClosureManifest.json");
        Assert.True(File.Exists(closureManifestPath));
        var closureManifestBytes = await File.ReadAllBytesAsync(closureManifestPath);
        var closureManifestSha256 = Convert.ToHexString(SHA256.HashData(closureManifestBytes));
        Assert.Contains($"GeoraePlanArtifactRetentionTestClosureManifestSha256='{closureManifestSha256}'", finalizer, StringComparison.Ordinal);
        using (var closureManifest = JsonDocument.Parse(closureManifestBytes))
        {
            Assert.Equal(
                new[] { "entries", "kind", "outputDirectoryCount", "outputFileCount", "schemaVersion" },
                closureManifest.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(1, closureManifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("georaeplan-artifact-retention-test-closure-v1", closureManifest.RootElement.GetProperty("kind").GetString());
            Assert.Equal(209, closureManifest.RootElement.GetProperty("outputFileCount").GetInt32());
            Assert.Equal(65, closureManifest.RootElement.GetProperty("outputDirectoryCount").GetInt32());
            var entries = closureManifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
            Assert.Equal(276, entries.Length);
            var relativePaths = entries.Select(entry => entry.GetProperty("relativePath").GetString()!).ToArray();
            Assert.Equal(relativePaths.Order(StringComparer.Ordinal).ToArray(), relativePaths);
            Assert.Equal(relativePaths.Length, relativePaths.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("source/ArtifactRetentionSafetyTests.cs", relativePaths);
            Assert.Contains("source/Invoke-GeoraePlanArtifactRetention.ps1", relativePaths);
        }
        Assert.Contains("Get-GeoraePlanArtifactRetentionExactClosurePlan", finalizer, StringComparison.Ordinal);
        Assert.Contains("Assert-GeoraePlanArtifactRetentionExactClosurePlan", finalizer, StringComparison.Ordinal);
        Assert.Contains("New-GeoraePlanArtifactRetentionHeldStageCleanupPlan", common, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-GeoraePlanArtifactRetentionHeldStageCleanupPending", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("TestResultPath", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("PostflightResultPath", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("TestFixtureExecutablePath", finalizer, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~ArtifactRetentionSafetyTests", finalizer, StringComparison.Ordinal);
        Assert.Contains("EvidenceOutputPath", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("RemotePushProof", finalizer, StringComparison.Ordinal);
        Assert.DoesNotContain("ScopedGitProof", finalizer, StringComparison.Ordinal);
        Assert.Contains("retryCommand = $null", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedGate_FinalizerAcceptsOnlyBoundCanonicalResultsAndExactLocalRemote()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();

        var happy = await fixture.FinalizeAsync(apply: true);
        Assert.True(happy.ExitCode == 0, $"Finalize failed. stdout={happy.Stdout} stderr={happy.Stderr}");
        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.Equal("stable payload", await File.ReadAllTextAsync(Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt")));
        var producerManifest = await File.ReadAllTextAsync(fixture.ManifestPath);
        Assert.DoesNotContain(fixture.BareRemote, producerManifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", producerManifest, StringComparison.Ordinal);
        var consumer = await fixture.RunRetentionConsumerAsync();
        Assert.Equal(0, consumer.ExitCode);
        Assert.Contains("artifact_retention=DRY_RUN candidate_count=1", consumer.Stdout, StringComparison.Ordinal);
        Assert.Contains($"artifact_id={fixture.ArtifactId}", consumer.Stdout, StringComparison.OrdinalIgnoreCase);

        using (var fileRemote = await SecureFixture.CreateAsync())
        {
            await fileRemote.ProvisionAndCreateCandidateAsync();
            var fileRemoteResult = await fileRemote.FinalizeAsync(apply: true, invalid: "safe-file");
            Assert.True(fileRemoteResult.ExitCode == 0, $"file:// remote finalize failed. stdout={fileRemoteResult.Stdout} stderr={fileRemoteResult.Stderr}");
            Assert.DoesNotContain(new Uri(fileRemote.BareRemote).AbsoluteUri, await File.ReadAllTextAsync(fileRemote.ManifestPath), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(new Uri(fileRemote.BareRemote).AbsoluteUri, fileRemoteResult.Stdout + fileRemoteResult.Stderr, StringComparison.OrdinalIgnoreCase);
        }

        using (var swappedConfig = await SecureFixture.CreateAsync())
        {
            await swappedConfig.ProvisionAndCreateCandidateAsync();
            var swapped = await swappedConfig.FinalizeAsync(apply: true, testFault: "SwapRemoteConfigBeforeLsRemote");
            Assert.True(swapped.ExitCode == 0, $"Captured remote URL was not stable across config replacement. stdout={swapped.Stdout} stderr={swapped.Stderr}");
            Assert.DoesNotContain("invalid.example", swapped.Stdout + swapped.Stderr, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var invalid in new[] { "wrong-ref", "credential-remote", "query-remote", "fragment-remote" })
        {
            using var negative = await SecureFixture.CreateAsync();
            await negative.ProvisionAndCreateCandidateAsync();
            var result = await negative.FinalizeAsync(apply: true, invalid: invalid);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(negative.CompletionPath));
            if (invalid is "query-remote" or "fragment-remote")
                Assert.DoesNotContain("fixture-nonsecret-marker", result.Stdout + result.Stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RedGate_StagingFailureLeavesNoGuidCandidate_AndExactResumeOnlyCompletesMatchingManifest()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAsync();

        var stagedFailure = await fixture.RunAsync(
            "New-GeoraePlanArtifactRetentionCandidate.ps1",
            "-ArtifactId", fixture.ArtifactId,
            "-StagePath", fixture.StagePath,
            "-Apply",
            "-TestFaultInjection", "BeforeHandleBoundPublish");
        Assert.NotEqual(0, stagedFailure.ExitCode);
        Assert.False(Directory.Exists(fixture.CandidatePath));
        Assert.Empty(Directory.GetDirectories(fixture.AllowedParent, "????????????????????????????????"));

        await fixture.CreateCandidateAsync();
        var first = await fixture.FinalizeAsync(apply: true, leaveCompletionMissing: true);
        Assert.NotEqual(0, first.ExitCode);
        Assert.True(File.Exists(fixture.ParentFinalizeJournalPath), $"Faulted finalize did not publish its journal. stdout={first.Stdout} stderr={first.Stderr}");
        Assert.False(File.Exists(fixture.CompletionPath));

        var resumed = await fixture.FinalizeAsync(apply: true);
        Assert.True(resumed.ExitCode == 0, $"Resume failed. stdout={resumed.Stdout} stderr={resumed.Stderr}");
        Assert.True(File.Exists(fixture.CompletionPath));
        var consumer = await fixture.RunRetentionConsumerAsync();
        Assert.Equal(0, consumer.ExitCode);
        Assert.Contains("artifact_retention=DRY_RUN candidate_count=1", consumer.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedGate_RejectsAclBroadPrincipalDenyAndPinnedParentCandidateOrEvidenceLease()
    {
        await AssertAclMutationIsRejectedAsync(
            new SecurityIdentifier("S-1-5-21-2147483000-2147483001-2147483002-4242"),
            AccessControlType.Allow);
        await AssertAclMutationIsRejectedAsync(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            AccessControlType.Deny);

        using (var lease = await SecureFixture.CreateAsync())
        {
            await lease.ProvisionAndCreateCandidateAsync();
            using (var noDeleteProbe = new FileStream(lease.ParentLeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                Assert.Throws<IOException>(() => File.Delete(lease.ParentLeasePath));
            await using var parentHandle = new FileStream(lease.ParentLeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await lease.FinalizeAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(lease.CompletionPath));
        }
    }

    [Fact]
    public async Task RedGate_HandleBoundRenamePublishesOnlyHeldStageAndPreservesEvidence()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAsync();
        var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SentinelPath)));

        await fixture.CreateCandidateAsync();
        var publish = await fixture.FinalizeAsync(apply: true, testFault: "SwapStageNameBeforePublish");
        Assert.True(publish.ExitCode == 0, $"Swap finalize failed. stdout={publish.Stdout} stderr={publish.Stderr}");
        Assert.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SentinelPath))));
        Assert.True(File.Exists(Path.Combine(fixture.CandidatePath, ".georaeplan-artifact-owner.json")));
        Assert.False(Directory.Exists(fixture.StagePath));
        Assert.False(Directory.Exists(fixture.StagePath + ".swapped"));
    }

    [Fact]
    public async Task RedGate_InPlaceStageMetadataMutationBeforePublishFailsClosedWithoutGuid()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAsync();
        var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SentinelPath)));

        await fixture.CreateCandidateAsync();
        var result = await fixture.FinalizeAsync(apply: true, testFault: "MutateStageMarkerBeforeHandleBoundPublish");
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(fixture.CandidatePath));
        Assert.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SentinelPath))));
    }

    [Fact]
    public async Task BootstrapAndFinalizeJournalMutationOrAclBroadeningFailsClosedAndRetriesExactly()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAsync();

        var bootstrapFile = new FileInfo(fixture.ParentBootstrapPath);
        var bootstrapAcl = bootstrapFile.GetAccessControl();
        var broadenedBootstrapAcl = bootstrapFile.GetAccessControl();
        broadenedBootstrapAcl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        bootstrapFile.SetAccessControl(broadenedBootstrapAcl);
        try
        {
            var aclRejected = await fixture.RunAsync(
                "New-GeoraePlanArtifactRetentionCandidate.ps1",
                "-ArtifactId", fixture.ArtifactId,
                "-StagePath", fixture.StagePath,
                "-Apply");
            Assert.NotEqual(0, aclRejected.ExitCode);
            Assert.False(Directory.Exists(fixture.StagePath));
        }
        finally { RestoreSecurity(bootstrapFile, bootstrapAcl); }

        var bootstrapMutation = await fixture.RunAsync(
            "New-GeoraePlanArtifactRetentionCandidate.ps1",
            "-ArtifactId", fixture.ArtifactId,
            "-StagePath", fixture.StagePath,
            "-Apply",
            "-TestFaultInjection", "MutateBootstrapBeforeStageCreate");
        Assert.NotEqual(0, bootstrapMutation.ExitCode);
        Assert.False(Directory.Exists(fixture.StagePath));

        await fixture.CreateCandidateAsync();
        var journalMutation = await fixture.FinalizeAsync(apply: true, testFault: "MutateFinalizeJournalBeforeMaterialization");
        Assert.NotEqual(0, journalMutation.ExitCode);
        Assert.True(File.Exists(fixture.ParentFinalizeJournalPath));
        Assert.False(Directory.Exists(fixture.CandidatePath));

        var journalFile = new FileInfo(fixture.ParentFinalizeJournalPath);
        var originalAcl = journalFile.GetAccessControl();
        var mutatedAcl = journalFile.GetAccessControl();
        mutatedAcl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        journalFile.SetAccessControl(mutatedAcl);
        try
        {
            var rejected = await fixture.FinalizeAsync(apply: true);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.False(Directory.Exists(fixture.CandidatePath));
        }
        finally { RestoreSecurity(journalFile, originalAcl); }

        var recovered = await fixture.FinalizeAsync(apply: true);
        Assert.Equal(0, recovered.ExitCode);
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    [Fact]
    public async Task ExistingCompletionMustRemainExactAndMatchExpectedTreeOnRetry()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await fixture.FinalizeAsync(apply: true)).ExitCode);

        var exact = await File.ReadAllTextAsync(fixture.CompletionPath);
        await File.WriteAllTextAsync(fixture.CompletionPath, "{\"extra\":true," + exact[1..], new UTF8Encoding(false));
        var extra = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, extra.ExitCode);

        await File.WriteAllTextAsync(fixture.CompletionPath, exact, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt"), "tampered", new UTF8Encoding(false));
        var treeMismatch = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, treeMismatch.ExitCode);
    }

    [Fact]
    public async Task ForeignPrivateParentOrEmptyStageIsNeverAdoptedOrMutated()
    {
        using (var foreignParent = await SecureFixture.CreateAsync())
        {
            Directory.CreateDirectory(foreignParent.AllowedParent);
            SetExactPrivateDirectoryAcl(foreignParent.AllowedParent);
            await File.WriteAllTextAsync(foreignParent.ParentBootstrapPath, "foreign-bootstrap", new UTF8Encoding(false));
            SetExactPrivateFileAcl(foreignParent.ParentBootstrapPath);
            var rootSddl = GetSddl(new DirectoryInfo(foreignParent.AllowedParent));
            var bootstrapSddl = GetSddl(new FileInfo(foreignParent.ParentBootstrapPath));
            var bootstrapBytes = await File.ReadAllBytesAsync(foreignParent.ParentBootstrapPath);

            var rejected = await foreignParent.RunAsync("Initialize-GeoraePlanArtifactRetentionParent.ps1", "-Apply");
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(foreignParent.AllowedParent));
            Assert.Equal(bootstrapBytes, await File.ReadAllBytesAsync(foreignParent.ParentBootstrapPath));
            Assert.Equal(rootSddl, GetSddl(new DirectoryInfo(foreignParent.AllowedParent)));
            Assert.Equal(bootstrapSddl, GetSddl(new FileInfo(foreignParent.ParentBootstrapPath)));
        }

        using (var foreignStage = await SecureFixture.CreateAsync())
        {
            await foreignStage.ProvisionAsync();
            Directory.CreateDirectory(foreignStage.StagePath);
            SetExactPrivateDirectoryAcl(foreignStage.StagePath);
            var stageSddl = GetSddl(new DirectoryInfo(foreignStage.StagePath));
            var rejected = await foreignStage.RunAsync(
                "New-GeoraePlanArtifactRetentionCandidate.ps1",
                "-ArtifactId", foreignStage.ArtifactId,
                "-StagePath", foreignStage.StagePath,
                "-Apply");
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(foreignStage.StagePath));
            Assert.Equal(stageSddl, GetSddl(new DirectoryInfo(foreignStage.StagePath)));
        }
    }

    [Fact]
    public async Task TornDurableFilesAreExactPrefixesAndRetryPublishesValidCompletionLast()
    {
        foreach (var fault in new[] { "PartialFinalizeJournalOneByte", "PartialPayloadOneByte", "PartialOwnerTail", "PartialCompletionOneByte" })
        {
            using var fixture = await SecureFixture.CreateAsync();
            await fixture.ProvisionAndCreateCandidateAsync();
            var first = await fixture.FinalizeAsync(apply: true, testFault: fault);
            Assert.NotEqual(0, first.ExitCode);
            if (fault == "PartialCompletionOneByte")
            {
                Assert.Equal(1, new FileInfo(fixture.CompletionPath).Length);
                Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(File.ReadAllBytes(fixture.CompletionPath)));
                Assert.NotEqual(0, (await fixture.RunRetentionConsumerAsync()).ExitCode);
            }
            else
                Assert.False(File.Exists(fixture.CompletionPath));
            var torn = fault switch
            {
                "PartialFinalizeJournalOneByte" => fixture.ParentFinalizeJournalPath,
                "PartialPayloadOneByte" => Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt"),
                "PartialOwnerTail" => Path.Combine(fixture.CandidatePath, ".georaeplan-artifact-owner.json"),
                _ => fixture.CompletionPath
            };
            Assert.True(File.Exists(torn), $"Expected bounded torn durable file for {fault}.");
            var tornLength = new FileInfo(torn).Length;
            if (fault.EndsWith("OneByte", StringComparison.Ordinal)) Assert.Equal(1, tornLength);
            else Assert.True(tornLength > 1);

            var retry = await fixture.FinalizeAsync(apply: true);
            Assert.True(retry.ExitCode == 0, $"Exact-prefix retry failed for {fault}. stdout={retry.Stdout} stderr={retry.Stderr}");
            Assert.True(File.Exists(fixture.CompletionPath));
            var published = fault switch
            {
                "PartialFinalizeJournalOneByte" => fixture.ParentFinalizeJournalPath,
                "PartialPayloadOneByte" => Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt"),
                "PartialOwnerTail" => Path.Combine(fixture.CandidatePath, ".georaeplan-artifact-owner.json"),
                _ => fixture.CompletionPath
            };
            Assert.True(new FileInfo(published).Length > tornLength);
            if (fault == "PartialOwnerTail") Assert.Equal(tornLength + 1, new FileInfo(published).Length);
            var consumer = await fixture.RunRetentionConsumerAsync();
            Assert.Equal(0, consumer.ExitCode);
            Assert.Contains("artifact_retention=DRY_RUN candidate_count=1", consumer.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FinalizerRunsItsOwnTestGate_ThenStandaloneConsumer_AndRemovesStage()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        await fixture.WriteCallerAuthoredPassingEvidenceAsync();

        var rejected = await fixture.FinalizeAsync(apply: true, testFault: "RejectOwnedTestGateAfterExecution");
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.True(
            rejected.Stdout.Contains("artifact_retention_test_gate=PASSED", StringComparison.Ordinal),
            $"Owned gate trace missing. stdout={rejected.Stdout} stderr={rejected.Stderr}");
        Assert.False(File.Exists(fixture.CompletionPath));
        Assert.False(File.Exists(fixture.ParentFinalizeJournalPath));
        Assert.True(Directory.Exists(fixture.StagePath));

        var completed = await fixture.FinalizeAsync(apply: true);
        Assert.True(completed.ExitCode == 0, $"Finalize failed. stdout={completed.Stdout} stderr={completed.Stderr}");
        Assert.Contains("artifact_retention_test_gate=PASSED total=20 passed=20 failed=0 skipped=0", completed.Stdout, StringComparison.Ordinal);
        Assert.Contains($"artifact_retention=DRY_RUN artifact_id={fixture.ArtifactId}", completed.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact_retention=DRY_RUN candidate_count=1", completed.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.StagePath));
        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.True(File.Exists(fixture.ParentFinalizeJournalPath));

        var apply = await fixture.RunRetentionConsumerAsync(apply: true);
        Assert.Equal(0, apply.ExitCode);
        Assert.False(Directory.Exists(fixture.CandidatePath));
        Assert.False(Directory.Exists(fixture.StagePath));
    }

    [Fact]
    public async Task ConsumerContractIsRejectedBeforeJournalOrGuidMutation_AndMetadataBoundaryIsExact()
    {
        var common = Script("GeoraePlanArtifactRetentionProducer.Common.ps1");
        var boundary = await RunPowerShellAsync(
            "-NoProfile", "-Command",
            "& { . $args[0]; Assert-GeoraePlanArtifactRetentionConsumerMetadataBytes ([byte[]]::new(65536)); try { Assert-GeoraePlanArtifactRetentionConsumerMetadataBytes ([byte[]]::new(65537)); exit 9 } catch { exit 0 } }",
            common);
        Assert.Equal(0, boundary.ExitCode);

        foreach (var reserved in new[] { "RUN-ALL.CMD", "payload.PID", "nested.LEASE", ".georaeplan-artifact-owner.json" })
        {
            using var fixture = await SecureFixture.CreateAsync();
            await fixture.ProvisionAndCreateCandidateAsync();
            var path = Path.Combine(fixture.StagePath, reserved.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "reserved", new UTF8Encoding(false));
            var result = await fixture.FinalizeAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(fixture.ParentFinalizeJournalPath));
            Assert.False(Directory.Exists(fixture.CandidatePath));
        }

        using (var many = await SecureFixture.CreateAsync())
        {
            await many.ProvisionAndCreateCandidateAsync();
            var bulk = Path.Combine(many.StagePath, "bulk");
            Directory.CreateDirectory(bulk);
            for (var index = 0; index != 600; index++)
                await File.WriteAllTextAsync(Path.Combine(bulk, $"entry-{index:D4}-{new string('x', 48)}.txt"), "x", new UTF8Encoding(false));
            var result = await many.FinalizeAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(many.ParentFinalizeJournalPath));
            Assert.False(Directory.Exists(many.CandidatePath));
        }
    }

    [Fact]
    public async Task TestGateJsonTypesAndGitInsteadOfRewriteAreFailClosedBeforeMutation()
    {
        var typedResult = await RunPowerShellAsync(
            "-NoProfile", "-Command",
            "& { . $args[0]; try { Assert-GeoraePlanArtifactRetentionJsonNonNegativeInteger (,@('3')) 'single string array'; exit 9 } catch { exit 0 } }",
            Script("GeoraePlanArtifactRetentionProducer.Common.ps1"));
        Assert.Equal(0, typedResult.ExitCode);

        foreach (var unsafeKind in new[] { "insteadof", "pushinsteadof", "includeif" })
        {
            using var redirected = await SecureFixture.CreateAsync();
            await redirected.ProvisionAndCreateCandidateAsync();
            var isolatedValue = Path.Combine(redirected.EvidenceRoot, "isolated-config-value");
            var unsafeKey = unsafeKind switch
            {
                "includeif" => "includeif.onbranch:main.path",
                _ => $"url.{isolatedValue}.{unsafeKind}"
            };
            Assert.Equal(0, (await redirected.GitAsync("config", unsafeKey, isolatedValue)).ExitCode);
            var result = await redirected.FinalizeAsync(apply: true);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(redirected.ParentFinalizeJournalPath));
            Assert.False(Directory.Exists(redirected.CandidatePath));
            Assert.DoesNotContain(isolatedValue, result.Stdout + result.Stderr, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ExistingCompletionIsAuditedBeforeAnyPrefixRepair_AndRetryIsPostflightOnly()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await fixture.FinalizeAsync(apply: true)).ExitCode);
        Assert.False(Directory.Exists(fixture.StagePath));

        var payload = Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt");
        var exactPayload = await File.ReadAllBytesAsync(payload);
        await File.WriteAllBytesAsync(payload, exactPayload[..1]);
        var payloadPrefix = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, payloadPrefix.ExitCode);
        Assert.Equal(exactPayload[..1], await File.ReadAllBytesAsync(payload));

        await File.WriteAllBytesAsync(payload, exactPayload);
        var exactCompletion = await File.ReadAllBytesAsync(fixture.CompletionPath);
        await File.WriteAllBytesAsync(fixture.CompletionPath, exactCompletion[..1]);
        await File.WriteAllBytesAsync(payload, exactPayload[..1]);
        var bothPrefix = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, bothPrefix.ExitCode);
        Assert.Equal(exactCompletion[..1], await File.ReadAllBytesAsync(fixture.CompletionPath));
        Assert.Equal(exactPayload[..1], await File.ReadAllBytesAsync(payload));
    }

    [Fact]
    public async Task ToolExecutionIsHermetic_AndGitConfigCannotChangeAfterInspection()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        var shadow = Path.Combine(fixture.EvidenceRoot, "shadow-bin");
        Directory.CreateDirectory(shadow);
        var shadowMarker = Path.Combine(shadow, "used.marker");
        await File.WriteAllTextAsync(
            Path.Combine(shadow, "dotnet.cmd"),
            $"@echo off\r\necho used>\"{shadowMarker}\"\r\necho Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1\r\nexit /b 0\r\n",
            new UTF8Encoding(false));
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = shadow + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        };
        var shadowed = await fixture.FinalizeAsync(apply: true, environment: environment);
        Assert.True(shadowed.ExitCode == 0, $"Hermetic tool run failed. stdout={shadowed.Stdout} stderr={shadowed.Stderr}");
        Assert.Contains("total=20 passed=20 failed=0 skipped=0", shadowed.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(shadowMarker));

        using var swapped = await SecureFixture.CreateAsync();
        await swapped.ProvisionAndCreateCandidateAsync();
        var configPath = Path.Combine(swapped.RepositoryRoot, ".git", "config");
        var before = await File.ReadAllBytesAsync(configPath);
        var result = await swapped.FinalizeAsync(apply: true, testFault: "SwapRemoteConfigBeforeLsRemote");
        Assert.True(result.ExitCode == 0, $"Pinned config finalize failed. stdout={result.Stdout} stderr={result.Stderr}");
        Assert.Equal(before, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task ExistingCompletionCrossBindsRunRepositoryAndManifestBeforePostflight()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await fixture.FinalizeAsync(apply: true)).ExitCode);
        var completion = await File.ReadAllBytesAsync(fixture.CompletionPath);
        var payload = Path.Combine(fixture.CandidatePath, "payload", "sub", "artifact.txt");
        var payloadBytes = await File.ReadAllBytesAsync(payload);

        var changedRun = await fixture.FinalizeAsync(apply: true, producerRunId: Guid.NewGuid().ToString("N"));
        Assert.NotEqual(0, changedRun.ExitCode);
        Assert.Equal(completion, await File.ReadAllBytesAsync(fixture.CompletionPath));
        Assert.Equal(payloadBytes, await File.ReadAllBytesAsync(payload));

        var missingRepository = Path.Combine(fixture.Root, "missing-repository");
        var changedRepository = await fixture.FinalizeAsync(apply: true, repositoryRoot: missingRepository);
        Assert.NotEqual(0, changedRepository.ExitCode);
        Assert.Equal(completion, await File.ReadAllBytesAsync(fixture.CompletionPath));
    }

    [Fact]
    public async Task MultipleValidCandidatesAndCleanupFailureRetryUseConsumerPostflightOnly()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await fixture.FinalizeAsync(apply: true)).ExitCode);

        var secondId = Guid.NewGuid().ToString("N");
        var secondRun = Guid.NewGuid().ToString("N");
        var secondStage = Path.Combine(fixture.AllowedParent, ".georaeplan-producer-stage-" + secondId);
        Assert.Equal(0, (await fixture.RunAsync("New-GeoraePlanArtifactRetentionCandidate.ps1", "-ArtifactId", secondId, "-StagePath", secondStage, "-Apply")).ExitCode);
        Directory.CreateDirectory(Path.Combine(secondStage, "payload"));
        await File.WriteAllTextAsync(Path.Combine(secondStage, "payload", "second.txt"), "second", new UTF8Encoding(false));
        var second = await fixture.RunAsync(
            "Complete-GeoraePlanArtifactRetentionCandidate.ps1",
            "-ArtifactId", secondId, "-StagePath", secondStage, "-ProducerRunId", secondRun,
            "-EvidenceOutputPath", Path.Combine(fixture.EvidenceRoot, $"retention-evidence-{secondId}.json"),
            "-RepositoryRoot", fixture.RepositoryRoot, "-GitRemote", "fixture", "-GitRef", "refs/heads/main",
            "-ScopedPath", "scope/changed.txt", "-Apply");
        Assert.True(second.ExitCode == 0, $"Second candidate failed. stdout={second.Stdout} stderr={second.Stderr}");
        Assert.Contains("artifact_retention=DRY_RUN candidate_count=2", second.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(secondStage));

        using var cleanup = await SecureFixture.CreateAsync();
        await cleanup.ProvisionAndCreateCandidateAsync();
        var fault = await cleanup.FinalizeAsync(apply: true, testFault: "FailStageCleanup");
        Assert.NotEqual(0, fault.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", fault.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(cleanup.CompletionPath));
        Assert.True(Directory.Exists(cleanup.StagePath));
        var retry = await cleanup.FinalizeAsync(apply: true);
        Assert.Equal(0, retry.ExitCode);
        Assert.DoesNotContain("artifact_retention_test_gate=PASSED", retry.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cleanup.StagePath));
    }

    [Fact]
    public async Task DryRunsAreReadOnlyPreflights_AndInspectorAlwaysEmitsArray()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.StagePath, "payload.PID"), "reserved", new UTF8Encoding(false));
        var reserved = await fixture.FinalizeAsync(apply: false);
        Assert.NotEqual(0, reserved.ExitCode);
        Assert.False(File.Exists(fixture.ParentFinalizeJournalPath));
        Assert.False(Directory.Exists(fixture.CandidatePath));

        using var stage = await SecureFixture.CreateAsync();
        await stage.ProvisionAndCreateCandidateAsync();
        var manifest = Path.Combine(stage.StagePath, ".georaeplan-producer-stage.json");
        var before = await File.ReadAllBytesAsync(manifest);
        await File.AppendAllTextAsync(manifest, " ", new UTF8Encoding(false));
        var malformed = await stage.RunAsync("New-GeoraePlanArtifactRetentionCandidate.ps1", "-ArtifactId", stage.ArtifactId, "-StagePath", stage.StagePath);
        Assert.NotEqual(0, malformed.ExitCode);
        Assert.Equal(before.Length + 1, (await File.ReadAllBytesAsync(manifest)).Length);

        using var empty = await SecureFixture.CreateAsync();
        await empty.ProvisionAsync();
        var inspector = await empty.RunAsync("Get-GeoraePlanArtifactRetentionQuarantine.ps1");
        Assert.Equal(0, inspector.ExitCode);
        using var json = JsonDocument.Parse(inspector.Stdout);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(0, json.RootElement.GetArrayLength());
        var quarantineOne = Path.Combine(empty.AllowedParent, $".georaeplan-retention-{Guid.NewGuid():N}-{Guid.NewGuid():N}.quarantine");
        Directory.CreateDirectory(quarantineOne);
        SetExactPrivateDirectoryAcl(quarantineOne);
        var one = await empty.RunAsync("Get-GeoraePlanArtifactRetentionQuarantine.ps1");
        using var oneJson = JsonDocument.Parse(one.Stdout);
        Assert.Equal(JsonValueKind.Array, oneJson.RootElement.ValueKind);
        Assert.Equal(1, oneJson.RootElement.GetArrayLength());
        var quarantineTwo = Path.Combine(empty.AllowedParent, $".georaeplan-retention-{Guid.NewGuid():N}-{Guid.NewGuid():N}.quarantine");
        Directory.CreateDirectory(quarantineTwo);
        SetExactPrivateDirectoryAcl(quarantineTwo);
        var two = await empty.RunAsync("Get-GeoraePlanArtifactRetentionQuarantine.ps1");
        using var twoJson = JsonDocument.Parse(two.Stdout);
        Assert.Equal(JsonValueKind.Array, twoJson.RootElement.ValueKind);
        Assert.Equal(2, twoJson.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task OwnedGateUsesPinnedVstestExactTwentyAndBindsAssemblyAndSourceHashes()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        var completed = await fixture.FinalizeAsync(apply: true);
        Assert.True(completed.ExitCode == 0, $"Exact owned gate failed. stdout={completed.Stdout} stderr={completed.Stderr}");
        Assert.Contains("artifact_retention_test_gate=PASSED total=20 passed=20 failed=0 skipped=0", completed.Stdout, StringComparison.Ordinal);
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.EvidenceOutputPath));
        var test = evidence.RootElement.GetProperty("test");
        Assert.Matches("^[0-9A-F]{64}$", test.GetProperty("assemblySha256").GetString());
        Assert.Matches("^[0-9A-F]{64}$", test.GetProperty("sourceSha256").GetString());

        using var stale = await SecureFixture.CreateAsync();
        await stale.ProvisionAndCreateCandidateAsync();
        var safetySource = Path.Combine(FindRepositoryRoot(), "Tests", "GeoraePlan.Desktop.App.Tests", "ArtifactRetentionSafetyTests.cs");
        var exact = await File.ReadAllBytesAsync(safetySource);
        try
        {
            await File.WriteAllBytesAsync(safetySource, exact.Concat(new byte[] { (byte)' ' }).ToArray());
            var rejected = await stale.FinalizeAsync(apply: true);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.False(File.Exists(stale.ParentFinalizeJournalPath));
            Assert.False(File.Exists(stale.CompletionPath));
        }
        finally
        {
            await File.WriteAllBytesAsync(safetySource, exact);
        }
    }

    [Fact]
    public async Task GitProofPinsWorktreeConfigAndUsesCapturedFileEndpointOutsideRepository()
    {
        var actualRepository = FindRepositoryRoot();
        var actualConfig = Path.Combine(actualRepository, ".git", "config");
        var actualWorktreeConfig = Path.Combine(actualRepository, ".git", "config.worktree");
        var actualConfigBefore = await File.ReadAllBytesAsync(actualConfig);
        Assert.False(File.Exists(actualWorktreeConfig));
        var actualOverride = await RunGitAsync(actualRepository, "-c", "extensions.worktreeConfig=false", "-C", actualRepository, "config", "--bool", "extensions.worktreeConfig");
        Assert.Equal(0, actualOverride.ExitCode);
        Assert.Equal("false", actualOverride.Stdout.Trim());
        Assert.Equal(actualConfigBefore, await File.ReadAllBytesAsync(actualConfig));
        Assert.False(File.Exists(actualWorktreeConfig));

        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await fixture.GitAsync("config", "extensions.worktreeConfig", "true")).ExitCode);
        var worktreeConfig = Path.Combine(fixture.RepositoryRoot, ".git", "config.worktree");
        Assert.False(File.Exists(worktreeConfig));
        var result = await fixture.FinalizeAsync(apply: true, invalid: "safe-file");
        Assert.True(result.ExitCode == 0, $"Absent worktree config preflight failed. stdout={result.Stdout} stderr={result.Stderr}");
        Assert.False(File.Exists(worktreeConfig));
        Assert.Contains("artifact_retention_test_gate=PASSED total=20 passed=20 failed=0 skipped=0", result.Stdout, StringComparison.Ordinal);

        using var ignored = await SecureFixture.CreateAsync();
        await ignored.ProvisionAndCreateCandidateAsync();
        Assert.Equal(0, (await ignored.GitAsync("config", "extensions.worktreeConfig", "true")).ExitCode);
        Assert.Equal(0, (await ignored.GitAsync("config", "--worktree", "remote.fixture.url", "https://invalid.example/ignored.git")).ExitCode);
        var ignoredConfig = Path.Combine(ignored.RepositoryRoot, ".git", "config.worktree");
        var before = await File.ReadAllBytesAsync(ignoredConfig);
        var ignoredResult = await ignored.FinalizeAsync(apply: true);
        Assert.True(ignoredResult.ExitCode == 0, $"Ignored worktree config changed endpoint proof. stdout={ignoredResult.Stdout} stderr={ignoredResult.Stderr}");
        Assert.Equal(before, await File.ReadAllBytesAsync(ignoredConfig));
    }

    [Fact]
    public async Task VstestExecutionClosureIsHeldAndHashBoundThroughExactTwentyGate()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        var adapter = Path.Combine(FindRepositoryRoot(), "Tests", "GeoraePlan.Desktop.App.Tests", "bin", "Release", "net8.0-windows", "xunit.runner.visualstudio.testadapter.dll");
        var before = await File.ReadAllBytesAsync(adapter);
        try
        {
            var result = await fixture.FinalizeAsync(apply: true, testFault: "MutateVstestClosureDuringGate");
            Assert.True(result.ExitCode == 0, $"Pinned vstest closure failed. stdout={result.Stdout} stderr={result.Stderr}");
            Assert.Equal(before, await File.ReadAllBytesAsync(adapter));
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.EvidenceOutputPath));
            Assert.Matches("^[0-9A-F]{64}$", evidence.RootElement.GetProperty("test").GetProperty("closureManifestSha256").GetString());
        }
        finally
        {
            var after = File.Exists(adapter) ? await File.ReadAllBytesAsync(adapter) : Array.Empty<byte>();
            if (!before.AsSpan().SequenceEqual(after))
                await File.WriteAllBytesAsync(adapter, before);
        }
    }

    [Fact]
    public async Task TrustedClosureRejectsPreexistingExtraBeforeExecution()
    {
        var output = Path.Combine(FindRepositoryRoot(), "Tests", "GeoraePlan.Desktop.App.Tests", "bin", "Release", "net8.0-windows");
        var extra = Path.Combine(output, "preexisting-untrusted-extra.dll");
        using var extraFixture = await SecureFixture.CreateAsync();
        await extraFixture.ProvisionAndCreateCandidateAsync();
        try
        {
            await File.WriteAllBytesAsync(extra, [1, 2, 3, 4]);
            var rejected = await extraFixture.FinalizeAsync(apply: true);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.DoesNotContain("artifact_retention_completion=APPLIED", rejected.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(extraFixture.ParentFinalizeJournalPath));
            Assert.False(File.Exists(extraFixture.CompletionPath));
        }
        finally
        {
            if (File.Exists(extra)) File.Delete(extra);
        }
    }

    [Fact]
    public async Task TrustedClosureRejectsPreexistingReplacedDependencyBeforeExecution()
    {
        var output = Path.Combine(FindRepositoryRoot(), "Tests", "GeoraePlan.Desktop.App.Tests", "bin", "Release", "net8.0-windows");
        var dependency = Path.Combine(output, "cs", "Microsoft.TestPlatform.CrossPlatEngine.resources.dll");
        var original = await File.ReadAllBytesAsync(dependency);
        var identity = GetIdentity(dependency);
        using var replacedFixture = await SecureFixture.CreateAsync();
        await replacedFixture.ProvisionAndCreateCandidateAsync();
        try
        {
            var changed = original.ToArray();
            changed[^1] ^= 0x01;
            await File.WriteAllBytesAsync(dependency, changed);
            var rejected = await replacedFixture.FinalizeAsync(apply: true);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.DoesNotContain("artifact_retention_completion=APPLIED", rejected.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(replacedFixture.ParentFinalizeJournalPath));
            Assert.False(File.Exists(replacedFixture.CompletionPath));
        }
        finally
        {
            await File.WriteAllBytesAsync(dependency, original);
        }
        Assert.Equal(identity, GetIdentity(dependency));
        Assert.Equal(original, await File.ReadAllBytesAsync(dependency));
    }

    [Fact]
    public async Task TimedOutProcessTreeIsTerminatedBeforeSharedInputsAreRestored()
    {
        var harnessSource = await File.ReadAllTextAsync(GetType().Assembly.Location.Replace(
            "bin\\Release\\net8.0-windows\\GeoraePlan.Desktop.App.Tests.dll",
            "ArtifactRetentionProducerProvisioningTests.cs",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains("PROC_THREAD_ATTRIBUTE_" + "HANDLE_LIST", harnessSource, StringComparison.Ordinal);
        Assert.Contains("InitializeProcThread" + "AttributeList", harnessSource, StringComparison.Ordinal);
        Assert.Contains("UpdateProcThread" + "Attribute", harnessSource, StringComparison.Ordinal);
        Assert.Contains("Tree" + "Drained", harnessSource, StringComparison.Ordinal);
        Assert.Contains("CleanupFault" + "Step", harnessSource, StringComparison.Ordinal);
        Assert.Contains("cleanup" + "Diagnostics", harnessSource, StringComparison.Ordinal);

        var repository = FindRepositoryRoot();
        var source = Path.Combine(repository, "Tests", "GeoraePlan.Desktop.App.Tests", "ArtifactRetentionSafetyTests.cs");
        var dependency = Path.Combine(repository, "Tests", "GeoraePlan.Desktop.App.Tests", "bin", "Release", "net8.0-windows", "cs", "Microsoft.TestPlatform.CrossPlatEngine.resources.dll");
        var sourceBytes = await File.ReadAllBytesAsync(source);
        var dependencyBytes = await File.ReadAllBytesAsync(dependency);
        var sourceIdentity = GetIdentity(source);
        var dependencyIdentity = GetIdentity(dependency);
        var timeoutId = Guid.NewGuid().ToString("N");
        var pidFile = Path.Combine(Path.GetTempPath(), "georaeplan-retention-timeout-" + timeoutId + ".txt");
        var parentScript = Path.Combine(Path.GetTempPath(), "georaeplan-retention-timeout-" + timeoutId + ".ps1");
        var childScript = Path.Combine(Path.GetTempPath(), "georaeplan-retention-timeout-child-" + timeoutId + ".ps1");
        var childReady = Path.Combine(Path.GetTempPath(), "georaeplan-retention-timeout-ready-" + timeoutId + ".txt");
        var preAssignmentReady = Path.Combine(Path.GetTempPath(), "georaeplan-retention-pre-assignment-" + timeoutId + ".txt");
        int[] pids = [];
        var restoredBeforeEmergencyCleanup = false;
        try
        {
            await File.WriteAllBytesAsync(source, sourceBytes.Concat([(byte)' ']).ToArray());
            await File.WriteAllBytesAsync(dependency, dependencyBytes.Concat([(byte)' ']).ToArray());
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            await File.WriteAllTextAsync(
                childScript,
                "param([string]$Source,[string]$Dependency,[string]$Ready)\n" +
                "$a=[IO.File]::Open($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)\n" +
                "$b=[IO.File]::Open($Dependency,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)\n" +
                "[IO.File]::WriteAllText($Ready,'ready')\n" +
                "Start-Sleep -Seconds 30\n",
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                parentScript,
                "param([string]$Shell,[string]$ChildScript,[string]$Source,[string]$Dependency,[string]$Ready,[string]$PidFile,[string]$PreAssignmentReady)\n" +
                "[IO.File]::WriteAllText($PreAssignmentReady,'ready')\n" +
                "$child=Start-Process -FilePath $Shell -ArgumentList @('-NoProfile','-File',$ChildScript,$Source,$Dependency,$Ready) -WindowStyle Hidden -PassThru\n" +
                "$limit=[DateTime]::UtcNow.AddSeconds(4);while(-not(Test-Path -LiteralPath $Ready)){if([DateTime]::UtcNow-ge$limit){throw 'Child readiness timed out.'};Start-Sleep -Milliseconds 25}\n" +
                "[IO.File]::WriteAllText($PidFile,\"$PID|$($child.Id)\")\n" +
                "Start-Sleep -Seconds 30\n",
                new UTF8Encoding(false));
            var timedOut = await Assert.ThrowsAsync<ProcessTreeTimeoutException>(() => RunProcessAsync(
                shell,
                repository,
                TimeSpan.FromSeconds(10),
                () =>
                {
                    var barrier = Stopwatch.StartNew();
                    while (!File.Exists(preAssignmentReady) && barrier.Elapsed < TimeSpan.FromSeconds(4))
                        Thread.Sleep(20);
                    Assert.False(File.Exists(preAssignmentReady));
                },
                null,
                CleanupFaultStep.TerminateRoot |
                CleanupFaultStep.TerminateJob |
                CleanupFaultStep.DrainJob |
                CleanupFaultStep.WaitRoot |
                CleanupFaultStep.DrainOutput |
                CleanupFaultStep.CloseJob,
                "-NoProfile",
                "-File",
                parentScript,
                shell,
                childScript,
                source,
                dependency,
                childReady,
                pidFile,
                preAssignmentReady));
            Assert.IsType<TimeoutException>(timedOut.InnerException);
            Assert.True(timedOut.TreeDrained);
            Assert.Equal<uint?>(0U, timedOut.ActiveProcessCountAfterTermination);
            Assert.Equal(6, timedOut.CleanupResult.CleanupDiagnostics.Count);
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("terminate-root:", StringComparison.Ordinal));
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("terminate-job:", StringComparison.Ordinal));
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("drain-job:", StringComparison.Ordinal));
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("wait-root:", StringComparison.Ordinal));
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("drain-output:", StringComparison.Ordinal));
            Assert.Contains(timedOut.CleanupResult.CleanupDiagnostics, value => value.StartsWith("close-job:", StringComparison.Ordinal));
            pids = File.Exists(pidFile)
                ? (await File.ReadAllTextAsync(pidFile)).Split('|').Select(int.Parse).ToArray()
                : [];
            Assert.Equal(2, pids.Length);
            Assert.Equal(2, pids.Where(pid => pid > 0).Distinct().Count());
            Assert.All(pids, pid => Assert.False(IsProcessAlive(pid)));
            await File.WriteAllBytesAsync(source, sourceBytes);
            await File.WriteAllBytesAsync(dependency, dependencyBytes);
            restoredBeforeEmergencyCleanup = true;
        }
        finally
        {
            try
            {
                if (pids.Length == 0 && File.Exists(pidFile))
                    pids = (await File.ReadAllTextAsync(pidFile)).Split('|').Select(int.Parse).ToArray();
                var emergencyPids = pids.Where(pid => pid > 0).Distinct().ToArray();
                foreach (var pid in emergencyPids.Where(IsProcessAlive)) TryKillProcessTree(pid);
                var deadline = Stopwatch.StartNew();
                while (emergencyPids.Any(IsProcessAlive) && deadline.Elapsed < TimeSpan.FromSeconds(5))
                {
                    foreach (var pid in emergencyPids.Where(IsProcessAlive)) TryKillProcessTree(pid);
                    await Task.Delay(20);
                }
                if (emergencyPids.Any(IsProcessAlive))
                    throw new TimeoutException("Timeout fixture descendants remained active beyond emergency cleanup.");
                if (!restoredBeforeEmergencyCleanup)
                {
                    await RestoreExactBytesWithRetryAsync(source, sourceBytes, TimeSpan.FromSeconds(5));
                    await RestoreExactBytesWithRetryAsync(dependency, dependencyBytes, TimeSpan.FromSeconds(5));
                }
            }
            finally
            {
                if (File.Exists(pidFile)) File.Delete(pidFile);
                if (File.Exists(parentScript)) File.Delete(parentScript);
                if (File.Exists(childScript)) File.Delete(childScript);
                if (File.Exists(childReady)) File.Delete(childReady);
                if (File.Exists(preAssignmentReady)) File.Delete(preAssignmentReady);
            }
        }
        Assert.Equal(sourceIdentity, GetIdentity(source));
        Assert.Equal(dependencyIdentity, GetIdentity(dependency));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(dependencyBytes, await File.ReadAllBytesAsync(dependency));
    }

    [Fact]
    public async Task ExistingCompletionRejectsWrongJsonTypesBeforeAnyStageMutation()
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        var first = await fixture.FinalizeAsync(apply: true, testFault: "FailStageCleanup");
        Assert.NotEqual(0, first.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", first.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(fixture.CompletionPath));
        Assert.True(Directory.Exists(fixture.StagePath));

        var ownerPath = Path.Combine(fixture.CandidatePath, ".georaeplan-artifact-owner.json");
        var owner = JsonNode.Parse(await File.ReadAllTextAsync(ownerPath))!.AsObject();
        owner["schemaVersion"] = "1";
        await File.WriteAllTextAsync(ownerPath, owner.ToJsonString(), new UTF8Encoding(false));
        var wrongTypeBytes = await File.ReadAllBytesAsync(ownerPath);
        var stageTree = SnapshotTree(fixture.StagePath);

        var retry = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, retry.ExitCode);
        Assert.Equal(wrongTypeBytes, await File.ReadAllBytesAsync(ownerPath));
        Assert.Equal(stageTree, SnapshotTree(fixture.StagePath));

        owner["schemaVersion"] = 1;
        await File.WriteAllTextAsync(ownerPath, owner.ToJsonString(), new UTF8Encoding(false));
        var mutatePlan = await RunPowerShellAsync(
            "-NoProfile", "-Command",
            "& { . $args[0]; $value=Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json; $value.materializationPlan='single-string-array-is-forbidden'; [IO.File]::WriteAllText($args[1],(ConvertTo-GeoraePlanArtifactRetentionStrictJson $value),[Text.UTF8Encoding]::new($false)) }",
            Script("GeoraePlanArtifactRetentionProducer.Common.ps1"),
            fixture.ParentFinalizeJournalPath);
        Assert.Equal(0, mutatePlan.ExitCode);
        var wrongPlan = await File.ReadAllBytesAsync(fixture.ParentFinalizeJournalPath);
        var planRetry = await fixture.FinalizeAsync(apply: true);
        Assert.NotEqual(0, planRetry.ExitCode);
        Assert.Contains("materializationPlan must be a JSON array", planRetry.Stderr, StringComparison.Ordinal);
        Assert.Equal(wrongPlan, await File.ReadAllBytesAsync(fixture.ParentFinalizeJournalPath));
        Assert.Equal(stageTree, SnapshotTree(fixture.StagePath));
    }

    [Fact]
    public async Task CleanupDispositionIsReversibleAndExistingDryRunRunsStandalonePostflight()
    {
        var invalidClosureEntries = new[]
        {
            (Json: "{\"relativePath\":\"release/example\",\"kind\":\"directory\",\"length\":0,\"sha256\":null}", Error: "directory length and sha256 must be JSON null"),
            (Json: "{\"relativePath\":\"release/example.dll\",\"kind\":\"file\",\"length\":null,\"sha256\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", Error: "file length must be a non-negative JSON integer"),
            (Json: "{\"relativePath\":\"release/example.dll\",\"kind\":\"file\",\"length\":1,\"sha256\":7}", Error: "file sha256 must be a non-empty JSON string")
        };
        foreach (var invalid in invalidClosureEntries)
        {
            var encodedJson = Convert.ToBase64String(Encoding.UTF8.GetBytes(invalid.Json));
            Assert.Equal(invalid.Json, Encoding.UTF8.GetString(Convert.FromBase64String(encodedJson)));
            var rejected = await RunPowerShellAsync(
                "-NoProfile", "-Command",
                "& { . $args[0]; $bytes=[Convert]::FromBase64String($args[1]); $json=[Text.Encoding]::UTF8.GetString($bytes); if([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))-cne$args[1]){throw 'Fixture JSON byte roundtrip failed.'}; $entry=ConvertFrom-Json -InputObject $json; Assert-GeoraePlanArtifactRetentionTestClosureEntrySchema $entry }",
                Script("GeoraePlanArtifactRetentionProducer.Common.ps1"), encodedJson);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(invalid.Error, rejected.Stderr, StringComparison.OrdinalIgnoreCase);
        }

        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAndCreateCandidateAsync();
        var cleanupFailed = await fixture.FinalizeAsync(apply: true, testFault: "FailStageCleanup");
        Assert.NotEqual(0, cleanupFailed.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", cleanupFailed.Stdout, StringComparison.Ordinal);
        var stageTree = SnapshotTree(fixture.StagePath);

        var dryRun = await fixture.FinalizeAsync(apply: false);
        Assert.True(dryRun.ExitCode == 0, $"Existing completion dry-run failed. stdout={dryRun.Stdout} stderr={dryRun.Stderr}");
        Assert.Contains($"artifact_retention=DRY_RUN artifact_id={fixture.ArtifactId}", dryRun.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifact_retention_completion=DRY_RUN action=already_completed", dryRun.Stdout, StringComparison.Ordinal);
        Assert.Equal(stageTree, SnapshotTree(fixture.StagePath));

        var postflightFailed = await fixture.FinalizeAsync(apply: true, testFault: "FailAfterConsumerPostflight");
        Assert.NotEqual(0, postflightFailed.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", postflightFailed.Stdout, StringComparison.Ordinal);
        Assert.Equal(stageTree, SnapshotTree(fixture.StagePath));

        var interrupted = await fixture.FinalizeAsync(apply: true, testFault: "FailAfterFirstCleanupDisposition");
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", interrupted.Stdout, StringComparison.Ordinal);
        Assert.Contains("first cleanup disposition", interrupted.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(stageTree, SnapshotTree(fixture.StagePath));
        Assert.True(File.Exists(fixture.CompletionPath));

        using var abrupt = await SecureFixture.CreateAsync();
        await abrupt.ProvisionAndCreateCandidateAsync();
        var abruptSetup = await abrupt.FinalizeAsync(apply: true, testFault: "FailStageCleanup");
        Assert.NotEqual(0, abruptSetup.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", abruptSetup.Stdout, StringComparison.Ordinal);
        var abruptBefore = SnapshotTree(abrupt.StagePath);
        var stopped = await abrupt.FinalizeAsync(apply: true, testFault: "CrashBeforeCleanupCommit");
        Assert.NotEqual(0, stopped.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", stopped.Stdout, StringComparison.Ordinal);
        Assert.Contains("artifact_retention_cleanup=READY action=before_commit", stopped.Stdout, StringComparison.Ordinal);
        Assert.Equal(abruptBefore, SnapshotTree(abrupt.StagePath));

        using var partial = await SecureFixture.CreateAsync();
        await partial.ProvisionAndCreateCandidateAsync();
        var partialSetup = await partial.FinalizeAsync(apply: true, testFault: "FailStageCleanup");
        Assert.NotEqual(0, partialSetup.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", partialSetup.Stdout, StringComparison.Ordinal);
        var partialBefore = SnapshotTree(partial.StagePath);
        var partialCommit = await partial.FinalizeAsync(apply: true, testFault: "FailAfterFirstCleanupCommit");
        Assert.NotEqual(0, partialCommit.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", partialCommit.Stdout, StringComparison.Ordinal);
        Assert.True(Directory.Exists(partial.StagePath));
        Assert.NotEqual(partialBefore, SnapshotTree(partial.StagePath));
        Assert.Equal(0, (await partial.FinalizeAsync(apply: true)).ExitCode);
        Assert.False(Directory.Exists(partial.StagePath));

        using var finalChange = await SecureFixture.CreateAsync();
        await finalChange.ProvisionAndCreateCandidateAsync();
        var changed = await finalChange.FinalizeAsync(apply: true, testFault: "AddFinalChildAfterCommit");
        Assert.NotEqual(0, changed.ExitCode);
        Assert.DoesNotContain("artifact_retention_completion=APPLIED", changed.Stdout, StringComparison.Ordinal);
        Assert.False(Directory.Exists(finalChange.StagePath));
        var injected = Path.Combine(finalChange.CandidatePath, "post-commit-test-child.txt");
        Assert.True(File.Exists(injected));
        File.Delete(injected);
        var finalChangeRetry = await finalChange.FinalizeAsync(apply: true);
        Assert.True(finalChangeRetry.ExitCode == 0, $"Existing completion retry failed. stdout={finalChangeRetry.Stdout} stderr={finalChangeRetry.Stderr}");

        var retry = await fixture.FinalizeAsync(apply: true, testFault: "MutateFinalTreeDuringCleanup");
        Assert.Equal(0, retry.ExitCode);
        Assert.Equal(1, retry.Stdout.Split('\n').Count(line => line.Contains($"artifact_retention=DRY_RUN artifact_id={fixture.ArtifactId}", StringComparison.OrdinalIgnoreCase)));
        Assert.True(
            retry.Stdout.IndexOf($"artifact_retention=DRY_RUN artifact_id={fixture.ArtifactId}", StringComparison.OrdinalIgnoreCase) <
            retry.Stdout.IndexOf("artifact_retention_completion=APPLIED", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fixture.StagePath));
        Assert.True(File.Exists(fixture.CompletionPath));
    }

    private static async Task AssertAclMutationIsRejectedAsync(SecurityIdentifier sid, AccessControlType accessType)
    {
        using var fixture = await SecureFixture.CreateAsync();
        await fixture.ProvisionAsync();
        var parentDirectory = new DirectoryInfo(fixture.AllowedParent);
        var originalAcl = parentDirectory.GetAccessControl();
        var mutatedAcl = parentDirectory.GetAccessControl();
        mutatedAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData, accessType));
        parentDirectory.SetAccessControl(mutatedAcl);
        try
        {
            var result = await fixture.RunAsync(
                "New-GeoraePlanArtifactRetentionCandidate.ps1",
                "-ArtifactId", fixture.ArtifactId,
                "-StagePath", fixture.StagePath,
                "-Apply");
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(Directory.Exists(fixture.CandidatePath));
        }
        finally
        {
            var restoredAcl = new DirectorySecurity();
            restoredAcl.SetSecurityDescriptorSddlForm(
                originalAcl.GetSecurityDescriptorSddlForm(
                    AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group),
                AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
            parentDirectory.SetAccessControl(restoredAcl);
        }
    }

    private static void RestoreSecurity(FileSystemInfo entry, FileSystemSecurity original)
    {
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        if (entry is FileInfo file)
        {
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(sections), sections);
            file.SetAccessControl(restored);
        }
        else
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(sections), sections);
            ((DirectoryInfo)entry).SetAccessControl(restored);
        }
    }

    private static void SetExactPrivateDirectoryAcl(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current SID is unavailable.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in ExactPrivateSids(owner))
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private static void SetExactPrivateFileAcl(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current SID is unavailable.");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in ExactPrivateSids(owner))
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static IEnumerable<SecurityIdentifier> ExactPrivateSids(SecurityIdentifier owner) => new[]
    {
        owner,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    }.DistinctBy(sid => sid.Value);

    private static string GetSddl(FileSystemInfo entry)
    {
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        var security = entry is FileInfo file
            ? (FileSystemSecurity)file.GetAccessControl(sections)
            : ((DirectoryInfo)entry).GetAccessControl(sections);
        return security.GetSecurityDescriptorSddlForm(sections);
    }

    private sealed class SecureFixture : IDisposable
    {
        private bool disposed;

        private SecureFixture(string root, NativeIdentity rootIdentity, NativeIdentity fixtureBaseIdentity)
        {
            Root = root;
            RootIdentity = rootIdentity;
            FixtureBaseIdentity = fixtureBaseIdentity;
            AllowedParent = Path.Combine(root, "private-artifacts");
            RepositoryRoot = Path.Combine(root, "repo");
            BareRemote = Path.Combine(root, "remote.git");
            EvidenceRoot = Path.Combine(root, "evidence");
            SentinelPath = Path.Combine(EvidenceRoot, "protected-sentinel.bin");
            ArtifactId = Guid.NewGuid().ToString("N");
            ProducerRunId = Guid.NewGuid().ToString("N");
        }

        public string Root { get; }
        public NativeIdentity RootIdentity { get; }
        public NativeIdentity FixtureBaseIdentity { get; }
        public string AllowedParent { get; }
        public string RepositoryRoot { get; }
        public string BareRemote { get; }
        public string EvidenceRoot { get; }
        public string SentinelPath { get; }
        public string ArtifactId { get; }
        public string ProducerRunId { get; }
        public string StagePath => Path.Combine(AllowedParent, ".georaeplan-producer-stage-" + ArtifactId);
        public string CandidatePath => Path.Combine(AllowedParent, ArtifactId);
        public string ParentLeasePath => Path.Combine(AllowedParent, ".georaeplan-retention-parent.lease");
        public string ParentBootstrapPath => Path.Combine(Root, ".georaeplan-parent-bootstrap-private-artifacts.json");
        public string ManifestPath => Path.Combine(CandidatePath, ".georaeplan-artifact-producer-manifest.json");
        public string ParentFinalizeJournalPath => Path.Combine(AllowedParent, ".georaeplan-producer-finalize-" + ArtifactId + ".json");
        public string CompletionPath => Path.Combine(CandidatePath, ".georaeplan-artifact-completion.json");
        public string TestResultPath => Path.Combine(EvidenceRoot, "test-result.json");
        public string PostflightResultPath => Path.Combine(EvidenceRoot, "postflight-result.json");
        public string EvidenceOutputPath => Path.Combine(EvidenceRoot, $"retention-evidence-{ArtifactId}.json");
        public string CommitSha { get; private set; } = string.Empty;

        public static async Task<SecureFixture> CreateAsync()
        {
            var fixtureBase = Path.Combine("D:\\DevCaches", "georaeplan-retention-producer-red-tests");
            Directory.CreateDirectory(fixtureBase);
            var fixtureRoot = Path.Combine(fixtureBase, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            var fixture = new SecureFixture(fixtureRoot, GetIdentity(fixtureRoot), GetIdentity(fixtureBase));
            Directory.CreateDirectory(fixture.EvidenceRoot);
            SetExactPrivateDirectoryAcl(fixture.EvidenceRoot);
            await File.WriteAllBytesAsync(fixture.SentinelPath, SHA256.HashData(Encoding.UTF8.GetBytes("producer-handle-bound-sentinel")));
            Directory.CreateDirectory(fixture.RepositoryRoot);
            Assert.Equal(0, (await fixture.GitAsync("init")).ExitCode);
            Assert.Equal(0, (await fixture.GitAsync("config", "user.email", "fixture@example.invalid")).ExitCode);
            Assert.Equal(0, (await fixture.GitAsync("config", "user.name", "Fixture")).ExitCode);
            Directory.CreateDirectory(Path.Combine(fixture.RepositoryRoot, "scope"));
            await File.WriteAllTextAsync(Path.Combine(fixture.RepositoryRoot, "scope", "changed.txt"), "fixture", new UTF8Encoding(false));
            Assert.Equal(0, (await fixture.GitAsync("add", "scope/changed.txt")).ExitCode);
            Assert.Equal(0, (await fixture.GitAsync("commit", "-m", "fixture scoped commit")).ExitCode);
            fixture.CommitSha = (await fixture.GitAsync("rev-parse", "HEAD")).Stdout.Trim();
            Assert.Equal(0, (await RunGitAsync(fixture.Root, "init", "--bare", fixture.BareRemote)).ExitCode);
            Assert.Equal(0, (await fixture.GitAsync("remote", "add", "fixture", fixture.BareRemote)).ExitCode);
            Assert.Equal(0, (await fixture.GitAsync("push", "fixture", "HEAD:refs/heads/main")).ExitCode);
            return fixture;
        }

        public async Task ProvisionAsync()
        {
            var result = await RunAsync("Initialize-GeoraePlanArtifactRetentionParent.ps1", "-Apply");
            Assert.True(result.ExitCode == 0, $"Parent provision failed. stdout={result.Stdout} stderr={result.Stderr}");
        }

        public async Task CreateCandidateAsync()
        {
            var result = await RunAsync("New-GeoraePlanArtifactRetentionCandidate.ps1", "-ArtifactId", ArtifactId, "-StagePath", StagePath, "-Apply");
            Assert.True(result.ExitCode == 0, $"Stage creation failed. stdout={result.Stdout} stderr={result.Stderr}");
            Assert.True(Directory.Exists(StagePath));
            Assert.False(Directory.Exists(CandidatePath));
            Directory.CreateDirectory(Path.Combine(StagePath, "payload", "sub"));
            await File.WriteAllTextAsync(Path.Combine(StagePath, "payload", "sub", "artifact.txt"), "stable payload", new UTF8Encoding(false));
        }

        public async Task ProvisionAndCreateCandidateAsync()
        {
            await ProvisionAsync();
            await CreateCandidateAsync();
        }

        public async Task<ProcessResult> FinalizeAsync(
            bool apply,
            string? invalid = null,
            bool leaveCompletionMissing = false,
            string? testFault = null,
            string? producerRunId = null,
            string? repositoryRoot = null,
            IReadOnlyDictionary<string, string?>? environment = null)
        {
            var remote = invalid == "safe-file" ? "file-safe" : invalid == "credential-remote" ? "credential" : invalid == "query-remote" ? "query" : invalid == "fragment-remote" ? "fragment" : "fixture";
            var gitRef = invalid == "wrong-ref" ? "refs/heads/missing" : "refs/heads/main";
            if (invalid == "safe-file")
                await GitAsync("remote", "add", remote, new Uri(BareRemote).AbsoluteUri);
            if (invalid == "credential-remote")
                await GitAsync("remote", "add", remote, "ssh://user@invalid.example/repo.git");
            if (invalid == "query-remote")
                await GitAsync("remote", "add", remote, new Uri(BareRemote).AbsoluteUri + "?token=fixture-nonsecret-marker");
            if (invalid == "fragment-remote")
                await GitAsync("remote", "add", remote, new Uri(BareRemote).AbsoluteUri + "#fixture-nonsecret-marker");

            var args = new List<string>
            {
                "-ArtifactId", ArtifactId,
                "-StagePath", StagePath,
                "-ProducerRunId", producerRunId ?? ProducerRunId,
                "-EvidenceOutputPath", EvidenceOutputPath,
                "-RepositoryRoot", repositoryRoot ?? RepositoryRoot,
                "-GitRemote", remote,
                "-GitRef", gitRef,
                "-ScopedPath", "scope/changed.txt"
            };
            if (leaveCompletionMissing)
                args.AddRange(["-TestFaultInjection", "AfterManifestPublish"]);
            if (testFault is not null)
                args.AddRange(["-TestFaultInjection", testFault]);
            if (apply)
                args.Add("-Apply");
            return await RunAsyncWithEnvironment(environment, "Complete-GeoraePlanArtifactRetentionCandidate.ps1", args.ToArray());
        }

        public async Task WriteCallerAuthoredPassingEvidenceAsync()
        {
            await File.WriteAllTextAsync(TestResultPath, "{\"passed\":true}", new UTF8Encoding(false));
            await File.WriteAllTextAsync(PostflightResultPath, "{\"passed\":true}", new UTF8Encoding(false));
        }

        public Task<ProcessResult> RunAsync(string scriptName, params string[] extra)
            => RunAsyncWithEnvironment(null, scriptName, extra);

        public Task<ProcessResult> RunAsyncWithEnvironment(IReadOnlyDictionary<string, string?>? environment, string scriptName, params string[] extra)
        {
            var args = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Script(scriptName), "-AllowedParent", AllowedParent
            };
            args.AddRange(extra);
            return RunPowerShellAsyncWithEnvironment(environment, args.ToArray());
        }

        public Task<ProcessResult> RunRetentionConsumerAsync(bool apply = false) => apply
            ? RunAsync("Invoke-GeoraePlanArtifactRetention.ps1", "-Apply")
            : RunAsync("Invoke-GeoraePlanArtifactRetention.ps1");

        public async Task<ProcessResult> GitAsync(params string[] args) => await RunGitAsync(RepositoryRoot, args);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ValidateFixtureCleanupTarget(Root, RootIdentity, FixtureBaseIdentity, EvidenceRoot, RepositoryRoot, BareRemote, AllowedParent);
            Exception? last = null;
            for (var attempt = 0; attempt != 3 && Directory.Exists(Root); attempt++)
            {
                try
                {
                    ClearReadOnlyAttributes(Root);
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) { last = ex; Thread.Sleep(100); }
            }
            if (Directory.Exists(Root))
                throw new IOException($"Fixture cleanup failed: {Root}", last);
        }
    }

    private static async Task<ProcessResult> ParsePowerShell51Async(string scriptPath) => await RunPowerShellAsync(
        "-NoProfile", "-Command",
        "& { $tokens=$null; $errors=$null; [void][Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$tokens,[ref]$errors); if($errors.Count){$errors | % Message | Write-Error; exit 1} }",
        scriptPath);

    private static void ValidateFixtureCleanupTarget(
        string root,
        NativeIdentity rootIdentity,
        NativeIdentity fixtureBaseIdentity,
        string evidenceRoot,
        string repositoryRoot,
        string bareRemote,
        string allowedParent)
    {
        const string fixtureParent = @"D:\DevCaches\georaeplan-retention-producer-red-tests";
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(fullRoot), fixtureParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fullRoot), "N", out _) ||
            !Directory.Exists(fullRoot))
            throw new IOException($"Fixture cleanup target is not an exact dedicated GUID child: {root}");

        var currentRoot = GetIdentity(fullRoot);
        var currentBase = GetIdentity(fixtureParent);
        if (currentRoot != rootIdentity || currentBase != fixtureBaseIdentity ||
            !string.Equals(Path.GetDirectoryName(currentRoot.PhysicalPath), currentBase.PhysicalPath, StringComparison.OrdinalIgnoreCase) ||
            currentRoot.VolumeSerialNumber != currentBase.VolumeSerialNumber ||
            !Path.GetFullPath(evidenceRoot).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(repositoryRoot).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(bareRemote).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(allowedParent).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture cleanup identity or contained-path binding changed.");

        foreach (var path in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Fixture cleanup refuses reparse point: {path}");
        }
        foreach (var topLevel in Directory.EnumerateFileSystemEntries(fullRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(topLevel);
            if (string.Equals(name, ".georaeplan-parent-bootstrap-private-artifacts.json", StringComparison.Ordinal) &&
                File.Exists(topLevel) && !Directory.Exists(topLevel))
                continue;
            if (name.StartsWith(".georaeplan-parent-producer-stage-", StringComparison.OrdinalIgnoreCase))
            {
                var invocation = name[".georaeplan-parent-producer-stage-".Length..];
                if (!Guid.TryParseExact(invocation, "N", out _) ||
                    Directory.EnumerateFileSystemEntries(topLevel, "*", SearchOption.TopDirectoryOnly).Any())
                    throw new IOException($"Fixture cleanup refuses a non-empty or non-exact parent stage: {topLevel}");
                continue;
            }
            if (!new[] { "private-artifacts", "repo", "remote.git", "evidence" }.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Fixture cleanup refuses an unexpected top-level entry: {topLevel}");
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static Task<ProcessResult> RunPowerShellAsync(params string[] args) => RunProcessAsync(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        null,
        args);

    private static Task<ProcessResult> RunPowerShellAsyncWithEnvironment(IReadOnlyDictionary<string, string?>? environment, params string[] args) => RunProcessAsync(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        null,
        environment,
        args);

    private static Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] args) => RunProcessAsync("git", workingDirectory, args);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string? workingDirectory, params string[] args)
        => await RunProcessAsync(fileName, workingDirectory, null, args);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string? workingDirectory, IReadOnlyDictionary<string, string?>? environment, params string[] args)
        => await RunProcessAsync(fileName, workingDirectory, TimeSpan.FromSeconds(90), null, environment, args);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string? workingDirectory, TimeSpan timeout, Action? beforeAssignment, IReadOnlyDictionary<string, string?>? environment, params string[] args)
        => await RunProcessAsync(fileName, workingDirectory, timeout, beforeAssignment, environment, CleanupFaultStep.None, args);

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string? workingDirectory, TimeSpan timeout, Action? beforeAssignment, IReadOnlyDictionary<string, string?>? environment, CleanupFaultStep cleanupFaults, params string[] args)
    {
        var start = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory ?? string.Empty, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (string.Equals(Path.GetFileName(fileName), "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            var windowsPowerShellHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0");
            start.Environment["PSModulePath"] = Path.Combine(windowsPowerShellHome, "Modules");
        }
        if (args.Contains("-TestFaultInjection", StringComparer.Ordinal))
            start.Environment["GEORAEPLAN_ARTIFACT_PRODUCER_TEST_MODE"] = "1";
        if (environment is not null)
            foreach (var pair in environment)
                if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        ProcessTreeJob? job = null;
        NativeSuspendedProcess? process = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        Exception? primaryFailure = null;
        var timedOut = false;
        var terminateTree = false;
        ProcessTreeCleanupResult cleanup = ProcessTreeCleanupResult.NotStarted;
        try
        {
            job = ProcessTreeJob.Create();
            process = NativeSuspendedProcess.Create(start);
            stdout = process.ReadStandardOutputToEndAsync();
            stderr = process.ReadStandardErrorToEndAsync();
            beforeAssignment?.Invoke();
            job.Assign(process.ProcessHandle);
            process.Resume();
            try
            {
                await process.WaitForExitAsync(timeout);
            }
            catch (TimeoutException failure)
            {
                timedOut = true;
                terminateTree = true;
                primaryFailure = failure;
            }
        }
        catch (Exception failure)
        {
            terminateTree = true;
            primaryFailure = failure;
        }
        finally
        {
            if (process is not null && job is not null && stdout is not null && stderr is not null)
                cleanup = await CleanupProcessTreeAsync(process, job, stdout, stderr, terminateTree, cleanupFaults, TimeSpan.FromSeconds(5));
            else
                job?.Dispose();
        }

        try
        {
            if (primaryFailure is not null)
            {
                if (timedOut) throw new ProcessTreeTimeoutException(cleanup, primaryFailure);
                throw new ProcessTreeExecutionException(cleanup, primaryFailure);
            }
            if (!cleanup.TreeDrained || cleanup.CleanupDiagnostics.Count != 0)
                throw new ProcessTreeCleanupException(cleanup);
            return new ProcessResult(process!.ExitCode, stdout!.Result, stderr!.Result);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task ObserveProcessOutputAsync(Task<string> stdout, Task<string> stderr, TimeSpan timeout)
        => await Task.WhenAll(stdout, stderr).WaitAsync(timeout);

    private static async Task<ProcessTreeCleanupResult> CleanupProcessTreeAsync(
        NativeSuspendedProcess process,
        ProcessTreeJob job,
        Task<string> stdout,
        Task<string> stderr,
        bool terminateTree,
        CleanupFaultStep injectedFaults,
        TimeSpan timeout)
    {
        var cleanupDiagnostics = new List<string>();
        static Exception Injected(CleanupFaultStep step) => new InvalidOperationException("Injected cleanup diagnostic: " + step);
        void Record(string step, Exception failure) => cleanupDiagnostics.Add(step + ": " + failure.GetType().Name + ": " + failure.Message);

        if (terminateTree)
        {
            try
            {
                process.TerminateRoot();
                if (injectedFaults.HasFlag(CleanupFaultStep.TerminateRoot)) throw Injected(CleanupFaultStep.TerminateRoot);
            }
            catch (Exception failure) { Record("terminate-root", failure); }
            try
            {
                job.Terminate();
                if (injectedFaults.HasFlag(CleanupFaultStep.TerminateJob)) throw Injected(CleanupFaultStep.TerminateJob);
            }
            catch (Exception failure) { Record("terminate-job", failure); }
        }

        uint? activeProcessCount = null;
        try
        {
            await job.WaitForNoActiveProcessesAsync(timeout);
            activeProcessCount = job.ActiveProcessCount;
            if (injectedFaults.HasFlag(CleanupFaultStep.DrainJob)) throw Injected(CleanupFaultStep.DrainJob);
        }
        catch (Exception failure)
        {
            Record("drain-job", failure);
            try { job.Terminate(); }
            catch (Exception retryFailure) { Record("terminate-job-retry", retryFailure); }
            try
            {
                await job.WaitForNoActiveProcessesAsync(timeout);
                activeProcessCount = job.ActiveProcessCount;
            }
            catch (Exception retryFailure) { Record("drain-job-retry", retryFailure); }
        }

        var rootExited = false;
        try
        {
            await process.WaitForExitAsync(timeout);
            rootExited = true;
            if (injectedFaults.HasFlag(CleanupFaultStep.WaitRoot)) throw Injected(CleanupFaultStep.WaitRoot);
        }
        catch (Exception failure)
        {
            Record("wait-root", failure);
            try { process.TerminateRoot(); }
            catch (Exception retryFailure) { Record("terminate-root-retry", retryFailure); }
            try { await process.WaitForExitAsync(timeout); rootExited = true; }
            catch (Exception retryFailure) { Record("wait-root-retry", retryFailure); }
        }

        try
        {
            await ObserveProcessOutputAsync(stdout, stderr, timeout);
            if (injectedFaults.HasFlag(CleanupFaultStep.DrainOutput)) throw Injected(CleanupFaultStep.DrainOutput);
        }
        catch (Exception failure)
        {
            Record("drain-output", failure);
            try { await ObserveProcessOutputAsync(stdout, stderr, timeout); }
            catch (Exception retryFailure) { Record("drain-output-retry", retryFailure); }
        }

        try
        {
            job.Dispose();
            if (injectedFaults.HasFlag(CleanupFaultStep.CloseJob)) throw Injected(CleanupFaultStep.CloseJob);
        }
        catch (Exception failure) { Record("close-job", failure); }

        return new ProcessTreeCleanupResult(
            rootExited && activeProcessCount == 0,
            rootExited,
            activeProcessCount,
            cleanupDiagnostics.ToArray());
    }

    private sealed class NativeSuspendedProcess : IDisposable
    {
        private const uint CREATE_SUSPENDED = 0x00000004;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint STARTF_USESTDHANDLES = 0x00000100;
        private const uint HANDLE_FLAG_INHERIT = 0x00000001;
        private const uint WAIT_OBJECT_0 = 0;
        private const uint WAIT_FAILED = 0xFFFFFFFF;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x00020002;
        private readonly Process rootProcess;
        private readonly SafeFileHandle processHandle;
        private SafeFileHandle? primaryThreadHandle;
        private readonly StreamReader standardOutput;
        private readonly StreamReader standardError;

        private NativeSuspendedProcess(Process rootProcess, SafeFileHandle processHandle, SafeFileHandle primaryThreadHandle, StreamReader standardOutput, StreamReader standardError)
        {
            this.rootProcess = rootProcess;
            this.processHandle = processHandle;
            this.primaryThreadHandle = primaryThreadHandle;
            this.standardOutput = standardOutput;
            this.standardError = standardError;
        }

        public SafeFileHandle ProcessHandle => processHandle;
        public int ExitCode => rootProcess.ExitCode;

        public static NativeSuspendedProcess Create(ProcessStartInfo start)
        {
            SafeFileHandle? stdoutRead = null;
            SafeFileHandle? stdoutWrite = null;
            SafeFileHandle? stderrRead = null;
            SafeFileHandle? stderrWrite = null;
            SafeFileHandle? createdProcess = null;
            SafeFileHandle? createdThread = null;
            Process? root = null;
            StreamReader? stdoutReader = null;
            StreamReader? stderrReader = null;
            var environment = IntPtr.Zero;
            var attributeList = IntPtr.Zero;
            var inheritedHandleList = IntPtr.Zero;
            var attributeListInitialized = false;
            try
            {
                var security = new SecurityAttributesData
                {
                    Length = Marshal.SizeOf<SecurityAttributesData>(),
                    InheritHandle = true
                };
                if (!CreatePipe(out stdoutRead, out stdoutWrite, ref security, 0) ||
                    !SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0) ||
                    !CreatePipe(out stderrRead, out stderrWrite, ref security, 0) ||
                    !SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native test process output pipes could not be created.");

                var startup = new StartupInfoExData
                {
                    StartupInfo = new StartupInfoData
                    {
                        Size = Marshal.SizeOf<StartupInfoExData>(),
                        Flags = STARTF_USESTDHANDLES,
                        StandardInput = IntPtr.Zero,
                        StandardOutput = stdoutWrite.DangerousGetHandle(),
                        StandardError = stderrWrite.DangerousGetHandle()
                    }
                };
                var attributeListSize = IntPtr.Zero;
                _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                if (attributeListSize == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native test process attribute-list size could not be determined.");
                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native test process attribute list could not be initialized.");
                attributeListInitialized = true;
                inheritedHandleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
                Marshal.WriteIntPtr(inheritedHandleList, 0, stdoutWrite.DangerousGetHandle());
                Marshal.WriteIntPtr(inheritedHandleList, IntPtr.Size, stderrWrite.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                        inheritedHandleList,
                        (IntPtr)(IntPtr.Size * 2),
                        IntPtr.Zero,
                        IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native test process inherited-handle allowlist could not be set.");
                startup.AttributeList = attributeList;
                var commandLine = new StringBuilder(BuildWindowsCommandLine(start.FileName, start.ArgumentList));
                environment = Marshal.StringToHGlobalUni(BuildWindowsEnvironmentBlock(start));
                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                        environment,
                        string.IsNullOrWhiteSpace(start.WorkingDirectory) ? null : start.WorkingDirectory,
                        ref startup,
                        out var information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Native suspended test process could not be created.");

                createdProcess = new SafeFileHandle(information.Process, ownsHandle: true);
                createdThread = new SafeFileHandle(information.Thread, ownsHandle: true);
                stdoutWrite.Dispose();
                stdoutWrite = null;
                stderrWrite.Dispose();
                stderrWrite = null;
                root = Process.GetProcessById(unchecked((int)information.ProcessId));
                stdoutReader = new StreamReader(new FileStream(stdoutRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, true, 4096, leaveOpen: false);
                stdoutRead = null;
                stderrReader = new StreamReader(new FileStream(stderrRead, FileAccess.Read, 4096, isAsync: false), Encoding.UTF8, true, 4096, leaveOpen: false);
                stderrRead = null;
                var result = new NativeSuspendedProcess(root, createdProcess, createdThread, stdoutReader, stderrReader);
                root = null;
                createdProcess = null;
                createdThread = null;
                stdoutReader = null;
                stderrReader = null;
                return result;
            }
            catch (Exception primaryFailure)
            {
                var cleanupFailures = new List<Exception>();
                if (createdProcess is not null && !createdProcess.IsInvalid)
                {
                    if (!TerminateProcess(createdProcess, 1))
                        cleanupFailures.Add(new Win32Exception(Marshal.GetLastWin32Error(), "Native-create cleanup could not terminate the suspended process."));
                    var wait = WaitForSingleObject(createdProcess, 5000);
                    if (wait != WAIT_OBJECT_0)
                        cleanupFailures.Add(new Win32Exception(wait == WAIT_FAILED ? Marshal.GetLastWin32Error() : unchecked((int)wait), "Native-create cleanup could not prove suspended process exit."));
                }
                if (cleanupFailures.Count != 0)
                    throw new AggregateException("Native suspended process creation failed; cleanup diagnostics follow.", new[] { primaryFailure }.Concat(cleanupFailures));
                throw;
            }
            finally
            {
                if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
                if (inheritedHandleList != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandleList);
                if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                stderrReader?.Dispose();
                stdoutReader?.Dispose();
                root?.Dispose();
                createdThread?.Dispose();
                createdProcess?.Dispose();
                stderrWrite?.Dispose();
                stderrRead?.Dispose();
                stdoutWrite?.Dispose();
                stdoutRead?.Dispose();
            }
        }

        public Task<string> ReadStandardOutputToEndAsync() => standardOutput.ReadToEndAsync();
        public Task<string> ReadStandardErrorToEndAsync() => standardError.ReadToEndAsync();

        public void Resume()
        {
            var thread = primaryThreadHandle ?? throw new InvalidOperationException("Native test process was already resumed.");
            if (ResumeThread(thread) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Native test process primary thread could not be resumed.");
            thread.Dispose();
            primaryThreadHandle = null;
        }

        public void TerminateRoot()
        {
            if (!rootProcess.HasExited && !TerminateProcess(processHandle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Native suspended test process could not be terminated.");
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
            => await rootProcess.WaitForExitAsync().WaitAsync(timeout);

        public void Dispose()
        {
            primaryThreadHandle?.Dispose();
            standardError.Dispose();
            standardOutput.Dispose();
            rootProcess.Dispose();
            processHandle.Dispose();
        }

        private static string BuildWindowsCommandLine(string fileName, IEnumerable<string> arguments)
            => string.Join(" ", new[] { QuoteWindowsArgument(fileName) }.Concat(arguments.Select(QuoteWindowsArgument)));

        private static string QuoteWindowsArgument(string value)
        {
            if (value.Length != 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
                return value;
            var result = new StringBuilder().Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', backslashes * 2 + 1).Append('"');
                    backslashes = 0;
                    continue;
                }
                result.Append('\\', backslashes).Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        private static string BuildWindowsEnvironmentBlock(ProcessStartInfo start)
            => string.Join("\0", start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    private sealed class ProcessTreeJob : IDisposable
    {
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectExtendedLimitInformation = 9;
        private readonly SafeFileHandle handle;

        private ProcessTreeJob(SafeFileHandle handle) => this.handle = handle;

        public static ProcessTreeJob Create()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Test process Job Object creation failed.");
            try
            {
                var information = new JobObjectExtendedLimitInformationData
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformationData
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref information, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Test process Job Object limits could not be set.");
                return new ProcessTreeJob(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Assign(SafeFileHandle processHandle)
        {
            if (!AssignProcessToJobObject(handle, processHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Test process could not be assigned to its Job Object.");
        }

        public void Terminate()
        {
            if (!TerminateJobObject(handle, 1) && GetActiveProcessCount() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Timed-out test process Job Object could not be terminated.");
        }

        public async Task WaitForNoActiveProcessesAsync(TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (GetActiveProcessCount() != 0)
            {
                if (deadline.Elapsed >= timeout)
                    throw new TimeoutException("Timed-out test process Job Object remained active beyond the cleanup bound.");
                await Task.Delay(20);
            }
        }

        private uint GetActiveProcessCount()
        {
            if (!QueryInformationJobObject(handle, JobObjectBasicAccountingInformation, out var information, (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationData>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Test process Job Object accounting could not be queried.");
            return information.ActiveProcesses;
        }

        public uint ActiveProcessCount => GetActiveProcessCount();

        public void Dispose() => handle.Dispose();
    }

    [Flags]
    private enum CleanupFaultStep
    {
        None = 0,
        TerminateRoot = 1,
        TerminateJob = 2,
        DrainJob = 4,
        WaitRoot = 8,
        DrainOutput = 16,
        CloseJob = 32
    }

    private sealed record ProcessTreeCleanupResult(
        bool TreeDrained,
        bool RootExited,
        uint? ActiveProcessCount,
        IReadOnlyList<string> CleanupDiagnostics)
    {
        public static ProcessTreeCleanupResult NotStarted { get; } = new(false, false, null, ["cleanup-not-started"]);
    }

    private sealed class ProcessTreeTimeoutException : TimeoutException
    {
        public ProcessTreeTimeoutException(ProcessTreeCleanupResult cleanupResult, Exception innerException)
            : base("Timed-out test process tree cleanup completed.", innerException) => CleanupResult = cleanupResult;
        public ProcessTreeCleanupResult CleanupResult { get; }
        public bool TreeDrained => CleanupResult.TreeDrained;
        public uint? ActiveProcessCountAfterTermination => CleanupResult.ActiveProcessCount;
    }

    private sealed class ProcessTreeExecutionException : Exception
    {
        public ProcessTreeExecutionException(ProcessTreeCleanupResult cleanupResult, Exception innerException)
            : base("Test process execution failed; cleanup diagnostics are attached.", innerException) => CleanupResult = cleanupResult;
        public ProcessTreeCleanupResult CleanupResult { get; }
    }

    private sealed class ProcessTreeCleanupException : Exception
    {
        public ProcessTreeCleanupException(ProcessTreeCleanupResult cleanupResult)
            : base("Test process cleanup was incomplete or reported diagnostics: " + string.Join(" | ", cleanupResult.CleanupDiagnostics)) => CleanupResult = cleanupResult;
        public ProcessTreeCleanupResult CleanupResult { get; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task RestoreExactBytesWithRetryAsync(string path, byte[] bytes, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
                return;
            }
            catch (IOException) when (deadline.Elapsed < timeout)
            {
                await Task.Delay(20);
            }
        }
    }

    private static void TryKillProcessTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void WriteCanonical(string path, object value) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false }),
        new UTF8Encoding(false));

    private static string[] SnapshotTree(string root) => Directory.Exists(root)
        ? new[] { "D|.|" + GetSddl(new DirectoryInfo(root)) }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Directory.Exists(path)
                ? "D|" + Path.GetRelativePath(root, path).Replace('\\', '/') + "|" + GetSddl(new DirectoryInfo(path))
                : "F|" + Path.GetRelativePath(root, path).Replace('\\', '/') + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + "|" + GetSddl(new FileInfo(path))))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray()
        : Array.Empty<string>();

    private static string Script(string file) => Path.Combine(FindRepositoryRoot(), "tools", "maintenance", file);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) && Directory.Exists(Path.Combine(current.FullName, "tools"))) return current.FullName;
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static NativeIdentity GetIdentity(string path)
    {
        const uint fileReadAttributes = 0x80;
        const uint fileListDirectory = 0x1;
        const uint genericRead = 0x80000000;
        const uint shareRead = 0x1;
        const uint shareWrite = 0x2;
        const uint shareDelete = 0x4;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint directoryAttribute = 0x10;
        var fullPath = Path.GetFullPath(path);
        var attributes = GetFileAttributesW(fullPath);
        if (attributes == 0xffffffff)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var isDirectory = (attributes & directoryAttribute) != 0;
        using var handle = CreateFileW(
            fullPath,
            isDirectory ? fileReadAttributes | fileListDirectory : genericRead,
            shareRead | shareWrite | shareDelete,
            IntPtr.Zero,
            openExisting,
            isDirectory ? backupSemantics : 0,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var finalPath = buffer.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            finalPath = @"\\" + finalPath[8..];
        else if (finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            finalPath = finalPath[4..];
        var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return new NativeIdentity(finalPath, info.VolumeSerialNumber.ToString("X8"), fileId.ToString("X16"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationData
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersData
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformationData BasicLimitInformation;
        public IoCountersData IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationData
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributesData
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoData
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoExData
    {
        public StartupInfoData StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformationData
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributesData pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoExData startupInfo, out ProcessInformationData processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, uint flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobObjectExtendedLimitInformationData information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out JobObjectBasicAccountingInformationData information, uint informationLength, out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    private sealed record NativeIdentity(string PhysicalPath, string VolumeSerialNumber, string FileId);
    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
