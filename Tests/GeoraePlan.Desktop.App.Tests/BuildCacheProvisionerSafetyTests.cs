using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BuildCacheProvisionerSafetyTests
{
    private static readonly string[] RelativeLeaves =
    {
        "temp",
        Path.Combine("nuget", "packages"),
        Path.Combine("nuget", "http-cache"),
        Path.Combine("nuget", "plugins-cache"),
        "dotnet-home"
    };

    [Fact]
    public async Task Provisioner_DefaultsToDryRunAndCreatesNothing()
    {
        using var fixture = ProvisionerFixture.Create();

        var result = await fixture.RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Mode=DryRun", result.Output, StringComparison.Ordinal);
        Assert.Contains("EnvironmentPathCount=6", result.Output, StringComparison.Ordinal);
        Assert.Contains("UniqueLeafCount=5", result.Output, StringComparison.Ordinal);
        Assert.Contains("WouldCreate", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Fact]
    public async Task Provisioner_ApplyCreatesExactOwnedAclBoundContractAndIsIdempotent()
    {
        using var fixture = ProvisionerFixture.Create();

        var first = await fixture.RunAsync("-Apply");
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Mode=Apply", first.Output, StringComparison.Ordinal);
        Assert.Contains("ProvisioningComplete=True", first.Output, StringComparison.Ordinal);

        var ownerPath = Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json");
        var coordinatorPath = Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-provision.lease");
        Assert.True(File.Exists(ownerPath));
        Assert.True(File.Exists(coordinatorPath));

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(ownerPath)))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "georaeplan-build-cache",
                root.GetProperty("owner").GetString());
            Assert.Equal(
                Path.GetFullPath(fixture.CacheRoot),
                root.GetProperty("cacheRootPath").GetString(),
                ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(
                root.GetProperty("cacheRootPhysicalPath").GetString()));
            Assert.Matches(
                "^[A-F0-9]{8}$",
                root.GetProperty("volumeSerialNumber").GetString()!);
            Assert.Matches(
                "^[A-F0-9]{16}$",
                root.GetProperty("fileId").GetString()!);
            Assert.Equal(
                WindowsIdentity.GetCurrent().User!.Value,
                root.GetProperty("ownerSid").GetString());
            Assert.Equal(
                RelativeLeaves.Select(path => path.Replace('\\', '/')),
                root.GetProperty("leafRelativePaths")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }

        var trackedPaths = new List<string> { ownerPath, coordinatorPath };
        AssertAclContract(fixture.CacheRoot);
        AssertAclContract(Path.Combine(fixture.CacheRoot, "nuget"));
        AssertFileAclContract(fixture.JournalPath);
        AssertFileAclContract(ownerPath);
        AssertFileAclContract(coordinatorPath);
        foreach (var relativeLeaf in RelativeLeaves)
        {
            var leaf = Path.Combine(fixture.CacheRoot, relativeLeaf);
            var sentinel = Path.Combine(
                leaf,
                ".georaeplan-build-cache-lease");
            Assert.True(Directory.Exists(leaf));
            Assert.True(File.Exists(sentinel));
            Assert.Equal(0, new FileInfo(sentinel).Length);
            AssertAclContract(leaf);
            AssertFileAclContract(sentinel);
            trackedPaths.Add(sentinel);
        }

        var before = trackedPaths.ToDictionary(
            path => path,
            path => (File.GetLastWriteTimeUtc(path), File.ReadAllBytes(path)));
        var second = await fixture.RunAsync("-Apply");

        Assert.Equal(0, second.ExitCode);
        Assert.Contains("AlreadyProvisioned=True", second.Output, StringComparison.Ordinal);
        foreach (var entry in before)
        {
            Assert.Equal(entry.Value.Item1, File.GetLastWriteTimeUtc(entry.Key));
            Assert.Equal(entry.Value.Item2, File.ReadAllBytes(entry.Key));
        }
    }

    [Fact]
    public async Task Provisioner_AfterJournalNeverAdoptsOrMutatesAHostileCanonicalRoot()
    {
        using var fixture = ProvisionerFixture.Create();

        var interrupted = await fixture.RunAsync(
            "-Apply",
            "-TestFaultInjection",
            "AfterJournal");
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.True(File.Exists(fixture.JournalPath));
        AssertFileAclContract(fixture.JournalPath);

        Directory.Delete(fixture.CacheRoot, recursive: true);

        Directory.CreateDirectory(fixture.CacheRoot);
        var protectedPath = Path.Combine(fixture.CacheRoot, "foreign.bin");
        var protectedBytes = RandomNumberGenerator.GetBytes(43);
        await File.WriteAllBytesAsync(protectedPath, protectedBytes);
        var directory = new DirectoryInfo(fixture.CacheRoot);
        var acl = directory.GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData,
            AccessControlType.Allow));
        directory.SetAccessControl(acl);
        var rootAclBefore = CaptureDirectoryAcl(fixture.CacheRoot);
        var fileAclBefore = CaptureFileAcl(protectedPath);

        var retry = await fixture.RunAsync("-Apply");

        Assert.NotEqual(0, retry.ExitCode);
        Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(protectedPath));
        Assert.Equal(rootAclBefore, CaptureDirectoryAcl(fixture.CacheRoot));
        Assert.Equal(fileAclBefore, CaptureFileAcl(protectedPath));
        Assert.False(File.Exists(Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json")));
        Assert.All(RelativeLeaves, relative => Assert.False(
            Directory.Exists(Path.Combine(fixture.CacheRoot, relative))));
    }

    [Theory]
    [InlineData("JournalShortWrite")]
    [InlineData("JournalBeforeFlush")]
    [InlineData("JournalAfterFlushBeforePublish")]
    [InlineData("JournalProcessKillAfterFlush")]
    public async Task Provisioner_TornJournalStagingIsBoundedAndRetryable(
        string fault)
    {
        using var fixture = ProvisionerFixture.Create();

        var interrupted = await fixture.RunAsync(
            "-Apply",
            "-TestFaultInjection",
            fault);
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.True(Directory.Exists(fixture.CacheRoot));
        var pending = Assert.Single(Directory.GetFiles(
            fixture.CacheRoot,
            ".georaeplan-build-cache-provisioning.json.pending-*",
            SearchOption.TopDirectoryOnly));
        AssertFileAclContract(pending);

        var recovered = await fixture.RunAsync("-Apply");

        Assert.Equal(0, recovered.ExitCode);
        Assert.True(File.Exists(fixture.JournalPath));
        AssertFileAclContract(fixture.JournalPath);
        Assert.Empty(Directory.GetFileSystemEntries(
            fixture.CacheRoot,
            ".georaeplan-build-cache-provisioning.json.pending-*"));
    }

    [Theory]
    [InlineData("OwnerShortWrite")]
    [InlineData("OwnerOneByteWrite")]
    [InlineData("OwnerTailShortWrite")]
    [InlineData("OwnerBeforeFlush")]
    [InlineData("OwnerAfterFlushBeforePublish")]
    [InlineData("OwnerProcessKillAfterFlush")]
    public async Task Provisioner_TornOwnerStagingIsBoundedAndRetryable(
        string fault)
    {
        using var fixture = ProvisionerFixture.Create();
        var ownerPath = Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json");

        var interrupted = await fixture.RunAsync(
            "-Apply",
            "-TestFaultInjection",
            fault);
        Assert.NotEqual(0, interrupted.ExitCode);
        Assert.True(Directory.Exists(fixture.CacheRoot));
        Assert.False(File.Exists(ownerPath));
        var pendingPath = Assert.Single(Directory.GetFiles(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json.pending-*",
            SearchOption.TopDirectoryOnly));
        var interruptedPrefix = await File.ReadAllBytesAsync(pendingPath);
        if (fault == "OwnerOneByteWrite")
            Assert.Single(interruptedPrefix);

        var recovered = await fixture.RunAsync("-Apply");

        Assert.Equal(0, recovered.ExitCode);
        Assert.True(File.Exists(ownerPath));
        AssertFileAclContract(ownerPath);
        var ownerBytes = await File.ReadAllBytesAsync(ownerPath);
        if (fault == "OwnerTailShortWrite")
            Assert.Equal(ownerBytes.Length - 1, interruptedPrefix.Length);
        Assert.True(ownerBytes.AsSpan(0, interruptedPrefix.Length)
            .SequenceEqual(interruptedPrefix));
        Assert.Empty(Directory.GetFileSystemEntries(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json.pending-*"));
    }

    [Theory]
    [InlineData("Journal", ".georaeplan-build-cache-provisioning.json")]
    [InlineData("Owner", ".georaeplan-build-cache-owner.json")]
    public async Task Provisioner_PublishedMetadataCannotBeRenamedUntilLeaseDisposal(
        string label,
        string relativeTarget)
    {
        using var fixture = ProvisionerFixture.Create();
        var target = Path.Combine(fixture.CacheRoot, relativeTarget);
        var moved = target + ".hostile-moved";
        fixture.InjectInterleaving(
            "AfterMetadataPublishBeforeLeaseReturn",
            $$"""
            if ($Name -ceq '{{relativeTarget}}') {
                try {
                    [IO.File]::Move($path,$path + '.hostile-moved')
                    [Console]::Out.WriteLine('MetadataRenameUnexpectedlySucceeded={{label}}')
                } catch {
                    [Console]::Out.WriteLine('MetadataRenameBlocked={{label}}')
                }
            }
            """);

        var result = await fixture.RunAsync("-Apply");

        Assert.True(File.Exists(target));
        Assert.False(File.Exists(moved));
        var bytes = await File.ReadAllBytesAsync(target);
        AssertFileAclContract(target);
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains($"MetadataRenameBlocked={label}", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain($"MetadataRenameUnexpectedlySucceeded={label}", result.Output, StringComparison.Ordinal);

        File.Move(target, moved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(moved));
        File.Move(moved, target);
    }

    [Fact]
    public async Task Provisioner_NonconformingExistingSentinelFailsBeforeCreatingAnyMissingSentinel()
    {
        using var fixture = ProvisionerFixture.Create();
        Directory.CreateDirectory(fixture.CacheRoot);
        foreach (var relativeLeaf in RelativeLeaves)
        {
            Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, relativeLeaf));
        }

        var hostileSentinel = Path.Combine(
            fixture.CacheRoot,
            "dotnet-home",
            ".georaeplan-build-cache-lease");
        await File.WriteAllTextAsync(hostileSentinel, "not-the-empty-sentinel-contract");

        var result = await fixture.RunAsync("-Apply");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unowned", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "not-the-empty-sentinel-contract",
            await File.ReadAllTextAsync(hostileSentinel));
        Assert.All(
            RelativeLeaves.Where(path => path != "dotnet-home"),
            relativeLeaf => Assert.False(File.Exists(Path.Combine(
                fixture.CacheRoot,
                relativeLeaf,
                ".georaeplan-build-cache-lease"))));
    }

    [Theory]
    [InlineData("AfterJournal", true, false, 0)]
    [InlineData("AfterRoot", true, false, 0)]
    [InlineData("AfterOwner", true, true, 0)]
    [InlineData("AfterFirstSentinel", true, true, 1)]
    public async Task Provisioner_HandleRelativePartialCommitIsRetryable(
        string fault,
        bool rootExists,
        bool ownerExists,
        int sentinelCount)
    {
        using var fixture = ProvisionerFixture.Create();

        var failed = await fixture.RunAsync(
            "-Apply",
            "-TestFaultInjection",
            fault);

        Assert.NotEqual(0, failed.ExitCode);
        Assert.Contains("injected", failed.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(fixture.JournalPath));
        var journalBefore = await File.ReadAllBytesAsync(fixture.JournalPath);
        Assert.Equal(rootExists, Directory.Exists(fixture.CacheRoot));
        Assert.Equal(
            ownerExists,
            File.Exists(Path.Combine(
                fixture.CacheRoot,
                ".georaeplan-build-cache-owner.json")));
        Assert.Equal(
            sentinelCount,
            RelativeLeaves.Count(relativeLeaf => File.Exists(Path.Combine(
                fixture.CacheRoot,
                relativeLeaf,
                ".georaeplan-build-cache-lease"))));

        var recovered = await fixture.RunAsync("-Apply");
        Assert.Equal(0, recovered.ExitCode);
        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(fixture.JournalPath));
        Assert.All(
            RelativeLeaves,
            relativeLeaf => Assert.True(File.Exists(Path.Combine(
                fixture.CacheRoot,
                relativeLeaf,
                ".georaeplan-build-cache-lease"))));
    }

    [Fact]
    public async Task Provisioner_UnownedPartialCanonicalRootIsNeverAdopted()
    {
        using var fixture = ProvisionerFixture.Create();
        var hostile = Path.Combine(fixture.CacheRoot, "hostile.bin");
        var hostileBytes = new byte[] { 0x55, 0x4E, 0x4F, 0x57, 0x4E, 0x45, 0x44 };
        fixture.InjectInterleaving(
            "BeforeRootCreate",
            """
            $foreignRoot = [GeoraePlan.BuildCacheProvisioner.NativeEntry]::CreateDirectoryChild($parent,$rootName,$directoryDescriptor)
            try {
                [IO.File]::WriteAllBytes((Join-Path $rootPlan.LogicalPath 'hostile.bin'),[byte[]](0x55,0x4E,0x4F,0x57,0x4E,0x45,0x44))
            } finally {
                $foreignRoot.Dispose()
            }
            """);

        var raced = await fixture.RunAsync("-Apply");
        Assert.Equal(hostileBytes, await File.ReadAllBytesAsync(hostile));
        Assert.NotEqual(0, raced.ExitCode);
        AssertAclContract(fixture.CacheRoot);
        var rootAclBefore = CaptureDirectoryAcl(fixture.CacheRoot);
        var fileAclBefore = CaptureFileAcl(hostile);
        fixture.RestoreCleanScript();

        var result = await fixture.RunAsync("-Apply");

        Assert.Equal(hostileBytes, await File.ReadAllBytesAsync(hostile));
        Assert.Equal(rootAclBefore, CaptureDirectoryAcl(fixture.CacheRoot));
        Assert.Equal(fileAclBefore, CaptureFileAcl(hostile));
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(fixture.JournalPath));
        Assert.False(File.Exists(Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json")));
        Assert.All(
            RelativeLeaves,
            relativeLeaf => Assert.False(File.Exists(Path.Combine(
                fixture.CacheRoot,
                relativeLeaf,
                ".georaeplan-build-cache-lease"))));
    }

    [Fact]
    public async Task Provisioner_HostileJunctionFailsClosedWithoutWritingOutsideCache()
    {
        using var fixture = ProvisionerFixture.Create();
        var outside = Path.Combine(fixture.Root, "outside");
        var nuget = Path.Combine(fixture.CacheRoot, "nuget");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(fixture.CacheRoot);

        var junction = await RunProcessAsync(
            "cmd.exe",
            $"/d /c mklink /J \"{nuget}\" \"{outside}\"",
            fixture.Root);
        if (junction.ExitCode != 0)
        {
            return;
        }

        fixture.RegisterJunction(nuget);
        var result = await fixture.RunAsync("-Apply");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("reparse", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Provisioner_ActiveCoordinatorLeaseFailsBeforeMutation()
    {
        using var fixture = ProvisionerFixture.Create();
        var setup = await fixture.RunAsync("-Apply");
        Assert.True(setup.ExitCode == 0, setup.Output);
        var coordinatorPath = Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-provision.lease");
        var ownerPath = Path.Combine(
            fixture.CacheRoot,
            ".georaeplan-build-cache-owner.json");
        var ownerBytes = await File.ReadAllBytesAsync(ownerPath);
        await using var heldLease = new FileStream(
            coordinatorPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = await fixture.RunAsync("-Apply");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("coordinator lease", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ownerBytes, await File.ReadAllBytesAsync(ownerPath));
    }

    [Theory]
    [InlineData("BeforeRootCreate")]
    [InlineData("BeforeAclMutation")]
    public async Task Provisioner_DirectorySwapImmediatelyBeforeMutationCannotTouchProtectedTree(
        string interleavingPoint)
    {
        using var fixture = ProvisionerFixture.Create();
        if (interleavingPoint == "BeforeAclMutation")
        {
            var setup = await fixture.RunAsync("-Apply");
            Assert.True(setup.ExitCode == 0, setup.Output);
        }
        var protectedRoot = Path.Combine(
            fixture.Root,
            $"protected-{interleavingPoint}");
        CreateProtectedTree(protectedRoot);
        var before = CaptureProtectedTree(protectedRoot);

        var hook = interleavingPoint switch
        {
            "BeforeRootCreate" => $$"""
                $cacheParent = Split-Path -Parent $rootPlan.LogicalPath
                $movedParent = $cacheParent + '.interleaving-moved'
                [IO.Directory]::Move($cacheParent, $movedParent)
                & cmd.exe /d /c mklink /J "${cacheParent}" "{{PsLiteral(protectedRoot)}}" | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'root parent junction interleaving failed' }
                """,
            "BeforeAclMutation" => $$"""
                if ([string]::Equals(
                    $directoryPath,
                    (Join-Path $rootPlan.LogicalPath 'temp'),
                    [StringComparison]::OrdinalIgnoreCase)) {
                    $movedLeaf = $directoryPath + '.interleaving-moved'
                    [IO.Directory]::Move($directoryPath, $movedLeaf)
                    & cmd.exe /d /c mklink /J "${directoryPath}" "{{PsLiteral(protectedRoot)}}" | Out-Null
                    if ($LASTEXITCODE -ne 0) { throw 'ACL junction interleaving failed' }
                }
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(interleavingPoint))
        };
        fixture.InjectInterleaving(interleavingPoint, hook);

        var result = await fixture.RunAsync("-Apply");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, CaptureProtectedTree(protectedRoot));
        fixture.RemoveInjectedReparsePoints();
        fixture.RestoreCleanScript();
        var retry = await fixture.RunAsync("-Apply");
        Assert.True(retry.ExitCode == 0, retry.Output);
    }

    [Theory]
    [InlineData("BeforeCoordinatorOpenOrCreate", ".georaeplan-build-cache-provision.lease")]
    [InlineData("BeforeOwnerMarkerPublish", ".georaeplan-build-cache-owner.json")]
    [InlineData("BeforeSentinelPublish", "temp\\.georaeplan-build-cache-lease")]
    public async Task Provisioner_FileRaceImmediatelyBeforeMutationPreservesProtectedBytesAndRetries(
        string interleavingPoint,
        string relativeTarget)
    {
        using var fixture = ProvisionerFixture.Create();
        if (interleavingPoint == "BeforeOwnerMarkerPublish")
        {
            var setup = await fixture.RunAsync(
                "-Apply",
                "-TestFaultInjection",
                "AfterRoot");
            Assert.NotEqual(0, setup.ExitCode);
        }
        else
        {
            var setup = await fixture.RunAsync("-Apply");
            Assert.True(setup.ExitCode == 0, setup.Output);
            File.Delete(Path.Combine(
                fixture.CacheRoot,
                relativeTarget.Replace('\\', Path.DirectorySeparatorChar)));
        }
        var protectedFile = Path.Combine(
            fixture.Root,
            $"protected-{interleavingPoint}.bin");
        await File.WriteAllBytesAsync(
            protectedFile,
            interleavingPoint == "BeforeCoordinatorOpenOrCreate"
                ? Array.Empty<byte>()
                : new byte[] { 0x47, 0x50, 0x4C, 0x41, 0x4E });
        var protectedAcl = CaptureFileAcl(protectedFile);
        var protectedBytes = await File.ReadAllBytesAsync(protectedFile);
        var raceTarget = Path.Combine(
            fixture.CacheRoot,
            relativeTarget.Replace('\\', Path.DirectorySeparatorChar));
        var hook = $$"""
            $raceTarget = '{{PsLiteral(raceTarget)}}'
            [IO.Directory]::CreateDirectory((Split-Path -Parent $raceTarget)) | Out-Null
            & cmd.exe /d /c mklink /H "${raceTarget}" "{{PsLiteral(protectedFile)}}" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'file hardlink interleaving failed' }
            """;
        fixture.InjectInterleaving(interleavingPoint, hook);

        var result = await fixture.RunAsync("-Apply");

        Assert.Equal(protectedBytes, await File.ReadAllBytesAsync(protectedFile));
        Assert.Equal(protectedAcl, CaptureFileAcl(protectedFile));
        Assert.NotEqual(0, result.ExitCode);
        Assert.True(File.Exists(raceTarget), result.Output);
        File.Delete(raceTarget);
        fixture.RestoreCleanScript();
        var retry = await fixture.RunAsync("-Apply");
        Assert.True(
            retry.ExitCode == 0,
            $"retry output=[{retry.Output}] root entries=[{string.Join(",", Directory.GetFileSystemEntries(fixture.CacheRoot).Select(Path.GetFileName))}]");
    }

    [Fact]
    public async Task Provisioner_ParentLeaseDoesNotBlockUnrelatedChildrenAndEndsWithProcess()
    {
        using var fixture = ProvisionerFixture.Create();
        var unrelated = Path.Combine(fixture.Root, "unrelated-cache");
        var renamed = unrelated + ".renamed";
        var hook = $$"""
            [IO.Directory]::CreateDirectory('{{PsLiteral(unrelated)}}') | Out-Null
            [IO.File]::WriteAllText(
                (Join-Path '{{PsLiteral(unrelated)}}' 'probe.txt'),
                'unrelated-cache-remains-writable')
            [IO.Directory]::Move(
                '{{PsLiteral(unrelated)}}',
                '{{PsLiteral(renamed)}}')
            """;
        fixture.InjectInterleaving("BeforeRootCreate", hook);

        var result = await fixture.RunAsync("-Apply");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            "unrelated-cache-remains-writable",
            await File.ReadAllTextAsync(Path.Combine(renamed, "probe.txt")));
        var releasedPath = fixture.CacheRoot + ".lease-released";
        Directory.Move(fixture.CacheRoot, releasedPath);
        Directory.Move(releasedPath, fixture.CacheRoot);
    }

    [Theory]
    [InlineData("Owner", ".georaeplan-build-cache-owner.json")]
    [InlineData("Sentinel", "temp\\.georaeplan-build-cache-lease")]
    public async Task Provisioner_StableMetadataLeaseBlocksInPlaceMutationAndRetries(
        string label,
        string relativeTarget)
    {
        using var fixture = ProvisionerFixture.Create();
        var setup = await fixture.RunAsync("-Apply");
        Assert.True(setup.ExitCode == 0, setup.Output);
        var target = Path.Combine(
            fixture.CacheRoot,
            relativeTarget.Replace('\\', Path.DirectorySeparatorChar));
        var before = await File.ReadAllBytesAsync(target);
        var hook = $$"""
            if ([string]::Equals(
                $directoryPath,
                $rootPlan.LogicalPath,
                [StringComparison]::OrdinalIgnoreCase)) {
                [IO.File]::WriteAllText(
                    '{{PsLiteral(target)}}',
                    'hostile-in-place-{{label.ToLowerInvariant()}}-mutation')
            }
            """;
        fixture.InjectInterleaving("BeforeAclMutation", hook);

        var result = await fixture.RunAsync("-Apply");

        Assert.Equal(before, await File.ReadAllBytesAsync(target));
        Assert.NotEqual(0, result.ExitCode);
        fixture.RestoreCleanScript();
        var retry = await fixture.RunAsync("-Apply");
        Assert.True(retry.ExitCode == 0, retry.Output);
    }

    [Fact]
    public async Task Provisioner_StableFileHashRetainsLeaseUntilExplicitDispose()
    {
        using var fixture = ProvisionerFixture.Create();
        var probePath = Path.Combine(fixture.Root, "stable-file-probe.bin");
        await File.WriteAllTextAsync(probePath, "stable-before-hash");
        var hook = $$"""
            $stableProbePath = '{{PsLiteral(probePath)}}'
            $stableLease =
                [GeoraePlan.BuildCacheProvisioner.NativeEntry]::OpenStableFileLease(
                    $stableProbePath)
            try {
                [void]$stableLease.ComputeSha256()
                $stableLease.AssertIdentityAt($stableProbePath)
                $writeBlocked = $false
                try {
                    [IO.File]::WriteAllText(
                        $stableProbePath,
                        'hostile-write-after-hash')
                }
                catch [IO.IOException] {
                    $writeBlocked = $true
                }
                if (-not $writeBlocked) {
                    throw 'stable lease allowed a write after hashing'
                }
                Write-Output 'StableLeaseHashRetained=True'
            }
            finally {
                $stableLease.Dispose()
            }
            [IO.File]::WriteAllText($stableProbePath, 'write-after-dispose')
            Write-Output 'StableLeaseDisposeReleased=True'
            """;
        fixture.InjectInterleaving("BeforeRootCreate", hook);

        var result = await fixture.RunAsync("-Apply");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("StableLeaseHashRetained=True", result.Output, StringComparison.Ordinal);
        Assert.Contains("StableLeaseDisposeReleased=True", result.Output, StringComparison.Ordinal);
        Assert.Equal("write-after-dispose", await File.ReadAllTextAsync(probePath));
    }

    [Fact]
    public async Task Provisioner_IsPowerShell51ParseableAndContainsFailClosedSourceGuards()
    {
        var script = ProvisionerFixture.SourceScriptPath;
        Assert.True(File.Exists(script));
        var source = await File.ReadAllTextAsync(script);

        Assert.Contains(
            "$cacheRoot = 'D:\\DevCaches\\georaeplan-v1-prepare'",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[switch]$Apply", source, StringComparison.Ordinal);
        Assert.Contains("NtFileCreate", source, StringComparison.Ordinal);
        Assert.Contains("CreateExclusiveFileChild", source, StringComparison.Ordinal);
        Assert.Contains("RenameRelativeNoReplace", source, StringComparison.Ordinal);
        Assert.Contains("NtFileRenameInformationEx", source, StringComparison.Ordinal);
        Assert.Contains("FileRenamePosixSemantics", source, StringComparison.Ordinal);
        Assert.Contains("publish && isDirectory ? ShareDelete : 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[IO.File]::Move", source, StringComparison.Ordinal);
        Assert.Contains("DuplicateHandle", source, StringComparison.Ordinal);
        Assert.Contains("OpenStableFileLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenPublishStableFileLease", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter-PublishStableFileContract", source, StringComparison.Ordinal);
        Assert.Contains("NtCreateFile", source, StringComparison.Ordinal);
        Assert.Contains("RootDirectory", source, StringComparison.Ordinal);
        Assert.Contains("NtQueryEaFile", source, StringComparison.Ordinal);
        Assert.Contains("CreateProvisionedDirectoryChild", source, StringComparison.Ordinal);
        Assert.Contains("Assert-RootProvisioningToken", source, StringComparison.Ordinal);
        Assert.Contains("createdAtUtc=$CreatedAtUtc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("georaeplan-build-cache-root-", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory]::Move", source, StringComparison.Ordinal);
        Assert.Contains("LocalApplicationData", source, StringComparison.Ordinal);
        Assert.Contains("ApplicationData", source, StringComparison.Ordinal);
        Assert.Contains("georaeplan-v1-user-snapshots", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[IO.File]::Delete", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[IO.Directory]::Delete", source, StringComparison.OrdinalIgnoreCase);

        var parse = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -Command \"$tokens=$null;$errors=$null;" +
            $"[void][Management.Automation.Language.Parser]::ParseFile(" +
            $"'{script.Replace("'", "''")}',[ref]$tokens,[ref]$errors);" +
            "if($errors.Count -ne 0){$errors|ForEach-Object{$_.Message};exit 1}" +
            "Write-Output 'powershell-5.1-ast-ok'\"",
            RepoRoot);

        Assert.Equal(0, parse.ExitCode);
        Assert.Contains("powershell-5.1-ast-ok", parse.Output, StringComparison.Ordinal);
    }

    private static void AssertAclContract(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var ownerIdentity = security.GetOwner(typeof(SecurityIdentifier));
        var owner = Assert.IsType<SecurityIdentifier>(ownerIdentity).Value;
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, owner);

        var expected = new[]
        {
            WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var rules = security
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.Equal(expected.Length, rules.Length);
        Assert.Equal(
            expected,
            rules.Select(rule =>
            {
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                Assert.False(rule.IsInherited);
                Assert.Equal(
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    rule.InheritanceFlags);
                Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
                return Assert.IsType<SecurityIdentifier>(rule.IdentityReference).Value;
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static void AssertFileAclContract(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            WindowsIdentity.GetCurrent().User!.Value,
            Assert.IsType<SecurityIdentifier>(
                security.GetOwner(typeof(SecurityIdentifier))).Value);
        var expected = new[]
        {
            WindowsIdentity.GetCurrent().User!.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value
        }.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.Equal(expected.Length, rules.Length);
        Assert.Equal(
            expected,
            rules.Select(rule =>
            {
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                Assert.False(rule.IsInherited);
                Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
                Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
                return Assert.IsType<SecurityIdentifier>(rule.IdentityReference).Value;
            }).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static void CreateProtectedTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        File.WriteAllBytes(
            Path.Combine(root, "protected.bin"),
            new byte[] { 0x50, 0x52, 0x4F, 0x54, 0x45, 0x43, 0x54 });
        File.WriteAllText(
            Path.Combine(root, "nested", "protected.txt"),
            "protected-tree-must-remain-byte-exact");
    }

    private static string CaptureProtectedTree(string root)
    {
        var records = new List<string>
        {
            $"ROOT|{CaptureDirectoryAcl(root)}"
        };
        foreach (var directory in Directory.EnumerateDirectories(
            root,
            "*",
            SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            records.Add(
                $"D|{Path.GetRelativePath(root, directory)}|" +
                CaptureDirectoryAcl(directory));
        }
        foreach (var file in Directory.EnumerateFiles(
            root,
            "*",
            SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            records.Add(
                $"F|{Path.GetRelativePath(root, file)}|" +
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}|" +
                CaptureFileAcl(file));
        }
        return string.Join("\n", records);
    }

    private static string CaptureDirectoryAcl(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    private static string CaptureFileAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        return security.GetSecurityDescriptorSddlForm(
            AccessControlSections.Owner | AccessControlSections.Access);
    }

    private static string PsLiteral(string value) => value.Replace("'", "''");

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        return new ProcessResult(
            process.ExitCode,
            (await stdout) + Environment.NewLine + (await stderr));
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);

    private sealed class ProvisionerFixture : IDisposable
    {
        private readonly List<string> junctions = new();

        private ProvisionerFixture(
            string root,
            string cacheRoot,
            string scriptPath,
            string cleanSource)
        {
            Root = root;
            CacheRoot = cacheRoot;
            ScriptPath = scriptPath;
            CleanSource = cleanSource;
        }

        public string Root { get; }
        public string CacheRoot { get; }
        public string ScriptPath { get; }
        public string JournalPath
        {
            get
            {
                const string journalName = ".georaeplan-build-cache-provisioning.json";
                return Path.Combine(CacheRoot, journalName);
            }
        }
        private string CleanSource { get; }

        public static string SourceScriptPath => Path.Combine(
            RepoRoot,
            "tools",
            "maintenance",
            "Initialize-GeoraePlanBuildCache.ps1");

        public static ProvisionerFixture Create()
        {
            var root = Path.Combine(
                @"D:\DevCaches\georaeplan-build-cache-provisioner-tests",
                Guid.NewGuid().ToString("N"));
            var cacheRoot = Path.Combine(root, "cache-parent", "cache");
            var maintenance = Path.Combine(root, "repo", "tools", "maintenance");
            Directory.CreateDirectory(maintenance);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
            var scriptPath = Path.Combine(
                maintenance,
                "Initialize-GeoraePlanBuildCache.ps1");
            var source = File.ReadAllText(SourceScriptPath);
            source = source.Replace(
                "$cacheRoot = 'D:\\DevCaches\\georaeplan-v1-prepare'",
                $"$cacheRoot = '{cacheRoot.Replace("'", "''")}'",
                StringComparison.Ordinal);
            File.WriteAllText(scriptPath, source);
            return new ProvisionerFixture(root, cacheRoot, scriptPath, source);
        }

        public void InjectInterleaving(string point, string hook)
        {
            var source = CleanSource;
            var marker = $"# TEST-HOOK: {point}";
            if (source.Contains(marker, StringComparison.Ordinal))
            {
                source = source.Replace(
                    marker,
                    marker + Environment.NewLine + hook,
                    StringComparison.Ordinal);
            }
            else
            {
                var legacyNeedle = point switch
                {
                    "BeforeRootCreate" =>
                        "[IO.Directory]::CreateDirectory($rootPlan.LogicalPath) | Out-Null",
                    "BeforeCoordinatorOpenOrCreate" =>
                        "$coordinatorLease = Open-CoordinatorLease -Path $coordinatorPath",
                    "BeforeAclMutation" =>
                        "Set-AndAssertDirectorySecurity -Path $directoryPath",
                    "BeforeOwnerMarkerPublish" =>
                        "Write-NewOwnerMetadataAtomically `",
                    "BeforeSentinelPublish" =>
                        "New-EmptySentinel -Path $sentinelPath",
                    _ => throw new ArgumentOutOfRangeException(nameof(point))
                };
                Assert.Contains(legacyNeedle, source, StringComparison.Ordinal);
                source = source.Replace(
                    legacyNeedle,
                    hook + Environment.NewLine + legacyNeedle,
                    StringComparison.Ordinal);
            }
            File.WriteAllText(ScriptPath, source);
        }

        public void RestoreCleanScript() => File.WriteAllText(ScriptPath, CleanSource);

        public void RemoveInjectedReparsePoints()
        {
            foreach (var candidate in new[]
            {
                Path.GetDirectoryName(CacheRoot)!,
                CacheRoot,
                Path.Combine(CacheRoot, "temp")
            })
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }
                var attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(candidate);
                }
            }

            var cacheParent = Path.GetDirectoryName(CacheRoot)!;
            var movedParent = cacheParent + ".interleaving-moved";
            if (!Directory.Exists(cacheParent) && Directory.Exists(movedParent))
            {
                Directory.Move(movedParent, cacheParent);
            }
            var movedLeaf = Path.Combine(CacheRoot, "temp.interleaving-moved");
            var leaf = Path.Combine(CacheRoot, "temp");
            if (!Directory.Exists(leaf) && Directory.Exists(movedLeaf))
            {
                Directory.Move(movedLeaf, leaf);
            }
        }

        public Task<ProcessResult> RunAsync(params string[] arguments)
        {
            var quotedScript = $"\"{ScriptPath}\"";
            var joined = string.Join(
                " ",
                arguments.Select(argument => argument.Contains(' ')
                    ? $"\"{argument}\""
                    : argument));
            return RunProcessAsync(
                "powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File {quotedScript} {joined}",
                Root);
        }

        public void RegisterJunction(string path) => junctions.Add(path);

        public void Dispose()
        {
            foreach (var junction in junctions)
            {
                if (Directory.Exists(junction))
                {
                    DeleteDirectoryWithRetry(junction, recursive: false);
                }
            }

            RemoveInjectedReparsePoints();

            if (Directory.Exists(Root))
            {
                DeleteDirectoryWithRetry(Root, recursive: true);
            }
        }

        private static void DeleteDirectoryWithRetry(
            string path,
            bool recursive)
        {
            Exception? lastError = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive);
                    return;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    Thread.Sleep(50);
                }
            }

            throw new IOException(
                $"Failed to remove build-cache provisioner fixture: {path}",
                lastError);
        }
    }
}
